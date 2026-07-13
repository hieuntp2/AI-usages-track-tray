using System.IO;
using System.Text.Json;
using AiUsageTray.Infrastructure;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> from %LOCALAPPDATA%\AiUsageTray\config\settings.json,
/// tolerating a missing or corrupted file and applying schema migrations forward.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _sync = new();
    private AppSettings _current;

    public SettingsService()
    {
        _current = Load();
    }

    public AppSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            _current = settings;
            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                AtomicFile.WriteAllText(AppPaths.SettingsFile, json);
            }
            catch (Exception ex)
            {
                AppLog.Error("SettingsService", "Failed to save settings", ex);
            }
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_sync)
        {
            mutate(_current);
            Save(_current);
        }
    }

    private static AppSettings Load()
    {
        if (!AtomicFile.TryReadAllText(AppPaths.SettingsFile, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                return new AppSettings();
            }

            return Migrate(settings);
        }
        catch (JsonException ex)
        {
            AppLog.Warn("SettingsService", $"Corrupted settings.json, recovering with defaults: {ex.Message}");
            TryQuarantineCorruptFile();
            return new AppSettings();
        }
    }

    private static void TryQuarantineCorruptFile()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                AtomicFile.CreateTimestampedBackup(AppPaths.SettingsFile, AppPaths.BackupsDir);
            }
        }
        catch
        {
            // Best effort; falling back to defaults regardless.
        }
    }

    private static AppSettings Migrate(AppSettings settings)
    {
        // Schema version 0 (missing field, defaults to 0 on old files) -> 1: nothing structural
        // changed yet, this only stamps the version so future migrations have a known baseline.
        if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
        {
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        }

        return settings;
    }
}
