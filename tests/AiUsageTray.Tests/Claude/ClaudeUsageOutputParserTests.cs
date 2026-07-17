using AiUsageTray.Providers.Claude;
using Xunit;

namespace AiUsageTray.Tests.Claude;

public class ClaudeUsageOutputParserTests
{
    private const string Esc = "";

    /// <summary>
    /// Sanitized capture of the real interactive `/usage` panel from Claude Code 2.1.195 driven
    /// through a hidden ConPTY with `--safe-mode --ax-screen-reader`. It still carries the ANSI/VT
    /// control sequences, CRLF line endings, the doubled "X% X% used" progress-bar render, and the
    /// progressive repaint (the panel first shows a partial state, then repaints - via cursor moves,
    /// not newlines - with the model-specific weekly window and updated percentages). Personal data
    /// (account name, timezone city, session id, absolute paths) is replaced with neutral placeholders.
    /// </summary>
    private const string RealUsageCapture =
        Esc + "[?25l" + Esc + "[15;1Hyou: /usage" + Esc + "[K\r\n" +
        "Settings  Status   Config   Usage   Stats" + Esc + "[K\r\n" +
        "Session\r\n" +
        "Total cost:            $0.0000\r\n" +
        "Total duration (API):  0s\r\n" +
        "Total duration (wall): 18s\r\n" +
        "Total code changes:    0 lines added, 0 lines removed\r\n" +
        "Usage:                 0 input, 0 output, 0 cache read, 0 cache write\r\n" +
        "Current session\r\n" +
        "50% 50% used\r\n" +
        "Resets 7:30pm (Placeholder/Zone)\r\n" +
        "Current week (all models)\r\n" +
        "52% 52% used\r\n" +
        "Resets Jul 13, 11pm (Placeholder/Zone)\r\n" +
        "What's contributing to your limits usage?\r\n" +
        "Approximate, based on local sessions on this machine\r\n" +
        "Scanning local sessions…\r\n" +
        "Refreshing…\r\n" +
        // Repaint: content separated by cursor moves (ESC[..H) and line erases (ESC[K), NOT newlines.
        "Esc to cancel" + Esc + "[16;29H" + Esc + "[?25h" + Esc + "[?25l" + Esc + "[24;1H" +
        "51% 51% used" + Esc + "[K" +
        "Resets 7:30pm (Placeholder/Zone)" + Esc + "[K" +
        "Current week (all models)" + Esc + "[K" +
        "52% 52% used" + Esc + "[K" +
        "Resets Jul 13, 11pm (Placeholder/Zone)" + Esc + "[K" +
        "Current week (Fable)" + Esc + "[K" +
        "51% 51% used" + Esc + "[K" +
        "Resets Jul 13, 11pm (Placeholder/Zone)" + Esc + "[K" +
        "What's contributing to your limits usage?" + Esc + "[K" +
        "Last 24h" + Esc + "[K" +
        "80% of your usage was at >150k context" + Esc + "[K" +
        "80% of your usage came from subagent-heavy sessions" + Esc + "[K" +
        "Esc to cancel" + Esc + "[13;29H" + Esc + "[?25h\r\n";

    private static readonly DateTimeOffset Now = new(2026, 7, 13, 9, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void Parse_RealCapture_ExtractsFiveHourWeeklyAndModelWindows()
    {
        var result = ClaudeUsageOutputParser.Parse(RealUsageCapture, Now);

        var five = Assert.Single(result.Windows, w => w.Kind == "five_hour");
        var week = Assert.Single(result.Windows, w => w.Kind == "seven_day");
        var fable = Assert.Single(result.Windows, w => w.Kind == "weekly_model:fable");

        // Each window is read from its labeled block. The session shows 50 (its headed value): when
        // the panel repaints the session *value* it does so by cursor-addressing the number without
        // re-emitting the "Current session" heading, so a line parser keeps the last headed reading
        // rather than guessing that an orphaned "51%" belongs to the session. This ~1% loading-jitter
        // gap is a documented limitation of parsing a repainting interactive panel.
        Assert.Equal(50m, five.UsedPercent);
        Assert.Equal(52m, week.UsedPercent);
        Assert.Equal(51m, fable.UsedPercent);
        Assert.Equal("Fable", fable.ModelName);
    }

    [Fact]
    public void Parse_RealCapture_ReadsSessionCost()
    {
        var result = ClaudeUsageOutputParser.Parse(RealUsageCapture, Now);

        Assert.Equal(0m, result.SessionCostUsd);
    }

    [Fact]
    public void Parse_RealCapture_AbsoluteResetParsedTimeOnlyResetNull()
    {
        var result = ClaudeUsageOutputParser.Parse(RealUsageCapture, Now);

        var week = result.Windows.Single(w => w.Kind == "seven_day");
        var five = result.Windows.Single(w => w.Kind == "five_hour");

        // "Jul 13, 11pm" is unambiguous and resolves; "7:30pm" is time-only and must stay null.
        Assert.NotNull(week.ResetsAt);
        Assert.Equal(2026, week.ResetsAt!.Value.Year);
        Assert.Equal(7, week.ResetsAt.Value.Month);
        Assert.Equal(13, week.ResetsAt.Value.Day);
        Assert.Null(five.ResetsAt);

        // Raw text is preserved either way.
        Assert.Contains("7:30pm", five.RawResetText);
    }

    [Fact]
    public void Parse_StripsAnsiAndReadsHeadedRepaintBlock()
    {
        // The model-specific weekly window appears ONLY in the repainted block, whose lines are
        // separated by cursor moves/erases rather than newlines. Reading it proves ANSI handling
        // splits that block into lines correctly (otherwise the value would merge into prose).
        var result = ClaudeUsageOutputParser.Parse(RealUsageCapture, Now);

        var fable = result.Windows.Single(w => w.Kind == "weekly_model:fable");
        Assert.Equal(51m, fable.UsedPercent);
    }

    [Fact]
    public void Parse_DoubledPercent_TakesSingleValue()
    {
        var result = ClaudeUsageOutputParser.Parse("Current session\r\n42% 42% used\r\n", Now);

        Assert.Equal(42m, result.Windows.Single().UsedPercent);
    }

    [Fact]
    public void Parse_DecimalPercent_Preserved()
    {
        var result = ClaudeUsageOutputParser.Parse("Current session\n37.5% used\n", Now);

        Assert.Equal(37.5m, result.Windows.Single().UsedPercent);
    }

    [Fact]
    public void Parse_MissingPercent_StaysNullNeverZero()
    {
        var result = ClaudeUsageOutputParser.Parse("Current session\nResets 7:30pm\n", Now);

        var window = Assert.Single(result.Windows);
        Assert.Null(window.UsedPercent);
    }

    [Fact]
    public void Parse_MalformedPercent_StaysNull()
    {
        // No digits before the '%' - unreadable, so it must remain unknown rather than default to 0.
        var result = ClaudeUsageOutputParser.Parse("Current session\n--% used\nResets 7:30pm\n", Now);

        Assert.Null(result.Windows.Single().UsedPercent);
    }

    [Fact]
    public void Parse_ProseWithPercentUnderNoHeading_Ignored()
    {
        // The "what's contributing" section is prose containing "%" and "usage"; not a window.
        var text = "What's contributing to your limits usage?\n80% of your usage was at >150k context\n";

        var result = ClaudeUsageOutputParser.Parse(text, Now);

        Assert.Empty(result.Windows);
    }

    [Fact]
    public void Parse_ProsePercentDoesNotOverwriteWindowValue()
    {
        var text =
            "Current session\n30% used\nResets 7:30pm\n" +
            "What's contributing to your limits usage?\n80% of your usage was at >150k context\n";

        var result = ClaudeUsageOutputParser.Parse(text, Now);

        Assert.Equal(30m, result.Windows.Single(w => w.Kind == "five_hour").UsedPercent);
    }

    [Fact]
    public void Parse_AlternateWording_FiveHourAndWeekly()
    {
        var text = "5-hour limit\n10% used\nWeekly limit\n20% used\n";

        var result = ClaudeUsageOutputParser.Parse(text, Now);

        Assert.Equal(10m, result.Windows.Single(w => w.Kind == "five_hour").UsedPercent);
        Assert.Equal(20m, result.Windows.Single(w => w.Kind == "seven_day").UsedPercent);
    }

    /// <summary>
    /// Sanitized capture of `claude --ax-screen-reader --safe-mode "/usage"` run with NO terminal
    /// (stdin closed, stdout piped) on Claude Code 2.1.195: the panel renders one line per window
    /// and the process exits on its own. This is the shape the headless probe actually sees.
    /// </summary>
    private const string RealHeadlessCapture =
        "You are currently using your subscription to power your Claude Code usage\n" +
        "\n" +
        "Current session: 16% used · resets Jul 17, 11:30am (Placeholder/Zone)\n" +
        "Current week (all models): 26% used · resets Jul 20, 11pm (Placeholder/Zone)\n" +
        "Current week (Fable): 10% used · resets Jul 20, 11pm (Placeholder/Zone)\n" +
        "\n" +
        "What's contributing to your limits usage?\n" +
        "Approximate, based on local sessions on this machine — does not include other devices or claude.ai. Behaviors are independent characteristics, not a breakdown.\n" +
        "\n" +
        "Last 24h · 438 requests · 6 sessions\n" +
        "  56% of your usage was at >150k context\n" +
        "  Top MCP servers: codegraph 27%\n" +
        "\n" +
        "Last 7d · 2525 requests · 18 sessions\n" +
        "  65% of your usage was at >150k context\n" +
        "  60% of your usage came from subagent-heavy sessions\n" +
        "  Top subagents: general-purpose 15%, Explore 1%\n" +
        "  Top MCP servers: codegraph 4%, ccd_session 1%\n";

    [Fact]
    public void Parse_HeadlessSingleLineCapture_ExtractsAllThreeWindows()
    {
        var result = ClaudeUsageOutputParser.Parse(RealHeadlessCapture, Now);

        var five = Assert.Single(result.Windows, w => w.Kind == "five_hour");
        var week = Assert.Single(result.Windows, w => w.Kind == "seven_day");
        var fable = Assert.Single(result.Windows, w => w.Kind == "weekly_model:fable");

        Assert.Equal(16m, five.UsedPercent);
        Assert.Equal(26m, week.UsedPercent);
        Assert.Equal(10m, fable.UsedPercent);
        Assert.Equal("Fable", fable.ModelName);
        Assert.Equal(3, result.Windows.Count);
    }

    [Fact]
    public void Parse_HeadlessSingleLineCapture_ResolvesAbsoluteResets()
    {
        var result = ClaudeUsageOutputParser.Parse(RealHeadlessCapture, Now);

        var five = result.Windows.Single(w => w.Kind == "five_hour");
        var week = result.Windows.Single(w => w.Kind == "seven_day");

        // The one-line render carries a full month/day for every window, so all resets resolve.
        Assert.Equal(new DateTime(2026, 7, 17, 11, 30, 0), five.ResetsAt!.Value.DateTime);
        Assert.Equal(new DateTime(2026, 7, 20, 23, 0, 0), week.ResetsAt!.Value.DateTime);
        Assert.StartsWith("resets Jul 17", five.RawResetText);
    }

    [Fact]
    public void Parse_HeadlessSingleLineCapture_ProseColonsAndPercentagesIgnored()
    {
        // "Top MCP servers: codegraph 27%" has a colon and a percent but no window heading;
        // "56% of your usage..." has a percent but no heading either. Neither may become a window.
        var result = ClaudeUsageOutputParser.Parse(RealHeadlessCapture, Now);

        Assert.DoesNotContain(result.Windows, w => w.UsedPercent is 27m or 56m or 65m or 60m or 15m or 1m or 4m);
    }

    [Fact]
    public void Parse_SingleLineWithoutReset_PercentStillRead()
    {
        var result = ClaudeUsageOutputParser.Parse("Current session: 42% used\n", Now);

        var window = Assert.Single(result.Windows);
        Assert.Equal(42m, window.UsedPercent);
        Assert.Null(window.RawResetText);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void Parse_SingleLineRepaint_LastValueWins()
    {
        var text =
            "Current session: 10% used · resets Jul 17, 11:30am (Placeholder/Zone)\n" +
            "Current session: 11% used · resets Jul 17, 11:30am (Placeholder/Zone)\n";

        var result = ClaudeUsageOutputParser.Parse(text, Now);

        Assert.Equal(11m, Assert.Single(result.Windows).UsedPercent);
    }

    [Fact]
    public void Parse_EmptyOrNull_ReturnsNoWindows()
    {
        Assert.Empty(ClaudeUsageOutputParser.Parse("", Now).Windows);
        Assert.Empty(ClaudeUsageOutputParser.Parse(null!, Now).Windows);
    }

    [Fact]
    public void ToUsageWindows_ClampsOversizedPercentForDisplay()
    {
        var result = ClaudeUsageOutputParser.Parse("Current session\n150% used\n", Now);

        var windows = ClaudeUsageOutputParser.ToUsageWindows(result);

        Assert.Equal(100m, windows.Single().UsedPercent);
        Assert.Equal(0m, windows.Single().RemainingPercent);
    }

    [Fact]
    public void ToUsageWindows_NullPercentStaysNull()
    {
        var result = new ClaudeCliUsageResult(
            new[] { new ClaudeCliUsageWindow("five_hour", "5-hour limit", null, null, null) },
            null);

        var window = ClaudeUsageOutputParser.ToUsageWindows(result).Single();

        Assert.Null(window.UsedPercent);
        Assert.Null(window.RemainingPercent);
    }
}
