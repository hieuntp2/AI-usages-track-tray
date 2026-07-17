using System.Collections.ObjectModel;
using AiUsageTray.Models;
using AiUsageTray.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsageTray.ViewModels;

/// <summary>One provider card in the flyout. Rebuilt from a <see cref="ProviderState"/> on every refresh.</summary>
public sealed partial class ProviderCardViewModel : ObservableObject
{
    public string ProviderId { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _statusText = "Unknown";

    [ObservableProperty]
    private string _statusLevel = "Normal"; // "Normal" | "Warn" | "Error" - drives the status dot color.

    [ObservableProperty]
    private string? _accountLabel;

    [ObservableProperty]
    private string? _planName;

    [ObservableProperty]
    private string? _sourceLabel;

    [ObservableProperty]
    private string? _lastUpdatedText;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _hasWindows;

    [ObservableProperty]
    private bool _hasMetrics;

    /// <summary>
    /// True when the provider actually has usage data to show. Unconnected providers (not
    /// installed, setup required, signed out, never refreshed) are hidden from the flyout
    /// entirely - their state remains visible in the tray menu and Settings → Providers.
    /// </summary>
    [ObservableProperty]
    private bool _isConnected;

    public ObservableCollection<UsageWindowRowViewModel> Windows { get; } = new();

    public ObservableCollection<string> Metrics { get; } = new();

    public ProviderCardViewModel(string providerId, string displayName)
    {
        ProviderId = providerId;
        _displayName = displayName;
    }

    public void UpdateFrom(ProviderState state, TimeDisplayMode timeDisplay)
    {
        IsRefreshing = state.IsRefreshing;

        var snapshot = state.LastSnapshot;
        if (snapshot is null)
        {
            IsConnected = false;

            var detection = state.Detection;
            if (detection is { IsInstalled: false })
            {
                ApplyStatus(ProviderConnectionStatus.NotInstalled, detection.Message);
            }
            else
            {
                StatusText = "Not yet refreshed";
                StatusLevel = "Normal";
            }

            return;
        }

        AccountLabel = snapshot.AccountLabel;
        PlanName = snapshot.PlanName;
        SourceLabel = snapshot.Source;
        Message = snapshot.Message;
        LastUpdatedText = FormatLastUpdated(snapshot.CapturedAt);

        ApplyStatus(snapshot.Status, snapshot.Message);

        Windows.Clear();
        foreach (var window in snapshot.Windows)
        {
            Windows.Add(new UsageWindowRowViewModel(window, timeDisplay));
        }
        HasWindows = Windows.Count > 0;

        Metrics.Clear();
        foreach (var metric in snapshot.Metrics)
        {
            Metrics.Add(FormatMetric(metric));
        }
        HasMetrics = Metrics.Count > 0;

        // "Connected" == there is something real to display. Empty snapshots (NotInstalled,
        // SetupRequired, NotAuthenticated, errors with no prior data) hide the card.
        IsConnected = HasWindows || HasMetrics;
    }

    private void ApplyStatus(ProviderConnectionStatus status, string? message)
    {
        (StatusText, StatusLevel) = status switch
        {
            ProviderConnectionStatus.Available => ("Connected", "Normal"),
            ProviderConnectionStatus.Refreshing => ("Refreshing…", "Normal"),
            ProviderConnectionStatus.NotInstalled => (message ?? "Not installed", "Warn"),
            ProviderConnectionStatus.NotAuthenticated => (message ?? "Not signed in", "Warn"),
            ProviderConnectionStatus.SetupRequired => (message ?? "Setup required", "Warn"),
            ProviderConnectionStatus.Stale => (message ?? "Data may be stale", "Warn"),
            ProviderConnectionStatus.UnsupportedVersion => (message ?? "Unsupported version", "Warn"),
            ProviderConnectionStatus.Error => (message ?? "Error", "Error"),
            _ => ("Unknown", "Normal"),
        };
    }

    private static string FormatMetric(UsageMetric metric) => metric.Unit switch
    {
        "usd" => $"{metric.DisplayName}: ${metric.Value:0.00}",
        "percent" => $"{metric.DisplayName}: {metric.Value:0}%",
        // A bare quantity ("2 count") reads like a serialization bug - drop the pseudo-unit.
        "count" => $"{metric.DisplayName}: {metric.Value:#,0}",
        _ => $"{metric.DisplayName}: {metric.Value:#,0.##} {metric.Unit}",
    };

    private static string FormatLastUpdated(DateTimeOffset capturedAt)
    {
        var age = DateTimeOffset.UtcNow - capturedAt;
        if (age < TimeSpan.FromSeconds(30))
        {
            return "Last updated just now";
        }

        if (age.TotalMinutes < 1)
        {
            return $"Last updated {(int)age.TotalSeconds} seconds ago";
        }

        if (age.TotalHours < 1)
        {
            var minutes = (int)age.TotalMinutes;
            return $"Last updated {minutes} minute{(minutes == 1 ? "" : "s")} ago";
        }

        var hours = (int)age.TotalHours;
        return $"Last updated {hours} hour{(hours == 1 ? "" : "s")} ago";
    }
}
