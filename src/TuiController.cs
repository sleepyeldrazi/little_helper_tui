using System.Diagnostics;
using LittleHelper;
using LittleHelperTui.Dialogs;
using LittleHelperTui.Observers;
using LittleHelperTui.Views;
using Terminal.Gui;

namespace LittleHelperTui;

/// <summary>
/// Main controller for the TUI. Handles agent lifecycle, command dispatch,
/// and coordination between the UI and core agent.
/// </summary>
public class TuiController
{
    private readonly TuiConfig _config;
    private readonly GitCheckpoint? _gitCheckpoint;
    private readonly string _workingDir;
    private readonly bool _yoloMode;

    private MainWindow? _mainWindow;
    private TerminalGuiObserver? _observer;
    private Agent? _agent;
    private SessionLogger? _logger;
    private ResolvedModel? _model;

    private CancellationTokenSource? _currentCts;
    private string? _pendingSkillContent;

    public TuiController(TuiConfig config, bool yoloMode = false)
    {
        _config = config;
        _yoloMode = yoloMode;
        _workingDir = Directory.GetCurrentDirectory();

        // Initialize git checkpoint service
        _gitCheckpoint = new GitCheckpoint(_workingDir, config.GitCheckpoint);
        _gitCheckpoint.EnsureInitialized();
    }

    /// <summary>
    /// Set the main window reference.
    /// </summary>
    public void SetMainWindow(MainWindow window)
    {
        _mainWindow = window;
        _observer = new TerminalGuiObserver(window.ChatContent, _config);
        window.Observer = _observer;
    }

    /// <summary>
    /// Set the current model.
    /// </summary>
    public void SetModel(ResolvedModel model)
    {
        _model = model;
        _mainWindow?.SetStatus($"Model: {model.ModelId}");
    }

    /// <summary>
    /// Submit a user prompt to the agent.
    /// </summary>
    public async void SubmitPrompt(string text)
    {
        if (_mainWindow == null || _observer == null || _model == null)
            return;

        // Add user message to UI
        _observer.AddUserMessage(text);
        _observer.AddSeparator();

        // Create session logger if needed
        _logger ??= new SessionLogger(_model.ModelId, _workingDir);

        // Create agent if needed
        _agent ??= CreateAgent();

        // Prepare input with pending skill if any
        var effectiveInput = text;
        if (_pendingSkillContent != null)
        {
            effectiveInput = $"{_pendingSkillContent}\n\n---\n\n{text}";
            _pendingSkillContent = null;
        }

        // Run agent
        _currentCts = new CancellationTokenSource();
        var cts = _currentCts;
        var sw = Stopwatch.StartNew();

        _mainWindow.SetStatus($"Running {_model.ModelId}...");
        _mainWindow.InputField.Enabled = false;

        try
        {
            // Set up tool interceptor for git checkpoints + diff snapshots
            _agent.Control.ToolInterceptor = call =>
            {
                if (call.Name.Equals("write", StringComparison.OrdinalIgnoreCase) ||
                    call.Name.Equals("edit", StringComparison.OrdinalIgnoreCase) ||
                    call.Name.Equals("patch", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (call.Arguments.TryGetProperty("path", out var pathEl))
                        {
                            var path = pathEl.GetString();
                            if (path != null)
                            {
                                var fullPath = Path.GetFullPath(Path.Combine(_workingDir, path));
                                _gitCheckpoint?.CheckpointBeforeWrite(fullPath);
                                DiffViewer.Snapshot(fullPath);
                            }
                        }
                    }
                    catch { }
                }
                return call;
            };

            // Run agent in background
            var isFirstTurn = _agent.History.Count == 0;
            var result = await _agent.RunAsync(effectiveInput, cts.Token, clearHistory: isFirstTurn);

            sw.Stop();

            // Show done status
            _observer.AddStatusMessage(
                result.Success,
                _observer.CurrentStep,
                sw.ElapsedMilliseconds,
                _model.ContextWindow,
                result.FilesChanged.Count);

            // Auto-show diffs if configured
            if (_config.AutoShowDiffs && result.FilesChanged.Count > 0)
            {
                var lastFile = result.FilesChanged.LastOrDefault();
                if (lastFile != null)
                {
                    var diffView = new DiffView(lastFile);
                    _mainWindow.AddChatView(diffView);
                }
            }

            _mainWindow.SetStatus($"Done - {result.FilesChanged.Count} files changed");
        }
        catch (OperationCanceledException)
        {
            _observer.AddUserMessage("Cancelled.");
            _mainWindow.SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            _observer.AddUserMessage($"Error: {ex.Message}");
            _mainWindow.SetStatus("Error");
        }
        finally
        {
            _mainWindow.InputField.Enabled = true;
            _mainWindow.InputField.SetFocus();
            _currentCts = null;
        }
    }

    /// <summary>
    /// Execute a colon command.
    /// </summary>
    public void ExecuteCommand(string input)
    {
        var parts = input.Split(' ', 2);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (cmd)
        {
            case ":quit":
            case ":q":
            case ":exit":
                Quit();
                break;

            case ":hide":
            case ":sh":
            case ":shell":
                SpawnShell();
                break;

            case ":model":
                SwitchModel(arg);
                break;

            case ":tokens":
                ShowTokens();
                break;

            case ":history":
                ShowHistory();
                break;

            case ":sessions":
                ShowSessions(arg);
                break;

            case ":skills":
                LoadSkill();
                break;

            case ":diff":
                ShowDiff();
                break;

            case ":files":
                ShowFiles();
                break;

            case ":arena":
                ShowArena();
                break;

            case ":config":
                ShowConfig();
                break;

            case ":reset":
                Reset();
                break;

            case ":cancel":
                Cancel();
                break;

            case ":help":
            case ":h":
                ShowHelp();
                break;

            default:
                _observer?.AddUserMessage($"Unknown command: {cmd}");
                break;
        }
    }

    // --- Command implementations ---

    private void Quit()
    {
        _logger?.Dispose();
        Application.RequestStop();
    }

    private void SpawnShell()
    {
        Application.Shutdown();

        try
        {
            var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
            Console.WriteLine("Spawning shell. Type 'exit' or Ctrl+D to return to little helper.");

            var psi = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                WorkingDirectory = _workingDir
            };

            using var shellProc = Process.Start(psi);
            shellProc?.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start shell: {ex.Message}");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey(true);
        }

        // Re-initialize Terminal.Gui
        Application.Init();
        _mainWindow?.SetFocus();
    }

    private void SwitchModel(string arg)
    {
        if (_mainWindow == null) return;

        ResolvedModel? newModel;

        if (string.IsNullOrEmpty(arg))
        {
            var dialog = new ModelSelectionDialog();
            Application.Run(dialog);
            newModel = dialog.SelectedModel;
        }
        else
        {
            newModel = ModelConfig.Load().Resolve(arg);
            if (newModel == null)
            {
                _observer?.AddUserMessage($"Unknown model: {arg}");
                return;
            }
        }

        if (newModel != null)
        {
            _model = newModel;
            _agent = null; // Will recreate on next prompt
            _observer?.Reset();
            _observer?.AddUserMessage($"Switched to {newModel.ModelId}");
            _mainWindow.SetStatus($"Model: {newModel.ModelId}");
        }
    }

    private void ShowTokens()
    {
        if (_agent == null || _model == null)
        {
            _observer?.AddUserMessage("No conversation yet.");
            return;
        }

        var dialog = new TokensDialog(_agent.History, _model.ContextWindow,
            _observer?.TotalTokens ?? 0, _observer?.TotalThinkingTokens ?? 0);
        Application.Run(dialog);
    }

    private void ShowHistory()
    {
        if (_agent?.History.Count > 0)
        {
            foreach (var msg in _agent.History)
            {
                var content = msg.Content.Length > 100
                    ? msg.Content[..100] + "..."
                    : msg.Content;
                _observer?.AddUserMessage($"[{msg.Role}] {content}");
            }
        }
        else
        {
            _observer?.AddUserMessage("No conversation history yet.");
        }
    }

    private void ShowSessions(string arg)
    {
        if (!string.IsNullOrEmpty(arg) && int.TryParse(arg, out var sessionIdx))
        {
            // Show specific session
            var dialog = new SessionsDialog(sessionIdx);
            Application.Run(dialog);
        }
        else
        {
            // Browse sessions
            var dialog = new SessionsDialog();
            Application.Run(dialog);
        }
    }

    private void LoadSkill()
    {
        var skillContent = SkillBrowser.Browse(_workingDir);
        if (skillContent != null)
        {
            _pendingSkillContent = skillContent;
            _observer?.AddUserMessage("Skill loaded. It will be prepended to your next prompt.");
        }
    }

    private void ShowDiff()
    {
        if (_agent == null)
        {
            _observer?.AddUserMessage("No conversation yet.");
            return;
        }

        var lastFile = _agent.History
            .Where(m => m.Role == "tool" && m.ToolResult?.FilePath != null && !m.ToolResult.IsError)
            .Select(m => m.ToolResult!.FilePath!)
            .LastOrDefault();

        if (lastFile != null)
        {
            var diffView = new DiffView(lastFile);
            _mainWindow?.AddChatView(diffView);
        }
        else
        {
            _observer?.AddUserMessage("No file writes in this session.");
        }
    }

    private void ShowFiles()
    {
        if (_agent == null)
        {
            _observer?.AddUserMessage("No conversation yet.");
            return;
        }

        var files = _agent.History
            .Where(m => m.Role == "tool" && m.ToolResult?.FilePath != null && !m.ToolResult.IsError)
            .Select(m => m.ToolResult!.FilePath!)
            .Distinct()
            .ToList();

        if (files.Count > 0)
        {
            _observer?.AddUserMessage("Files changed this session:");
            foreach (var f in files)
                _observer?.AddUserMessage($"  {f}");
        }
        else
        {
            _observer?.AddUserMessage("No files changed yet.");
        }
    }

    private void ShowArena()
    {
        _observer?.AddUserMessage("Arena mode not yet implemented in Terminal.Gui version.");
    }

    private void ShowConfig()
    {
        _observer?.AddUserMessage("TUI Configuration:");
        _observer?.AddUserMessage($"  Thinking mode: {_config.ThinkingMode}");
        _observer?.AddUserMessage($"  Max steps: {_config.MaxSteps}");
        _observer?.AddUserMessage($"  Git checkpoint: {_config.GitCheckpoint}");
        _observer?.AddUserMessage($"  Auto show diffs: {_config.AutoShowDiffs}");
        _observer?.AddUserMessage($"  Verbose: {_config.Verbose}");
    }

    private void Reset()
    {
        _observer?.Reset();
        _observer?.AddUserMessage("Conversation reset.");
        _agent = null;
        _logger?.Dispose();
        _logger = null;
    }

    private void Cancel()
    {
        if (_currentCts != null && !_currentCts.IsCancellationRequested)
        {
            _currentCts.Cancel();
            _observer?.AddUserMessage("Cancelling...");
        }
    }

    private void ShowHelp()
    {
        _observer?.AddUserMessage("Commands:");
        _observer?.AddUserMessage("  :quit, :q       Exit the application");
        _observer?.AddUserMessage("  :hide, :sh      Spawn a shell (return with exit)");
        _observer?.AddUserMessage("  :model [name]   Switch model");
        _observer?.AddUserMessage("  :tokens         Show token budget");
        _observer?.AddUserMessage("  :history        Show conversation history");
        _observer?.AddUserMessage("  :sessions [N]   Browse or show session #N");
        _observer?.AddUserMessage("  :skills         Browse and load skills");
        _observer?.AddUserMessage("  :diff           Show diff for last file write");
        _observer?.AddUserMessage("  :files          List files changed this session");
        _observer?.AddUserMessage("  :config         Show TUI config");
        _observer?.AddUserMessage("  :reset          Reset conversation");
        _observer?.AddUserMessage("  :cancel         Cancel current agent run");
        _observer?.AddUserMessage("  :help, :h       Show this help");
    }

    // --- Helpers ---

    private Agent CreateAgent()
    {
        if (_model == null)
            throw new InvalidOperationException("No model selected");

        var (client, tools) = ClientFactory.Create(_model, _workingDir, _yoloMode);

        var skills = new SkillDiscovery();
        skills.Discover(_workingDir);

        var agentConfig = new AgentConfig(
            ModelEndpoint: _model.BaseUrl,
            ModelName: _model.ModelId,
            MaxContextTokens: _model.ContextWindow,
            MaxSteps: _config.MaxSteps,
            MaxRetries: 2,
            StallThreshold: 5,
            WorkingDirectory: _workingDir,
            Temperature: _model.Temperature,
            ApiKey: string.IsNullOrEmpty(_model.ApiKey) ? null : _model.ApiKey,
            ExtraHeaders: _model.Headers,
            EnableStreaming: _config.Streaming
        );

        return new Agent(agentConfig, client, tools, skills, _logger, _observer);
    }
}
