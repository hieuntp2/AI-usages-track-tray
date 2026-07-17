using System.Globalization;
using System.Text.RegularExpressions;

namespace AiUsageTray.Providers.Claude;

/// <summary>
/// Best-effort conversion of the reset wording in the <c>/usage</c> panel to an absolute instant.
///
/// Reset lines come in two shapes: an absolute date ("Resets Jul 13, 11pm (Asia/Saigon)") and a
/// time-only form ("Resets 7:30pm (Asia/Saigon)"). We only resolve the absolute-date form, where the
/// month and day remove the ambiguity; the time-only form is left <c>null</c> rather than guessing a
/// date (guessing across midnight/timezones would risk showing a wrong reset). The raw text is always
/// preserved by the caller, so nothing is lost when we decline to parse.
/// </summary>
public static partial class ClaudeUsageResetParser
{
    // "Resets Jul 13, 11pm (Asia/Saigon)" or "Resets Jul 13, 11:30pm" - month name + day + time.
    [GeneratedRegex(@"Resets\s+(?<month>[A-Za-z]{3,9})\s+(?<day>\d{1,2}),?\s+(?<time>\d{1,2}(?::\d{2})?\s*(?:am|pm))",
        RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteRegex();

    public static DateTimeOffset? TryParse(string? rawResetText, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(rawResetText))
        {
            return null;
        }

        var match = AbsoluteRegex().Match(rawResetText);
        if (!match.Success)
        {
            return null;
        }

        var month = match.Groups["month"].Value;
        var day = match.Groups["day"].Value;
        var time = match.Groups["time"].Value.Replace(" ", string.Empty);

        // Reset dates never carry a year on screen; assume the nearest sensible year (this or next).
        foreach (var year in new[] { now.Year, now.Year + 1 })
        {
            var candidate = $"{month} {day} {year} {time}";
            if (DateTime.TryParseExact(candidate,
                    new[] { "MMM d yyyy h:mmtt", "MMM d yyyy htt", "MMMM d yyyy h:mmtt", "MMMM d yyyy htt" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var parsed))
            {
                var result = new DateTimeOffset(parsed);

                // A December reset seen in January belongs to last year; pull it back if we overshot.
                if (year == now.Year && result < now.AddMonths(-6))
                {
                    continue;
                }

                return result;
            }
        }

        return null;
    }
}
