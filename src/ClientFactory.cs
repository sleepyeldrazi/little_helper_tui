using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Builds core objects from a resolved model config.
/// Routes to ModelClient (OpenAI) or AnthropicClient based on ApiType.
/// </summary>
public static class ClientFactory
{
    /// <summary>
    /// Create the right IModelClient based on resolved.ApiType.
    /// Registers all 5 standard tool schemas.
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

        ToolSchemas.RegisterAll(client);

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

        var config = new AgentConfig(
            ModelEndpoint: resolved.BaseUrl,
            ModelName: resolved.ModelId,
            MaxContextTokens: resolved.ContextWindow,
            MaxSteps: maxSteps,
            MaxRetries: 2,
            StallThreshold: 5,
            WorkingDirectory: workingDir,
            Temperature: resolved.Temperature,
            ApiKey: string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey);

        return new Agent(config, client, tools, skills, logger, observer);
    }
}
