using System.Text.Json;
using AiUsageTray.Providers.Codex;
using Xunit;

namespace AiUsageTray.Tests.Codex;

public class CodexRateLimitParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parse_PrefersRateLimitsByLimitId()
    {
        var result = Parse("""
        {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "limitName": "Codex",
                    "planType": "plus",
                    "primary": { "usedPercent": 37, "windowDurationMins": 300, "resetsAt": 1700000000 }
                }
            },
            "rateLimits": {
                "primary": { "usedPercent": 99, "windowDurationMins": 300 }
            }
        }
        """);

        var state = CodexRateLimitParser.Parse(result);

        Assert.Equal("plus", state.PlanType);
        Assert.Equal(37m, state.Primary!.UsedPercent);
    }

    [Fact]
    public void Parse_FallsBackToLegacyRateLimits()
    {
        var result = Parse("""
        {
            "rateLimits": {
                "limitId": "codex",
                "primary": { "usedPercent": 50, "windowDurationMins": 300 }
            }
        }
        """);

        var state = CodexRateLimitParser.Parse(result);

        Assert.Equal(50m, state.Primary!.UsedPercent);
    }

    [Fact]
    public void ToUsageWindows_PrimaryOnly_ReturnsOneWindow()
    {
        var result = Parse("""
        { "rateLimits": { "primary": { "usedPercent": 40, "windowDurationMins": 300 } } }
        """);

        var state = CodexRateLimitParser.Parse(result);
        var windows = CodexRateLimitParser.ToUsageWindows(state);

        var window = Assert.Single(windows);
        Assert.Equal("5-hour limit", window.DisplayName);
        Assert.Equal(40m, window.UsedPercent);
        Assert.Equal(60m, window.RemainingPercent);
    }

    [Fact]
    public void ToUsageWindows_PrimaryAndSecondary_ReturnsBoth()
    {
        var result = Parse("""
        {
            "rateLimits": {
                "primary": { "usedPercent": 37, "windowDurationMins": 300 },
                "secondary": { "usedPercent": 72, "windowDurationMins": 10080 }
            }
        }
        """);

        var state = CodexRateLimitParser.Parse(result);
        var windows = CodexRateLimitParser.ToUsageWindows(state);

        Assert.Equal(2, windows.Count);
        Assert.Equal("5-hour limit", windows[0].DisplayName);
        Assert.Equal("Weekly limit", windows[1].DisplayName);
    }

    [Fact]
    public void ToUsageWindows_MissingFields_ReturnsEmpty()
    {
        var result = Parse("{ \"rateLimits\": {} }");

        var state = CodexRateLimitParser.Parse(result);
        var windows = CodexRateLimitParser.ToUsageWindows(state);

        Assert.Empty(windows);
        Assert.Null(state.Primary);
    }

    [Theory]
    [InlineData(300, "5-hour limit")]
    [InlineData(10080, "Weekly limit")]
    [InlineData(120, "2-hour limit")]
    [InlineData(1440, "1-day limit")]
    [InlineData(90, "90-minute limit")]
    [InlineData(null, "Usage limit")]
    public void LabelForDuration_DerivesFromMinutes(int? minutes, string expected)
    {
        Assert.Equal(expected, CodexRateLimitParser.LabelForDuration(minutes));
    }

    [Fact]
    public void Merge_SparseUpdate_PreservesUntouchedFields()
    {
        var full = Parse("""
        {
            "rateLimits": {
                "limitId": "codex",
                "planType": "plus",
                "primary": { "usedPercent": 10, "windowDurationMins": 300, "resetsAt": 1700000000 },
                "secondary": { "usedPercent": 20, "windowDurationMins": 10080 }
            }
        }
        """);

        var sparseUpdate = Parse("""
        { "rateLimits": { "primary": { "usedPercent": 15 } } }
        """);

        var state = CodexRateLimitParser.Parse(full);
        var update = CodexRateLimitParser.Parse(sparseUpdate);
        state.Merge(update);

        Assert.Equal(15m, state.Primary!.UsedPercent);
        Assert.Equal(300, state.Primary.WindowDurationMins); // preserved, not nulled out
        Assert.NotNull(state.Primary.ResetsAt); // preserved
        Assert.Equal(20m, state.Secondary!.UsedPercent); // untouched window entirely preserved
        Assert.Equal("plus", state.PlanType); // untouched top-level field preserved
    }

    [Fact]
    public void UnixTimestamp_Seconds_ConvertsCorrectly()
    {
        var result = Parse("""{ "rateLimits": { "primary": { "resetsAt": 1700000000, "windowDurationMins": 300 } } } """);
        var state = CodexRateLimitParser.Parse(result);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), state.Primary!.ResetsAt);
    }

    [Fact]
    public void UnixTimestamp_Milliseconds_ConvertsCorrectly()
    {
        var millis = 1700000000000L;
        var result = Parse($$"""{ "rateLimits": { "primary": { "resetsAt": {{millis}}, "windowDurationMins": 300 } } } """);
        var state = CodexRateLimitParser.Parse(result);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(millis), state.Primary!.ResetsAt);
    }

    [Fact]
    public void MissingRateLimitReachedType_IsNullNotEmpty()
    {
        var result = Parse("""{ "rateLimits": { "primary": { "usedPercent": 5, "windowDurationMins": 300 } } } """);
        var state = CodexRateLimitParser.Parse(result);

        Assert.Null(state.RateLimitReachedType);
    }

    /// <summary>
    /// Captured verbatim from a live `codex app-server` session (codex-cli 0.144.1) responding to
    /// account/rateLimits/read. Two things this fixture proves that hand-written JSON couldn't:
    /// the real "credits"/"rateLimitResetCredits" field shapes, and that "primary" is not always the
    /// 5-hour window - here this account's only populated window is a 10080-minute ("weekly") one.
    /// </summary>
    private const string RealAccountRateLimitsReadResult = """
    {
        "rateLimits": {
            "limitId": "codex",
            "limitName": null,
            "primary": { "usedPercent": 11, "windowDurationMins": 10080, "resetsAt": 1784500282 },
            "secondary": null,
            "credits": { "hasCredits": false, "unlimited": false, "balance": "0" },
            "individualLimit": null,
            "planType": "plus",
            "rateLimitReachedType": null
        },
        "rateLimitsByLimitId": {
            "codex": {
                "limitId": "codex",
                "limitName": null,
                "primary": { "usedPercent": 11, "windowDurationMins": 10080, "resetsAt": 1784500282 },
                "secondary": null,
                "credits": { "hasCredits": false, "unlimited": false, "balance": "0" },
                "individualLimit": null,
                "planType": "plus",
                "rateLimitReachedType": null
            }
        },
        "rateLimitResetCredits": {
            "availableCount": 3,
            "credits": []
        }
    }
    """;

    [Fact]
    public void Parse_RealCodexCliFixture_OnlyWeeklyWindowPopulated()
    {
        var state = CodexRateLimitParser.Parse(Parse(RealAccountRateLimitsReadResult));
        var windows = CodexRateLimitParser.ToUsageWindows(state);

        var window = Assert.Single(windows);
        Assert.Equal("Weekly limit", window.DisplayName);
        Assert.Equal(11m, window.UsedPercent);
        Assert.Equal("plus", state.PlanType);
    }

    [Fact]
    public void Parse_RealCodexCliFixture_ResetCreditCountReadFromTopLevel()
    {
        var state = CodexRateLimitParser.Parse(Parse(RealAccountRateLimitsReadResult));

        Assert.Equal(3, state.ResetCreditCount);
    }

    [Fact]
    public void Parse_RealCodexCliFixture_NoCreditsMeansAvailableCreditIsNull()
    {
        var state = CodexRateLimitParser.Parse(Parse(RealAccountRateLimitsReadResult));

        Assert.Null(state.AvailableCredit); // hasCredits: false -> not a meaningful balance
    }

    [Fact]
    public void ToCreditMetrics_HasCreditsTrue_SurfacesBalance()
    {
        var result = Parse("""
        { "rateLimits": { "credits": { "hasCredits": true, "unlimited": false, "balance": "42.5" } } }
        """);
        var state = CodexRateLimitParser.Parse(result);

        var metrics = CodexRateLimitParser.ToCreditMetrics(state);

        Assert.Contains(metrics, m => m.Id == "available_credit" && m.Value == 42.5m);
    }

    [Fact]
    public void ToCreditMetrics_ResetCreditCountPresent_Surfaced()
    {
        var result = Parse("""{ "rateLimits": {}, "rateLimitResetCredits": { "availableCount": 2 } } """);
        var state = CodexRateLimitParser.Parse(result);

        var metrics = CodexRateLimitParser.ToCreditMetrics(state);

        Assert.Contains(metrics, m => m.Id == "reset_credits" && m.Value == 2m);
    }
}
