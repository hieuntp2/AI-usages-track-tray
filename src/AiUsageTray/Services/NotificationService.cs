using AiUsageTray.Models;

namespace AiUsageTray.Services;

public sealed record NotificationEvent(string Title, string Message);

/// <summary>
/// Decides when a quota-threshold notification should fire. Tracks which thresholds have already
/// been notified per provider+window, and resets that tracking whenever the window's reset time
/// changes (a new quota period has started). This is the single choke point that prevents
/// re-notifying on every background refresh - callers can invoke <see cref="Evaluate"/> as often
/// as they like.
/// </summary>
public sealed class NotificationService
{
    private sealed class WindowNotificationState
    {
        public DateTimeOffset? ResetsAt;
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
                state = new WindowNotificationState { ResetsAt = window.ResetsAt };
                _states[key] = state;
            }

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
