using System.Text;
using Spectre.Console;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace LittleHelperTui;

/// <summary>Possible scroll actions.</summary>
public enum ScrollAction { None, Up, Down }

/// <summary>Possible input events from the terminal.</summary>
public enum InputEventType { None, Key, ScrollUp, ScrollDown, MouseEvent }

/// <summary>An input event from the terminal.</summary>
public readonly struct InputEvent
{
    public InputEventType Type { get; }
    public ConsoleKeyInfo? Key { get; }

    public static InputEvent None => new(InputEventType.None, null);
    public static InputEvent ScrollUp => new(InputEventType.ScrollUp, null);
    public static InputEvent ScrollDown => new(InputEventType.ScrollDown, null);

    public InputEvent(InputEventType type, ConsoleKeyInfo? key)
    {
        Type = type;
        Key = key;
    }

    public InputEvent(ConsoleKeyInfo key)
    {
        Type = InputEventType.Key;
        Key = key;
    }
}

/// <summary>
/// Cross-platform terminal input handler with proper escape sequence parsing.
/// Uses platform-specific APIs (not Console.ReadKey) to read raw bytes.
/// </summary>
public static class InputHandler
{
    // History for command recall
    private static readonly List<string> History = new();
    private static int _historyIndex = -1;
    private static string _savedDraft = "";

    /// <summary>Number of terminal lines the last input occupied.</summary>
    public static int LastRenderedLineCount { get; private set; } = 1;

    // Event queue - input thread produces, main thread consumes
    private static readonly Channel<InputEvent> _eventChannel = Channel.CreateUnbounded<InputEvent>();

    // Track if input thread is running
    private static CancellationTokenSource? _inputCts;
    private static Task? _inputTask;

    /// <summary>Start the background input processing thread.</summary>
    public static void StartInputThread()
    {
        if (_inputTask != null) return;

        _inputCts = new CancellationTokenSource();
        _inputTask = Task.Run(() => InputLoop(_inputCts.Token));
    }

    /// <summary>Stop the input thread.</summary>
    public static void StopInputThread()
    {
        _inputCts?.Cancel();
        _inputTask?.Wait(TimeSpan.FromSeconds(1));
        _inputTask = null;
    }

    /// <summary>Try to get an input event without blocking.</summary>
    public static bool TryReadEvent(out InputEvent evt)
    {
        return _eventChannel.Reader.TryRead(out evt);
    }

    /// <summary>Background input loop - runs on dedicated thread.</summary>
    private static void InputLoop(CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            WindowsInputLoop(ct);
        else
            UnixInputLoop(ct);
    }

    #region Unix Implementation

    private const int STDIN_FILENO = 0;

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, out termios termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optional_actions, ref termios termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int select(int nfds, ref fd_set readfds, IntPtr writefds, IntPtr exceptfds, ref timeval timeout);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct fd_set
    {
        public int fd_count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public int[] fd_array;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct timeval
    {
        public int tv_sec;
        public int tv_usec;
    }

    // Termios flags
    private const uint ICANON = 2;
    private const uint ECHO = 8;
    private const uint ISIG = 1;
    private const uint IEXTEN = 32768;
    private const uint IXON = 1024;
    private const uint IXOFF = 4096;
    private const uint ICRNL = 256;
    private const int TCSANOW = 0;

    private static termios? _originalTermios;

    private static void UnixInputLoop(CancellationToken ct)
    {
        // Enable raw mode
        if (!EnableRawMode()) return;

        try
        {
            var parser = new EscapeSequenceParser();
            var buffer = new byte[256];

            while (!ct.IsCancellationRequested)
            {
                // Wait for input with timeout (allows checking cancellation)
                if (!WaitForInput(100)) continue;

                // Read available bytes
                int count = read(STDIN_FILENO, buffer, buffer.Length);
                if (count <= 0) continue;

                // Process each byte through the state machine
                for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                {
                    var evt = parser.ProcessByte(buffer[i]);
                    if (evt.Type != InputEventType.None)
                    {
                        _eventChannel.Writer.TryWrite(evt);
                    }
                }
            }
        }
        finally
        {
            DisableRawMode();
        }
    }

    private static bool EnableRawMode()
    {
        if (tcgetattr(STDIN_FILENO, out var term) != 0)
            return false;

        _originalTermios = term;

        term.c_lflag &= ~(ICANON | ECHO | ISIG | IEXTEN);
        term.c_iflag &= ~(IXON | IXOFF | ICRNL);
        term.c_cc[6] = 0; // VMIN - return immediately
        term.c_cc[5] = 0; // VTIME - no timeout

        return tcsetattr(STDIN_FILENO, TCSANOW, ref term) == 0;
    }

    private static void DisableRawMode()
    {
        if (_originalTermios.HasValue)
        {
            var term = _originalTermios.Value;
            tcsetattr(STDIN_FILENO, TCSANOW, ref term);
        }
    }

    private static bool WaitForInput(int timeoutMs)
    {
        var fds = new fd_set();
        fds.fd_count = 1;
        fds.fd_array = new int[64];
        fds.fd_array[0] = STDIN_FILENO;

        var tv = new timeval
        {
            tv_sec = timeoutMs / 1000,
            tv_usec = (timeoutMs % 1000) * 1000
        };

        return select(STDIN_FILENO + 1, ref fds, IntPtr.Zero, IntPtr.Zero, ref tv) > 0;
    }

    #endregion

    #region Windows Implementation

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadConsoleInput(IntPtr hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekConsoleInput(IntPtr hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushConsoleInputBuffer(IntPtr hConsoleHandle);

    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const ushort KEY_EVENT = 0x0001;
    private const ushort MOUSE_EVENT = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_RECORD
    {
        public ushort EventType;
        public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public bool bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    private static uint _originalConsoleMode;

    private static void WindowsInputLoop(CancellationToken ct)
    {
        var handle = GetStdHandle(STD_INPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;

        // Enable mouse input
        if (!GetConsoleMode(handle, out _originalConsoleMode)) return;
        SetConsoleMode(handle, _originalConsoleMode | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS);

        try
        {
            var records = new INPUT_RECORD[32];

            while (!ct.IsCancellationRequested)
            {
                // Check if events available
                if (!PeekConsoleInput(handle, records, 1, out var numEvents))
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (numEvents == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Read the events
                if (!ReadConsoleInput(handle, records, 32, out numEvents))
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Process events
                for (int i = 0; i < numEvents && !ct.IsCancellationRequested; i++)
                {
                    var evt = ProcessWindowsEvent(records[i]);
                    if (evt.Type != InputEventType.None)
                    {
                        _eventChannel.Writer.TryWrite(evt);
                    }
                }
            }
        }
        finally
        {
            SetConsoleMode(handle, _originalConsoleMode);
        }
    }

    private static InputEvent ProcessWindowsEvent(INPUT_RECORD record)
    {
        if (record.EventType == KEY_EVENT && record.KeyEvent.bKeyDown)
        {
            var ke = record.KeyEvent;
            var key = new ConsoleKeyInfo(
                ke.UnicodeChar,
                (ConsoleKey)ke.wVirtualKeyCode,
                (ke.dwControlKeyState & 0x0010) != 0, // Shift
                (ke.dwControlKeyState & 0x0002) != 0, // Alt
                (ke.dwControlKeyState & 0x0008) != 0  // Control
            );
            return new InputEvent(key);
        }

        // Windows mouse events could be processed here if needed

        return InputEvent.None;
    }

    #endregion

    #region Escape Sequence Parser (State Machine)

    /// <summary>
    /// State machine parser for ANSI escape sequences.
    /// Thread-safe - each input thread has its own instance.
    /// </summary>
    private class EscapeSequenceParser
    {
        private enum State { Normal, Escape, CSI, CSIParam, MouseSGR }

        private State _state = State.Normal;
        private readonly List<byte> _buffer = new();

        public InputEvent ProcessByte(byte b)
        {
            switch (_state)
            {
                case State.Normal:
                    if (b == 0x1B) // ESC
                    {
                        _state = State.Escape;
                        _buffer.Clear();
                        _buffer.Add(b);
                        return InputEvent.None;
                    }
                    else if (b >= 32 && b < 127) // Printable ASCII
                    {
                        return new InputEvent(new ConsoleKeyInfo((char)b, MapAsciiToKey((char)b), false, false, false));
                    }
                    else if (b == 0x0D) // CR
                    {
                        return new InputEvent(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
                    }
                    else if (b == 0x7F) // DEL -> Backspace
                    {
                        return new InputEvent(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false));
                    }
                    else if (b < 32) // Control characters
                    {
                        return ProcessControlChar(b);
                    }
                    return InputEvent.None;

                case State.Escape:
                    _buffer.Add(b);
                    if (b == (byte)'[')
                    {
                        _state = State.CSI;
                        return InputEvent.None;
                    }
                    else if (b == (byte)'O')
                    {
                        // SS3 sequences (F1-F4) - skip for now
                        _state = State.Normal;
                        return InputEvent.None;
                    }
                    else
                    {
                        // Alt+key sequence
                        _state = State.Normal;
                        if (b >= 32 && b < 127)
                        {
                            return new InputEvent(new ConsoleKeyInfo((char)b, MapAsciiToKey((char)b), false, true, false));
                        }
                        return InputEvent.None;
                    }

                case State.CSI:
                    _buffer.Add(b);
                    if (b == (byte)'<')
                    {
                        _state = State.MouseSGR;
                        return InputEvent.None;
                    }
                    else if (IsCsiIntermediate(b))
                    {
                        _state = State.CSIParam;
                        return InputEvent.None;
                    }
                    else if (IsCsiFinal(b))
                    {
                        var evt = ProcessCsiSequence(_buffer);
                        _state = State.Normal;
                        _buffer.Clear();
                        return evt;
                    }
                    return InputEvent.None;

                case State.CSIParam:
                    _buffer.Add(b);
                    if (IsCsiFinal(b))
                    {
                        var evt = ProcessCsiSequence(_buffer);
                        _state = State.Normal;
                        _buffer.Clear();
                        return evt;
                    }
                    return InputEvent.None;

                case State.MouseSGR:
                    _buffer.Add(b);
                    if (b == (byte)'M' || b == (byte)'m')
                    {
                        var evt = ProcessMouseSgr(_buffer);
                        _state = State.Normal;
                        _buffer.Clear();
                        return evt;
                    }
                    return InputEvent.None;

                default:
                    _state = State.Normal;
                    return InputEvent.None;
            }
        }

        private static bool IsCsiIntermediate(byte b) => b >= 0x20 && b <= 0x2F;
        private static bool IsCsiFinal(byte b) => (b >= 0x40 && b <= 0x7E) || b == 0x7E;

        private InputEvent ProcessCsiSequence(List<byte> seq)
        {
            // Convert to string for easier parsing (skip ESC [ )
            var content = Encoding.ASCII.GetString(seq.Skip(2).ToArray());

            // Arrow keys
            if (content == "A") return new InputEvent(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false));
            if (content == "B") return new InputEvent(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false));
            if (content == "C") return new InputEvent(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false));
            if (content == "D") return new InputEvent(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false));
            if (content == "H") return new InputEvent(new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false));
            if (content == "F") return new InputEvent(new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false));
            if (content == "3~") return new InputEvent(new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false));

            // Bracket paste
            if (content == "200~" || content == "201~") return InputEvent.None; // Ignore paste markers

            // Alt+arrows (1;3A, 1;3B, etc)
            if (content.EndsWith("A") && content.Contains(";3")) return InputEvent.ScrollUp;
            if (content.EndsWith("B") && content.Contains(";3")) return InputEvent.ScrollDown;

            return InputEvent.None;
        }

        private InputEvent ProcessMouseSgr(List<byte> seq)
        {
            // Format: ESC [ < btn ; row ; col M/m
            var content = Encoding.ASCII.GetString(seq.Skip(3).ToArray()); // Skip ESC [ <
            content = content.TrimEnd('M', 'm');

            var parts = content.Split(';');
            if (parts.Length >= 1 && int.TryParse(parts[0], out var btn))
            {
                if (btn == 64) return InputEvent.ScrollUp;   // Wheel up
                if (btn == 65) return InputEvent.ScrollDown; // Wheel down
            }

            return InputEvent.None;
        }

        private static InputEvent ProcessControlChar(byte b)
        {
            // Ctrl+A = 1, Ctrl+E = 5, Ctrl+C = 3, Ctrl+D = 4, etc
            var c = (char)(b + 64);
            var key = (ConsoleKey)('A' + b - 1);

            if (b == 3) // Ctrl+C
                return new InputEvent(new ConsoleKeyInfo('c', ConsoleKey.C, false, false, true));
            if (b == 4) // Ctrl+D
                return new InputEvent(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, true));

            return new InputEvent(new ConsoleKeyInfo(c, key, false, false, true));
        }

        private static ConsoleKey MapAsciiToKey(char c)
        {
            if (c >= 'a' && c <= 'z') return (ConsoleKey)(c - 'a' + (int)ConsoleKey.A);
            if (c >= 'A' && c <= 'Z') return (ConsoleKey)(c - 'A' + (int)ConsoleKey.A);
            if (c >= '0' && c <= '9') return (ConsoleKey)(c - '0' + (int)ConsoleKey.D0);
            if (c == ' ') return ConsoleKey.Spacebar;
            if (c == '\t') return ConsoleKey.Tab;
            if (c == '\r') return ConsoleKey.Enter;
            return ConsoleKey.None;
        }
    }

    #endregion

    #region ReadLine Implementation

    /// <summary>Read a line with full editing support. Returns null on Ctrl+C / Ctrl+D.</summary>
    public static string? ReadLine(IAnsiConsole console, string prompt = ">")
    {
        var buf = new StringBuilder();
        int cursor = 0;
        int prevRenderedLines = 1;

        // Ensure input thread is running
        StartInputThread();

        Console.Write($"\u001b[1m{prompt}\u001b[0m ");
        var promptLen = prompt.Length + 1;

        while (true)
        {
            // Process any pending events
            while (TryReadEvent(out var evt))
            {
                if (evt.Type == InputEventType.ScrollUp || evt.Type == InputEventType.ScrollDown)
                {
                    // Queue scroll for the UI to handle
                    ScrollQueue.Add(evt.Type == InputEventType.ScrollUp ? ScrollAction.Up : ScrollAction.Down);
                    continue;
                }

                if (evt.Type != InputEventType.Key || !evt.Key.HasValue)
                    continue;

                var key = evt.Key.Value;
                var result = ProcessKey(key, buf, ref cursor, ref prevRenderedLines, prompt, promptLen);

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

            // Small sleep to prevent busy-waiting
            Thread.Sleep(5);
        }
    }

    // Scroll queue for UI thread to consume
    private static readonly List<ScrollAction> ScrollQueue = new();

    /// <summary>Check for pending scroll actions (called from main thread).</summary>
    public static ScrollAction GetPendingScroll()
    {
        lock (ScrollQueue)
        {
            if (ScrollQueue.Count > 0)
            {
                var action = ScrollQueue[0];
                ScrollQueue.RemoveAt(0);
                return action;
            }
            return ScrollAction.None;
        }
    }

    private enum ReadLineResult { Continue, Submit, Cancel }

    private static ReadLineResult ProcessKey(ConsoleKeyInfo key, StringBuilder buf, ref int cursor, ref int prevRenderedLines, string prompt, int promptLen)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
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

        RedrawLine(buf.ToString(), cursor, promptLen, ref prevRenderedLines);
        return ReadLineResult.Continue;
    }

    private static void RedrawLine(string text, int cursor, int promptLen, ref int prevRenderedLines)
    {
        var totalWidth = Console.WindowWidth;
        if (totalWidth <= 0) totalWidth = 80;
        var totalCells = promptLen + text.Length;

        if (prevRenderedLines > 1)
            Console.Write($"\u001b[{prevRenderedLines - 1}A");

        Console.Write("\u001b[2K\r\u001b[J");
        Console.Write(new string(' ', promptLen));
        Console.Write(text);

        var newRenderedLines = (totalCells / totalWidth) + 1;
        if (totalCells > 0 && totalCells % totalWidth == 0) newRenderedLines++;
        prevRenderedLines = newRenderedLines;

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

    private static int JumpWordBack(StringBuilder buf, int cursor)
    {
        int pos = cursor - 1;
        while (pos > 0 && char.IsWhiteSpace(buf[pos])) pos--;
        while (pos > 0 && !char.IsWhiteSpace(buf[pos - 1])) pos--;
        return pos < 0 ? 0 : pos;
    }

    private static int JumpWordForward(StringBuilder buf, int cursor)
    {
        int pos = cursor;
        while (pos < buf.Length && char.IsWhiteSpace(buf[pos])) pos++;
        while (pos < buf.Length && !char.IsWhiteSpace(buf[pos])) pos++;
        return pos;
    }

    #endregion
}
