using AiUsageTray.Providers.Claude;
using Xunit;

namespace AiUsageTray.Tests.Claude;

public class ClaudeAuthStatusParserTests
{
    /// <summary>
    /// Sanitized capture of real `claude auth status --json` output (Claude Code 2.1.195). The
    /// account email, org id, and org name are present in the live output but the parser must not
    /// surface them, so the placeholders here are irrelevant to what we assert.
    /// </summary>
    private const string RealLoggedIn = """
    {
      "loggedIn": true,
      "authMethod": "claude.ai",
      "apiProvider": "firstParty",
      "email": "person@example.com",
      "orgId": "00000000-0000-0000-0000-000000000000",
      "orgName": "Example Org",
      "subscriptionType": "team"
    }
    """;

    [Fact]
    public void TryParse_RealLoggedIn_ReadsBehaviorFieldsOnly()
    {
        var ok = ClaudeAuthStatusParser.TryParse(RealLoggedIn, out var status, out var error);

        Assert.True(ok, error);
        Assert.True(status!.LoggedIn);
        Assert.Equal("claude.ai", status.AuthMethod);
        Assert.Equal("team", status.SubscriptionType);
    }

    [Fact]
    public void TryParse_LoggedOut_LoggedInFalse()
    {
        var ok = ClaudeAuthStatusParser.TryParse("""{ "loggedIn": false }""", out var status, out _);

        Assert.True(ok);
        Assert.False(status!.LoggedIn);
        Assert.Null(status.SubscriptionType);
    }

    [Fact]
    public void TryParse_MissingLoggedIn_TreatedAsNotLoggedIn()
    {
        var ok = ClaudeAuthStatusParser.TryParse("""{ "authMethod": "claude.ai" }""", out var status, out _);

        Assert.True(ok);
        Assert.False(status!.LoggedIn);
    }

    [Fact]
    public void TryParse_Empty_Fails()
    {
        var ok = ClaudeAuthStatusParser.TryParse("", out var status, out var error);

        Assert.False(ok);
        Assert.Null(status);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_InvalidJson_Fails()
    {
        var ok = ClaudeAuthStatusParser.TryParse("not json at all", out var status, out var error);

        Assert.False(ok);
        Assert.Null(status);
        Assert.NotNull(error);
    }
}
