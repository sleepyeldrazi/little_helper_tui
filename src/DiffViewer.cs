using System.Diagnostics;

namespace LittleHelperTui;

/// <summary>
/// Diff viewer utilities. Captures file content before agent modifies them.
/// </summary>
public static class DiffViewer
{
    /// <summary>Snapshot of file contents before agent modifies them.</summary>
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
}
