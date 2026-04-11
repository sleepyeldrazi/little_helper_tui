# little_helper TUI Rewrite Plan: Terminal.Gui

## Status: Input Handling Failed

The custom P/Invoke-based input handling approach has failed twice:
1. First attempt: Terminal corruption, sequence leakage, escape codes appearing in output
2. Second attempt (clean-input branch): ^M characters appearing, Enter key not working

**Decision**: Move to Terminal.Gui v2 framework which has solved these problems professionally.

---

## Current Architecture (Pre-Rewrite)

### Structure
```
Program.cs (Main loop)
├── EnterAlternateScreen() - ESC[?1047h
├── Model selection/setup
└── Main REPL loop
    ├── CheckResize() - Redraw on width change
    ├── CheckScroll() - Process pending scroll events  
    ├── observer.Drain() - Render queued output
    ├── InputHandler.ReadLine() - BLOCKING custom input
    │   ├── Unix: P/Invoke read/select + termios raw mode
    │   ├── Windows: Console API ReadConsoleInput
    │   └── EscapeSequenceParser (state machine)
    └── Agent.RunAsync() - Streaming LLM response
        └── TuiObserver callbacks render panels
```

### Key Components

**TuiObserver**: Implements IAgentObserver
- Accumulates render actions in _renderHistory
- Drain() writes queued content
- Redraw() replays history on resize/scroll
- Scroll state managed by offset into _renderHistory

**InputHandler**: Custom cross-platform input
- Dedicated thread reading raw bytes
- Platform-specific implementations (Unix P/Invoke, Windows Console API)
- State machine for escape sequence parsing
- FAILED: Terminal state corruption, race conditions

**Agent Integration**: little_helper_core submodule
- Agent.RunAsync() with streaming callbacks
- ToolInterceptor for git checkpoints
- SessionLogger for JSONL logs

### UI Design (Must Preserve)
- **Scrollback-based**: Chat history in alternate buffer
- **Panel rendering**: Spectre.Console panels for user/assistant/tool
- **Status bar**: Model, steps, tokens, timing
- **Commands**: :quit, :hide, :sessions, :tokens, etc.
- **Input line**: Custom readline with history, tab completion

---

## Terminal.Gui v2 Overview

### What is Terminal.Gui?

Terminal.Gui (v2) is a cross-platform Terminal UI toolkit for .NET:
- **Repository**: https://github.com/gui-cs/Terminal.Gui
- **License**: MIT
- **Version**: v2 (actively developed, v1 is legacy)
- **Platforms**: Windows, macOS, Linux
- **Size**: ~2MB dependency, ~50K LOC

### Key Concepts (CRITICAL: Read docs during implementation)

#### 1. Application Model
```csharp
// Terminal.Gui manages the main loop
Application.Init();
Application.Run(new MyWindow());
Application.Shutdown();
```

**Key difference**: Framework owns the main loop, not us.

#### 2. View Hierarchy
```csharp
// Everything is a View
Window (toplevel)
├── ScrollView (our chat history)
│   ├── Label/Message (each turn)
│   └── ...
├── TextField (input line at bottom)
└── StatusBar (bottom status)
```

**Key difference**: No alternate buffer, no scrollback - we render Views.

#### 3. Event-Driven Input
```csharp
// Terminal.Gui handles ALL input
Application.RootMouseEvent += (mouse) => { };
Application.RootKeyEvent += (key) => { };
// Or per-view events
myView.KeyPress += (args) => { };
```

**Key difference**: No raw mode management, no P/Invoke.

#### 4. ConsoleDriver Architecture
```csharp
// Platform abstraction
ConsoleDriver (abstract)
├── WindowsDriver - Windows Console API
├── CursesDriver - Unix ncurses
└── NetDriver - Pure .NET (fallback)
```

Driver handles: screen buffer, colors, input, output.

### Documentation (MUST READ BEFORE CODING)

1. **Getting Started**: https://gui-cs.github.io/Terminal.GuiV2Docs/
2. **API Reference**: https://gui-cs.github.io/Terminal.GuiV2Docs/api/Terminal.Gui.html
3. **v2 Branch README**: https://github.com/gui-cs/Terminal.Gui/tree/v2_develop
4. **Examples**: https://github.com/gui-cs/Terminal.Gui/tree/v2_develop/Terminal.Gui/Examples

**Critical sections to read**:
- `Application` class - lifecycle management
- `View` class - layout, drawing, events
- `ScrollView` - our chat history container
- `TextView` vs `TextField` - input handling
- `StatusBar` - bottom bar
- `Dialog` - for commands like :model selection
- `MenuBar` - optional for commands
- ColorScheme/Color - theming

---

## Rewrite Architecture (Target)

### High-Level Structure

```csharp
Application.Init();

// Main window containing everything
var mainWindow = new MainWindow();
Application.Run(mainWindow);

Application.Shutdown();
```

### MainWindow View Hierarchy

```
MainWindow (Window)
├── ChatScrollView (ScrollView) - fills most of screen
│   └── ChatContent (View) - grows dynamically
│       ├── UserMessageView (custom View) - green panel
│       ├── AssistantMessageView (custom View) - blue panel
│       ├── ToolResultView (custom View) - green/red panel
│       └── StatusBarView (custom View) - done indicator
├── InputField (TextField) - bottom, fixed height
│   └── History support, tab completion
└── StatusBar (StatusBar) - very bottom
    └── model | steps | tokens | time
```

### Component Mapping

| Current Component | Terminal.Gui Equivalent |
|-------------------|------------------------|
| Program.cs main loop | `Application.Run()` |
| TuiObserver | Custom Views that render content |
| InputHandler.ReadLine | `TextField` with events |
| CheckScroll/Redraw | `ScrollView.ScrollTo()` |
| CheckResize | `View.LayoutSubviews()` |
| Spectre.Console panels | Custom `View` subclasses |
| Alternate buffer | Full-screen `Window` |

### Pseudocode Implementation

#### 1. MainWindow.cs
```csharp
public class MainWindow : Window
{
    private ScrollView _chatScroll;
    private View _chatContent; // Contains message views
    private TextField _inputField;
    private StatusBar _statusBar;
    private TuiController _controller;

    public MainWindow(TuiController controller)
    {
        _controller = controller;
        
        // Setup layout
        SetupLayout();
        
        // Wire events
        _inputField.KeyPress += OnInputKeyPress;
        Application.RootMouseEvent += OnGlobalMouse;
    }

    void SetupLayout()
    {
        // Chat area takes most space
        _chatScroll = new ScrollView()
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2, // Leave room for input + status
            ShowVerticalScrollIndicator = true,
            ContentSize = new Size(80, 1000) // Grows dynamically
        };
        
        _chatContent = new View()
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Auto() // Dynamic height
        };
        _chatScroll.Add(_chatContent);
        
        // Input at bottom
        _inputField = new TextField()
        {
            X = 0, 
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill()
        };
        
        // Status bar at very bottom
        _statusBar = new StatusBar(new StatusItem[] {
            new StatusItem(Key.Q, "~Q~uit", () => Application.RequestStop()),
            new StatusItem(Key.T, "~T~okens", () => ShowTokens())
        });
        
        Add(_chatScroll, _inputField, _statusBar);
    }

    void OnInputKeyPress(KeyEventEventArgs args)
    {
        if (args.KeyEvent.Key == Key.Enter)
        {
            var text = _inputField.Text.ToString();
            _inputField.Text = "";
            args.Handled = true;
            
            if (text.StartsWith(":"))
                _controller.ExecuteCommand(text);
            else
                _controller.SubmitPrompt(text);
        }
    }

    void OnGlobalMouse(MouseEvent mouse)
    {
        // Mouse wheel scrolls chat
        if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            _chatScroll.ScrollUp(3);
        }
        else if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            _chatScroll.ScrollDown(3);
        }
    }

    // Called by controller to add message
    public void AddMessage(MessageView view)
    {
        _chatContent.Add(view);
        // Auto-scroll to bottom
        _chatScroll.ScrollTo(_chatContent.Bounds.Height, 0);
        SetNeedsDisplay();
    }
}
```

#### 2. Message Views (Spectre Panel Replacement)
```csharp
// Base class for chat messages
public abstract class MessageView : View
{
    protected string Content { get; set; }
    
    public MessageView(string content)
    {
        Content = content;
        CanFocus = false;
    }
    
    public override void Redraw(Rect bounds)
    {
        // Draw border using Terminal.Gui primitives
        // Draw content wrapped to width
        // Calculate Height based on content
    }
}

public class UserMessageView : MessageView
{
    public UserMessageView(string text) : base(text) { }
    
    public override void Redraw(Rect bounds)
    {
        // Green border, "You" header
        // Use Application.Driver.DrawFrame
        // Use ColorScheme for colors
    }
}

public class AssistantMessageView : MessageView
{
    public AssistantMessageView(string text, int step) : base(text) { }
    
    public override void Redraw(Rect bounds)
    {
        // Blue border, "Assistant Step N" header
    }
}

public class ToolResultView : MessageView
{
    public ToolResultView(string tool, string result) : base(result) { }
    
    public override void Redraw(Rect bounds)
    {
        // Green + icon for success, red x for error
        // Compact display
    }
}
```

#### 3. TuiController (Replaces Program.cs logic)
```csharp
public class TuiController
{
    private MainWindow _window;
    private Agent _agent;
    private ResolvedModel _model;
    private SessionLogger _logger;
    
    public void Run()
    {
        Application.Init();
        
        // Model selection dialog
        _model = ShowModelSelectionDialog();
        if (_model == null) return;
        
        _window = new MainWindow(this);
        Application.Run(_window);
        
        Application.Shutdown();
    }
    
    public async void SubmitPrompt(string text)
    {
        // Add user message to UI
        _window.AddMessage(new UserMessageView(text));
        
        // Create agent if needed
        _agent ??= CreateAgent();
        
        // Run agent with streaming
        var cts = new CancellationTokenSource();
        var observer = new TerminalGuiObserver(_window);
        
        _agent.Observer = observer; // Modified core interface
        await _agent.RunAsync(text, cts.Token);
    }
    
    public void ExecuteCommand(string cmd)
    {
        switch (cmd.ToLower())
        {
            case ":quit": Application.RequestStop(); break;
            case ":tokens": ShowTokensDialog(); break;
            case ":sessions": ShowSessionsDialog(); break;
            // ... etc
        }
    }
}
```

#### 4. TerminalGuiObserver (Replaces TuiObserver)
```csharp
public class TerminalGuiObserver : IAgentObserver
{
    private MainWindow _window;
    
    public TerminalGuiObserver(MainWindow window)
    {
        _window = window;
    }
    
    public void OnModelResponse(ModelResponse response, int step)
    {
        Application.MainLoop.Invoke(() =>
        {
            _window.AddMessage(new AssistantMessageView(response.Content, step));
        });
    }
    
    public void OnToolCallCompleted(ToolCall call, ToolResult result, ...)
    {
        Application.MainLoop.Invoke(() =>
        {
            _window.AddMessage(new ToolResultView(call.Name, result.Output));
        });
    }
    
    // Other callbacks...
}
```

### Critical Implementation Notes

#### 1. Thread Safety
Terminal.Gui is NOT thread-safe. All UI updates must be on main thread:
```csharp
Application.MainLoop.Invoke(() => {
    // Safe to update UI here
    _window.AddMessage(view);
});
```

#### 2. Async Integration
Agent runs on background thread, UI updates via Invoke:
```csharp
public async void SubmitPrompt(string text)
{
    // UI update (on main thread already)
    AddUserMessage(text);
    
    // Background work
    await Task.Run(() => _agent.RunAsync(...));
    
    // UI update via Invoke
    Application.MainLoop.Invoke(() => AddDoneMessage());
}
```

#### 3. Layout System
Terminal.Gui uses computed layout:
```csharp
// Absolute positioning
X = 0, Y = 0

// Fill remaining space
Width = Dim.Fill(), Height = Dim.Fill()

// Relative to other views
Y = Pos.Bottom(otherView)

// Anchored to end
Y = Pos.AnchorEnd(2) // 2 rows from bottom
```

#### 4. Color Schemes
```csharp
// Define custom scheme
Colors.Base.Normal = Application.Driver.MakeAttribute(Color.White, Color.Blue);
Colors.Base.Focus = Application.Driver.MakeAttribute(Color.Black, Color.Gray);

// Apply to view
myView.ColorScheme = Colors.Base;
```

### File Structure (Target)

```
src/
├── Program.cs              # Entry point, Application.Init/Run/Shutdown
├── TuiController.cs        # Main controller, agent integration
├── Views/
│   ├── MainWindow.cs       # Root window with layout
│   ├── ChatScrollView.cs   # Scrollable message container
│   ├── MessageViews.cs     # UserMessageView, AssistantMessageView, etc.
│   ├── InputField.cs       # TextField with history/completion
│   └── StatusBarView.cs    # Bottom status
├── Observers/
│   └── TerminalGuiObserver.cs  # IAgentObserver implementation
├── Dialogs/
│   ├── ModelSelectionDialog.cs
│   ├── SessionsDialog.cs
│   └── TokensDialog.cs
└── CoreIntegration/
    └── ClientFactory.cs    # Unchanged from current
```

### Migration Steps

1. **Add Terminal.Gui v2 NuGet**
   ```xml
   <PackageReference Include="Terminal.Gui" Version="2.0.0-*" />
   ```

2. **Create Views skeleton**
   - Start with MainWindow containing just ScrollView + TextField
   - Verify basic layout works

3. **Port observer callbacks**
   - TuiObserver → TerminalGuiObserver
   - Test message rendering

4. **Port input handling**
   - Commands (:quit, etc.) via TextField events
   - Mouse wheel via RootMouseEvent

5. **Port dialogs**
   - Model selection, sessions, etc.

6. **Polish**
   - Colors matching current theme
   - Status bar
   - Resize handling

### Risk Assessment

**Pros**:
- Professional input handling (no corruption)
- Cross-platform by design
- No P/Invoke in our code
- Maintained framework

**Cons**:
- Major rewrite (~1-2 weeks)
- Different architecture (event-driven vs sequential)
- Framework overhead (~2MB)
- v2 is actively developed (API may change)

**Alternative**: Use Terminal.Gui's NetDriver as reference implementation for our own driver, but this is essentially copying their code.

### Decision

Proceed with Terminal.Gui v2 rewrite. The input problem is solved by the framework, and the event-driven model is actually cleaner for a TUI than our current sequential approach.

---

## Next Steps

1. Read Terminal.Gui v2 documentation (links above)
2. Study Examples in their repo
3. Create feature branch `feat/terminal-gui`
4. Implement skeleton MainWindow
5. Port incrementally

**Estimated LOC**: ~2,000-2,500 (current is ~2,500, should stay similar)
