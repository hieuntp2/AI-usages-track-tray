namespace AiUsageTray.Providers.Codex;

public sealed record CodexWindowData(
    decimal? UsedPercent,
    int? WindowDurationMins,
    DateTimeOffset? ResetsAt)
{
    /// <summary>Overlay non-null fields from a sparse update on top of this window, keeping the rest.</summary>
    public CodexWindowData MergeSparse(CodexWindowData update) => new(
        update.UsedPercent ?? UsedPercent,
        update.WindowDurationMins ?? WindowDurationMins,
        update.ResetsAt ?? ResetsAt);
}

/// <summary>
/// Accumulated, mutable rate-limit state for the Codex account. `account/rateLimits/read` provides
/// a full snapshot; the `account/rateLimits/updated` notification may carry only some fields, so
/// updates are merged field-by-field on top of the last known-complete state rather than replacing
/// it wholesale (which would otherwise null out fields the notification didn't include).
/// </summary>
public sealed class CodexRateLimitState
{
    public string? LimitId { get; private set; }

    public string? LimitName { get; private set; }

    public string? PlanType { get; private set; }

    public CodexWindowData? Primary { get; private set; }

    public CodexWindowData? Secondary { get; private set; }

    public string? RateLimitReachedType { get; private set; }

    public decimal? AvailableCredit { get; private set; }

    public int? ResetCreditCount { get; private set; }

    public void Merge(CodexRateLimitState update)
    {
        LimitId = update.LimitId ?? LimitId;
        LimitName = update.LimitName ?? LimitName;
        PlanType = update.PlanType ?? PlanType;
        Primary = MergeWindow(Primary, update.Primary);
        Secondary = MergeWindow(Secondary, update.Secondary);
        RateLimitReachedType = update.RateLimitReachedType ?? RateLimitReachedType;
        AvailableCredit = update.AvailableCredit ?? AvailableCredit;
        ResetCreditCount = update.ResetCreditCount ?? ResetCreditCount;
    }

    private static CodexWindowData? MergeWindow(CodexWindowData? existing, CodexWindowData? update)
    {
        if (update is null)
        {
            return existing;
        }

        return existing is null ? update : existing.MergeSparse(update);
    }

    public static CodexRateLimitState CreateFull(
        string? limitId, string? limitName, string? planType,
        CodexWindowData? primary, CodexWindowData? secondary,
        string? rateLimitReachedType, decimal? availableCredit, int? resetCreditCount) => new()
    {
        LimitId = limitId,
        LimitName = limitName,
        PlanType = planType,
        Primary = primary,
        Secondary = secondary,
        RateLimitReachedType = rateLimitReachedType,
        AvailableCredit = availableCredit,
        ResetCreditCount = resetCreditCount,
    };
}
