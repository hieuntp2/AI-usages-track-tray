using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using AiUsageTray.Infrastructure;
using AiUsageTray.Models;
using AiUsageTray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace AiUsageTray.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly ProviderOrchestrator _orchestrator;
    private readonly DiagnosticsService _diagnosticsService;
    private readonly Dispatcher _dispatcher;
    private readonly Action<ProviderState> _onOrchestratorStateChanged;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private int _refreshIntervalSeconds;

    [ObservableProperty]
    private TimeDisplayMode _timeDisplay;

    [ObservableProperty]
    private bool _iconAlertEnabled;

    [ObservableProperty]
    private double _iconAlertThresholdPercent;

    [ObservableProperty]
    private string _diagnosticsSummary = string.Empty;

    public ObservableCollection<ProviderSettingsRowViewModel> Providers { get; } = new();

    public IEnumerable<AppTheme> ThemeOptions { get; } = Enum.GetValues<AppTheme>();

    public IEnumerable<TimeDisplayMode> TimeDisplayOptions { get; } = Enum.GetValues<TimeDisplayMode>();

    public IRelayCommand OpenLogFolderCommand { get; }

    public IRelayCommand ExportDiagnosticsCommand { get; }

    public IRelayCommand RefreshDiagnosticsCommand { get; }

    public SettingsViewModel(SettingsService settingsService, ProviderOrchestrator orchestrator, DiagnosticsService diagnosticsService)
    {
        _settingsService = settingsService;
        _orchestrator = orchestrator;
        _diagnosticsService = diagnosticsService;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var settings = settingsService.Current;
        _startWithWindows = settings.StartWithWindows;
        _startMinimized = settings.StartMinimized;
        _theme = settings.Theme;
        _refreshIntervalSeconds = settings.RefreshIntervalSeconds;
        _timeDisplay = settings.TimeDisplay;
        _iconAlertEnabled = settings.IconAlertEnabled;
        _iconAlertThresholdPercent = settings.IconAlertThresholdPercent;

        foreach (var state in orchestrator.States)
        {
            var row = new ProviderSettingsRowViewModel(state.Provider, settingsService);
            row.ApplyDetection(state.Detection);
            row.ApplySnapshot(state.LastSnapshot);
            row.Authenticated += () => _ = _orchestrator.RefreshAsync(row.ProviderId, CancellationToken.None);
            Providers.Add(row);
        }

        _onOrchestratorStateChanged = state => _dispatcher.BeginInvoke(() =>
        {
            var row = Providers.FirstOrDefault(p => p.ProviderId == state.Provider.Id);
            row?.ApplyDetection(state.Detection);
            row?.ApplySnapshot(state.LastSnapshot);
        });
        orchestrator.StateChanged += _onOrchestratorStateChanged;

        OpenLogFolderCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(AppPaths.LogsDir) { UseShellExecute = true }));
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
        RefreshDiagnosticsCommand = new RelayCommand(RefreshDiagnosticsSummary);

        RefreshDiagnosticsSummary();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        StartupRegistration.SetEnabled(value);
        Persist();
    }

    partial void OnStartMinimizedChanged(bool value) => Persist();

    partial void OnThemeChanged(AppTheme value) => Persist();

    partial void OnRefreshIntervalSecondsChanged(int value) => Persist();

    partial void OnTimeDisplayChanged(TimeDisplayMode value) => Persist();

    partial void OnIconAlertEnabledChanged(bool value) => Persist();

    partial void OnIconAlertThresholdPercentChanged(double value) => Persist();

    private void Persist()
    {
        _settingsService.Update(s =>
        {
            s.StartWithWindows = StartWithWindows;
            s.StartMinimized = StartMinimized;
            s.Theme = Theme;
            s.RefreshIntervalSeconds = RefreshIntervalSeconds;
            s.TimeDisplay = TimeDisplay;
            s.IconAlertEnabled = IconAlertEnabled;
            s.IconAlertThresholdPercent = IconAlertThresholdPercent;
        });
    }

    private void RefreshDiagnosticsSummary()
    {
        var snapshot = _diagnosticsService.BuildSnapshot();
        DiagnosticsSummary =
            $"App version: {snapshot.AppVersion}\n" +
            $"Windows: {snapshot.WindowsVersion}\n" +
            $"Last successful refresh: {(snapshot.LastSuccessfulRefresh is { } t ? t.ToLocalTime().ToString("g") : "never")}\n" +
            $"Last error: {snapshot.LastError ?? "none"}";
    }

    private void ExportDiagnostics()
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"aiusagetray-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON files (*.json)|*.json",
            InitialDirectory = AppPaths.DataDir,
        };

        if (dialog.ShowDialog() == true)
        {
            _diagnosticsService.ExportSanitized(dialog.FileName);
        }
    }

    public void Dispose() => _orchestrator.StateChanged -= _onOrchestratorStateChanged;
}
