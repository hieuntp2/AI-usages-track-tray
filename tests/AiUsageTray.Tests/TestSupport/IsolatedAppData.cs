using System.IO;
using AiUsageTray.Infrastructure;
using Xunit;

namespace AiUsageTray.Tests.TestSupport;

/// <summary>
/// Redirects <see cref="AppPaths"/> to a throwaway temp directory for the lifetime of a test, so
/// tests that exercise settings/bridge persistence never touch the real %LOCALAPPDATA%\AiUsageTray.
/// Tests using this must not run in parallel with each other (xunit collection below serializes them).
/// </summary>
public sealed class IsolatedAppData : IDisposable
{
    public string RootPath { get; }

    public IsolatedAppData()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "AiUsageTrayTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        AppPaths.SetRootForTests(RootPath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch
        {
            // Best effort; temp directory, safe to leak on rare cleanup failure.
        }
    }
}

/// <summary>
/// Every test class that mutates <see cref="AppPaths"/> (via <see cref="IsolatedAppData"/>) joins
/// this collection so xunit runs them sequentially - AppPaths.Root is process-wide mutable state.
/// </summary>
[CollectionDefinition("IsolatedAppData", DisableParallelization = true)]
public sealed class IsolatedAppDataCollection
{
}
