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

        try
        {
            // Resolve model — matches old startup flow exactly:
            // 1. Check tui.json default_model, then models.json default_model
            // 2. If no providers configured → manual entry
            // 3. Otherwise → provider selection picker
            var modelConfig = ModelConfig.Load();
            var hasConfiguredProviders = modelConfig.Providers.Count > 0;
            var defaultModel = config.DefaultModel ?? modelConfig.DefaultModel;

            ResolvedModel? resolved = null;

            if (!string.IsNullOrEmpty(defaultModel))
            {
                resolved = modelConfig.Resolve(defaultModel);
                if (resolved == null && !hasConfiguredProviders)
                    resolved = ShowManualEntry();
                else if (resolved == null)
                    resolved = ShowModelSelection();
            }
            else if (!hasConfiguredProviders)
            {
                resolved = ShowManualEntry();
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

            // Show welcome banner (plain text, like old FigletText + info)
            mainWindow.AppendLine("little helper");
            mainWindow.AppendLine($"Using {resolved.ModelId} ({resolved.BaseUrl})");
            mainWindow.AppendLine("Hint: use :help for the command list");
            mainWindow.AppendLine();

            Application.Run(mainWindow);
            return 0;
        }
        finally
        {
            Application.Shutdown();
        }
    }

    /// <summary>Show provider/model picker dialog. Returns to manual entry if requested.</summary>
    private static ResolvedModel? ShowModelSelection()
    {
        while (true)
        {
            var dialog = new ModelSelectionDialog();
            Application.Run(dialog);

            if (dialog.ShowManualEntry)
            {
                var result = ShowManualEntry();
                if (result != null) return result;
                continue; // back to picker if manual was cancelled
            }

            return dialog.SelectedModel;
        }
    }

    /// <summary>Show manual endpoint/model/apikey entry dialog.</summary>
    private static ResolvedModel? ShowManualEntry()
    {
        var dialog = new ManualModelDialog();
        Application.Run(dialog);
        return dialog.Result;
    }
}
