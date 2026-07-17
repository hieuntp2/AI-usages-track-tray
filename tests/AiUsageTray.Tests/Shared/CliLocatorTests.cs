using AiUsageTray.Infrastructure;
using AiUsageTray.Providers.Codex;
using Xunit;

namespace AiUsageTray.Tests.Shared;

public class CliLocatorTests
{
    [Fact]
    public void RankCandidates_NpmShimLayout_PrefersCmdOverExtensionlessPosixShim()
    {
        // Exactly what `where.exe codex` returns for an npm global install: the unrunnable
        // POSIX shim first, the runnable .cmd second. The .cmd must win.
        var ranked = CliLocator.RankCandidates(new[]
        {
            @"C:\Users\someone\AppData\Roaming\npm\codex",
            @"C:\Users\someone\AppData\Roaming\npm\codex.cmd",
        });

        Assert.Equal(@"C:\Users\someone\AppData\Roaming\npm\codex.cmd", ranked[0]);
    }

    [Fact]
    public void RankCandidates_ExeBeatsEverything()
    {
        var ranked = CliLocator.RankCandidates(new[]
        {
            @"C:\tools\claude.cmd",
            @"C:\tools\claude",
            @"C:\Program Files\Claude\claude.exe",
            @"C:\tools\claude.bat",
        });

        Assert.Equal(@"C:\Program Files\Claude\claude.exe", ranked[0]);
        Assert.Equal(@"C:\tools\claude", ranked[^1]); // extensionless always last
    }

    [Fact]
    public void RankCandidates_EmptyAndWhitespaceLines_Dropped()
    {
        var ranked = CliLocator.RankCandidates(new[] { "", "  ", @"C:\x\tool.exe" });

        Assert.Single(ranked);
    }

    /// <summary>
    /// Live smoke test: on a machine where a `codex` CLI resolves on PATH (like this dev machine,
    /// where the npm shim layout originally broke detection), detection must produce a version.
    /// On machines without codex it passes trivially - no network access either way.
    /// </summary>
    [Fact]
    public async Task DetectAsync_WhenCodexOnPath_ResolvesRunnableFileAndVersion()
    {
        var detection = await CodexCliLocator.DetectAsync(CancellationToken.None);

        if (!detection.IsInstalled)
        {
            return; // codex not present on this machine - nothing to assert
        }

        Assert.NotNull(detection.Version);
        Assert.True(detection.IsSupportedVersion, detection.Message);
    }
}
