using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationPruningCoordinator))]
/// <summary>
/// Prunes draft auto-generated strategies that repeatedly fail validation.
/// </summary>
internal sealed class StrategyGenerationPruningCoordinator : IStrategyGenerationPruningCoordinator
{
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationPruningCoordinator(TradingMetrics metrics, TimeProvider timeProvider)
    {
        _metrics = metrics;
        _timeProvider = timeProvider;
    }

    public async Task<int> PruneStaleStrategiesAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        ScreeningAuditLogger auditLogger,
        int pruneAfterFailed,
        CancellationToken ct)
    {
        // Only paused draft auto strategies are eligible here; active or already-promoted
        // strategies are handled by other lifecycle paths.
        var db = readCtx.GetDbContext();
        var writeDb = writeCtx.GetDbContext();

        var drafts = await db.Set<Strategy>()
            .Where(s => s.LifecycleStage == StrategyLifecycleStage.Draft
                     && s.Status == StrategyStatus.Paused
                     && s.Name.StartsWith("Auto-")
                     && !s.IsDeleted)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);

        if (drafts.Count == 0)
            return 0;

        var draftIds = drafts.Select(d => d.Id).ToList();
        var orderedRuns = await db.Set<BacktestRun>()
            .Where(r => draftIds.Contains(r.StrategyId)
                     && !r.IsDeleted
                     && (r.Status == RunStatus.Failed || r.Status == RunStatus.Completed))
            .Select(r => new { r.StrategyId, r.Status, r.CompletedAt, r.CreatedAt, r.Id })
            .ToListAsync(ct);

        var toPrune = new List<(long Id, string Name, int Failed)>();
        var consecutiveFailuresByStrategy = orderedRuns
            .GroupBy(r => r.StrategyId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    int consecutiveFailures = 0;
                    foreach (var run in g
                        .OrderByDescending(r => r.CompletedAt ?? r.CreatedAt)
                        .ThenByDescending(r => r.Id))
                    {
                        if (run.Status != RunStatus.Failed)
                            break;

                        consecutiveFailures++;
                    }

                    return consecutiveFailures;
                });

        foreach (var draft in drafts)
        {
            if (!consecutiveFailuresByStrategy.TryGetValue(draft.Id, out var consecutiveFailures))
                continue;

            if (consecutiveFailures < pruneAfterFailed)
                continue;

            var entity = await writeDb.Set<Strategy>().FindAsync([draft.Id], ct);
            if (entity == null)
                continue;

            MarkStrategyAsPruned(entity, _timeProvider.GetUtcNow().UtcDateTime);
            toPrune.Add((draft.Id, draft.Name, consecutiveFailures));
        }

        if (toPrune.Count > 0)
        {
            await writeCtx.SaveChangesAsync(ct);
            foreach (var (id, name, failedCount) in toPrune)
            {
                _metrics.StrategyCandidatesPruned.Add(1);
                await auditLogger.LogPruningAsync(id, name, failedCount, ct);
            }
        }

        return toPrune.Count;
    }

    public static void MarkStrategyAsPruned(Strategy strategy, DateTime prunedAtUtc)
    {
        // Preserve the pruning timestamp inside screening metadata so later analytics can
        // differentiate deliberate pruning from compensation cleanup.
        strategy.IsDeleted = true;
        strategy.PrunedAtUtc = prunedAtUtc;

        var metrics = ScreeningMetrics.FromJson(strategy.ScreeningMetricsJson);
        if (metrics != null)
            strategy.ScreeningMetricsJson = (metrics with { PrunedAtUtc = prunedAtUtc }).ToJson();
    }

    public static void MarkStrategyAsCompensatedDeletion(Strategy strategy)
    {
        strategy.IsDeleted = true;
        strategy.PrunedAtUtc = null;

        var metrics = ScreeningMetrics.FromJson(strategy.ScreeningMetricsJson);
        if (metrics != null && metrics.PrunedAtUtc != null)
            strategy.ScreeningMetricsJson = (metrics with { PrunedAtUtc = null }).ToJson();
    }
}
