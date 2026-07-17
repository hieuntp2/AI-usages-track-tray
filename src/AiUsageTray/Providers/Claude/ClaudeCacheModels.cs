using System.Globalization;
using System.Text.Json;
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

/// <summary>
/// Mirrors the subset of Claude Code's status-line stdin JSON this app cares about, matching the
/// official schema at code.claude.com/docs/en/statusline (verified against a live capture from
/// Claude Code 2.1.195). All fields optional - absence means unknown, never zero.
/// </summary>
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

    /// <summary>Only present for Claude.ai Pro/Max subscribers, and only after the first API response.</summary>
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

/// <summary>
/// Real shape (v2.1.132+): totals reflect the *current context window*, `used_percentage`/
/// `remaining_percentage` are precomputed by Claude Code (nullable early in a session), and
/// `current_usage` breaks the input side down (null before the first API call / after /compact).
/// </summary>
public sealed class ClaudeContextWindow
{
    [JsonPropertyName("total_input_tokens")]
    public long? TotalInputTokens { get; set; }

    [JsonPropertyName("total_output_tokens")]
    public long? TotalOutputTokens { get; set; }

    [JsonPropertyName("context_window_size")]
    public long? ContextWindowSize { get; set; }

    [JsonPropertyName("used_percentage")]
    public decimal? UsedPercentage { get; set; }

    [JsonPropertyName("remaining_percentage")]
    public decimal? RemainingPercentage { get; set; }

    [JsonPropertyName("current_usage")]
    public ClaudeContextCurrentUsage? CurrentUsage { get; set; }
}

public sealed class ClaudeContextCurrentUsage
{
    [JsonPropertyName("input_tokens")]
    public long? InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public long? OutputTokens { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public long? CacheCreationInputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    public long? CacheReadInputTokens { get; set; }
}

public sealed class ClaudeCostInfo
{
    [JsonPropertyName("total_cost_usd")]
    public decimal? TotalCostUsd { get; set; }
}

/// <summary>Absent entirely before the first API response and for non-Pro/Max account types.</summary>
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

    /// <summary>
    /// Per the official schema this arrives as Unix epoch *seconds* (a JSON number); older
    /// assumptions/other tools use ISO-8601 strings. The converter accepts both - without it, a
    /// numeric value would throw during deserialization and take the whole payload down with it.
    /// </summary>
    [JsonPropertyName("resets_at")]
    [JsonConverter(typeof(FlexibleTimestampConverter))]
    public DateTimeOffset? ResetsAt { get; set; }
}

/// <summary>Reads a timestamp that may be a Unix-seconds number, a Unix-milliseconds number, or an
/// ISO-8601 string. Unparsable values become null (unknown), never an exception.</summary>
public sealed class FlexibleTimestampConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number when reader.TryGetInt64(out var numeric):
                try
                {
                    // Values above ~10^12 are milliseconds, otherwise seconds.
                    return numeric > 100_000_000_000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                        : DateTimeOffset.FromUnixTimeSeconds(numeric);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }

            case JsonTokenType.String:
                var text = reader.GetString();
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : null;

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteNumberValue(v.ToUnixTimeSeconds());
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
