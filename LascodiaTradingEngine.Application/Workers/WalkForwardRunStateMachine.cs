using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

internal static class WalkForwardRunStateMachine
{
    internal static bool CanTransition(RunStatus from, RunStatus to) => (from, to) switch
    {
        (RunStatus.Queued, RunStatus.Running) => true,
        (RunStatus.Running, RunStatus.Queued) => true,
        (RunStatus.Running, RunStatus.Completed) => true,
        (RunStatus.Running, RunStatus.Failed) => true,
        (RunStatus.Failed, RunStatus.Queued) => true,
        _ when from == to => true,
        _ => false
    };

    internal static void Transition(
        WalkForwardRun run,
        RunStatus to,
        DateTime utcNow,
        string? errorMessage = null,
        ValidationFailureCode? failureCode = null,
        string? failureDetailsJson = null,
        bool resetQueuePosition = true,
        DateTime? availableAtUtc = null)
    {
        var from = run.Status;
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Illegal WalkForwardRun status transition: {from} -> {to} (runId={run.Id})");

        run.Status = to;

        switch (to)
        {
            case RunStatus.Queued:
                if (resetQueuePosition || from != RunStatus.Queued)
                    run.QueuedAt = utcNow;
                run.AvailableAt = availableAtUtc ?? utcNow;
                run.ClaimedAt = null;
                run.ClaimedByWorkerId = null;
                run.ExecutionStartedAt = null;
                run.CompletedAt = null;
                run.ErrorMessage = null;
                run.FailureCode = null;
                run.FailureDetailsJson = null;
                run.LastHeartbeatAt = null;
                run.ExecutionLeaseExpiresAt = null;
                run.ExecutionLeaseToken = null;
                run.AverageOutOfSampleScore = null;
                run.ScoreConsistency = null;
                run.WindowResultsJson = null;
                break;

            case RunStatus.Completed:
                run.CompletedAt = utcNow;
                run.ErrorMessage = null;
                run.FailureCode = null;
                run.FailureDetailsJson = null;
                run.ClaimedByWorkerId = null;
                run.ExecutionLeaseExpiresAt = null;
                run.ExecutionLeaseToken = null;
                break;

            case RunStatus.Failed:
                run.CompletedAt = utcNow;
                run.ErrorMessage = errorMessage;
                run.FailureCode = failureCode;
                run.FailureDetailsJson = failureDetailsJson;
                run.ClaimedByWorkerId = null;
                run.ExecutionLeaseExpiresAt = null;
                run.ExecutionLeaseToken = null;
                run.AverageOutOfSampleScore = null;
                run.ScoreConsistency = null;
                run.WindowResultsJson = null;
                break;
        }
    }
}
