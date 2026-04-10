using System.Diagnostics;
using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

class Program
{
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

        // REPL loop
        var observer = new TuiObserver();
        var modelId = resolved.ModelId;

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
                if (HandleCommand(input, console, ref resolved, ref observer, workingDir))
                    continue;
                break; // :quit
            }

            // Run the agent
            console.WriteLine();
            var logger = new SessionLogger(modelId, workingDir);

            using var cts = new CancellationTokenSource();
            var agent = ClientFactory.CreateAgent(resolved!, workingDir, observer, logger);

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

            try
            {
                await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"Running {resolved!.ModelId}...", async ctx =>
                    {
                        // Run the agent in the background, draining observer events
                        var task = agent.RunAsync(input, cts.Token);

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

                // Reset observer for next run
                observer = new TuiObserver();
            }
        }

        return 0;
    }

    /// <summary>
    /// Read user input. Returns null on Ctrl+C or empty quit.
    /// </summary>
    private static string? ReadInput(IAnsiConsole console)
    {
        try
        {
            var input = console.Prompt(
                new TextPrompt<string>("[bold]>[/]")
                    .AllowEmpty());
            return input;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Handle :commands. Returns true to continue the loop, false to quit.
    /// </summary>
    private static bool HandleCommand(string input, IAnsiConsole console,
        ref ResolvedModel? resolved, ref TuiObserver observer, string workingDir)
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
                return false;

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
                return true;

            case ":help":
                console.MarkupLine("[bold]Commands:[/]");
                console.MarkupLine("  [blue]:model [name][/]  Switch model");
                console.MarkupLine("  [blue]:help[/]          Show this help");
                console.MarkupLine("  [blue]:quit[/]          Exit");
                return true;

            default:
                console.MarkupLine($"[red]Unknown command: {Markup.Escape(cmd)}[/]  Type [blue]:help[/] for commands");
                return true;
        }
    }
}
