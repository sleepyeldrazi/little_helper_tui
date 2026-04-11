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

        _gitCheckpoint = new GitCheckpoint(_workingDir, config.GitCheckpoint);
        _gitCheckpoint.EnsureInitialized();
    }

    public void SetMainWindow(MainWindow window)
    {
        _mainWindow = window;
        _observer = new TerminalGuiObserver(window, _config);
    }

    public void SetModel(ResolvedModel model)
    {
        _model = model;
        _mainWindow?.SetStatus($"Model: {model.ModelId}");
    }

    /// <summary>Submit a user prompt to the agent.</summary>
    public async void SubmitPrompt(string text)
    {
        if (_mainWindow == null || _observer == null || _model == null)
            return;

        // Render user message panel
        _observer.AddUserMessage(text);

        // Create session logger if needed
        _logger ??= new SessionLogger(_model.ModelId, _workingDir);

        // Create agent if needed
        _agent ??= CreateAgent();

        // Prepare input with pending skill
        var effectiveInput = text;
        if (_pendingSkillContent != null)
        {
            effectiveInput = $"{_pendingSkillContent}\n\n---\n\n{text}";
            _pendingSkillContent = null;
        }

        _currentCts = new CancellationTokenSource();
        var cts = _currentCts;
        var sw = Stopwatch.StartNew();

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

            var isFirstTurn = _agent.History.Count == 0;
            var result = await _agent.RunAsync(effectiveInput, cts.Token, clearHistory: isFirstTurn);

            sw.Stop();

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
                    DiffViewer.ShowLastDiff(_mainWindow, lastFile);
            }

            // no-op: status shown inline by observer
        }
        catch (OperationCanceledException)
        {
            _mainWindow.AddColoredBlock("Cancelled.");
            // _mainWindow.SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            _mainWindow.AddColoredBlock($"Error: {ex.Message}");
            // _mainWindow.SetStatus("Error");
        }
        finally
        {
            _mainWindow.InputField.Enabled = true;
            _mainWindow.InputField.SetFocus();
            _currentCts = null;
        }
    }

    /// <summary>Execute a colon command.</summary>
    public void ExecuteCommand(string input)
    {
        var parts = input.Split(' ', 2);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (cmd)
        {
            case ":quit" or ":q" or ":exit":
                Quit();
                break;
            case ":hide" or ":sh" or ":shell":
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
            case ":config":
                ShowConfig();
                break;
            case ":reset":
                Reset();
                break;
            case ":cancel":
                Cancel();
                break;
            case ":help" or ":h":
                ShowHelp();
                break;
            default:
                _mainWindow?.AddColoredBlock($"Unknown command: {cmd}  Type :help for commands");
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

            if (dialog.ShowManualEntry)
            {
                var manualDialog = new ManualModelDialog();
                Application.Run(manualDialog);
                newModel = manualDialog.Result;
            }
            else
            {
                newModel = dialog.SelectedModel;
            }
        }
        else
        {
            newModel = ModelConfig.Load().Resolve(arg);
            if (newModel == null)
            {
                _mainWindow.AddColoredBlock($"Unknown model: {arg}");
                return;
            }
        }

        if (newModel != null)
        {
            _model = newModel;
            _agent = null;
            _observer?.Reset();
            _mainWindow.AddColoredBlock($"Switched to {newModel.ModelId}");
            // _mainWindow.SetStatus($"Model: {newModel.ModelId}");
        }
    }

    private void ShowTokens()
    {
        if (_agent == null || _model == null)
        {
            _mainWindow?.AddColoredBlock("No conversation yet.");
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
            _mainWindow?.AddColoredBlock("History (last 20):");
            foreach (var msg in _agent.History.TakeLast(20))
            {
                var content = msg.Content ?? "(tool calls)";
                if (content.Length > 80) content = content[..80] + "...";
                content = content.Replace("\n", " ").Trim();
                _mainWindow?.AddColoredBlock($"  [{msg.Role}] {content}");
            }
            _mainWindow?.AddColoredBlock("");
        }
        else
        {
            _mainWindow?.AddColoredBlock("No conversation history yet.");
        }
    }

    private void ShowSessions(string arg)
    {
        if (!string.IsNullOrEmpty(arg) && int.TryParse(arg, out var sessionIdx))
        {
            var dialog = new SessionsDialog(sessionIdx - 1); // 1-based to 0-based
            Application.Run(dialog);
        }
        else
        {
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
            _mainWindow?.AddColoredBlock("Skill loaded. It will be prepended to your next prompt.");
        }
    }

    private void ShowDiff()
    {
        if (_agent == null)
        {
            _mainWindow?.AddColoredBlock("No conversation yet.");
            return;
        }

        var lastFile = _agent.History
            .Where(m => m.Role == "tool" && m.ToolResult?.FilePath != null && !m.ToolResult.IsError)
            .Select(m => m.ToolResult!.FilePath!)
            .LastOrDefault();

        if (lastFile != null)
            DiffViewer.ShowLastDiff(_mainWindow!, lastFile);
        else
            _mainWindow?.AddColoredBlock("No file writes in this session.");
    }

    private void ShowFiles()
    {
        if (_agent == null)
        {
            _mainWindow?.AddColoredBlock("No conversation yet.");
            return;
        }

        var files = _agent.History
            .Where(m => m.Role == "tool" && m.ToolResult?.FilePath != null && !m.ToolResult.IsError)
            .Select(m => m.ToolResult!.FilePath!)
            .Distinct()
            .ToList();

        if (files.Count > 0)
        {
            _mainWindow?.AddColoredBlock("Files changed this session:");
            foreach (var f in files)
                _mainWindow?.AddColoredBlock($"  {f}");
            _mainWindow?.AddColoredBlock("");
        }
        else
        {
            _mainWindow?.AddColoredBlock("No files changed yet.");
        }
    }

    private void ShowConfig()
    {
        _mainWindow?.AddColoredBlock("TUI Config (~/.little_helper/tui.json):");
        _mainWindow?.AddColoredBlock($"  thinking_mode:        {_config.ThinkingMode}");
        _mainWindow?.AddColoredBlock($"  show_token_budget:    {_config.ShowTokenBudget}");
        _mainWindow?.AddColoredBlock($"  auto_show_diffs:      {_config.AutoShowDiffs}");
        _mainWindow?.AddColoredBlock($"  max_tool_output_lines: {_config.MaxToolOutputLines}");
        _mainWindow?.AddColoredBlock($"  max_steps:            {_config.MaxSteps}");
        _mainWindow?.AddColoredBlock($"  default_model:        {_config.DefaultModel ?? "(none)"}");
        _mainWindow?.AddColoredBlock($"  streaming:            {_config.Streaming}");
        _mainWindow?.AddColoredBlock($"  git_checkpoint:       {_config.GitCheckpoint}");
        _mainWindow?.AddColoredBlock($"  theme:                {_config.Theme}");
        _mainWindow?.AddColoredBlock($"  verbose:              {_config.Verbose}");
        _mainWindow?.AddColoredBlock("");
    }

    private void Reset()
    {
        _observer?.Reset();
        _agent = null;
        _logger?.Dispose();
        _logger = null;
        _mainWindow?.AddColoredBlock("Conversation reset.");
    }

    private void Cancel()
    {
        if (_currentCts != null && !_currentCts.IsCancellationRequested)
        {
            _currentCts.Cancel();
            _mainWindow?.AddColoredBlock("Cancelling...");
        }
    }

    private void ShowHelp()
    {
        _mainWindow?.AddColoredBlock("Commands:");
        _mainWindow?.AddColoredBlock("  :model [name]   Switch model");
        _mainWindow?.AddColoredBlock("  :tokens         Show token budget");
        _mainWindow?.AddColoredBlock("  :history        Show conversation history");
        _mainWindow?.AddColoredBlock("  :sessions [N]   Browse sessions / show session #N");
        _mainWindow?.AddColoredBlock("  :skills         Browse and inject skills");
        _mainWindow?.AddColoredBlock("  :diff           Show diff for last file write");
        _mainWindow?.AddColoredBlock("  :files          List files changed this session");
        _mainWindow?.AddColoredBlock("  :config         Show TUI config");
        _mainWindow?.AddColoredBlock("  :reset          Reset conversation");
        _mainWindow?.AddColoredBlock("  :cancel         Cancel current agent run");
        _mainWindow?.AddColoredBlock("  :hide           Drop to shell, return with 'exit'");
        _mainWindow?.AddColoredBlock("  :quit           Exit");
        _mainWindow?.AddColoredBlock("");
        _mainWindow?.AddColoredBlock("During agent run: Ctrl+C = cancel");
    }

    // --- Helpers ---

    private Agent CreateAgent()
    {
        if (_model == null)
            throw new InvalidOperationException("No model selected");

        return ClientFactory.CreateAgent(_model, _workingDir, _observer!, _config, _logger, _yoloMode);
    }
}
