using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AiUsageTray.Infrastructure;

namespace AiUsageTray.Providers.Codex;

/// <summary>
/// Owns the lifetime of a single `codex app-server` child process and its JSON-RPC client.
/// The process is kept alive for the whole application session (refreshes reuse it rather than
/// spawning per-call), restarted with exponential backoff after an unexpected exit, and always
/// re-handshakes (initialize/initialized) before being handed back to callers.
/// </summary>
public sealed class CodexProcessSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly string _executablePath;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private CodexJsonRpcClient? _client;
    private TimeSpan _currentBackoff = InitialBackoff;
    private bool _disposed;

    public event Action<string, JsonElement?>? NotificationReceived;

    public CodexProcessSupervisor(string executablePath)
    {
        _executablePath = executablePath;
    }

    public bool IsRunning => _process is { HasExited: false } && _client is not null;

    /// <summary>Returns a live, handshaked client, starting or restarting the process if needed.</summary>
    public async Task<CodexJsonRpcClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return _client!;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return _client!;
            }

            await StartAsync(cancellationToken).ConfigureAwait(false);
            return _client!;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        await CleanupCurrentAsync().ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = "app-server",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new CodexProcessNotRunningException($"Failed to start '{_executablePath} app-server': {ex.Message}");
        }

        _ = ConsumeStderrAsync(process);

        var client = new CodexJsonRpcClient(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
        client.NotificationReceived += (method, args) => NotificationReceived?.Invoke(method, args);
        client.StartReadLoop();

        _process = process;
        _client = client;

        process.Exited += (_, _) => OnProcessExited();

        await HandshakeAsync(client, cancellationToken).ConfigureAwait(false);

        _currentBackoff = InitialBackoff; // Reset backoff after a fully successful start.
    }

    private async Task HandshakeAsync(CodexJsonRpcClient client, CancellationToken cancellationToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";

        await client.SendRequestAsync(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = "ai_usage_tray",
                    title = "AI Usage Tray",
                    version,
                },
            },
            HandshakeTimeout,
            cancellationToken).ConfigureAwait(false);

        await client.SendNotificationAsync("initialized", new { }).ConfigureAwait(false);
    }

    private static async Task ConsumeStderrAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                AppLog.Debug("Codex.Process", $"stderr: {line}");
            }
        }
        catch
        {
            // Process likely exited; nothing to do.
        }
    }

    private void OnProcessExited()
    {
        AppLog.Warn("Codex.Process", "codex app-server exited unexpectedly.");
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await client.SendRequestAsync(method, @params, RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (CodexProcessNotRunningException)
        {
            // The process died mid-call; attempt one restart-and-retry with backoff so a
            // transient crash doesn't surface as a permanent error to the UI.
            await Task.Delay(_currentBackoff, cancellationToken).ConfigureAwait(false);
            _currentBackoff = TimeSpan.FromTicks(Math.Min(_currentBackoff.Ticks * 2, MaxBackoff.Ticks));

            var freshClient = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            return await freshClient.SendRequestAsync(method, @params, RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CleanupCurrentAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort.
            }

            _process.Dispose();
            _process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CleanupCurrentAsync().ConfigureAwait(false);
        _startLock.Dispose();
    }
}
