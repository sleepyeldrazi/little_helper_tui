using System.ComponentModel;
using LittleHelper;
using LittleHelperTui.Views;
using Terminal.Gui;

namespace LittleHelperTui.Dialogs;

/// <summary>
/// Dialog showing token budget breakdown.
/// </summary>
public class TokensDialog : Dialog
{
    public TokensDialog(IReadOnlyList<ChatMessage> history, int maxContext, int totalTokens, int thinkingTokens)
    {
        Title = "Token Budget";
        Width = Dim.Percent(70);
        Height = Dim.Percent(80);
        ColorScheme = DarkColors.Dialog;

        // Calculate breakdown
        var systemTokens = history
            .Where(m => m.Role == "system")
            .Sum(m => EstimateTokens(m.Content));

        var userTokens = history
            .Where(m => m.Role == "user")
            .Sum(m => EstimateTokens(m.Content));

        var assistantTokens = history
            .Where(m => m.Role == "assistant")
            .Sum(m => EstimateTokens(m.Content));

        var toolTokens = history
            .Where(m => m.Role == "tool")
            .Sum(m => EstimateTokens(m.Content));

        // Summary
        var summaryLabel = new Label
        {
            X = 1,
            Y = 1,
            Text = $"Total: {totalTokens} / {FormatTokens(maxContext)} ({100 * totalTokens / maxContext}%)"
        };

        // Breakdown list
        var breakdown = new System.Collections.ObjectModel.ObservableCollection<string>
        {
            $"System:     {systemTokens,8} ({100 * systemTokens / Math.Max(1, maxContext)}%)",
            $"User:       {userTokens,8} ({100 * userTokens / Math.Max(1, maxContext)}%)",
            $"Assistant:  {assistantTokens,8} ({100 * assistantTokens / Math.Max(1, maxContext)}%)",
            $"Tools:      {toolTokens,8} ({100 * toolTokens / Math.Max(1, maxContext)}%)",
            $"Thinking:   {thinkingTokens,8} ({100 * thinkingTokens / Math.Max(1, maxContext)}%)",
            "",
            $"Available:  {maxContext - totalTokens,8} ({100 * (maxContext - totalTokens) / maxContext}%)",
            $"Messages:   {history.Count,8}"
        };

        var listView = new ListView
        {
            X = 1,
            Y = 3,
            Width = Dim.Fill(2),
            Height = Dim.Fill(4),
            Source = new ListWrapper<string>(breakdown)
        };

        // Progress bar (ASCII)
        var barWidth = 50;
        var filled = totalTokens * barWidth / maxContext;
        var bar = "[" + new string('█', filled) + new string('░', barWidth - filled) + "]";

        var barLabel = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(4),
            Text = bar
        };

        // Close button
        var closeButton = new Button { Title = "Close", IsDefault = true };
        closeButton.Accept += (s, e) =>
        {
            Application.RequestStop();
            if (e is HandledEventArgs he) he.Handled = true;
        };

        AddButton(closeButton);
        Add(summaryLabel, listView, barLabel);
    }

    private static int EstimateTokens(string? text)
    {
        // Rough approximation: ~4 chars per token
        return (text?.Length ?? 0) / 4;
    }

    private static string FormatTokens(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:F1}M"
        : tokens >= 1_000 ? $"{tokens / 1_000.0:F1}K"
        : $"{tokens}";
}
