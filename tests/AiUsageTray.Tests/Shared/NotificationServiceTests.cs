using System.IO;
using AiUsageTray.Infrastructure;
using AiUsageTray.Models;
using AiUsageTray.Services;
using AiUsageTray.Tests.TestSupport;
using Xunit;

namespace AiUsageTray.Tests.Shared;

[Collection("IsolatedAppData")]
public class NotificationServiceTests
{
    private static UsageSnapshot MakeSnapshot(decimal usedPercent, DateTimeOffset? resetsAt) => new(
        "codex", "OpenAI Codex", null, null, ProviderConnectionStatus.Available, DateTimeOffset.UtcNow, "codex app-server",
        new[] { new UsageWindow("primary", "5-hour limit", usedPercent, 100 - usedPercent, resetsAt, null, null, null, "percent") },
        Array.Empty<UsageMetric>(), null);

    [Theory]
    [InlineData(70)]
    [InlineData(90)]
    [InlineData(99.9)]
    public void Evaluate_BelowOneHundred_NeverFiresLimitNotification(decimal usage)
    {
        using var isolated = new IsolatedAppData();
        var service = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        service.NotificationRequested += fired.Add;

        service.Evaluate(MakeSnapshot(usage, DateTimeOffset.UtcNow.AddHours(3)));

        Assert.Empty(fired);
    }

    [Fact]
    public void Evaluate_ReachesOneHundred_FiresLimitOnce()
    {
        using var isolated = new IsolatedAppData();
        var service = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        service.NotificationRequested += fired.Add;
        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);

        service.Evaluate(MakeSnapshot(99, resetsAt));
        service.Evaluate(MakeSnapshot(100, resetsAt));
        service.Evaluate(MakeSnapshot(100, resetsAt));

        Assert.Contains("reached 100%", Assert.Single(fired).Message);
    }

    [Fact]
    public void Evaluate_SameFullPeriodAfterServiceRestart_DoesNotRepeat()
    {
        using var isolated = new IsolatedAppData();
        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
        var first = new NotificationService(new SettingsService());
        var firstEvents = new List<NotificationEvent>();
        first.NotificationRequested += firstEvents.Add;
        first.Evaluate(MakeSnapshot(100, resetsAt));
        Assert.Single(firstEvents);

        var restarted = new NotificationService(new SettingsService());
        var restartedEvents = new List<NotificationEvent>();
        restarted.NotificationRequested += restartedEvents.Add;
        restarted.Evaluate(MakeSnapshot(100, resetsAt));

        Assert.Empty(restartedEvents);
    }

    [Fact]
    public void Evaluate_NotificationsDisabledForProvider_NeverFires()
    {
        using var isolated = new IsolatedAppData();
        var settingsService = new SettingsService();
        settingsService.Update(s => s.GetOrAddProvider("codex").Notifications.Enabled = false);
        var service = new NotificationService(settingsService);
        var fired = new List<NotificationEvent>();
        service.NotificationRequested += fired.Add;

        service.Evaluate(MakeSnapshot(100, DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Empty(fired);
    }

    [Fact]
    public void Evaluate_ResetsAtChangesAndUsageDrops_FiresResetNotification()
    {
        using var isolated = new IsolatedAppData();
        var service = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        service.NotificationRequested += fired.Add;

        service.Evaluate(MakeSnapshot(65, DateTimeOffset.UtcNow.AddHours(1)));
        fired.Clear();
        service.Evaluate(MakeSnapshot(2, DateTimeOffset.UtcNow.AddHours(6)));

        var reset = Assert.Single(fired, e => e.Title.Contains("reset", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("has been reset", reset.Message);
        Assert.Contains("2%", reset.Message);
    }

    [Fact]
    public void Evaluate_FirstSighting_DoesNotFireResetNotification()
    {
        using var isolated = new IsolatedAppData();
        var service = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        service.NotificationRequested += fired.Add;

        service.Evaluate(MakeSnapshot(3, DateTimeOffset.UtcNow.AddHours(2)));

        Assert.Empty(fired);
    }

    [Fact]
    public void Evaluate_ResetsAtChangesButPriorUsageWasTrivial_NoResetNotification()
    {
        using var isolated = new IsolatedAppData();
        var service = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        service.NotificationRequested += fired.Add;

        service.Evaluate(MakeSnapshot(4, DateTimeOffset.UtcNow.AddHours(1)));
        service.Evaluate(MakeSnapshot(1, DateTimeOffset.UtcNow.AddHours(6)));

        Assert.Empty(fired);
    }

    [Fact]
    public void Evaluate_SharpUsageDropWithoutResetTimeChange_FiresResetOnlyOnce()
    {
        using var isolated = new IsolatedAppData();
        var service = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        service.NotificationRequested += fired.Add;

        service.Evaluate(MakeSnapshot(80, null));
        service.Evaluate(MakeSnapshot(5, null));
        service.Evaluate(MakeSnapshot(5, null));

        Assert.Single(fired, e => e.Title.Contains("reset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_ResetAfterServiceRestart_DoesNotRepeatAndRearmsLimit()
    {
        using var isolated = new IsolatedAppData();
        var firstPeriod = DateTimeOffset.UtcNow.AddHours(1);
        var secondPeriod = DateTimeOffset.UtcNow.AddHours(6);
        var first = new NotificationService(new SettingsService());
        first.Evaluate(MakeSnapshot(100, firstPeriod));

        var resetService = new NotificationService(new SettingsService());
        var resetEvents = new List<NotificationEvent>();
        resetService.NotificationRequested += resetEvents.Add;
        resetService.Evaluate(MakeSnapshot(2, secondPeriod));
        Assert.Single(resetEvents, e => e.Title.Contains("reset", StringComparison.OrdinalIgnoreCase));

        var restarted = new NotificationService(new SettingsService());
        var restartedEvents = new List<NotificationEvent>();
        restarted.NotificationRequested += restartedEvents.Add;
        restarted.Evaluate(MakeSnapshot(2, secondPeriod));
        Assert.Empty(restartedEvents);

        restarted.Evaluate(MakeSnapshot(100, secondPeriod));
        Assert.Single(restartedEvents, e => e.Message.Contains("reached 100%", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_CorruptRecoveredState_BaselinesFullWindowWithoutNotification()
    {
        using var isolated = new IsolatedAppData();
        File.WriteAllText(AppPaths.NotificationStateFile, "{broken");
        var service = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        service.NotificationRequested += fired.Add;

        service.Evaluate(MakeSnapshot(100, DateTimeOffset.UtcNow.AddHours(3)));

        Assert.Empty(fired);
    }

    [Fact]
    public void Evaluate_DurableWriteFails_DelaysEventUntilWriteSucceeds()
    {
        using var isolated = new IsolatedAppData();
        var store = new ControllableStateStore();
        var service = new NotificationService(new SettingsService(), store);
        var fired = new List<NotificationEvent>();
        service.NotificationRequested += fired.Add;
        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);

        service.Evaluate(MakeSnapshot(100, resetsAt));
        Assert.Empty(fired);

        store.AllowSave = true;
        service.Evaluate(MakeSnapshot(100, resetsAt));

        Assert.Single(fired);
    }

    private sealed class ControllableStateStore : INotificationStateStore
    {
        public bool AllowSave { get; set; }

        public NotificationStateDocument State { get; private set; } = new();

        public NotificationStateLoadResult Load() => new(State.Clone());

        public bool TrySave(NotificationStateDocument state)
        {
            if (!AllowSave)
            {
                return false;
            }

            State = state.Clone();
            return true;
        }
    }
}
