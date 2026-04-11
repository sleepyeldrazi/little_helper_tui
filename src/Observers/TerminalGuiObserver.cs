using System.Text;
using LittleHelper;
using LittleHelperTui.Views;
using Terminal.Gui;

namespace LittleHelperTui.Observers;

/// <summary>
/// IAgentObserver implementation that renders agent activity via Terminal.Gui views.
/// All UI updates are marshaled to the main thread via Application.Invoke.
/// </summary>
public class TerminalGuiObserver : IAgentObserver
{
    private readonly MainWindow _mainWindow;
    private readonly TuiConfig _config;
    private readonly StringBuilder _streamingContent = new();
    private readonly StringBuilder _streamingThinking = new();
    private bool _isStreaming;

    // Config-driven display settings
    private readonly string _thinkingMode;
    private readonly int _maxToolOutputLines;
    private readonly bool _verbose;

    public int CurrentStep { get; private set; }
    public AgentState CurrentState { get; private set; } = AgentState.Planning;
    public int TotalTokens { get; private set; }
    public int TotalThinkingTokens { get; private set; }

    /// <summary>Current streaming content preview.</summary>
    public string StreamingPreview
    {
        get
        {
            lock (_streamingContent)
            {
                if (_streamingContent.Length == 0) return "";
                var text = _streamingContent.ToString();
                if (text.Length > 80) text = "..." + text[^77..];
                return text.Replace("\n", " ").Trim();
            }
        }
    }

    public TerminalGuiObserver(MainWindow mainWindow, TuiConfig? config = null)
    {
        _mainWindow = mainWindow;
        _config = config ?? new TuiConfig();
        _thinkingMode = _config.ThinkingMode;
        _maxToolOutputLines = _config.MaxToolOutputLines;
        _verbose = _config.Verbose;
    }

    /// <summary>Reset observer state.</summary>
    public void Reset()
    {
        TotalTokens = 0;
        TotalThinkingTokens = 0;
        CurrentStep = 0;
        CurrentState = AgentState.Planning;
        _streamingContent.Clear();
        _streamingThinking.Clear();
        _isStreaming = false;
    }

    // --- IAgentObserver implementation ---

    public void OnStepStart(int step)
    {
        CurrentStep = step;
        InvokeOnMain(() => _mainWindow.SetStatus($"Step {step} | {FormatTokens(TotalTokens)} tokens"));
    }

    private static string FormatTokens(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:F1}M"
        : tokens >= 1_000 ? $"{tokens / 1_000.0:F1}K"
        : $"{tokens}";

    public void OnModelResponse(ModelResponse response, int step)
    {
        TotalTokens += response.TokensUsed;
        TotalThinkingTokens += response.ThinkingTokens;
        _isStreaming = false;

        // Show thinking if present and mode allows
        if (!string.IsNullOrEmpty(response.ThinkingContent) && _thinkingMode != "hidden")
        {
            var thinking = response.ThinkingContent;
            string preview;

            if (_thinkingMode == "full")
            {
                preview = thinking;
            }
            else // "condensed" (default)
            {
                var lines = thinking.Split('\n');
                if (lines.Length <= 8)
                    preview = thinking;
                else
                    preview = string.Join('\n', lines.Take(3))
                        + "\n  ..."
                        + "\n" + string.Join('\n', lines.TakeLast(3));
            }

            InvokeOnMain(() =>
            {
                var view = new ThinkingView(preview, response.ThinkingTokens, response.TokensUsed);
                AddView(view);
            });
        }

        // Show assistant text content
        if (!string.IsNullOrWhiteSpace(response.Content))
        {
            InvokeOnMain(() =>
            {
                var view = new AssistantMessageView(response.Content, step, response.TokensUsed);
                AddView(view);
            });
        }

        // Show pending tool calls in verbose mode
        if (_verbose && response.ToolCalls.Count > 0)
        {
            InvokeOnMain(() =>
            {
                var sb = new StringBuilder();
                foreach (var tc in response.ToolCalls)
                {
                    var detail = FormatToolArgs(tc.Name, tc.Arguments);
                    sb.AppendLine($"> {tc.Name}({detail})");
                }
                var view = new LogMessageView(sb.ToString(), LogLevel.Info);
                AddView(view);
            });
        }
    }

    public ToolCall? OnToolCallExecuting(ToolCall call, int step)
    {
        // No-op: interception goes through AgentControl.ToolInterceptor
        return call;
    }

    public void OnToolCallCompleted(ToolCall call, ToolResult result, long durationMs, int step)
    {
        InvokeOnMain(() =>
        {
            var view = new ToolResultView(call.Name, result.Output, result.IsError, durationMs);
            AddView(view);
        });
    }

    public void OnStateChange(AgentState from, AgentState to)
    {
        CurrentState = to;

        // Show state transitions in verbose mode, except for important ones
        if (_verbose || to == AgentState.ErrorRecovery)
        {
            var level = to == AgentState.ErrorRecovery
                ? LogLevel.Warning
                : LogLevel.Info;

            InvokeOnMain(() =>
            {
                var view = new LogMessageView($"[State] {from} -> {to}", level);
                AddView(view);
            });
        }
    }

    public void OnCompaction(CompactionResult result)
    {
        InvokeOnMain(() =>
        {
            var view = new LogMessageView(
                $"* Context compacted (saved {result.TokensSaved} tokens)",
                LogLevel.Info);
            AddView(view);
        });
    }

    public void OnError(string message)
    {
        // Categorize by prefix
        var level = LogLevel.Error;
        if (message.StartsWith("WARN:"))
            level = LogLevel.Warning;
        else if (message.StartsWith("[State]") || message.StartsWith("[Step]") || message.StartsWith("[Done]"))
            level = LogLevel.Info;

        // Suppress verbose messages unless in verbose mode
        if (level == LogLevel.Info && !_verbose)
            return;

        // Suppress tool execution details (redundant with OnToolCallCompleted)
        if (message.StartsWith("  "))
            return;

        InvokeOnMain(() =>
        {
            var view = new LogMessageView(message, level);
            AddView(view);
        });
    }

    public void OnStreamChunk(string contentDelta, string? thinkingDelta)
    {
        if (!_isStreaming)
        {
            _isStreaming = true;
            _streamingContent.Clear();
            _streamingThinking.Clear();
        }

        if (!string.IsNullOrEmpty(contentDelta))
            _streamingContent.Append(contentDelta);
        if (!string.IsNullOrEmpty(thinkingDelta))
            _streamingThinking.Append(thinkingDelta);
    }

    // --- Public methods for controller ---

    /// <summary>Add a user message to the chat.</summary>
    public void AddUserMessage(string text)
    {
        InvokeOnMain(() =>
        {
            var view = new UserMessageView(text);
            AddView(view);
        });
    }

    /// <summary>Add a status/done message to the chat.</summary>
    public void AddStatusMessage(bool success, int steps, long elapsedMs, int maxContext, int filesChanged)
    {
        InvokeOnMain(() =>
        {
            var view = new StatusView(success, steps, elapsedMs, TotalTokens,
                TotalThinkingTokens, maxContext, filesChanged);
            AddView(view);
        });
    }

    /// <summary>Add a separator line.</summary>
    public void AddSeparator()
    {
        InvokeOnMain(() =>
        {
            var view = new LogMessageView("──", LogLevel.Info);
            AddView(view);
        });
    }

    // --- Helpers ---

    private void InvokeOnMain(Action action)
    {
        Application.Invoke(action);
    }

    private void AddView(View view)
    {
        _mainWindow.AddChatView(view);
    }

    private static string FormatToolArgs(string toolName, System.Text.Json.JsonElement args)
        => Agent.FormatToolDetail(toolName, args);
}
