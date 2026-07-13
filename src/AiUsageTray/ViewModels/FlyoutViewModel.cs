using System.Collections.ObjectModel;
using System.Windows.Threading;
using AiUsageTray.Models;
using AiUsageTray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiUsageTray.ViewModels;

public sealed partial class FlyoutViewModel : ObservableObject, IDisposable
{
    private readonly ProviderOrchestrator _orchestrator;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _clockTimer;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private string _lastGlobalRefreshText = "Never refreshed";

    [ObservableProperty]
    private bool _isRefreshing;

    public ObservableCollection<ProviderCardViewModel> Providers { get; } = new();

    public IAsyncRelayCommand RefreshCommand { get; }

    public IRelayCommand OpenSettingsCommand { get; }

    public event Action? SettingsRequested;

    public FlyoutViewModel(ProviderOrchestrator orchestrator, SettingsService settingsService)
    {
        _orchestrator = orchestrator;
        _settingsService = settingsService;
        _dispatcher = Dispatcher.CurrentDispatcher;

        foreach (var state in orchestrator.States)
        {
            var card = new ProviderCardViewModel(state.Provider.Id, state.Provider.DisplayName);
            card.UpdateFrom(state, _settingsService.Current.TimeDisplay);
            Providers.Add(card);
        }

        _orchestrator.StateChanged += OnStateChanged;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) => RefreshTimeLabels();
        _clockTimer.Start();
    }

    private void OnStateChanged(ProviderState state)
    {
        _dispatcher.Invoke(() =>
        {
            var card = Providers.FirstOrDefault(c => c.ProviderId == state.Provider.Id);
            card?.UpdateFrom(state, _settingsService.Current.TimeDisplay);
        });
    }

    private void RefreshTimeLabels()
    {
        foreach (var (card, state) in Providers.Zip(_orchestrator.States))
        {
            card.UpdateFrom(state, _settingsService.Current.TimeDisplay);
        }
    }

    public async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            await _orchestrator.RefreshAllAsync(CancellationToken.None).ConfigureAwait(true);
            LastGlobalRefreshText = $"Last refreshed at {DateTimeOffset.Now:HH:mm:ss}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        _orchestrator.StateChanged -= OnStateChanged;
    }
}
