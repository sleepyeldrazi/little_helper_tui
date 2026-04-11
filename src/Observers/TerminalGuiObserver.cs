using System.Text;
using LittleHelper;
using LittleHelperTui.Views;
using Terminal.Gui;

namespace LittleHelperTui.Observers;

/// <summary>
/// Renders agent activity as colored text blocks into MainWindow.
/// Panels split into 3 blocks: colored border top, white content, colored border bottom.
/// Matches old Spectre visual style.
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

        // Thinking (grey border, white content)
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

            var header = $"Thinking ({FormatTokens(response.ThinkingTokens)} thinking, {FormatTokens(response.TokensUsed)} context)";
            WritePanel(header, preview, DarkColors.ThinkingBorder);
        }

        // Assistant (blue border, white content)
        if (!string.IsNullOrWhiteSpace(response.Content))
        {
            var header = $"Assistant Step {step} ({FormatTokens(response.TokensUsed)} context)";
            WritePanel(header, response.Content, DarkColors.AssistantBorder);
        }

        // Verbose: pending tool calls
        if (_verbose && response.ToolCalls.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var tc in response.ToolCalls)
                sb.AppendLine($"  >{tc.Name}({FormatToolArgs(tc.Name, tc.Arguments)})");
            _mainWindow.AddColoredBlock(sb.ToString(), DarkColors.Warning);
        }
    }

    public ToolCall? OnToolCallExecuting(ToolCall call, int step) => call;

    public void OnToolCallCompleted(ToolCall call, ToolResult result, long durationMs, int step)
    {
        var icon = result.IsError ? "x" : "+";
        var iconScheme = result.IsError ? DarkColors.ToolErr : DarkColors.ToolOk;

        // Icon + tool name line: colored
        _mainWindow.AddColoredBlock(
            $"{icon} {call.Name} ({FormatDuration(durationMs)})",
            iconScheme);

        // Detail + output: dim
        var sb = new StringBuilder();
        sb.AppendLine($"  {FormatToolArgs(call.Name, call.Arguments)}");

        var output = result.Output;
        if (result.IsError)
        {
            var maxErr = Math.Max(200, _maxToolOutputLines * 40);
            var errText = output.Length > maxErr ? output[..maxErr] + "..." : output;
            sb.AppendLine($"  {errText}");
        }
        else if (IsWriteTool(call.Name))
            AppendWriteOutput(sb, output);
        else if (call.Name.Equals("read", StringComparison.OrdinalIgnoreCase))
            AppendReadOutput(sb, output);
        else
            AppendCompactOutput(sb, output, Math.Max(3, _maxToolOutputLines / 4));

        _mainWindow.AddColoredBlock(sb.ToString(), DarkColors.Dim);
    }

    public void OnStateChange(AgentState from, AgentState to) => CurrentState = to;

    public void OnCompaction(CompactionResult result)
    {
        _mainWindow.AddColoredBlock(
            $"* Context compacted (saved {result.TokensSaved} tokens)", DarkColors.Warning);
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
        WritePanel("You", text, DarkColors.UserBorder);
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

        // Icon: colored
        var iconScheme = success ? DarkColors.ToolOk : DarkColors.ToolErr;
        _mainWindow.AddColoredBlock($"{icon} Done {steps} steps, {elapsed}, {context}/{maxCtx} context ({thinking} thinking)",
            iconScheme);

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
    /// 3-block panel: colored top border, white content, colored bottom border.
    /// No right-side borders to avoid width alignment bugs.
    /// </summary>
    private void WritePanel(string header, string content, ColorScheme borderScheme)
    {
        // Top border: ╭─Header──────────
        _mainWindow.AddColoredBlock(
            $"\u256d\u2500{header}\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            borderScheme);

        // Content: white text with │ prefix
        var sb = new StringBuilder();
        foreach (var line in content.Split('\n'))
            sb.AppendLine($"\u2502 {line}");
        _mainWindow.AddColoredBlock(sb.ToString(), DarkColors.Content);

        // Bottom border: ╰──────────
        _mainWindow.AddColoredBlock(
            "\u2570\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            borderScheme);
    }

    // --- Tool output ---

    private static bool IsWriteTool(string name) =>
        name.Equals("write", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("edit", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("patch", StringComparison.OrdinalIgnoreCase);

    private static void AppendWriteOutput(StringBuilder sb, string output)
    {
        if (string.IsNullOrEmpty(output)) { sb.AppendLine("  (empty)"); return; }
        if (output.Contains("wrote") || output.Contains("written") || output.Length < 100)
        { sb.AppendLine($"  {output}"); return; }

        var lines = output.Split('\n');
        foreach (var line in lines.Take(8))
        {
            if (line.StartsWith("+") && !line.StartsWith("++"))
                sb.AppendLine($"  +{line[1..]}");
            else if (line.StartsWith("-") && !line.StartsWith("--"))
                sb.AppendLine($"  -{line[1..]}");
            else
                sb.AppendLine($"  {line}");
        }
        var remaining = lines.Length - 8;
        if (remaining > 0) sb.AppendLine($"  ... {remaining} more lines");
    }

    private static void AppendReadOutput(StringBuilder sb, string output)
    {
        if (string.IsNullOrEmpty(output)) { sb.AppendLine("  (empty)"); return; }
        var lines = output.Split('\n');
        sb.AppendLine($"  {lines.Length} lines");
        foreach (var line in lines.Take(6))
        {
            var t = line.Length > 120 ? line[..120] + "..." : line;
            sb.AppendLine($"  {t}");
        }
        var remaining = lines.Length - 6;
        if (remaining > 0) sb.AppendLine($"  ... {remaining} more lines");
    }

    private static void AppendCompactOutput(StringBuilder sb, string output, int maxLines)
    {
        if (string.IsNullOrEmpty(output)) { sb.AppendLine("  (empty)"); return; }
        var lines = output.Split('\n');
        foreach (var line in lines.Take(maxLines))
        {
            var t = line.Length > 120 ? line[..120] + "..." : line;
            sb.AppendLine($"  {t}");
        }
        var remaining = lines.Length - maxLines;
        if (remaining > 0) sb.AppendLine($"  ... {remaining} more lines");
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
