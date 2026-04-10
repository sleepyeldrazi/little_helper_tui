using System.Diagnostics;
using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Model arena: runs two models side-by-side on the same prompt
/// and compares their results. Uses two independent Agent instances.
/// </summary>
public static class ModelArena
{
    public static async Task RunArena(IAnsiConsole console, string prompt,
        ResolvedModel model1, ResolvedModel model2, string workingDir)
    {
        console.MarkupLine($"[bold]Arena:[/] [blue]{model1.ModelId}[/] vs [blue]{model2.ModelId}[/]");
        console.MarkupLine($"[dim]Prompt: {Markup.Escape(prompt.Length > 80 ? prompt[..80] + "..." : prompt)}[/]");
        console.WriteLine();

        var logger1 = new SessionLogger(model1.ModelId + "_arena", workingDir);
        var logger2 = new SessionLogger(model2.ModelId + "_arena", workingDir);

        var observer1 = new TuiObserver();
        var observer2 = new TuiObserver();

        var cts = new CancellationTokenSource();

        // Create two agents
        var agent1 = ClientFactory.CreateAgent(model1, workingDir, observer1, logger1);
        var agent2 = ClientFactory.CreateAgent(model2, workingDir, observer2, logger2);

        // Run both in parallel
        var sw = Stopwatch.StartNew();
        var task1 = agent1.RunAsync(prompt, cts.Token);
        var task2 = agent2.RunAsync(prompt, cts.Token);

        // Show progress for both
        await console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Running arena...", async ctx =>
            {
                while (!task1.IsCompleted || !task2.IsCompleted)
                {
                    observer1.Drain(console);
                    observer2.Drain(console);

                    var s1 = task1.IsCompleted ? "done" : $"step {observer1.CurrentStep}";
                    var s2 = task2.IsCompleted ? "done" : $"step {observer2.CurrentStep}";
                    ctx.Status($"Arena: {model1.ModelId} ({s1}) vs {model2.ModelId} ({s2})");

                    await Task.Delay(200, cts.Token);
                }
            });

        sw.Stop();

        // Get results
        var result1 = await task1;
        var result2 = await task2;

        // Drain final events
        observer1.Drain(console);
        observer2.Drain(console);

        // Show comparison table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Arena Results[/]")
            .AddColumn("")
            .AddColumn($"[blue]{model1.ModelId}[/]")
            .AddColumn($"[blue]{model2.ModelId}[/]");

        table.AddRow("Success",
            result1.Success ? "[green]yes[/]" : "[red]no[/]",
            result2.Success ? "[green]yes[/]" : "[red]no[/]");

        table.AddRow("Steps",
            $"{observer1.CurrentStep}",
            $"{observer2.CurrentStep}");

        table.AddRow("Tokens",
            $"{observer1.TotalTokens}",
            $"{observer2.TotalTokens}");

        table.AddRow("Thinking",
            $"{observer1.TotalThinkingTokens}",
            $"{observer2.TotalThinkingTokens}");

        table.AddRow("Time",
            $"{sw.Elapsed.TotalSeconds:F1}s",
            $"{sw.Elapsed.TotalSeconds:F1}s");

        table.AddRow("Files",
            $"{result1.FilesChanged.Count}",
            $"{result2.FilesChanged.Count}");

        console.Write(table);

        // Show output previews
        if (!string.IsNullOrEmpty(result1.Output))
        {
            var preview = result1.Output.Length > 200 ? result1.Output[..200] + "..." : result1.Output;
            console.Write(new Panel(Markup.Escape(preview))
                .Header($"[blue]{model1.ModelId}[/] output")
                .Border(BoxBorder.Rounded)
                .Expand());
        }

        if (!string.IsNullOrEmpty(result2.Output))
        {
            var preview = result2.Output.Length > 200 ? result2.Output[..200] + "..." : result2.Output;
            console.Write(new Panel(Markup.Escape(preview))
                .Header($"[blue]{model2.ModelId}[/] output")
                .Border(BoxBorder.Rounded)
                .Expand());
        }

        console.WriteLine();
        console.MarkupLine($"[dim]Arena completed in {sw.Elapsed.TotalSeconds:F1}s[/]");
        console.WriteLine();

        logger1.Dispose();
        logger2.Dispose();
        cts.Dispose();
    }
}
