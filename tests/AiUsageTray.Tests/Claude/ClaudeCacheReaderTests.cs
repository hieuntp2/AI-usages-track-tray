using System.IO;
using AiUsageTray.Infrastructure;
using AiUsageTray.Providers.Claude;
using Xunit;

namespace AiUsageTray.Tests.Claude;

public class ClaudeCacheReaderTests
{
    [Fact]
    public void ReadLatest_NoFile_ReportsNotExists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"claude-cache-{Guid.NewGuid():N}.json");
        var reader = new ClaudeCacheReader(path);

        var result = reader.ReadLatest();

        Assert.False(result.FileExists);
        Assert.Null(result.Envelope);
        reader.Dispose();
    }

    [Fact]
    public void ReadLatest_AtomicWrite_ReadsCompleteContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"claude-cache-{Guid.NewGuid():N}.json");
        try
        {
            AtomicFile.WriteAllText(path, """{"capturedAt":"2026-07-13T10:00:00Z","payload":{"session_id":"s1"}}""");

            var reader = new ClaudeCacheReader(path);
            var result = reader.ReadLatest();

            Assert.True(result.FileExists);
            Assert.Null(result.Error);
            Assert.Equal("s1", result.Envelope!.Payload!.SessionId);
            reader.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLatest_SecondAtomicWrite_SeesNewestContentNotAMix()
    {
        var path = Path.Combine(Path.GetTempPath(), $"claude-cache-{Guid.NewGuid():N}.json");
        try
        {
            AtomicFile.WriteAllText(path, """{"capturedAt":"2026-07-13T10:00:00Z","payload":{"session_id":"old-session"}}""");
            AtomicFile.WriteAllText(path, """{"capturedAt":"2026-07-13T11:00:00Z","payload":{"session_id":"new-session"}}""");

            var reader = new ClaudeCacheReader(path);
            var result = reader.ReadLatest();

            Assert.Equal("new-session", result.Envelope!.Payload!.SessionId);
            reader.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLatest_InvalidJson_ReturnsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"claude-cache-{Guid.NewGuid():N}.json");
        try
        {
            AtomicFile.WriteAllText(path, "{ not valid json");

            var reader = new ClaudeCacheReader(path);
            var result = reader.ReadLatest();

            Assert.True(result.FileExists);
            Assert.Null(result.Envelope);
            Assert.NotNull(result.Error);
            reader.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
