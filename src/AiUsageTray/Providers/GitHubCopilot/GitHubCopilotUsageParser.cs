using System.Globalization;
using System.Text.Json;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.GitHubCopilot;

/// <summary>
/// Aggregates a raw GitHub billing usage response into normalized metrics. Pure/stateless so it
/// is unit-testable against fixtures without any real GitHub API access.
/// </summary>
public static class GitHubCopilotUsageParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static GitHubBillingUsageResponse? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GitHubBillingUsageResponse>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sums Copilot-product quantities for the current UTC month.</summary>
    public static UsageMetric? AggregateCurrentMonth(GitHubBillingUsageResponse? response, string metricId, string displayName, string unit, DateTimeOffset now)
    {
        if (response?.UsageItems is not { Count: > 0 } items)
        {
            return null;
        }

        decimal total = 0;
        var found = false;

        foreach (var item in items)
        {
            if (item.Product is null || !item.Product.Contains("copilot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // GitHub billing dates are calendar dates with no time component. Parsing with
            // AssumeUniversal (rather than the local-time default) avoids shifting the date
            // across a month boundary purely due to the host machine's timezone offset.
            if (item.Date is null || !DateTimeOffset.TryParse(item.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            {
                continue;
            }

            if (date.Year != now.Year || date.Month != now.Month)
            {
                continue;
            }

            if (item.Quantity is { } quantity)
            {
                total += quantity;
                found = true;
            }
        }

        return found ? new UsageMetric(metricId, displayName, total, unit) : null;
    }
}
