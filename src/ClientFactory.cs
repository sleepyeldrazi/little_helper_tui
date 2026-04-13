using LittleHelper;
using LittleHelperTui.Observers;

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
        ResolvedModel resolved, string workingDir, bool allowEscape = false)
    {
        IModelClient client;

        if (resolved.ApiType == "anthropic")
        {
            client = new AnthropicClient(
                resolved.BaseUrl,
                resolved.ModelId,
                resolved.Temperature,
                string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
                resolved.Headers,
                resolved.AuthType);
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

        // Register tools on the appropriate client (skip if provider says no tools)
        if (resolved.ToolsEnabled != false)
            ToolSchemas.RegisterAll(client, resolved.ContextWindow, resolved.ModelId);

        var tools = new ToolExecutor(workingDir, blockDestructive: false, allowEscape: allowEscape);

        // Wire up SpawnManager for sub-agent spawning via tmux
        tools.SpawnManager = new SpawnManager();

        return (client, tools);
    }

    /// <summary>
    /// Create a fully wired Agent with observer and logger.
    /// Config values (MaxSteps, EnableStreaming) come from TuiConfig.
    /// </summary>
    public static Agent CreateAgent(ResolvedModel resolved, string workingDir,
        TerminalGuiObserver observer, TuiConfig config, SessionLogger? logger = null, bool allowEscape = false)
    {
        var (client, tools) = Create(resolved, workingDir, allowEscape);

        SkillDiscovery.SeedDefaults(ResolveBundledSkillsDir());
        var skills = new SkillDiscovery();
        skills.Discover(workingDir);

        var agentConfig = new AgentConfig(
            ModelEndpoint: resolved.BaseUrl,
            ModelName: resolved.ModelId,
            MaxContextTokens: resolved.ContextWindow,
            MaxSteps: config.MaxSteps,
            MaxRetries: 2,
            StallThreshold: 5,
            WorkingDirectory: workingDir,
            Temperature: resolved.Temperature,
            ApiKey: string.IsNullOrEmpty(resolved.ApiKey) ? null : resolved.ApiKey,
            ExtraHeaders: resolved.Headers,
            EnableStreaming: config.Streaming,
            PromptTier: resolved.PromptTier
        );

        return new Agent(agentConfig, client, tools, skills, logger, observer);
    }

    private static string ResolveBundledSkillsDir()
    {
        var assemblyDir = AppContext.BaseDirectory;
        var devPath = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", "core", "skills"));
        if (Directory.Exists(devPath))
            return devPath;
        return Path.GetFullPath(Path.Combine(assemblyDir, "skills"));
    }
}
