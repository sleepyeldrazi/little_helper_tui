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

        Application.Init();

        // Set dark color scheme globally
        if (Colors.ColorSchemes.TryGetValue("Toplevel", out var topScheme))
            Colors.ColorSchemes["Toplevel"] = DarkColors.Base;
        if (Colors.ColorSchemes.TryGetValue("Base", out var baseScheme))
            Colors.ColorSchemes["Base"] = DarkColors.Base;
        if (Colors.ColorSchemes.TryGetValue("Dialog", out var dlgScheme))
            Colors.ColorSchemes["Dialog"] = DarkColors.Dialog;

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
            mainWindow.AddColoredBlock("little helper", DarkColors.Assistant);
            mainWindow.AddColoredBlock($"Using {resolved.ModelId} ({resolved.BaseUrl})", DarkColors.Dim);
            mainWindow.AddColoredBlock("Hint: use :help for the command list", DarkColors.Dim);
            mainWindow.AddColoredBlock("", DarkColors.Base);

            Application.Run(mainWindow);
            return 0;
        }
        finally
        {
            Application.Shutdown();
        }
    }

    /// <summary>Show provider setup dialog (first run, no providers configured).</summary>
    private static ResolvedModel? ShowEndpointSetup()
    {
        var dialog = new EndpointSetupDialog();
        Application.Run(dialog);
        return dialog.Result;
    }

    /// <summary>Show model picker. Loops back if manual entry is cancelled.</summary>
    private static ResolvedModel? ShowModelSelection()
    {
        while (true)
        {
            var dialog = new ModelSelectionDialog();
            Application.Run(dialog);

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
