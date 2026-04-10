using System.Collections.Concurrent;
using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Manages agent intervention: pause/resume, tool call interception,
/// and message injection. Runs alongside the agent loop, polling for
/// user commands while the agent works.
/// </summary>
public class InterventionManager
{
    private readonly Agent _agent;
    private readonly IAnsiConsole _console;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentQueue<string> _pendingCommands = new();
    private ToolCall? _toolCallOverride;
    private bool _skipCurrentTool;

    public bool IsPaused => _agent.Control.IsPaused;

    public InterventionManager(Agent agent, IAnsiConsole console, CancellationTokenSource cts)
    {
        _agent = agent;
        _console = console;
        _cts = cts;
    }

    /// <summary>Enqueue a command from the main thread.</summary>
    public void EnqueueCommand(string command) => _pendingCommands.Enqueue(command);

    /// <summary>
    /// Set up tool interception on the agent. Returns the call as-is unless
    /// the user queued a skip/edit command.
    /// </summary>
    public void InstallToolInterceptor()
    {
        _agent.Control.ToolInterceptor = call =>
        {
            // Snapshot files before writes (for diff)
            if (call.Name.Equals("write", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (call.Arguments.TryGetProperty("path", out var pathEl))
                    {
                        var path = pathEl.GetString();
                        if (path != null)
                        {
                            var fullPath = Path.GetFullPath(Path.Combine(
                                Directory.GetCurrentDirectory(), path));
                            DiffViewer.Snapshot(fullPath);
                        }
                    }
                }
                catch { /* best effort */ }
            }

            // Check if there's a pending skip/edit
            if (_skipCurrentTool)
            {
                _skipCurrentTool = false;
                _console.MarkupLine($"[yellow]Skipped: {call.Name}[/]");
                return null; // null = skip
            }

            if (_toolCallOverride != null)
            {
                var overriden = _toolCallOverride;
                _toolCallOverride = null;
                return overriden;
            }

            return call;
        };
    }

    /// <summary>Pause the agent.</summary>
    public void Pause()
    {
        _agent.Control.Pause();
        _console.MarkupLine("[yellow]Paused. Use :resume to continue, :skip to skip next tool, :inject <msg> to redirect.[/]");
    }

    /// <summary>Resume the agent.</summary>
    public void Resume()
    {
        _agent.Control.Resume();
        _console.MarkupLine("[green]Resumed.[/]");
    }

    /// <summary>Skip the next tool call.</summary>
    public void SkipNextTool()
    {
        _skipCurrentTool = true;
        _console.MarkupLine("[yellow]Will skip next tool call.[/]");
    }

    /// <summary>Inject a redirect message.</summary>
    public void InjectMessage(string message)
    {
        _agent.Control.InjectMessage(message);
        _console.MarkupLine($"[green]Injected: {Markup.Escape(message[..Math.Min(80, message.Length)])}[/]");
    }
}
