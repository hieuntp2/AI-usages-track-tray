using System.Globalization;
using System.Text.RegularExpressions;
using AiUsageTray.Infrastructure;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.Claude;

/// <summary>
/// Turns the flat screen-reader text of Claude Code's interactive <c>/usage</c> panel into
/// normalized models. Pure and stateless: it takes a captured string (possibly still carrying ANSI
/// control sequences, CRLF, wrapped lines, and progressive re-renders) and never touches a process
/// or the file system, so it is fully unit-testable against fixtures.
///
/// Design notes for the fragility this necessarily inherits from parsing an interactive command:
/// <list type="bullet">
/// <item>Windows are gated on their heading ("Current session", "Current week (...)") so unrelated
///   lines that happen to contain "%" or "usage" (promo text, the "what's contributing" section)
///   can never be mistaken for a quota value.</item>
/// <item>The panel repaints as it loads; the same heading can appear several times with changing
///   values. The LAST occurrence of each window wins.</item>
/// <item>A percentage that can't be read stays <c>null</c> (unknown) - it is never coerced to 0.</item>
/// <item>Reset text is preserved verbatim; an absolute instant is only produced when the wording is
///   unambiguous (contains an explicit month/day), otherwise <c>ResetsAt</c> stays null.</item>
/// </list>
/// </summary>
public static partial class ClaudeUsageOutputParser
{
    // "51%", "51.5%", or the doubled "51% 51% used" render - first percentage on the line wins.
    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*%")]
    private static partial Regex PercentRegex();

    // "Total cost:   $1.2345"
    [GeneratedRegex(@"Total cost:\s*\$\s*([\d,]+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CostRegex();

    // "Current week (all models)" / "Current week (Fable)" -> capture the parenthesized scope.
    [GeneratedRegex(@"^current week\s*\((?<scope>[^)]+)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WeekHeadingRegex();

    // Non-interactive render: everything after the heading colon, e.g. "16% used · resets Jul 17,
    // 11:30am (Asia/Saigon)". Anchored on "<percent>% used" so prose after a colon can't qualify.
    [GeneratedRegex(@"^(?<percent>\d+(?:\.\d+)?)\s*%\s*used\b", RegexOptions.IgnoreCase)]
    private static partial Regex SingleLineValueRegex();

    private const string FiveHourKind = "five_hour";
    private const string SevenDayKind = "seven_day";
    private const string WeeklyModelPrefix = "weekly_model:";

    public static ClaudeCliUsageResult Parse(string rawOutput) => Parse(rawOutput, DateTimeOffset.Now);

    public static ClaudeCliUsageResult Parse(string rawOutput, DateTimeOffset now)
    {
        var text = AnsiText.StripAndNormalizeNewlines(rawOutput ?? string.Empty);
        var lines = text.Split('\n');

        // Preserve first-seen order of window kinds but let later renders overwrite the values.
        var order = new List<string>();
        var byKind = new Dictionary<string, ClaudeCliUsageWindow>(StringComparer.Ordinal);
        decimal? sessionCost = null;

        PendingWindow? pending = null;

        void Finalize()
        {
            if (pending is { } p)
            {
                var window = p.ToWindow(now);
                if (!byKind.ContainsKey(window.Kind))
                {
                    order.Add(window.Kind);
                }

                byKind[window.Kind] = window;
                pending = null;
            }
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (sessionCost is null && CostRegex().Match(line) is { Success: true } costMatch)
            {
                if (decimal.TryParse(costMatch.Groups[1].Value.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost))
                {
                    sessionCost = cost;
                }
            }

            // Headless print mode ("claude --ax-screen-reader /usage" with no TTY) emits each window
            // as one line: "Current week (all models): 26% used · resets Jul 20, 11pm (Asia/Saigon)".
            if (TryParseSingleLineWindow(line, now) is { } complete)
            {
                Finalize();
                if (!byKind.ContainsKey(complete.Kind))
                {
                    order.Add(complete.Kind);
                }

                byKind[complete.Kind] = complete;
                continue;
            }

            if (TryMatchHeading(line, out var heading))
            {
                Finalize();
                pending = new PendingWindow(heading!.Kind, heading.DisplayName, heading.ModelName);
                continue;
            }

            if (pending is null)
            {
                continue;
            }

            if (pending.UsedPercent is null && LooksLikePercentLine(line) && PercentRegex().Match(line) is { Success: true } pm
                && decimal.TryParse(pm.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
            {
                pending.UsedPercent = percent;
                continue;
            }

            if (line.StartsWith("Resets", StringComparison.OrdinalIgnoreCase))
            {
                pending.RawResetText = line;
                Finalize();
            }
        }

        Finalize();

        return new ClaudeCliUsageResult(order.Select(k => byKind[k]).ToList(), sessionCost);
    }

    /// <summary>Maps parsed CLI windows into the normalized model, clamping percentages for display only.</summary>
    public static IReadOnlyList<UsageWindow> ToUsageWindows(ClaudeCliUsageResult result)
    {
        var windows = new List<UsageWindow>(result.Windows.Count);
        foreach (var w in result.Windows)
        {
            var used = UsageWindow.ClampPercent(w.UsedPercent);
            decimal? remaining = used is null ? null : Math.Clamp(100m - used.Value, 0m, 100m);
            windows.Add(new UsageWindow(
                Id: w.Kind,
                DisplayName: w.DisplayName,
                UsedPercent: used,
                RemainingPercent: remaining,
                ResetsAt: w.ResetsAt,
                Duration: null,
                UsedValue: null,
                LimitValue: null,
                Unit: "percent"));
        }

        return windows;
    }

    /// <summary>
    /// Parses the one-line-per-window shape of the non-interactive `/usage` render. The text left of
    /// the first colon must be a known window heading, and the remainder must start with
    /// "&lt;percent&gt;% used" - so lines like "Top MCP servers: codegraph 27%" can never qualify.
    /// The reset wording, when present, starts at "resets" and is preserved verbatim.
    /// </summary>
    private static ClaudeCliUsageWindow? TryParseSingleLineWindow(string line, DateTimeOffset now)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0 || colon >= line.Length - 1)
        {
            return null;
        }

        if (!TryMatchHeading(line[..colon].Trim(), out var heading))
        {
            return null;
        }

        var rest = line[(colon + 1)..].Trim();
        var valueMatch = SingleLineValueRegex().Match(rest);
        if (!valueMatch.Success
            || !decimal.TryParse(valueMatch.Groups["percent"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
        {
            return null;
        }

        string? rawResetText = null;
        var resetIndex = rest.IndexOf("resets", StringComparison.OrdinalIgnoreCase);
        if (resetIndex >= 0)
        {
            rawResetText = rest[resetIndex..].Trim();
        }

        return new ClaudeCliUsageWindow(
            heading!.Kind,
            heading.DisplayName,
            percent,
            rawResetText,
            ClaudeUsageResetParser.TryParse(rawResetText, now),
            heading.ModelName);
    }

    private static bool TryMatchHeading(string line, out Heading? heading)
    {
        heading = null;

        // 5-hour / session window. Claude has used both "Current session" and "5-hour limit" wording.
        if (line.Equals("Current session", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(line, @"^(current\s+)?5[- ]hour( limit)?$", RegexOptions.IgnoreCase))
        {
            heading = new Heading(FiveHourKind, "5-hour limit", null);
            return true;
        }

        var weekMatch = WeekHeadingRegex().Match(line);
        if (weekMatch.Success)
        {
            var scope = weekMatch.Groups["scope"].Value.Trim();
            if (scope.Equals("all models", StringComparison.OrdinalIgnoreCase))
            {
                heading = new Heading(SevenDayKind, "Weekly limit (all models)", null);
            }
            else
            {
                heading = new Heading(WeeklyModelPrefix + scope.ToLowerInvariant(), $"Weekly limit ({scope})", scope);
            }

            return true;
        }

        // "Current week" with no scope, or a plain "Weekly limit" / "7-day" heading.
        if (line.Equals("Current week", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(line, @"^(weekly limit|7[- ]day( limit)?)$", RegexOptions.IgnoreCase))
        {
            heading = new Heading(SevenDayKind, "Weekly limit", null);
            return true;
        }

        return false;
    }

    /// <summary>
    /// A quota line is a short line that is essentially just a percentage (optionally the doubled
    /// "X% X% used" render). This deliberately rejects prose like "80% of your usage was at >150k".
    /// </summary>
    private static bool LooksLikePercentLine(string line)
    {
        if (!line.Contains('%'))
        {
            return false;
        }

        // Strip the percentages and the word "used"; anything substantial left over means it's prose.
        var residue = PercentRegex().Replace(line, string.Empty);
        residue = Regex.Replace(residue, @"\bused\b", string.Empty, RegexOptions.IgnoreCase).Trim();
        return residue.Length == 0;
    }

    private sealed record Heading(string Kind, string DisplayName, string? ModelName);

    private sealed class PendingWindow
    {
        public PendingWindow(string kind, string displayName, string? modelName)
        {
            Kind = kind;
            DisplayName = displayName;
            ModelName = modelName;
        }

        public string Kind { get; }

        public string DisplayName { get; }

        public string? ModelName { get; }

        public decimal? UsedPercent { get; set; }

        public string? RawResetText { get; set; }

        public ClaudeCliUsageWindow ToWindow(DateTimeOffset now) =>
            new(Kind, DisplayName, UsedPercent, RawResetText, ClaudeUsageResetParser.TryParse(RawResetText, now), ModelName);
    }
}
