using System.Text.Json;
using AiUsageTray.Infrastructure;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.Codex;

/// <summary>
/// Reads OpenAI Codex CLI quota via `codex app-server`'s JSON-RPC surface. Never starts a Codex
/// thread/turn - only calls the read-only account/rateLimits endpoints - so refreshing usage can
/// never consume the user's Codex quota or send a prompt on their behalf.
/// </summary>
public sealed class CodexUsageProvider : IUsageProvider, IDisposable
{
    public string Id => "codex";

    public string DisplayName => "OpenAI Codex";

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsActiveRefresh: true,
        SupportsPercentageWindows: true,
        SupportsMonetaryCost: false,
        SupportsRequestCounts: false,
        SupportsTokenCounts: true,
        RequiresSetup: false,
        RequiresNetwork: false);

    private readonly object _stateLock = new();
    private CodexProcessSupervisor? _supervisor;
    private CodexRateLimitState _rateLimitState = new();
    private string? _executablePath;
    private string? _version;
    private bool _versionSupported = true;
    private DateTimeOffset? _lastSuccessAt;

    public async Task<ProviderDetectionResult> DetectAsync(CancellationToken cancellationToken)
    {
        var detection = await CodexCliLocator.DetectAsync(cancellationToken).ConfigureAwait(false);

        lock (_stateLock)
        {
            _executablePath = detection.ExecutablePath;
            _version = detection.Version;
            _versionSupported = detection.IsSupportedVersion;
        }

        return detection;
    }

    public Task<ProviderSetupResult> SetupAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderSetupResult(true, "Codex requires no setup - detection and quota reads run automatically."));

    public async Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        string? executablePath;
        bool versionSupported;
        lock (_stateLock)
        {
            executablePath = _executablePath;
            versionSupported = _versionSupported;
        }

        if (executablePath is null)
        {
            return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.NotInstalled, "codex-cli", "Codex CLI not installed.");
        }

        if (!versionSupported)
        {
            return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.UnsupportedVersion, "codex-cli", "Update Codex CLI to enable usage monitoring.");
        }

        var supervisor = GetOrCreateSupervisor(executablePath);

        try
        {
            var accountTask = supervisor.SendRequestAsync("account/read", new { refreshToken = false }, cancellationToken);
            var rateLimitsTask = supervisor.SendRequestAsync("account/rateLimits/read", null, cancellationToken);

            await Task.WhenAll(accountTask, rateLimitsTask).ConfigureAwait(false);

            var (accountLabel, planName) = CodexUsageParser.ParseAccount(await accountTask.ConfigureAwait(false));

            var freshState = CodexRateLimitParser.Parse(await rateLimitsTask.ConfigureAwait(false));
            lock (_stateLock)
            {
                _rateLimitState.Merge(freshState);
            }

            IReadOnlyList<UsageMetric> usageMetrics = Array.Empty<UsageMetric>();
            try
            {
                var usageResult = await supervisor.SendRequestAsync("account/usage/read", null, cancellationToken).ConfigureAwait(false);
                usageMetrics = CodexUsageParser.ParseUsageMetrics(usageResult);
            }
            catch (CodexRpcErrorException ex)
            {
                AppLog.Debug("Codex.Provider", $"account/usage/read unsupported by this server: {ex.Message}");
            }

            CodexRateLimitState snapshotState;
            lock (_stateLock)
            {
                snapshotState = _rateLimitState;
                _lastSuccessAt = DateTimeOffset.UtcNow;
            }

            var windows = CodexRateLimitParser.ToUsageWindows(snapshotState);
            var metrics = CodexRateLimitParser.ToCreditMetrics(snapshotState).Concat(usageMetrics).ToList();

            return new UsageSnapshot(
                ProviderId: Id,
                ProviderName: DisplayName,
                AccountLabel: accountLabel,
                PlanName: planName ?? snapshotState.PlanType,
                Status: ProviderConnectionStatus.Available,
                CapturedAt: DateTimeOffset.UtcNow,
                Source: "codex app-server",
                Windows: windows,
                Metrics: metrics,
                Message: null);
        }
        catch (CodexProcessNotRunningException ex)
        {
            return BuildErrorSnapshot(ProviderConnectionStatus.Error, $"Codex app-server unavailable: {ex.Message}");
        }
        catch (CodexRequestTimeoutException ex)
        {
            return BuildErrorSnapshot(ProviderConnectionStatus.Error, ex.Message);
        }
        catch (CodexRpcErrorException ex) when (IsAuthError(ex))
        {
            return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.NotAuthenticated, "codex app-server", "Codex is not signed in.");
        }
        catch (CodexRpcErrorException ex)
        {
            return BuildErrorSnapshot(ProviderConnectionStatus.Error, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return BuildErrorSnapshot(ProviderConnectionStatus.Error, "Refresh was cancelled.");
        }
    }

    private static bool IsAuthError(CodexRpcErrorException ex) =>
        ex.Message.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("not logged in", StringComparison.OrdinalIgnoreCase);

    private UsageSnapshot BuildErrorSnapshot(ProviderConnectionStatus status, string message)
    {
        DateTimeOffset? lastSuccessAt;
        CodexRateLimitState state;
        lock (_stateLock)
        {
            lastSuccessAt = _lastSuccessAt;
            state = _rateLimitState;
        }

        // Preserve the last known-good windows so a transient error doesn't blank the UI.
        var windows = lastSuccessAt is null ? Array.Empty<UsageWindow>() : CodexRateLimitParser.ToUsageWindows(state);

        return new UsageSnapshot(
            Id, DisplayName, null, state.PlanType,
            lastSuccessAt is null ? status : ProviderConnectionStatus.Stale,
            DateTimeOffset.UtcNow, "codex app-server", windows, Array.Empty<UsageMetric>(), message);
    }

    private CodexProcessSupervisor GetOrCreateSupervisor(string executablePath)
    {
        lock (_stateLock)
        {
            if (_supervisor is not null)
            {
                return _supervisor;
            }

            var supervisor = new CodexProcessSupervisor(executablePath);
            supervisor.NotificationReceived += OnNotification;
            _supervisor = supervisor;
            return supervisor;
        }
    }

    private void OnNotification(string method, JsonElement? args)
    {
        if (method != "account/rateLimits/updated" || args is not { } payload)
        {
            return;
        }

        try
        {
            var update = CodexRateLimitParser.Parse(payload);
            lock (_stateLock)
            {
                _rateLimitState.Merge(update);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Codex.Provider", $"Failed to merge rateLimits notification: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _supervisor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
