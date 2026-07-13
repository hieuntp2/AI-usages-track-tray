using System.Text.Json;
using AiUsageTray.Providers.Codex;
using Xunit;

namespace AiUsageTray.Tests.Codex;

public class CodexUsageParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParseAccount_ReadsLabelAndPlan()
    {
        var result = Parse("""{ "account": { "email": "user@example.com", "planType": "plus" } } """);

        var (label, plan) = CodexUsageParser.ParseAccount(result);

        Assert.Equal("user@example.com", label);
        Assert.Equal("plus", plan);
    }

    [Fact]
    public void ParseAccount_MissingFields_ReturnsNulls()
    {
        var result = Parse("{ \"account\": {} }");

        var (label, plan) = CodexUsageParser.ParseAccount(result);

        Assert.Null(label);
        Assert.Null(plan);
    }

    [Fact]
    public void ParseUsageMetrics_AllFieldsPresent()
    {
        var result = Parse("""
        {
            "lifetimeTokens": 123456,
            "peakDailyTokens": 5000,
            "currentStreak": 7,
            "longestStreak": 21
        }
        """);

        var metrics = CodexUsageParser.ParseUsageMetrics(result);

        Assert.Equal(4, metrics.Count);
        Assert.Contains(metrics, m => m.Id == "lifetimeTokens" && m.Value == 123456m);
        Assert.Contains(metrics, m => m.Id == "longestStreak" && m.Value == 21m);
    }

    [Fact]
    public void ParseUsageMetrics_MissingFields_ReturnsEmpty()
    {
        var result = Parse("{}");

        var metrics = CodexUsageParser.ParseUsageMetrics(result);

        Assert.Empty(metrics);
    }
}
