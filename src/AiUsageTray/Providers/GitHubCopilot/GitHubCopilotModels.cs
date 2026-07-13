using System.Text.Json.Serialization;

namespace AiUsageTray.Providers.GitHubCopilot;

/// <summary>
/// One usage line item as returned by GitHub's billing usage endpoints. Field names follow
/// GitHub's documented (but evolving) billing usage report shape - unknown/renamed fields are
/// simply ignored rather than causing a parse failure, since GitHub's billing model can change.
/// </summary>
public sealed class GitHubBillingUsageItem
{
    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("product")]
    public string? Product { get; set; }

    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; set; }

    [JsonPropertyName("unitType")]
    public string? UnitType { get; set; }

    [JsonPropertyName("grossAmount")]
    public decimal? GrossAmount { get; set; }
}

public sealed class GitHubBillingUsageResponse
{
    [JsonPropertyName("usageItems")]
    public List<GitHubBillingUsageItem>? UsageItems { get; set; }
}
