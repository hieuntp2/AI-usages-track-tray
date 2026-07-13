using System.IO;
using System.IO.Pipes;
using System.Text;

namespace AiUsageTray.Infrastructure;

/// <summary>
/// Enforces a single running instance per user via a named mutex, and lets a second launch
/// signal the first (over a named pipe) to open its flyout instead of starting a redundant process.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Local\\AiUsageTray.SingleInstance.Mutex";
    private const string PipeName = "AiUsageTray.ActivationPipe";
    public const string ActivateMessage = "ACTIVATE";

    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCts;

    public bool IsPrimaryInstance { get; private set; }

    /// <summary>Raised (on a background thread) when a second instance asks this one to activate.</summary>
    public event Action? ActivationRequested;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
        return createdNew;
    }

    public void StartListening()
    {
        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    var message = await reader.ReadLineAsync(token).ConfigureAwait(false);

                    if (message == ActivateMessage)
                    {
                        ActivationRequested?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("SingleInstance", $"Activation listener error: {ex.GetType().Name}");
                }
            }
        }, token);
    }

    /// <summary>Called from a non-primary process to ask the primary instance to show itself.</summary>
    public static bool TrySignalPrimaryInstance(TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect((int)timeout.TotalMilliseconds);

            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(ActivateMessage);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();

        if (IsPrimaryInstance)
        {
            _mutex?.ReleaseMutex();
        }

        _mutex?.Dispose();
    }
}
