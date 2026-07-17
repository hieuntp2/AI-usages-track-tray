using AiUsageTray.Infrastructure;
using Xunit;

namespace AiUsageTray.Tests.Shared;

/// <summary>
/// A true second-process launch can't be exercised from xunit, but the named-mutex acquisition
/// logic (the actual single-instance guarantee) works identically for a second in-process handle
/// to the same mutex name. Each test uses a unique mutex name so it can never collide with a real
/// AiUsageTray.exe running on the same machine (which holds the production mutex).
/// </summary>
public class SingleInstanceTests
{
    private static string UniqueName() => $"Local\\AiUsageTrayTests.{Guid.NewGuid():N}";

    [Fact]
    public void SecondInstance_FailsToAcquireWhileFirstHoldsMutex()
    {
        var name = UniqueName();
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);

        var firstAcquired = first.TryAcquire();
        var secondAcquired = second.TryAcquire();

        Assert.True(firstAcquired);
        Assert.False(secondAcquired);
    }

    [Fact]
    public void AfterFirstDisposed_NewInstanceCanAcquire()
    {
        var name = UniqueName();
        var first = new SingleInstance(name);
        Assert.True(first.TryAcquire());
        first.Dispose();

        using var second = new SingleInstance(name);
        Assert.True(second.TryAcquire());
    }
}
