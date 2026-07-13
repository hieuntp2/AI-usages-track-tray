namespace AiUsageTray.Models;

/// <summary>
/// Connection/health state of a provider, surfaced directly in the UI.
/// </summary>
public enum ProviderConnectionStatus
{
    Unknown,
    Available,
    Refreshing,
    NotInstalled,
    NotAuthenticated,
    SetupRequired,
    Stale,
    Error,
    UnsupportedVersion,
}

/// <summary>
/// A single quota window (e.g. "5-hour limit", "Weekly limit", "Monthly credits").
/// Not every provider populates every field - absence means "unknown", never zero.
/// </summary>
public sealed record UsageWindow(
    string Id,
    string DisplayName,
    decimal? UsedPercent,
    decimal? RemainingPercent,
    DateTimeOffset? ResetsAt,
    TimeSpan? Duration,
    decimal? UsedValue,
    decimal? LimitValue,
    string? Unit)
{
    /// <summary>
    /// Clamps a raw percentage into [0, 100] for display while preserving the caller's
    /// ability to log the original (possibly malformed) value separately.
    /// </summary>
    public static decimal? ClampPercent(decimal? value)
    {
        if (value is null)
        {
            return null;
        }

        return Math.Clamp(value.Value, 0m, 100m);
    }
}

/// <summary>
/// A provider-specific scalar metric that does not fit the used/remaining/reset shape of a
/// <see cref="UsageWindow"/> (e.g. lifetime tokens, current streak, session cost).
/// </summary>
public sealed record UsageMetric(
    string Id,
    string DisplayName,
    decimal Value,
    string Unit);

/// <summary>
/// Normalized, provider-agnostic usage snapshot. This is the only shape the UI layer binds to;
/// provider-specific fields never leak past this record (raw payloads are attached separately
/// and only retained for diagnostics).
/// </summary>
public sealed record UsageSnapshot(
    string ProviderId,
    string ProviderName,
    string? AccountLabel,
    string? PlanName,
    ProviderConnectionStatus Status,
    DateTimeOffset CapturedAt,
    string Source,
    IReadOnlyList<UsageWindow> Windows,
    IReadOnlyList<UsageMetric> Metrics,
    string? Message)
{
    public static UsageSnapshot Empty(string providerId, string providerName, ProviderConnectionStatus status, string source, string? message = null) =>
        new(providerId, providerName, null, null, status, DateTimeOffset.UtcNow, source, Array.Empty<UsageWindow>(), Array.Empty<UsageMetric>(), message);
}

/// <summary>Result of probing whether a provider's client is installed/reachable.</summary>
public sealed record ProviderDetectionResult(
    bool IsInstalled,
    string? ExecutablePath,
    string? Version,
    bool IsSupportedVersion,
    string? Message);

/// <summary>Result of running a provider's (optional) setup flow, e.g. installing a bridge script.</summary>
public sealed record ProviderSetupResult(
    bool Success,
    string? Message);

/// <summary>
/// Declares what a provider can/can't do so the UI and orchestrator don't have to special-case
/// providers by name. New providers should be addable purely by implementing this contract.
/// </summary>
public sealed record ProviderCapabilities(
    bool SupportsActiveRefresh,
    bool SupportsPercentageWindows,
    bool SupportsMonetaryCost,
    bool SupportsRequestCounts,
    bool SupportsTokenCounts,
    bool RequiresSetup,
    bool RequiresNetwork);
