using System.Text.Json;
using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Manages session persistence. Reads JSONL logs from ~/.little_helper/logs/,
/// allows browsing and resuming past sessions.
/// </summary>
public static class SessionManager
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".little_helper", "logs");

    /// <summary>A parsed session summary from JSONL logs.</summary>
    public record SessionEntry(
        string FileName,
        DateTime StartTime,
        string Model,
        int TotalSteps,
        int TotalTokens,
        bool Success,
        double DurationSec,
        List<string> FilesChanged);

    /// <summary>List recent sessions, newest first.</summary>
    public static List<SessionEntry> ListSessions(int maxCount = 20)
    {
        if (!Directory.Exists(LogDir))
            return new();

        var sessions = new List<SessionEntry>();

        foreach (var file in Directory.GetFiles(LogDir, "*.jsonl")
            .OrderByDescending(f => File.GetLastWriteTime(f)).Take(maxCount))
        {
            var entry = ParseSession(file);
            if (entry != null)
                sessions.Add(entry);
        }

        return sessions;
    }

    /// <summary>Show session browser and let user pick one.</summary>
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
            .AddColumn("#")
            .AddColumn("Date")
            .AddColumn("Model")
            .AddColumn("Steps")
            .AddColumn("Tokens")
            .AddColumn("Result")
            .AddColumn("Time");

        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var icon = s.Success ? "[green]ok[/]" : "[red]fail[/]";
            table.AddRow(
                $"[dim]{i + 1}[/]",
                s.StartTime.ToString("MMM dd HH:mm"),
                Markup.Escape(s.Model),
                $"{s.TotalSteps}",
                FormatTokens(s.TotalTokens),
                icon,
                $"{s.DurationSec:F1}s");
        }

        console.Write(table);
        console.WriteLine();
    }

    /// <summary>Parse a JSONL session file into a summary.</summary>
    private static SessionEntry? ParseSession(string filePath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            if (lines.Length == 0) return null;

            DateTime startTime = DateTime.MinValue;
            string model = "";
            int totalSteps = 0;
            int totalTokens = 0;
            bool success = false;
            double durationSec = 0;
            var filesChanged = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                switch (type)
                {
                    case "session_start":
                        if (root.TryGetProperty("timestamp", out var ts))
                            DateTime.TryParse(ts.GetString(), out startTime);
                        if (root.TryGetProperty("model", out var m))
                            model = m.GetString() ?? "";
                        break;
                    case "session_end":
                        if (root.TryGetProperty("success", out var suc))
                            success = suc.GetBoolean();
                        if (root.TryGetProperty("steps", out var st))
                            totalSteps = st.GetInt32();
                        if (root.TryGetProperty("total_tokens", out var tok))
                            totalTokens = tok.GetInt32();
                        if (root.TryGetProperty("duration_sec", out var dur))
                            durationSec = double.TryParse(dur.GetString(), out var d) ? d : 0;
                        if (root.TryGetProperty("files_changed", out var fc)
                            && fc.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in fc.EnumerateArray())
                                filesChanged.Add(f.GetString() ?? "");
                        }
                        break;
                }
            }

            return new SessionEntry(
                Path.GetFileName(filePath), startTime, model,
                totalSteps, totalTokens, success, durationSec, filesChanged);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F1}K";
        return $"{tokens}";
    }
}
