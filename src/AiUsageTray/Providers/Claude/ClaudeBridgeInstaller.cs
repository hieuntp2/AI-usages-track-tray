using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiUsageTray.Infrastructure;
using AiUsageTray.Models;

namespace AiUsageTray.Providers.Claude;

public enum ClaudeBridgeStatus
{
    NotInstalled,
    Installed,
    Damaged,
}

/// <summary>
/// Installs/repairs/removes the Claude Code status-line bridge. Everything here is idempotent:
/// running Install twice, or Install-then-Repair, must never wrap the bridge around itself or
/// lose the user's original statusLine configuration.
/// </summary>
public sealed class ClaudeBridgeInstaller
{
    private const string EmbeddedResourceName = "AiUsageTray.Providers.Claude.Bridge.claude-statusline-bridge.ps1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ClaudeSettingsService _settingsService;

    public ClaudeBridgeInstaller(ClaudeSettingsService? settingsService = null)
    {
        _settingsService = settingsService ?? new ClaudeSettingsService();
    }

    public string BuildBridgeCommand() =>
        $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{AppPaths.ClaudeBridgeScriptFile.Replace('\\', '/')}\"";

    public ClaudeBridgeStatus GetStatus()
    {
        var metadata = ReadMetadata();
        if (metadata is not { Installed: true })
        {
            return ClaudeBridgeStatus.NotInstalled;
        }

        JsonObject settings;
        try
        {
            settings = _settingsService.Read();
        }
        catch
        {
            return ClaudeBridgeStatus.Damaged;
        }

        var statusLine = ClaudeSettingsService.GetStatusLine(settings);
        var currentCommand = statusLine?["command"]?.GetValue<string>();

        return IsOurCommand(currentCommand) ? ClaudeBridgeStatus.Installed : ClaudeBridgeStatus.Damaged;
    }

    public bool IsOurCommand(string? command) =>
        !string.IsNullOrEmpty(command) &&
        command.Replace('\\', '/').Contains(AppPaths.ClaudeBridgeScriptFile.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public ProviderSetupResult Install(CancellationToken cancellationToken = default)
    {
        try
        {
            WriteBridgeScript();

            JsonObject settings;
            try
            {
                settings = _settingsService.ReadStrict();
            }
            catch (JsonException ex)
            {
                return new ProviderSetupResult(false, $"Existing Claude settings.json is not valid JSON: {ex.Message}. No changes were made.");
            }

            var existingStatusLine = ClaudeSettingsService.GetStatusLine(settings);
            var existingCommand = existingStatusLine?["command"]?.GetValue<string>();

            if (IsOurCommand(existingCommand) && ReadMetadata() is { Installed: true })
            {
                return new ProviderSetupResult(true, "Claude integration is already installed.");
            }

            _settingsService.BackupIfExists();

            var metadata = new ClaudeBridgeMetadata
            {
                Installed = true,
                InstalledAt = DateTimeOffset.UtcNow,
                HadOriginalStatusLine = existingStatusLine is not null,
                OriginalStatusLineJson = existingStatusLine?.ToJsonString(),
                OriginalCommand = existingStatusLine?["type"]?.GetValue<string>() == "command" ? existingCommand : null,
                BridgeScriptPath = AppPaths.ClaudeBridgeScriptFile,
            };
            WriteMetadata(metadata);

            ApplyBridgeStatusLine(settings);
            _settingsService.Write(settings);

            return new ProviderSetupResult(true, "Claude integration installed. Open Claude Code and send a normal prompt once so Claude can provide the first usage snapshot.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Claude.BridgeInstaller", "Install failed", ex);
            return new ProviderSetupResult(false, $"Install failed: {ex.Message}");
        }
    }

    public ProviderSetupResult Repair()
    {
        try
        {
            WriteBridgeScript();

            var metadata = ReadMetadata();
            if (metadata is null)
            {
                // No prior install recorded - treat Repair as a fresh Install so this is always safe to call.
                return Install();
            }

            JsonObject settings;
            try
            {
                settings = _settingsService.ReadStrict();
            }
            catch (JsonException ex)
            {
                return new ProviderSetupResult(false, $"Existing Claude settings.json is not valid JSON: {ex.Message}. No changes were made.");
            }

            _settingsService.BackupIfExists();
            ApplyBridgeStatusLine(settings);
            _settingsService.Write(settings);

            metadata.Installed = true;
            WriteMetadata(metadata);

            return new ProviderSetupResult(true, "Claude integration repaired.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Claude.BridgeInstaller", "Repair failed", ex);
            return new ProviderSetupResult(false, $"Repair failed: {ex.Message}");
        }
    }

    public ProviderSetupResult Remove()
    {
        try
        {
            var metadata = ReadMetadata();

            JsonObject settings;
            try
            {
                settings = _settingsService.ReadStrict();
            }
            catch (JsonException ex)
            {
                return new ProviderSetupResult(false, $"Existing Claude settings.json is not valid JSON: {ex.Message}. No changes were made.");
            }

            _settingsService.BackupIfExists();

            if (metadata is { HadOriginalStatusLine: true, OriginalStatusLineJson: { } originalJson })
            {
                settings["statusLine"] = JsonNode.Parse(originalJson);
            }
            else
            {
                settings.Remove("statusLine");
            }

            _settingsService.Write(settings);

            if (metadata is not null)
            {
                metadata.Installed = false;
                WriteMetadata(metadata);
            }

            return new ProviderSetupResult(true, "Claude integration removed and original configuration restored.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Claude.BridgeInstaller", "Remove failed", ex);
            return new ProviderSetupResult(false, $"Remove failed: {ex.Message}");
        }
    }

    private void ApplyBridgeStatusLine(JsonObject settings)
    {
        settings["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = BuildBridgeCommand(),
        };
    }

    private static void WriteBridgeScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded bridge script resource '{EmbeddedResourceName}' not found.");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        AtomicFile.WriteAllText(AppPaths.ClaudeBridgeScriptFile, content);
    }

    public static ClaudeBridgeMetadata? ReadMetadata()
    {
        if (!AtomicFile.TryReadAllText(AppPaths.ClaudeBridgeMetadataFile, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ClaudeBridgeMetadata>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteMetadata(ClaudeBridgeMetadata metadata)
    {
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        AtomicFile.WriteAllText(AppPaths.ClaudeBridgeMetadataFile, json);
    }
}
