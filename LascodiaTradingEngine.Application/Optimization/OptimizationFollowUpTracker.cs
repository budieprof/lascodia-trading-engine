using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Shared helper that updates an optimization run's <see cref="ValidationFollowUpStatus"/>
/// when a validation follow-up (backtest or walk-forward) completes. Used by both
/// <c>BacktestWorker</c> and <c>WalkForwardWorker</c> to close the feedback loop.
///
/// Transitions:
/// <list type="bullet">
///   <item>Pending → Failed: immediately on first follow-up failure</item>
///   <item>Pending → Passed: when all follow-ups have completed successfully</item>
///   <item>Failed → (no change): once failed, the status is terminal</item>
/// </list>
/// </summary>
internal static class OptimizationFollowUpTracker
{
    /// <summary>
    /// Updates the parent optimization run's follow-up status based on whether the
    /// just-completed validation run succeeded. Safe to call from any worker — the
    /// method is idempotent and respects the terminal Failed state.
    /// </summary>
    internal static async Task UpdateStatusAsync(
        DbContext writeDb,
        long optimizationRunId,
        bool followUpPassed,
        IWriteApplicationDbContext writeContext,
        CancellationToken ct)
    {
        var optRun = await writeDb.Set<OptimizationRun>()
            .FirstOrDefaultAsync(r => r.Id == optimizationRunId && !r.IsDeleted, ct);

        if (optRun is null
            || optRun.ValidationFollowUpStatus == ValidationFollowUpStatus.Failed)
            return; // Already failed — don't overwrite

        if (!followUpPassed)
        {
            optRun.ValidationFollowUpStatus = ValidationFollowUpStatus.Failed;
            await writeContext.SaveChangesAsync(ct);
            return;
        }

        // Two queries (one per entity type) instead of four: each returns the count
        // of pending (non-terminal) and failed runs in a single round-trip.
        var btStats = await writeDb.Set<BacktestRun>()
            .Where(r => r.SourceOptimizationRunId == optimizationRunId && !r.IsDeleted)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Pending = g.Count(r => r.Status != RunStatus.Completed && r.Status != RunStatus.Failed),
                Failed  = g.Count(r => r.Status == RunStatus.Failed),
            })
            .FirstOrDefaultAsync(ct);

        var wfStats = await writeDb.Set<WalkForwardRun>()
            .Where(r => r.SourceOptimizationRunId == optimizationRunId && !r.IsDeleted)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Pending = g.Count(r => r.Status != RunStatus.Completed && r.Status != RunStatus.Failed),
                Failed  = g.Count(r => r.Status == RunStatus.Failed),
            })
            .FirstOrDefaultAsync(ct);

        bool hasBacktestRow = (btStats?.Total ?? 0) > 0;
        bool hasWalkForwardRow = (wfStats?.Total ?? 0) > 0;
        int totalPending = (btStats?.Pending ?? 0) + (wfStats?.Pending ?? 0);
        int totalFailed  = (btStats?.Failed ?? 0) + (wfStats?.Failed ?? 0);

        if (!hasBacktestRow || !hasWalkForwardRow)
            return;

        if (totalPending == 0)
        {
            optRun.ValidationFollowUpStatus = totalFailed > 0
                ? ValidationFollowUpStatus.Failed
                : ValidationFollowUpStatus.Passed;
            await writeContext.SaveChangesAsync(ct);
        }
    }
}
