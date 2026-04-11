using LittleHelper;
using LittleHelperTui.Dialogs;
using LittleHelperTui.Views;
using Terminal.Gui;

namespace LittleHelperTui;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var yoloMode = args.Contains("--yolo") || args.Contains("-y");
        var config = TuiConfig.Load();

        // Ensure truecolor is available (Terminal.Gui may not recognize xterm-ghostty etc.)
        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM") ?? "";
        if (string.IsNullOrEmpty(colorTerm))
            Environment.SetEnvironmentVariable("COLORTERM", "truecolor");

        Application.Init();
        Application.Force16Colors = false;

        // Set dark color scheme globally for all built-in widget types
        var dark = DarkColors.Base;
        var dialog = DarkColors.Dialog;
        foreach (var key in Colors.ColorSchemes.Keys.ToList())
        {
            Colors.ColorSchemes[key] = key == "Dialog" ? dialog : dark;
        }

        try
        {
            // Resolve model — matches old startup flow:
            // 1. Check tui.json default_model, then models.json default_model
            // 2. If no providers configured → EndpointSetup (provider picker)
            // 3. Otherwise → model selection picker
            var modelConfig = ModelConfig.Load();
            var hasConfiguredProviders = modelConfig.Providers.Count > 0;
            var defaultModel = config.DefaultModel ?? modelConfig.DefaultModel;

            ResolvedModel? resolved = null;

            if (!string.IsNullOrEmpty(defaultModel))
            {
                resolved = modelConfig.Resolve(defaultModel);
                if (resolved == null && !hasConfiguredProviders)
                    resolved = ShowEndpointSetup();
                else if (resolved == null)
                    resolved = ShowModelSelection();
            }
            else if (!hasConfiguredProviders)
            {
                // First run — show provider setup (like old EndpointSetup)
                resolved = ShowEndpointSetup();
            }
            else
            {
                resolved = ShowModelSelection();
            }

            if (resolved == null)
            {
                Application.Shutdown();
                return 1;
            }

            // Create controller and main window
            var controller = new TuiController(config, yoloMode);
            var mainWindow = new MainWindow(controller);
            controller.SetMainWindow(mainWindow);
            controller.SetModel(resolved);

            // Show welcome banner (dim, like old Spectre)
            mainWindow.AddColoredBlock("little helper", DarkColors.AssistantBorder);
            mainWindow.AddColoredBlock($"Using {resolved.ModelId} ({resolved.BaseUrl})", DarkColors.Dim);
            mainWindow.AddColoredBlock("Detecting context window...", DarkColors.Dim);
            mainWindow.AddColoredBlock("Hint: use :help for the command list", DarkColors.Dim);
            mainWindow.AddColoredBlock("", DarkColors.Base);

            // Detect context window in background once event loop is running
            var resolvedCopy = resolved;
            mainWindow.Ready += (s, e) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var detected = await DetectContextWindowAsync(resolvedCopy);
                        Application.Invoke(() =>
                        {
                            controller.SetModel(detected);
                            var ctxK = detected.ContextWindow >= 1024
                                ? $"{detected.ContextWindow / 1024}K"
                                : $"{detected.ContextWindow}";
                            mainWindow.AddColoredBlock($"Context window: {ctxK} tokens", DarkColors.Dim);
                        });
                    }
                    catch { }
                });
            };

            Application.Run(mainWindow);
            return 0;
        }
        finally
        {
            Application.Shutdown();
        }
    }

    /// <summary>
    /// Auto-detect context window from server if the config value looks like a default.
    /// Priority: 1) models.json value (if explicitly set/non-default) 2) server query 3) fallback
    /// </summary>
    private static async Task<ResolvedModel> DetectContextWindowAsync(ResolvedModel resolved)
    {
        // If models.json has a non-default context window, trust it
        if (resolved.ContextWindow != 32768)
            return resolved;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using IModelClient client = resolved.ApiType == "anthropic"
                ? new AnthropicClient(
                    resolved.BaseUrl, resolved.ModelId, resolved.Temperature,
                    string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
                    resolved.Headers, resolved.AuthType)
                : new ModelClient(
                    resolved.BaseUrl, resolved.ModelId, resolved.Temperature,
                    string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
                    resolved.Headers);

            var detected = await client.QueryContextWindow(cts.Token);
            if (detected.HasValue && detected.Value > 0)
                return resolved with { ContextWindow = detected.Value };
        }
        catch
        {
            // Detection failed, use config value
        }

        return resolved;
    }

    /// <summary>Show provider setup dialog (first run, no providers configured).</summary>
    private static ResolvedModel? ShowEndpointSetup()
    {
        var dialog = new EndpointSetupDialog();
        Application.Run(dialog);
        return dialog.Result;
    }

    /// <summary>Show model picker. Loops back if manual/endpoint entry is cancelled.</summary>
    private static ResolvedModel? ShowModelSelection()
    {
        while (true)
        {
            var dialog = new ModelSelectionDialog();
            Application.Run(dialog);

            if (dialog.ShowEndpointSetup)
            {
                var result = ShowEndpointSetup();
                if (result != null) return result;
                continue;
            }

            if (dialog.ShowManualEntry)
            {
                var manual = new ManualModelDialog();
                Application.Run(manual);
                if (manual.Result != null) return manual.Result;
                continue;
            }

            return dialog.SelectedModel;
        }
    }
}
