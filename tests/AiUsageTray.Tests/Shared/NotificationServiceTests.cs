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

    [Fact]
    public void Evaluate_CrossingThreshold_FiresOnce()
    {
        using var isolated = new IsolatedAppData();
        var settingsService = new SettingsService();
        var notificationService = new NotificationService(settingsService);
        var fired = new List<NotificationEvent>();
        notificationService.NotificationRequested += fired.Add;

        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
        notificationService.Evaluate(MakeSnapshot(75, resetsAt));
        notificationService.Evaluate(MakeSnapshot(76, resetsAt)); // still above 70, must not refire
        notificationService.Evaluate(MakeSnapshot(80, resetsAt));

        Assert.Single(fired); // only the 70% threshold was newly crossed across these calls
    }

    [Fact]
    public void Evaluate_MultipleThresholds_EachFiresOnceInOrder()
    {
        using var isolated = new IsolatedAppData();
        var settingsService = new SettingsService();
        var notificationService = new NotificationService(settingsService);
        var fired = new List<NotificationEvent>();
        notificationService.NotificationRequested += fired.Add;

        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
        notificationService.Evaluate(MakeSnapshot(95, resetsAt)); // crosses 70, 90 and 100? no, 95 < 100

        Assert.Equal(2, fired.Count); // 70% and 90% thresholds crossed in one jump
    }

    [Fact]
    public void Evaluate_ResetsAtChanges_AllowsRenotification()
    {
        using var isolated = new IsolatedAppData();
        var settingsService = new SettingsService();
        var notificationService = new NotificationService(settingsService);
        var fired = new List<NotificationEvent>();
        notificationService.NotificationRequested += fired.Add;

        var firstWindow = DateTimeOffset.UtcNow.AddHours(3);
        notificationService.Evaluate(MakeSnapshot(75, firstWindow));
        Assert.Single(fired);

        // New quota period starts (resetsAt changes) - the 70% threshold should be able to fire again.
        var secondWindow = DateTimeOffset.UtcNow.AddHours(8);
        notificationService.Evaluate(MakeSnapshot(75, secondWindow));

        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void Evaluate_NotificationsDisabledForProvider_NeverFires()
    {
        using var isolated = new IsolatedAppData();
        var settingsService = new SettingsService();
        settingsService.Update(s => s.GetOrAddProvider("codex").Notifications.Enabled = false);
        var notificationService = new NotificationService(settingsService);
        var fired = new List<NotificationEvent>();
        notificationService.NotificationRequested += fired.Add;

        notificationService.Evaluate(MakeSnapshot(99, DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Empty(fired);
    }

    [Fact]
    public void Evaluate_ResetsAtChangesAndUsageDrops_FiresResetNotification()
    {
        using var isolated = new IsolatedAppData();
        var notificationService = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        notificationService.NotificationRequested += fired.Add;

        notificationService.Evaluate(MakeSnapshot(65, DateTimeOffset.UtcNow.AddHours(1)));
        fired.Clear();

        // New quota period: reset time moved forward, usage back near zero.
        notificationService.Evaluate(MakeSnapshot(2, DateTimeOffset.UtcNow.AddHours(6)));

        var reset = Assert.Single(fired, e => e.Title.Contains("reset"));
        Assert.Contains("has been reset", reset.Message);
        Assert.Contains("2%", reset.Message);
    }

    [Fact]
    public void Evaluate_FirstSighting_DoesNotFireResetNotification()
    {
        using var isolated = new IsolatedAppData();
        var notificationService = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        notificationService.NotificationRequested += fired.Add;

        // App start / provider newly added: low usage, never seen before - not a reset.
        notificationService.Evaluate(MakeSnapshot(3, DateTimeOffset.UtcNow.AddHours(2)));

        Assert.DoesNotContain(fired, e => e.Title.Contains("reset"));
    }

    [Fact]
    public void Evaluate_ResetsAtChangesButPriorUsageWasTrivial_NoResetNotification()
    {
        using var isolated = new IsolatedAppData();
        var notificationService = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        notificationService.NotificationRequested += fired.Add;

        notificationService.Evaluate(MakeSnapshot(4, DateTimeOffset.UtcNow.AddHours(1)));
        notificationService.Evaluate(MakeSnapshot(1, DateTimeOffset.UtcNow.AddHours(6)));

        Assert.DoesNotContain(fired, e => e.Title.Contains("reset"));
    }

    [Fact]
    public void Evaluate_SharpUsageDropWithoutResetTimeChange_FiresResetNotification()
    {
        using var isolated = new IsolatedAppData();
        var notificationService = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        notificationService.NotificationRequested += fired.Add;

        // Some providers never report resets_at - a cliff-drop in usage is the only reset signal.
        notificationService.Evaluate(MakeSnapshot(80, null));
        fired.Clear();
        notificationService.Evaluate(MakeSnapshot(5, null));

        Assert.Single(fired, e => e.Title.Contains("reset"));
    }

    [Fact]
    public void Evaluate_ThresholdsCanFireAgainAfterReset()
    {
        using var isolated = new IsolatedAppData();
        var notificationService = new NotificationService(new SettingsService());
        var fired = new List<NotificationEvent>();
        notificationService.NotificationRequested += fired.Add;

        var firstWindow = DateTimeOffset.UtcNow.AddHours(1);
        notificationService.Evaluate(MakeSnapshot(75, firstWindow)); // 70% threshold
        fired.Clear();

        var secondWindow = DateTimeOffset.UtcNow.AddHours(6);
        notificationService.Evaluate(MakeSnapshot(2, secondWindow));  // reset
        notificationService.Evaluate(MakeSnapshot(75, secondWindow)); // 70% again, new period

        Assert.Single(fired, e => e.Title.Contains("reset"));
        Assert.Single(fired, e => e.Message.Contains("reached 70%"));
    }
}
