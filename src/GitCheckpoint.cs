using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;

namespace LittleHelperTui;

/// <summary>
/// Git-based checkpoints before write operations.
/// Creates a local commit snapshot of the file about to be overwritten,
/// so the user can always roll back if the agent makes a bad edit.
///
/// Modes (from tui.json git_checkpoint):
///   "auto" = checkpoint only if .git already exists in working dir
///   "on"   = always checkpoint (git init if needed, stays local)
///   "off"  = never checkpoint
/// </summary>
public class GitCheckpoint
{
    private readonly string _workingDir;
    private readonly string _mode; // auto, on, off
    private readonly IAnsiConsole? _console;

    // Lazy-evaluated: is git initialized in this working dir?
    private bool? _gitReady;

    public GitCheckpoint(string workingDir, string mode, IAnsiConsole? console = null)
    {
        _workingDir = workingDir;
        _mode = mode.ToLowerInvariant();
        _console = console;
    }

    /// <summary>
    /// Whether checkpoints are effectively active for this working directory.
    /// Resolves the "auto" mode by checking if .git exists.
    /// </summary>
    public bool IsActive
    {
        get
        {
            if (_mode == "off") return false;
            if (_mode == "on") return true;
            // "auto" — check once
            _gitReady ??= Directory.Exists(Path.Combine(_workingDir, ".git"));
            return _gitReady.Value;
        }
    }

    /// <summary>
    /// Ensure git is initialized (for "on" mode in a fresh directory).
    /// Called once when the agent starts a run, not per-file.
    /// </summary>
    public void EnsureInitialized()
    {
        if (_mode != "on") return;
        if (Directory.Exists(Path.Combine(_workingDir, ".git"))) return;

        // git init
        var init = RunGit("init");
        if (init.ExitCode != 0)
        {
            _console?.MarkupLine($"[red]! git init failed: {Markup.Escape(init.Output)}[/]");
            return;
        }

        // Make an initial commit of whatever is there (or allow empty)
        // Configure user locally so we don't need global git config
        RunGit("config user.email \"little-helper@local\"");
        RunGit("config user.name \"little helper\"");

        // Add everything and commit, or make an empty commit if dir is empty
        var addResult = RunGit("add -A");
        var status = RunGit("status --porcelain");
        if (string.IsNullOrWhiteSpace(status.Output))
        {
            // Empty directory -- make an empty initial commit
            RunGit("commit --allow-empty -m \"checkpoint: initial (empty directory)\"");
        }
        else
        {
            RunGit("commit -m \"checkpoint: initial state\"");
        }

        _console?.MarkupLine("[dim][yellow]*[/] Initialized local git for checkpoints[/]");
        _gitReady = true;
    }

    /// <summary>
    /// Create a checkpoint commit before a write operation overwrites a file.
    /// Only commits if there are staged changes since last checkpoint.
    /// </summary>
    public void CheckpointBeforeWrite(string filePath)
    {
        if (!IsActive) return;

        try
        {
            var relativePath = Path.GetRelativePath(_workingDir, filePath);

            // Stage the specific file (captures current state before overwrite)
            RunGit($"add -- \"{EscapeGitPath(relativePath)}\"");

            // Check if there's anything staged
            var diff = RunGit("diff --cached --quiet");
            if (diff.ExitCode == 0)
            {
                // Nothing staged -- no need to commit
                return;
            }

            // Create the checkpoint commit
            var commitMsg = $"checkpoint: before write {relativePath}";
            var result = RunGit($"commit -m \"{commitMsg}\"");

            if (result.ExitCode == 0)
            {
                _console?.MarkupLine(
                    $"[dim][yellow]*[/] Git checkpoint: {Markup.Escape(relativePath)}[/]");
            }
        }
        catch (Exception ex)
        {
            // Best effort -- don't block the agent if git fails
            _console?.MarkupLine(
                $"[dim][yellow]*[/] Git checkpoint skipped: {Markup.Escape(ex.Message)}[/]");
        }
    }

    // --- Git helpers ---

    private GitRunResult RunGit(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = _workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return new GitRunResult(-1, "", "Failed to start git");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(10000); // 10s timeout

            return new GitRunResult(process.ExitCode, stdout.Trim(), stderr.Trim());
        }
        catch (Exception ex)
        {
            return new GitRunResult(-1, "", ex.Message);
        }
    }

    /// <summary>Escape a path for git CLI arguments (basic shell safety).</summary>
    private static string EscapeGitPath(string path)
    {
        // Replace backslashes (Windows), keep forward slashes
        return path.Replace('\\', '/')
            .Replace("\"", "\\\"")
            .Replace("$", "\\$");
    }

    private record GitRunResult(int ExitCode, string Output, string Error);
}
