using System.Net.Http;
using System.Net.Http.Headers;
using AiUsageTray.Infrastructure;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.GitHubCopilot;

/// <summary>
/// Beta/scaffold provider for GitHub Copilot, driven by GitHub's official REST billing endpoints
/// (never Copilot CLI text scraping). Disabled by default (see <see cref="AppSettings"/>) - the
/// HTTP/parsing path is implemented and unit-testable, but has not been exercised against a real
/// GitHub billing account, since GitHub's billing usage response shape may still change. Do not
/// enable by default until that has been verified against a live account.
/// </summary>
public sealed class GitHubCopilotUsageProvider : IUsageProvider, IDisposable
{
    private const string TokenSecretKey = "github-copilot-token";
    private static readonly Uri ApiBase = new("https://api.github.com/");

    public string Id => "github-copilot";

    public string DisplayName => "GitHub Copilot";

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsActiveRefresh: true,
        SupportsPercentageWindows: false,
        SupportsMonetaryCost: false,
        SupportsRequestCounts: true,
        SupportsTokenCounts: false,
        RequiresSetup: true,
        RequiresNetwork: true);

    private readonly HttpClient _httpClient;
    private string? _configuredUsername;

    public GitHubCopilotUsageProvider(string? username = null, HttpClient? httpClient = null)
    {
        _configuredUsername = username;
        _httpClient = httpClient ?? new HttpClient { BaseAddress = ApiBase, Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>Updates the configured username in place - the orchestrator holds a single long-lived
    /// provider instance, so credentials entered later in Settings must be applied without recreating it.</summary>
    public void SetUsername(string? username) => _configuredUsername = username;

    public Task<ProviderDetectionResult> DetectAsync(CancellationToken cancellationToken)
    {
        var hasToken = SecretStore.Exists(TokenSecretKey);
        var hasUsername = !string.IsNullOrWhiteSpace(_configuredUsername);

        return Task.FromResult(hasToken && hasUsername
            ? new ProviderDetectionResult(true, null, null, true, null)
            : new ProviderDetectionResult(true, null, null, true, "GitHub Copilot requires a fine-grained token and username in Settings."));
    }

    public Task<ProviderSetupResult> SetupAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderSetupResult(false, "Configure the GitHub username and fine-grained token (Plan: read permission) in Settings > Providers > GitHub Copilot."));

    /// <summary>Called from the Settings/Add-provider UI once the user supplies credentials.</summary>
    public void Configure(string username, string fineGrainedToken)
    {
        SetUsername(username);
        SecretStore.Save(TokenSecretKey, fineGrainedToken);
    }

    /// <summary>
    /// Saves credentials and immediately validates the token against GitHub's `/user` endpoint, so
    /// the Add-provider UI can show a real "authenticated" or "token rejected" result right away
    /// instead of waiting for the next scheduled refresh.
    /// </summary>
    public async Task<ProviderSetupResult> ConfigureAndAuthenticateAsync(string username, string fineGrainedToken, CancellationToken cancellationToken)
    {
        Configure(username, fineGrainedToken);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fineGrainedToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AiUsageTray", "0.1"));

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ProviderSetupResult(false, $"GitHub rejected the token (HTTP {(int)response.StatusCode}). Check the token and its \"Plan: read\" permission.");
            }

            return new ProviderSetupResult(true, "GitHub Copilot authenticated successfully.");
        }
        catch (HttpRequestException ex)
        {
            return new ProviderSetupResult(false, $"Could not reach GitHub: {ex.Message}");
        }
    }

    public void RemoveCredentials() => SecretStore.Delete(TokenSecretKey);

    public async Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_configuredUsername))
        {
            return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.SetupRequired, "github-billing-api", "GitHub username not configured.");
        }

        var token = SecretStore.Load(TokenSecretKey);
        if (string.IsNullOrEmpty(token))
        {
            return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.NotAuthenticated, "github-billing-api", "GitHub token not configured.");
        }

        var now = DateTimeOffset.UtcNow;
        var metrics = new List<UsageMetric>();
        string? message = null;

        try
        {
            var creditMetric = await TryFetchAsync($"users/{_configuredUsername}/settings/billing/ai_credit/usage",
                token, "ai_credits_used", "AI credits used", "credits", now, cancellationToken).ConfigureAwait(false);

            var premiumRequestMetric = await TryFetchAsync($"users/{_configuredUsername}/settings/billing/premium_request/usage",
                token, "premium_requests_used", "Premium requests used", "requests", now, cancellationToken).ConfigureAwait(false);

            if (creditMetric is not null)
            {
                metrics.Add(creditMetric);
            }

            if (premiumRequestMetric is not null)
            {
                metrics.Add(premiumRequestMetric);
            }

            if (metrics.Count == 0)
            {
                message = "No Copilot usage reported for the current billing period, or plan allowance is unavailable.";
            }
        }
        catch (HttpRequestException ex)
        {
            return UsageSnapshot.Empty(Id, DisplayName, ProviderConnectionStatus.Error, "github-billing-api", $"GitHub API request failed: {ex.Message}");
        }

        return new UsageSnapshot(
            Id, DisplayName, _configuredUsername, null,
            ProviderConnectionStatus.Available, now, "github-billing-api",
            Array.Empty<UsageWindow>(), metrics,
            message ?? "Exact plan allowance and remaining balance are not exposed by GitHub's billing API.");
    }

    private async Task<UsageMetric?> TryFetchAsync(string relativeUrl, string token, string metricId, string displayName, string unit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AiUsageTray", "0.1"));

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Debug("GitHubCopilot.Provider", $"{relativeUrl} returned {(int)response.StatusCode}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = GitHubCopilotUsageParser.Parse(json);
        return GitHubCopilotUsageParser.AggregateCurrentMonth(parsed, metricId, displayName, unit, now);
    }

    public void Dispose() => _httpClient.Dispose();
}
