using System.Text;
using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// IAgentObserver implementation that renders agent activity via Spectre.Console.
/// Accumulates events during the agent loop and renders them to the console.
/// Thread-safe: events are queued and drained on the main thread.
/// </summary>
public class TuiObserver : IAgentObserver
{
    private readonly object _lock = new();
    private readonly List<Action<IAnsiConsole>> _renderQueue = new();
    private readonly List<Action<IAnsiConsole>> _renderHistory = new(); // survives Drain for redraw
    private readonly StringBuilder _streamingContent = new();
    private readonly StringBuilder _streamingThinking = new();
    private bool _isStreaming;

    // Config-driven display settings
    private readonly string _thinkingMode;       // "full", "condensed", "hidden"
    private readonly int _maxToolOutputLines;    // replaces hardcoded limits
    private readonly string _theme;              // "default", "monochrome", "dark"
    private readonly bool _verbose;              // show > pending calls and state transitions

    public int CurrentStep { get; private set; }
    public AgentState CurrentState { get; private set; } = AgentState.Planning;
    public int TotalTokens { get; private set; }
    public int TotalThinkingTokens { get; private set; }
    public bool IsPaused { get; set; }

    /// <summary>Current streaming content (for live display during spin loop).</summary>
    public string StreamingPreview
    {
        get
        {
            lock (_lock)
            {
                if (_streamingContent.Length == 0) return "";
                var text = _streamingContent.ToString();
                // Last 80 chars for compact preview
                if (text.Length > 80) text = "..." + text[^77..];
                return text.Replace("\n", " ").Trim();
            }
        }
    }

    public TuiObserver(TuiConfig? config = null)
    {
        config ??= new TuiConfig();
        _thinkingMode = config.ThinkingMode;
        _maxToolOutputLines = config.MaxToolOutputLines;
        _theme = config.Theme;
        _verbose = config.Verbose;
    }

    /// <summary>Reset observer state (for model switches and :reset).</summary>
    public void Reset()
    {
        TotalTokens = 0;
        TotalThinkingTokens = 0;
        CurrentStep = 0;
        CurrentState = AgentState.Planning;
        lock (_lock)
        {
            _renderQueue.Clear();
            _renderHistory.Clear();
        }
    }

    /// <summary>Drain and render all queued events to the console.</summary>
    public void Drain(IAnsiConsole console)
    {
        List<Action<IAnsiConsole>> batch;
        lock (_lock)
        {
            batch = new List<Action<IAnsiConsole>>(_renderQueue);
            _renderQueue.Clear();
            _renderHistory.AddRange(batch);
        }

        foreach (var action in batch)
            action(console);
    }

    /// <summary>
    /// Record a render action into history for redraw.
    /// Use for UI elements written outside observer callbacks
    /// (user panels, separators, status bar) so they survive redraw.
    /// </summary>
    public void Record(Action<IAnsiConsole> action)
    {
        lock (_lock)
        {
            _renderHistory.Add(action);
        }
        action(AnsiConsole.Console);
    }

    /// <summary>
    /// Clear screen and replay all rendered panels at the current terminal width.
    /// Called when terminal width changes (font size, window resize).
    /// </summary>
    public void Redraw(IAnsiConsole console)
    {
        List<Action<IAnsiConsole>> snapshot;
        lock (_lock)
        {
            snapshot = new List<Action<IAnsiConsole>>(_renderHistory);
        }

        Console.Clear();
        foreach (var action in snapshot)
            action(console);
        Console.Out.Flush();
    }

    // --- IAgentObserver implementation ---

    public void OnStepStart(int step)
    {
        CurrentStep = step;
    }

    public void OnModelResponse(ModelResponse response, int step)
    {
        TotalTokens += response.TokensUsed;
        TotalThinkingTokens += response.ThinkingTokens;
        _isStreaming = false;

        lock (_lock)
        {
            // Show thinking if present and mode allows
            if (!string.IsNullOrEmpty(response.ThinkingContent) && _thinkingMode != "hidden")
            {
                _renderQueue.Add(console =>
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

                    var ctxTok = FormatTokens(response.TokensUsed);
                    var thinkTok = FormatTokens(response.ThinkingTokens);
                    var thinkingPanel = new Panel(Markup.Escape(preview))
                        .Header($"[dim]Thinking[/] [dim]({thinkTok} thinking, {ctxTok} context)[/]")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Grey)
                        .Expand();
                    console.Write(thinkingPanel);
                });
            }

            // Show assistant text content
            if (!string.IsNullOrWhiteSpace(response.Content))
            {
                _renderQueue.Add(console =>
                {
                    var ctxTok = FormatTokens(response.TokensUsed);
                    var panel = new Panel(Markup.Escape(response.Content))
                        .Header($"[blue]Assistant[/] [dim]Step {step} ({ctxTok} context)[/]")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Blue)
                        .Expand();
                    console.Write(panel);
                });
            }

            // Show tool calls summary (pending > lines) — only in verbose mode
            if (_verbose && response.ToolCalls.Count > 0)
            {
                _renderQueue.Add(console =>
                {
                    foreach (var tc in response.ToolCalls)
                    {
                        var detail = FormatToolArgs(tc.Name, tc.Arguments);
                        var line = $"  [yellow]>{tc.Name}[/]([grey]{Markup.Escape(detail)}[/])";
                        console.MarkupLine(line);
                    }
                    console.WriteLine();
                });
            }
        }
    }

    public ToolCall? OnToolCallExecuting(ToolCall call, int step)
    {
        // No-op: the actual interception pipeline goes through
        // AgentControl.ToolInterceptor (set in Program.cs).
        return call;
    }

    public void OnToolCallCompleted(ToolCall call, ToolResult result, long durationMs, int step)
    {
        lock (_lock)
        {
            _renderQueue.Add(console =>
            {
                var color = result.IsError ? "red" : "green";
                var icon = result.IsError ? "x" : "+";
                var detail = FormatToolArgs(call.Name, call.Arguments);

                // Tool result header
                var header = $"[{color}]{icon} {call.Name}[/] [dim]({FormatDuration(durationMs)})[/]";
                console.MarkupLine(header);
                console.MarkupLine($"  [grey]{Markup.Escape(detail)}[/]");

                // Render output based on tool type
                var output = result.Output;
                if (result.IsError)
                {
                    // Errors: show in red, truncated
                    var maxErr = Math.Max(200, _maxToolOutputLines * 40);
                    var errText = output.Length > maxErr ? output[..maxErr] + "..." : output;
                    console.MarkupLine($"  [red]{Markup.Escape(errText)}[/]");
                }
                else if (call.Name.Equals("write", StringComparison.OrdinalIgnoreCase) ||
                         call.Name.Equals("edit", StringComparison.OrdinalIgnoreCase) ||
                         call.Name.Equals("patch", StringComparison.OrdinalIgnoreCase))
                {
                    RenderWriteOutput(console, output);
                }
                else if (call.Name.Equals("read", StringComparison.OrdinalIgnoreCase))
                {
                    RenderReadOutput(console, output);
                }
                else
                {
                    // Generic: show compact
                    var maxLines = Math.Max(3, _maxToolOutputLines / 4);
                    RenderCompactOutput(console, output, maxLines);
                }

                console.WriteLine();
            });
        }
    }

    /// <summary>Render write output with git-style +/- diff markers.</summary>
    private static void RenderWriteOutput(IAnsiConsole console, string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            console.MarkupLine("  [dim](empty)[/]");
            return;
        }

        // Check if the output looks like diff content or just a confirmation
        if (output.Contains("wrote") || output.Contains("written") || output.Length < 100)
        {
            // Short confirmation message
            console.MarkupLine($"  [green]{Markup.Escape(output)}[/]");
            return;
        }

        // Multi-line content: show with +/- markers
        var lines = output.Split('\n');
        var maxShow = 8;

        foreach (var line in lines.Take(maxShow))
        {
            var escaped = Markup.Escape(line);
            if (line.StartsWith("+") && !line.StartsWith("++"))
                console.MarkupLine($"  [green]+{escaped[1..]}[/]");
            else if (line.StartsWith("-") && !line.StartsWith("--"))
                console.MarkupLine($"  [red]-{escaped[1..]}[/]");
            else if (line.StartsWith("@@"))
                console.MarkupLine($"  [cyan]{escaped}[/]");
            else
                console.MarkupLine($"  [dim]{escaped}[/]");
        }

        var remaining = lines.Length - maxShow;
        if (remaining > 0)
            console.MarkupLine($"  [dim]... {remaining} more lines[/]");
    }

    /// <summary>Render read output: show path + line count + first few lines.</summary>
    private static void RenderReadOutput(IAnsiConsole console, string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            console.MarkupLine("  [dim](empty)[/]");
            return;
        }

        var lines = output.Split('\n');
        var maxShow = 6;

        // Show file stats
        console.MarkupLine($"  [dim]{lines.Length} lines[/]");

        // Show first few lines — truncate raw text BEFORE escaping to avoid
        // cutting mid-escape-sequence (e.g. [[ for literal [)
        foreach (var line in lines.Take(maxShow))
        {
            var truncated = line.Length > 120 ? line[..120] + "..." : line;
            console.MarkupLine($"  [dim]{Markup.Escape(truncated)}[/]");
        }

        var remaining = lines.Length - maxShow;
        if (remaining > 0)
            console.MarkupLine($"  [dim]... {remaining} more lines[/]");
    }

    /// <summary>Render generic output compactly.</summary>
    private static void RenderCompactOutput(IAnsiConsole console, string output, int maxLines)
    {
        if (string.IsNullOrEmpty(output))
        {
            console.MarkupLine("  [dim](empty)[/]");
            return;
        }

        var lines = output.Split('\n');
        foreach (var line in lines.Take(maxLines))
        {
            var truncated = line.Length > 120 ? line[..120] + "..." : line;
            console.MarkupLine($"  [dim]{Markup.Escape(truncated)}[/]");
        }

        var remaining = lines.Length - maxLines;
        if (remaining > 0)
            console.MarkupLine($"  [dim]... {remaining} more lines[/]");
    }

    public void OnStateChange(AgentState from, AgentState to)
    {
        CurrentState = to;
    }

    public void OnCompaction(CompactionResult result)
    {
        lock (_lock)
        {
            _renderQueue.Add(console =>
            {
                console.MarkupLine($"[dim][yellow]*[/] Context compacted (saved {result.TokensSaved} tokens)[/]");
                console.WriteLine();
            });
        }
    }

    public void OnError(string message)
    {
        lock (_lock)
        {
            _renderQueue.Add(console =>
            {
                RenderLogMessage(console, message);
            });
        }
    }

    /// <summary>
    /// Render a log message from core's Agent.Log() / observer routing.
    /// Categorizes by prefix to use appropriate styling:
    /// - [State] / [Step] / [Done] -> dim (informational), hidden unless verbose
    /// - WARN: -> yellow (warning)
    /// - ERROR: / Model API error / actual failures -> red (error)
    /// - tool execution detail lines -> suppressed (redundant with OnToolCallCompleted)
    /// </summary>
    private void RenderLogMessage(IAnsiConsole console, string message)
    {
        var escaped = Markup.Escape(message);

        // Tool execution detail (redundant with OnToolCallCompleted display)
        // Always suppress — the + line already shows name + args + timing + output
        if (message.StartsWith("  "))
            return;

        // Informational: state transitions, step progress, completion
        if (message.StartsWith("[State]") || message.StartsWith("[Step") || message.StartsWith("[Done]"))
        {
            // Always show stall/limit/error recovery states regardless of verbose
            if (message.Contains("Stall detected") || message.Contains("Step limit") ||
                message.Contains("Error recovery") || message.Contains("Max error recovery"))
            {
                console.MarkupLine($"[yellow]{escaped}[/]");
                return;
            }
            // verbose-only for routine state/step/done messages
            if (_verbose)
                console.MarkupLine($"[dim]{escaped}[/]");
            return;
        }

        // Warnings
        if (message.StartsWith("WARN:") || message.StartsWith("WARN "))
        {
            console.MarkupLine($"[yellow]{escaped}[/]");
            return;
        }

        // Injected messages
        if (message.StartsWith("[Injected]"))
        {
            console.MarkupLine($"[dim][yellow]{escaped}[/][/]");
            return;
        }

        // Everything else: actual errors
        console.MarkupLine($"[red]{escaped}[/]");
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

    // --- Helpers ---

    // Use core's public formatter so new tools are handled automatically
    private static string FormatToolArgs(string toolName, System.Text.Json.JsonElement args)
        => Agent.FormatToolDetail(toolName, args);

    private static string FormatDuration(long ms) =>
        ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:F1}s";

    private static string FormatTokens(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:F1}M"
        : tokens >= 1_000 ? $"{tokens / 1_000.0:F1}K"
        : $"{tokens}";
}