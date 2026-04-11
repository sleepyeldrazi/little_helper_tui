using System.ComponentModel;
using System.Text;
using Terminal.Gui;

namespace LittleHelperTui.Views;

/// <summary>
/// Main application window. Chat output is a scrollable TextView (read-only).
/// Input is a TextField at the bottom. Status bar shows model/step info.
/// </summary>
public class MainWindow : Window
{
    private readonly TextView _chatView;
    private readonly TextField _inputField;
    private readonly Label _statusLabel;
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
        Title = "little helper";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        // Chat output: read-only text view filling most of the window
        _chatView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2), // leave room for input + status
            ReadOnly = true,
            WordWrap = true,
            Text = ""
        };

        // Input field at bottom
        _inputField = new TextField
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
            Text = ""
        };

        // Status bar at very bottom
        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = "Ready"
        };

        Add(_chatView, _inputField, _statusLabel);

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

    /// <summary>Set the status bar text.</summary>
    public void SetStatus(string text)
    {
        Application.Invoke(() => { _statusLabel.Text = text; });
    }

    /// <summary>
    /// Append text to the chat output. This is the primary rendering method.
    /// All message formatting happens in the observer/controller — this just appends raw text.
    /// </summary>
    public void AppendText(string text)
    {
        Application.Invoke(() =>
        {
            _chatBuffer.Append(text);
            _chatView.Text = _chatBuffer.ToString();

            if (_autoScroll)
            {
                // Move cursor to end to auto-scroll
                _chatView.MoveEnd();
            }
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

    private void ScrollChat(int delta)
    {
        // Positive delta = down, negative = up
        var row = _chatView.TopRow + delta;
        if (row < 0) row = 0;
        _chatView.TopRow = row;

        // Disable auto-scroll if user scrolled up
        var totalLines = _chatBuffer.ToString().Split('\n').Length;
        var viewHeight = _chatView.Frame.Height;
        _autoScroll = row >= totalLines - viewHeight - 2;
    }

    private void OnInputAccepting(object? sender, EventArgs e)
    {
        var text = _inputField.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text))
            return;

        // Add to history
        if (_history.Count == 0 || _history[^1] != text)
            _history.Add(text);
        _historyIndex = -1;

        // Clear input
        _inputField.Text = "";

        // Handle command or prompt
        if (text.StartsWith(":"))
            _controller.ExecuteCommand(text);
        else
            _controller.SubmitPrompt(text);

        if (e is HandledEventArgs handled)
            handled.Handled = true;
    }

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;

        if (direction < 0) // Up (older)
        {
            if (_historyIndex == -1)
            {
                _savedDraft = _inputField.Text ?? "";
                _historyIndex = _history.Count - 1;
            }
            else if (_historyIndex > 0)
            {
                _historyIndex--;
            }
            _inputField.Text = _history[_historyIndex];
        }
        else // Down (newer)
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
