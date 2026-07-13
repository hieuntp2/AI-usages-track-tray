using System.Text.Json;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.Codex;

/// <summary>
/// Parses the result of `account/rateLimits/read` (and the sparse `account/rateLimits/updated`
/// notification) into a <see cref="CodexRateLimitState"/>, and turns that state into normalized
/// <see cref="UsageWindow"/>s for the UI. Pure/stateless so it can be unit tested against fixtures
/// without spawning a real Codex process.
/// </summary>
public static class CodexRateLimitParser
{
    public const string CodexLimitId = "codex";

    public static CodexRateLimitState Parse(JsonElement result)
    {
        // `rateLimitResetCredits` (the count of free "full reset" credits available) lives at the
        // top level of the result, as a sibling of rateLimits[ByLimitId] - not nested per-entry.
        var resetCreditCount = result.TryGetProperty("rateLimitResetCredits").TryGetProperty("availableCount").GetIntOrNull();

        var byLimitId = result.TryGetProperty("rateLimitsByLimitId");
        if (byLimitId is { ValueKind: JsonValueKind.Object } byLimitIdObj &&
            byLimitIdObj.TryGetProperty(CodexLimitId, out var codexEntry))
        {
            return ParseEntry(codexEntry, resetCreditCount);
        }

        var legacy = result.TryGetProperty("rateLimits");
        if (legacy is { } legacyElement)
        {
            return ParseEntry(legacyElement, resetCreditCount);
        }

        // The whole result might itself be the entry (some server versions omit the wrapper).
        return ParseEntry(result, resetCreditCount);
    }

    private static CodexRateLimitState ParseEntry(JsonElement entry, int? resetCreditCount)
    {
        var primary = ParseWindow(entry.TryGetProperty("primary"));
        var secondary = ParseWindow(entry.TryGetProperty("secondary"));

        return CodexRateLimitState.CreateFull(
            limitId: entry.TryGetProperty("limitId").GetStringOrNull(),
            limitName: entry.TryGetProperty("limitName").GetStringOrNull(),
            planType: entry.TryGetProperty("planType").GetStringOrNull(),
            primary: primary,
            secondary: secondary,
            rateLimitReachedType: entry.TryGetProperty("rateLimitReachedType").GetStringOrNull(),
            availableCredit: ParseAvailableCredit(entry.TryGetProperty("credits")),
            resetCreditCount: resetCreditCount);
    }

    /// <summary>
    /// The real `account/rateLimits/read` response nests credit info as
    /// `credits: { hasCredits, unlimited, balance }` (a numeric string), not a flat
    /// `availableCredit` field. Only surfaced when `hasCredits` is true - otherwise the plan simply
    /// doesn't use credit-based limiting and a "0" balance would be misleading, not meaningful.
    /// </summary>
    private static decimal? ParseAvailableCredit(JsonElement? credits)
    {
        if (credits is not { } c)
        {
            return null;
        }

        var hasCredits = c.TryGetProperty("hasCredits") is { ValueKind: JsonValueKind.True };
        return hasCredits ? c.TryGetProperty("balance").GetDecimalOrNull() : null;
    }

    private static CodexWindowData? ParseWindow(JsonElement? windowElement)
    {
        if (windowElement is not { } w)
        {
            return null;
        }

        var usedPercent = w.TryGetProperty("usedPercent").GetDecimalOrNull();
        var durationMins = w.TryGetProperty("windowDurationMins").GetIntOrNull();
        var resetsAt = w.TryGetProperty("resetsAt").GetTimestampOrNull();

        if (usedPercent is null && durationMins is null && resetsAt is null)
        {
            return null;
        }

        return new CodexWindowData(usedPercent, durationMins, resetsAt);
    }

    public static IReadOnlyList<UsageWindow> ToUsageWindows(CodexRateLimitState state)
    {
        var windows = new List<UsageWindow>();

        if (state.Primary is { } primary)
        {
            windows.Add(ToUsageWindow("primary", primary));
        }

        if (state.Secondary is { } secondary)
        {
            windows.Add(ToUsageWindow("secondary", secondary));
        }

        return windows;
    }

    private static UsageWindow ToUsageWindow(string id, CodexWindowData data)
    {
        var usedPercent = UsageWindow.ClampPercent(data.UsedPercent);
        decimal? remainingPercent = usedPercent is null ? null : Math.Clamp(100m - usedPercent.Value, 0m, 100m);

        return new UsageWindow(
            Id: id,
            DisplayName: LabelForDuration(data.WindowDurationMins),
            UsedPercent: usedPercent,
            RemainingPercent: remainingPercent,
            ResetsAt: data.ResetsAt,
            Duration: data.WindowDurationMins is { } mins ? TimeSpan.FromMinutes(mins) : null,
            UsedValue: null,
            LimitValue: null,
            Unit: "percent");
    }

    /// <summary>Surfaces credit/reset-credit info as compact metrics, when the account has any.</summary>
    public static IReadOnlyList<UsageMetric> ToCreditMetrics(CodexRateLimitState state)
    {
        var metrics = new List<UsageMetric>();

        if (state.AvailableCredit is { } credit)
        {
            metrics.Add(new UsageMetric("available_credit", "Available credit", credit, "credits"));
        }

        if (state.ResetCreditCount is { } resetCredits)
        {
            metrics.Add(new UsageMetric("reset_credits", "Free limit resets available", resetCredits, "count"));
        }

        return metrics;
    }

    /// <summary>
    /// Derives a human label purely from the reported window duration - never hardcodes "5 hours"
    /// or "weekly" as fixed constants, since Codex may change these window sizes server-side.
    /// </summary>
    public static string LabelForDuration(int? windowDurationMins)
    {
        if (windowDurationMins is not { } mins || mins <= 0)
        {
            return "Usage limit";
        }

        return mins switch
        {
            300 => "5-hour limit",
            10080 => "Weekly limit",
            _ => FormatDuration(mins),
        };
    }

    private static string FormatDuration(int totalMinutes)
    {
        if (totalMinutes % 1440 == 0)
        {
            var days = totalMinutes / 1440;
            return $"{days}-day limit";
        }

        if (totalMinutes % 60 == 0)
        {
            var hours = totalMinutes / 60;
            return $"{hours}-hour limit";
        }

        return $"{totalMinutes}-minute limit";
    }
}
