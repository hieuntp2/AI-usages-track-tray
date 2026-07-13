using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using AiUsageTray.Infrastructure;
using AiUsageTray.Models;
using AiUsageTray.Providers.Claude;
using AiUsageTray.Providers.Codex;
using AiUsageTray.Providers.GitHubCopilot;
using AiUsageTray.Services;
using AiUsageTray.ViewModels;
using AiUsageTray.Views;

namespace AiUsageTray;

public partial class App : Application
{
    private static readonly TimeSpan CodexOpenStaleThreshold = TimeSpan.FromSeconds(60);

    private SingleInstance? _singleInstance;
    private SettingsService? _settingsService;
    private ProviderOrchestrator? _orchestrator;
    private NotificationService? _notificationService;
    private DiagnosticsService? _diagnosticsService;
    private TrayIconService? _trayIconService;
    private FlyoutWindow? _flyoutWindow;
    private FlyoutViewModel? _flyoutViewModel;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsViewModel;
    private DispatcherTimer? _backgroundRefreshTimer;
    private CancellationTokenSource? _lifetimeCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new SingleInstance();
        if (!_singleInstance.TryAcquire())
        {
            SingleInstance.TrySignalPrimaryInstance(TimeSpan.FromSeconds(2));
            Shutdown();
            return;
        }

        _singleInstance.ActivationRequested += () => Dispatcher.Invoke(ShowFlyout);
        _singleInstance.StartListening();

        _lifetimeCts = new CancellationTokenSource();

        _settingsService = new SettingsService();
        AppLog.DebugLoggingEnabled = false;

        ApplyTheme(_settingsService.Current.Theme);
        ThemeDetector.EnsureSubscribed();
        ThemeDetector.SystemThemeChanged += () => Dispatcher.Invoke(() =>
        {
            if (_settingsService!.Current.Theme == AppTheme.System)
            {
                ApplyTheme(AppTheme.System);
            }
        });

        var providers = BuildProviders(_settingsService);
        _orchestrator = new ProviderOrchestrator(_settingsService, providers);
        _notificationService = new NotificationService(_settingsService);
        _diagnosticsService = new DiagnosticsService(_orchestrator);

        _notificationService.NotificationRequested += evt => _trayIconService?.ShowBalloon(evt.Title, evt.Message);

        _orchestrator.StateChanged += state =>
        {
            if (state.LastSnapshot is { } snapshot)
            {
                _notificationService.Evaluate(snapshot);
            }

            Dispatcher.BeginInvoke(() =>
            {
                _trayIconService?.UpdateTooltip(_orchestrator.States, _settingsService.Current.IconAlertThresholdPercent, _settingsService.Current.IconAlertEnabled);
                _trayIconService?.UpdateProviderMenu(_orchestrator.States, _settingsService.Current);
            });
        };

        if (providers.OfType<ClaudeUsageProvider>().FirstOrDefault() is { } claudeProvider)
        {
            claudeProvider.CacheUpdated += () => _ = _orchestrator.RefreshAsync("claude", _lifetimeCts.Token);
        }

        _flyoutViewModel = new FlyoutViewModel(_orchestrator, _settingsService);
        _flyoutViewModel.SettingsRequested += () => ShowSettings();

        _trayIconService = new TrayIconService(_settingsService.Current.StartWithWindows);
        _trayIconService.OpenRequested += ShowFlyout;
        _trayIconService.RefreshRequested += () => _ = _orchestrator.RefreshAllAsync(_lifetimeCts.Token);
        _trayIconService.SettingsRequested += () => ShowSettings();
        _trayIconService.AboutRequested += ShowAbout;
        _trayIconService.ExitRequested += Shutdown;
        _trayIconService.StartWithWindowsToggled += value =>
        {
            StartupRegistration.SetEnabled(value);
            _settingsService.Update(s => s.StartWithWindows = value);
        };
        _trayIconService.AddProviderRequested += () => ShowSettings(initialTab: 1);
        _trayIconService.ProviderRowClicked += _ => ShowSettings(initialTab: 1);
        _trayIconService.UpdateProviderMenu(_orchestrator.States, _settingsService.Current);

        _backgroundRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(60, _settingsService.Current.RefreshIntervalSeconds)),
        };
        _backgroundRefreshTimer.Tick += (_, _) => _ = _orchestrator.RefreshAllAsync(_lifetimeCts.Token);
        _backgroundRefreshTimer.Start();

        _ = InitializeAndRefreshAsync();

        if (!_settingsService.Current.StartMinimized)
        {
            ShowFlyout();
        }
    }

    private async Task InitializeAndRefreshAsync()
    {
        await _orchestrator!.InitializeAsync(_lifetimeCts!.Token).ConfigureAwait(true);
        await _orchestrator.RefreshAllAsync(_lifetimeCts.Token).ConfigureAwait(true);
    }

    private static List<IUsageProvider> BuildProviders(SettingsService settingsService)
    {
        var githubUsername = settingsService.Current.GetOrAddProvider("github-copilot").Extra.GetValueOrDefault("username");

        return new List<IUsageProvider>
        {
            new CodexUsageProvider(),
            new ClaudeUsageProvider(),
            new GitHubCopilotUsageProvider(githubUsername),
        };
    }

    private void ShowFlyout()
    {
        _flyoutWindow ??= new FlyoutWindow(_flyoutViewModel!);

        if (_flyoutWindow.IsVisible)
        {
            _flyoutWindow.Hide();
            return;
        }

        if (_orchestrator!.States.FirstOrDefault(s => s.Provider.Id == "codex") is { LastSnapshot.CapturedAt: var capturedAt } &&
            DateTimeOffset.UtcNow - capturedAt > CodexOpenStaleThreshold)
        {
            _ = _orchestrator.RefreshAsync("codex", _lifetimeCts!.Token);
        }

        _flyoutWindow.ShowNearTray();
    }

    private void ShowSettings(int initialTab = 0)
    {
        _flyoutWindow?.Hide();

        if (_settingsWindow is null)
        {
            _settingsViewModel = new SettingsViewModel(_settingsService!, _orchestrator!, _diagnosticsService!);
            _settingsWindow = new SettingsWindow(_settingsViewModel);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsViewModel!.Dispose();
                _settingsViewModel = null;
                _settingsWindow = null;
            };
        }

        _settingsWindow.SelectTab(initialTab);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShowAbout()
    {
        var window = new AboutWindow();
        window.Show();
        window.Activate();
    }

    private void ApplyTheme(AppTheme theme)
    {
        var useDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => ThemeDetector.IsSystemDarkTheme(),
        };

        var dictionaries = Resources.MergedDictionaries;
        var themeDict = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Light.xaml") == true
                                                          || d.Source?.OriginalString.Contains("Themes/Dark.xaml") == true);
        var newSource = new Uri(useDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);

        if (themeDict is not null)
        {
            var index = dictionaries.IndexOf(themeDict);
            dictionaries[index] = new ResourceDictionary { Source = newSource };
        }
        else
        {
            dictionaries.Insert(0, new ResourceDictionary { Source = newSource });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _backgroundRefreshTimer?.Stop();
        _lifetimeCts?.Cancel();
        _flyoutViewModel?.Dispose();
        _trayIconService?.Dispose();
        _orchestrator?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
