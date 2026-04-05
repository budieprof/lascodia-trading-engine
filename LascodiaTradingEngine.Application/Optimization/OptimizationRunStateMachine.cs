using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Guards OptimizationRun lifecycle transitions so retries and recovery paths cannot
/// silently move terminal runs back into mutable states.
/// </summary>
internal static class OptimizationRunStateMachine
{
    internal static bool CanTransition(OptimizationRunStatus from, OptimizationRunStatus to) => (from, to) switch
    {
        (OptimizationRunStatus.Queued, OptimizationRunStatus.Running) => true,
        (OptimizationRunStatus.Running, OptimizationRunStatus.Queued) => true,
        (OptimizationRunStatus.Running, OptimizationRunStatus.Completed) => true,
        (OptimizationRunStatus.Running, OptimizationRunStatus.Failed) => true,
        (OptimizationRunStatus.Failed, OptimizationRunStatus.Queued) => true, // retry path
        (OptimizationRunStatus.Failed, OptimizationRunStatus.Abandoned) => true, // dead-letter after retries exhausted
        (OptimizationRunStatus.Completed, OptimizationRunStatus.Approved) => true,
        (OptimizationRunStatus.Completed, OptimizationRunStatus.Rejected) => true,
        _ when from == to => true,
        _ => false
    };

    internal static void Transition(
        OptimizationRun run,
        OptimizationRunStatus to,
        DateTime utcNow,
        string? errorMessage = null)
    {
        if (!CanTransition(run.Status, to))
        {
            throw new InvalidOperationException(
                $"Illegal OptimizationRun status transition: {run.Status} -> {to} (runId={run.Id})");
        }

        run.Status = to;

        switch (to)
        {
            case OptimizationRunStatus.Queued:
                run.StartedAt = utcNow;
                run.CompletedAt = null;
                run.ApprovedAt = null;
                run.ErrorMessage = null;
                run.FailureCategory = null;
                run.ExecutionLeaseExpiresAt = null;
                break;

            case OptimizationRunStatus.Completed:
                run.CompletedAt = utcNow;
                run.ErrorMessage = null;
                run.FailureCategory = null;
                run.ExecutionLeaseExpiresAt = null;
                break;

            case OptimizationRunStatus.Failed:
                run.CompletedAt = utcNow;
                run.ErrorMessage = errorMessage;
                run.ExecutionLeaseExpiresAt = null;
                break;

            case OptimizationRunStatus.Approved:
                run.ApprovedAt = utcNow;
                if (!run.CompletedAt.HasValue)
                    run.CompletedAt = utcNow;
                run.ErrorMessage = null;
                run.FailureCategory = null;
                run.ExecutionLeaseExpiresAt = null;
                break;

            case OptimizationRunStatus.Rejected:
                if (!run.CompletedAt.HasValue)
                    run.CompletedAt = utcNow;
                run.ErrorMessage = null;
                run.FailureCategory = null;
                run.ExecutionLeaseExpiresAt = null;
                break;

            case OptimizationRunStatus.Abandoned:
                if (!run.CompletedAt.HasValue)
                    run.CompletedAt = utcNow;
                run.ErrorMessage = errorMessage ?? "Retry budget exhausted — moved to dead-letter queue";
                run.ExecutionLeaseExpiresAt = null;
                break;
        }
    }
}
