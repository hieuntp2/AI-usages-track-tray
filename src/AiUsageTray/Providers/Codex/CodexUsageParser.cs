using System.Text.Json;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.Codex;

/// <summary>Parses `account/read` and `account/usage/read` results into normalized pieces.</summary>
public static class CodexUsageParser
{
    public static (string? AccountLabel, string? PlanName) ParseAccount(JsonElement result)
    {
        var account = result.TryGetProperty("account") ?? result;

        var label = account.TryGetProperty("email").GetStringOrNull()
            ?? account.TryGetProperty("accountLabel").GetStringOrNull()
            ?? account.TryGetProperty("label").GetStringOrNull();

        var plan = account.TryGetProperty("planType").GetStringOrNull()
            ?? account.TryGetProperty("plan").GetStringOrNull()
            ?? account.TryGetProperty("planName").GetStringOrNull();

        return (label, plan);
    }

    /// <summary>
    /// Extracts a compact set of scalar usage metrics for the flyout. Detailed daily-bucket
    /// activity is intentionally not surfaced here - it belongs in a future provider-details view.
    /// </summary>
    public static IReadOnlyList<UsageMetric> ParseUsageMetrics(JsonElement result)
    {
        var metrics = new List<UsageMetric>();

        AddIfPresent(metrics, result, "lifetimeTokens", "Lifetime tokens", "tokens");
        AddIfPresent(metrics, result, "peakDailyTokens", "Peak daily tokens", "tokens");
        AddIfPresent(metrics, result, "currentStreak", "Current streak", "days");
        AddIfPresent(metrics, result, "longestStreak", "Longest streak", "days");

        return metrics;
    }

    private static void AddIfPresent(List<UsageMetric> metrics, JsonElement result, string field, string label, string unit)
    {
        var value = result.TryGetProperty(field).GetDecimalOrNull();
        if (value is not null)
        {
            metrics.Add(new UsageMetric(field, label, value.Value, unit));
        }
    }
}
