using AiUsageTray.Models;
using AiUsageTray.Providers.Claude;
using Xunit;

namespace AiUsageTray.Tests.Claude;

public class ClaudeCacheParserTests
{
    private const string CompletePayload = """
    {
        "capturedAt": "2026-07-13T10:00:00Z",
        "payload": {
            "session_id": "abcdef1234567890",
            "version": "1.2.3",
            "model": { "id": "claude-opus", "display_name": "Claude Opus" },
            "context_window": { "used_tokens": 5000, "total_tokens": 10000 },
            "cost": { "total_cost_usd": 1.25 },
            "rate_limits": {
                "five_hour": { "used_percentage": 21, "resets_at": "2026-07-13T15:00:00Z" },
                "seven_day": { "used_percentage": 48, "resets_at": "2026-07-18T00:00:00Z" }
            }
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
