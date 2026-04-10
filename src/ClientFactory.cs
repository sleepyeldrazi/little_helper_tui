using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Builds core objects (Agent, ModelClient, ToolExecutor) from a resolved model config.
/// Centralizes the wiring so the main loop doesn't deal with construction details.
/// </summary>
public static class ClientFactory
{
    /// <summary>
    /// Create a fully wired (ModelClient, ToolExecutor) pair ready for agent use.
    /// Registers all 5 standard tool schemas.
    /// </summary>
    public static (ModelClient client, ToolExecutor tools) Create(ResolvedModel resolved, string workingDir)
    {
        var client = new ModelClient(
            resolved.BaseUrl,
            resolved.ModelId,
            resolved.Temperature,
            string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey);

        ToolSchemas.RegisterAll(client);

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
            ApiKey: string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey);

        return new Agent(config, client, tools, skills, logger, observer);
    }
}
