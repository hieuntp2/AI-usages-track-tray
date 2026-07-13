using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AiUsageTray.Infrastructure;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Minimal rolling-file logger. Never writes secrets: every message is passed through
/// <see cref="Redact"/> before touching disk, and callers must not pass raw tokens/payloads
/// unless debug logging is explicitly enabled by the user.
/// </summary>
public static class AppLog
{
    private static readonly object SyncRoot = new();
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);
    private const long MaxFileSizeBytes = 2 * 1024 * 1024;

    public static bool DebugLoggingEnabled { get; set; }

    private static readonly Regex[] SecretPatterns =
    {
        new(@"(sk-[A-Za-z0-9_-]{10,})", RegexOptions.Compiled),
        new(@"(gh[pousr]_[A-Za-z0-9]{10,})", RegexOptions.Compiled),
        new(@"(""?(?:access_?token|refresh_?token|api_?key|authorization|bearer)""?\s*[:=]\s*""?)([^"",\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    public static void Info(string source, string message) => Write(LogLevel.Info, source, message);

    public static void Warn(string source, string message) => Write(LogLevel.Warn, source, message);

    public static void Error(string source, string message, Exception? exception = null) =>
        Write(LogLevel.Error, source, exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}");

    public static void Debug(string source, string message)
    {
        if (DebugLoggingEnabled)
        {
            Write(LogLevel.Debug, source, message);
        }
    }

    public static string Redact(string input)
    {
        var result = input;
        foreach (var pattern in SecretPatterns)
        {
            result = pattern.Replace(result, m => m.Groups.Count > 2 ? $"{m.Groups[1].Value}[REDACTED]" : "[REDACTED]");
        }

        return result;
    }

    private static void Write(LogLevel level, string source, string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {source}: {Redact(message)}";

            lock (SyncRoot)
            {
                var path = CurrentLogFilePath();
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                RollIfNeeded(path);
                PruneOldLogs();
            }
        }
        catch
        {
            // Logging must never crash the application.
        }
    }

    private static string CurrentLogFilePath() =>
        Path.Combine(AppPaths.LogsDir, $"aiusagetray-{DateTimeOffset.Now:yyyy-MM-dd}.log");

    private static void RollIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxFileSizeBytes)
        {
            return;
        }

        var rolledPath = Path.Combine(
            AppPaths.LogsDir,
            $"{Path.GetFileNameWithoutExtension(path)}-{DateTimeOffset.Now:HHmmss}{Path.GetExtension(path)}");
        File.Move(path, rolledPath, overwrite: true);
    }

    private static void PruneOldLogs()
    {
        var cutoff = DateTimeOffset.Now - RetentionPeriod;
        foreach (var file in Directory.EnumerateFiles(AppPaths.LogsDir, "*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff.DateTime)
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort.
        }
    }
}
