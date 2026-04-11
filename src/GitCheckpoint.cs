using System.Diagnostics;

namespace LittleHelperTui;

/// <summary>
/// Handles automatic git checkpoints before agent write operations.
/// </summary>
public class GitCheckpoint
{
    private readonly string _workingDir;
    private readonly string _mode;
    private bool _initialized;

    public GitCheckpoint(string workingDir, string mode)
    {
        _workingDir = workingDir;
        _mode = mode;
    }

    /// <summary>
    /// Ensure git is initialized if checkpointing is enabled.
    /// </summary>
    public void EnsureInitialized()
    {
        if (_mode == "off" || _initialized)
            return;

        var gitDir = Path.Combine(_workingDir, ".git");

        if (!Directory.Exists(gitDir) && _mode == "on")
        {
            // Auto-initialize git repo
            try
            {
                RunGit("init");
                RunGit("config user.email \"little-helper@local\"");
                RunGit("config user.name \"Little Helper\"");

                // Create initial commit if there are files
                RunGit("add -A");
                RunGit("commit -m \"Initial checkpoint\" --allow-empty");
            }
            catch { /* best effort */ }
        }

        _initialized = true;
    }

    /// <summary>
    /// Create a checkpoint before writing to a file.
    /// </summary>
    public void CheckpointBeforeWrite(string filePath)
    {
        if (_mode == "off")
            return;

        // Only checkpoint if git is already initialized (or mode is "on")
        var gitDir = Path.Combine(_workingDir, ".git");
        if (!Directory.Exists(gitDir) && _mode == "auto")
            return;

        try
        {
            var relativePath = Path.GetRelativePath(_workingDir, filePath);
            // Stage the current content if file exists
            if (File.Exists(filePath))
            {
                RunGit($"add \"{relativePath}\"");
                RunGit($"commit -m \"Checkpoint before modifying {relativePath}\" --allow-empty");
            }
        }
        catch { /* best effort */ }
    }

    private void RunGit(string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = _workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
    }
}
