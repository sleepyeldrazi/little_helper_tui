using System.Diagnostics;
using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

class Program
{
    private static string? _pendingSkillContent = null;
    private static TuiConfig _tuiConfig = new();
    private static GitCheckpoint? _gitCheckpoint;
    private static int _lastConsoleWidth = Console.WindowWidth;
    private static bool _yoloMode = false;  // Allow writes outside working directory

    // Alternate screen buffer: preserves user's terminal history on exit
    // ?1047h = switch to alt buffer (keeps scrollback) — works on most modern terminals
    // ?1049h = switch + clear — needed for Terminal.app which doesn't support scrollback in alt buffer
    // Ghostty, iTerm2, Alacritty, WezTerm all support ?1047h properly
    private static readonly bool IsMacTerminal = Environment.GetEnvironmentVariable("TERM_PROGRAM") == "Apple_Terminal";

    private static string EnterAltBuffer => IsMacTerminal
        ? "\x1b[?1049h"  // Terminal.app: no scrollback, but consistent behavior
        : "\x1b[?1047h\x1b[H\x1b[2J";  // Modern terminals: scrollback supported

    private static string LeaveAltBuffer => IsMacTerminal
        ? "\x1b[?1049l"  // Terminal.app
        : "\x1b[?1047l";  // Modern terminals

    private static void EnterAlternateScreen() => Console.Write(EnterAltBuffer);
    private static void LeaveAlternateScreen() => Console.Write(LeaveAltBuffer);

    /// <summary>Check if terminal width changed and redraw if so.</summary>
    private static void CheckResize(IAnsiConsole console, TuiObserver observer)
    {
        try
        {
            var width = Console.WindowWidth;
            if (width != _lastConsoleWidth)
            {
                _lastConsoleWidth = width;
                observer.Redraw(console);
            }
        }
        catch { /* Console.WindowWidth can throw when no terminal */ }
    }

    static async Task<int> Main(string[] args)
    {
        // Parse --yolo flag
        _yoloMode = args.Contains("--yolo") || args.Contains("-y");

        _tuiConfig = TuiConfig.Load();

        var console = AnsiConsole.Console;

        // Enter alternate screen buffer early — everything below lives in it
        EnterAlternateScreen();
        Console.Out.Flush();
        _lastConsoleWidth = Console.WindowWidth;

        // Initialize git checkpoint service
        _gitCheckpoint = new GitCheckpoint(
            Directory.GetCurrentDirectory(), _tuiConfig.GitCheckpoint, console);
        _gitCheckpoint.EnsureInitialized();

        // Use default model from config if set, otherwise prompt
        ResolvedModel? resolved;
        var modelConfig = ModelConfig.Load();
        var hasConfiguredProviders = modelConfig.Providers.Count > 0;

        // Check tui.json default_model first, then models.json default_model
        var defaultModel = _tuiConfig.DefaultModel ?? modelConfig.DefaultModel;
        if (!string.IsNullOrEmpty(defaultModel))
        {
            resolved = modelConfig.Resolve(defaultModel);
            if (resolved == null && !hasConfiguredProviders)
                resolved = await EndpointSetup.RunAsync(console);
            else if (resolved == null)
                resolved = await ModelSelector.SelectAsync(console);
        }
        else if (!hasConfiguredProviders)
        {
            // First run with no providers — show setup menu
            resolved = await EndpointSetup.RunAsync(console);
        }
        else
        {
            resolved = await ModelSelector.SelectAsync(console);
        }
        if (resolved == null)
        {
            LeaveAlternateScreen();
            return 1;
        }

        // Show banner + model info (now inside alt buffer, survives)
        console.Write(new FigletText("little helper")
            .LeftJustified()
            .Color(Color.Blue));
        console.MarkupLine("[dim]Terminal UI v0.1.0[/]");
        console.MarkupLine($"[green]Using {resolved.ModelId}[/] [dim]({resolved.BaseUrl})[/]");

        // Warn Terminal.app users about scrollback limitations
        if (IsMacTerminal)
        {
            console.MarkupLine("[dim][yellow]Note:[/] Terminal.app has limited scrollback. Use iTerm2 or Ghostty for full scrollback.[/]");
        }
        console.WriteLine();

        var workingDir = Directory.GetCurrentDirectory();
        var modelId = resolved.ModelId;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            console.MarkupLine("[yellow]Use :quit to exit.[/]");
        };

        // Ensure we leave alternate buffer on any exit path
        AppDomain.CurrentDomain.ProcessExit += (_, _) => LeaveAlternateScreen();

        var observer = new TuiObserver(_tuiConfig);
        Agent? agent = null;
        SessionLogger? logger = null;

        // Helper to create/recreate the agent when model changes or on reset
        Agent CreateAgent() => ClientFactory.CreateAgent(resolved!, workingDir, observer, _tuiConfig, logger, _yoloMode);

        while (true)
        {
            CheckResize(console, observer);
            observer.Drain(console);
            observer.Record(c => c.MarkupLine("[dim]──[/]"));

            var input = InputHandler.ReadLine(console);
            if (input == null) { console.MarkupLine("[dim]Goodbye![/]"); break; }

            input = input.Trim();
            if (string.IsNullOrEmpty(input)) continue;

            // Commands
            if (input.StartsWith(':'))
            {
                var cmdResult = await HandleCommand(
                    input, console, resolved, observer, agent, workingDir);
                if (cmdResult.Result == CmdResult.Quit) break;
                if (cmdResult.ResetAgent)
                {
                    observer = new TuiObserver(_tuiConfig);
                    agent = null;
                    if (cmdResult.DisposeLogger)
                    {
                        logger?.Dispose();
                        logger = null;
                    }
                }
                if (cmdResult.NewModel != null)
                {
                    resolved = cmdResult.NewModel;
                    agent = null;
                    // Reset observer on model switch -- don't carry over tokens/steps
                    observer = new TuiObserver(_tuiConfig);
                }
                continue;
            }

            // Create agent on first prompt or after reset/model switch
            agent ??= CreateAgent();

            // Create session logger on first prompt (persists until :reset or model switch)
            logger ??= new SessionLogger(modelId, workingDir);

            // Run agent
            console.WriteLine();

            var effectiveInput = input;
            if (_pendingSkillContent != null)
            {
                effectiveInput = $"{_pendingSkillContent}\n\n---\n\n{input}";
                _pendingSkillContent = null;
            }

            using var cts = new CancellationTokenSource();

            // Clear the raw input lines that InputHandler echoed during typing
            // so only the formatted panel remains. +1 for the ── separator line.
            var inputLineCount = InputHandler.LastRenderedLineCount + 1;
            for (int i = 0; i < inputLineCount; i++)
                Console.Write("\u001b[1A\r\u001b[K");
            Console.Write("\r\u001b[K");

            var userPanel = new Panel(Markup.Escape(input))
                .Header("[green]You[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Green)
                .Expand();
            observer.Record(c =>
            {
                c.Write(userPanel);
                c.WriteLine();
            });

            // Set up tool interceptor for git checkpoints + diff snapshots
            agent.Control.ToolInterceptor = call =>
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
                                var fullPath = Path.GetFullPath(Path.Combine(workingDir, path));
                                // Git checkpoint before write
                                _gitCheckpoint?.CheckpointBeforeWrite(fullPath);
                                // Diff snapshot before write
                                DiffViewer.Snapshot(fullPath);
                            }
                        }
                    }
                    catch { }
                }
                return call;
            };

            var sw = Stopwatch.StartNew();
            AgentResult? result2 = null;
            var agentRef = agent;

            // Safety net: capture any remaining Console.Error output from core
            // (most errors now route through observer.OnError, but fallback Console.Error
            // calls still exist for when no observer is available)
            var originalStderr = Console.Error;
            var stderrCapture = new StringWriter();
            Console.SetError(stderrCapture);

            try
            {
                await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"Running {resolved!.ModelId}...", async ctx =>
                    {
                        // Multi-turn: preserve history for follow-up turns
                        var isFirstTurn = agentRef.History.Count == 0;
                        var task = agentRef.RunAsync(effectiveInput, cts.Token,
                            clearHistory: isFirstTurn);
                        while (!task.IsCompleted)
                        {
                            CheckResize(console, observer);
                            observer.Drain(console);
                            var preview = observer.StreamingPreview;
                            var status = string.IsNullOrEmpty(preview)
                                ? $"Running {resolved!.ModelId}... Step {observer.CurrentStep}"
                                : Markup.Escape(preview);
                            ctx.Status(status);
                            await Task.Delay(100, cts.Token);
                        }
                        result2 = await task;
                    });
            }
            catch (OperationCanceledException)
            {
                console.MarkupLine("[yellow]Cancelled.[/]");
                console.WriteLine();
                continue;
            }
            finally
            {
                Console.SetError(originalStderr);
            }

            // Show captured stderr as clean error messages
            // (don't deduplicate -- repeated errors are meaningful)
            var stderrLines = stderrCapture.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Take(10)
                .ToList();
            foreach (var line in stderrLines)
            {
                var msg = line.Length > 200 ? line[..200] + "..." : line;
                observer.Record(c => c.MarkupLine($"[red][stderr] {Markup.Escape(msg)}[/]"));
            }

            sw.Stop();
            observer.Drain(console);

            if (result2 != null)
            {
                // Capture values for the closure
                var doneStep = observer.CurrentStep;
                var doneMs = sw.ElapsedMilliseconds;
                var doneResult = result2;
                observer.Record(c => StatusBar.RenderDone(c, modelId, doneResult, doneStep, doneMs, observer));

                // Auto-show diffs if configured and files were changed
                if (_tuiConfig.AutoShowDiffs && result2.FilesChanged.Count > 0)
                {
                    var lastFile = result2.FilesChanged.LastOrDefault();
                    if (lastFile != null)
                        observer.Record(c => DiffViewer.ShowLastDiff(c, lastFile));
                }
            }
        }

        logger?.Dispose();
        LeaveAlternateScreen();
        return 0;
    }

    private enum CmdResult { Continue, Quit, Reset }

    private record CmdHandleResult(CmdResult Result, ResolvedModel? NewModel = null,
        bool ResetAgent = false, bool DisposeLogger = false);

    private static async Task<CmdHandleResult> HandleCommand(
        string input, IAnsiConsole console, ResolvedModel? resolved,
        TuiObserver observer, Agent? agent, string workingDir)
    {
        var parts = input.Split(' ', 2);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (cmd)
        {
            case ":quit" or ":q" or ":exit":
                LeaveAlternateScreen();
                console.MarkupLine("[dim]Goodbye![/]");
                return new CmdHandleResult(CmdResult.Quit);

            case ":hide" or ":sh" or ":shell":
                // Drop back to the user's terminal, come back to same state
                LeaveAlternateScreen();
                Console.Out.Flush();

                var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
                console.MarkupLine("[dim]Spawning shell. Type 'exit' or Ctrl+D to return to little helper.[/]");
                Console.Out.Flush();

                var psi = new ProcessStartInfo(shell)
                {
                    UseShellExecute = false,
                    WorkingDirectory = workingDir
                };
                try
                {
                    var shellProc = Process.Start(psi);
                    shellProc?.WaitForExit();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to start shell: {ex.Message}");
                }

                // Re-enter alternate buffer and redraw
                EnterAlternateScreen();
                Console.Out.Flush();
                _lastConsoleWidth = Console.WindowWidth;
                observer.Redraw(console);
                return new CmdHandleResult(CmdResult.Continue);

            case ":model":
                ResolvedModel? newResolved;
                if (string.IsNullOrEmpty(arg))
                {
                    newResolved = await ModelSelector.SelectAsync(console);
                }
                else
                {
                    newResolved = ModelConfig.Load().Resolve(arg);
                    if (newResolved == null)
                        console.MarkupLine($"[red]Unknown model: {Markup.Escape(arg)}[/]");
                }
                if (newResolved != null)
                    console.MarkupLine($"[green]Switched to {newResolved.ModelId}[/]");
                return new CmdHandleResult(CmdResult.Continue, newResolved);

            case ":tokens":
                if (agent != null && resolved != null)
                    TokenBudget.Render(console, agent.History, resolved.ContextWindow, observer.TotalTokens, observer.TotalThinkingTokens);
                else
                    console.MarkupLine("[dim]No conversation yet.[/]");
                return new CmdHandleResult(CmdResult.Continue);

            case ":history":
                if (agent?.History.Count > 0) RenderHistory(console, agent.History);
                else console.MarkupLine("[dim]No conversation history yet.[/]");
                return new CmdHandleResult(CmdResult.Continue);

            case ":sessions":
                if (!string.IsNullOrEmpty(arg) && int.TryParse(arg, out var sessionIdx))
                    SessionManager.ShowSession(console, sessionIdx);
                else
                    SessionManager.BrowseSessions(console);
                return new CmdHandleResult(CmdResult.Continue);

            case ":skills":
                var skillContent = SkillBrowser.Browse(console, workingDir);
                if (skillContent != null)
                {
                    _pendingSkillContent = skillContent;
                    console.MarkupLine("[dim]Skill loaded. It will be prepended to your next prompt.[/]");
                }
                return new CmdHandleResult(CmdResult.Continue);

            case ":diff":
                if (agent != null)
                {
                    var lastFile = agent.History
                        .Where(m => m.Role == "tool" && m.ToolResult?.FilePath != null && !m.ToolResult.IsError)
                        .Select(m => m.ToolResult!.FilePath!)
                        .LastOrDefault();
                    if (lastFile != null) DiffViewer.ShowLastDiff(console, lastFile);
                    else console.MarkupLine("[dim]No file writes in this session.[/]");
                }
                else console.MarkupLine("[dim]No conversation yet.[/]");
                return new CmdHandleResult(CmdResult.Continue);

            case ":files":
                if (agent != null)
                {
                    var files = agent.History
                        .Where(m => m.Role == "tool" && m.ToolResult?.FilePath != null && !m.ToolResult.IsError)
                        .Select(m => m.ToolResult!.FilePath!)
                        .Distinct()
                        .ToList();
                    if (files.Count > 0)
                    {
                        console.MarkupLine("[bold]Files changed this session:[/]");
                        foreach (var f in files)
                            console.MarkupLine($"  [blue]{Markup.Escape(f)}[/]");
                        console.WriteLine();
                    }
                    else console.MarkupLine("[dim]No files changed yet.[/]");
                }
                else console.MarkupLine("[dim]No conversation yet.[/]");
                return new CmdHandleResult(CmdResult.Continue);

            case ":arena":
                console.MarkupLine("[bold]Select two models for arena mode:[/]");
                console.MarkupLine("[dim]Model 1:[/]");
                var m1 = await ModelSelector.SelectAsync(console);
                if (m1 == null) return new CmdHandleResult(CmdResult.Continue);
                console.MarkupLine("[dim]Model 2:[/]");
                var m2 = await ModelSelector.SelectAsync(console);
                if (m2 == null) return new CmdHandleResult(CmdResult.Continue);
                var arenaPrompt = console.Prompt(new TextPrompt<string>("[bold]Arena prompt:[/]"));
                await ModelArena.RunArena(console, arenaPrompt, m1, m2, workingDir, _tuiConfig);
                return new CmdHandleResult(CmdResult.Continue);

            case ":config":
                console.MarkupLine("[bold]TUI Config[/] [dim](~/.little_helper/tui.json)[/]");
                console.MarkupLine($"  [blue]thinking_mode[/]:       {_tuiConfig.ThinkingMode}");
                console.MarkupLine($"  [blue]show_token_budget[/]:   {_tuiConfig.ShowTokenBudget}");
                console.MarkupLine($"  [blue]auto_show_diffs[/]:      {_tuiConfig.AutoShowDiffs}");
                console.MarkupLine($"  [blue]max_tool_output_lines[/]: {_tuiConfig.MaxToolOutputLines}");
                console.MarkupLine($"  [blue]max_steps[/]:           {_tuiConfig.MaxSteps}");
                console.MarkupLine($"  [blue]default_model[/]:       {_tuiConfig.DefaultModel ?? "(none)"}");
                console.MarkupLine($"  [blue]streaming[/]:           {_tuiConfig.Streaming}");
                console.MarkupLine($"  [blue]git_checkpoint[/]:      {_tuiConfig.GitCheckpoint}");
                console.MarkupLine($"  [blue]theme[/]:               {_tuiConfig.Theme}");
                console.MarkupLine($"  [blue]verbose[/]:             {_tuiConfig.Verbose}");
                console.WriteLine();
                console.MarkupLine("[dim]Edit ~/.little_helper/tui.json to change settings.[/]");
                return new CmdHandleResult(CmdResult.Continue);

            case ":reset":
                agent?.ClearHistory();
                console.MarkupLine("[dim]Conversation reset.[/]");
                return new CmdHandleResult(CmdResult.Reset, ResetAgent: true, DisposeLogger: true);

            case ":yolo":
                _yoloMode = !_yoloMode;
                var status = _yoloMode ? "[green]enabled[/]" : "[dim]disabled[/]";
                console.MarkupLine($"Yolo mode {status}. Agent can now write outside working directory.");
                // Recreate agent with new yolo setting
                return new CmdHandleResult(CmdResult.Reset, ResetAgent: true, DisposeLogger: false);

            case ":help":
                console.MarkupLine("[bold]Commands:[/]");
                console.MarkupLine($"  [blue]:model[/] {Markup.Escape("[name]")}   Switch model");
                console.MarkupLine("  [blue]:tokens[/]         Show token budget");
                console.MarkupLine("  [blue]:history[/]        Show conversation history");
                console.MarkupLine($"  [blue]:sessions[/] {Markup.Escape("[N]")}   Browse sessions / show session #N");
                console.MarkupLine("  [blue]:skills[/]         Browse and inject skills");
                console.MarkupLine("  [blue]:diff[/]           Show diff for last file write");
                console.MarkupLine("  [blue]:files[/]          List files changed this session");
                console.MarkupLine("  [blue]:arena[/]          A/B test two models side-by-side");
                console.MarkupLine("  [blue]:config[/]         Show TUI config");
                console.MarkupLine("  [blue]:reset[/]          Reset conversation");
                console.MarkupLine("  [blue]:yolo[/]           Toggle write-outside-workdir mode");
                console.MarkupLine("  [blue]:help[/]           Show this help");
                console.MarkupLine("  [blue]:hide[/]           Drop to shell, return with 'exit'");
                console.MarkupLine("  [blue]:quit[/]           Exit");
                console.WriteLine();
                console.MarkupLine("[dim]During agent run: Ctrl+C = cancel[/]");
                console.MarkupLine("[dim]Start with --yolo to enable write-outside-workdir from the start[/]");
                return new CmdHandleResult(CmdResult.Continue);

            default:
                console.MarkupLine($"[red]Unknown command: {Markup.Escape(cmd)}[/]  Type [blue]:help[/] for commands");
                return new CmdHandleResult(CmdResult.Continue);
        }
    }

    private static void RenderHistory(IAnsiConsole console, IReadOnlyList<ChatMessage> history)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Role").AddColumn("Content");

        foreach (var msg in history.TakeLast(20))
        {
            var content = msg.Content ?? "(tool calls)";
            if (content.Length > 80) content = content[..80] + "...";
            var roleColor = msg.Role switch { "system" => "blue", "user" => "green", "assistant" => "teal", "tool" => "yellow", _ => "white" };
            table.AddRow($"[{roleColor}]{msg.Role}[/]", Markup.Escape(content));
        }

        console.Write(table);
        console.WriteLine();
    }
}