using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationRunProgressTracker
{
    private const int MaxCodeLength = 100;
    private const int MaxMessageLength = 500;

    internal static void SetStage(
        OptimizationRun run,
        OptimizationExecutionStage stage,
        string? message,
        DateTime utcNow)
    {
        run.ExecutionStage = stage;
        run.ExecutionStageMessage = Truncate(message, MaxMessageLength);
        run.ExecutionStageUpdatedAt = utcNow;
    }

    internal static void SetTerminalStageFromStatus(OptimizationRun run, DateTime utcNow)
    {
        OptimizationExecutionStage terminalStage = run.Status switch
        {
            OptimizationRunStatus.Completed => OptimizationExecutionStage.Completed,
            OptimizationRunStatus.Approved => OptimizationExecutionStage.Approved,
            OptimizationRunStatus.Rejected => OptimizationExecutionStage.Rejected,
            OptimizationRunStatus.Failed => OptimizationExecutionStage.Failed,
            OptimizationRunStatus.Abandoned => OptimizationExecutionStage.Abandoned,
            OptimizationRunStatus.Queued => OptimizationExecutionStage.Queued,
            OptimizationRunStatus.Running => OptimizationExecutionStage.Preflight,
            _ => OptimizationExecutionStage.Preflight,
        };

        SetStage(run, terminalStage, GetDefaultStageMessage(terminalStage), utcNow);
    }

    internal static void RecordOperationalIssue(
        OptimizationRun run,
        string code,
        string message,
        DateTime utcNow)
    {
        run.LastOperationalIssueCode = Truncate(code, MaxCodeLength);
        run.LastOperationalIssueMessage = Truncate(message, MaxMessageLength);
        run.LastOperationalIssueAt = utcNow;
    }

    internal static void ClearOperationalIssue(OptimizationRun run)
    {
        run.LastOperationalIssueCode = null;
        run.LastOperationalIssueMessage = null;
        run.LastOperationalIssueAt = null;
    }

    internal static string GetDefaultStageMessage(OptimizationExecutionStage stage) => stage switch
    {
        OptimizationExecutionStage.Queued => "Queued for optimization.",
        OptimizationExecutionStage.Preflight => "Claimed by worker; running preflight checks.",
        OptimizationExecutionStage.DataLoad => "Loading candles and baseline context.",
        OptimizationExecutionStage.Search => "Running Bayesian search across the parameter space.",
        OptimizationExecutionStage.Validation => "Validating Pareto candidates against approval gates.",
        OptimizationExecutionStage.Persist => "Persisting optimization results and reports.",
        OptimizationExecutionStage.Approval => "Applying approval or manual-review outcome.",
        OptimizationExecutionStage.CompletionPublication => "Publishing terminal optimization-completion side effects.",
        OptimizationExecutionStage.FollowUp => "Monitoring post-approval validation follow-ups.",
        OptimizationExecutionStage.Completed => "Optimization completed.",
        OptimizationExecutionStage.Approved => "Optimization approved and applied to the strategy.",
        OptimizationExecutionStage.Rejected => "Optimization completed but requires manual review.",
        OptimizationExecutionStage.Failed => "Optimization failed.",
        OptimizationExecutionStage.Abandoned => "Optimization was abandoned after a permanent failure.",
        _ => "Optimization pipeline is progressing.",
    };

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            return value;

        return value[..maxLength];
    }
}
