using AiUsageTray.Models;

namespace AiUsageTray.ViewModels;

/// <summary>Display-ready projection of a single <see cref="UsageWindow"/> for one progress row in the flyout.</summary>
public sealed class UsageWindowRowViewModel
{
    public string DisplayName { get; }

    public bool HasPercent { get; }

    public double UsedPercentValue { get; }

    public string UsageSummaryText { get; }

    /// <summary>Compact right-aligned value for the row header: "16%" for percentage windows,
    /// the value summary for count-based windows, "—" when usage is unknown.</summary>
    public string HeadlineValueText { get; }

    public string? ResetText { get; }

    public string ProgressLevel { get; } // "Normal" | "Warn" | "Danger" - drives progress bar color in the view.

    public UsageWindowRowViewModel(UsageWindow window, TimeDisplayMode timeDisplay)
    {
        DisplayName = window.DisplayName;

        if (window.UsedPercent is { } used)
        {
            HasPercent = true;
            UsedPercentValue = (double)used;
            var remaining = window.RemainingPercent ?? (100m - used);
            UsageSummaryText = $"Used {used:0}% · Remaining {remaining:0}%";
            HeadlineValueText = $"{used:0}%";
            ProgressLevel = used >= 90 ? "Danger" : used >= 70 ? "Warn" : "Normal";
        }
        else if (window.UsedValue is { } usedValue)
        {
            HasPercent = false;
            var limitText = window.LimitValue is { } limit ? $" / {limit:0.##}" : string.Empty;
            UsageSummaryText = $"{usedValue:0.##}{limitText} {window.Unit}".Trim();
            HeadlineValueText = UsageSummaryText;
            ProgressLevel = "Normal";
        }
        else
        {
            HasPercent = false;
            UsageSummaryText = "Usage unknown";
            HeadlineValueText = "—";
            ProgressLevel = "Normal";
        }

        ResetText = FormatReset(window.ResetsAt, timeDisplay);
    }

    private static string? FormatReset(DateTimeOffset? resetsAt, TimeDisplayMode timeDisplay)
    {
        if (resetsAt is not { } value)
        {
            return null;
        }

        var local = value.ToLocalTime();
        var now = DateTimeOffset.Now;
        var relative = FormatRelative(local - now);
        var exact = FormatExact(local, now);

        return timeDisplay switch
        {
            TimeDisplayMode.Exact => $"Resets {exact}",
            TimeDisplayMode.Both => $"Resets {relative} ({exact})",
            _ => $"Resets {relative}",
        };
    }

    private static string FormatRelative(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero)
        {
            return "soon";
        }

        if (delta.TotalHours < 1)
        {
            return $"in {(int)delta.TotalMinutes}m";
        }

        if (delta.TotalDays < 1)
        {
            return $"in {(int)delta.TotalHours}h {delta.Minutes}m";
        }

        return $"in {(int)delta.TotalDays}d {delta.Hours}h";
    }

    private static string FormatExact(DateTimeOffset local, DateTimeOffset now)
    {
        return local.Date == now.Date
            ? $"today at {local:HH:mm}"
            : $"{local:dddd} at {local:HH:mm}";
    }
}
