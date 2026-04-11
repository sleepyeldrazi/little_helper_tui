using System.Text;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace LittleHelperTui.Views;

/// <summary>
/// Color schemes: dark background (R:21 G:24 B:28), softer palette matching old Spectre TUI.
/// </summary>
public static class DarkColors
{
    // Background color: R:21 G:24 B:28 (dark grayish)
    public static readonly Color Bg = new(21, 24, 28);

    public static readonly ColorScheme Base = MakeScheme(Color.Gray, Bg);
    public static readonly ColorScheme UserBorder = MakeScheme(Color.Green, Bg);
    public static readonly ColorScheme AssistantBorder = MakeScheme(new Color(159, 156, 236), Bg);
    public static readonly ColorScheme ThinkingBorder = MakeScheme(Color.DarkGray, Bg);
    public static readonly ColorScheme Content = MakeScheme(Color.White, Bg);
    public static readonly ColorScheme ToolOk = MakeScheme(Color.Green, Bg);
    public static readonly ColorScheme ToolErr = MakeScheme(Color.Red, Bg);
    public static readonly ColorScheme Dim = MakeScheme(Color.DarkGray, Bg);
    public static readonly ColorScheme Warning = MakeScheme(new Color(180, 180, 180), Bg);
    public static readonly ColorScheme Bright = MakeScheme(Color.White, Bg);
    public static readonly ColorScheme Error = MakeScheme(Color.Red, Bg);
    public static readonly ColorScheme Bold = MakeScheme(Color.White, Bg);
    public static readonly ColorScheme Teal = MakeScheme(new Color(100, 200, 180), Bg);

    public static readonly ColorScheme Dialog = new()
    {
        Normal = new Attribute(Color.Gray, new Color(40, 44, 52)),
        Focus = new Attribute(Color.White, new Color(55, 60, 72)),
        HotNormal = new Attribute(Color.White, new Color(40, 44, 52)),
        HotFocus = new Attribute(Color.White, new Color(55, 60, 72))
    };

    private static ColorScheme MakeScheme(Color fg, Color bg) => new()
    {
        Normal = new Attribute(fg, bg),
        Focus = new Attribute(fg, bg),
        HotNormal = new Attribute(fg, bg),
        HotFocus = new Attribute(fg, bg)
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
    private readonly TextField _inputField;
    private readonly TuiController _controller;
    private bool _autoScroll = true;
    private int _nextY;
    private bool _updatingSize;

    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _savedDraft = "";

    public TextField InputField => _inputField;

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
            Height = Dim.Fill(1),
            ColorScheme = DarkColors.Base,
            ShowVerticalScrollIndicator = true
        };
        _scrollView.Add(_chatContent);

        var promptLabel = new Label
        {
            X = 0, Y = Pos.AnchorEnd(1),
            Text = "> ",
            ColorScheme = DarkColors.Dim
        };

        _inputField = new TextField
        {
            X = 2, Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(), Height = 1,
            Text = "",
            ColorScheme = DarkColors.Base
        };

        Add(_scrollView, promptLabel, _inputField);

        _inputField.Accept += OnInputAccepting;
        _inputField.KeyDown += (s, e) =>
        {
            switch (e.KeyCode)
            {
                case KeyCode.CursorUp: NavigateHistory(-1); e.Handled = true; break;
                case KeyCode.CursorDown: NavigateHistory(1); e.Handled = true; break;
                case KeyCode.PageUp: ScrollChat(-10); e.Handled = true; break;
                case KeyCode.PageDown: ScrollChat(10); e.Handled = true; break;
                case KeyCode.C when e.IsCtrl: _controller.Cancel(); e.Handled = true; break;
            }
        };

        _inputField.SetFocus();
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

    private void OnInputAccepting(object? sender, EventArgs e)
    {
        var text = _inputField.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;

        if (_history.Count == 0 || _history[^1] != text)
            _history.Add(text);
        _historyIndex = -1;
        _inputField.Text = "";

        if (text.StartsWith(":"))
            _controller.ExecuteCommand(text);
        else
            _controller.SubmitPrompt(text);

        if (e is System.ComponentModel.HandledEventArgs handled)
            handled.Handled = true;
    }

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;
        if (direction < 0)
        {
            if (_historyIndex == -1)
            {
                _savedDraft = _inputField.Text ?? "";
                _historyIndex = _history.Count - 1;
            }
            else if (_historyIndex > 0) _historyIndex--;
            _inputField.Text = _history[_historyIndex];
        }
        else
        {
            if (_historyIndex >= 0)
            {
                if (_historyIndex < _history.Count - 1)
                {
                    _historyIndex++;
                    _inputField.Text = _history[_historyIndex];
                }
                else
                {
                    _historyIndex = -1;
                    _inputField.Text = _savedDraft;
                }
            }
        }
    }
}
