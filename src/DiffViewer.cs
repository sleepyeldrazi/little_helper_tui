using LittleHelperTui.Views;

namespace LittleHelperTui;

/// <summary>
/// Diff viewer utilities. Captures file content before agent modifies them.
/// </summary>
public static class DiffViewer
{
    private static readonly Dictionary<string, string> _snapshots = new();
    private static readonly Lock _lock = new();
    private static readonly string WorkingDir = Directory.GetCurrentDirectory();

    /// <summary>Snapshot a file's content before it gets overwritten.</summary>
    public static void Snapshot(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var content = File.ReadAllText(filePath);
                lock (_lock)
                {
                    _snapshots[filePath] = content;
                }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>Get a snapshot for a file path.</summary>
    public static string? GetSnapshot(string filePath)
    {
        lock (_lock)
        {
            _snapshots.TryGetValue(filePath, out var content);
            return content;
        }
    }

    /// <summary>Clear all snapshots.</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _snapshots.Clear();
        }
    }

    /// <summary>Show the last diff for a file, rendering into MainWindow as text.</summary>
    public static void ShowLastDiff(MainWindow window, string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                window.AddColoredBlock($"File not found: {filePath}");
                return;
            }

            var currentContent = File.ReadAllText(filePath);
            var oldContent = GetSnapshot(filePath) ?? "";
            var diff = ComputeDiff(oldContent, currentContent, filePath);

            var added = diff.Count(d => d.Type == DiffType.Added);
            var removed = diff.Count(d => d.Type == DiffType.Removed);

            window.AddColoredBlock($"Diff: {Path.GetRelativePath(WorkingDir, filePath)} (+{added} -{removed})");

            foreach (var line in diff.Take(100))
            {
                switch (line.Type)
                {
                    case DiffType.Added:
                        window.AddColoredBlock($"  + {line.Content}");
                        break;
                    case DiffType.Removed:
                        window.AddColoredBlock($"  - {line.Content}");
                        break;
                    case DiffType.Context:
                        window.AddColoredBlock($"    {line.Content}");
                        break;
                    case DiffType.Header:
                        window.AddColoredBlock($"  {line.Content}");
                        break;
                }
            }

            if (diff.Count > 100)
                window.AddColoredBlock($"  ... ({diff.Count - 100} more lines)");

            window.AddColoredBlock("");
        }
        catch (Exception ex)
        {
            window.AddColoredBlock($"Error: {ex.Message}");
        }
    }

    private static List<DiffLine> ComputeDiff(string oldContent, string newContent, string filePath)
    {
        var result = new List<DiffLine>();
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');

        int prefixLen = 0;
        while (prefixLen < oldLines.Length && prefixLen < newLines.Length
            && oldLines[prefixLen] == newLines[prefixLen])
            prefixLen++;

        int suffixLen = 0;
        while (suffixLen < oldLines.Length - prefixLen && suffixLen < newLines.Length - prefixLen
            && oldLines[oldLines.Length - 1 - suffixLen] == newLines[newLines.Length - 1 - suffixLen])
            suffixLen++;

        result.Add(new DiffLine(DiffType.Header, $"--- a/{Path.GetRelativePath(WorkingDir, filePath)}"));
        result.Add(new DiffLine(DiffType.Header, $"+++ b/{Path.GetRelativePath(WorkingDir, filePath)}"));

        int contextLines = 3;
        int startContext = Math.Max(0, prefixLen - contextLines);
        for (int i = startContext; i < prefixLen; i++)
            result.Add(new DiffLine(DiffType.Context, oldLines[i]));

        for (int i = prefixLen; i < oldLines.Length - suffixLen; i++)
            result.Add(new DiffLine(DiffType.Removed, oldLines[i]));

        for (int i = prefixLen; i < newLines.Length - suffixLen; i++)
            result.Add(new DiffLine(DiffType.Added, newLines[i]));

        int endContext = Math.Min(suffixLen, contextLines);
        for (int i = 0; i < endContext; i++)
            result.Add(new DiffLine(DiffType.Context, newLines[newLines.Length - suffixLen + i]));

        return result;
    }

    private enum DiffType { Context, Added, Removed, Header }
    private record DiffLine(DiffType Type, string Content);
}
