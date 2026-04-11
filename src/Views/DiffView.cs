using System.Diagnostics;
using System.Text;
using Terminal.Gui;

namespace LittleHelperTui.Views;

/// <summary>
/// View that displays a unified diff for a file.
/// </summary>
public class DiffView : FrameView
{
    private readonly string _filePath;
    private readonly string? _oldContent;
    private readonly string? _newContent;

    public DiffView(string filePath)
    {
        _filePath = filePath;
        Title = $"Diff: {Path.GetFileName(filePath)}";
        Width = Dim.Fill();
        Height = Dim.Auto();

        // Load content
        try
        {
            if (File.Exists(filePath))
            {
                _newContent = File.ReadAllText(filePath);
            }

            _oldContent = DiffViewer.GetSnapshot(filePath);
        }
        catch { /* best effort */ }

        BuildContent();
    }

    private void BuildContent()
    {
        if (_newContent == null)
        {
            var errorLabel = new Label
            {
                Text = $"File not found: {_filePath}",
                Width = Dim.Fill()
            };
            Add(errorLabel);
            return;
        }

        if (_oldContent == null)
        {
            var newFileLabel = new Label
            {
                Text = $"New file ({_newContent.Length} bytes)",
                Width = Dim.Fill()
            };
            Add(newFileLabel);
            return;
        }

        // Compute diff
        var diff = ComputeDiff(_oldContent, _newContent);
        var added = diff.Count(d => d.Type == DiffType.Added);
        var removed = diff.Count(d => d.Type == DiffType.Removed);

        // Header
        var headerLabel = new Label
        {
            Text = $"+{added} -{removed}",
            Width = Dim.Fill(),
            Y = 0
        };
        Add(headerLabel);

        // Diff lines
        int y = 1;
        foreach (var line in diff.Take(50))
        {
            var text = line.Type switch
            {
                DiffType.Added => "+ " + line.Content,
                DiffType.Removed => "- " + line.Content,
                DiffType.Header => "  " + line.Content,
                _ => "  " + line.Content
            };

            var lineLabel = new Label
            {
                Text = Truncate(text, 100),
                Width = Dim.Fill(),
                Y = y++
            };
            Add(lineLabel);
        }

        if (diff.Count > 50)
        {
            var moreLabel = new Label
            {
                Text = $"... ({diff.Count - 50} more lines)",
                Width = Dim.Fill(),
                Y = y
            };
            Add(moreLabel);
        }
    }

    private List<DiffLine> ComputeDiff(string oldContent, string newContent)
    {
        var result = new List<DiffLine>();
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');

        // Simple diff: find common prefix and suffix
        int prefixLen = 0;
        while (prefixLen < oldLines.Length && prefixLen < newLines.Length
            && oldLines[prefixLen] == newLines[prefixLen])
            prefixLen++;

        int suffixLen = 0;
        while (suffixLen < oldLines.Length - prefixLen && suffixLen < newLines.Length - prefixLen
            && oldLines[oldLines.Length - 1 - suffixLen] == newLines[newLines.Length - 1 - suffixLen])
            suffixLen++;

        // Header
        result.Add(new DiffLine(DiffType.Header, $"--- a/{Path.GetFileName(_filePath)}"));
        result.Add(new DiffLine(DiffType.Header, $"+++ b/{Path.GetFileName(_filePath)}"));

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

    private static string Truncate(string s, int maxLen)
    {
        if (s.Length <= maxLen) return s;
        return s[..(maxLen - 3)] + "...";
    }

    private enum DiffType { Context, Added, Removed, Header }
    private record DiffLine(DiffType Type, string Content);
}
