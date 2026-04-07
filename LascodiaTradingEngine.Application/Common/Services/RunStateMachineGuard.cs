using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Services;

/// <summary>
/// Validates that run status transitions are legal. Prevents invalid state changes
/// like Completed → Running or Failed → Queued.
/// </summary>
public static class RunStateMachineGuard
{
    private static readonly Dictionary<RunStatus, HashSet<RunStatus>> AllowedTransitions = new()
    {
        [RunStatus.Queued]    = new() { RunStatus.Running, RunStatus.Failed },
        [RunStatus.Running]   = new() { RunStatus.Completed, RunStatus.Failed },
        [RunStatus.Completed] = new() { }, // Terminal
        [RunStatus.Failed]    = new() { RunStatus.Queued }, // Retry only
    };

    private static readonly Dictionary<OptimizationRunStatus, HashSet<OptimizationRunStatus>> OptAllowedTransitions = new()
    {
        [OptimizationRunStatus.Queued]    = new() { OptimizationRunStatus.Running, OptimizationRunStatus.Failed, OptimizationRunStatus.Abandoned },
        [OptimizationRunStatus.Running]   = new() { OptimizationRunStatus.Completed, OptimizationRunStatus.Failed },
        [OptimizationRunStatus.Completed] = new() { OptimizationRunStatus.Approved, OptimizationRunStatus.Rejected },
        [OptimizationRunStatus.Failed]    = new() { OptimizationRunStatus.Queued },
        [OptimizationRunStatus.Approved]  = new() { },
        [OptimizationRunStatus.Rejected]  = new() { OptimizationRunStatus.Queued },
        [OptimizationRunStatus.Abandoned] = new() { },
    };

    public static bool IsTransitionAllowed(RunStatus from, RunStatus to)
    {
        return AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    public static void AssertTransition(RunStatus from, RunStatus to)
    {
        if (!IsTransitionAllowed(from, to))
            throw new InvalidOperationException($"Invalid run status transition: {from} → {to}");
    }

    public static bool IsOptTransitionAllowed(OptimizationRunStatus from, OptimizationRunStatus to)
    {
        return OptAllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    public static void AssertOptTransition(OptimizationRunStatus from, OptimizationRunStatus to)
    {
        if (!IsOptTransitionAllowed(from, to))
            throw new InvalidOperationException($"Invalid optimization run status transition: {from} → {to}");
    }
}
