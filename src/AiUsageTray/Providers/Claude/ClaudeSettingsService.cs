using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiUsageTray.Infrastructure;

namespace AiUsageTray.Providers.Claude;

/// <summary>
/// Reads/writes %USERPROFILE%\.claude\settings.json as a loosely-typed <see cref="JsonObject"/>
/// tree so every property this app doesn't understand (padding, refreshInterval, unrelated
/// top-level keys, ...) round-trips untouched. Never re-serializes through a strongly-typed model,
/// which would silently drop unknown fields.
/// </summary>
public sealed class ClaudeSettingsService
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string SettingsPath { get; }

    public ClaudeSettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? AppPaths.ClaudeSettingsFile;
    }

    public bool Exists => File.Exists(SettingsPath);

    /// <summary>Returns the parsed settings object, or an empty object if the file doesn't exist yet.</summary>
    public JsonObject Read()
    {
        if (!File.Exists(SettingsPath))
        {
            return new JsonObject();
        }

        if (!AtomicFile.TryReadAllText(SettingsPath, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return node as JsonObject ?? new JsonObject();
    }

    /// <summary>Throws <see cref="JsonException"/> if the on-disk file is not valid JSON.</summary>
    public JsonObject ReadStrict()
    {
        if (!File.Exists(SettingsPath))
        {
            return new JsonObject();
        }

        if (!AtomicFile.TryReadAllText(SettingsPath, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        var node = JsonNode.Parse(text);
        return node as JsonObject ?? throw new JsonException("settings.json root is not an object.");
    }

    public void BackupIfExists()
    {
        if (File.Exists(SettingsPath))
        {
            AtomicFile.CreateTimestampedBackup(SettingsPath, AppPaths.BackupsDir);
        }
    }

    public void Write(JsonObject settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);

        var json = settings.ToJsonString(WriteOptions);
        AtomicFile.WriteAllText(SettingsPath, json);
    }

    public static JsonObject? GetStatusLine(JsonObject settings) =>
        settings.TryGetPropertyValue("statusLine", out var node) ? node as JsonObject : null;
}
