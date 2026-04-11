using System.ComponentModel;
using LittleHelper;
using LittleHelperTui.Views;
using Terminal.Gui;

namespace LittleHelperTui.Dialogs;

/// <summary>
/// First-run provider setup dialog. Matches old EndpointSetup:
/// shows known provider templates so the user can pick one and enter API key / model.
/// </summary>
public class EndpointSetupDialog : Dialog
{
    public ResolvedModel? Result { get; private set; }

    private record ProviderTemplate(
        string Name, string DisplayName, string BaseUrl, string ApiType,
        string AuthType, string DefaultModel, int ContextWindow,
        Dictionary<string, string>? Headers = null, string? Hint = null);

    private static readonly List<ProviderTemplate> Templates = new()
    {
        new("ollama", "Ollama (local)", "http://localhost:11434/v1", "openai", "bearer",
            "qwen3:14b", 32768, Hint: "Make sure Ollama is running"),
        new("lm-studio", "LM Studio (local)", "http://localhost:1234/v1", "openai", "bearer",
            "default", 32768, Hint: "Start LM Studio's local server first"),
        new("llama-cpp", "llama.cpp server (local)", "http://localhost:8080/v1", "openai", "bearer",
            "default", 32768, Hint: "Run llama-server with --port 8080"),

        new("openai", "OpenAI (GPT-4o, o3, ...)", "https://api.openai.com/v1", "openai", "bearer",
            "gpt-4o", 128000),
        new("anthropic", "Anthropic (Claude Sonnet, Opus, ...)", "https://api.anthropic.com", "anthropic", "x-api-key",
            "claude-sonnet-4-20250514", 200000),
        new("openrouter", "OpenRouter (multi-provider)", "https://openrouter.ai/api/v1", "openai", "bearer",
            "anthropic/claude-sonnet-4-20250514", 200000),
        new("kimi", "Kimi (Moonshot)", "https://api.kimi.com/coding", "anthropic", "x-api-key",
            "kimi-for-coding", 131072, Headers: new() { ["User-Agent"] = "claude-cli/1.0.0" }),
        new("groq", "Groq (fast inference)", "https://api.groq.com/openai/v1", "openai", "bearer",
            "llama-3.3-70b-versatile", 131072),
        new("minimax", "MiniMax", "https://api.minimax.chat/v1", "openai", "bearer",
            "MiniMax-M1", 1048576),
        new("z-ai", "Z.AI", "https://api.z.ai/v1", "openai", "bearer",
            "z1-regular", 131072),
        new("opencode", "OpenCode Go", "https://api.opencode.ai/v1", "openai", "bearer",
            "opencode-v1", 131072),

        new("custom", "Custom endpoint", "", "openai", "bearer",
            "", 32768, Hint: "Enter any OpenAI-compatible endpoint"),
    };

    public EndpointSetupDialog()
    {
        Title = "Welcome to little helper!";
        Width = Dim.Percent(70);
        Height = Dim.Percent(80);
        ColorScheme = DarkColors.Dialog;

        var subtitle = new Label
        {
            X = 1, Y = 1,
            Text = "Let's set up your first model endpoint.\nPick a provider:",
            ColorScheme = DarkColors.Dialog
        };

        var items = Templates.Select(t => t.DisplayName).Append("Skip (enter manually)").ToList();
        var listItems = new System.Collections.ObjectModel.ObservableCollection<string>(items);

        var listView = new ListView
        {
            X = 1, Y = 4,
            Width = Dim.Fill(2),
            Height = Dim.Fill(4),
            Source = new ListWrapper<string>(listItems),
            ColorScheme = DarkColors.Dialog
        };

        listView.OpenSelectedItem += (s, e) => SelectProvider(e.Item);

        var selectButton = new Button { Title = "Select", IsDefault = true };
        selectButton.Accept += (s, e) =>
        {
            SelectProvider(listView.SelectedItem);
            if (e is HandledEventArgs he) he.Handled = true;
        };

        var cancelButton = new Button { Title = "Cancel" };
        cancelButton.Accept += (s, e) =>
        {
            Result = null;
            Application.RequestStop(this);
            if (e is HandledEventArgs he) he.Handled = true;
        };

        AddButton(selectButton);
        AddButton(cancelButton);
        Add(subtitle, listView);
    }

    private void SelectProvider(int index)
    {
        if (index < 0 || index >= Templates.Count + 1) return;

        if (index >= Templates.Count)
        {
            // "Skip (enter manually)"
            var manual = new ManualModelDialog();
            Application.Run(manual);
            Result = manual.Result;
            Application.RequestStop(this);
            return;
        }

        var template = Templates[index];
        bool isLocal = template.Name is "ollama" or "lm-studio" or "llama-cpp";
        bool isCustom = template.Name == "custom";

        if (isCustom)
        {
            var manual = new ManualModelDialog();
            Application.Run(manual);
            Result = manual.Result;
            Application.RequestStop(this);
            return;
        }

        // Show provider-specific setup dialog
        var setup = new ProviderSetupDialog(template.DisplayName, template.BaseUrl,
            template.DefaultModel, isLocal, template.Hint);
        Application.Run(setup);

        if (setup.Endpoint != null && setup.ModelName != null)
        {
            var endpoint = setup.Endpoint.TrimEnd('/');
            var apiKey = setup.ApiKey ?? "";

            // Save to models.json
            var config = ModelConfig.Load();
            config.Providers[template.Name] = new ProviderConfig
            {
                BaseUrl = endpoint,
                ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey,
                ApiType = template.ApiType,
                AuthType = template.AuthType,
                Headers = template.Headers,
                DefaultContextWindow = template.ContextWindow,
                Models = new List<ModelEntry>
                {
                    new()
                    {
                        Id = setup.ModelName,
                        Name = template.DisplayName.Split('(')[0].Trim(),
                        ContextWindow = template.ContextWindow
                    }
                }
            };
            config.DefaultModel = setup.ModelName;
            config.Save();

            Result = new ResolvedModel(endpoint, setup.ModelName, apiKey,
                template.ContextWindow, 0.3, template.ApiType, template.Headers, template.AuthType);
        }

        Application.RequestStop(this);
    }
}

/// <summary>
/// Provider-specific setup: endpoint URL, model name (with fetch from server), API key.
/// </summary>
public class ProviderSetupDialog : Dialog
{
    public string? Endpoint { get; private set; }
    public string? ModelName { get; private set; }
    public string? ApiKey { get; private set; }

    public ProviderSetupDialog(string providerName, string defaultUrl, string defaultModel,
        bool isLocal, string? hint = null)
    {
        Title = $"Set up {providerName}";
        Width = Dim.Percent(60);
        Height = isLocal ? 16 : 18;
        ColorScheme = DarkColors.Dialog;

        int y = 1;

        if (hint != null)
        {
            var hintLabel = new Label { X = 1, Y = y, Text = hint };
            Add(hintLabel);
            y += 2;
        }

        var urlLabel = new Label { X = 1, Y = y, Text = "Endpoint URL:" };
        var urlField = new TextField { X = 1, Y = y + 1, Width = Dim.Fill(2), Text = defaultUrl };
        y += 3;

        TextField? apiKeyField = null;
        if (!isLocal)
        {
            var apiKeyLabel = new Label { X = 1, Y = y, Text = "API key:" };
            apiKeyField = new TextField { X = 1, Y = y + 1, Width = Dim.Fill(2), Secret = true, Text = "" };
            Add(apiKeyLabel, apiKeyField);
            y += 3;
        }

        var modelLabel = new Label { X = 1, Y = y, Text = "Model:" };
        var modelField = new TextField { X = 1, Y = y + 1, Width = Dim.Fill(16), Text = defaultModel };
        var fetchButton = new Button { X = Pos.AnchorEnd(15), Y = y + 1, Title = "Fetch List" };
        y += 3;

        var modelListView = new ListView
        {
            X = 1, Y = y,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
            Visible = false,
            ColorScheme = DarkColors.Dialog
        };

        fetchButton.Accept += async (s, e) =>
        {
            if (e is HandledEventArgs he) he.Handled = true;
            var url = urlField.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(url)) return;

            fetchButton.Title = "Fetching...";
            fetchButton.Enabled = false;
            Application.Refresh();

            try
            {
                var key = apiKeyField?.Text?.Trim();
                var models = await ModelClient.FetchAvailableModels(
                    url.TrimEnd('/'), string.IsNullOrEmpty(key) ? null : key);

                Application.Invoke(() =>
                {
                    fetchButton.Title = "Fetch List";
                    fetchButton.Enabled = true;

                    if (models.Count == 0)
                    {
                        MessageBox.ErrorQuery("No Models", "Could not fetch models from endpoint.", "OK");
                        return;
                    }

                    var items = new System.Collections.ObjectModel.ObservableCollection<string>(models);
                    modelListView.Source = new ListWrapper<string>(items);
                    modelListView.Visible = true;
                    modelListView.SetFocus();
                });
            }
            catch
            {
                Application.Invoke(() =>
                {
                    fetchButton.Title = "Fetch List";
                    fetchButton.Enabled = true;
                    MessageBox.ErrorQuery("Error", "Failed to connect to endpoint.", "OK");
                });
            }
        };

        modelListView.OpenSelectedItem += (s, e) =>
        {
            if (e.Item >= 0 && modelListView.Source != null)
            {
                modelField.Text = modelListView.Source.ToList()[e.Item]?.ToString() ?? "";
                modelListView.Visible = false;
                modelField.SetFocus();
            }
        };

        var okButton = new Button { Title = "OK", IsDefault = true };
        okButton.Accept += (s, e) =>
        {
            Endpoint = urlField.Text?.Trim() ?? defaultUrl;
            ModelName = modelField.Text?.Trim() ?? defaultModel;
            ApiKey = apiKeyField?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(Endpoint)) Endpoint = defaultUrl;
            if (string.IsNullOrEmpty(ModelName)) ModelName = defaultModel;
            Application.RequestStop(this);
            if (e is HandledEventArgs he) he.Handled = true;
        };

        var cancelButton = new Button { Title = "Cancel" };
        cancelButton.Accept += (s, e) =>
        {
            Endpoint = null;
            ModelName = null;
            Application.RequestStop(this);
            if (e is HandledEventArgs he) he.Handled = true;
        };

        AddButton(okButton);
        AddButton(cancelButton);
        Add(urlLabel, urlField, modelLabel, modelField, fetchButton, modelListView);
    }
}
