using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Status bar rendering. Shows model name, step count, token usage, and agent state
/// as a horizontal rule at key points in the conversation.
/// </summary>
public static class StatusBar
{
    public static void Render(IAnsiConsole console, string modelName, TuiObserver observer)
    {
        var state = observer.CurrentState;
        var stateColor = state switch
        {
            AgentState.Done => "green",
            AgentState.ErrorRecovery => "red",
            _ => "yellow"
        };

        var tokens = FormatTokens(observer.TotalTokens);
        var thinking = FormatTokens(observer.TotalThinkingTokens);
        var step = observer.CurrentStep;

        var left = $"[bold]{modelName}[/]";
        var middle = $"[dim]Step {step}[/]";
        var right = $"[dim]{tokens} tokens[/] [dim]({thinking} thinking)[/] [{stateColor}]{state}[/]";

        console.MarkupLine($"{left}  {middle}  {right}");
        console.WriteLine();
    }

    public static void RenderDone(IAnsiConsole console, string modelName,
        AgentResult result, int steps, long elapsedMs, TuiObserver observer)
    {
        var icon = result.Success ? "[green]:check_mark:[/]" : "[red]:cross_mark:[/]";
        var context = FormatTokens(observer.TotalTokens);
        var thinking = FormatTokens(observer.TotalThinkingTokens);
        var elapsed = elapsedMs < 1000 ? $"{elapsedMs}ms" : $"{elapsedMs / 1000.0:F1}s";

        console.WriteLine();
        console.MarkupLine($"{icon} [bold]Done[/] [dim]{steps} steps, {elapsed}, {context} context ({thinking} thinking)[/]");

        if (result.FilesChanged.Count > 0)
        {
            console.MarkupLine($"[dim]  {result.FilesChanged.Count} files changed. Use :files to list.[/]");
        }

        console.WriteLine();
    }

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F1}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F1}K";
        return $"{tokens}";
    }
}
