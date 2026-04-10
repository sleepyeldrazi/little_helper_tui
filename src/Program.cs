using System.Diagnostics;
using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

class Program
{
    private static string? _pendingSkillContent = null;

    static async Task<int> Main(string[] args)
    {
        var console = AnsiConsole.Console;

        console.Write(new FigletText("little helper")
            .LeftJustified()
            .Color(Color.Blue));
        console.MarkupLine("[dim]Terminal UI v0.1.0[/]");
        console.WriteLine();

        var resolved = ModelSelector.Select(console);
        if (resolved == null)
        {
            console.MarkupLine("[red]No model selected. Exiting.[/]");
            return 1;
        }

        var workingDir = Directory.GetCurrentDirectory();
        var modelId = resolved.ModelId;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            console.MarkupLine("[yellow]Use :quit to exit.[/]");
        };

        var observer = new TuiObserver();
        Agent? lastAgent = null;

        while (true)
        {
            observer.Drain(console);
            console.MarkupLine("[dim]──[/]");

            var input = ReadInput(console);
            if (input == null) { console.MarkupLine("[dim]Goodbye![/]"); break; }

            input = input.Trim();
            if (string.IsNullOrEmpty(input)) continue;

            // Commands
            if (input.StartsWith(':'))
            {
                var (result, newModel) = await HandleCommand(input, console, resolved, observer, lastAgent, workingDir);
                if (result == CmdResult.Quit) break;
                if (result == CmdResult.Reset) { observer = new TuiObserver(); lastAgent = null; }
                if (newModel != null) resolved = newModel;
                continue;
            }

            // Run agent
            console.WriteLine();

            var effectiveInput = input;
            if (_pendingSkillContent != null)
            {
                effectiveInput = $"{_pendingSkillContent}\n\n---\n\n{input}";
                _pendingSkillContent = null;
            }

            var logger = new SessionLogger(modelId, workingDir);
            using var cts = new CancellationTokenSource();
            lastAgent = ClientFactory.CreateAgent(resolved!, workingDir, observer, logger);

            var userPanel = new Panel(Markup.Escape(input))
                .Header("[green]You[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Green)
                .Expand();
            console.Write(userPanel);
            console.WriteLine();

            // Set up tool interceptor for diff snapshots
            lastAgent.Control.ToolInterceptor = call =>
            {
                if (call.Name.Equals("write", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (call.Arguments.TryGetProperty("path", out var pathEl))
                        {
                            var path = pathEl.GetString();
                            if (path != null)
                                DiffViewer.Snapshot(Path.GetFullPath(Path.Combine(workingDir, path)));
                        }
                    }
                    catch { }
                }
                return call;
            };

            var sw = Stopwatch.StartNew();
            AgentResult? result2 = null;
            var agentRef = lastAgent;

            // Redirect stderr so core's Console.Error.WriteLine doesn't
            // corrupt the Spectre spinner display
            var originalStderr = Console.Error;
            var stderrCapture = new StringWriter();
            Console.SetError(stderrCapture);

            try
            {
                await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"Running {resolved!.ModelId}...", async ctx =>
                    {
                        var task = agentRef.RunAsync(effectiveInput, cts.Token);
                        while (!task.IsCompleted)
                        {
                            observer.Drain(console);
                            ctx.Status($"Running {resolved!.ModelId}... Step {observer.CurrentStep} [dim](Space=pause, Ctrl+C=cancel)[/]");
                            await Task.Delay(100, cts.Token);
                        }
                        result2 = await task;
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
                Console.SetError(originalStderr);
                logger.Dispose();
            }

            // Show captured stderr as clean error messages (deduplicate, limit)
            var stderrLines = stderrCapture.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Distinct()
                .Take(5)
                .ToList();
            foreach (var line in stderrLines)
            {
                var msg = line.Length > 120 ? line[..120] + "..." : line;
                console.MarkupLine($"[red]! {Markup.Escape(msg)}[/]");
            }

            sw.Stop();
            observer.Drain(console);

            if (result2 != null)
            {
                StatusBar.RenderDone(console, modelId, result2, observer.CurrentStep, sw.ElapsedMilliseconds);
                observer = new TuiObserver();
            }
        }

        return 0;
    }

    private static string? ReadInput(IAnsiConsole console)
    {
        try { return console.Prompt(new TextPrompt<string>("[bold]>[/]").AllowEmpty()); }
        catch { return null; }
    }

    private enum CmdResult { Continue, Quit, Reset }

    private static async Task<(CmdResult Result, ResolvedModel? NewModel)> HandleCommand(
        string input, IAnsiConsole console, ResolvedModel? resolved,
        TuiObserver observer, Agent? lastAgent, string workingDir)
    {
        var parts = input.Split(' ', 2);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (cmd)
        {
            case ":quit" or ":q" or ":exit":
                console.MarkupLine("[dim]Goodbye![/]");
                return (CmdResult.Quit, null);

            case ":model":
                ResolvedModel? newResolved;
                if (string.IsNullOrEmpty(arg))
                {
                    newResolved = ModelSelector.Select(console);
                }
                else
                {
                    newResolved = ModelConfig.Load().Resolve(arg);
                    if (newResolved == null)
                        console.MarkupLine($"[red]Unknown model: {Markup.Escape(arg)}[/]");
                }
                if (newResolved != null)
                    console.MarkupLine($"[green]Switched to {newResolved.ModelId}[/]");
                return (CmdResult.Continue, newResolved);

            case ":tokens":
                if (lastAgent != null && resolved != null)
                    TokenBudget.Render(console, lastAgent.History, resolved.ContextWindow, observer.TotalTokens, observer.TotalThinkingTokens);
                else
                    console.MarkupLine("[dim]No conversation yet.[/]");
                return (CmdResult.Continue, null);

            case ":history":
                if (lastAgent?.History.Count > 0) RenderHistory(console, lastAgent.History);
                else console.MarkupLine("[dim]No conversation history yet.[/]");
                return (CmdResult.Continue, null);

            case ":sessions":
                SessionManager.BrowseSessions(console);
                return (CmdResult.Continue, null);

            case ":skills":
                var skillContent = SkillBrowser.Browse(console, workingDir);
                if (skillContent != null)
                {
                    _pendingSkillContent = skillContent;
                    console.MarkupLine("[dim]Skill loaded. It will be prepended to your next prompt.[/]");
                }
                return (CmdResult.Continue, null);

            case ":diff":
                if (lastAgent != null)
                {
                    var lastFile = lastAgent.History
                        .Where(m => m.Role == "tool" && m.ToolResult?.FilePath != null && !m.ToolResult.IsError)
                        .Select(m => m.ToolResult!.FilePath!)
                        .LastOrDefault();
                    if (lastFile != null) DiffViewer.ShowLastDiff(console, lastFile);
                    else console.MarkupLine("[dim]No file writes in this session.[/]");
                }
                else console.MarkupLine("[dim]No conversation yet.[/]");
                return (CmdResult.Continue, null);

            case ":arena":
                console.MarkupLine("[bold]Select two models for arena mode:[/]");
                console.MarkupLine("[dim]Model 1:[/]");
                var m1 = ModelSelector.Select(console);
                if (m1 == null) return (CmdResult.Continue, null);
                console.MarkupLine("[dim]Model 2:[/]");
                var m2 = ModelSelector.Select(console);
                if (m2 == null) return (CmdResult.Continue, null);
                var arenaPrompt = console.Prompt(new TextPrompt<string>("[bold]Arena prompt:[/]"));
                await ModelArena.RunArena(console, arenaPrompt, m1, m2, workingDir);
                return (CmdResult.Continue, null);

            case ":reset":
                console.MarkupLine("[dim]Conversation reset.[/]");
                return (CmdResult.Reset, null);

            case ":help":
                console.MarkupLine("[bold]Commands:[/]");
                console.MarkupLine("  [blue]:model [name][/]  Switch model");
                console.MarkupLine("  [blue]:tokens[/]        Show token budget");
                console.MarkupLine("  [blue]:history[/]       Show conversation history");
                console.MarkupLine("  [blue]:sessions[/]      Browse past sessions");
                console.MarkupLine("  [blue]:skills[/]        Browse and inject skills");
                console.MarkupLine("  [blue]:diff[/]          Show diff for last file write");
                console.MarkupLine("  [blue]:arena[/]         A/B test two models side-by-side");
                console.MarkupLine("  [blue]:reset[/]         Reset conversation");
                console.MarkupLine("  [blue]:help[/]          Show this help");
                console.MarkupLine("  [blue]:quit[/]          Exit");
                console.MarkupLine("");
                console.MarkupLine("[dim]During agent run: Space=pause/resume, Ctrl+C=cancel[/]");
                return (CmdResult.Continue, null);

            default:
                console.MarkupLine($"[red]Unknown command: {Markup.Escape(cmd)}[/]  Type [blue]:help[/] for commands");
                return (CmdResult.Continue, null);
        }
    }

    private static void RenderHistory(IAnsiConsole console, IReadOnlyList<ChatMessage> history)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Role").AddColumn("Content");

        foreach (var msg in history.TakeLast(20))
        {
            var content = msg.Content ?? "(tool calls)";
            if (content.Length > 80) content = content[..80] + "...";
            var roleColor = msg.Role switch { "system" => "blue", "user" => "green", "assistant" => "teal", "tool" => "yellow", _ => "white" };
            table.AddRow($"[{roleColor}]{msg.Role}[/]", Markup.Escape(content));
        }

        console.Write(table);
        console.WriteLine();
    }
}
