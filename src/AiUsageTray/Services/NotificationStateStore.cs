using System.Text.Json;
using AiUsageTray.Infrastructure;

namespace AiUsageTray.Services;

internal interface INotificationStateStore
{
    NotificationStateLoadResult Load();

    bool TrySave(NotificationStateDocument state);
}

internal sealed record NotificationStateLoadResult(NotificationStateDocument State);

internal sealed class NotificationStateDocument
{
    public int SchemaVersion { get; set; } = 1;

    public bool SuppressNotificationsForUnseenWindows { get; set; }

    public Dictionary<string, NotificationWindowState> Windows { get; set; } = new(StringComparer.Ordinal);

    public NotificationStateDocument Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        SuppressNotificationsForUnseenWindows = SuppressNotificationsForUnseenWindows,
        Windows = Windows.ToDictionary(
            pair => pair.Key,
            pair => new NotificationWindowState
            {
                ResetsAt = pair.Value.ResetsAt,
                LastUsedPercent = pair.Value.LastUsedPercent,
                LimitReachedNotified = pair.Value.LimitReachedNotified,
            },
            StringComparer.Ordinal),
    };
}

internal sealed class NotificationWindowState
{
    public DateTimeOffset? ResetsAt { get; set; }

    public decimal? LastUsedPercent { get; set; }

    public bool LimitReachedNotified { get; set; }
}

internal sealed class NotificationStateStore : INotificationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public NotificationStateLoadResult Load()
    {
        if (!AtomicFile.TryReadAllText(AppPaths.NotificationStateFile, out var json) ||
            string.IsNullOrWhiteSpace(json))
        {
            return new NotificationStateLoadResult(new NotificationStateDocument());
        }

        try
        {
            var state = JsonSerializer.Deserialize<NotificationStateDocument>(json, JsonOptions)
                        ?? new NotificationStateDocument();
            state.Windows ??= new Dictionary<string, NotificationWindowState>(StringComparer.Ordinal);
            return new NotificationStateLoadResult(state);
        }
        catch (JsonException ex)
        {
            AppLog.Warn(
                "NotificationStateStore",
                $"Corrupted notification state; rebuilding a safe baseline: {ex.Message}");
            TryBackUpCorruptState();

            var recovered = new NotificationStateDocument
            {
                SuppressNotificationsForUnseenWindows = true,
            };
            TrySave(recovered);
            return new NotificationStateLoadResult(recovered);
        }
    }

    public bool TrySave(NotificationStateDocument state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            AtomicFile.WriteAllText(AppPaths.NotificationStateFile, json);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("NotificationStateStore", "Failed to persist notification state", ex);
            return false;
        }
    }

    private static void TryBackUpCorruptState()
    {
        try
        {
            AtomicFile.CreateTimestampedBackup(AppPaths.NotificationStateFile, AppPaths.BackupsDir);
        }
        catch (Exception ex)
        {
            AppLog.Warn(
                "NotificationStateStore",
                $"Could not back up corrupted notification state: {ex.Message}");
        }
    }
}
