using Terminal.Gui;

namespace LittleHelperTui.Views;

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
        Title = $"Assistant Step {step}";
        BorderStyle = LineStyle.Single;
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
/// Thinking view - displays thinking content.
/// </summary>
public class ThinkingView : FrameView
{
    public int ThinkingTokens { get; }
    public int ContextTokens { get; }

    public ThinkingView(string content, int thinkingTokens, int contextTokens)
    {
        ThinkingTokens = thinkingTokens;
        ContextTokens = contextTokens;
        Title = "Thinking";
        BorderStyle = LineStyle.Single;
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
/// Tool result view - displays tool execution results.
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
/// Status view - displays completion status.
/// </summary>
public class StatusView : View
{
    public bool Success { get; }
    public int Steps { get; }
    public long ElapsedMs { get; }
    public int TotalTokens { get; }
    public int ThinkingTokens { get; }
    public int MaxContext { get; }
    public int FilesChanged { get; }

    public StatusView(bool success, int steps, long elapsedMs, int totalTokens,
        int thinkingTokens, int maxContext, int filesChanged)
    {
        Success = success;
        Steps = steps;
        ElapsedMs = elapsedMs;
        TotalTokens = totalTokens;
        ThinkingTokens = thinkingTokens;
        MaxContext = maxContext;
        FilesChanged = filesChanged;
        Width = Dim.Fill();
        Height = FilesChanged > 0 ? 3 : 2;

        var icon = Success ? "✓" : "✗";
        var elapsed = elapsedMs < 1000 ? $"{elapsedMs}ms" : $"{elapsedMs / 1000.0:F1}s";
        var stats = $"{icon} Done - {steps} steps, {elapsed}, {FormatTokens(TotalTokens)}/{FormatTokens(MaxContext)} context";
        if (thinkingTokens > 0)
            stats += $" ({FormatTokens(thinkingTokens)} thinking)";

        var statusLabel = new Label
        {
            Text = stats,
            Width = Dim.Fill(),
            X = 0,
            Y = 0
        };

        Add(statusLabel);

        if (FilesChanged > 0)
        {
            var filesLabel = new Label
            {
                Text = $"  {FilesChanged} files changed. Use :files to list.",
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
/// Simple log message view for errors and system messages.
/// </summary>
public class LogMessageView : Label
{
    public LogLevel Level { get; }

    public LogMessageView(string message, LogLevel level)
    {
        Level = level;
        Text = message;
        Width = Dim.Fill();

        var color = level switch
        {
            LogLevel.Error => Color.Red,
            LogLevel.Warning => Color.Yellow,
            LogLevel.Info => Color.Gray,
            LogLevel.Success => Color.Green,
            _ => Color.White
        };

        // Note: ColorScheme is set by the parent or application
    }
}

public enum LogLevel { Error, Warning, Info, Success }
