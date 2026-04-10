using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Manages session persistence. Writes JSONL logs, reads them back
/// for browsing and resuming conversations.
/// </summary>
public class SessionManager
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".little_helper", "logs");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    // --- Types ---

    public record SessionSummary(
        string FileName,
        DateTime StartTime,
        DateTime? EndTime,
        string Model,
        int Steps,
        int Tokens,
        int ThinkingTokens,
        bool Success,
        double DurationSec,
        List<string> FilesChanged,
        string? FirstPrompt);

    private record JsonlEntry(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("timestamp")] string? Timestamp = null,
        [property: JsonPropertyName("model")] string? Model = null,
        [property: JsonPropertyName("success")] bool? Success = null,
        [property: JsonPropertyName("steps")] int? Steps = null,
        [property: JsonPropertyName("total_tokens")] int? TotalTokens = null,
        [property: JsonPropertyName("thinking_tokens")] int? ThinkingTokens = null,
        [property: JsonPropertyName("duration_sec")] double? DurationSec = null,
        [property: JsonPropertyName("files_changed")] List<string>? FilesChanged = null,
        [property: JsonPropertyName("content")] string? Content = null,
        [property: JsonPropertyName("role")] string? Role = null);

    // --- Public API ---

    /// <summary>List recent sessions, newest first.</summary>
    public static List<SessionSummary> ListSessions(int maxCount = 20)
    {
        if (!Directory.Exists(LogDir))
            return new();

        var sessions = new List<SessionSummary>();

        foreach (var file in Directory.GetFiles(LogDir, "*.jsonl")
            .OrderByDescending(f => File.GetLastWriteTime(f)).Take(maxCount))
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
        var filePath = Path.Combine(LogDir, session.FileName);

        // Parse full log for step details
        var steps = ParseSteps(filePath);

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

        // Show step-by-step transcript
        foreach (var step in steps)
        {
            if (step.Role == "user")
            {
                console.Write(new Panel(Markup.Escape(step.Content ?? ""))
                    .Header("[green]User[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Green)
                    .Expand());
            }
            else if (step.Role == "assistant")
            {
                var content = step.Content ?? "(tool calls)";
                if (content.Length > 300) content = content[..300] + "...";
                console.MarkupLine($"  [teal]Assistant:[/] {Markup.Escape(content)}");
            }
        }

        console.WriteLine();
    }

    // --- Parsing ---

    private static SessionSummary? ParseSession(string filePath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            if (lines.Length == 0) return null;

            DateTime startTime = DateTime.MinValue;
            DateTime? endTime = null;
            string model = "unknown";
            int totalSteps = 0;
            int totalTokens = 0;
            int thinkingTokens = 0;
            bool success = false;
            double durationSec = 0;
            var filesChanged = new List<string>();
            string? firstPrompt = null;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var entry = JsonSerializer.Deserialize<JsonlEntry>(line, JsonOpts);
                if (entry == null) continue;

                switch (entry.Type)
                {
                    case "session_start":
                        if (entry.Timestamp != null) DateTime.TryParse(entry.Timestamp, out startTime);
                        model = entry.Model ?? "unknown";
                        break;
                    case "session_end":
                        if (entry.Timestamp != null)
                        {
                            if (DateTime.TryParse(entry.Timestamp, out var et))
                                endTime = et;
                        }
                        success = entry.Success ?? false;
                        totalSteps = entry.Steps ?? 0;
                        totalTokens = entry.TotalTokens ?? 0;
                        thinkingTokens = entry.ThinkingTokens ?? 0;
                        durationSec = entry.DurationSec ?? 0;
                        if (entry.FilesChanged != null)
                            filesChanged = entry.FilesChanged;
                        break;
                    case "step":
                        if (firstPrompt == null && entry.Role == "user" && entry.Content != null)
                            firstPrompt = entry.Content;
                        break;
                }
            }

            return new SessionSummary(
                Path.GetFileName(filePath), startTime, endTime, model,
                totalSteps, totalTokens, thinkingTokens, success,
                durationSec, filesChanged, firstPrompt);
        }
        catch
        {
            return null;
        }
    }

    private static List<(string Role, string? Content)> ParseSteps(string filePath)
    {
        var steps = new List<(string Role, string? Content)>();

        try
        {
            foreach (var line in File.ReadAllLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var entry = JsonSerializer.Deserialize<JsonlEntry>(line, JsonOpts);
                if (entry?.Type == "step" && entry.Role != null)
                    steps.Add((entry.Role, entry.Content));
            }
        }
        catch { }

        return steps;
    }

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F1}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F1}K";
        return $"{tokens}";
    }
}
