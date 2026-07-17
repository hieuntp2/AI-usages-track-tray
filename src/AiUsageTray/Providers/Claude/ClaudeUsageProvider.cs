using AiUsageTray.Infrastructure;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.Claude;

/// <summary>
/// Surfaces Claude Code quota from two complementary sources. The status-line bridge's cache file
/// is preferred - it's a free, side-effect-free file read that also carries context/cost metrics -
/// but it only updates while the user is actually using Claude Code. Whenever the cache is missing,
/// stale, or a window's reset time has passed, the provider falls back to actively probing the CLI
/// headlessly (<see cref="ClaudeUsageProbe"/>), so usage keeps updating even when Claude Code is
/// never opened. The probe never sends a prompt to the model and consumes no quota.
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider, IDisposable
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    private const string BridgeSource = "claude-statusline-bridge";
    private const string CliProbeSource = "claude-cli";

    public string Id => "claude";

    public string DisplayName => "Claude Code";

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsActiveRefresh: true,
        SupportsPercentageWindows: true,
        SupportsMonetaryCost: true,
        SupportsRequestCounts: false,
        SupportsTokenCounts: true,
        RequiresSetup: true,
        RequiresNetwork: false);

    private readonly ClaudeBridgeInstaller _installer;
    private readonly ClaudeCacheReader _cacheReader;
    private readonly ClaudeUsageProbe _probe;
    private string? _cliExecutablePath;

    public event Action? CacheUpdated
    {
        add => _cacheReader.CacheChanged += value;
        remove => _cacheReader.CacheChanged -= value;
    }

    public ClaudeUsageProvider(ClaudeBridgeInstaller? installer = null, ClaudeCacheReader? cacheReader = null, ClaudeUsageProbe? probe = null)
    {
        _installer = installer ?? new ClaudeBridgeInstaller();
        _cacheReader = cacheReader ?? new ClaudeCacheReader(AppPaths.ClaudeLatestCacheFile);
        _probe = probe ?? new ClaudeUsageProbe();
        _cacheReader.StartWatching();
    }

    public async Task<ProviderDetectionResult> DetectAsync(CancellationToken cancellationToken)
    {
        var (path, version) = await CliLocator.FindAndProbeAsync("claude", "--version", cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return new ProviderDetectionResult(false, null, null, false, "Claude Code CLI not installed.");
        }

        _cliExecutablePath = path;
        return new ProviderDetectionResult(true, path, version, IsSupportedVersion: true, Message: null);
    }

    public Task<ProviderSetupResult> SetupAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_installer.Install(cancellationToken));

    public Task<ProviderSetupResult> RepairAsync() => Task.FromResult(_installer.Repair());

    public Task<ProviderSetupResult> RemoveIntegrationAsync() => Task.FromResult(_installer.Remove());

    public async Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        if (_cliExecutablePath is null)
        {
            return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.NotInstalled, BridgeSource, "Claude Code CLI not installed.");
        }

        var read = _cacheReader.ReadLatest();
        var cache = BuildCacheView(read);

        // A fresh bridge snapshot with real quota bars needs no process spawn - use it as-is.
        if (cache is { CanSkipProbe: true })
        {
            return cache.Snapshot;
        }

        // Cache missing, stale, quota-bar-less, or past a reset time: ask the CLI directly so the
        // card keeps updating even when the user never opens Claude Code.
        var probe = await _probe.ProbeAsync(_cliExecutablePath, cancellationToken).ConfigureAwait(false);
        if (probe.Status == ClaudeProbeStatus.Success)
        {
            AppLog.Info("ClaudeUsageProvider", $"CLI usage probe returned {probe.Usage!.Windows.Count} quota window(s).");
            return BuildProbeSnapshot(probe.Usage);
        }

        if (probe.Status == ClaudeProbeStatus.NotAuthenticated)
        {
            return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.NotAuthenticated, CliProbeSource, probe.Message);
        }

        AppLog.Warn("ClaudeUsageProvider", $"CLI usage probe failed ({probe.Status}): {probe.Message}");

        // Probe unavailable (old CLI, timeout, transient error): fall back to the passive
        // bridge-cache behavior, which still explains its own gaps.
        if (cache is not null)
        {
            return cache.Snapshot;
        }

        var bridgeStatus = _installer.GetStatus();
        if (bridgeStatus == ClaudeBridgeStatus.NotInstalled)
        {
            return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.SetupRequired, BridgeSource, "Integration not configured. Run setup to enable usage monitoring.");
        }

        if (bridgeStatus == ClaudeBridgeStatus.Damaged)
        {
            return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.Error, BridgeSource, "Integration damaged. Use \"Repair integration\" in Settings.");
        }

        if (!read.FileExists)
        {
            return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.SetupRequired, BridgeSource,
                "Waiting for first Claude response. Open Claude Code and send a normal prompt once.");
        }

        return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.Error, BridgeSource,
            read.Error ?? "Cached Claude data could not be read.");
    }

    private sealed record CacheView(UsageSnapshot Snapshot, bool CanSkipProbe);

    /// <summary>
    /// Builds the bridge-cache view of usage, or null when the cache has no readable payload.
    /// <see cref="CacheView.CanSkipProbe"/> is true only when the snapshot is fresh, shows actual
    /// quota windows, and no window's reset time has passed - anything less and the caller should
    /// try the CLI probe for better data.
    /// </summary>
    private CacheView? BuildCacheView(ClaudeCacheReadResult read)
    {
        if (read.Envelope?.Payload is not { } payload)
        {
            return null;
        }

        var capturedAt = read.Envelope.CapturedAt ?? read.FileWriteTime ?? DateTimeOffset.UtcNow;
        var age = DateTimeOffset.UtcNow - capturedAt;
        var isStale = age > StaleAfter;

        var windows = ClaudeCacheParser.BuildWindows(payload);
        var metrics = ClaudeCacheParser.BuildMetrics(payload);
        var resetPassed = ClaudeCacheParser.HasResetTimePassed(windows, DateTimeOffset.UtcNow);

        // rate_limits only appears for Pro/Max accounts, and only after the session's first API
        // response - a payload without it is normal early on, not an error, but the card should
        // say why no quota bars are showing instead of silently rendering an empty card.
        var noRateLimitsMessage = payload.RateLimits is null
            ? "Claude hasn't reported rate limits yet. They appear for Pro/Max accounts after the first response in a session."
            : null;

        var message = (resetPassed ? "Limit may have reset. Refreshing from the Claude CLI…" : null)
            ?? noRateLimitsMessage
            ?? (isStale ? $"Last updated {FormatAge(age)} ago." : null);

        var snapshot = new UsageSnapshot(
            ProviderId: Id,
            ProviderName: DisplayName,
            AccountLabel: payload.SessionId is null ? null : $"Session {Shorten(payload.SessionId)}",
            PlanName: payload.Model?.DisplayName ?? payload.Model?.Id,
            Status: isStale ? ProviderConnectionStatus.Stale : ProviderConnectionStatus.Available,
            CapturedAt: capturedAt,
            Source: BridgeSource,
            Windows: windows,
            Metrics: metrics,
            Message: message);

        return new CacheView(snapshot, CanSkipProbe: !isStale && !resetPassed && windows.Count > 0);
    }

    /// <summary>Snapshot built from a headless CLI probe. Quota windows only: the probe's own
    /// session cost/context are meaningless to the user, and stale cache metrics would mislead.</summary>
    private UsageSnapshot BuildProbeSnapshot(ClaudeCliUsageResult usage) => new(
        ProviderId: Id,
        ProviderName: DisplayName,
        AccountLabel: null,
        PlanName: null,
        Status: ProviderConnectionStatus.Available,
        CapturedAt: DateTimeOffset.UtcNow,
        Source: CliProbeSource,
        Windows: ClaudeUsageOutputParser.ToUsageWindows(usage),
        Metrics: Array.Empty<UsageMetric>(),
        Message: null);

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
