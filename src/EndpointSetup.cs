using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// First-run endpoint setup. Presents known providers so the user
/// can "just drop an API key" for cloud models, or configure local endpoints.
/// Writes the result to ~/.little_helper/models.json.
/// </summary>
public static class EndpointSetup
{
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
            "default", 4096, Hint: "Run llama-server with --port 8080"),

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

    public static async Task<ResolvedModel?> RunAsync(IAnsiConsole console)
    {
        console.WriteLine();
        console.MarkupLine("[bold blue]Welcome to little helper![/]");
        console.MarkupLine("[dim]Let's set up your first model endpoint.[/]");
        console.WriteLine();

        // Provider picker
        var items = Templates.Select(t => t.DisplayName).ToList();
        items.Add("Skip (enter manually)");

        var selected = console.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Pick a provider:[/]")
                .PageSize(15)
                .AddChoices(items));

        if (selected == "Skip (enter manually)")
            return await ManualEntry(console);

        var template = Templates.First(t => t.DisplayName == selected);

        return template.Name switch
        {
            "ollama" or "lm-studio" or "llama-cpp" => await SetupLocal(console, template),
            "custom" => await SetupCustom(console),
            _ => await SetupCloud(console, template)
        };
    }

    private static async Task<ResolvedModel> SetupLocal(IAnsiConsole console, ProviderTemplate template)
    {
        if (template.Hint != null)
        {
            console.MarkupLine($"[dim]{Markup.Escape(template.Hint)}.[/]");
            console.WriteLine();
        }

        var url = console.Prompt(
            new TextPrompt<string>("[bold]Endpoint URL:[/]")
                .DefaultValue(template.BaseUrl)
                .AllowEmpty());
        if (string.IsNullOrWhiteSpace(url)) url = template.BaseUrl;

        var model = console.Prompt(
            new TextPrompt<string>("[bold]Model name:[/]")
                .DefaultValue(template.DefaultModel)
                .AllowEmpty());
        if (string.IsNullOrWhiteSpace(model)) model = template.DefaultModel;

        // Auto-detect context window from endpoint
        var contextWindow = await DetectContextWindowAsync(console, url.TrimEnd('/'), model, apiKey: null);

        var config = BuildConfig(template, url, apiKey: null, contextWindow);
        config.Save();
        console.MarkupLine($"[green]Saved to ~/.little_helper/models.json[/]");

        return new ResolvedModel(
            url.TrimEnd('/'), model, "", contextWindow, 0.3,
            template.ApiType, template.Headers, template.AuthType);
    }

    private static async Task<ResolvedModel> SetupCloud(IAnsiConsole console, ProviderTemplate template)
    {
        console.MarkupLine($"[dim]Drop your {template.DisplayName.Split('(')[0].Trim()} API key:[/]");
        var apiKey = console.Prompt(
            new TextPrompt<string>("[bold]API key:[/]")
                .Secret()
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            console.MarkupLine("[yellow]No API key entered. You can add it later in ~/.little_helper/models.json[/]");
            apiKey = "";
        }

        var model = console.Prompt(
            new TextPrompt<string>("[bold]Model ID[/] [dim](press Enter for default)[/]:")
                .DefaultValue(template.DefaultModel)
                .AllowEmpty());
        if (string.IsNullOrWhiteSpace(model)) model = template.DefaultModel;

        var url = template.BaseUrl;
        if (template.Name == "kimi")
            url = "https://api.kimi.com/coding"; // Kimi coding endpoint

        // Auto-detect context window (or use template default if detection fails)
        var contextWindow = await DetectContextWindowAsync(console, url.TrimEnd('/'), model,
            string.IsNullOrEmpty(apiKey) ? null : apiKey, template.ContextWindow);

        var config = BuildConfig(template, url, apiKey, contextWindow);
        config.Save();
        console.MarkupLine($"[green]Saved to ~/.little_helper/models.json[/]");

        return new ResolvedModel(
            url.TrimEnd('/'), model, apiKey, contextWindow, 0.3,
            template.ApiType, template.Headers, template.AuthType);
    }

    private static async Task<ResolvedModel> SetupCustom(IAnsiConsole console)
    {
        var url = console.Prompt(
            new TextPrompt<string>("[bold]Endpoint URL:[/]")
                .DefaultValue("http://localhost:11434/v1"));
        var model = console.Prompt(
            new TextPrompt<string>("[bold]Model name:[/]")
                .DefaultValue("qwen3:14b"));
        var apiKey = console.Prompt(
            new TextPrompt<string>("[bold]API key[/] [dim](leave empty for none)[/]:")
                .AllowEmpty());

        // Auto-detect context window from endpoint
        var contextWindow = await DetectContextWindowAsync(console, url.TrimEnd('/'), model,
            string.IsNullOrEmpty(apiKey) ? null : apiKey);

        // Just return, don't save a template for custom
        return new ResolvedModel(
            url.TrimEnd('/'), model, string.IsNullOrEmpty(apiKey) ? "" : apiKey,
            contextWindow, 0.3);
    }

    private static async Task<ResolvedModel?> ManualEntry(IAnsiConsole console)
    {
        // Original manual prompt flow
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

        // Auto-detect context window from endpoint
        var contextWindow = await DetectContextWindowAsync(console, endpoint.TrimEnd('/'), model,
            string.IsNullOrEmpty(apiKey) ? null : apiKey);

        return new ResolvedModel(
            endpoint.TrimEnd('/'), model,
            string.IsNullOrEmpty(apiKey) ? "" : apiKey,
            contextWindow, 0.3);
    }

    /// <summary>Build a ModelConfig with the selected provider template.</summary>
    private static ModelConfig BuildConfig(ProviderTemplate template, string url, string? apiKey, int contextWindow)
    {
        var config = ModelConfig.Load();

        config.Providers[template.Name] = new ProviderConfig
        {
            BaseUrl = url.TrimEnd('/'),
            ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey,
            ApiType = template.ApiType,
            AuthType = template.AuthType,
            Headers = template.Headers,
            DefaultContextWindow = contextWindow,
            Models = new List<ModelEntry>
            {
                new()
                {
                    Id = template.DefaultModel,
                    Name = template.DisplayName.Split('(')[0].Trim(),
                    ContextWindow = contextWindow
                }
            }
        };
        config.DefaultModel = template.DefaultModel;

        return config;
    }

    /// <summary>
    /// Auto-detect context window from endpoint. Falls back to provided default or 32k.
    /// </summary>
    private static async Task<int> DetectContextWindowAsync(IAnsiConsole console, string endpoint, string model, string? apiKey, int fallback = 32768)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var client = new ModelClient(endpoint.TrimEnd('/'), model, 0.3,
                string.IsNullOrEmpty(apiKey) ? null : apiKey);
            var detected = await client.QueryContextWindow(cts.Token);
            if (detected.HasValue && detected.Value > 0)
            {
                console.MarkupLine($"[dim]Detected context window: {detected.Value / 1024}K tokens[/]");
                return detected.Value;
            }
        }
        catch
        {
            // Detection failed, use fallback
        }

        console.MarkupLine($"[dim]Using default context window: {fallback / 1024}K tokens[/]");
        return fallback;
    }
}
