using System.IO;
using System.IO.Pipelines;
using System.Text;
using AiUsageTray.Providers.Codex;
using AiUsageTray.Tests.TestSupport;
using Xunit;

namespace AiUsageTray.Tests.Codex;

/// <summary>
/// Exercises <see cref="CodexJsonRpcClient"/> against in-memory pipes standing in for a child
/// process's stdin/stdout - no real `codex` process is ever spawned. Joins the IsolatedAppData
/// collection because the client logs warnings via AppLog - without redirecting AppPaths, the
/// malformed-line test would write its fixture string into the REAL app's log file, which once
/// sent this project on a hunt for a phantom app-server bug.
/// </summary>
[Collection("IsolatedAppData")]
public class CodexJsonRpcClientTests : IDisposable
{
    private readonly IsolatedAppData _isolated = new();

    public void Dispose() => _isolated.Dispose();

    private sealed class Harness : IAsyncDisposable
    {
        public required CodexJsonRpcClient Client { get; init; }
        public required Stream WriteToClient { get; init; }
        private readonly StreamReader _requestReader;

        public Harness(Stream writtenByClient)
        {
            _requestReader = new StreamReader(writtenByClient, Encoding.UTF8, leaveOpen: true);
        }

        public async Task<string> ReadNextRequestLineAsync() => (await _requestReader.ReadLineAsync())!;

        public async Task SendLineToClientAsync(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await WriteToClient.WriteAsync(bytes);
            await WriteToClient.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _requestReader.Dispose();
        }
    }

    private static Harness CreateHarness()
    {
        // "stdin" pipe: the client writes requests, the test reads them.
        var stdinPipe = new Pipe();
        // "stdout" pipe: the test writes fake responses, the client reads them.
        var stdoutPipe = new Pipe();

        var clientInput = stdinPipe.Writer.AsStream();
        var testReadsRequests = stdinPipe.Reader.AsStream();

        var clientOutput = stdoutPipe.Reader.AsStream();
        var testWritesResponses = stdoutPipe.Writer.AsStream();

        var client = new CodexJsonRpcClient(clientInput, clientOutput);
        client.StartReadLoop();

        return new Harness(testReadsRequests) { Client = client, WriteToClient = testWritesResponses };
    }

    [Fact]
    public async Task SendRequestAsync_CorrelatesResponseById()
    {
        await using var h = CreateHarness();

        var requestTask = h.Client.SendRequestAsync("account/read", new { refreshToken = false }, TimeSpan.FromSeconds(5), CancellationToken.None);

        var requestLine = await h.ReadNextRequestLineAsync();
        Assert.Contains("\"method\":\"account/read\"", requestLine);
        Assert.Contains("\"id\":1", requestLine);

        await h.SendLineToClientAsync("""{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""");

        var result = await requestTask;
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task SendRequestAsync_MultipleInFlight_CorrelateIndependently()
    {
        await using var h = CreateHarness();

        var task1 = h.Client.SendRequestAsync("a", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        var line1 = await h.ReadNextRequestLineAsync();

        var task2 = h.Client.SendRequestAsync("b", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        var line2 = await h.ReadNextRequestLineAsync();

        Assert.Contains("\"id\":1", line1);
        Assert.Contains("\"id\":2", line2);

        // Respond out of order - id 2 first - to prove correlation isn't order-dependent.
        await h.SendLineToClientAsync("""{"jsonrpc":"2.0","id":2,"result":{"which":"b"}}""");
        await h.SendLineToClientAsync("""{"jsonrpc":"2.0","id":1,"result":{"which":"a"}}""");

        var result1 = await task1;
        var result2 = await task2;

        Assert.Equal("a", result1.GetProperty("which").GetString());
        Assert.Equal("b", result2.GetProperty("which").GetString());
    }

    [Fact]
    public async Task SendRequestAsync_TimesOutWhenNoResponseArrives()
    {
        await using var h = CreateHarness();

        await Assert.ThrowsAsync<CodexRequestTimeoutException>(() =>
            h.Client.SendRequestAsync("account/read", null, TimeSpan.FromMilliseconds(100), CancellationToken.None));
    }

    [Fact]
    public async Task SendRequestAsync_CancellationTokenCancelled_ThrowsOperationCanceled()
    {
        await using var h = CreateHarness();
        using var cts = new CancellationTokenSource();

        var task = h.Client.SendRequestAsync("account/read", null, TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task ErrorResponse_ThrowsCodexRpcErrorException()
    {
        await using var h = CreateHarness();

        var requestTask = h.Client.SendRequestAsync("account/read", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        await h.ReadNextRequestLineAsync();

        await h.SendLineToClientAsync("""{"jsonrpc":"2.0","id":1,"error":{"code":-32001,"message":"not authenticated"}}""");

        var ex = await Assert.ThrowsAsync<CodexRpcErrorException>(() => requestTask);
        Assert.Equal(-32001, ex.Code);
        Assert.Contains("not authenticated", ex.Message);
    }

    [Fact]
    public async Task MalformedLine_IsIgnoredAndSubsequentValidLineStillProcessed()
    {
        await using var h = CreateHarness();

        var requestTask = h.Client.SendRequestAsync("account/read", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        await h.ReadNextRequestLineAsync();

        await h.SendLineToClientAsync("{ not valid json !!");
        await h.SendLineToClientAsync("""{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""");

        var result = await requestTask;
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Notification_RaisesEventWithoutConsumingRequestSlot()
    {
        await using var h = CreateHarness();

        string? receivedMethod = null;
        h.Client.NotificationReceived += (method, _) => receivedMethod = method;

        await h.SendLineToClientAsync("""{"jsonrpc":"2.0","method":"account/rateLimits/updated","params":{"primary":{"usedPercent":10}}}""");

        // Give the read loop a moment to dispatch (event handlers run on the read-loop thread).
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (receivedMethod is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal("account/rateLimits/updated", receivedMethod);
    }

    [Fact]
    public async Task StreamClosed_FailsPendingRequestsWithProcessNotRunning()
    {
        var stdinPipe = new Pipe();
        var stdoutPipe = new Pipe();
        var client = new CodexJsonRpcClient(stdinPipe.Writer.AsStream(), stdoutPipe.Reader.AsStream());
        client.StartReadLoop();

        var requestTask = client.SendRequestAsync("account/read", null, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Simulate the child process exiting: complete the "stdout" pipe (EOF for the reader).
        await stdoutPipe.Writer.CompleteAsync();

        await Assert.ThrowsAsync<CodexProcessNotRunningException>(() => requestTask);

        await client.DisposeAsync();
    }
}
