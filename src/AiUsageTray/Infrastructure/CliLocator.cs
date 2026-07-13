using System.Diagnostics;

namespace AiUsageTray.Infrastructure;

/// <summary>
/// Locates a CLI executable on PATH via `where.exe` and reads its version by invoking it with a
/// version flag, without assuming any particular installation method (npm, standalone binary, ...).
/// Shared by every provider whose detection follows this "where + --version" shape.
/// </summary>
public static class CliLocator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static async Task<string?> FindExecutableAsync(string executableName, CancellationToken cancellationToken)
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
                return null;
            }

            var firstLine = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
        }
        catch (Exception ex)
        {
            AppLog.Debug("CliLocator", $"where.exe probe for '{executableName}' failed: {ex.Message}");
            return null;
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
