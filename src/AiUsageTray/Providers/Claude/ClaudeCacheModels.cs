using System.Text.Json.Serialization;

namespace AiUsageTray.Providers.Claude;

/// <summary>Wrapper the bridge script writes to disk: the raw Claude status-line JSON plus a local capture time.</summary>
public sealed class ClaudeCacheEnvelope
{
    [JsonPropertyName("capturedAt")]
    public DateTimeOffset? CapturedAt { get; set; }

    [JsonPropertyName("payload")]
    public ClaudeStatusLinePayload? Payload { get; set; }
}

/// <summary>Mirrors the subset of Claude Code's status-line stdin JSON this app cares about. All fields optional.</summary>
public sealed class ClaudeStatusLinePayload
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("model")]
    public ClaudeModelInfo? Model { get; set; }

    [JsonPropertyName("context_window")]
    public ClaudeContextWindow? ContextWindow { get; set; }

    [JsonPropertyName("cost")]
    public ClaudeCostInfo? Cost { get; set; }

    [JsonPropertyName("rate_limits")]
    public ClaudeRateLimits? RateLimits { get; set; }
}

public sealed class ClaudeModelInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

public sealed class ClaudeContextWindow
{
    [JsonPropertyName("used_tokens")]
    public long? UsedTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long? TotalTokens { get; set; }
}

public sealed class ClaudeCostInfo
{
    [JsonPropertyName("total_cost_usd")]
    public decimal? TotalCostUsd { get; set; }
}

/// <summary>Absent entirely before the first API response, for unsupported account types, or on older CLI versions.</summary>
public sealed class ClaudeRateLimits
{
    [JsonPropertyName("five_hour")]
    public ClaudeRateLimitWindow? FiveHour { get; set; }

    [JsonPropertyName("seven_day")]
    public ClaudeRateLimitWindow? SevenDay { get; set; }
}

public sealed class ClaudeRateLimitWindow
{
    [JsonPropertyName("used_percentage")]
    public decimal? UsedPercentage { get; set; }

    [JsonPropertyName("resets_at")]
    public string? ResetsAt { get; set; }
}
