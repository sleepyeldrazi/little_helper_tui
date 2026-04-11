using Terminal.Gui;

namespace LittleHelperTui.Views;

/// <summary>
/// Shared color schemes for message views.
/// </summary>
internal static class ChatColors
{
    public static ColorScheme User => new()
    {
        Normal = new Terminal.Gui.Attribute(Color.Cyan, Color.Black),
        Focus = new Terminal.Gui.Attribute(Color.Cyan, Color.Black),
        HotNormal = new Terminal.Gui.Attribute(Color.BrightCyan, Color.Black),
        HotFocus = new Terminal.Gui.Attribute(Color.BrightCyan, Color.Black)
    };

    public static ColorScheme Assistant => new()
    {
        Normal = new Terminal.Gui.Attribute(Color.White, Color.Black),
        Focus = new Terminal.Gui.Attribute(Color.White, Color.Black),
        HotNormal = new Terminal.Gui.Attribute(Color.BrightYellow, Color.Black),
        HotFocus = new Terminal.Gui.Attribute(Color.BrightYellow, Color.Black)
    };

    public static ColorScheme Thinking => new()
    {
        Normal = new Terminal.Gui.Attribute(Color.DarkGray, Color.Black),
        Focus = new Terminal.Gui.Attribute(Color.DarkGray, Color.Black),
        HotNormal = new Terminal.Gui.Attribute(Color.Gray, Color.Black),
        HotFocus = new Terminal.Gui.Attribute(Color.Gray, Color.Black)
    };

    public static ColorScheme ToolSuccess => new()
    {
        Normal = new Terminal.Gui.Attribute(Color.Green, Color.Black),
        Focus = new Terminal.Gui.Attribute(Color.Green, Color.Black),
        HotNormal = new Terminal.Gui.Attribute(Color.BrightGreen, Color.Black),
        HotFocus = new Terminal.Gui.Attribute(Color.BrightGreen, Color.Black)
    };

    public static ColorScheme ToolError => new()
    {
        Normal = new Terminal.Gui.Attribute(Color.Red, Color.Black),
        Focus = new Terminal.Gui.Attribute(Color.Red, Color.Black),
        HotNormal = new Terminal.Gui.Attribute(Color.BrightRed, Color.Black),
        HotFocus = new Terminal.Gui.Attribute(Color.BrightRed, Color.Black)
    };

    public static ColorScheme Status => new()
    {
        Normal = new Terminal.Gui.Attribute(Color.Gray, Color.Black),
        Focus = new Terminal.Gui.Attribute(Color.Gray, Color.Black),
        HotNormal = new Terminal.Gui.Attribute(Color.White, Color.Black),
        HotFocus = new Terminal.Gui.Attribute(Color.White, Color.Black)
    };
}

/// <summary>
/// User message view - displays user input in a bordered frame.
/// </summary>
public class UserMessageView : FrameView
{
    public string Content { get; }

    public UserMessageView(string content)
    {
        Content = content;
        Title = "You";
        BorderStyle = LineStyle.Double;
        ColorScheme = ChatColors.User;
        Width = Dim.Fill();
        Height = Dim.Auto();

        var label = new Label
        {
            Text = content,
            Width = Dim.Fill(),
            Height = Dim.Auto(),
            X = 0,
            Y = 0
        };

        Add(label);
    }
}

/// <summary>
/// Assistant message view - displays assistant response in a bordered frame.
/// </summary>
public class AssistantMessageView : FrameView
{
    public int Step { get; }
    public int TokensUsed { get; }

    public AssistantMessageView(string content, int step, int tokensUsed = 0)
    {
        Step = step;
        TokensUsed = tokensUsed;
        Title = tokensUsed > 0 ? $"Assistant #{step} ({FormatTokens(tokensUsed)})" : $"Assistant #{step}";
        BorderStyle = LineStyle.Rounded;
        ColorScheme = ChatColors.Assistant;
        Width = Dim.Fill();
        Height = Dim.Auto();

        var label = new Label
        {
            Text = content,
            Width = Dim.Fill(),
            Height = Dim.Auto(),
            X = 0,
            Y = 0
        };

        Add(label);
    }

    private static string FormatTokens(int tokens) =>
        tokens >= 1_000 ? $"{tokens / 1_000.0:F1}K" : $"{tokens}";
}

/// <summary>
/// Thinking view - displays thinking content in dimmed style.
/// </summary>
public class ThinkingView : FrameView
{
    public int ThinkingTokens { get; }
    public int ContextTokens { get; }

    public ThinkingView(string content, int thinkingTokens, int contextTokens)
    {
        ThinkingTokens = thinkingTokens;
        ContextTokens = contextTokens;
        Title = $"Thinking ({FormatTokens(thinkingTokens)})";
        BorderStyle = LineStyle.Dashed;
        ColorScheme = ChatColors.Thinking;
        Width = Dim.Fill();
        Height = Dim.Auto();

        var label = new Label
        {
            Text = content,
            Width = Dim.Fill(),
            Height = Dim.Auto(),
            X = 0,
            Y = 0
        };

        Add(label);
    }

    private static string FormatTokens(int tokens) =>
        tokens >= 1_000 ? $"{tokens / 1_000.0:F1}K" : $"{tokens}";
}

/// <summary>
/// Tool result view - displays tool execution results with color-coded status.
/// </summary>
public class ToolResultView : View
{
    public string ToolName { get; }
    public string Result { get; }
    public bool IsError { get; }
    public long DurationMs { get; }

    public ToolResultView(string toolName, string result, bool isError, long durationMs)
    {
        ToolName = toolName;
        Result = result;
        IsError = isError;
        DurationMs = durationMs;
        ColorScheme = isError ? ChatColors.ToolError : ChatColors.ToolSuccess;
        Width = Dim.Fill();
        Height = Dim.Auto();

        var icon = IsError ? "✗" : "✓";
        var header = new Label
        {
            Text = $"{icon} {toolName} ({FormatDuration(DurationMs)})",
            Width = Dim.Fill(),
            X = 0,
            Y = 0
        };

        // Result preview (first few lines)
        var lines = result.Split('\n').Take(6).ToList();
        var resultText = string.Join("\n", lines);
        if (result.Split('\n').Length > 6)
        {
            resultText += $"\n... {result.Split('\n').Length - 6} more lines";
        }

        var resultLabel = new Label
        {
            Text = resultText,
            Width = Dim.Fill(),
            Height = Dim.Auto(),
            X = 2,
            Y = 1
        };

        Add(header, resultLabel);
    }

    private static string FormatDuration(long ms) =>
        ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:F1}s";
}

/// <summary>
/// Status view - displays completion status with compact summary.
/// </summary>
public class StatusView : View
{
    public StatusView(bool success, int steps, long elapsedMs, int totalTokens,
        int thinkingTokens, int maxContext, int filesChanged)
    {
        ColorScheme = success ? ChatColors.ToolSuccess : ChatColors.ToolError;
        Width = Dim.Fill();
        Height = filesChanged > 0 ? 3 : 2;

        var icon = success ? "✓" : "✗";
        var elapsed = elapsedMs < 1000 ? $"{elapsedMs}ms" : $"{elapsedMs / 1000.0:F1}s";
        var pct = maxContext > 0 ? $" ({100 * totalTokens / maxContext}%)" : "";
        var stats = $"{icon} {steps} steps, {elapsed}, {FormatTokens(totalTokens)}/{FormatTokens(maxContext)}{pct}";
        if (thinkingTokens > 0)
            stats += $" +{FormatTokens(thinkingTokens)} thinking";

        var statusLabel = new Label
        {
            Text = stats,
            Width = Dim.Fill(),
            X = 0,
            Y = 0
        };

        Add(statusLabel);

        if (filesChanged > 0)
        {
            var filesLabel = new Label
            {
                Text = $"  {filesChanged} files changed — :files to list, :diff to view",
                Width = Dim.Fill(),
                X = 0,
                Y = 1
            };
            Add(filesLabel);
        }
    }

    private static string FormatTokens(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:F1}M"
        : tokens >= 1_000 ? $"{tokens / 1_000.0:F1}K"
        : $"{tokens}";
}

/// <summary>
/// Simple log message view for errors and system messages with level-based coloring.
/// </summary>
public class LogMessageView : Label
{
    public LogLevel Level { get; }

    public LogMessageView(string message, LogLevel level)
    {
        Level = level;
        Text = message;
        Width = Dim.Fill();

        ColorScheme = level switch
        {
            LogLevel.Error => ChatColors.ToolError,
            LogLevel.Warning => new ColorScheme
            {
                Normal = new Terminal.Gui.Attribute(Color.Yellow, Color.Black),
                Focus = new Terminal.Gui.Attribute(Color.Yellow, Color.Black),
                HotNormal = new Terminal.Gui.Attribute(Color.BrightYellow, Color.Black),
                HotFocus = new Terminal.Gui.Attribute(Color.BrightYellow, Color.Black)
            },
            LogLevel.Success => ChatColors.ToolSuccess,
            _ => ChatColors.Status
        };
    }
}

public enum LogLevel { Error, Warning, Info, Success }
