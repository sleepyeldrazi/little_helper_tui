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
    /// Registers tool schemas (without additionalProperties: false for OpenAI compat).
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

        // Register tools with schemas compatible with llama.cpp / local servers.
        // The core's NormalizeToolSchema adds additionalProperties: false which
        // breaks llama.cpp's GBNF grammar generator. Forgecode strips this field,
        // opencode never includes it. We do the same.
        ToolSchemas.RegisterAll(client, resolved.ContextWindow, resolved.ModelId);

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
            ApiKey: string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
            EnableStreaming: tuiConfig?.Streaming ?? false);

        return new Agent(config, client, tools, skills, logger, observer);
    }
}
