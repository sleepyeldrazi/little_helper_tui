using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Builds core objects from a resolved model config.
/// Routes to ModelClient (OpenAI) or AnthropicClient based on ApiType.
/// Skips tool registration for endpoints that can't handle tool schemas
/// (e.g. llama.cpp with small models).
/// </summary>
public static class ClientFactory
{
    /// <summary>
    /// Create the right IModelClient based on resolved.ApiType.
    /// Conditionally registers tool schemas based on endpoint capabilities.
    /// </summary>
    public static (IModelClient client, ToolExecutor tools) Create(
        ResolvedModel resolved, string workingDir)
    {
        IModelClient client = resolved.ApiType.ToLowerInvariant() switch
        {
            "anthropic" or "anthropic-messages" => new AnthropicClient(
                resolved.BaseUrl,
                resolved.ModelId,
                resolved.Temperature,
                string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
                resolved.Headers),
            _ => new ModelClient(
                resolved.BaseUrl,
                resolved.ModelId,
                resolved.Temperature,
                string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
                resolved.Headers)
        };

        var registerTools = ShouldRegisterTools(resolved);
        if (registerTools)
        {
            ToolSchemas.RegisterAll(client, resolved.ContextWindow, resolved.ModelId);
        }

        var tools = new ToolExecutor(workingDir, blockDestructive: false);

        return (client, tools);
    }

    /// <summary>
    /// Create a fully wired Agent with observer, logger, and config from tui.json.
    /// </summary>
    public static Agent CreateAgent(ResolvedModel resolved, string workingDir,
        TuiObserver observer, SessionLogger? logger = null, TuiConfig? tuiConfig = null)
    {
        var (client, tools) = Create(resolved, workingDir);

        var skills = new SkillDiscovery();
        skills.Discover(workingDir);

        var maxSteps = tuiConfig?.MaxSteps ?? 30;
        var noTools = !ShouldRegisterTools(resolved);

        var config = new AgentConfig(
            ModelEndpoint: resolved.BaseUrl,
            ModelName: resolved.ModelId,
            MaxContextTokens: resolved.ContextWindow,
            MaxSteps: maxSteps,
            MaxRetries: 2,
            StallThreshold: 5,
            WorkingDirectory: workingDir,
            Temperature: resolved.Temperature,
            ApiKey: string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
            EnableStreaming: tuiConfig?.Streaming ?? false);

        return new Agent(config, client, tools, skills, logger, observer);
    }

    /// <summary>
    /// Determine if tool schemas should be registered for this endpoint.
    /// llama.cpp servers with small models often can't parse tool schemas
    /// (GBNF grammar generation fails with "Unable to generate parser").
    /// Ollama handles tools via its own adapter layer, so it's always safe.
    /// Anthropic-compatible endpoints always support tools.
    /// </summary>
    private static bool ShouldRegisterTools(ResolvedModel resolved)
    {
        var url = resolved.BaseUrl.ToLowerInvariant();
        var model = resolved.ModelId.ToLowerInvariant();

        // Anthropic API always supports tools
        if (resolved.ApiType.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            return true;

        // Ollama handles tool schema conversion internally
        if (url.Contains("localhost:11434") || url.Contains("127.0.0.1:11434"))
            return true;

        // llama.cpp servers: check if the model is likely to support tools
        // Small models (< 8B params) and Gemma models often fail
        if (url.Contains("127.0.0.1") || url.Contains("localhost"))
        {
            // Not Ollama, probably llama.cpp -- be cautious with small models
            if (model.Contains("gemma") || model.Contains("phi"))
                return false;

            // Check for size indication in model name
            var sizeMatch = System.Text.RegularExpressions.Regex.Match(model, @"(\d+(?:\.\d+)?)\s*b");
            if (sizeMatch.Success && double.TryParse(sizeMatch.Groups[1].Value, out var billions))
                return billions >= 14.0; // Only register tools for 14B+ models on llama.cpp
        }

        // Cloud providers: always register
        return true;
    }
}
