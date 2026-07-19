using AiUsageTray.Models;

namespace AiUsageTray.Services;

public sealed record NotificationEvent(string Title, string Message);

/// <summary>
/// Produces durable, at-most-once notifications when a quota window reaches 100% or genuinely
/// resets. Delivery markers are persisted before subscribers are notified, so repeated refreshes
/// and application restarts cannot replay the same event.
/// </summary>
public sealed class NotificationService
{
    private const decimal LimitReachedPercent = 100m;
    private const decimal ResetNotifyMinimumPriorUsedPercent = 10m;
    private const decimal ResetNotifyMinimumDropPercent = 30m;

    private readonly object _sync = new();
    private readonly SettingsService _settingsService;
    private readonly INotificationStateStore _stateStore;
    private readonly List<NotificationEvent> _pendingEvents = new();
    private NotificationStateDocument _state;
    private bool _hasUnpersistedChanges;

    public event Action<NotificationEvent>? NotificationRequested;

    public NotificationService(SettingsService settingsService)
        : this(settingsService, new NotificationStateStore())
    {
    }

    internal NotificationService(SettingsService settingsService, INotificationStateStore stateStore)
    {
        _settingsService = settingsService;
        _stateStore = stateStore;
        _state = stateStore.Load().State;
    }

    public void Evaluate(UsageSnapshot snapshot)
    {
        var providerSettings = _settingsService.Current.GetOrAddProvider(snapshot.ProviderId);
        if (!providerSettings.Notifications.Enabled)
        {
            return;
        }

        List<NotificationEvent> readyEvents;
        lock (_sync)
        {
            foreach (var window in snapshot.Windows)
            {
                if (window.UsedPercent is not { } used)
                {
                    continue;
                }

                EvaluateWindow(snapshot, window, used);
            }

            readyEvents = PersistAndTakePendingEvents();
        }

        foreach (var notificationEvent in readyEvents)
        {
            NotificationRequested?.Invoke(notificationEvent);
        }
    }

    private void EvaluateWindow(UsageSnapshot snapshot, UsageWindow window, decimal used)
    {
        var key = $"{snapshot.ProviderId}:{window.Id}";
        if (!_state.Windows.TryGetValue(key, out var state))
        {
            state = new NotificationWindowState
            {
                ResetsAt = window.ResetsAt,
                LastUsedPercent = used,
                LimitReachedNotified =
                    _state.SuppressNotificationsForUnseenWindows && used >= LimitReachedPercent,
            };
            _state.Windows[key] = state;
            _hasUnpersistedChanges = true;

            if (!_state.SuppressNotificationsForUnseenWindows && used >= LimitReachedPercent)
            {
                QueueLimitNotification(snapshot, window, state);
            }

            return;
        }

        var resetTimeChanged = state.ResetsAt != window.ResetsAt;
        var meaningfulTimedReset = resetTimeChanged &&
                                   state.ResetsAt is not null &&
                                   state.LastUsedPercent is { } priorTimedUsage &&
                                   priorTimedUsage >= ResetNotifyMinimumPriorUsedPercent &&
                                   used < priorTimedUsage;
        var meaningfulCliffReset = !resetTimeChanged &&
                                   state.LastUsedPercent is { } priorUsage &&
                                   priorUsage >= ResetNotifyMinimumPriorUsedPercent &&
                                   priorUsage - used >= ResetNotifyMinimumDropPercent;

        if (meaningfulTimedReset || meaningfulCliffReset)
        {
            state = new NotificationWindowState
            {
                ResetsAt = window.ResetsAt,
                LastUsedPercent = used,
            };
            _state.Windows[key] = state;
            _hasUnpersistedChanges = true;
            _pendingEvents.Add(CreateResetNotification(snapshot, window, used));
        }
        else
        {
            if (state.ResetsAt != window.ResetsAt)
            {
                state.ResetsAt = window.ResetsAt;
                _hasUnpersistedChanges = true;
            }

            if (state.LastUsedPercent != used)
            {
                state.LastUsedPercent = used;
                _hasUnpersistedChanges = true;
            }
        }

        if (used >= LimitReachedPercent && !state.LimitReachedNotified)
        {
            QueueLimitNotification(snapshot, window, state);
        }
    }

    private void QueueLimitNotification(
        UsageSnapshot snapshot,
        UsageWindow window,
        NotificationWindowState state)
    {
        state.LimitReachedNotified = true;
        _hasUnpersistedChanges = true;

        var resetText = FormatReset(window.ResetsAt);
        var message =
            $"{snapshot.ProviderName} {window.DisplayName.ToLowerInvariant()} reached 100%." +
            (resetText is null ? string.Empty : $" {resetText}");
        _pendingEvents.Add(new NotificationEvent($"{snapshot.ProviderName} usage", message));
    }

    private static NotificationEvent CreateResetNotification(
        UsageSnapshot snapshot,
        UsageWindow window,
        decimal used) =>
        new(
            $"{snapshot.ProviderName} usage reset",
            $"{snapshot.ProviderName} {window.DisplayName.ToLowerInvariant()} has been reset. Usage is now {used:0}%.");

    private List<NotificationEvent> PersistAndTakePendingEvents()
    {
        if (!_hasUnpersistedChanges || !_stateStore.TrySave(_state.Clone()))
        {
            return new List<NotificationEvent>();
        }

        _hasUnpersistedChanges = false;
        var readyEvents = _pendingEvents.ToList();
        _pendingEvents.Clear();
        return readyEvents;
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
