namespace AiUsageTray.Models;

/// <summary>
/// Contract every AI-usage provider implements. The orchestrator and UI depend only on this
/// interface plus the normalized models in <see cref="UsageSnapshot"/> - adding a new provider
/// (Copilot, Gemini CLI, Cursor, ...) never requires touching orchestration or UI code.
/// </summary>
public interface IUsageProvider
{
    string Id { get; }

    string DisplayName { get; }

    ProviderCapabilities Capabilities { get; }

    /// <summary>Probe whether the underlying CLI/client is installed and which version it reports.</summary>
    Task<ProviderDetectionResult> DetectAsync(CancellationToken cancellationToken);

    /// <summary>Run any one-time setup required before usage can be read (e.g. install a bridge script).</summary>
    Task<ProviderSetupResult> SetupAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Produce the latest known usage snapshot. For actively-polled providers this may perform
    /// I/O (spawn a process, read a file); for event-driven providers this must be a cheap,
    /// side-effect-free read of the last cached snapshot and must never consume user quota.
    /// </summary>
    Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken);
}
