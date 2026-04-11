using System.Text;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace LittleHelperTui.Views;

/// <summary>
/// Color schemes: use terminal's default background (no explicit bg color).
/// Foreground colors are RGB for consistent accent colors.
/// </summary>
public static class DarkColors
{
    // Don't set background - let terminal use its native color
    public static readonly ColorScheme Base = MakeScheme(Color.Gray);
    public static readonly ColorScheme UserBorder = MakeScheme(Color.Green);
    public static readonly ColorScheme AssistantBorder = MakeScheme(new Color(159, 156, 236));
    public static readonly ColorScheme ThinkingBorder = MakeScheme(Color.DarkGray);
    public static readonly ColorScheme Content = MakeScheme(Color.White);
    public static readonly ColorScheme ToolOk = MakeScheme(Color.Green);
    public static readonly ColorScheme ToolErr = MakeScheme(Color.Red);
    public static readonly ColorScheme Dim = MakeScheme(Color.DarkGray);
    public static readonly ColorScheme Warning = MakeScheme(new Color(180, 180, 180));
    public static readonly ColorScheme Bright = MakeScheme(Color.White);
    public static readonly ColorScheme Error = MakeScheme(Color.Red);
    public static readonly ColorScheme Bold = MakeScheme(Color.White);
    public static readonly ColorScheme Teal = MakeScheme(new Color(100, 200, 180));

    public static readonly ColorScheme Dialog = new()
    {
        Normal = new Attribute(Color.Gray, Color.Black),
        Focus = new Attribute(Color.White, Color.Black),
        HotNormal = new Attribute(Color.White, Color.Black),
        HotFocus = new Attribute(Color.White, Color.Black)
    };

    // Create scheme with foreground color only - background uses terminal default
    private static ColorScheme MakeScheme(Color fg) => new()
    {
        Normal = new Attribute(fg, Color.Black),
        Focus = new Attribute(fg, Color.Black),
        HotNormal = new Attribute(fg, Color.Black),
        HotFocus = new Attribute(fg, Color.Black)
    };
}

public record TextSegment(string Text, ColorScheme Scheme);

/// <summary>
/// A view that renders multiple colored text segments on a single line.
/// </summary>
public class ColoredLine : View
{
    private readonly List<TextSegment> _segments;

    public ColoredLine(List<TextSegment> segments)
    {
        _segments = segments;
        Height = 1;
        Width = Dim.Fill();
        CanFocus = false;
    }

    public override void OnDrawContent(System.Drawing.Rectangle viewport)
    {
        var bounds = GetContentSize();
        int x = 0;
        foreach (var seg in _segments)
        {
            Driver.SetAttribute(seg.Scheme.Normal);
            foreach (var rune in seg.Text.EnumerateRunes())
            {
                var cols = rune.GetColumns();
                if (cols < 1) cols = 1;
                if (x + cols > bounds.Width) break;
                Move(x, 0);
                Driver.AddRune(rune);
                x += cols;
            }
        }
        var spaceRune = new Rune(' ');
        Driver.SetAttribute(DarkColors.Base.Normal);
        while (x < bounds.Width)
        {
            Move(x, 0);
            Driver.AddRune(spaceRune);
            x++;
        }
    }
}

/// <summary>
/// Main window: dark, borderless, full-screen.
/// Chat area uses a simple View with manual Y tracking inside a ScrollView.
/// All view mutation goes through InvokeUI which handles thread safety.
/// </summary>
public class MainWindow : Window
{
    private readonly ScrollView _scrollView;
    private readonly View _chatContent;
    private readonly TextView _inputView;
    private readonly TuiController _controller;
    private bool _autoScroll = true;
    private int _nextY;
    private bool _updatingSize;

    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _savedDraft = "";

    public TextView InputView => _inputView;

    public MainWindow(TuiController controller)
    {
        _controller = controller;
        Title = "";
        BorderStyle = LineStyle.None;
        ColorScheme = DarkColors.Base;
        X = 0; Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _chatContent = new View
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = DarkColors.Base
        };

        _scrollView = new ScrollView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),  // Leave room for multi-line input
            ColorScheme = DarkColors.Base,
            ShowVerticalScrollIndicator = true
        };
        _scrollView.Add(_chatContent);

        var promptLabel = new Label
        {
            X = 0, Y = Pos.AnchorEnd(3),
            Text = "> ",
            ColorScheme = DarkColors.Dim
        };

        _inputView = new TextView
        {
            X = 2, Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(), Height = 3,
            Text = "",
            ColorScheme = DarkColors.Base,
            WordWrap = true,
            AllowsTab = false  // Tab does path completion, not insert tab
        };

        Add(_scrollView, promptLabel, _inputView);

        _inputView.KeyDown += (s, e) =>
        {
            switch (e.KeyCode)
            {
                case KeyCode.CursorUp when e.IsCtrl: NavigateHistory(-1); e.Handled = true; break;
                case KeyCode.CursorDown when e.IsCtrl: NavigateHistory(1); e.Handled = true; break;
                case KeyCode.PageUp: ScrollChat(-10); e.Handled = true; break;
                case KeyCode.PageDown: ScrollChat(10); e.Handled = true; break;
                case KeyCode.Tab: CompletePath(); e.Handled = true; break;
                case KeyCode.C when e.IsCtrl && !e.IsShift: _controller.Cancel(); e.Handled = true; break;
                case KeyCode.Enter when !e.IsShift: SubmitInput(); e.Handled = true; break;
            }
        };

        _inputView.SetFocus();
    }

    /// <summary>
    /// Marshal action to UI thread. Always uses Application.Invoke to ensure
    /// the action runs during the main loop iteration, not inside an event handler.
    /// </summary>
    private static void InvokeUI(Action action)
    {
        Application.Invoke(action);
    }

    public void SetStatus(string text) { }

    public int GetChatWidth()
    {
        var w = _scrollView.Frame.Width;
        if (w <= 0) return 80;
        return w - 1;
    }

    public void AddColoredBlock(string text, ColorScheme? scheme = null)
    {
        InvokeUI(() =>
        {
            var lines = text.Split('\n');
            foreach (var line in lines)
            {
                var label = new Label
                {
                    X = 0,
                    Y = _nextY,
                    Width = Dim.Fill(),
                    Height = 1,
                    Text = line,
                    ColorScheme = scheme ?? DarkColors.Base
                };
                _chatContent.Add(label);
                _nextY++;
            }
            UpdateContentSize();
        });
    }

    public void AddColoredSegments(List<TextSegment> segments)
    {
        InvokeUI(() =>
        {
            var line = new ColoredLine(segments)
            {
                X = 0,
                Y = _nextY,
            };
            _chatContent.Add(line);
            _nextY++;
            UpdateContentSize();
        });
    }

    public void ClearChat()
    {
        InvokeUI(() =>
        {
            foreach (var child in _chatContent.Subviews.ToList())
                _chatContent.Remove(child);
            _nextY = 0;
            _autoScroll = true;
            UpdateContentSize();
        });
    }

    private void UpdateContentSize()
    {
        if (_updatingSize) return;
        _updatingSize = true;
        try
        {
            _chatContent.Height = Math.Max(1, _nextY);
            var w = _scrollView.Frame.Width;
            if (w <= 0) w = 80;
            _scrollView.SetContentSize(new System.Drawing.Size(w, Math.Max(1, _nextY)));

            if (_autoScroll)
            {
                var viewportHeight = _scrollView.Frame.Height;
                var targetOffset = Math.Max(0, _nextY - viewportHeight);
                _scrollView.ContentOffset = new System.Drawing.Point(0, -targetOffset);
            }
        }
        finally
        {
            _updatingSize = false;
        }
    }

    public void ScrollToBottom()
    {
        InvokeUI(() =>
        {
            var viewportHeight = _scrollView.Frame.Height;
            var targetOffset = Math.Max(0, _nextY - viewportHeight);
            _scrollView.ContentOffset = new System.Drawing.Point(0, -targetOffset);
            _autoScroll = true;
        });
    }

    private void ScrollChat(int delta)
    {
        var newY = _scrollView.ContentOffset.Y - delta;
        var maxOffset = Math.Max(0, _nextY - _scrollView.Frame.Height);
        newY = Math.Max(-maxOffset, Math.Min(0, newY));
        _scrollView.ContentOffset = new System.Drawing.Point(0, newY);
        _autoScroll = -newY >= _nextY - _scrollView.Frame.Height - 2;
    }

    private void SubmitInput()
    {
        var text = _inputView.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;

        if (_history.Count == 0 || _history[^1] != text)
            _history.Add(text);
        _historyIndex = -1;
        _inputView.Text = "";

        if (text.StartsWith(":"))
            _controller.ExecuteCommand(text);
        else
            _controller.SubmitPrompt(text);
    }

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;
        if (direction < 0)
        {
            if (_historyIndex == -1)
            {
                _savedDraft = _inputView.Text ?? "";
                _historyIndex = _history.Count - 1;
            }
            else if (_historyIndex > 0) _historyIndex--;
            _inputView.Text = _history[_historyIndex];
        }
        else
        {
            if (_historyIndex >= 0)
            {
                if (_historyIndex < _history.Count - 1)
                {
                    _historyIndex++;
                    _inputView.Text = _history[_historyIndex];
                }
                else
                {
                    _historyIndex = -1;
                    _inputView.Text = _savedDraft;
                }
            }
        }
    }

    /// <summary>Path completion on Tab - completes partial paths using filesystem.</summary>
    private void CompletePath()
    {
        var text = _inputView.Text ?? "";
        if (string.IsNullOrEmpty(text)) return;

        // Find the partial path at cursor position
        // TextView.CursorPosition is a Point (column, line), extract position
        var cursorPoint = _inputView.CursorPosition;
        var lines = text.Split('\n');
        var lineIndex = Math.Min(cursorPoint.Y, lines.Length - 1);
        var colIndex = Math.Min(cursorPoint.X, lines[lineIndex].Length);
        
        // Calculate absolute position in text
        var cursorPos = lines.Take(lineIndex).Sum(l => l.Length + 1) + colIndex;
        var beforeCursor = text[..Math.Min(cursorPos, text.Length)];
        
        // Find the start of the current word (path)
        var wordStart = beforeCursor.Length - 1;
        while (wordStart >= 0 && !char.IsWhiteSpace(beforeCursor[wordStart]))
            wordStart--;
        wordStart++;
        
        var partial = beforeCursor[wordStart..];
        if (string.IsNullOrEmpty(partial)) return;

        try
        {
            // Determine directory and pattern
            string dir, pattern;
            if (partial.EndsWith('/'))
            {
                dir = partial;
                pattern = "*";
            }
            else
            {
                dir = Path.GetDirectoryName(partial) ?? ".";
                pattern = Path.GetFileName(partial) + "*";
                if (dir == "") dir = ".";
            }

            // Expand ~ to home directory
            if (dir.StartsWith("~"))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                dir = home + dir[1..];
            }

            if (!Directory.Exists(dir)) return;

            // Find matches
            var entries = new List<string>();
            if (partial.EndsWith('/') || !Path.GetFileName(partial).Contains('.'))
            {
                // Include directories
                var dirs = Directory.GetDirectories(dir, pattern);
                entries.AddRange(dirs.Select(d => d + "/"));
            }
            var files = Directory.GetFiles(dir, pattern);
            entries.AddRange(files);

            if (entries.Count == 0) return;

            if (entries.Count == 1)
            {
                // Single match - complete it
                var completion = entries[0];
                if (partial.StartsWith("./") || partial.StartsWith("../"))
                {
                    // Keep relative prefix
                    var prefix = Path.GetDirectoryName(partial) ?? "";
                    if (!string.IsNullOrEmpty(prefix)) completion = prefix + "/" + Path.GetFileName(completion);
                }
                else if (partial.StartsWith("~"))
                {
                    // Convert back to ~ form if that's what user typed
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (completion.StartsWith(home))
                        completion = "~" + completion[home.Length..];
                }

                var newText = beforeCursor[..wordStart] + completion + text[cursorPos..];
                _inputView.Text = newText;
                // Set cursor to end of completion (on same line for simplicity)
                _inputView.CursorPosition = new System.Drawing.Point(wordStart + completion.Length, cursorPoint.Y);
            }
            else
            {
                // Multiple matches - show them
                var commonPrefix = FindCommonPrefix(entries);
                if (commonPrefix.Length > partial.Length)
                {
                    // Complete to common prefix
                    var newText = beforeCursor[..wordStart] + commonPrefix + text[cursorPos..];
                    _inputView.Text = newText;
                    _inputView.CursorPosition = new System.Drawing.Point(wordStart + commonPrefix.Length, cursorPoint.Y);
                }
                else
                {
                    // Show options
                    _controller.ShowCompletions(entries.Select(e => Path.GetFileName(e) ?? e).ToList());
                }
            }
        }
        catch { /* ignore completion errors */ }
    }

    private static string FindCommonPrefix(List<string> strings)
    {
        if (strings.Count == 0) return "";
        var prefix = strings[0];
        foreach (var s in strings)
        {
            while (!s.StartsWith(prefix) && prefix.Length > 0)
                prefix = prefix[..^1];
        }
        return prefix;
    }
}
