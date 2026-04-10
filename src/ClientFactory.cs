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
    /// Looks up provider headers from the model config if not supplied.
    /// </summary>
    public static (ModelClient client, ToolExecutor tools) Create(
        ResolvedModel resolved, string workingDir,
        Dictionary<string, string>? extraHeaders = null)
    {
        // Look up provider headers from config if not passed explicitly
        var headers = extraHeaders ?? LookupProviderHeaders(resolved);

        var client = new ModelClient(
            resolved.BaseUrl,
            resolved.ModelId,
            resolved.Temperature,
            string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
            headers);

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

    /// <summary>
    /// Look up provider headers from ~/.little_helper/models.json by matching the base URL.
    /// This is needed because ResolvedModel doesn't carry headers.
    /// </summary>
    private static Dictionary<string, string>? LookupProviderHeaders(ResolvedModel resolved)
    {
        try
        {
            var config = ModelConfig.Load();
            foreach (var (_, provider) in config.Providers)
            {
                if (provider.BaseUrl.TrimEnd('/') == resolved.BaseUrl.TrimEnd('/')
                    && provider.Headers != null && provider.Headers.Count > 0)
                {
                    return provider.Headers;
                }
            }
        }
        catch { /* best effort */ }

        return null;
    }
}
