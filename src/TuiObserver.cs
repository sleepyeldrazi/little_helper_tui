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
    private readonly StringBuilder _streamingContent = new();
    private readonly StringBuilder _streamingThinking = new();
    private bool _isStreaming;

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

    /// <summary>Drain and render all queued events to the console.</summary>
    public void Drain(IAnsiConsole console)
    {
        List<Action<IAnsiConsole>> batch;
        lock (_lock)
        {
            batch = new List<Action<IAnsiConsole>>(_renderQueue);
            _renderQueue.Clear();
        }

        foreach (var action in batch)
            action(console);
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
            // Show thinking if present
            if (!string.IsNullOrEmpty(response.ThinkingContent))
            {
                _renderQueue.Add(console =>
                {
                    var thinking = response.ThinkingContent;
                    var lines = thinking.Split('\n');
                    string preview;
                    if (lines.Length <= 8)
                        preview = thinking;
                    else
                        preview = string.Join('\n', lines.Take(3))
                            + "\n  ..."
                            + "\n" + string.Join('\n', lines.TakeLast(3));

                    var thinkingPanel = new Panel(Markup.Escape(preview))
                        .Header($"[dim]Thinking[/] [dim]({response.ThinkingTokens} tokens)[/]")
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
                    var panel = new Panel(Markup.Escape(response.Content))
                        .Header($"[blue]Assistant[/] [dim]Step {step}[/]")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Blue)
                        .Expand();
                    console.Write(panel);
                });
            }

            // Show tool calls summary
            if (response.ToolCalls.Count > 0)
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
        // Diff snapshotting is handled by InterventionManager's ToolInterceptor
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
                    var errText = output.Length > 200 ? output[..200] + "..." : output;
                    console.MarkupLine($"  [red]{Markup.Escape(errText)}[/]");
                }
                else if (call.Name.Equals("write", StringComparison.OrdinalIgnoreCase))
                {
                    RenderWriteOutput(console, output);
                }
                else if (call.Name.Equals("read", StringComparison.OrdinalIgnoreCase))
                {
                    RenderReadOutput(console, output);
                }
                else
                {
                    // Generic: show compact (max 5 lines)
                    RenderCompactOutput(console, output, 5);
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
        var shown = 0;

        foreach (var line in lines)
        {
            if (shown >= maxShow) break;

            var escaped = Markup.Escape(line);
            if (line.StartsWith("+") && !line.StartsWith("++"))
            {
                console.MarkupLine($"  [green]+{escaped[1..]}[/]");
            }
            else if (line.StartsWith("-") && !line.StartsWith("--"))
            {
                console.MarkupLine($"  [red]-{escaped[1..]}[/]");
            }
            else if (line.StartsWith("@@"))
            {
                console.MarkupLine($"  [cyan]{escaped}[/]");
            }
            else
            {
                console.MarkupLine($"  [dim]{escaped}[/]");
            }
            shown++;
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

        // Show first few lines
        foreach (var line in lines.Take(maxShow))
        {
            var escaped = Markup.Escape(line);
            if (escaped.Length > 120) escaped = escaped[..120] + "...";
            console.MarkupLine($"  [dim]{escaped}[/]");
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
            var escaped = Markup.Escape(line);
            if (escaped.Length > 120) escaped = escaped[..120] + "...";
            console.MarkupLine($"  [dim]{escaped}[/]");
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
                console.MarkupLine($"[red]! {Markup.Escape(message)}[/]");
            });
        }
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

    private static string FormatToolArgs(string toolName, System.Text.Json.JsonElement args)
    {
        try
        {
            return toolName.ToLowerInvariant() switch
            {
                "read" => args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                "write" => args.TryGetProperty("path", out var w) ? w.GetString() ?? "" : "",
                "run" or "bash" => args.TryGetProperty("command", out var c)
                    ? Truncate(c.GetString() ?? "", 80) : "",
                "search" => args.TryGetProperty("pattern", out var s)
                    ? $"\"{s.GetString()}\"" : "",
                _ => Truncate(args.GetRawText(), 60)
            };
        }
        catch { return ""; }
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";

    private static string FormatDuration(long ms) =>
        ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:F1}s";
}
