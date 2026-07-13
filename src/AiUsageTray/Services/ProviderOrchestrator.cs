using System.Collections.Concurrent;
using AiUsageTray.Infrastructure;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

public sealed record ProviderState(
    IUsageProvider Provider,
    UsageSnapshot? LastSnapshot,
    ProviderDetectionResult? Detection,
    bool IsRefreshing,
    string? LastError,
    DateTimeOffset? LastErrorAt);

/// <summary>
/// Owns the set of registered providers, refreshes them concurrently (bounded), and keeps the
/// last-known-good snapshot for each so a single failing provider never blanks the UI or blocks
/// the others. This is the only place that knows about "all providers" as a collection - the
/// providers themselves never know about each other.
/// </summary>
public sealed class ProviderOrchestrator : IDisposable
{
    private const int MaxConcurrentRefreshes = 4;

    private readonly SettingsService _settingsService;
    private readonly ConcurrentDictionary<string, ProviderState> _states = new();
    private readonly SemaphoreSlim _refreshGate = new(MaxConcurrentRefreshes);
    private readonly List<IUsageProvider> _providers;

    public event Action<ProviderState>? StateChanged;

    public ProviderOrchestrator(SettingsService settingsService, IEnumerable<IUsageProvider> providers)
    {
        _settingsService = settingsService;
        _providers = providers.ToList();

        foreach (var provider in _providers)
        {
            _states[provider.Id] = new ProviderState(provider, null, null, false, null, null);
        }
    }

    public IReadOnlyList<ProviderState> States => _providers
        .Select(p => _states[p.Id])
        .ToList();

    public IEnumerable<IUsageProvider> EnabledProviders =>
        _providers.Where(p => _settingsService.Current.GetOrAddProvider(p.Id).Enabled);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var tasks = _providers.Select(async provider =>
        {
            try
            {
                var detection = await provider.DetectAsync(cancellationToken).ConfigureAwait(false);
                UpdateState(provider.Id, s => s with { Detection = detection });
            }
            catch (Exception ex)
            {
                AppLog.Error("Orchestrator", $"Detection failed for provider '{provider.Id}'", ex);
                UpdateState(provider.Id, s => s with { LastError = ex.Message, LastErrorAt = DateTimeOffset.UtcNow });
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var tasks = EnabledProviders.Select(provider => RefreshOneAsync(provider, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public Task RefreshAsync(string providerId, CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(p => p.Id == providerId);
        return provider is null ? Task.CompletedTask : RefreshOneAsync(provider, cancellationToken);
    }

    private async Task RefreshOneAsync(IUsageProvider provider, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        UpdateState(provider.Id, s => s with { IsRefreshing = true });

        try
        {
            var snapshot = await provider.RefreshAsync(cancellationToken).ConfigureAwait(false);
            UpdateState(provider.Id, s => s with
            {
                LastSnapshot = snapshot,
                IsRefreshing = false,
                LastError = null,
            });
        }
        catch (OperationCanceledException)
        {
            UpdateState(provider.Id, s => s with { IsRefreshing = false });
        }
        catch (Exception ex)
        {
            AppLog.Error("Orchestrator", $"Refresh failed for provider '{provider.Id}'", ex);
            UpdateState(provider.Id, s => s with
            {
                IsRefreshing = false,
                LastError = ex.Message,
                LastErrorAt = DateTimeOffset.UtcNow,
            });
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void UpdateState(string providerId, Func<ProviderState, ProviderState> mutate)
    {
        _states.AddOrUpdate(
            providerId,
            _ => throw new InvalidOperationException($"Unknown provider '{providerId}'"),
            (_, existing) => mutate(existing));

        StateChanged?.Invoke(_states[providerId]);
    }

    public void Dispose()
    {
        _refreshGate.Dispose();
        foreach (var provider in _providers.OfType<IDisposable>())
        {
            provider.Dispose();
        }
    }
}
