using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationRunLifecycle
{
    internal static bool ShouldPreservePersistedResult(bool completionPersisted, OptimizationRunStatus status)
        => completionPersisted
        && status is OptimizationRunStatus.Completed
                 or OptimizationRunStatus.Approved
                 or OptimizationRunStatus.Rejected;

    internal static void RequeueForRecovery(OptimizationRun run, DateTime utcNow)
        => OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, utcNow);

    internal static void FailForRetry(
        OptimizationRun run,
        string errorMessage,
        OptimizationFailureCategory failureCategory,
        DateTime utcNow)
    {
        run.FailureCategory = failureCategory;
        OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Failed, utcNow, errorMessage);
        run.ApprovedAt = null;
        run.DeferredUntilUtc = null;
        run.BestParametersJson = null;
        run.BestHealthScore = null;
        run.BestSharpeRatio = null;
        run.BestMaxDrawdownPct = null;
        run.BestWinRate = null;
        run.ApprovalReportJson = null;
        run.ValidationFollowUpsCreatedAt = null;
        run.ValidationFollowUpStatus = null;
        run.FollowUpLastCheckedAt = null;
        run.NextFollowUpCheckAt = null;
        run.FollowUpRepairAttempts = 0;
        run.FollowUpLastStatusCode = null;
        run.FollowUpLastStatusMessage = null;
        run.FollowUpStatusUpdatedAt = null;
        run.CompletionPublicationStatus = null;
        run.CompletionPublicationPayloadJson = null;
        run.CompletionPublicationAttempts = 0;
        run.CompletionPublicationLastAttemptAt = null;
        run.CompletionPublicationCompletedAt = null;
        run.CompletionPublicationErrorMessage = null;
        run.LastOperationalIssueCode = null;
        run.LastOperationalIssueMessage = null;
        run.LastOperationalIssueAt = null;
        run.ExecutionStage = OptimizationExecutionStage.Failed;
        run.ExecutionStageMessage = errorMessage;
        run.ExecutionStageUpdatedAt = utcNow;
    }
}
