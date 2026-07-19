using System.IO;
using AiUsageTray.Infrastructure;
using AiUsageTray.Services;
using AiUsageTray.Tests.TestSupport;
using Xunit;

namespace AiUsageTray.Tests.Shared;

[Collection("IsolatedAppData")]
public sealed class NotificationStateStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsEmptyNormalState()
    {
        using var isolated = new IsolatedAppData();

        var result = new NotificationStateStore().Load();

        Assert.Empty(result.State.Windows);
        Assert.False(result.State.SuppressNotificationsForUnseenWindows);
    }

    [Fact]
    public void TrySave_ThenLoad_RoundTripsWindowState()
    {
        using var isolated = new IsolatedAppData();
        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
        var state = new NotificationStateDocument();
        state.Windows["codex:primary"] = new NotificationWindowState
        {
            ResetsAt = resetsAt,
            LastUsedPercent = 100m,
            LimitReachedNotified = true,
        };
        var store = new NotificationStateStore();

        Assert.True(store.TrySave(state));
        var loaded = store.Load().State.Windows["codex:primary"];

        Assert.Equal(resetsAt, loaded.ResetsAt);
        Assert.Equal(100m, loaded.LastUsedPercent);
        Assert.True(loaded.LimitReachedNotified);
    }

    [Fact]
    public void Load_MalformedFile_RecoversWithConservativeBaselineMode()
    {
        using var isolated = new IsolatedAppData();
        File.WriteAllText(AppPaths.NotificationStateFile, "{not-json");

        var result = new NotificationStateStore().Load();

        Assert.Empty(result.State.Windows);
        Assert.True(result.State.SuppressNotificationsForUnseenWindows);
        Assert.NotEmpty(Directory.GetFiles(AppPaths.BackupsDir, "notification-state*.bak"));
    }
}
