using System.Text.Json.Serialization;

namespace AiUsageTray.Providers.Claude;

/// <summary>
/// Persisted at %LOCALAPPDATA%\AiUsageTray\config\claude-bridge.json. Records enough about the
/// user's original statusLine configuration to restore it exactly on "Remove integration", and
/// enough to re-apply our own bridge entry on "Repair integration" without re-deriving anything.
/// </summary>
public sealed class ClaudeBridgeMetadata
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("installed")]
    public bool Installed { get; set; }

    [JsonPropertyName("installedAt")]
    public DateTimeOffset InstalledAt { get; set; }

    /// <summary>True if a statusLine block existed in settings.json before we touched it.</summary>
    [JsonPropertyName("hadOriginalStatusLine")]
    public bool HadOriginalStatusLine { get; set; }

    /// <summary>The exact original "statusLine" JSON object, serialized as text, for byte-for-byte restore.</summary>
    [JsonPropertyName("originalStatusLineJson")]
    public string? OriginalStatusLineJson { get; set; }

    /// <summary>The original command string (when type == "command"), used by the bridge script to forward calls.</summary>
    [JsonPropertyName("originalCommand")]
    public string? OriginalCommand { get; set; }

    [JsonPropertyName("bridgeScriptPath")]
    public string BridgeScriptPath { get; set; } = string.Empty;
}
