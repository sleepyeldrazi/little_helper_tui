using System.Text;
using Terminal.Gui;

namespace LittleHelperTui.Views;

/// <summary>
/// Main application window. No visible border/chrome — acts as a full-screen container.
/// Chat output is a scrollable read-only TextView. Input is a TextField at the bottom.
/// Matches the old Spectre UI: just text filling the screen.
/// </summary>
public class MainWindow : Window
{
    private readonly TextView _chatView;
    private readonly TextField _inputField;
    private readonly TuiController _controller;
    private readonly StringBuilder _chatBuffer = new();
    private bool _autoScroll = true;

    // Input history
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _savedDraft = "";

    public TextField InputField => _inputField;

    public MainWindow(TuiController controller)
    {
        _controller = controller;

        // No border/title — match old full-screen Spectre look
        Title = "";
        BorderStyle = LineStyle.None;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        // Chat output: read-only text view filling the screen
        _chatView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1), // leave 1 row for input
            ReadOnly = true,
            WordWrap = true,
            Text = ""
        };

        // Input field at bottom (acts as the > prompt)
        _inputField = new TextField
        {
            X = 2,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = ""
        };

        // ">" prompt label
        var promptLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Text = "> "
        };

        Add(_chatView, promptLabel, _inputField);

        // Wire up events
        _inputField.Accept += OnInputAccepting;

        // Input history and scroll key bindings
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

    /// <summary>Set the status in the window title (minimal, no status bar).</summary>
    public void SetStatus(string text)
    {
        // No-op — old UI didn't have a persistent status bar.
        // Status is shown inline in the chat via observer.
    }

    /// <summary>
    /// Append text to the chat output. All formatting happens in the caller.
    /// </summary>
    public void AppendText(string text)
    {
        Application.Invoke(() =>
        {
            _chatBuffer.Append(text);
            _chatView.Text = _chatBuffer.ToString();

            if (_autoScroll)
                _chatView.MoveEnd();
        });
    }

    /// <summary>Append a line of text (adds newline).</summary>
    public void AppendLine(string text = "")
    {
        AppendText(text + "\n");
    }

    /// <summary>Clear all chat output.</summary>
    public void ClearChat()
    {
        Application.Invoke(() =>
        {
            _chatBuffer.Clear();
            _chatView.Text = "";
            _autoScroll = true;
        });
    }

    /// <summary>Get the current terminal width for panel formatting.</summary>
    public int GetWidth()
    {
        return _chatView.Frame.Width > 0 ? _chatView.Frame.Width - 1 : 80;
    }

    private void ScrollChat(int delta)
    {
        var row = _chatView.TopRow + delta;
        if (row < 0) row = 0;
        _chatView.TopRow = row;

        var totalLines = _chatBuffer.ToString().Split('\n').Length;
        var viewHeight = _chatView.Frame.Height;
        _autoScroll = row >= totalLines - viewHeight - 2;
    }

    private void OnInputAccepting(object? sender, EventArgs e)
    {
        var text = _inputField.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text))
            return;

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
