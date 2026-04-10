using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Model selection prompt. Loads ~/.little_helper/models.json and
/// presents a Spectre selection prompt for the user to pick a model.
/// Falls back to a manual entry if no config exists.
/// </summary>
public static class ModelSelector
{
    public static ResolvedModel? Select(IAnsiConsole console)
    {
        var config = ModelConfig.Load();
        var models = config.GetAllModels();

        if (models.Count == 0)
        {
            return PromptManual(console);
        }

        // Build selection items
        var items = new List<SelectionItem>();
        foreach (var (provider, modelId, name, contextWindow, apiType) in models)
        {
            var label = $"{name} [dim]({provider}, {contextWindow / 1024}K context)[/]";
            items.Add(new SelectionItem(label, provider, modelId, contextWindow, apiType));
        }
        items.Add(new SelectionItem("[dim]Other (enter manually)[/]", "", "", 0, ""));

        console.WriteLine();
        var selected = console.Prompt(
            new SelectionPrompt<SelectionItem>()
                .Title("[bold]Select a model:[/]")
                .PageSize(10)
                .MoreChoicesText("[dim](Move up and down to see more)[/]")
                .UseConverter(item => item.Label.Replace("[/", "[/").Replace("[dim]", "").Replace("[bold]", ""))
                .AddChoices(items)
        );

        if (string.IsNullOrEmpty(selected.ModelId))
            return PromptManual(console);

        // Resolve through config to get full details (api key, etc.)
        var resolved = config.Resolve(selected.ModelId);
        if (resolved != null)
        {
            console.MarkupLine($"[green]Using {resolved.ModelId}[/] [dim]({resolved.BaseUrl})[/]");
            return resolved;
        }

        return PromptManual(console);
    }

    private static ResolvedModel? PromptManual(IAnsiConsole console)
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

        return new ResolvedModel(
            endpoint.TrimEnd('/'),
            model,
            string.IsNullOrEmpty(apiKey) ? "" : apiKey,
            32768,
            0.3);
    }

    private record SelectionItem(
        string Label, string Provider, string ModelId, int ContextWindow, string ApiType);
}
