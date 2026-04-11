using System.ComponentModel;
using LittleHelper;
using Terminal.Gui;

namespace LittleHelperTui.Dialogs;

/// <summary>
/// Dialog for selecting a model from configured providers.
/// Matches old ModelSelector: configured providers on top, unconfigured below,
/// manual entry at bottom.
/// </summary>
public class ModelSelectionDialog : Dialog
{
    public ResolvedModel? SelectedModel { get; private set; }
    public bool ShowManualEntry { get; private set; }

    private readonly ListView _listView;
    private readonly List<SelectionItem> _items = new();

    public ModelSelectionDialog()
    {
        Title = "Select a model";
        Width = Dim.Percent(60);
        Height = Dim.Percent(70);

        LoadModels();

        var listItems = new System.Collections.ObjectModel.ObservableCollection<string>(
            _items.Select(i => i.DisplayText));

        _listView = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Height = Dim.Fill(4),
            Source = new ListWrapper<string>(listItems)
        };

        _listView.OpenSelectedItem += (s, e) => SelectItem(e.Item);

        var selectButton = new Button { Title = "Select", IsDefault = true };
        selectButton.Accept += (s, e) =>
        {
            if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _items.Count)
                SelectItem(_listView.SelectedItem);
            if (e is HandledEventArgs he) he.Handled = true;
        };

        var cancelButton = new Button { Title = "Cancel" };
        cancelButton.Accept += (s, e) =>
        {
            SelectedModel = null;
            ShowManualEntry = false;
            Application.RequestStop(this);
            if (e is HandledEventArgs he) he.Handled = true;
        };

        AddButton(selectButton);
        AddButton(cancelButton);
        Add(_listView);
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

        _items.AddRange(configured);

        if (unconfigured.Count > 0)
        {
            _items.Add(new SelectionItem("────────────────", "", "", 0, "", isSeparator: true));
            _items.AddRange(unconfigured);
        }

        _items.Add(new SelectionItem("Other (enter manually)", "", "", 0, ""));
    }

    private void SelectItem(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];

        if (item.IsSeparator) return;

        if (string.IsNullOrEmpty(item.ModelId))
        {
            ShowManualEntry = true;
            Application.RequestStop(this);
            return;
        }

        var config = ModelConfig.Load();
        SelectedModel = config.Resolve(item.ModelId);
        ShowManualEntry = false;
        Application.RequestStop(this);
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
                : $"{DisplayName}  ({Provider}, {ContextWindowK}K context)";

        private string ContextWindowK => ContextWindow > 0 ? (ContextWindow / 1024).ToString() : "?";

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
/// Matches old ModelSelector.PromptManual: endpoint, model name, API key.
/// </summary>
public class ManualModelDialog : Dialog
{
    public ResolvedModel? Result { get; private set; }

    private readonly TextField _endpointField;
    private readonly TextField _modelField;
    private readonly TextField _apiKeyField;

    public ManualModelDialog()
    {
        Title = "Manual Model Entry";
        Width = Dim.Percent(60);
        Height = 14;

        var endpointLabel = new Label { X = 1, Y = 1, Text = "Endpoint URL:" };
        _endpointField = new TextField
        {
            X = 1, Y = 2, Width = Dim.Fill(2),
            Text = "http://localhost:11434/v1"
        };

        var modelLabel = new Label { X = 1, Y = 4, Text = "Model name:" };
        _modelField = new TextField
        {
            X = 1, Y = 5, Width = Dim.Fill(2),
            Text = "qwen3:14b"
        };

        var apiKeyLabel = new Label { X = 1, Y = 7, Text = "API key (leave empty for none):" };
        _apiKeyField = new TextField
        {
            X = 1, Y = 8, Width = Dim.Fill(2),
            Text = ""
        };

        var okButton = new Button { Title = "OK", IsDefault = true };
        okButton.Accept += (s, e) =>
        {
            OnOk();
            Application.RequestStop(this);
            if (e is HandledEventArgs he) he.Handled = true;
        };

        var cancelButton = new Button { Title = "Cancel" };
        cancelButton.Accept += (s, e) =>
        {
            Result = null;
            Application.RequestStop(this);
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
            endpoint.TrimEnd('/'), model,
            apiKey, 32768, 0.3);
    }
}
