using System.Text;
using LittleHelper;
using LittleHelperTui.Views;
using Terminal.Gui;

namespace LittleHelperTui.Observers;

/// <summary>
/// Renders agent activity as colored text blocks into MainWindow.
/// Panels have rounded box-drawing borders with colored headers.
/// Multi-color lines use AddColoredSegments for mixed styling.
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

    public void OnStepStart(int step) => CurrentStep = step;

    public void OnModelResponse(ModelResponse response, int step)
    {
        TotalTokens += response.TokensUsed;
        TotalThinkingTokens += response.ThinkingTokens;
        _isStreaming = false;

        // Thinking panel (grey border)
        if (!string.IsNullOrEmpty(response.ThinkingContent) && _thinkingMode != "hidden")
        {
            var thinking = response.ThinkingContent;
            string preview;
            if (_thinkingMode == "full")
                preview = thinking;
            else
            {
                var lines = thinking.Split('\n');
                if (lines.Length <= 8)
                    preview = thinking;
                else
                    preview = string.Join('\n', lines.Take(3))
                        + "\n  ..."
                        + "\n" + string.Join('\n', lines.TakeLast(3));
            }

            // Header: "Thinking" dim, stats dim
            var headerSegments = new List<TextSegment>
            {
                new("Thinking", DarkColors.Dim),
                new($" ({FormatTokens(response.ThinkingTokens)} thinking, {FormatTokens(response.TokensUsed)} context)", DarkColors.Dim)
            };
            WritePanel(headerSegments, preview, DarkColors.ThinkingBorder);
        }

        // Assistant panel (blue border)
        if (!string.IsNullOrWhiteSpace(response.Content))
        {
            // Header: "Assistant" in blue, stats dim
            var headerSegments = new List<TextSegment>
            {
                new("Assistant", DarkColors.AssistantBorder),
                new($" Step {step} ({FormatTokens(response.TokensUsed)} context)", DarkColors.Dim)
            };
            WritePanel(headerSegments, response.Content, DarkColors.AssistantBorder);
        }

        // Verbose: pending tool calls
        if (_verbose && response.ToolCalls.Count > 0)
        {
            foreach (var tc in response.ToolCalls)
            {
                _mainWindow.AddColoredSegments(new List<TextSegment>
                {
                    new($"  >{tc.Name}", DarkColors.Warning),
                    new($"({FormatToolArgs(tc.Name, tc.Arguments)})", DarkColors.Dim)
                });
            }
        }
    }

    public ToolCall? OnToolCallExecuting(ToolCall call, int step) => call;

    public void OnToolCallCompleted(ToolCall call, ToolResult result, long durationMs, int step)
    {
        var icon = result.IsError ? "x" : "+";
        var iconScheme = result.IsError ? DarkColors.ToolErr : DarkColors.ToolOk;

        // Icon + tool name: colored icon, dim duration — multi-color line
        _mainWindow.AddColoredSegments(new List<TextSegment>
        {
            new($"{icon} {call.Name}", iconScheme),
            new($" ({FormatDuration(durationMs)})", DarkColors.Dim)
        });

        // Detail: dim
        _mainWindow.AddColoredBlock($"  {FormatToolArgs(call.Name, call.Arguments)}", DarkColors.Dim);

        // Output
        var output = result.Output;
        if (result.IsError)
        {
            var maxErr = Math.Max(200, _maxToolOutputLines * 40);
            var errText = output.Length > maxErr ? output[..maxErr] + "..." : output;
            _mainWindow.AddColoredBlock($"  {errText}", DarkColors.ToolErr);
        }
        else if (IsWriteTool(call.Name))
            RenderWriteOutput(output);
        else if (call.Name.Equals("read", StringComparison.OrdinalIgnoreCase))
            RenderReadOutput(output);
        else
            RenderCompactOutput(output, Math.Max(3, _maxToolOutputLines / 4));
    }

    public void OnStateChange(AgentState from, AgentState to) => CurrentState = to;

    public void OnCompaction(CompactionResult result)
    {
        _mainWindow.AddColoredSegments(new List<TextSegment>
        {
            new("*", DarkColors.Warning),
            new($" Context compacted (saved {result.TokensSaved} tokens)", DarkColors.Dim)
        });
    }

    public void OnError(string message) => RenderLogMessage(message);

    public void OnStreamChunk(string contentDelta, string? thinkingDelta)
    {
        if (!_isStreaming) { _isStreaming = true; _streamingContent.Clear(); _streamingThinking.Clear(); }
        if (!string.IsNullOrEmpty(contentDelta)) _streamingContent.Append(contentDelta);
        if (!string.IsNullOrEmpty(thinkingDelta)) _streamingThinking.Append(thinkingDelta);
    }

    // --- Public helpers ---

    public void AddUserMessage(string text)
    {
        // User panel with green border, "You" header
        var headerSegments = new List<TextSegment>
        {
            new("You", DarkColors.UserBorder)
        };
        WritePanel(headerSegments, text, DarkColors.UserBorder);
        _mainWindow.AddColoredBlock("", DarkColors.Base);
    }

    public void AddSeparator()
    {
        _mainWindow.AddColoredBlock("\u2500\u2500", DarkColors.Dim);
    }

    public void AddStatusMessage(bool success, int steps, long elapsedMs, int maxContext, int filesChanged)
    {
        var icon = success ? "\u2714" : "\u2718";
        var elapsed = elapsedMs < 1000 ? $"{elapsedMs}ms" : $"{elapsedMs / 1000.0:F1}s";
        var context = FormatTokens(TotalTokens);
        var thinking = FormatTokens(TotalThinkingTokens);
        var maxCtx = FormatTokens(maxContext);

        var iconScheme = success ? DarkColors.ToolOk : DarkColors.ToolErr;

        // Multi-color done line: icon colored, "Done" bold, stats dim
        _mainWindow.AddColoredSegments(new List<TextSegment>
        {
            new($"{icon} ", iconScheme),
            new("Done", DarkColors.Bold),
            new($" {steps} steps, {elapsed}, {context}/{maxCtx} context ({thinking} thinking)", DarkColors.Dim)
        });

        // Files: dim
        if (filesChanged > 0)
            _mainWindow.AddColoredBlock($"  {filesChanged} files changed. Use :files to list.", DarkColors.Dim);

        _mainWindow.AddColoredBlock("", DarkColors.Base);
    }

    public void AddInfoMessage(string text)
    {
        _mainWindow.AddColoredBlock(text, DarkColors.Base);
    }

    // --- Panel rendering ---

    /// <summary>
    /// Rounded box panel: ╭─Header─────────╮ / │ content / ╰─────────────────╯
    /// Header is multi-color segments. Border and content have separate colors.
    /// </summary>
    private void WritePanel(List<TextSegment> headerSegments, string content, ColorScheme borderScheme)
    {
        var width = _mainWindow.GetChatWidth();
        if (width < 20) width = 80;

        // Build header text to measure its length
        var headerLen = 0;
        foreach (var seg in headerSegments)
            headerLen += seg.Text.Length;

        // Top border: ╭─ + header segments + ─...─╮
        var topSegments = new List<TextSegment>();
        topSegments.Add(new("\u256d\u2500", borderScheme));
        topSegments.AddRange(headerSegments);
        var remainingDashes = Math.Max(1, width - 4 - headerLen);
        topSegments.Add(new(new string('\u2500', remainingDashes) + "\u256e", borderScheme));
        _mainWindow.AddColoredSegments(topSegments);

        // Content: │ line │  — border chars in border color, content in white
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line;
            var maxContentWidth = width - 4; // "│ " + content + " │"
            if (trimmed.Length > maxContentWidth && maxContentWidth > 0)
                trimmed = trimmed[..maxContentWidth];
            var padding = Math.Max(0, maxContentWidth - trimmed.Length);

            _mainWindow.AddColoredSegments(new List<TextSegment>
            {
                new("\u2502 ", borderScheme),
                new(trimmed + new string(' ', padding), DarkColors.Content),
                new(" \u2502", borderScheme)
            });
        }

        // Bottom border: ╰─...─╯
        var bottomDashes = Math.Max(1, width - 2);
        _mainWindow.AddColoredBlock(
            "\u2570" + new string('\u2500', bottomDashes - 1) + "\u256f",
            borderScheme);
    }

    // --- Tool output rendering ---

    private static bool IsWriteTool(string name) =>
        name.Equals("write", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("edit", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("patch", StringComparison.OrdinalIgnoreCase);

    private void RenderWriteOutput(string output)
    {
        if (string.IsNullOrEmpty(output)) { _mainWindow.AddColoredBlock("  (empty)", DarkColors.Dim); return; }
        if (output.Contains("wrote") || output.Contains("written") || output.Length < 100)
        { _mainWindow.AddColoredBlock($"  {output}", DarkColors.Dim); return; }

        var lines = output.Split('\n');
        foreach (var line in lines.Take(8))
        {
            if (line.StartsWith("+") && !line.StartsWith("++"))
                _mainWindow.AddColoredBlock($"  +{line[1..]}", DarkColors.ToolOk);
            else if (line.StartsWith("-") && !line.StartsWith("--"))
                _mainWindow.AddColoredBlock($"  -{line[1..]}", DarkColors.ToolErr);
            else
                _mainWindow.AddColoredBlock($"  {line}", DarkColors.Dim);
        }
        var remaining = lines.Length - 8;
        if (remaining > 0) _mainWindow.AddColoredBlock($"  ... {remaining} more lines", DarkColors.Dim);
    }

    private void RenderReadOutput(string output)
    {
        if (string.IsNullOrEmpty(output)) { _mainWindow.AddColoredBlock("  (empty)", DarkColors.Dim); return; }
        var lines = output.Split('\n');
        _mainWindow.AddColoredBlock($"  {lines.Length} lines", DarkColors.Dim);
        foreach (var line in lines.Take(6))
        {
            var t = line.Length > 120 ? line[..120] + "..." : line;
            _mainWindow.AddColoredBlock($"  {t}", DarkColors.Dim);
        }
        var remaining = lines.Length - 6;
        if (remaining > 0) _mainWindow.AddColoredBlock($"  ... {remaining} more lines", DarkColors.Dim);
    }

    private void RenderCompactOutput(string output, int maxLines)
    {
        if (string.IsNullOrEmpty(output)) { _mainWindow.AddColoredBlock("  (empty)", DarkColors.Dim); return; }
        var lines = output.Split('\n');
        foreach (var line in lines.Take(maxLines))
        {
            var t = line.Length > 120 ? line[..120] + "..." : line;
            _mainWindow.AddColoredBlock($"  {t}", DarkColors.Dim);
        }
        var remaining = lines.Length - maxLines;
        if (remaining > 0) _mainWindow.AddColoredBlock($"  ... {remaining} more lines", DarkColors.Dim);
    }

    private void RenderLogMessage(string message)
    {
        if (message.StartsWith("  ")) return;

        if (message.StartsWith("[State]") || message.StartsWith("[Step") || message.StartsWith("[Done]"))
        {
            if (message.Contains("Stall detected") || message.Contains("Step limit") ||
                message.Contains("Error recovery") || message.Contains("Max error recovery"))
            { _mainWindow.AddColoredBlock(message, DarkColors.Warning); return; }
            if (_verbose) _mainWindow.AddColoredBlock(message, DarkColors.Dim);
            return;
        }

        if (message.StartsWith("WARN:") || message.StartsWith("WARN "))
        { _mainWindow.AddColoredBlock(message, DarkColors.Warning); return; }

        if (message.StartsWith("[Injected]"))
        { _mainWindow.AddColoredBlock(message, DarkColors.Dim); return; }

        _mainWindow.AddColoredBlock(message, DarkColors.Error);
    }

    // --- Formatting ---

    private static string FormatToolArgs(string toolName, System.Text.Json.JsonElement args)
        => Agent.FormatToolDetail(toolName, args);

    private static string FormatDuration(long ms) =>
        ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:F1}s";

    private static string FormatTokens(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:F1}M"
        : tokens >= 1_000 ? $"{tokens / 1_000.0:F1}K"
        : $"{tokens}";
}
