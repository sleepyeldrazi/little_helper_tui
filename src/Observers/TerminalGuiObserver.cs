using System.Text;
using LittleHelper;
using LittleHelperTui.Views;
using Terminal.Gui;

namespace LittleHelperTui.Observers;

/// <summary>
/// IAgentObserver that renders agent activity as plain text lines into the MainWindow's
/// chat TextView. Matches the visual style of the old Spectre.Console TUI:
/// - User messages in a green-bordered box
/// - Assistant responses in a blue-bordered box
/// - Tool results as +/x prefix lines
/// - Thinking in a grey-bordered box
/// All UI updates are marshaled to the main thread via Application.Invoke.
/// </summary>
public class TerminalGuiObserver : IAgentObserver
{
    private readonly MainWindow _mainWindow;
    private readonly TuiConfig _config;
    private readonly StringBuilder _streamingContent = new();
    private readonly StringBuilder _streamingThinking = new();
    private bool _isStreaming;

    private readonly string _thinkingMode;
    private readonly int _maxToolOutputLines;
    private readonly bool _verbose;

    public int CurrentStep { get; private set; }
    public AgentState CurrentState { get; private set; } = AgentState.Planning;
    public int TotalTokens { get; private set; }
    public int TotalThinkingTokens { get; private set; }

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

    // --- IAgentObserver ---

    public void OnStepStart(int step)
    {
        CurrentStep = step;
        _mainWindow.SetStatus($"Step {step} | {FormatTokens(TotalTokens)} tokens");
    }

    public void OnModelResponse(ModelResponse response, int step)
    {
        TotalTokens += response.TokensUsed;
        TotalThinkingTokens += response.ThinkingTokens;
        _isStreaming = false;

        // Thinking panel
        if (!string.IsNullOrEmpty(response.ThinkingContent) && _thinkingMode != "hidden")
        {
            var thinking = response.ThinkingContent;
            string preview;

            if (_thinkingMode == "full")
            {
                preview = thinking;
            }
            else // condensed
            {
                var lines = thinking.Split('\n');
                if (lines.Length <= 8)
                    preview = thinking;
                else
                    preview = string.Join('\n', lines.Take(3))
                        + "\n  ..."
                        + "\n" + string.Join('\n', lines.TakeLast(3));
            }

            var header = $"Thinking ({FormatTokens(response.ThinkingTokens)} thinking, {FormatTokens(response.TokensUsed)} context)";
            WritePanel(header, preview, '.');
        }

        // Assistant response
        if (!string.IsNullOrWhiteSpace(response.Content))
        {
            var header = $"Assistant  Step {step} ({FormatTokens(response.TokensUsed)} context)";
            WritePanel(header, response.Content, '-');
        }

        // Verbose: pending tool calls
        if (_verbose && response.ToolCalls.Count > 0)
        {
            foreach (var tc in response.ToolCalls)
            {
                var detail = FormatToolArgs(tc.Name, tc.Arguments);
                _mainWindow.AppendLine($"  >{tc.Name}({detail})");
            }
            _mainWindow.AppendLine();
        }
    }

    public ToolCall? OnToolCallExecuting(ToolCall call, int step) => call;

    public void OnToolCallCompleted(ToolCall call, ToolResult result, long durationMs, int step)
    {
        var icon = result.IsError ? "x" : "+";
        var detail = FormatToolArgs(call.Name, call.Arguments);

        _mainWindow.AppendLine($"{icon} {call.Name} ({FormatDuration(durationMs)})");
        _mainWindow.AppendLine($"  {detail}");

        var output = result.Output;
        if (result.IsError)
        {
            var maxErr = Math.Max(200, _maxToolOutputLines * 40);
            var errText = output.Length > maxErr ? output[..maxErr] + "..." : output;
            _mainWindow.AppendLine($"  {errText}");
        }
        else if (call.Name.Equals("write", StringComparison.OrdinalIgnoreCase) ||
                 call.Name.Equals("edit", StringComparison.OrdinalIgnoreCase) ||
                 call.Name.Equals("patch", StringComparison.OrdinalIgnoreCase))
        {
            RenderWriteOutput(output);
        }
        else if (call.Name.Equals("read", StringComparison.OrdinalIgnoreCase))
        {
            RenderReadOutput(output);
        }
        else
        {
            var maxLines = Math.Max(3, _maxToolOutputLines / 4);
            RenderCompactOutput(output, maxLines);
        }

        _mainWindow.AppendLine();
    }

    public void OnStateChange(AgentState from, AgentState to)
    {
        CurrentState = to;
    }

    public void OnCompaction(CompactionResult result)
    {
        _mainWindow.AppendLine($"* Context compacted (saved {result.TokensSaved} tokens)");
        _mainWindow.AppendLine();
    }

    public void OnError(string message)
    {
        RenderLogMessage(message);
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

    // --- Public helpers for controller ---

    /// <summary>Render a user message in a bordered panel (matching old green panel).</summary>
    public void AddUserMessage(string text)
    {
        WritePanel("You", text, '=');
    }

    /// <summary>Write a separator line.</summary>
    public void AddSeparator()
    {
        _mainWindow.AppendLine("──");
    }

    /// <summary>Write a status/done message.</summary>
    public void AddStatusMessage(bool success, int steps, long elapsedMs, int maxContext, int filesChanged)
    {
        var icon = success ? "+" : "x";
        var elapsed = elapsedMs < 1000 ? $"{elapsedMs}ms" : $"{elapsedMs / 1000.0:F1}s";
        var context = FormatTokens(TotalTokens);
        var thinking = FormatTokens(TotalThinkingTokens);
        var maxCtx = FormatTokens(maxContext);

        _mainWindow.AppendLine();
        _mainWindow.AppendLine($"{icon} Done  {steps} steps, {elapsed}, {context}/{maxCtx} context ({thinking} thinking)");

        if (filesChanged > 0)
            _mainWindow.AppendLine($"  {filesChanged} files changed. Use :files to list.");

        _mainWindow.AppendLine();
    }

    /// <summary>Write a simple info/error line.</summary>
    public void AddInfoMessage(string text)
    {
        _mainWindow.AppendLine(text);
    }

    // --- Rendering helpers ---

    /// <summary>
    /// Draw a simple text panel with box-drawing border.
    /// borderChar: '=' for user (double-ish), '-' for assistant, '.' for thinking
    /// </summary>
    private void WritePanel(string header, string content, char borderChar)
    {
        // Simple bordered panel matching old Spectre rounded panels
        // Use box-drawing characters for a clean look
        var width = 78; // reasonable default, content will wrap in TextView anyway

        string topLeft, topRight, bottomLeft, bottomRight, horizontal, vertical;
        if (borderChar == '=')
        {
            // User: double border
            topLeft = "\u2554"; topRight = "\u2557";
            bottomLeft = "\u255a"; bottomRight = "\u255d";
            horizontal = "\u2550"; vertical = "\u2551";
        }
        else if (borderChar == '.')
        {
            // Thinking: light dotted
            topLeft = "\u250c"; topRight = "\u2510";
            bottomLeft = "\u2514"; bottomRight = "\u2518";
            horizontal = "\u2504"; vertical = "\u2502";
        }
        else
        {
            // Assistant/default: rounded
            topLeft = "\u256d"; topRight = "\u256e";
            bottomLeft = "\u2570"; bottomRight = "\u256f";
            horizontal = "\u2500"; vertical = "\u2502";
        }

        var headerPad = header.Length + 2; // space + header + space
        var barLen = Math.Max(0, width - 2 - headerPad);

        _mainWindow.AppendLine($"{topLeft}{horizontal} {header} {new string(horizontal[0], barLen)}{topRight}");

        foreach (var line in content.Split('\n'))
        {
            _mainWindow.AppendLine($"{vertical} {line}");
        }

        _mainWindow.AppendLine($"{bottomLeft}{new string(horizontal[0], width - 2)}{bottomRight}");
    }

    private void RenderWriteOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            _mainWindow.AppendLine("  (empty)");
            return;
        }

        if (output.Contains("wrote") || output.Contains("written") || output.Length < 100)
        {
            _mainWindow.AppendLine($"  {output}");
            return;
        }

        var lines = output.Split('\n');
        var maxShow = 8;
        foreach (var line in lines.Take(maxShow))
        {
            if (line.StartsWith("+") && !line.StartsWith("++"))
                _mainWindow.AppendLine($"  +{line[1..]}");
            else if (line.StartsWith("-") && !line.StartsWith("--"))
                _mainWindow.AppendLine($"  -{line[1..]}");
            else if (line.StartsWith("@@"))
                _mainWindow.AppendLine($"  {line}");
            else
                _mainWindow.AppendLine($"  {line}");
        }

        var remaining = lines.Length - maxShow;
        if (remaining > 0)
            _mainWindow.AppendLine($"  ... {remaining} more lines");
    }

    private void RenderReadOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            _mainWindow.AppendLine("  (empty)");
            return;
        }

        var lines = output.Split('\n');
        _mainWindow.AppendLine($"  {lines.Length} lines");

        foreach (var line in lines.Take(6))
        {
            var truncated = line.Length > 120 ? line[..120] + "..." : line;
            _mainWindow.AppendLine($"  {truncated}");
        }

        var remaining = lines.Length - 6;
        if (remaining > 0)
            _mainWindow.AppendLine($"  ... {remaining} more lines");
    }

    private void RenderCompactOutput(string output, int maxLines)
    {
        if (string.IsNullOrEmpty(output))
        {
            _mainWindow.AppendLine("  (empty)");
            return;
        }

        var lines = output.Split('\n');
        foreach (var line in lines.Take(maxLines))
        {
            var truncated = line.Length > 120 ? line[..120] + "..." : line;
            _mainWindow.AppendLine($"  {truncated}");
        }

        var remaining = lines.Length - maxLines;
        if (remaining > 0)
            _mainWindow.AppendLine($"  ... {remaining} more lines");
    }

    private void RenderLogMessage(string message)
    {
        // Tool execution detail (redundant with OnToolCallCompleted)
        if (message.StartsWith("  "))
            return;

        // Informational: state transitions, step progress, completion
        if (message.StartsWith("[State]") || message.StartsWith("[Step") || message.StartsWith("[Done]"))
        {
            if (message.Contains("Stall detected") || message.Contains("Step limit") ||
                message.Contains("Error recovery") || message.Contains("Max error recovery"))
            {
                _mainWindow.AppendLine(message);
                return;
            }
            if (_verbose)
                _mainWindow.AppendLine(message);
            return;
        }

        if (message.StartsWith("WARN:") || message.StartsWith("WARN "))
        {
            _mainWindow.AppendLine(message);
            return;
        }

        if (message.StartsWith("[Injected]"))
        {
            _mainWindow.AppendLine(message);
            return;
        }

        // Everything else: errors
        _mainWindow.AppendLine(message);
    }

    // --- Formatting helpers ---

    private static string FormatToolArgs(string toolName, System.Text.Json.JsonElement args)
        => Agent.FormatToolDetail(toolName, args);

    private static string FormatDuration(long ms) =>
        ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:F1}s";

    private static string FormatTokens(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:F1}M"
        : tokens >= 1_000 ? $"{tokens / 1_000.0:F1}K"
        : $"{tokens}";
}
