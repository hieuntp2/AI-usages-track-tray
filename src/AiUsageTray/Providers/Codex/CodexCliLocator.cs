using AiUsageTray.Infrastructure;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.Codex;

/// <summary>
/// Locates the Codex CLI on PATH and reads its version, without assuming any particular
/// installation method (npm global install, standalone binary, etc.). Multiple PATH matches are
/// probed in executable-preference order - npm installs ship both a POSIX shim and a .cmd shim,
/// and only the latter is launchable from a Windows process.
/// </summary>
public static class CodexCliLocator
{
    public static async Task<ProviderDetectionResult> DetectAsync(CancellationToken cancellationToken)
    {
        var (path, version) = await CliLocator.FindAndProbeAsync("codex", "--version", cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return new ProviderDetectionResult(false, null, null, false, "Codex CLI not installed.");
        }

        if (version is null)
        {
            return new ProviderDetectionResult(true, path, null, false, "Could not determine Codex CLI version.");
        }

        return new ProviderDetectionResult(true, path, version, IsSupportedVersion: true, Message: null);
    }
}
