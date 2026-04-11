using System.ComponentModel;
using LittleHelper;
using LittleHelperTui.Dialogs;
using LittleHelperTui.Views;
using Terminal.Gui;

namespace LittleHelperTui;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Parse --yolo flag
        var yoloMode = args.Contains("--yolo") || args.Contains("-y");

        // Load config
        var config = TuiConfig.Load();

        // Initialize Terminal.Gui
        Application.Init();

        try
        {
            // Always show model selection dialog first
            // This shows local models on top if configured
            ResolvedModel? resolved = ShowModelSelection();

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

            // Show welcome banner
            ShowWelcomeBanner(mainWindow, resolved);

            // Run the application
            Application.Run(mainWindow);

            return 0;
        }
        finally
        {
            Application.Shutdown();
        }
    }

    private static ResolvedModel? ShowModelSelection()
    {
        while (true)
        {
            var dialog = new ModelSelectionDialog();
            
            // Run the dialog - this blocks until dialog closes
            Application.Run(dialog);
            
            // Check if user wants manual entry
            if (dialog.ShowManualEntry)
            {
                // Show manual entry dialog
                var manualDialog = new ManualModelDialog();
                Application.Run(manualDialog);
                
                if (manualDialog.Result != null)
                {
                    return manualDialog.Result;
                }
                // If manual dialog was cancelled, loop back to selection
                continue;
            }
            
            // User selected a model or cancelled
            return dialog.SelectedModel;
        }
    }

    private static void ShowWelcomeBanner(MainWindow window, ResolvedModel model)
    {
        var banner = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(),
            Height = 3,
            Text = $@"little helper v0.2.0
Using {model.ModelId} ({model.BaseUrl})
Hint: use :help for the command list"
        };

        // Add banner as first child of chat content
        if (window.ChatContent.Subviews.Count == 0)
        {
            window.ChatContent.Add(banner);
        }
    }
}
