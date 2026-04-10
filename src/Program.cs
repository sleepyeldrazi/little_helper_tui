using System.Diagnostics;
using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

class Program
{
    // Pending skill content to inject into the next prompt
    private static string? _pendingSkillContent = null;

    static async Task<int> Main(string[] args)
    {
        var console = AnsiConsole.Console;

        // Header
        console.Write(new FigletText("little helper")
            .LeftJustified()
            .Color(Color.Blue));
        console.MarkupLine("[dim]Terminal UI v0.1.0[/]");
        console.WriteLine();

        // Model selection
        var resolved = ModelSelector.Select(console);
        if (resolved == null)
        {
            console.MarkupLine("[red]No model selected. Exiting.[/]");
            return 1;
        }

        var workingDir = Directory.GetCurrentDirectory();
        var modelId = resolved.ModelId;

        // State that persists across REPL iterations
        var observer = new TuiObserver();
        Agent? lastAgent = null;

        while (true)
        {
            // Drain any pending renders from previous run
            observer.Drain(console);

            // Prompt for input
            console.MarkupLine("[dim]──[/]");
            var input = ReadInput(console);
            if (input == null)
            {
                console.MarkupLine("[dim]Goodbye![/]");
                break;
            }

            input = input.Trim();
            if (string.IsNullOrEmpty(input))
                continue;

            // Handle commands
            if (input.StartsWith(':'))
            {
                var cmdResult = HandleCommand(input, console, ref resolved, observer, lastAgent, workingDir);
                if (cmdResult == CommandResult.Quit) break;
                if (cmdResult == CommandResult.Reset)
                {
                    observer = new TuiObserver();
                    lastAgent = null;
                }
                continue;
            }

            // Run the agent
            console.WriteLine();

            // Inject pending skill content if any
            var effectiveInput = input;
            if (_pendingSkillContent != null)
            {
                effectiveInput = $"{_pendingSkillContent}\n\n---\n\n{input}";
                _pendingSkillContent = null;
            }

            var logger = new SessionLogger(modelId, workingDir);

            using var cts = new CancellationTokenSource();
            lastAgent = ClientFactory.CreateAgent(resolved!, workingDir, observer, logger);

            // Render user message
            var userPanel = new Panel(Markup.Escape(input))
                .Header("[green]You[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Green)
                .Expand();
            console.Write(userPanel);
            console.WriteLine();

            // Run agent with a status spinner while waiting
            var sw = Stopwatch.StartNew();
            AgentResult? result = null;
            var agentRef = lastAgent;

            try
            {
                await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"Running {resolved!.ModelId}...", async ctx =>
                    {
                        var task = agentRef.RunAsync(effectiveInput, cts.Token);

                        // Periodically drain events to update display
                        while (!task.IsCompleted)
                        {
                            observer.Drain(console);
                            await Task.Delay(100, cts.Token);
                        }

                        result = await task;
                    });
            }
            catch (OperationCanceledException)
            {
                console.MarkupLine("[yellow]Cancelled.[/]");
                console.WriteLine();
                continue;
            }
            finally
            {
                logger.Dispose();
            }

            sw.Stop();

            // Final drain and result display
            observer.Drain(console);

            if (result != null)
            {
                StatusBar.RenderDone(console, modelId, result, observer.CurrentStep, sw.ElapsedMilliseconds);

                // Reset observer for next run (keep lastAgent for history access)
                observer = new TuiObserver();
            }
        }

        return 0;
    }

    /// <summary>Read user input. Returns null on Ctrl+C or empty quit.</summary>
    private static string? ReadInput(IAnsiConsole console)
    {
        try
        {
            return console.Prompt(
                new TextPrompt<string>("[bold]>[/]")
                    .AllowEmpty());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private enum CommandResult { Continue, Quit, Reset }

    /// <summary>Handle :commands.</summary>
    private static CommandResult HandleCommand(string input, IAnsiConsole console,
        ref ResolvedModel? resolved, TuiObserver observer, Agent? lastAgent, string workingDir)
    {
        var parts = input.Split(' ', 2);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (cmd)
        {
            case ":quit":
            case ":q":
            case ":exit":
                console.MarkupLine("[dim]Goodbye![/]");
                return CommandResult.Quit;

            case ":model":
                if (string.IsNullOrEmpty(arg))
                {
                    resolved = ModelSelector.Select(console);
                    if (resolved != null)
                        console.MarkupLine($"[green]Switched to {resolved.ModelId}[/]");
                }
                else
                {
                    var config = ModelConfig.Load();
                    resolved = config.Resolve(arg);
                    if (resolved != null)
                        console.MarkupLine($"[green]Switched to {resolved.ModelId}[/]");
                    else
                        console.MarkupLine($"[red]Unknown model: {Markup.Escape(arg)}[/]");
                }
                return CommandResult.Continue;

            case ":tokens":
                if (lastAgent != null && resolved != null)
                {
                    TokenBudget.Render(console, lastAgent.History,
                        resolved.ContextWindow,
                        observer.TotalTokens,
                        observer.TotalThinkingTokens);
                }
                else
                {
                    console.MarkupLine("[dim]No conversation yet. Run a prompt first.[/]");
                }
                return CommandResult.Continue;

            case ":history":
                if (lastAgent != null && lastAgent.History.Count > 0)
                {
                    RenderHistory(console, lastAgent.History);
                }
                else
                {
                    console.MarkupLine("[dim]No conversation history yet.[/]");
                }
                return CommandResult.Continue;

            case ":sessions":
                SessionManager.BrowseSessions(console);
                return CommandResult.Continue;

            case ":skills":
                var skillContent = SkillBrowser.Browse(console, workingDir);
                if (skillContent != null)
                {
                    // Store for injection into next prompt
                    _pendingSkillContent = skillContent;
                    console.MarkupLine("[dim]Skill loaded. It will be prepended to your next prompt.[/]");
                }
                return CommandResult.Continue;

            case ":diff":
                if (lastAgent != null)
                {
                    var lastFile = lastAgent.History
                        .Where(m => m.Role == "tool" && m.ToolResult?.FilePath != null && !m.ToolResult.IsError)
                        .Select(m => m.ToolResult!.FilePath!)
                        .LastOrDefault();
                    if (lastFile != null)
                        DiffViewer.ShowLastDiff(console, lastFile);
                    else
                        console.MarkupLine("[dim]No file writes in this session.[/]");
                }
                else
                {
                    console.MarkupLine("[dim]No conversation yet.[/]");
                }
                return CommandResult.Continue;

            case ":reset":
                console.MarkupLine("[dim]Conversation reset.[/]");
                return CommandResult.Reset;

            case ":help":
                console.MarkupLine("[bold]Commands:[/]");
                console.MarkupLine("  [blue]:model [name][/]  Switch model");
                console.MarkupLine("  [blue]:tokens[/]        Show token budget");
                console.MarkupLine("  [blue]:history[/]       Show conversation history");
                console.MarkupLine("  [blue]:sessions[/]      Browse past sessions");
                console.MarkupLine("  [blue]:skills[/]        Browse and inject skills");
                console.MarkupLine("  [blue]:diff[/]          Show diff for last file write");
                console.MarkupLine("  [blue]:reset[/]         Reset conversation");
                console.MarkupLine("  [blue]:help[/]          Show this help");
                console.MarkupLine("  [blue]:quit[/]          Exit");
                return CommandResult.Continue;

            default:
                console.MarkupLine($"[red]Unknown command: {Markup.Escape(cmd)}[/]  Type [blue]:help[/] for commands");
                return CommandResult.Continue;
        }
    }

    /// <summary>Render conversation history as a compact summary.</summary>
    private static void RenderHistory(IAnsiConsole console, IReadOnlyList<ChatMessage> history)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Role")
            .AddColumn("Content");

        foreach (var msg in history.TakeLast(20))
        {
            var role = msg.Role;
            var content = msg.Content ?? "(tool calls)";
            if (content.Length > 80)
                content = content[..80] + "...";

            var roleColor = role switch
            {
                "system" => "blue",
                "user" => "green",
                "assistant" => "cyan",
                "tool" => "yellow",
                _ => "white"
            };

            table.AddRow($"[{roleColor}]{role}[/]", Markup.Escape(content));
        }

        console.Write(table);
        console.WriteLine();
    }
}
