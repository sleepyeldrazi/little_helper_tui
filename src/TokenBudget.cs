using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Token budget visualization. Reads conversation history from Agent
/// and renders a bar chart of context window usage by category.
/// </summary>
public static class TokenBudget
{
    /// <summary>
    /// Render a full token budget breakdown with bar chart + summary.
    /// </summary>
    public static void Render(IAnsiConsole console, IReadOnlyList<ChatMessage> history,
        int maxContextTokens, int totalTokensUsed, int totalThinkingTokens)
    {
        // Categorize and estimate tokens per message
        int systemTokens = 0;
        int userTokens = 0;
        int assistantTokens = 0;
        int toolTokens = 0;
        int messageCount = history.Count;

        foreach (var msg in history)
        {
            var estimated = Compaction.EstimateTokens(msg);
            switch (msg.Role)
            {
                case "system": systemTokens += estimated; break;
                case "user": userTokens += estimated; break;
                case "assistant": assistantTokens += estimated; break;
                case "tool": toolTokens += estimated; break;
            }
        }

        var used = systemTokens + userTokens + assistantTokens + toolTokens + totalThinkingTokens;
        var available = Math.Max(0, maxContextTokens - used);
        var pct = maxContextTokens > 0 ? (double)used / maxContextTokens * 100 : 0;

        // Color based on pressure
        var pressureColor = pct switch
        {
            > 90 => Color.Red,
            > 75 => Color.Orange1,
            > 50 => Color.Yellow,
            _ => Color.Green
        };

        // Bar chart
        var chart = new BarChart()
            .Width(50)
            .Label($"[bold]Token Budget[/] -- {FormatTokens(used)}/{FormatTokens(maxContextTokens)} [{pressureColor}]{pct:F0}%[/]")
            .AddItem("System", systemTokens, Color.Blue)
            .AddItem("User", userTokens, Color.Green)
            .AddItem("Assistant", assistantTokens, Color.Teal)
            .AddItem("Tools", toolTokens, Color.Yellow)
            .AddItem("Thinking", totalThinkingTokens, Color.Grey)
            .AddItem("Available", available, Color.Grey35);

        console.Write(chart);

        // Summary table
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("")
            .AddColumn("")
            .AddColumn("");

        table.AddRow("[bold]Category[/]", "[bold]Tokens[/]", "[bold]% of Context[/]");
        table.AddRow("System", $"{systemTokens}", Pct(systemTokens, maxContextTokens));
        table.AddRow("User", $"{userTokens}", Pct(userTokens, maxContextTokens));
        table.AddRow("Assistant", $"{assistantTokens}", Pct(assistantTokens, maxContextTokens));
        table.AddRow("Tools", $"{toolTokens}", Pct(toolTokens, maxContextTokens));
        table.AddRow("Thinking", $"{totalThinkingTokens}", Pct(totalThinkingTokens, maxContextTokens));
        table.AddRow("[bold]Total Used[/]", $"[bold]{FormatTokens(used)}[/]", $"[bold]{pct:F1}%[/]");
        table.AddRow("[dim]Available[/]", $"[dim]{FormatTokens(available)}[/]", $"[dim]{100 - pct:F1}%[/]");

        console.Write(table);

        // Messages count
        console.MarkupLine($"[dim]  {messageCount} messages in context[/]");
        console.WriteLine();
    }

    /// <summary>
    /// Render a compact single-line token budget for status display.
    /// </summary>
    public static string RenderCompact(IReadOnlyList<ChatMessage> history,
        int maxContextTokens, int totalTokensUsed, int totalThinkingTokens)
    {
        var used = totalTokensUsed + totalThinkingTokens;
        var pct = maxContextTokens > 0 ? (double)used / maxContextTokens * 100 : 0;

        // Build a tiny bar
        var barWidth = 20;
        var filled = (int)Math.Round(pct / 100 * barWidth);
        var bar = new string('█', Math.Min(filled, barWidth))
                + new string('░', Math.Max(barWidth - filled, 0));

        return $"{bar} {FormatTokens(used)}/{FormatTokens(maxContextTokens)} ({pct:F0}%)";
    }

    private static string Pct(int value, int total) =>
        total > 0 ? $"{(double)value / total * 100:F1}%" : "0%";

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F1}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F1}K";
        return $"{tokens}";
    }
}
