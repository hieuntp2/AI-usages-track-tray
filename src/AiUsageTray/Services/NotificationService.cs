using AiUsageTray.Models;

namespace AiUsageTray.Services;

public sealed record NotificationEvent(string Title, string Message);

/// <summary>
/// Decides when a quota notification should fire. Two kinds are produced, both deduplicated per
/// provider+window so background refreshes can call <see cref="Evaluate"/> as often as they like:
/// threshold notifications (usage climbed past 70/90/100%), and reset notifications (a new quota
/// period started - either the window's reset time changed, or usage dropped sharply while it had
/// been meaningfully consumed).
/// </summary>
public sealed class NotificationService
{
    /// <summary>Usage must have reached at least this much for a reset to be worth announcing.</summary>
    private const decimal ResetNotifyMinimumPriorUsedPercent = 10m;

    /// <summary>Without a reset-time change, usage must fall by at least this much to count as a reset.</summary>
    private const decimal ResetNotifyMinimumDropPercent = 30m;

    private sealed class WindowNotificationState
    {
        public DateTimeOffset? ResetsAt;
        public decimal? LastUsedPercent;
        public HashSet<int> NotifiedThresholds { get; } = new();
    }

    private readonly SettingsService _settingsService;
    private readonly Dictionary<string, WindowNotificationState> _states = new();

    public event Action<NotificationEvent>? NotificationRequested;

    public NotificationService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public void Evaluate(UsageSnapshot snapshot)
    {
        var providerSettings = _settingsService.Current.GetOrAddProvider(snapshot.ProviderId);
        if (!providerSettings.Notifications.Enabled)
        {
            return;
        }

        foreach (var window in snapshot.Windows)
        {
            if (window.UsedPercent is not { } used)
            {
                continue;
            }

            var key = $"{snapshot.ProviderId}:{window.Id}";
            if (!_states.TryGetValue(key, out var state) || state.ResetsAt != window.ResetsAt)
            {
                var previous = _states.GetValueOrDefault(key);
                NotifyResetIfMeaningful(snapshot, window, previous, used);

                state = new WindowNotificationState { ResetsAt = window.ResetsAt };
                _states[key] = state;
            }
            else if (state.LastUsedPercent is { } lastUsed &&
                     lastUsed >= ResetNotifyMinimumPriorUsedPercent &&
                     lastUsed - used >= ResetNotifyMinimumDropPercent)
            {
                // Reset time didn't change (or the provider never reports one), but usage fell off
                // a cliff - treat as a reset and start a fresh dedup window for thresholds too.
                NotifyReset(snapshot, window, used);
                state = new WindowNotificationState { ResetsAt = window.ResetsAt };
                _states[key] = state;
            }

            state.LastUsedPercent = used;

            foreach (var threshold in providerSettings.Notifications.Percentages.OrderBy(t => t))
            {
                if (used >= threshold && state.NotifiedThresholds.Add(threshold))
                {
                    var resetText = FormatReset(window.ResetsAt);
                    var message = $"{snapshot.ProviderName} {window.DisplayName.ToLowerInvariant()} reached {threshold}%.{(resetText is null ? "" : $" {resetText}")}";
                    NotificationRequested?.Invoke(new NotificationEvent($"{snapshot.ProviderName} usage", message));
                }
            }
        }
    }

    private void NotifyResetIfMeaningful(UsageSnapshot snapshot, Models.UsageWindow window, WindowNotificationState? previous, decimal currentUsed)
    {
        // Only announce when a period we actually watched ends: the old state must exist, have had
        // real consumption, and the new reading must be lower - otherwise every first sighting of a
        // window (app start, provider newly added) would produce a bogus "reset" balloon.
        if (previous is { ResetsAt: not null, LastUsedPercent: { } lastUsed } &&
            lastUsed >= ResetNotifyMinimumPriorUsedPercent &&
            currentUsed < lastUsed)
        {
            NotifyReset(snapshot, window, currentUsed);
        }
    }

    private void NotifyReset(UsageSnapshot snapshot, Models.UsageWindow window, decimal currentUsed)
    {
        var message = $"{snapshot.ProviderName} {window.DisplayName.ToLowerInvariant()} has been reset. Usage is now {currentUsed:0}%.";
        NotificationRequested?.Invoke(new NotificationEvent($"{snapshot.ProviderName} usage reset", message));
    }

    public void ResetProvider(string providerId)
    {
        foreach (var key in _states.Keys.Where(k => k.StartsWith($"{providerId}:", StringComparison.Ordinal)).ToList())
        {
            _states.Remove(key);
        }
    }

    private static string? FormatReset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } value)
        {
            return null;
        }

        var local = value.ToLocalTime();
        return $"It resets {local:dddd} at {local:HH:mm}.";
    }
}
