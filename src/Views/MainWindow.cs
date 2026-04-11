using System.Text;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace LittleHelperTui.Views;

/// <summary>
/// Dark color schemes matching the old Spectre.Console TUI palette.
/// </summary>
public static class DarkColors
{
    /// <summary>Base scheme: light grey on black.</summary>
    public static readonly ColorScheme Base = new()
    {
        Normal = new Attribute(Color.Gray, Color.Black),
        Focus = new Attribute(Color.White, Color.Black),
        HotNormal = new Attribute(Color.White, Color.Black),
        HotFocus = new Attribute(Color.White, Color.Black)
    };

    /// <summary>User messages: green on black.</summary>
    public static readonly ColorScheme User = new()
    {
        Normal = new Attribute(Color.Green, Color.Black),
        Focus = new Attribute(Color.BrightGreen, Color.Black),
        HotNormal = new Attribute(Color.BrightGreen, Color.Black),
        HotFocus = new Attribute(Color.BrightGreen, Color.Black)
    };

    /// <summary>Assistant messages: blue on black.</summary>
    public static readonly ColorScheme Assistant = new()
    {
        Normal = new Attribute(Color.Blue, Color.Black),
        Focus = new Attribute(Color.BrightBlue, Color.Black),
        HotNormal = new Attribute(Color.BrightBlue, Color.Black),
        HotFocus = new Attribute(Color.BrightBlue, Color.Black)
    };

    /// <summary>Thinking: dim grey on black.</summary>
    public static readonly ColorScheme Thinking = new()
    {
        Normal = new Attribute(Color.DarkGray, Color.Black),
        Focus = new Attribute(Color.Gray, Color.Black),
        HotNormal = new Attribute(Color.Gray, Color.Black),
        HotFocus = new Attribute(Color.Gray, Color.Black)
    };

    /// <summary>Tool success: green on black.</summary>
    public static readonly ColorScheme ToolOk = new()
    {
        Normal = new Attribute(Color.Green, Color.Black),
        Focus = new Attribute(Color.BrightGreen, Color.Black),
        HotNormal = new Attribute(Color.BrightGreen, Color.Black),
        HotFocus = new Attribute(Color.BrightGreen, Color.Black)
    };

    /// <summary>Tool error: red on black.</summary>
    public static readonly ColorScheme ToolErr = new()
    {
        Normal = new Attribute(Color.Red, Color.Black),
        Focus = new Attribute(Color.BrightRed, Color.Black),
        HotNormal = new Attribute(Color.BrightRed, Color.Black),
        HotFocus = new Attribute(Color.BrightRed, Color.Black)
    };

    /// <summary>Dim info text.</summary>
    public static readonly ColorScheme Dim = new()
    {
        Normal = new Attribute(Color.DarkGray, Color.Black),
        Focus = new Attribute(Color.DarkGray, Color.Black),
        HotNormal = new Attribute(Color.DarkGray, Color.Black),
        HotFocus = new Attribute(Color.DarkGray, Color.Black)
    };

    /// <summary>Status/done: bright on black.</summary>
    public static readonly ColorScheme Status = new()
    {
        Normal = new Attribute(Color.White, Color.Black),
        Focus = new Attribute(Color.White, Color.Black),
        HotNormal = new Attribute(Color.White, Color.Black),
        HotFocus = new Attribute(Color.White, Color.Black)
    };

    /// <summary>Yellow warnings.</summary>
    public static readonly ColorScheme Warning = new()
    {
        Normal = new Attribute(Color.Yellow, Color.Black),
        Focus = new Attribute(Color.BrightYellow, Color.Black),
        HotNormal = new Attribute(Color.BrightYellow, Color.Black),
        HotFocus = new Attribute(Color.BrightYellow, Color.Black)
    };

    /// <summary>Dialog scheme: white on dark grey.</summary>
    public static readonly ColorScheme Dialog = new()
    {
        Normal = new Attribute(Color.White, Color.DarkGray),
        Focus = new Attribute(Color.Black, Color.Cyan),
        HotNormal = new Attribute(Color.Cyan, Color.DarkGray),
        HotFocus = new Attribute(Color.Black, Color.Cyan)
    };
}

/// <summary>
/// Main application window. Dark background, no border chrome.
/// Chat area is a ScrollView of colored Label views. Input is a TextField at bottom.
/// </summary>
public class MainWindow : Window
{
    private readonly ScrollView _scrollView;
    private readonly View _chatContent;
    private readonly TextField _inputField;
    private readonly TuiController _controller;
    private bool _autoScroll = true;

    // Input history
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
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        // Inner content view that grows as labels are added
        _chatContent = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Auto(),
            ColorScheme = DarkColors.Base
        };

        // ScrollView wrapping the content
        _scrollView = new ScrollView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ColorScheme = DarkColors.Base
        };
        _scrollView.Add(_chatContent);

        // ">" prompt
        var promptLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Text = "> ",
            ColorScheme = DarkColors.Dim
        };

        // Input field
        _inputField = new TextField
        {
            X = 2,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = "",
            ColorScheme = DarkColors.Base
        };

        Add(_scrollView, promptLabel, _inputField);

        _inputField.Accept += OnInputAccepting;

        _inputField.KeyDown += (s, e) =>
        {
            switch (e.KeyCode)
            {
                case KeyCode.CursorUp:
                    NavigateHistory(-1);
                    e.Handled = true;
                    break;
                case KeyCode.CursorDown:
                    NavigateHistory(1);
                    e.Handled = true;
                    break;
                case KeyCode.PageUp:
                    ScrollChat(-10);
                    e.Handled = true;
                    break;
                case KeyCode.PageDown:
                    ScrollChat(10);
                    e.Handled = true;
                    break;
            }
        };

        _inputField.SetFocus();
    }

    public void SetStatus(string text) { /* inline in chat */ }

    /// <summary>Get terminal width for panel formatting.</summary>
    public int GetWidth()
    {
        return _scrollView.Frame.Width > 0 ? _scrollView.Frame.Width - 1 : 80;
    }

    /// <summary>
    /// Add a colored text block to the chat. Each block is a Label with its own ColorScheme.
    /// </summary>
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

            if (_autoScroll)
                ScrollToBottom();
        });
    }

    /// <summary>Convenience: add a line with default color.</summary>
    public void AppendLine(string text = "")
    {
        AddColoredBlock(text, DarkColors.Base);
    }

    /// <summary>Clear all chat output.</summary>
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
            else if (_historyIndex > 0)
                _historyIndex--;
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
