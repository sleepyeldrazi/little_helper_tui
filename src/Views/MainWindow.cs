using System.ComponentModel;
using Terminal.Gui;
using LittleHelperTui.Observers;

namespace LittleHelperTui.Views;

/// <summary>
/// Main application window containing chat scroll view, input field, and status bar.
/// </summary>
public class MainWindow : Window
{
    private View _chatContent;
    private TextField _inputField;
    private Label _statusLabel;
    private TuiController _controller;

    // Input history
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _savedDraft = "";

    public View ChatContent => _chatContent;
    public TextField InputField => _inputField;
    public TerminalGuiObserver? Observer { get; set; }

    public MainWindow(TuiController controller)
    {
        _controller = controller;
        Title = "little helper";

        // Setup layout
        SetupLayout();

        // Set initial focus
        _inputField.SetFocus();
    }

    private void SetupLayout()
    {
        // Main window fills the screen
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        // Chat content container (scrollable area)
        _chatContent = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2 // Leave room for input + status
        };

        // Input field at bottom
        _inputField = new TextField
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1
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

        Add(_chatContent, _inputField, _statusLabel);

        // Wire up events
        _inputField.Accept += OnInputAccepting;
        
        // Handle key bindings for history
    }

    /// <summary>
    /// Set the status bar text.
    /// </summary>
    public void SetStatus(string text)
    {
        _statusLabel.Text = text;
    }

    /// <summary>
    /// Handle input field accepting (Enter key).
    /// </summary>
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
        {
            _controller.ExecuteCommand(text);
        }
        else
        {
            _controller.SubmitPrompt(text);
        }
        
        // Mark as handled
        if (e is HandledEventArgs handled)
        {
            handled.Handled = true;
        }
    }

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0)
            return;

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

    /// <summary>
    /// Add a view to the chat content.
    /// </summary>
    public void AddChatView(View view)
    {
        view.X = 0;
        view.Y = _chatContent.Subviews.Count > 0 
            ? Pos.Bottom(_chatContent.Subviews[^1]) 
            : 0;
        
        _chatContent.Add(view);
    }

    /// <summary>
    /// Clear all chat content.
    /// </summary>
    public void ClearChat()
    {
        foreach (var child in _chatContent.Subviews.ToList())
        {
            _chatContent.Remove(child);
        }
    }
}
