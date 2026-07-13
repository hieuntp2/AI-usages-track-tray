using System.IO;
namespace AiUsageTray.Infrastructure;

/// <summary>Centralizes every on-disk location the app uses under %LOCALAPPDATA%\AiUsageTray.</summary>
public static class AppPaths
{
    private static string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiUsageTray");

    public static string Root => _root;

    /// <summary>Test-only seam so unit tests can redirect all app paths under an isolated temp directory.</summary>
    internal static void SetRootForTests(string path) => _root = path;

    public static string ConfigDir => EnsureDir(Path.Combine(Root, "config"));

    public static string DataDir => EnsureDir(Path.Combine(Root, "data"));

    public static string BridgeDir => EnsureDir(Path.Combine(Root, "bridge"));

    public static string LogsDir => EnsureDir(Path.Combine(Root, "logs"));

    public static string BackupsDir => EnsureDir(Path.Combine(Root, "backups"));

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");

    public static string ClaudeBridgeMetadataFile => Path.Combine(ConfigDir, "claude-bridge.json");

    public static string ClaudeLatestCacheFile => Path.Combine(DataDir, "claude-latest.json");

    public static string ClaudeBridgeScriptFile => Path.Combine(BridgeDir, "claude-statusline-bridge.ps1");

    public static string ClaudeSettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
