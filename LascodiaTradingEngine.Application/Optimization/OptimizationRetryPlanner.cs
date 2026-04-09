using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationRetryPlanner
{
    internal static IQueryable<OptimizationRun> QueryRetryReadyRuns(
        DbContext db,
        int maxRetryAttempts,
        DateTime nowUtc)
        => db.Set<OptimizationRun>()
            .Where(r => r.Status == OptimizationRunStatus.Failed
                     && !r.IsDeleted
                     && r.RetryCount < maxRetryAttempts
                     && r.FailureCategory != OptimizationFailureCategory.ConfigError
                     && r.FailureCategory != OptimizationFailureCategory.SearchExhausted
                     && r.FailureCategory != OptimizationFailureCategory.StrategyRemoved
                     && r.CompletedAt != null
                     && r.CompletedAt.Value.AddMinutes(15 << r.RetryCount) <= nowUtc
                     && !db.Set<OptimizationRun>().Any(active =>
                         !active.IsDeleted
                         && active.StrategyId == r.StrategyId
                         && (active.Status == OptimizationRunStatus.Queued
                             || active.Status == OptimizationRunStatus.Running)));
}
