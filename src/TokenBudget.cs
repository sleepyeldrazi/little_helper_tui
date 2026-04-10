using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Token budget visualization. Shows an ASCII bar chart of context window usage
/// broken down by category: system, user, assistant, tools, thinking.
/// </summary>
public static class TokenBudget
{
    /// <summary>
    /// Render a token budget breakdown for the current conversation.
    /// Uses Compaction.EstimateTokens per message for accurate counts.
    /// </summary>
    public static void Render(IAnsiConsole console, IReadOnlyList<ChatMessage> history,
        int maxContextTokens, int totalTokensUsed, int totalThinkingTokens)
    {
        // Categorize messages and estimate tokens
        int systemTokens = 0;
        int userTokens = 0;
        int assistantTokens = 0;
        int toolTokens = 0;

        foreach (var msg in history)
        {
            var estimated = Compaction.EstimateTokens(msg);
            switch (msg.Role)
            {
                case "system":
                    systemTokens += estimated;
                    break;
                case "user":
                    userTokens += estimated;
                    break;
                case "assistant":
                    assistantTokens += estimated;
                    break;
                case "tool":
                    toolTokens += estimated;
                    break;
            }
        }

        var used = systemTokens + userTokens + assistantTokens + toolTokens + totalThinkingTokens;
        var available = Math.Max(0, maxContextTokens - used);

        // Build bar chart
        var chart = new BarChart()
            .Width(60)
            .Label("[bold]Token Budget[/]")
            .AddItem("System", systemTokens, Color.Blue)
            .AddItem("User", userTokens, Color.Green)
            .AddItem("Assistant", assistantTokens, Color.Teal)
            .AddItem("Tools", toolTokens, Color.Yellow)
            .AddItem("Thinking", totalThinkingTokens, Color.Grey)
            .AddItem("Available", available, Color.Grey);

        console.Write(chart);

        // Summary line
        var pct = maxContextTokens > 0 ? (double)used / maxContextTokens * 100 : 0;
        var pctColor = pct > 80 ? "red" : pct > 60 ? "yellow" : "green";
        console.MarkupLine($"  Used [bold]{FormatTokens(used)}[/] / {FormatTokens(maxContextTokens)} [{pctColor}]({pct:F0}%)[/]  {FormatTokens(available)} left");
        console.WriteLine();
    }

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F1}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F1}K";
        return $"{tokens}";
    }
}
