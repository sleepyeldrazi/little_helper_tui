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
            // Create controller
            var controller = new TuiController(config, yoloMode);

            // Model selection
            var modelConfig = ModelConfig.Load();
            var hasConfiguredProviders = modelConfig.Providers.Count > 0;

            ResolvedModel? resolved = null;
            var defaultModel = config.DefaultModel ?? modelConfig.DefaultModel;

            if (!string.IsNullOrEmpty(defaultModel))
            {
                resolved = modelConfig.Resolve(defaultModel);
                if (resolved == null && !hasConfiguredProviders)
                {
                    resolved = await ShowEndpointSetupAsync();
                }
                else if (resolved == null)
                {
                    resolved = ShowModelSelection();
                }
            }
            else if (!hasConfiguredProviders)
            {
                resolved = await ShowEndpointSetupAsync();
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

            // Create main window
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

    private static async Task<ResolvedModel?> ShowEndpointSetupAsync()
    {
        // First run setup - show endpoint configuration dialog
        var dialog = new EndpointSetupDialog();
        
        Application.Run(dialog);
        if (dialog.Result != null)
        {
            // Test the connection
            try
            {
                var client = new ModelClient(
                    dialog.Result.BaseUrl,
                    dialog.Result.ModelId,
                    0.3,
                    string.IsNullOrEmpty(dialog.Result.ApiKey) ? null : dialog.Result.ApiKey);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var detected = await client.QueryContextWindow(cts.Token);

                if (detected.HasValue && detected.Value > 0)
                {
                    return dialog.Result with { ContextWindow = detected.Value };
                }
            }
            catch { /* fall through to default */ }

            return dialog.Result;
        }

        return null;
    }

    private static ResolvedModel? ShowModelSelection()
    {
        var dialog = new ModelSelectionDialog();
        
        Application.Run(dialog);
        // Dialog completed
        {
            return dialog.SelectedModel;
        }
        
        return null;
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

/// <summary>
/// First-run endpoint setup dialog.
/// </summary>
public class EndpointSetupDialog : Dialog
{
    public ResolvedModel? Result { get; private set; }

    private TextField _endpointField;
    private TextField _modelField;

    public EndpointSetupDialog()
    {
        Title = "Welcome to little helper";
        Width = Dim.Percent(70);
        Height = 16;

        var introLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Height = 2,
            Text = "Configure your first model endpoint:"
        };

        // Endpoint
        var endpointLabel = new Label
        {
            X = 1,
            Y = 4,
            Text = "Endpoint URL (e.g., http://localhost:11434/v1):"
        };

        _endpointField = new TextField
        {
            X = 1,
            Y = 5,
            Width = Dim.Fill(2),
            Text = "http://localhost:11434/v1"
        };

        // Model
        var modelLabel = new Label
        {
            X = 1,
            Y = 7,
            Text = "Model name (e.g., qwen3:14b):"
        };

        _modelField = new TextField
        {
            X = 1,
            Y = 8,
            Width = Dim.Fill(2),
            Text = "qwen3:14b"
        };

        // Buttons
        var okButton = new Button { Title = "OK", IsDefault = true };
        okButton.Accept += (s, e) =>
        {
            OnOk();
            if (e is HandledEventArgs he) he.Handled = true;
        };

        var cancelButton = new Button { Title = "Cancel" };
        cancelButton.Accept += (s, e) =>
        {
            Application.RequestStop();
            if (e is HandledEventArgs he) he.Handled = true;
        };

        AddButton(okButton);
        AddButton(cancelButton);

        Add(introLabel, endpointLabel, _endpointField, modelLabel, _modelField);
    }

    private void OnOk()
    {
        var endpoint = _endpointField.Text?.Trim() ?? "";
        var model = _modelField.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(endpoint))
            endpoint = "http://localhost:11434/v1";

        if (string.IsNullOrEmpty(model))
            model = "qwen3:14b";

        Result = new ResolvedModel(
            endpoint.TrimEnd('/'),
            model,
            "", // No API key
            32768, // Default context
            0.3);

        Application.RequestStop();
    }
}
