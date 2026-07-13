using AiUsageTray.Infrastructure;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.Codex;

/// <summary>
/// Locates the Codex CLI on PATH and reads its version, without assuming any particular
/// installation method (npm global install, standalone binary, etc.) - it only relies on the
/// `codex` executable being resolvable via the Windows `where` mechanism.
/// </summary>
public static class CodexCliLocator
{
    public static async Task<ProviderDetectionResult> DetectAsync(CancellationToken cancellationToken)
    {
        var path = await CliLocator.FindExecutableAsync("codex", cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return new ProviderDetectionResult(false, null, null, false, "Codex CLI not installed.");
        }

        var version = await CliLocator.ReadVersionAsync(path, "--version", cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            return new ProviderDetectionResult(true, path, null, false, "Could not determine Codex CLI version.");
        }

        return new ProviderDetectionResult(true, path, version, IsSupportedVersion: true, Message: null);
    }
}
