using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AiUsageTray.Infrastructure;

namespace AiUsageTray.Providers.Codex;

/// <summary>
/// Thrown when the underlying `codex app-server` process is not running (exited, never started,
/// or failed to launch) and a caller tried to send a request through it.
/// </summary>
public sealed class CodexProcessNotRunningException : Exception
{
    public CodexProcessNotRunningException(string message) : base(message)
    {
    }
}

/// <summary>Thrown when a JSON-RPC request does not receive a response within its timeout.</summary>
public sealed class CodexRequestTimeoutException : Exception
{
    public CodexRequestTimeoutException(string method) : base($"Request '{method}' timed out.")
    {
    }
}

/// <summary>Thrown when the Codex app-server returns a JSON-RPC error object for a request.</summary>
public sealed class CodexRpcErrorException : Exception
{
    public int Code { get; }

    public CodexRpcErrorException(int code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// A minimal, transport-agnostic JSON-RPC 2.0 client speaking newline-delimited JSON over a pair
/// of streams (in practice, a child process's stdin/stdout). Owns request-id correlation, request
/// timeouts, notification dispatch, and defends against malformed or oversized lines so a single
/// bad message from the server can never take the whole client down.
/// </summary>
public sealed class CodexJsonRpcClient : IAsyncDisposable
{
    private const int MaxLineLengthBytes = 4 * 1024 * 1024;

    private readonly Stream _input;   // what we write to (child's stdin)
    private readonly Stream _output;  // what we read from (child's stdout)
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<long, PendingRequest> _pending = new();
    private readonly object _pendingLock = new();
    private long _nextId;
    private Task? _readLoopTask;
    private CancellationTokenSource? _readLoopCts;
    private volatile bool _disposed;

    public event Action<string, JsonElement?>? NotificationReceived;

    public event Action? Disconnected;

    private sealed record PendingRequest(TaskCompletionSource<JsonElement> Completion, CancellationTokenRegistration Registration);

    public CodexJsonRpcClient(Stream input, Stream output)
    {
        _input = input;
        _output = output;
        _writer = new StreamWriter(_input, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
    }

    public void StartReadLoop()
    {
        _readLoopCts = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? @params, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new CodexProcessNotRunningException("JSON-RPC client has been disposed.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var registration = timeoutCts.Token.Register(() =>
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }

            tcs.TrySetException(cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : new CodexRequestTimeoutException(method));
        });

        lock (_pendingLock)
        {
            _pending[id] = new PendingRequest(tcs, registration);
        }

        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null)
        {
            payload["params"] = @params;
        }

        try
        {
            await WriteLineAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }

            registration.Dispose();
            throw new CodexProcessNotRunningException($"Failed to write request '{method}': {ex.Message}");
        }

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }
    }

    public async Task SendNotificationAsync(string method, object? @params)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (@params is not null)
        {
            payload["params"] = @params;
        }

        await WriteLineAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(string json)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(_output, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await ReadLineBoundedAsync(reader, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                {
                    break; // EOF: process exited.
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                DispatchLine(line);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Codex.JsonRpc", $"Read loop terminated: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            FailAllPending(new CodexProcessNotRunningException("Codex app-server connection closed."));
            Disconnected?.Invoke();
        }
    }

    private static async Task<string?> ReadLineBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[1];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            var c = buffer[0];
            if (c == '\n')
            {
                return builder.ToString();
            }

            if (c != '\r')
            {
                builder.Append(c);
            }

            if (builder.Length > MaxLineLengthBytes)
            {
                AppLog.Warn("Codex.JsonRpc", "Dropping oversized line from app-server.");
                builder.Clear();
            }
        }
    }

    private void DispatchLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            // Include a short, redacted prefix of the offending line - "malformed" alone is
            // undiagnosable. AppLog redacts token-shaped substrings before anything hits disk.
            var preview = line.Length > 120 ? line[..120] + "…" : line;
            AppLog.Warn("Codex.JsonRpc", $"Ignoring malformed line ({ex.Message}): {preview}");
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var idElement) && (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)))
            {
                HandleResponse(root, idElement);
                return;
            }

            if (root.TryGetProperty("method", out var methodElement))
            {
                var method = methodElement.GetString() ?? string.Empty;
                JsonElement? paramsElement = root.TryGetProperty("params", out var p) ? p.Clone() : null;
                NotificationReceived?.Invoke(method, paramsElement);
            }
        }
    }

    private void HandleResponse(JsonElement root, JsonElement idElement)
    {
        if (!TryGetId(idElement, out var id))
        {
            return;
        }

        PendingRequest? pending;
        lock (_pendingLock)
        {
            if (_pending.Remove(id, out var found))
            {
                pending = found;
            }
            else
            {
                pending = null;
            }
        }

        if (pending is null)
        {
            return; // Late/duplicate response for an already-timed-out request.
        }

        if (root.TryGetProperty("error", out var errorElement))
        {
            var code = errorElement.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            var message = errorElement.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
            pending.Completion.TrySetException(new CodexRpcErrorException(code, message));
            return;
        }

        var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
        pending.Completion.TrySetResult(result);
    }

    private static bool TryGetId(JsonElement idElement, out long id)
    {
        switch (idElement.ValueKind)
        {
            case JsonValueKind.Number when idElement.TryGetInt64(out id):
                return true;
            case JsonValueKind.String when long.TryParse(idElement.GetString(), out id):
                return true;
            default:
                id = 0;
                return false;
        }
    }

    private void FailAllPending(Exception exception)
    {
        List<PendingRequest> toFail;
        lock (_pendingLock)
        {
            toFail = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (var pending in toFail)
        {
            pending.Completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readLoopCts?.Cancel();

        FailAllPending(new CodexProcessNotRunningException("Codex JSON-RPC client disposed."));

        if (_readLoopTask is not null)
        {
            try
            {
                await _readLoopTask.ConfigureAwait(false);
            }
            catch
            {
                // Already logged in the loop.
            }
        }

        _writer.Dispose();
        _writeLock.Dispose();
        _readLoopCts?.Dispose();
    }
}
