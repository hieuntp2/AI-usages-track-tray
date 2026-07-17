namespace AiUsageTray.Providers.Claude;

/// <summary>
/// Outcome of a headless Claude auth check (`claude auth status --json`). Deliberately narrow: we
/// keep only what governs behavior (logged in, method, subscription tier) and intentionally drop
/// email/orgId/orgName so personal identifiers never enter our models, logs, or diagnostics.
/// </summary>
public sealed record ClaudeAuthStatus(
    bool LoggedIn,
    string? AuthMethod,
    string? SubscriptionType);

/// <summary>How far a <see cref="ClaudeUsageProbe"/> attempt got, so the UI can distinguish states.</summary>
public enum ClaudeProbeStatus
{
    /// <summary>The probe returned parseable usage.</summary>
    Success,

    /// <summary>The CLI is installed but the user is not signed in - setup/login is required.</summary>
    NotAuthenticated,

    /// <summary>The installed CLI could not drive the `/usage` command (missing flag/command, old build).</summary>
    Unsupported,

    /// <summary>Startup/command/shutdown timed out, or the session was cancelled.</summary>
    Timeout,

    /// <summary>Any other provider-local error running the probe.</summary>
    Error,
}

/// <summary>
/// One quota window parsed from the `/usage` panel. <paramref name="Kind"/> is a stable key
/// ("five_hour", "seven_day", "weekly_model:&lt;name&gt;"); <paramref name="RawResetText"/> preserves
/// the exact on-screen reset wording even when it can't be resolved to an absolute instant.
/// </summary>
public sealed record ClaudeCliUsageWindow(
    string Kind,
    string DisplayName,
    decimal? UsedPercent,
    string? RawResetText,
    DateTimeOffset? ResetsAt,
    string? ModelName = null);

/// <summary>Everything the parser could extract from a single `/usage` capture.</summary>
public sealed record ClaudeCliUsageResult(
    IReadOnlyList<ClaudeCliUsageWindow> Windows,
    decimal? SessionCostUsd);

/// <summary>Full result of one probe attempt: status plus (on success) the parsed usage.</summary>
public sealed record ClaudeUsageProbeResult(
    ClaudeProbeStatus Status,
    ClaudeCliUsageResult? Usage,
    string? Message)
{
    public static ClaudeUsageProbeResult Ok(ClaudeCliUsageResult usage) => new(ClaudeProbeStatus.Success, usage, null);

    public static ClaudeUsageProbeResult Fail(ClaudeProbeStatus status, string? message) => new(status, null, message);
}
