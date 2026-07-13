using AiUsageTray.Infrastructure;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.Claude;

/// <summary>
/// Surfaces Claude Code quota purely from the status-line bridge's cache file. This provider never
/// launches Claude Code and never sends a prompt - RefreshAsync is a cheap, side-effect-free file
/// read, since usage data is event-driven (populated only when the user's own Claude Code session
/// produces a status-line update).
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider, IDisposable
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public string Id => "claude";

    public string DisplayName => "Claude Code";

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsActiveRefresh: false,
        SupportsPercentageWindows: true,
        SupportsMonetaryCost: true,
        SupportsRequestCounts: false,
        SupportsTokenCounts: true,
        RequiresSetup: true,
        RequiresNetwork: false);

    private readonly ClaudeBridgeInstaller _installer;
    private readonly ClaudeCacheReader _cacheReader;
    private string? _cliExecutablePath;

    public event Action? CacheUpdated
    {
        add => _cacheReader.CacheChanged += value;
        remove => _cacheReader.CacheChanged -= value;
    }

    public ClaudeUsageProvider(ClaudeBridgeInstaller? installer = null, ClaudeCacheReader? cacheReader = null)
    {
        _installer = installer ?? new ClaudeBridgeInstaller();
        _cacheReader = cacheReader ?? new ClaudeCacheReader(AppPaths.ClaudeLatestCacheFile);
        _cacheReader.StartWatching();
    }

    public async Task<ProviderDetectionResult> DetectAsync(CancellationToken cancellationToken)
    {
        var path = await CliLocator.FindExecutableAsync("claude", cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return new ProviderDetectionResult(false, null, null, false, "Claude Code CLI not installed.");
        }

        _cliExecutablePath = path;
        var version = await CliLocator.ReadVersionAsync(path, "--version", cancellationToken).ConfigureAwait(false);
        return new ProviderDetectionResult(true, path, version, IsSupportedVersion: true, Message: null);
    }

    public Task<ProviderSetupResult> SetupAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_installer.Install(cancellationToken));

    public Task<ProviderSetupResult> RepairAsync() => Task.FromResult(_installer.Repair());

    public Task<ProviderSetupResult> RemoveIntegrationAsync() => Task.FromResult(_installer.Remove());

    public Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        if (_cliExecutablePath is null)
        {
            return Task.FromResult(UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.NotInstalled, "claude-statusline-bridge", "Claude Code CLI not installed."));
        }

        var bridgeStatus = _installer.GetStatus();
        if (bridgeStatus == ClaudeBridgeStatus.NotInstalled)
        {
            return Task.FromResult(UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.SetupRequired, "claude-statusline-bridge", "Integration not configured. Run setup to enable usage monitoring."));
        }

        if (bridgeStatus == ClaudeBridgeStatus.Damaged)
        {
            return Task.FromResult(UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.Error, "claude-statusline-bridge", "Integration damaged. Use \"Repair integration\" in Settings."));
        }

        var read = _cacheReader.ReadLatest();

        if (!read.FileExists)
        {
            return Task.FromResult(UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.SetupRequired, "claude-statusline-bridge",
                "Waiting for first Claude response. Open Claude Code and send a normal prompt once."));
        }

        if (read.Envelope?.Payload is not { } payload)
        {
            return Task.FromResult(UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.Error, "claude-statusline-bridge",
                read.Error ?? "Cached Claude data could not be read."));
        }

        var capturedAt = read.Envelope.CapturedAt ?? read.FileWriteTime ?? DateTimeOffset.UtcNow;
        var age = DateTimeOffset.UtcNow - capturedAt;
        var isStale = age > StaleAfter;

        var windows = ClaudeCacheParser.BuildWindows(payload);
        var metrics = ClaudeCacheParser.BuildMetrics(payload);

        var message = BuildResetPassedMessageIfNeeded(windows) ?? (isStale
            ? $"Last updated {FormatAge(age)} ago."
            : null);

        return Task.FromResult(new UsageSnapshot(
            ProviderId: Id,
            ProviderName: DisplayName,
            AccountLabel: payload.SessionId is null ? null : $"Session {Shorten(payload.SessionId)}",
            PlanName: payload.Model?.DisplayName ?? payload.Model?.Id,
            Status: isStale ? ProviderConnectionStatus.Stale : ProviderConnectionStatus.Available,
            CapturedAt: capturedAt,
            Source: "claude-statusline-bridge",
            Windows: windows,
            Metrics: metrics,
            Message: message));
    }

    /// <summary>
    /// If a window's reset time has already passed and we haven't seen a fresher snapshot since,
    /// we must not silently claim 0% used - the real state is unknown until Claude reports again.
    /// </summary>
    private static string? BuildResetPassedMessageIfNeeded(IReadOnlyList<UsageWindow> windows)
    {
        if (ClaudeCacheParser.HasResetTimePassed(windows, DateTimeOffset.UtcNow))
        {
            return "Limit may have reset. Open Claude Code to confirm current usage.";
        }

        return null;
    }

    private static string Shorten(string sessionId) => sessionId.Length <= 8 ? sessionId : sessionId[..8];

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1)
        {
            return "less than a minute";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes} minute{((int)age.TotalMinutes == 1 ? "" : "s")}";
        }

        return $"{(int)age.TotalHours} hour{((int)age.TotalHours == 1 ? "" : "s")}";
    }

    public void Dispose() => _cacheReader.Dispose();
}
