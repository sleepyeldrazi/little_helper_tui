using System.Diagnostics;
using System.Text;
using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Diff viewer. Shows unified diffs for file writes by the agent.
/// Captures file content before writes when possible.
/// </summary>
public static class DiffViewer
{
    /// <summary>Snapshot of file contents before agent modifies them.</summary>
    private static readonly Dictionary<string, string> _snapshots = new();
    private static readonly string WorkingDir = Directory.GetCurrentDirectory();

    /// <summary>Snapshot a file's content before it gets overwritten.</summary>
    public static void Snapshot(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var content = File.ReadAllText(filePath);
                lock (_snapshots)
                {
                    _snapshots[filePath] = content;
                }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>Show diff for a file that was written by the agent.</summary>
    public static void ShowDiff(IAnsiConsole console, string filePath, string newContent)
    {
        string? oldContent;
        lock (_snapshots)
        {
            _snapshots.TryGetValue(filePath, out oldContent);
        }

        if (oldContent == null)
        {
            // No snapshot -- just show it's a new file
            console.MarkupLine($"[green]+ New file: {Markup.Escape(filePath)}[/] ({newContent.Length} bytes)");
            return;
        }

        // Compute unified diff
        var diff = ComputeDiff(oldContent, newContent, filePath);
        RenderDiff(console, diff, filePath);
    }

    /// <summary>Show the last diff for a file path.</summary>
    public static void ShowLastDiff(IAnsiConsole console, string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                console.MarkupLine($"[red]File not found: {Markup.Escape(filePath)}[/]");
                return;
            }

            var currentContent = File.ReadAllText(filePath);
            ShowDiff(console, filePath, currentContent);
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
        }
    }

    /// <summary>Render a diff with color-coded lines.</summary>
    private static void RenderDiff(IAnsiConsole console, List<DiffLine> diff, string filePath)
    {
        var added = diff.Count(d => d.Type == DiffType.Added);
        var removed = diff.Count(d => d.Type == DiffType.Removed);

        var header = $"[bold]Diff: {Markup.Escape(Path.GetRelativePath(WorkingDir, filePath))}[/] [dim](+{added} -{removed})[/]";
        console.MarkupLine(header);

        foreach (var line in diff.Take(100)) // Limit display
        {
            switch (line.Type)
            {
                case DiffType.Added:
                    console.MarkupLine($"[green]  + {Markup.Escape(line.Content)}[/]");
                    break;
                case DiffType.Removed:
                    console.MarkupLine($"[red]  - {Markup.Escape(line.Content)}[/]");
                    break;
                case DiffType.Context:
                    console.MarkupLine($"[dim]    {Markup.Escape(line.Content)}[/]");
                    break;
                case DiffType.Header:
                    console.MarkupLine($"[blue]  {Markup.Escape(line.Content)}[/]");
                    break;
            }
        }

        if (diff.Count > 100)
            console.MarkupLine($"[dim]  ... ({diff.Count - 100} more lines)[/]");

        console.WriteLine();
    }

    /// <summary>Simple line-based diff computation.</summary>
    private static List<DiffLine> ComputeDiff(string oldContent, string newContent, string filePath)
    {
        var result = new List<DiffLine>();
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');

        // Simple Myers-like diff: find common prefix and suffix, mark middle as changed
        int prefixLen = 0;
        while (prefixLen < oldLines.Length && prefixLen < newLines.Length
            && oldLines[prefixLen] == newLines[prefixLen])
            prefixLen++;

        int suffixLen = 0;
        while (suffixLen < oldLines.Length - prefixLen && suffixLen < newLines.Length - prefixLen
            && oldLines[oldLines.Length - 1 - suffixLen] == newLines[newLines.Length - 1 - suffixLen])
            suffixLen++;

        // Header
        result.Add(new DiffLine(DiffType.Header, $"--- a/{Path.GetRelativePath(WorkingDir, filePath)}"));
        result.Add(new DiffLine(DiffType.Header, $"+++ b/{Path.GetRelativePath(WorkingDir, filePath)}"));

        // Context before
        int contextLines = 3;
        int startContext = Math.Max(0, prefixLen - contextLines);
        for (int i = startContext; i < prefixLen; i++)
            result.Add(new DiffLine(DiffType.Context, oldLines[i]));

        // Removed lines
        for (int i = prefixLen; i < oldLines.Length - suffixLen; i++)
            result.Add(new DiffLine(DiffType.Removed, oldLines[i]));

        // Added lines
        for (int i = prefixLen; i < newLines.Length - suffixLen; i++)
            result.Add(new DiffLine(DiffType.Added, newLines[i]));

        // Context after
        int endContext = Math.Min(suffixLen, contextLines);
        for (int i = 0; i < endContext; i++)
            result.Add(new DiffLine(DiffType.Context, newLines[newLines.Length - suffixLen + i]));

        return result;
    }

    private enum DiffType { Context, Added, Removed, Header }
    private record DiffLine(DiffType Type, string Content);
}
