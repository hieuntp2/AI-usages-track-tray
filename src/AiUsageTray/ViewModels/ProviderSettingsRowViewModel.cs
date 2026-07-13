using AiUsageTray.Models;
using AiUsageTray.Providers.Claude;
using AiUsageTray.Providers.GitHubCopilot;
using AiUsageTray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiUsageTray.ViewModels;

public sealed partial class ProviderSettingsRowViewModel : ObservableObject
{
    private readonly IUsageProvider _provider;
    private readonly SettingsService _settingsService;

    public string ProviderId => _provider.Id;

    public string DisplayName => _provider.DisplayName;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _detectionStatus = "Checking…";

    [ObservableProperty]
    private string? _executablePath;

    [ObservableProperty]
    private string? _version;

    [ObservableProperty]
    private string _dataSource;

    [ObservableProperty]
    private string? _lastMessage;

    /// <summary>Live connection/auth status derived from the provider's last refresh - this is
    /// what surfaces an authentication error inline on this row, not just after clicking a button.</summary>
    [ObservableProperty]
    private string _connectionStatusText = "Not yet checked";

    [ObservableProperty]
    private bool _connectionHasError;

    [ObservableProperty]
    private string? _gitHubUsername;

    public bool SupportsClaudeSetup => _provider is ClaudeUsageProvider;

    public bool IsGitHubCopilot => _provider is GitHubCopilotUsageProvider;

    public IAsyncRelayCommand SetupCommand { get; }

    public IAsyncRelayCommand RepairCommand { get; }

    public IAsyncRelayCommand RemoveCommand { get; }

    /// <summary>Raised after credentials are saved and successfully authenticated, so the owning
    /// view model can trigger an immediate refresh instead of waiting for the next scheduled one.</summary>
    public event Action? Authenticated;

    public ProviderSettingsRowViewModel(IUsageProvider provider, SettingsService settingsService)
    {
        _provider = provider;
        _settingsService = settingsService;
        _dataSource = provider.Capabilities.RequiresNetwork ? "Network API" : provider.Id == "claude" ? "Status-line cache" : "Local process";
        var providerSettings = settingsService.Current.GetOrAddProvider(provider.Id);
        _enabled = providerSettings.Enabled;
        _gitHubUsername = providerSettings.Extra.GetValueOrDefault("username");

        SetupCommand = new AsyncRelayCommand(async () =>
        {
            var result = await _provider.SetupAsync(CancellationToken.None).ConfigureAwait(true);
            LastMessage = result.Message;
        });

        RepairCommand = new AsyncRelayCommand(async () =>
        {
            if (_provider is ClaudeUsageProvider claude)
            {
                var result = await claude.RepairAsync().ConfigureAwait(true);
                LastMessage = result.Message;
            }
        });

        RemoveCommand = new AsyncRelayCommand(async () =>
        {
            if (_provider is ClaudeUsageProvider claude)
            {
                var result = await claude.RemoveIntegrationAsync().ConfigureAwait(true);
                LastMessage = result.Message;
            }
        });
    }

    partial void OnEnabledChanged(bool value)
    {
        _settingsService.Update(s => s.GetOrAddProvider(ProviderId).Enabled = value);
    }

    partial void OnGitHubUsernameChanged(string? value)
    {
        if (_provider is GitHubCopilotUsageProvider gitHub)
        {
            gitHub.SetUsername(value);
        }

        _settingsService.Update(s => s.GetOrAddProvider(ProviderId).Extra["username"] = value ?? string.Empty);
    }

    /// <summary>Saves and validates GitHub Copilot credentials, enabling the provider on success.</summary>
    public async Task SaveGitHubCredentialsAsync(string token)
    {
        if (_provider is not GitHubCopilotUsageProvider gitHub)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(GitHubUsername) || string.IsNullOrWhiteSpace(token))
        {
            LastMessage = "Enter both a GitHub username and a fine-grained token.";
            return;
        }

        var result = await gitHub.ConfigureAndAuthenticateAsync(GitHubUsername, token, CancellationToken.None).ConfigureAwait(true);
        LastMessage = result.Message;
        ConnectionStatusText = result.Success ? "Connected" : $"Error: {result.Message}";
        ConnectionHasError = !result.Success;

        if (result.Success)
        {
            Enabled = true;
            Authenticated?.Invoke();
        }
    }

    public void ApplyDetection(ProviderDetectionResult? detection)
    {
        if (detection is null)
        {
            DetectionStatus = "Checking…";
            return;
        }

        ExecutablePath = detection.ExecutablePath;
        Version = detection.Version;
        DetectionStatus = detection switch
        {
            { IsInstalled: false } => detection.Message ?? "Not installed",
            { IsSupportedVersion: false } => detection.Message ?? "Unsupported version",
            _ => "Detected",
        };
    }

    /// <summary>Reflects the provider's last refresh outcome onto this row, so an authentication
    /// error is visible here immediately - including the moment Settings is first opened.</summary>
    public void ApplySnapshot(UsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        (ConnectionStatusText, ConnectionHasError) = snapshot.Status switch
        {
            ProviderConnectionStatus.Available => ("Connected", false),
            ProviderConnectionStatus.Refreshing => ("Refreshing…", false),
            ProviderConnectionStatus.NotAuthenticated => ($"Not authenticated: {snapshot.Message}", true),
            ProviderConnectionStatus.SetupRequired => (snapshot.Message ?? "Setup required", true),
            ProviderConnectionStatus.Stale => ("Stale data", true),
            ProviderConnectionStatus.UnsupportedVersion => (snapshot.Message ?? "Unsupported version", true),
            ProviderConnectionStatus.NotInstalled => (snapshot.Message ?? "Not installed", true),
            ProviderConnectionStatus.Error => ($"Error: {snapshot.Message}", true),
            _ => ("Not yet checked", false),
        };
    }
}
