using System.ComponentModel;
using LittleHelper;
using LittleHelperTui.Views;
using Terminal.Gui;

namespace LittleHelperTui.Dialogs;

/// <summary>
/// Dialog for selecting a model from configured providers.
/// Shows all configured models, plus options to fetch from server or enter manually.
/// </summary>
public class ModelSelectionDialog : Dialog
{
    public ResolvedModel? SelectedModel { get; private set; }
    public bool ShowManualEntry { get; private set; }
    public bool ShowEndpointSetup { get; private set; }

    private readonly ListView _listView;
    private readonly List<SelectionItem> _items = new();

    public ModelSelectionDialog()
    {
        Title = "Select a model";
        Width = Dim.Percent(60);
        Height = Dim.Percent(70);
        ColorScheme = DarkColors.Dialog;
        Margin.ShadowStyle = ShadowStyle.Transparent;

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
            ShowEndpointSetup = false;
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

        _items.Add(new SelectionItem("────────────────", "", "", 0, "", isSeparator: true));
        _items.Add(new SelectionItem("Add new endpoint...", "", "", 0, "", action: ItemAction.EndpointSetup));
        _items.Add(new SelectionItem("Enter manually...", "", "", 0, "", action: ItemAction.ManualEntry));
    }

    private void SelectItem(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];

        if (item.IsSeparator) return;

        if (item.Action == ItemAction.EndpointSetup)
        {
            ShowEndpointSetup = true;
            Application.RequestStop(this);
            return;
        }

        if (item.Action == ItemAction.ManualEntry)
        {
            ShowManualEntry = true;
            Application.RequestStop(this);
            return;
        }

        var config = ModelConfig.Load();
        SelectedModel = config.Resolve(item.ModelId);
        Application.RequestStop(this);
    }

    private enum ItemAction { Select, ManualEntry, EndpointSetup }

    private class SelectionItem
    {
        public string DisplayName { get; }
        public string Provider { get; }
        public string ModelId { get; }
        public int ContextWindow { get; }
        public string ApiType { get; }
        public bool IsSeparator { get; }
        public ItemAction Action { get; }

        public string DisplayText => IsSeparator
            ? DisplayName
            : Action != ItemAction.Select
                ? DisplayName
                : $"{DisplayName}  ({Provider}, {ContextWindowK}K context)";

        private string ContextWindowK => ContextWindow > 0 ? (ContextWindow / 1024).ToString() : "?";

        public SelectionItem(string displayName, string provider, string modelId,
            int contextWindow, string apiType, bool isSeparator = false,
            ItemAction action = ItemAction.Select)
        {
            DisplayName = displayName;
            Provider = provider;
            ModelId = modelId;
            ContextWindow = contextWindow;
            ApiType = apiType;
            IsSeparator = isSeparator;
            Action = action;
        }
    }
}

/// <summary>
/// Dialog for manually entering model details.
/// </summary>
public class ManualModelDialog : Dialog
{
    public ResolvedModel? Result { get; private set; }

    private readonly TextField _endpointField;
    private readonly TextField _modelField;
    private readonly TextField _apiKeyField;
    private readonly ListView _modelListView;

    public ManualModelDialog()
    {
        Title = "Manual Model Entry";
        Width = Dim.Percent(60);
        Height = 18;
        ColorScheme = DarkColors.Dialog;
        Margin.ShadowStyle = ShadowStyle.Transparent;

        var endpointLabel = new Label { X = 1, Y = 1, Text = "Endpoint URL:" };
        _endpointField = new TextField
        {
            X = 1, Y = 2, Width = Dim.Fill(16),
            Text = "http://localhost:11434/v1"
        };

        var apiKeyLabel = new Label { X = 1, Y = 4, Text = "API key (leave empty for none):" };
        _apiKeyField = new TextField
        {
            X = 1, Y = 5, Width = Dim.Fill(2),
            Secret = true, Text = ""
        };

        var modelLabel = new Label { X = 1, Y = 7, Text = "Model name:" };
        _modelField = new TextField
        {
            X = 1, Y = 8, Width = Dim.Fill(16),
            Text = ""
        };

        var fetchButton = new Button { X = Pos.AnchorEnd(15), Y = 2, Title = "Fetch List" };
        _modelListView = new ListView
        {
            X = 1, Y = 9,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
            Visible = false,
            ColorScheme = DarkColors.Dialog
        };

        fetchButton.Accept += async (s, e) =>
        {
            if (e is HandledEventArgs he) he.Handled = true;
            var url = _endpointField.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(url)) return;

            fetchButton.Title = "Fetching...";
            fetchButton.Enabled = false;

            try
            {
                var key = _apiKeyField.Text?.Trim();
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
                    _modelListView.Source = new ListWrapper<string>(items);
                    _modelListView.Visible = true;
                    _modelListView.SetFocus();
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

        _modelListView.OpenSelectedItem += (s, e) =>
        {
            if (e.Item >= 0 && _modelListView.Source != null)
            {
                _modelField.Text = _modelListView.Source.ToList()[e.Item]?.ToString() ?? "";
                _modelListView.Visible = false;
                _modelField.SetFocus();
            }
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
        Add(endpointLabel, _endpointField, fetchButton,
            apiKeyLabel, _apiKeyField,
            modelLabel, _modelField, _modelListView);
    }

    private void OnOk()
    {
        var endpoint = _endpointField.Text?.Trim() ?? "";
        var model = _modelField.Text?.Trim() ?? "";
        var apiKey = _apiKeyField.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(endpoint))
            endpoint = "http://localhost:11434/v1";
        if (string.IsNullOrEmpty(model))
        {
            MessageBox.ErrorQuery("Error", "Model name is required.", "OK");
            return;
        }

        Result = new ResolvedModel(
            endpoint.TrimEnd('/'), model,
            apiKey, 32768, 0.3);
    }
}
