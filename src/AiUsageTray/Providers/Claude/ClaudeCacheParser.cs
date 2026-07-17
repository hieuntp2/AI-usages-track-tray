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

        return new UsageWindow(
            Id: id,
            DisplayName: displayName,
            UsedPercent: usedPercent,
            RemainingPercent: remainingPercent,
            ResetsAt: window.ResetsAt,
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

        if (ComputeContextUsedPercent(payload.ContextWindow) is { } contextPercent)
        {
            metrics.Add(new UsageMetric("context_window", "Context used", contextPercent, "percent"));
        }

        if (payload.Cost?.TotalCostUsd is { } cost)
        {
            metrics.Add(new UsageMetric("session_cost", "Session cost", cost, "usd"));
        }

        return metrics;
    }

    /// <summary>
    /// Context usage percentage: prefers Claude Code's own precomputed `used_percentage` (nullable
    /// early in a session), falling back to computing input-side tokens / window size the same way
    /// the docs define it (input + cache-creation + cache-read, output excluded).
    /// </summary>
    public static decimal? ComputeContextUsedPercent(ClaudeContextWindow? contextWindow)
    {
        if (contextWindow is null)
        {
            return null;
        }

        if (contextWindow.UsedPercentage is { } precomputed)
        {
            return Math.Clamp(precomputed, 0m, 100m);
        }

        if (contextWindow is { CurrentUsage: { } usage, ContextWindowSize: > 0 and { } size })
        {
            var inputSideTokens = (usage.InputTokens ?? 0) + (usage.CacheCreationInputTokens ?? 0) + (usage.CacheReadInputTokens ?? 0);
            return Math.Clamp(inputSideTokens * 100m / size, 0m, 100m);
        }

        return null;
    }

    /// <summary>
    /// True if any window's reset time has already passed. Callers must not infer 0% usage from
    /// this - the real post-reset value is unknown until a fresh Claude response arrives.
    /// </summary>
    public static bool HasResetTimePassed(IReadOnlyList<UsageWindow> windows, DateTimeOffset now) =>
        windows.Any(w => w.ResetsAt is { } resetsAt && resetsAt < now);
}
