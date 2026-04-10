using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationRunDeferralTracker
{
    /// <summary>Maximum number of times a run may be deferred before it is abandoned.</summary>
    internal const int MaxDeferralCount = 5;

    /// <summary>Maximum age (in days) from the original queue time before a deferred run is abandoned.</summary>
    internal const int MaxDeferralTtlDays = 7;

    internal static void ApplyDeferral(
        OptimizationRun run,
        OptimizationDeferralReason reason,
        DateTime deferredUntilUtc,
        DateTime nowUtc)
    {
        int nextDeferralCount = checked(run.DeferralCount + 1);
        var queueAnchorUtc = run.QueuedAt == default ? run.StartedAt : run.QueuedAt;
        double ageDays = (nowUtc - queueAnchorUtc).TotalDays;

        if (nextDeferralCount > MaxDeferralCount || ageDays > MaxDeferralTtlDays)
        {
            string abandonReason = nextDeferralCount > MaxDeferralCount
                ? $"Exceeded maximum deferral count ({MaxDeferralCount})"
                : $"Exceeded maximum deferral TTL ({MaxDeferralTtlDays} days, age={ageDays:F1}d)";
            OptimizationRunStateMachine.Transition(
                run,
                OptimizationRunStatus.Abandoned,
                nowUtc,
                abandonReason);
            run.FailureCategory = OptimizationFailureCategory.Timeout;
            return;
        }

        OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, nowUtc);
        run.DeferralReason = reason;
        run.DeferredAtUtc = nowUtc;
        run.DeferredUntilUtc = deferredUntilUtc;
        run.DeferralCount = nextDeferralCount;
        run.LastResumedAtUtc = null;
    }

    internal static void MarkResumed(OptimizationRun run, DateTime nowUtc)
    {
        run.DeferralReason = null;
        run.DeferredAtUtc = null;
        run.DeferredUntilUtc = null;
        run.LastResumedAtUtc = nowUtc;
    }
}
