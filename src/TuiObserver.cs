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

                // Result content
                var output = result.Output;
                if (output.Length > 300)
                    output = output[..300] + "...";

                var lines = new Rows(
                    new Markup(header),
                    new Markup($"  [grey]{Markup.Escape(detail)}[/]"),
                    new Markup($"  [dim]{Markup.Escape(output)}[/]")
                );

                var panel = new Panel(lines)
                    .Border(BoxBorder.None)
                    .Expand()
                    .Padding(1, 0, 0, 0);
                console.Write(panel);
                console.WriteLine();
            });
        }
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
