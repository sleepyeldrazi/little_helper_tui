using System.Text;
using Spectre.Console;
using System.Runtime.InteropServices;

namespace LittleHelperTui;

/// <summary>Possible scroll actions from input.</summary>
public enum ScrollAction { None, Up, Down }

/// <summary>Terminal raw mode handling for Unix systems.</summary>
internal static class TerminalRawMode
{
    private static bool _isRaw = false;

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, out termios termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optional_actions, ref termios termios);

    [StructLayout(LayoutKind.Sequential)]
    private struct termios
    {
        public uint c_iflag;
        public uint c_oflag;
        public uint c_cflag;
        public uint c_lflag;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] c_cc;
        public uint c_ispeed;
        public uint c_ospeed;
    }

    private static termios? _originalState;
    private const int TCSANOW = 0;
    private const int STDIN_FILENO = 0;

    // Local modes (c_lflag)
    private const uint ICANON = 2;    // Canonical mode
    private const uint ECHO = 8;      // Echo input
    private const uint ISIG = 1;      // Signal chars
    private const uint IEXTEN = 32768; // Extended input processing

    // Input modes (c_iflag)
    private const uint IXON = 1024;   // Enable XON/XOFF flow control (output)
    private const uint IXOFF = 4096;  // Enable XON/XOFF flow control (input)
    private const uint ICRNL = 256;   // Map CR to NL
    private const uint INLCR = 64;    // Map NL to CR
    private const uint IGNCR = 128;   // Ignore CR

    // Output modes (c_oflag)
    private const uint OPOST = 1;     // Post-process output

    public static void EnableRawMode()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return; // Windows doesn't need this

        if (_isRaw) return;

        try
        {
            if (tcgetattr(STDIN_FILENO, out var state) != 0)
                return;

            _originalState = state;

            // Disable canonical mode, echo, and signal chars
            // Also disable input processing that might interpret escape sequences
            state.c_lflag &= ~(ICANON | ECHO | ISIG | IEXTEN);
            state.c_iflag &= ~(IXON | IXOFF | ICRNL | INLCR | IGNCR);
            state.c_oflag &= ~(OPOST);

            // Set minimum read to 1 byte, no timeout
            state.c_cc[6] = 1;  // VMIN
            state.c_cc[5] = 0;  // VTIME

            if (tcsetattr(STDIN_FILENO, TCSANOW, ref state) == 0)
                _isRaw = true;
        }
        catch { /* Best effort */ }
    }

    public static void DisableRawMode()
    {
        if (!_isRaw || !_originalState.HasValue)
            return;

        try
        {
            var state = _originalState.Value;
            tcsetattr(STDIN_FILENO, TCSANOW, ref state);
            _isRaw = false;
        }
        catch { /* Best effort */ }
    }
}

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

    /// <summary>Number of terminal lines the last input occupied (for clearing before panel render).</summary>
    public static int LastRenderedLineCount { get; private set; } = 1;

    // Scroll event handling - shared state for TryReadScrollAction
    private static readonly object _scrollLock = new();
    private static ScrollAction _pendingScroll = ScrollAction.None;

    /// <summary>Check if there's a pending scroll action without blocking.</summary>
    public static ScrollAction TryReadScrollAction()
    {
        lock (_scrollLock)
        {
            var action = _pendingScroll;
            _pendingScroll = ScrollAction.None;
            return action;
        }
    }

    /// <summary>Queue a scroll action from mouse/key handler.</summary>
    private static void QueueScroll(ScrollAction action)
    {
        lock (_scrollLock)
        {
            _pendingScroll = action;
        }
    }

    /// <summary>Read a line with full editing support. Returns null on Ctrl+C / Ctrl+D.</summary>
    public static string? ReadLine(IAnsiConsole console, string prompt = ">")
    {
        var buf = new StringBuilder();
        int cursor = 0; // position within buf
        int prevRenderedLines = 1; // track how many lines we're occupying
        bool inBracketPaste = false; // track \x1b[200~ ... \x1b[201~ paste mode

        Console.Write($"\u001b[1m{prompt}\u001b[0m ");
        var promptLen = prompt.Length + 1; // visible width of "> "

        // Enable raw mode on Unix to get escape sequences
        TerminalRawMode.EnableRawMode();
        try
        {
            while (true)
            {
                var key = ReadKeyRaw();

                // Check for scroll events first (processed separately)
                if (key.Scroll != ScrollAction.None)
                {
                    QueueScroll(key.Scroll);
                    continue;
                }

                // Process regular key
                if (!key.HasValue)
                    continue;

                var k = key.Key;

                // Handle the key
                var result = ProcessKey(k, buf, ref cursor, ref prevRenderedLines, ref inBracketPaste, prompt, promptLen);
                if (result == ReadLineResult.Submit)
                {
                    var line = buf.ToString();
                    if (!string.IsNullOrEmpty(line))
                    {
                        if (History.Count == 0 || History[^1] != line)
                            History.Add(line);
                    }
                    _historyIndex = -1;
                    LastRenderedLineCount = prevRenderedLines;
                    return line;
                }
                if (result == ReadLineResult.Cancel)
                {
                    _historyIndex = -1;
                    return null;
                }
            }
        }
        finally
        {
            TerminalRawMode.DisableRawMode();
        }
    }

    private enum ReadLineResult { Continue, Submit, Cancel }

    /// <summary>Raw key with optional scroll action.</summary>
    private readonly struct RawKey
    {
        public ConsoleKeyInfo Key { get; }
        public ScrollAction Scroll { get; }
        public bool HasValue { get; }

        public RawKey(ConsoleKeyInfo key) { Key = key; Scroll = ScrollAction.None; HasValue = true; }
        public RawKey(ScrollAction scroll) { Key = default; Scroll = scroll; HasValue = false; }
        public static RawKey None => new();
    }

    /// <summary>Read a key with raw escape sequence parsing.</summary>
    private static RawKey ReadKeyRaw()
    {
        int b = Console.Read();
        if (b == -1) return RawKey.None;

        // ESC (27) - could be escape sequence
        if (b == 27)
        {
            // Read the rest of the sequence with timeout
            var seq = new List<int> { 27 };
            var timeout = DateTime.Now.AddMilliseconds(50);

            // Read as much as available up to the timeout
            while (DateTime.Now < timeout && seq.Count < 30)
            {
                // Try to read non-blocking - in raw mode we can do this
                if (Console.KeyAvailable)
                {
                    int next = Console.Read();
                    if (next == -1) break;
                    seq.Add(next);
                }
                else
                {
                    System.Threading.Thread.Sleep(1);
                }
            }

            // Parse the sequence
            if (seq.Count >= 2 && seq[1] == '[')
            {
                // CSI sequence - rebuild string for parsing
                var seqBytes = seq.Skip(2).ToList();
                if (seqBytes.Count == 0) return RawKey.None;

                // Mouse SGR: <button;row;colM or m
                // Format: ESC [ < btn ; row ; col M
                if (seqBytes[0] == '<')
                {
                    // Find the terminating M or m
                    int termIdx = -1;
                    for (int i = 0; i < seqBytes.Count; i++)
                    {
                        if (seqBytes[i] == 'M' || seqBytes[i] == 'm')
                        {
                            termIdx = i;
                            break;
                        }
                    }

                    if (termIdx > 0)
                    {
                        var content = string.Join("", seqBytes.Skip(1).Take(termIdx - 1).Select(c => (char)c));
                        var parts = content.Split(';');
                        if (parts.Length >= 1 && int.TryParse(parts[0], out var btn))
                        {
                            if (btn == 64) return new RawKey(ScrollAction.Up);   // Wheel up
                            if (btn == 65) return new RawKey(ScrollAction.Down); // Wheel down
                        }
                    }
                    return RawKey.None;
                }

                // For other sequences, build string representation
                var seqStr = string.Join("", seqBytes.Select(c => (char)c));

                // Bracket paste
                if (seqStr == "200~") return new RawKey(new ConsoleKeyInfo('\x16', ConsoleKey.P, false, false, false));
                if (seqStr == "201~") return new RawKey(new ConsoleKeyInfo('\x17', ConsoleKey.Q, false, false, false));

                // Alt+arrows: 1;3A, 1;3B, etc
                if (seqStr == "1;3A" || seqStr == "1;9A" || seqStr == ";3A")
                    return new RawKey(ScrollAction.Up);
                if (seqStr == "1;3B" || seqStr == "1;9B" || seqStr == ";3B")
                    return new RawKey(ScrollAction.Down);

                // Regular arrows
                if (seqStr.EndsWith("A")) // Up
                    return new RawKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false));
                if (seqStr.EndsWith("B")) // Down
                    return new RawKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false));
                if (seqStr.EndsWith("C")) // Right
                    return new RawKey(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false));
                if (seqStr.EndsWith("D")) // Left
                    return new RawKey(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false));
                if (seqStr.EndsWith("H")) // Home
                    return new RawKey(new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false));
                if (seqStr.EndsWith("F")) // End
                    return new RawKey(new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false));

                // 3~ = Delete
                if (seqStr == "3~")
                    return new RawKey(new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false));
            }

            // Unknown escape sequence - ignore
            return RawKey.None;
        }

        // Regular character
        char c = (char)b;
        ConsoleKey keyCode = MapCharToKey(c);
        return new RawKey(new ConsoleKeyInfo(c, keyCode, false, false, false));
    }

    private static ConsoleKey MapCharToKey(char c)
    {
        if (c >= 'a' && c <= 'z') return (ConsoleKey)(c - 'a' + (int)ConsoleKey.A);
        if (c >= 'A' && c <= 'Z') return (ConsoleKey)(c - 'A' + (int)ConsoleKey.A);
        if (c >= '0' && c <= '9') return (ConsoleKey)(c - '0' + (int)ConsoleKey.D0);
        if (c == ' ') return ConsoleKey.Spacebar;
        if (c == '\t') return ConsoleKey.Tab;
        if (c == '\r' || c == '\n') return ConsoleKey.Enter;
        if (c == 127) return ConsoleKey.Backspace;
        if (c == 3) return ConsoleKey.C; // Ctrl+C
        if (c == 4) return ConsoleKey.D; // Ctrl+D
        if (c == 21) return ConsoleKey.U; // Ctrl+U
        if (c == 11) return ConsoleKey.K; // Ctrl+K
        if (c == 1) return ConsoleKey.A; // Ctrl+A
        if (c == 5) return ConsoleKey.E; // Ctrl+E
        return ConsoleKey.None;
    }

    private static ReadLineResult ProcessKey(ConsoleKeyInfo key, StringBuilder buf, ref int cursor, ref int prevRenderedLines, ref bool inBracketPaste, string prompt, int promptLen)
    {
        // Check for bracket paste markers
        if (key.KeyChar == '\x16') { inBracketPaste = true; return ReadLineResult.Continue; }
        if (key.KeyChar == '\x17') { inBracketPaste = false; return ReadLineResult.Continue; }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                if (inBracketPaste)
                {
                    buf.Insert(cursor, '\n');
                    cursor++;
                    return ReadLineResult.Continue;
                }
                Console.WriteLine();
                return ReadLineResult.Submit;

            case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
            case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                Console.WriteLine();
                return ReadLineResult.Cancel;

            case ConsoleKey.LeftArrow:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    cursor = JumpWordBack(buf, cursor);
                else if (cursor > 0)
                    cursor--;
                break;

            case ConsoleKey.RightArrow:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    cursor = JumpWordForward(buf, cursor);
                else if (cursor < buf.Length)
                    cursor++;
                break;

            case ConsoleKey.Home:
            case ConsoleKey.A when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                cursor = 0;
                break;

            case ConsoleKey.End:
            case ConsoleKey.E when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                cursor = buf.Length;
                break;

            case ConsoleKey.Backspace:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
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

            case ConsoleKey.Delete:
                if (cursor < buf.Length)
                    buf.Remove(cursor, 1);
                break;

            case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                buf.Remove(0, cursor);
                cursor = 0;
                break;

            case ConsoleKey.K when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                buf.Remove(cursor, buf.Length - cursor);
                break;

            case ConsoleKey.Tab:
                // Tab completion
                var (completed, options) = TabComplete(buf.ToString(), cursor);
                if (options != null && options.Count > 1)
                {
                    // Clear current input lines before showing options
                    if (prevRenderedLines > 1)
                        Console.Write($"\u001b[{prevRenderedLines - 1}A");
                    Console.Write("\r\u001b[J");

                    Console.WriteLine();
                    var display = string.Join("  ", options.Take(20).Select(Path.GetFileName));
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(display)}[/]");
                    if (options.Count > 20)
                        AnsiConsole.MarkupLine($"[dim]... and {options.Count - 20} more[/]");
                    Console.Write($"\u001b[1m{prompt}\u001b[0m ");
                    prevRenderedLines = 1;
                }
                else if (completed != null)
                {
                    buf.Clear();
                    buf.Append(completed);
                    cursor = buf.Length;
                }
                break;

            case ConsoleKey.UpArrow:
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

            case ConsoleKey.DownArrow:
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

            default:
                if (!char.IsControl(key.KeyChar))
                {
                    buf.Insert(cursor, key.KeyChar);
                    cursor++;
                }
                break;
        }

        // Re-render the line
        RedrawLine(buf.ToString(), cursor, promptLen, ref prevRenderedLines);
        return ReadLineResult.Continue;
    }

    /// <summary>Clear and redraw the input line.</summary>
    private static void RedrawLine(string text, int cursor, int promptLen, ref int prevRenderedLines)
    {
        var totalWidth = Console.WindowWidth;
        if (totalWidth <= 0) totalWidth = 80;
        var totalCells = promptLen + text.Length;

        // Move to start of input area (first line of our text)
        if (prevRenderedLines > 1)
            Console.Write($"\u001b[{prevRenderedLines - 1}A");

        // Clear from cursor to end of screen (handles all lines)
        Console.Write("\u001b[2K"); // clear current line
        Console.Write("\r");
        Console.Write("\u001b[J");  // clear everything below

        // Write prompt + text
        Console.Write(new string(' ', promptLen));
        Console.Write(text);

        // Track new line count
        var newRenderedLines = (totalCells / totalWidth) + 1;
        if (totalCells > 0 && totalCells % totalWidth == 0) newRenderedLines++;
        prevRenderedLines = newRenderedLines;

        // Position cursor: we're at end of text, move to cursor position
        var cursorPos = promptLen + cursor;
        var endPos = totalCells;
        var cursorLine = cursorPos / totalWidth;
        var endLine = endPos / totalWidth;
        var cursorCol = cursorPos % totalWidth;
        var endCol = endPos % totalWidth;

        var lineDiff = endLine - cursorLine;
        var colDiff = endCol - cursorCol;

        if (lineDiff > 0) Console.Write($"\u001b[{lineDiff}A");
        if (colDiff > 0) Console.Write($"\u001b[{colDiff}D");
        else if (colDiff < 0) Console.Write($"\u001b[{-colDiff}C");

        Console.Out.Flush();
    }

    /// <summary>Clear the entire input line.</summary>
    private static void ClearLine(StringBuilder buf, int cursor, int promptLen, int prevRenderedLines)
    {
        var totalWidth = Console.WindowWidth;
        if (totalWidth <= 0) totalWidth = 80;

        if (prevRenderedLines > 1)
            Console.Write($"\u001b[{prevRenderedLines - 1}A");

        Console.Write("\u001b[2K\r\u001b[J");
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
        if (Directory.Exists(expanded) && !fragment.EndsWith('/'))
        {
            // Exact directory match without trailing slash: just add "/"
            var completed = text[..wordStart] + fragment + "/" + afterCursor;
            return (completed, null);
        }
        else if (Directory.Exists(expanded))
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
