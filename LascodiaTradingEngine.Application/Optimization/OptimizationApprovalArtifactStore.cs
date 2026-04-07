using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationApprovalArtifactStore
{
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationApprovalArtifactStore(TimeProvider timeProvider)
        => _timeProvider = timeProvider;

    internal void RollbackTrackedArtifacts(DbContext writeDb, long optimizationRunId, long strategyId)
    {
        try
        {
            OptimizationFollowUpCoordinator.DetachPendingValidationFollowUps(writeDb, optimizationRunId);

            foreach (var entry in writeDb.ChangeTracker.Entries<StrategyRegimeParams>()
                         .Where(e => e.Entity.StrategyId == strategyId
                                  && e.Entity.OptimizationRunId == optimizationRunId)
                         .ToList())
            {
                if (entry.State == EntityState.Added)
                {
                    entry.State = EntityState.Detached;
                    continue;
                }

                if (entry.State == EntityState.Modified)
                {
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                }
            }
        }
        catch
        {
        }
    }

    internal async Task SaveRegimeParamsAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        Strategy strategy,
        OptimizationRun run,
        string paramsJson,
        decimal healthScore,
        decimal ciLower,
        MarketRegimeEnum regime,
        CancellationToken ct,
        bool persistChanges = true)
    {
        var existing = await writeDb.Set<StrategyRegimeParams>()
            .FirstOrDefaultAsync(p => p.StrategyId == strategy.Id && p.Regime == regime && !p.IsDeleted, ct);

        if (existing is not null)
        {
            existing.ParametersJson = CanonicalParameterJson.Normalize(paramsJson);
            existing.HealthScore = healthScore;
            existing.HealthScoreCILower = ciLower;
            existing.OptimizationRunId = run.Id;
            existing.OptimizedAt = UtcNow;
        }
        else
        {
            writeDb.Set<StrategyRegimeParams>().Add(new StrategyRegimeParams
            {
                StrategyId = strategy.Id,
                Regime = regime,
                ParametersJson = CanonicalParameterJson.Normalize(paramsJson),
                HealthScore = healthScore,
                HealthScoreCILower = ciLower,
                OptimizationRunId = run.Id,
                OptimizedAt = UtcNow,
            });
        }

        if (persistChanges)
            await writeCtx.SaveChangesAsync(ct);
    }
}
