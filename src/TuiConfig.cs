using System.Text.Json;
using System.Text.Json.Serialization;

namespace LittleHelperTui;

/// <summary>
/// TUI configuration loaded from ~/.little_helper/tui.json.
/// Controls display preferences, agent defaults, and keybindings.
/// </summary>
public class TuiConfig
{
    // Thinking panel: "full" = show all, "condensed" = first+last 3 lines, "hidden" = skip
    [JsonPropertyName("thinking_mode")]
    public string ThinkingMode { get; set; } = "condensed";

    // Token budget display
    [JsonPropertyName("show_token_budget")]
    public bool ShowTokenBudget { get; set; } = true;

    // Auto-show diff panel after agent writes a file
    [JsonPropertyName("auto_show_diffs")]
    public bool AutoShowDiffs { get; set; } = true;

    // Max tool output lines to display (replaces hardcoded limits)
    [JsonPropertyName("max_tool_output_lines")]
    public int MaxToolOutputLines { get; set; } = 20;

    // Agent step limit -- high default since stall detection + error budget
    // + compaction + user cancel provide the real safety nets
    [JsonPropertyName("max_steps")]
    public int MaxSteps { get; set; } = 500;

    // Default model (skip picker on startup if set)
    [JsonPropertyName("default_model")]
    public string? DefaultModel { get; set; }

    // Enable SSE streaming via observer OnStreamChunk
    [JsonPropertyName("streaming")]
    public bool Streaming { get; set; } = false;

    // Git checkpoints before write operations
    // "auto" = checkpoint if git is already initialized, skip otherwise
    // "on"   = always checkpoint (git init if needed, local commits only)
    // "off"  = never checkpoint
    [JsonPropertyName("git_checkpoint")]
    public string GitCheckpoint { get; set; } = "auto";

    // Theme: "default" = current colors, "monochrome" = greyscale, "dark" = dark mode
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "default";

    // Verbose: show pending tool calls (> prefix) and state transition details
    [JsonPropertyName("verbose")]
    public bool Verbose { get; set; } = false;

    // Console driver: "net" = NetDriver (truecolor, slower), "curses" = CursesDriver (16-color, faster)
    [JsonPropertyName("driver")]
    public string Driver { get; set; } = "net";

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