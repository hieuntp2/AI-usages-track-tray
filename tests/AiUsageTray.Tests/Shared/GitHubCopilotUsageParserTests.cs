using AiUsageTray.Providers.GitHubCopilot;
using Xunit;

namespace AiUsageTray.Tests.Shared;

public class GitHubCopilotUsageParserTests
{
    [Fact]
    public void AggregateCurrentMonth_SumsCopilotItemsInMonth()
    {
        var now = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var json = """
        {
            "usageItems": [
                { "date": "2026-07-01", "product": "copilot", "quantity": 3 },
                { "date": "2026-07-10", "product": "copilot", "quantity": 2 },
                { "date": "2026-06-30", "product": "copilot", "quantity": 100 },
                { "date": "2026-07-05", "product": "actions", "quantity": 50 }
            ]
        }
        """;

        var parsed = GitHubCopilotUsageParser.Parse(json);
        var metric = GitHubCopilotUsageParser.AggregateCurrentMonth(parsed, "requests", "Requests", "requests", now);

        Assert.NotNull(metric);
        Assert.Equal(5m, metric!.Value); // only the two July + copilot-product rows count
    }

    [Fact]
    public void AggregateCurrentMonth_NoMatchingItems_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var parsed = GitHubCopilotUsageParser.Parse("""{ "usageItems": [] }""");

        var metric = GitHubCopilotUsageParser.AggregateCurrentMonth(parsed, "requests", "Requests", "requests", now);

        Assert.Null(metric);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsNullNotThrow()
    {
        var parsed = GitHubCopilotUsageParser.Parse("{ not json");

        Assert.Null(parsed);
    }
}
