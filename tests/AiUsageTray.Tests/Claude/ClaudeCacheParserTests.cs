using AiUsageTray.Models;
using AiUsageTray.Providers.Claude;
using Xunit;

namespace AiUsageTray.Tests.Claude;

public class ClaudeCacheParserTests
{
    /// <summary>
    /// Matches the official statusline schema (code.claude.com/docs/en/statusline): `resets_at` is
    /// Unix epoch seconds (a JSON number), context_window uses total_*_tokens/context_window_size/
    /// used_percentage/current_usage - not the used_tokens/total_tokens shape originally guessed.
    /// </summary>
    private const string CompletePayload = """
    {
        "capturedAt": "2026-07-13T10:00:00Z",
        "payload": {
            "session_id": "abcdef1234567890",
            "version": "2.1.195",
            "model": { "id": "claude-opus", "display_name": "Claude Opus" },
            "context_window": {
                "total_input_tokens": 15500,
                "total_output_tokens": 1200,
                "context_window_size": 200000,
                "used_percentage": 50,
                "remaining_percentage": 50,
                "current_usage": {
                    "input_tokens": 8500,
                    "output_tokens": 1200,
                    "cache_creation_input_tokens": 5000,
                    "cache_read_input_tokens": 2000
                }
            },
            "cost": { "total_cost_usd": 1.25 },
            "rate_limits": {
                "five_hour": { "used_percentage": 21, "resets_at": 1784500282 },
                "seven_day": { "used_percentage": 48, "resets_at": 1784900000 }
            }
        }
    }
    """;

    /// <summary>
    /// Captured verbatim from a live Claude Code 2.1.195 session through the bridge: session start,
    /// so rate_limits absent and every context percentage null. Missing must mean unknown, not 0.
    /// </summary>
    private const string RealSessionStartPayload = """
    {
        "capturedAt": "2026-07-13T05:49:44.8728669Z",
        "payload": {
            "session_id": "050af46a-fd0e-47b6-85c9-1c69c12f7c97",
            "cwd": "C:\\Users\\someone",
            "effort": { "level": "high" },
            "model": { "id": "claude-fable-5", "display_name": "Fable 5" },
            "version": "2.1.195",
            "output_style": { "name": "default" },
            "cost": { "total_cost_usd": 0, "total_duration_ms": 1800, "total_api_duration_ms": 0, "total_lines_added": 0, "total_lines_removed": 0 },
            "context_window": {
                "total_input_tokens": 0,
                "total_output_tokens": 0,
                "context_window_size": 1000000,
                "current_usage": null,
                "used_percentage": null,
                "remaining_percentage": null
            },
            "exceeds_200k_tokens": false,
            "fast_mode": false,
            "thinking": { "enabled": true }
        }
    }
    """;

    [Fact]
    public void TryParseEnvelope_CompleteJson_Succeeds()
    {
        var ok = ClaudeCacheParser.TryParseEnvelope(CompletePayload, out var envelope, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("abcdef1234567890", envelope!.Payload!.SessionId);
        Assert.Equal(21m, envelope.Payload.RateLimits!.FiveHour!.UsedPercentage);
    }

    [Fact]
    public void TryParseEnvelope_InvalidJson_Fails()
    {
        var ok = ClaudeCacheParser.TryParseEnvelope("{ not json", out var envelope, out var error);

        Assert.False(ok);
        Assert.Null(envelope);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseEnvelope_EmptyString_Fails()
    {
        var ok = ClaudeCacheParser.TryParseEnvelope("", out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void BuildWindows_MissingRateLimits_ReturnsEmpty()
    {
        ClaudeCacheParser.TryParseEnvelope("""{"payload":{"session_id":"x"}}""", out var envelope, out _);

        var windows = ClaudeCacheParser.BuildWindows(envelope!.Payload);

        Assert.Empty(windows);
    }

    [Fact]
    public void BuildWindows_OneWindowMissing_ReturnsOnlyPresentOne()
    {
        ClaudeCacheParser.TryParseEnvelope("""
        { "payload": { "rate_limits": { "five_hour": { "used_percentage": 21 } } } }
        """, out var envelope, out _);

        var windows = ClaudeCacheParser.BuildWindows(envelope!.Payload);

        var window = Assert.Single(windows);
        Assert.Equal("five_hour", window.Id);
        Assert.Equal(21m, window.UsedPercent);
        Assert.Equal(79m, window.RemainingPercent);
    }

    [Fact]
    public void BuildWindows_InvalidTimestamp_ResetsAtIsNull()
    {
        ClaudeCacheParser.TryParseEnvelope("""
        { "payload": { "rate_limits": { "five_hour": { "used_percentage": 10, "resets_at": "not-a-date" } } } }
        """, out var envelope, out _);

        var windows = ClaudeCacheParser.BuildWindows(envelope!.Payload);

        Assert.Null(windows[0].ResetsAt);
    }

    [Fact]
    public void BuildMetrics_ContextAndCost_Computed()
    {
        ClaudeCacheParser.TryParseEnvelope(CompletePayload, out var envelope, out _);

        var metrics = ClaudeCacheParser.BuildMetrics(envelope!.Payload);

        Assert.Contains(metrics, m => m.Id == "context_window" && m.Value == 50m);
        Assert.Contains(metrics, m => m.Id == "session_cost" && m.Value == 1.25m);
    }

    [Fact]
    public void BuildWindows_EpochSecondsResetsAt_ConvertsCorrectly()
    {
        ClaudeCacheParser.TryParseEnvelope(CompletePayload, out var envelope, out _);

        var windows = ClaudeCacheParser.BuildWindows(envelope!.Payload);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784500282), windows[0].ResetsAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784900000), windows[1].ResetsAt);
    }

    [Fact]
    public void RealSessionStartPayload_ParsesWithUnknownsNotZeros()
    {
        var ok = ClaudeCacheParser.TryParseEnvelope(RealSessionStartPayload, out var envelope, out var error);

        Assert.True(ok, error);
        var payload = envelope!.Payload!;
        Assert.Null(payload.RateLimits);
        Assert.Empty(ClaudeCacheParser.BuildWindows(payload));

        // Context percentages are null at session start - no context metric may be fabricated.
        var metrics = ClaudeCacheParser.BuildMetrics(payload);
        Assert.DoesNotContain(metrics, m => m.Id == "context_window");
        Assert.Equal("Fable 5", payload.Model!.DisplayName);
    }

    [Fact]
    public void ComputeContextUsedPercent_FallsBackToCurrentUsageTokens()
    {
        // used_percentage null, but current_usage present: docs define used% as
        // (input + cache_creation + cache_read) / window size, output excluded.
        var contextWindow = new ClaudeContextWindow
        {
            ContextWindowSize = 200000,
            UsedPercentage = null,
            CurrentUsage = new ClaudeContextCurrentUsage
            {
                InputTokens = 8500,
                OutputTokens = 999999, // must be ignored
                CacheCreationInputTokens = 5000,
                CacheReadInputTokens = 2000,
            },
        };

        var percent = ClaudeCacheParser.ComputeContextUsedPercent(contextWindow);

        Assert.Equal(7.75m, percent); // 15500 / 200000 * 100
    }

    [Fact]
    public void HasResetTimePassed_PastReset_ReturnsTrue()
    {
        var windows = new[]
        {
            new UsageWindow("five_hour", "5-hour limit", 50, 50, DateTimeOffset.UtcNow.AddMinutes(-5), null, null, null, "percent"),
        };

        Assert.True(ClaudeCacheParser.HasResetTimePassed(windows, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void HasResetTimePassed_FutureReset_ReturnsFalse()
    {
        var windows = new[]
        {
            new UsageWindow("five_hour", "5-hour limit", 50, 50, DateTimeOffset.UtcNow.AddHours(1), null, null, null, "percent"),
        };

        Assert.False(ClaudeCacheParser.HasResetTimePassed(windows, DateTimeOffset.UtcNow));
    }
}
