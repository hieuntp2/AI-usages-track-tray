using System.Diagnostics;
using System.Text;
using AiUsageTray.Infrastructure;

namespace AiUsageTray.Providers.Claude;

/// <summary>
/// Actively reads Claude Code quota with no user interaction by driving the CLI headlessly:
/// `claude auth status --json` confirms sign-in, then `claude --safe-mode --ax-screen-reader /usage`
/// prints the usage panel as flat one-line-per-window text (the non-TTY render) and exits on its
/// own. Neither call ever sends a prompt to the model, so probing consumes zero quota.
///
/// `--safe-mode` keeps the probe inert (hooks, plugins, MCP servers, and CLAUDE.md discovery are
/// all skipped) while OAuth/subscription auth still works - unlike `--bare`, which drops OAuth and
/// would break subscription accounts. `--ax-screen-reader` doubles as a version gate: builds too
/// old to drive `/usage` this way reject the unknown flag and fail fast, *before* anything could
/// reach the model.
/// </summary>
public sealed class ClaudeUsageProbe
{
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UsageTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Floor between CLI spawns: a probe boots the whole CLI, so rapid manual refreshes reuse the
    /// last result instead of stacking node/CLI processes. Background polling (default 300s) is
    /// unaffected.
    /// </summary>
    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(90);

    // Same pattern as CodexProcessSupervisor: one app-lifetime job object, never disposed - the OS
    // closing the handle at process death is exactly what kills any still-running probe child.
    private static readonly Lazy<ChildProcessJob> KillOnExitJob = new(() => new ChildProcessJob());

    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private ClaudeUsageProbeResult? _lastResult;
    private DateTimeOffset _lastCompletedAt;

    /// <summary>Runs one probe (auth check, then /usage), serialized and rate-limited.</summary>
    public async Task<ClaudeUsageProbeResult> ProbeAsync(string cliExecutablePath, CancellationToken cancellationToken)
    {
        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastResult is not null && DateTimeOffset.UtcNow - _lastCompletedAt < MinInterval)
            {
                return _lastResult;
            }

            var result = await ProbeCoreAsync(cliExecutablePath, cancellationToken).ConfigureAwait(false);
            _lastResult = result;
            _lastCompletedAt = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    private static async Task<ClaudeUsageProbeResult> ProbeCoreAsync(string cliExecutablePath, CancellationToken cancellationToken)
    {
        var auth = await RunCliAsync(cliExecutablePath, "auth status --json", AuthTimeout, cancellationToken).ConfigureAwait(false);
        if (auth.TimedOut)
        {
            return ClaudeUsageProbeResult.Fail(ClaudeProbeStatus.Timeout, "Claude CLI auth check timed out.");
        }

        if (ClaudeAuthStatusParser.TryParse(auth.StdOut, out var authStatus, out var authError))
        {
            if (!authStatus!.LoggedIn)
            {
                return ClaudeUsageProbeResult.Fail(ClaudeProbeStatus.NotAuthenticated,
                    "Claude Code is signed out. Run `claude` in a terminal and sign in.");
            }
        }
        else if (auth.ExitCode != 0)
        {
            // Old builds without `auth status --json` land here; don't guess, just report.
            return ClaudeUsageProbeResult.Fail(ClaudeProbeStatus.Unsupported,
                $"Claude CLI auth check failed: {FirstLine(auth.StdErr, auth.StdOut) ?? authError ?? "unknown error"}");
        }

        var usage = await RunCliAsync(cliExecutablePath, "--safe-mode --ax-screen-reader /usage", UsageTimeout, cancellationToken).ConfigureAwait(false);
        if (usage.TimedOut)
        {
            return ClaudeUsageProbeResult.Fail(ClaudeProbeStatus.Timeout, "Claude CLI /usage probe timed out.");
        }

        if (usage.ExitCode != 0)
        {
            var detail = FirstLine(usage.StdErr, usage.StdOut);
            var unsupported = detail?.Contains("unknown option", StringComparison.OrdinalIgnoreCase) == true
                || detail?.Contains("unknown command", StringComparison.OrdinalIgnoreCase) == true;
            return ClaudeUsageProbeResult.Fail(
                unsupported ? ClaudeProbeStatus.Unsupported : ClaudeProbeStatus.Error,
                unsupported
                    ? "Installed Claude Code build is too old for headless /usage. Update with `claude update`."
                    : $"Claude CLI /usage probe failed: {detail ?? "unknown error"}");
        }

        var parsed = ClaudeUsageOutputParser.Parse(usage.StdOut);
        if (parsed.Windows.Count == 0)
        {
            // Exit 0 but nothing recognizable: most likely a panel wording change. Surface enough
            // to diagnose without dumping the whole capture into the UI.
            return ClaudeUsageProbeResult.Fail(ClaudeProbeStatus.Unsupported,
                $"Could not read quota from /usage output ({FirstLine(usage.StdOut, null) ?? "empty output"}).");
        }

        return ClaudeUsageProbeResult.Ok(parsed);
    }

    private sealed record CliRun(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    private static async Task<CliRun> RunCliAsync(string executablePath, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                // Redirected + immediately closed stdin gives the CLI a clean EOF; combined with
                // piped stdout it selects the flat non-interactive render instead of the TUI.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Neutral, app-owned working directory - never a user repo, so no project
                // settings, trust state, or CLAUDE.md can differ between probes.
                WorkingDirectory = AppPaths.Root,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            },
        };

        process.StartInfo.Environment["NO_COLOR"] = "1";
        process.StartInfo.Environment["DISABLE_AUTOUPDATER"] = "1";

        process.Start();
        KillOnExitJob.Value.TryAssign(process);
        process.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return new CliRun(process.ExitCode, await stdOutTask.ConfigureAwait(false), await stdErrTask.ConfigureAwait(false), TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            AppLog.Warn("ClaudeUsageProbe", $"'{arguments}' did not finish within {timeout.TotalSeconds:0}s; killed.");
            return new CliRun(-1, string.Empty, string.Empty, TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort; the job object reaps anything left when the app exits.
        }
    }

    private static string? FirstLine(string primary, string? fallback)
    {
        var text = string.IsNullOrWhiteSpace(primary) ? fallback : primary;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.TrimStart().Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }
}
