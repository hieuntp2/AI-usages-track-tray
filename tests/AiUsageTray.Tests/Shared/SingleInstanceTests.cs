using AiUsageTray.Infrastructure;
using Xunit;

namespace AiUsageTray.Tests.Shared;

/// <summary>
/// A true second-process launch can't be exercised from xunit, but the named-mutex acquisition
/// logic (the actual single-instance guarantee) works identically for a second in-process handle
/// to the same mutex name, which is what this test exercises.
/// </summary>
public class SingleInstanceTests
{
    [Fact]
    public void SecondInstance_FailsToAcquireWhileFirstHoldsMutex()
    {
        using var first = new SingleInstance();
        using var second = new SingleInstance();

        var firstAcquired = first.TryAcquire();
        var secondAcquired = second.TryAcquire();

        Assert.True(firstAcquired);
        Assert.False(secondAcquired);
    }

    [Fact]
    public void AfterFirstDisposed_NewInstanceCanAcquire()
    {
        var first = new SingleInstance();
        Assert.True(first.TryAcquire());
        first.Dispose();

        using var second = new SingleInstance();
        Assert.True(second.TryAcquire());
    }
}
