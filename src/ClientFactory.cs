using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Builds core objects (Agent, ModelClient, ToolExecutor) from a resolved model config.
/// Routes to AnthropicClient or ModelClient based on ApiType.
/// </summary>
public static class ClientFactory
{
    /// <summary>
    /// Create a fully wired (IModelClient, ToolExecutor) pair ready for agent use.
    /// Routes to AnthropicClient for api_type "anthropic", ModelClient otherwise.
    /// </summary>
    public static (IModelClient client, ToolExecutor tools) Create(
        ResolvedModel resolved, string workingDir)
    {
        IModelClient client;

        if (resolved.ApiType == "anthropic")
        {
            client = new AnthropicClient(
                resolved.BaseUrl,
                resolved.ModelId,
                resolved.Temperature,
                string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
                resolved.Headers);
        }
        else
        {
            client = new ModelClient(
                resolved.BaseUrl,
                resolved.ModelId,
                resolved.Temperature,
                string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
                resolved.Headers);
        }

        // Register tools on the appropriate client
        ToolSchemas.RegisterAll(client, resolved.ContextWindow, resolved.ModelId);

        var tools = new ToolExecutor(workingDir, blockDestructive: false);
        return (client, tools);
    }

    /// <summary>
    /// Create a fully wired Agent with observer and logger.
    /// </summary>
    public static Agent CreateAgent(ResolvedModel resolved, string workingDir,
        TuiObserver observer, SessionLogger? logger = null)
    {
        var (client, tools) = Create(resolved, workingDir);

        var skills = new SkillDiscovery();
        skills.Discover(workingDir);

        var config = new AgentConfig(
            ModelEndpoint: resolved.BaseUrl,
            ModelName: resolved.ModelId,
            MaxContextTokens: resolved.ContextWindow,
            MaxSteps: 30,
            MaxRetries: 2,
            StallThreshold: 5,
            WorkingDirectory: workingDir,
            Temperature: resolved.Temperature,
            ApiKey: string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
            ExtraHeaders: resolved.Headers);

        return new Agent(config, client, tools, skills, logger, observer);
    }
}