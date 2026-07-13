using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageTray.Infrastructure;

/// <summary>
/// Stores small secrets (e.g. a GitHub fine-grained token) encrypted at rest using Windows DPAPI,
/// scoped to the current user. Never stores plaintext secrets in JSON config.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretStore
{
    private static string DirectoryPath => Path.Combine(AppPaths.ConfigDir, "secrets");

    private static string PathFor(string key) => Path.Combine(DirectoryPath, $"{Sanitize(key)}.bin");

    public static void Save(string key, string secret)
    {
        Directory.CreateDirectory(DirectoryPath);
        var plainBytes = Encoding.UTF8.GetBytes(secret);
        var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(key), protectedBytes);
    }

    public static string? Load(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            AppLog.Warn("SecretStore", $"Failed to decrypt secret '{key}': {ex.Message}");
            return null;
        }
    }

    public static void Delete(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static bool Exists(string key) => File.Exists(PathFor(key));

    private static string Sanitize(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        return builder.ToString();
    }
}
