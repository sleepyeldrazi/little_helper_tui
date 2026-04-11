using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Model selection prompt. Loads ~/.little_helper/models.json and
/// presents a Spectre selection prompt sorted by provider status.
/// Configured providers (with API key or localhost) on top, others below.
/// </summary>
public static class ModelSelector
{
    public static async Task<ResolvedModel?> SelectAsync(IAnsiConsole console)
    {
        var config = ModelConfig.Load();
        var models = config.GetAllModels();

        if (models.Count == 0)
        {
            return await PromptManual(console);
        }

        // Separate into configured (has API key or localhost) and unconfigured
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

        // Build final list: configured first, then unconfigured, then manual
        var items = new List<SelectionItem>();
        items.AddRange(configured);

        if (unconfigured.Count > 0)
        {
            items.Add(new SelectionItem("-- no API key --", "", "", 0, "", true));
            items.AddRange(unconfigured);
        }

        items.Add(new SelectionItem("Other (enter manually)", "", "", 0, ""));

        console.WriteLine();
        var selected = console.Prompt(
            new SelectionPrompt<SelectionItem>()
                .Title("[bold]Select a model:[/]")
                .PageSize(15)
                .MoreChoicesText("[dim](Move up and down to see more)[/]")
                .UseConverter(item =>
                {
                    if (item.IsSeparator)
                        return $"[dim]────────────────[/]";
                    if (string.IsNullOrEmpty(item.ModelId))
                        return $"[dim]{item.DisplayName}[/]";
                    return $"{item.DisplayName}  [dim]({item.Provider}, {item.ContextWindowK}K context)[/]";
                })
                .AddChoices(items)
        );

        if (string.IsNullOrEmpty(selected.ModelId))
            return await PromptManual(console);

        // Resolve through config to get full details (api key, etc.)
        var resolved = config.Resolve(selected.ModelId);
        if (resolved != null)
        {
            console.MarkupLine($"[green]Using {resolved.ModelId}[/] [dim]({resolved.BaseUrl})[/]");
            return resolved;
        }

        return await PromptManual(console);
    }

    private static async Task<ResolvedModel?> PromptManual(IAnsiConsole console)
    {
        var endpoint = console.Prompt(
            new TextPrompt<string>("[bold]Endpoint URL:[/]")
                .DefaultValue("http://localhost:11434/v1")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = "http://localhost:11434/v1";

        var model = console.Prompt(
            new TextPrompt<string>("[bold]Model name:[/]")
                .DefaultValue("qwen3:14b"));

        var apiKey = console.Prompt(
            new TextPrompt<string>("[bold]API key[/] [dim](leave empty for none)[/]:")
                .AllowEmpty());

        // Context window priority: 1) models.json 2) auto-detect 3) 32k fallback
        var contextWindow = await ResolveContextWindowAsync(console, endpoint, model, apiKey);

        return new ResolvedModel(
            endpoint.TrimEnd('/'), model,
            string.IsNullOrEmpty(apiKey) ? "" : apiKey,
            contextWindow, 0.3);
    }

    /// <summary>
    /// Resolve context window with priority:
    /// 1. Check models.json if model is listed there
    /// 2. Auto-detect via endpoint query
    /// 3. Fall back to 32k
    /// </summary>
    private static async Task<int> ResolveContextWindowAsync(IAnsiConsole console, string endpoint, string model, string? apiKey)
    {
        // Priority 1: Check models.json
        var config = ModelConfig.Load();
        var resolvedFromConfig = config.Resolve(model);
        if (resolvedFromConfig != null && resolvedFromConfig.ContextWindow > 0)
        {
            return resolvedFromConfig.ContextWindow;
        }

        // Priority 2: Auto-detect from endpoint
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var client = new ModelClient(endpoint.TrimEnd('/'), model, 0.3,
                string.IsNullOrEmpty(apiKey) ? null : apiKey);
            var detected = await client.QueryContextWindow(cts.Token);
            if (detected.HasValue && detected.Value > 0)
            {
                return detected.Value;
            }
        }
        catch
        {
            // Detection failed, fall through to default
        }

        // Priority 3: Default fallback
        return 32768;
    }

    private record SelectionItem(
        string DisplayName, string Provider, string ModelId, int ContextWindow, string ApiType,
        bool IsSeparator = false)
    {
        public string ContextWindowK => ContextWindow > 0 ? (ContextWindow / 1024).ToString() : "?";
    }
}
