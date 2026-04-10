using System.Text;
using Spectre.Console;

namespace LittleHelperTui;

/// <summary>
/// Custom readline input with cursor movement, editing keys,
/// and tab-completion for file paths.
/// </summary>
public static class InputHandler
{
    // History
    private static readonly List<string> History = new();
    private static int _historyIndex = -1;
    private static string _savedDraft = "";

    /// <summary>Read a line with full editing support. Returns null on Ctrl+C / Ctrl+D.</summary>
    public static string? ReadLine(IAnsiConsole console, string prompt = ">")
    {
        var buf = new StringBuilder();
        int cursor = 0; // position within buf

        console.Markup($"\u001b[1m{prompt}\u001b[0m ");
        var promptLen = prompt.Length + 1; // visible width of "> "

        while (true)
        {
            var key = Console.ReadKey(true);

            switch (key)
            {
                // --- Submit / Cancel ---
                case { Key: ConsoleKey.Enter }:
                    Console.WriteLine();
                    var line = buf.ToString();
                    if (!string.IsNullOrEmpty(line))
                    {
                        // Dedup history
                        if (History.Count == 0 || History[^1] != line)
                            History.Add(line);
                    }
                    _historyIndex = -1;
                    return line;

                case { Key: ConsoleKey.Escape }:
                    ClearLine(buf, cursor, promptLen);
                    buf.Clear();
                    cursor = 0;
                    break;

                case { Key: ConsoleKey.C, Modifiers: ConsoleModifiers.Control }:
                case { Key: ConsoleKey.D, Modifiers: ConsoleModifiers.Control }:
                    Console.WriteLine();
                    _historyIndex = -1;
                    return null;

                // --- Cursor movement ---
                case { Key: ConsoleKey.LeftArrow }:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        // Ctrl+Left: jump back one word
                        cursor = JumpWordBack(buf, cursor);
                    else if (cursor > 0)
                        cursor--;
                    break;

                case { Key: ConsoleKey.RightArrow }:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        // Ctrl+Right: jump forward one word
                        cursor = JumpWordForward(buf, cursor);
                    else if (cursor < buf.Length)
                        cursor++;
                    break;

                case { Key: ConsoleKey.Home }:
                case { Key: ConsoleKey.A, Modifiers: ConsoleModifiers.Control }:
                    cursor = 0;
                    break;

                case { Key: ConsoleKey.End }:
                case { Key: ConsoleKey.E, Modifiers: ConsoleModifiers.Control }:
                    cursor = buf.Length;
                    break;

                // --- Deletion ---
                case { Key: ConsoleKey.Backspace }:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        // Ctrl+W: delete word back
                        var newCursor = JumpWordBack(buf, cursor);
                        buf.Remove(newCursor, cursor - newCursor);
                        cursor = newCursor;
                    }
                    else if (cursor > 0)
                    {
                        buf.Remove(cursor - 1, 1);
                        cursor--;
                    }
                    break;

                case { Key: ConsoleKey.Delete }:
                    if (cursor < buf.Length)
                        buf.Remove(cursor, 1);
                    break;

                case { Key: ConsoleKey.U, Modifiers: ConsoleModifiers.Control }:
                    // Ctrl+U: clear line before cursor
                    buf.Remove(0, cursor);
                    cursor = 0;
                    break;

                case { Key: ConsoleKey.K, Modifiers: ConsoleModifiers.Control }:
                    // Ctrl+K: clear line after cursor
                    buf.Remove(cursor, buf.Length - cursor);
                    break;

                // --- Tab completion ---
                case { Key: ConsoleKey.Tab }:
                    var (completed, options) = TabComplete(buf.ToString(), cursor);
                    if (options != null && options.Count > 1)
                    {
                        // Multiple matches: show options, keep buffer
                        Console.WriteLine();
                        var display = string.Join("  ", options.Take(20).Select(Path.GetFileName));
                        console.MarkupLine($"[dim]{Markup.Escape(display)}[/]");
                        if (options.Count > 20)
                            console.MarkupLine($"[dim]... and {options.Count - 20} more[/]");
                        console.Markup($"\u001b[1m{prompt}\u001b[0m ");
                        promptLen = prompt.Length + 1;
                    }
                    else if (completed != null)
                    {
                        buf.Clear();
                        buf.Append(completed);
                        cursor = buf.Length;
                    }
                    break;

                // --- History ---
                case { Key: ConsoleKey.UpArrow }:
                    if (History.Count > 0)
                    {
                        if (_historyIndex == -1)
                        {
                            _savedDraft = buf.ToString();
                            _historyIndex = History.Count - 1;
                        }
                        else if (_historyIndex > 0)
                        {
                            _historyIndex--;
                        }
                        buf.Clear();
                        buf.Append(History[_historyIndex]);
                        cursor = buf.Length;
                    }
                    break;

                case { Key: ConsoleKey.DownArrow }:
                    if (_historyIndex >= 0)
                    {
                        if (_historyIndex < History.Count - 1)
                        {
                            _historyIndex++;
                            buf.Clear();
                            buf.Append(History[_historyIndex]);
                        }
                        else
                        {
                            _historyIndex = -1;
                            buf.Clear();
                            buf.Append(_savedDraft);
                        }
                        cursor = buf.Length;
                    }
                    break;

                // --- Typing ---
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buf.Insert(cursor, key.KeyChar);
                        cursor++;
                    }
                    break;
            }

            // Re-render the line
            RedrawLine(buf.ToString(), cursor, promptLen);
        }
    }

    /// <summary>Clear and redraw the input line.</summary>
    private static void RedrawLine(string text, int cursor, int promptLen)
    {
        // Move to start of input, clear to end of line, write text, position cursor
        Console.Write("\r");
        Console.Write(new string(' ', promptLen + text.Length + 1));
        Console.Write("\r");
        Console.Write(new string(' ', promptLen));
        Console.Write(text);
        // Position cursor
        Console.SetCursorPosition(promptLen + cursor, Console.CursorTop);
    }

    /// <summary>Clear the entire input line.</summary>
    private static void ClearLine(StringBuilder buf, int cursor, int promptLen)
    {
        Console.Write("\r");
        Console.Write(new string(' ', promptLen + buf.Length + 1));
        Console.Write("\r");
        Console.Write(new string(' ', promptLen));
    }

    /// <summary>Tab-complete a file path at cursor position.</summary>
    private static (string? completed, List<string>? options) TabComplete(string text, int cursor)
    {
        // Find the path-like token at or before cursor
        var beforeCursor = text[..cursor];
        var afterCursor = text[cursor..];

        // Find the start of the current "word" (delimited by spaces, quotes)
        int wordStart = beforeCursor.Length - 1;
        while (wordStart >= 0 && beforeCursor[wordStart] != ' ' && beforeCursor[wordStart] != '"' && beforeCursor[wordStart] != '\'')
            wordStart--;
        wordStart++;

        var fragment = beforeCursor[wordStart..];
        if (string.IsNullOrEmpty(fragment))
            return (null, null);

        // Expand ~ to home dir
        string expanded;
        bool hadTilde = false;
        if (fragment.StartsWith("~/"))
        {
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fragment[2..]);
            hadTilde = true;
        }
        else if (fragment == "~")
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            hadTilde = true;
        }
        else
        {
            expanded = Path.GetFullPath(fragment);
        }

        // Determine directory and prefix
        string dir, prefix;
        if (Directory.Exists(expanded))
        {
            dir = expanded;
            prefix = "";
        }
        else
        {
            dir = Path.GetDirectoryName(expanded) ?? ".";
            prefix = Path.GetFileName(expanded);
        }

        if (!Directory.Exists(dir))
            return (null, null);

        try
        {
            var matches = Directory.GetFileSystemEntries(dir, prefix + "*")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matches.Count == 0)
                return (null, null);

            if (matches.Count == 1)
            {
                // Single match: complete it
                var match = matches[0];
                var suffix = match[dir.Length..].TrimStart('/');
                if (Directory.Exists(match))
                    suffix += "/";

                // Reconstruct the path with the original prefix style
                var basePath = fragment[..^prefix.Length];
                if (hadTilde && basePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
                    basePath = "~" + basePath[Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Length..];

                var completed = text[..wordStart] + basePath + suffix + afterCursor;
                return (completed, null);
            }

            // Multiple matches: find common prefix and show options
            var relMatches = matches.Select(p =>
            {
                var name = p[dir.Length..].TrimStart('/');
                if (Directory.Exists(p)) name += "/";
                return name;
            }).ToList();

            // Find common prefix among matches
            var commonPrefix = relMatches[0];
            foreach (var m in relMatches.Skip(1))
            {
                var len = 0;
                while (len < commonPrefix.Length && len < m.Length
                    && char.ToLowerInvariant(commonPrefix[len]) == char.ToLowerInvariant(m[len]))
                    len++;
                commonPrefix = commonPrefix[..len];
            }

            if (commonPrefix.Length > prefix.Length)
            {
                var basePath = fragment[..^prefix.Length];
                if (hadTilde && basePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
                    basePath = "~" + basePath[Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Length..];
                var completed = text[..wordStart] + basePath + commonPrefix + afterCursor;
                return (completed, relMatches);
            }

            return (null, relMatches);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Jump cursor back one word.</summary>
    private static int JumpWordBack(StringBuilder buf, int cursor)
    {
        int pos = cursor - 1;
        // Skip spaces
        while (pos > 0 && char.IsWhiteSpace(buf[pos])) pos--;
        // Skip word chars
        while (pos > 0 && !char.IsWhiteSpace(buf[pos - 1])) pos--;
        return pos < 0 ? 0 : pos;
    }

    /// <summary>Jump cursor forward one word.</summary>
    private static int JumpWordForward(StringBuilder buf, int cursor)
    {
        int pos = cursor;
        // Skip spaces
        while (pos < buf.Length && char.IsWhiteSpace(buf[pos])) pos++;
        // Skip word chars
        while (pos < buf.Length && !char.IsWhiteSpace(buf[pos])) pos++;
        return pos;
    }
}
