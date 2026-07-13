using System.IO;
using AiUsageTray.Infrastructure;

namespace AiUsageTray.Providers.Claude;

public sealed record ClaudeCacheReadResult(
    bool FileExists,
    ClaudeCacheEnvelope? Envelope,
    DateTimeOffset? FileWriteTime,
    string? Error);

/// <summary>
/// Watches the bridge's cache file for changes (event-driven via <see cref="FileSystemWatcher"/>,
/// with a slow poll fallback in case the watcher misses an event, which happens occasionally on
/// network drives or under heavy filesystem activity) and exposes the newest successfully-parsed
/// payload. Never triggers Claude Code itself - purely reads whatever the bridge already wrote.
/// </summary>
public sealed class ClaudeCacheReader : IDisposable
{
    private static readonly TimeSpan PollFallbackInterval = TimeSpan.FromSeconds(15);

    private readonly string _cacheFilePath;
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _pollTimer;
    private DateTimeOffset? _lastKnownWriteTime;

    public event Action? CacheChanged;

    public ClaudeCacheReader(string cacheFilePath)
    {
        _cacheFilePath = cacheFilePath;
    }

    public void StartWatching()
    {
        var directory = Path.GetDirectoryName(_cacheFilePath)!;
        Directory.CreateDirectory(directory);

        try
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_cacheFilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => RaiseIfChanged();
            _watcher.Created += (_, _) => RaiseIfChanged();
            _watcher.Renamed += (_, _) => RaiseIfChanged();
            _watcher.Error += (_, e) => AppLog.Warn("Claude.CacheReader", $"FileSystemWatcher error: {e.GetException().Message}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Claude.CacheReader", $"Failed to start FileSystemWatcher, relying on polling: {ex.Message}");
        }

        _pollTimer = new System.Threading.Timer(_ => RaiseIfChanged(), null, PollFallbackInterval, PollFallbackInterval);
    }

    private void RaiseIfChanged()
    {
        lock (_sync)
        {
            if (!File.Exists(_cacheFilePath))
            {
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(_cacheFilePath);
            if (_lastKnownWriteTime is { } known && writeTime <= known.UtcDateTime)
            {
                return;
            }

            _lastKnownWriteTime = writeTime;
        }

        CacheChanged?.Invoke();
    }

    public ClaudeCacheReadResult ReadLatest()
    {
        if (!File.Exists(_cacheFilePath))
        {
            return new ClaudeCacheReadResult(false, null, null, null);
        }

        if (!AtomicFile.TryReadAllText(_cacheFilePath, out var json))
        {
            return new ClaudeCacheReadResult(true, null, null, "Cache file is locked or unreadable.");
        }

        var writeTime = File.GetLastWriteTimeUtc(_cacheFilePath);

        if (!ClaudeCacheParser.TryParseEnvelope(json, out var envelope, out var error))
        {
            return new ClaudeCacheReadResult(true, null, writeTime, error);
        }

        return new ClaudeCacheReadResult(true, envelope, writeTime, null);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _pollTimer?.Dispose();
    }
}
