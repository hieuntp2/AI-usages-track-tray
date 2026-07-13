using System.Globalization;
using System.Text.Json;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.Claude;

/// <summary>
/// Parses the JSON envelope written by the status-line bridge into normalized models. Pure/stateless
/// so it can be unit tested against fixtures without any file system or process involvement.
/// </summary>
public static class ClaudeCacheParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryParseEnvelope(string json, out ClaudeCacheEnvelope? envelope, out string? error)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            envelope = null;
            error = "Cache file is empty.";
            return false;
        }

        try
        {
            envelope = JsonSerializer.Deserialize<ClaudeCacheEnvelope>(json, JsonOptions);
            if (envelope is null)
            {
                error = "Cache file deserialized to null.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            envelope = null;
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }

    public static IReadOnlyList<UsageWindow> BuildWindows(ClaudeStatusLinePayload? payload)
    {
        if (payload?.RateLimits is not { } rateLimits)
        {
            return Array.Empty<UsageWindow>();
        }

        var windows = new List<UsageWindow>();

        if (rateLimits.FiveHour is { } fiveHour)
        {
            windows.Add(BuildWindow("five_hour", "5-hour limit", fiveHour));
        }

        if (rateLimits.SevenDay is { } sevenDay)
        {
            windows.Add(BuildWindow("seven_day", "Weekly limit", sevenDay));
        }

        return windows;
    }

    private static UsageWindow BuildWindow(string id, string displayName, ClaudeRateLimitWindow window)
    {
        var usedPercent = UsageWindow.ClampPercent(window.UsedPercentage);
        decimal? remainingPercent = usedPercent is null ? null : Math.Clamp(100m - usedPercent.Value, 0m, 100m);
        var resetsAt = ParseIsoTimestamp(window.ResetsAt);

        return new UsageWindow(
            Id: id,
            DisplayName: displayName,
            UsedPercent: usedPercent,
            RemainingPercent: remainingPercent,
            ResetsAt: resetsAt,
            Duration: null,
            UsedValue: null,
            LimitValue: null,
            Unit: "percent");
    }

    public static IReadOnlyList<UsageMetric> BuildMetrics(ClaudeStatusLinePayload? payload)
    {
        if (payload is null)
        {
            return Array.Empty<UsageMetric>();
        }

        var metrics = new List<UsageMetric>();

        if (payload.ContextWindow is { UsedTokens: { } used, TotalTokens: { } total } && total > 0)
        {
            var percent = Math.Clamp(used * 100m / total, 0m, 100m);
            metrics.Add(new UsageMetric("context_window", "Context used", percent, "percent"));
        }

        if (payload.Cost?.TotalCostUsd is { } cost)
        {
            metrics.Add(new UsageMetric("session_cost", "Session cost", cost, "usd"));
        }

        return metrics;
    }

    /// <summary>
    /// True if any window's reset time has already passed. Callers must not infer 0% usage from
    /// this - the real post-reset value is unknown until a fresh Claude response arrives.
    /// </summary>
    public static bool HasResetTimePassed(IReadOnlyList<UsageWindow> windows, DateTimeOffset now) =>
        windows.Any(w => w.ResetsAt is { } resetsAt && resetsAt < now);

    /// <summary>Never throws; unparsable/missing timestamps become null (unknown), not "now".</summary>
    public static DateTimeOffset? ParseIsoTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
