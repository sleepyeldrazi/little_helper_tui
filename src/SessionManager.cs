using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Manages session browsing. Uses core's SessionLogReader for parsing
/// so the TUI doesn't duplicate the log format.
/// </summary>
public static class SessionManager
{
    // --- Public API ---

    /// <summary>List recent sessions, newest first.</summary>
    public static List<SessionSummary> ListSessions(int maxCount = 20)
    {
        var files = SessionLogReader.ListLogFiles();
        var sessions = new List<SessionSummary>();

        foreach (var file in files.Take(maxCount))
        {
            var entry = ParseSession(file);
            if (entry != null)
                sessions.Add(entry);
        }

        return sessions;
    }

    /// <summary>Show session browser table.</summary>
    public static void BrowseSessions(IAnsiConsole console)
    {
        var sessions = ListSessions();
        if (sessions.Count == 0)
        {
            console.MarkupLine("[dim]No sessions found in ~/.little_helper/logs/[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Recent Sessions[/]")
            .AddColumn("#")
            .AddColumn("Date")
            .AddColumn("Model")
            .AddColumn("Steps")
            .AddColumn("Tokens")
            .AddColumn("Time")
            .AddColumn("Preview");

        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var preview = s.FirstPrompt ?? "";
            if (preview.Length > 40) preview = preview[..40] + "...";

            table.AddRow(
                $"[dim]{i + 1}[/]",
                s.StartTime.ToString("MMM dd HH:mm"),
                Markup.Escape(s.Model),
                $"{s.Steps}",
                FormatTokens(s.Tokens),
                $"{s.DurationSec:F1}s",
                Markup.Escape(preview));
        }

        console.Write(table);
        console.WriteLine();
    }

    /// <summary>Show detailed view of a specific session.</summary>
    public static void ShowSession(IAnsiConsole console, int index)
    {
        var sessions = ListSessions();
        if (index < 1 || index > sessions.Count)
        {
            console.MarkupLine("[red]Invalid session number.[/]");
            return;
        }

        var session = sessions[index - 1];

        // Header
        var icon = session.Success ? "[green]:check_mark:[/]" : "[red]:cross_mark:[/]";
        console.MarkupLine($"{icon} [bold]{session.Model}[/] -- {session.StartTime:yyyy-MM-dd HH:mm}");
        console.MarkupLine($"  Steps: {session.Steps}  Tokens: {FormatTokens(session.Tokens)}  Thinking: {FormatTokens(session.ThinkingTokens)}  Time: {session.DurationSec:F1}s");

        if (session.FilesChanged.Count > 0)
        {
            console.MarkupLine("  Files changed:");
            foreach (var f in session.FilesChanged)
                console.MarkupLine($"    [blue]{Markup.Escape(f)}[/]");
        }

        console.WriteLine();

        // Show step-by-step transcript using core's SessionEntry
        var entries = SessionLogReader.ReadEntries(session.FilePath);
        foreach (var entry in entries)
        {
            if (entry.Type == "step" && entry.Preview != null)
            {
                var role = entry.ToolCalls > 0 ? "assistant" : "assistant";
                var content = entry.Preview;
                if (content.Length > 300) content = content[..300] + "...";
                console.MarkupLine($"  [teal]{role}:[/] {Markup.Escape(content)}");
            }
            else if (entry.Type == "tool" && entry.Tool != null)
            {
                var resultColor = entry.IsError == true ? "red" : "green";
                var icon2 = entry.IsError == true ? "x" : "+";
                console.MarkupLine($"  [{resultColor}]{icon2} {entry.Tool}[/] [dim]{Markup.Escape(entry.Args ?? "")}[/]");
            }
        }

        console.WriteLine();
    }

    // --- Parsing (uses core's SessionEntry type) ---

    public record SessionSummary(
        string FilePath,
        DateTime StartTime,
        string Model,
        int Steps,
        int Tokens,
        int ThinkingTokens,
        bool Success,
        double DurationSec,
        List<string> FilesChanged,
        string? FirstPrompt);

    private static SessionSummary? ParseSession(string filePath)
    {
        try
        {
            var entries = SessionLogReader.ReadEntries(filePath);
            if (entries.Count == 0) return null;

            DateTime startTime = DateTime.MinValue;
            string model = "unknown";
            int totalSteps = 0;
            int totalTokens = 0;
            int thinkingTokens = 0;
            bool success = false;
            double durationSec = 0;
            var filesChanged = new List<string>();
            string? firstPrompt = null;

            foreach (var entry in entries)
            {
                switch (entry.Type)
                {
                    case "session_start":
                        if (entry.Timestamp != null) DateTime.TryParse(entry.Timestamp, out startTime);
                        model = entry.Model ?? "unknown";
                        break;
                    case "session_end":
                        success = entry.Success ?? false;
                        totalSteps = entry.Steps ?? 0;
                        totalTokens = entry.TotalTokens ?? 0;
                        thinkingTokens = entry.ThinkingTokens ?? 0;
                        if (double.TryParse(entry.DurationSec, out var ds))
                            durationSec = ds;
                        if (entry.FilesChanged != null)
                            filesChanged = entry.FilesChanged;
                        break;
                    case "step":
                        if (firstPrompt == null && entry.Preview != null)
                            firstPrompt = entry.Preview;
                        break;
                }
            }

            return new SessionSummary(
                filePath, startTime, model,
                totalSteps, totalTokens, thinkingTokens, success,
                durationSec, filesChanged, firstPrompt);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F1}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F1}K";
        return $"{tokens}";
    }
}