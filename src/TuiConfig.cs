using System.Text.Json;
using System.Text.Json.Serialization;

namespace LittleHelperTui;

/// <summary>
/// TUI configuration loaded from ~/.little_helper/tui.json.
/// Controls display preferences, keybindings, and defaults.
/// </summary>
public class TuiConfig
{
    // Thinking panel
    [JsonPropertyName("thinking_mode")]
    public string ThinkingMode { get; set; } = "condensed"; // full, condensed, hidden

    // Token budget
    [JsonPropertyName("show_token_budget")]
    public bool ShowTokenBudget { get; set; } = true;

    // Diff viewer
    [JsonPropertyName("auto_show_diffs")]
    public bool AutoShowDiffs { get; set; } = true;

    // Max tool output lines to display
    [JsonPropertyName("max_tool_output_lines")]
    public int MaxToolOutputLines { get; set; } = 20;

    // Agent defaults
    [JsonPropertyName("max_steps")]
    public int MaxSteps { get; set; } = 30;

    [JsonPropertyName("default_model")]
    public string? DefaultModel { get; set; }

    // Streaming
    [JsonPropertyName("streaming")]
    public bool Streaming { get; set; } = false;

    // Git checkpoints before write operations
    // "auto" = checkpoint if git is already initialized, skip otherwise
    // "on"   = always checkpoint (git init if needed, local commits only)
    // "off"  = never checkpoint
    [JsonPropertyName("git_checkpoint")]
    public string GitCheckpoint { get; set; } = "auto"; // auto, on, off

    // Theme
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "default"; // default, monochrome, dark

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".little_helper", "tui.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Load config from disk, or create default if missing.</summary>
    public static TuiConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<TuiConfig>(json, JsonOpts) ?? new TuiConfig();
            }
        }
        catch { /* fall through to default */ }

        var config = new TuiConfig();
        config.Save();
        return config;
    }

    /// <summary>Save current config to disk.</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* best effort */ }
    }
}
