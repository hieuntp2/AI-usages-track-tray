using System.Diagnostics;
using System.IO;

namespace AiUsageTray.Infrastructure;

/// <summary>
/// Locates a CLI executable on PATH via `where.exe` and reads its version by invoking it with a
/// version flag, without assuming any particular installation method (npm, standalone binary, ...).
/// Shared by every provider whose detection follows this "where + --version" shape.
/// </summary>
public static class CliLocator
{
    // Generous: npm .cmd shims boot node, which can be slow on cold start or under AV scanning.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Orders `where.exe` matches so that files Windows can actually execute come first. npm global
    /// installs put an extensionless POSIX shell shim (for Git Bash) *ahead of* the runnable
    /// `.cmd` shim in the same directory, and `where` lists both - launching the extensionless one
    /// via CreateProcess just fails, which previously surfaced as "Could not determine version".
    /// </summary>
    public static IReadOnlyList<string> RankCandidates(IEnumerable<string> candidates) => candidates
        .Select(c => c.Trim())
        .Where(c => c.Length > 0)
        .OrderBy(c => Path.GetExtension(c).ToLowerInvariant() switch
        {
            ".exe" => 0,
            ".com" => 1,
            ".cmd" => 2,
            ".bat" => 3,
            "" => 5, // extensionless: almost certainly a POSIX shim, try last
            _ => 4,
        })
        .ToList();

    /// <summary>
    /// Finds the executable and reads its version in one pass: tries each ranked `where.exe` match
    /// until one successfully reports a version. Returns the path that actually worked, so callers
    /// spawn the same file that answered the probe. Falls back to (firstMatch, null) when something
    /// matched on PATH but nothing would run - "installed but unusable" beats "not installed".
    /// </summary>
    public static async Task<(string? Path, string? Version)> FindAndProbeAsync(string executableName, string versionArgs, CancellationToken cancellationToken)
    {
        var candidates = await FindExecutableCandidatesAsync(executableName, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return (null, null);
        }

        foreach (var candidate in candidates)
        {
            var version = await ReadVersionAsync(candidate, versionArgs, cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                return (candidate, version);
            }
        }

        return (candidates[0], null);
    }

    public static async Task<string?> FindExecutableAsync(string executableName, CancellationToken cancellationToken)
    {
        var candidates = await FindExecutableCandidatesAsync(executableName, cancellationToken).ConfigureAwait(false);
        return candidates.FirstOrDefault();
    }

    public static async Task<IReadOnlyList<string>> FindExecutableCandidatesAsync(string executableName, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = executableName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);

            var output = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await WaitForExitAsync(process, cts.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return Array.Empty<string>();
            }

            return RankCandidates(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch (Exception ex)
        {
            AppLog.Debug("CliLocator", $"where.exe probe for '{executableName}' failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public static async Task<string?> ReadVersionAsync(string executablePath, string versionArgs, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = versionArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);

            var output = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await WaitForExitAsync(process, cts.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return null;
            }

            return output.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        }
        catch (Exception ex)
        {
            AppLog.Debug("CliLocator", $"'{versionArgs}' probe for '{executablePath}' failed: {ex.Message}");
            return null;
        }
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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
            // Best effort.
        }
    }
}
