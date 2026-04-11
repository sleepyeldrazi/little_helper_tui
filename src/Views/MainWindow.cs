using System.Text;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace LittleHelperTui.Views;

/// <summary>
/// Color schemes: dark background, softer palette matching old Spectre TUI.
/// </summary>
public static class DarkColors
{
    // Main text: light grey on black
    public static readonly ColorScheme Base = MakeScheme(Color.Gray, Color.Black);

    // User panel border: green on black
    public static readonly ColorScheme UserBorder = MakeScheme(Color.Green, Color.Black);

    // Assistant panel border: blue on black (soft teal-ish)
    public static readonly ColorScheme AssistantBorder = MakeScheme(Color.Blue, Color.Black);

    // Thinking panel border: dark grey on black
    public static readonly ColorScheme ThinkingBorder = MakeScheme(Color.DarkGray, Color.Black);

    // Panel content: white on black (readable)
    public static readonly ColorScheme Content = MakeScheme(Color.White, Color.Black);

    // Tool success icon/header
    public static readonly ColorScheme ToolOk = MakeScheme(Color.Green, Color.Black);

    // Tool error icon/header
    public static readonly ColorScheme ToolErr = MakeScheme(Color.Red, Color.Black);

    // Dim info/detail text
    public static readonly ColorScheme Dim = MakeScheme(Color.DarkGray, Color.Black);

    // Yellow warnings/compaction
    public static readonly ColorScheme Warning = MakeScheme(Color.Yellow, Color.Black);

    // Bright white for emphasis
    public static readonly ColorScheme Bright = MakeScheme(Color.White, Color.Black);

    // Red errors
    public static readonly ColorScheme Error = MakeScheme(Color.Red, Color.Black);

    // Dialog: soft grey on very dark grey, subtle highlight
    public static readonly ColorScheme Dialog = new()
    {
        Normal = new Attribute(Color.Gray, new Color(30, 30, 30)),
        Focus = new Attribute(Color.White, new Color(50, 50, 60)),
        HotNormal = new Attribute(Color.White, new Color(30, 30, 30)),
        HotFocus = new Attribute(Color.White, new Color(50, 50, 60))
    };

    private static ColorScheme MakeScheme(Color fg, Color bg) => new()
    {
        Normal = new Attribute(fg, bg),
        Focus = new Attribute(fg, bg),
        HotNormal = new Attribute(fg, bg),
        HotFocus = new Attribute(fg, bg)
    };
}

/// <summary>
/// Main window: dark, borderless, full-screen.
/// Chat area is a ScrollView of colored Label blocks. Input at bottom with "> " prompt.
/// </summary>
public class MainWindow : Window
{
    private readonly ScrollView _scrollView;
    private readonly View _chatContent;
    private readonly TextField _inputField;
    private readonly TuiController _controller;
    private bool _autoScroll = true;

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
            Height = Dim.Auto(),
            ColorScheme = DarkColors.Base
        };

        _scrollView = new ScrollView
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ColorScheme = DarkColors.Base
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
            }
        };

        _inputField.SetFocus();
    }

    public void SetStatus(string text) { }

    /// <summary>Add a colored text block to the chat.</summary>
    public void AddColoredBlock(string text, ColorScheme? scheme = null)
    {
        Application.Invoke(() =>
        {
            var label = new Label
            {
                X = 0,
                Y = _chatContent.Subviews.Count > 0
                    ? Pos.Bottom(_chatContent.Subviews[^1])
                    : 0,
                Width = Dim.Fill(),
                Height = Dim.Auto(),
                Text = text,
                ColorScheme = scheme ?? DarkColors.Base
            };

            _chatContent.Add(label);
            if (_autoScroll) ScrollToBottom();
        });
    }

    public void ClearChat()
    {
        Application.Invoke(() =>
        {
            foreach (var child in _chatContent.Subviews.ToList())
                _chatContent.Remove(child);
            _autoScroll = true;
        });
    }

    public void ScrollToBottom()
    {
        Application.Invoke(() =>
        {
            _scrollView.ScrollDown(_scrollView.GetContentSize().Height);
        });
    }

    private void ScrollChat(int delta)
    {
        var newOffset = _scrollView.ContentOffset with
        {
            Y = _scrollView.ContentOffset.Y - delta
        };
        _scrollView.ContentOffset = newOffset;

        var contentHeight = _scrollView.GetContentSize().Height;
        var viewportHeight = _scrollView.Frame.Height;
        var offset = -_scrollView.ContentOffset.Y;
        _autoScroll = offset >= contentHeight - viewportHeight - 2;
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
