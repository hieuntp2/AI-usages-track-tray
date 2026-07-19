namespace AiUsageTray.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum TimeDisplayMode
{
    Relative,
    Exact,
    Both,
}

public sealed class NotificationThresholds
{
    public bool Enabled { get; set; } = true;
}

public sealed class ProviderSettings
{
    public string ProviderId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public NotificationThresholds Notifications { get; set; } = new();

    /// <summary>Provider-specific extra config (e.g. GitHub username, fine-grained token reference).</summary>
    public Dictionary<string, string> Extra { get; set; } = new();
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; } = true;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public int RefreshIntervalSeconds { get; set; } = 300;

    public TimeDisplayMode TimeDisplay { get; set; } = TimeDisplayMode.Relative;

    public double IconAlertThresholdPercent { get; set; } = 90;

    public bool IconAlertEnabled { get; set; } = true;

    public List<ProviderSettings> Providers { get; set; } = new()
    {
        new ProviderSettings { ProviderId = "codex", Enabled = true },
        new ProviderSettings { ProviderId = "claude", Enabled = true },
        new ProviderSettings { ProviderId = "github-copilot", Enabled = false },
    };

    public ProviderSettings GetOrAddProvider(string providerId)
    {
        var existing = Providers.FirstOrDefault(p => p.ProviderId == providerId);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ProviderSettings { ProviderId = providerId, Enabled = false };
        Providers.Add(created);
        return created;
    }
}
