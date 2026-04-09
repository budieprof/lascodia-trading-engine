using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationRunDeferralTracker
{
    internal static void ApplyDeferral(
        OptimizationRun run,
        OptimizationDeferralReason reason,
        DateTime deferredUntilUtc,
        DateTime nowUtc)
    {
        OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, nowUtc);
        run.DeferralReason = reason;
        run.DeferredAtUtc = nowUtc;
        run.DeferredUntilUtc = deferredUntilUtc;
        run.DeferralCount = checked(run.DeferralCount + 1);
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
