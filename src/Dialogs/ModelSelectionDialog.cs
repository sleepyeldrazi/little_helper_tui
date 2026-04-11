using System.ComponentModel;
using LittleHelper;
using Terminal.Gui;

namespace LittleHelperTui.Dialogs;

/// <summary>
/// Dialog for selecting a model from configured providers.
/// </summary>
public class ModelSelectionDialog : Dialog
{
    public ResolvedModel? SelectedModel { get; private set; }

    private ListView _listView;
    private List<SelectionItem> _items = new();

    public ModelSelectionDialog()
    {
        Title = "Select Model";
        Width = Dim.Percent(60);
        Height = Dim.Percent(70);

        // Title label
        var titleLabel = new Label
        {
            X = 1,
            Y = 1,
            Text = "Select a model:"
        };

        // Load models
        LoadModels();

        // List view - use source with the item strings
        var listItems = new System.Collections.ObjectModel.ObservableCollection<string>(_items.Select(i => i.DisplayText));
        _listView = new ListView
        {
            X = 1,
            Y = 3,
            Width = Dim.Fill(2),
            Height = Dim.Fill(4),
            Source = new ListWrapper<string>(listItems)
        };

        _listView.OpenSelectedItem += (s, e) => SelectItem(e.Item);

        // Buttons - Dialog has AddButton method
        var selectButton = new Button { Title = "Select" };
        selectButton.Accept += (s, e) =>
        {
            if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _items.Count)
            {
                SelectItem(_listView.SelectedItem);
            }
            if (e is HandledEventArgs he) he.Handled = true;
        };

        var manualButton = new Button { Title = "Manual" };
        manualButton.Accept += (s, e) =>
        {
            OnManual();
            if (e is HandledEventArgs he) he.Handled = true;
        };

        var cancelButton = new Button { Title = "Cancel" };
        cancelButton.Accept += (s, e) =>
        {
            Application.RequestStop();
            if (e is HandledEventArgs he) he.Handled = true;
        };

        AddButton(selectButton);
        AddButton(manualButton);
        AddButton(cancelButton);

        Add(titleLabel, _listView);
    }

    private void LoadModels()
    {
        var config = ModelConfig.Load();
        var models = config.GetAllModels();

        var configured = new List<SelectionItem>();
        var unconfigured = new List<SelectionItem>();

        foreach (var (provider, modelId, name, contextWindow, apiType) in models)
        {
            var item = new SelectionItem(name, provider, modelId, contextWindow, apiType);
            var prov = config.Providers.TryGetValue(provider, out var p) ? p : null;
            var hasApiKey = !string.IsNullOrEmpty(prov?.ApiKey);
            var isLocalhost = prov?.BaseUrl?.Contains("localhost") == true
                           || prov?.BaseUrl?.Contains("127.0.0.1") == true;

            if (hasApiKey || isLocalhost)
                configured.Add(item);
            else
                unconfigured.Add(item);
        }

        // Add configured first
        _items.AddRange(configured);

        // Separator for unconfigured
        if (unconfigured.Count > 0)
        {
            _items.Add(new SelectionItem("-- no API key --", "", "", 0, "", isSeparator: true));
            _items.AddRange(unconfigured);
        }

        // Manual entry option
        _items.Add(new SelectionItem("Other (enter manually)", "", "", 0, ""));
    }

    private void SelectItem(int index)
    {
        var item = _items[index];

        if (item.IsSeparator)
            return;

        if (string.IsNullOrEmpty(item.ModelId))
        {
            // Manual entry
            OnManual();
            return;
        }

        var config = ModelConfig.Load();
        SelectedModel = config.Resolve(item.ModelId);
        Application.RequestStop();
    }

    private void OnManual()
    {
        var manualDialog = new ManualModelDialog();
        
        // Run the dialog modally
        Application.Run(manualDialog);
        if (manualDialog.Result != null)
        {
            SelectedModel = manualDialog.Result;
            Application.RequestStop();
        }
    }

    private class SelectionItem
    {
        public string DisplayName { get; }
        public string Provider { get; }
        public string ModelId { get; }
        public int ContextWindow { get; }
        public string ApiType { get; }
        public bool IsSeparator { get; }

        public string DisplayText => IsSeparator
            ? DisplayName
            : string.IsNullOrEmpty(ModelId)
                ? DisplayName
                : $"{DisplayName} ({Provider}, {ContextWindowK}K)";

        public string ContextWindowK => ContextWindow > 0 ? (ContextWindow / 1024).ToString() : "?";

        public SelectionItem(string displayName, string provider, string modelId,
            int contextWindow, string apiType, bool isSeparator = false)
        {
            DisplayName = displayName;
            Provider = provider;
            ModelId = modelId;
            ContextWindow = contextWindow;
            ApiType = apiType;
            IsSeparator = isSeparator;
        }
    }
}

/// <summary>
/// Dialog for manually entering model details.
/// </summary>
public class ManualModelDialog : Dialog
{
    public ResolvedModel? Result { get; private set; }

    private TextField _endpointField;
    private TextField _modelField;
    private TextField _apiKeyField;

    public ManualModelDialog()
    {
        Title = "Manual Model Entry";
        Width = Dim.Percent(60);
        Height = 14;

        // Endpoint
        var endpointLabel = new Label
        {
            X = 1,
            Y = 1,
            Text = "Endpoint URL:"
        };

        _endpointField = new TextField
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(2),
            Text = "http://localhost:11434/v1"
        };

        // Model
        var modelLabel = new Label
        {
            X = 1,
            Y = 4,
            Text = "Model name:"
        };

        _modelField = new TextField
        {
            X = 1,
            Y = 5,
            Width = Dim.Fill(2),
            Text = "qwen3:14b"
        };

        // API Key
        var apiKeyLabel = new Label
        {
            X = 1,
            Y = 7,
            Text = "API key (optional):"
        };

        _apiKeyField = new TextField
        {
            X = 1,
            Y = 8,
            Width = Dim.Fill(2),
            Text = ""
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

        Add(endpointLabel, _endpointField, modelLabel, _modelField,
            apiKeyLabel, _apiKeyField);
    }

    private void OnOk()
    {
        var endpoint = _endpointField.Text?.Trim() ?? "";
        var model = _modelField.Text?.Trim() ?? "";
        var apiKey = _apiKeyField.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(endpoint))
            endpoint = "http://localhost:11434/v1";

        if (string.IsNullOrEmpty(model))
            model = "qwen3:14b";

        Result = new ResolvedModel(
            endpoint.TrimEnd('/'),
            model,
            apiKey,
            32768, // Default context window
            0.3);

        Application.RequestStop();
    }
}
