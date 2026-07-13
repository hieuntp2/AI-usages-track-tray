using System.IO;
using System.Reflection;
using System.Text.Json;
using AiUsageTray.Infrastructure;

namespace AiUsageTray.Services;

public sealed record DiagnosticsSnapshot(
    string AppVersion,
    string WindowsVersion,
    IReadOnlyList<ProviderDiagnostic> Providers,
    DateTimeOffset? LastSuccessfulRefresh,
    string? LastError);

public sealed record ProviderDiagnostic(string ProviderId, string? Version, string? Status, string? LastError);

/// <summary>
/// Builds diagnostics for the Settings > Diagnostics tab and for sanitized export. The export path
/// never includes tokens, credentials, prompt content, or (where avoidable) the full user profile
/// path - only what's needed to triage a provider/refresh issue.
/// </summary>
public sealed class DiagnosticsService
{
    private readonly ProviderOrchestrator _orchestrator;

    public DiagnosticsService(ProviderOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public DiagnosticsSnapshot BuildSnapshot()
    {
        var states = _orchestrator.States;
        var providerDiagnostics = states.Select(s => new ProviderDiagnostic(
            s.Provider.Id,
            s.Detection?.Version,
            s.LastSnapshot?.Status.ToString(),
            s.LastError)).ToList();

        return new DiagnosticsSnapshot(
            AppVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            WindowsVersion: Environment.OSVersion.VersionString,
            Providers: providerDiagnostics,
            LastSuccessfulRefresh: states.Where(s => s.LastSnapshot is not null).Select(s => s.LastSnapshot!.CapturedAt).DefaultIfEmpty().Max(),
            LastError: states.OrderByDescending(s => s.LastErrorAt).FirstOrDefault(s => s.LastError is not null)?.LastError);
    }

    public void ExportSanitized(string destinationPath)
    {
        var snapshot = BuildSnapshot();
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(destinationPath, Sanitize(json));
    }

    private static string Sanitize(string json)
    {
        var redacted = AppLog.Redact(json);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(userProfile) ? redacted : redacted.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }
}
