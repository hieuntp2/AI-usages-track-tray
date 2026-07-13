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
}
