using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationSchedulingCoordinator
{
    private readonly ILogger<OptimizationSchedulingCoordinator> _logger;
    private readonly TradingMetrics _metrics;
    private readonly GradualRolloutManager _rolloutManager;
    private readonly TimeProvider _timeProvider;

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationSchedulingCoordinator(
        ILogger<OptimizationSchedulingCoordinator> logger,
        TradingMetrics metrics,
        GradualRolloutManager rolloutManager,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _metrics = metrics;
        _rolloutManager = rolloutManager;
        _timeProvider = timeProvider;
    }

    internal async Task AutoScheduleUnderperformersAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        OptimizationConfig config,
        CancellationToken ct)
    {
        var db = readCtx.GetDbContext();
        var writeDb = writeCtx.GetDbContext();

        var rollingStrategies = await writeDb.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active
                     && !s.IsDeleted
                     && s.RolloutPct != null
                     && s.RolloutPct < 100)
            .ToListAsync(ct);

        foreach (var rs in rollingStrategies)
        {
            try
            {
                int observationDays = config.RolloutObservationDays;
                var outcome = await _rolloutManager.EvaluateRolloutAsync(
                    rs,
                    db,
                    config.AutoApprovalMinHealthScore,
                    observationDays,
                    ct,
                    config.RolloutTier1Pct,
                    config.RolloutTier2Pct,
                    config.RolloutTier3Pct);

                if (outcome is null)
                    continue;

                await writeCtx.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "OptimizationWorker: rollout for strategy {Id} ({Name}) -> {Outcome}",
                    rs.Id,
                    rs.Name,
                    outcome);

                if (outcome == "rolledback")
                    _metrics.OptimizationAutoRejected.Add(1);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OptimizationWorker: rollout evaluation failed for strategy {Id} (non-fatal)", rs.Id);
            }
        }

        try
        {
            int maxRunsPerWeek = config.MaxRunsPerWeek > 0 ? config.MaxRunsPerWeek : 20;
            var weekCutoff = UtcNow.AddDays(-7);
            var recentRunCount = await db.Set<OptimizationRun>()
                .Where(r => !r.IsDeleted
                         && r.Status != OptimizationRunStatus.Queued
                         && (r.ClaimedAt ?? r.ExecutionStartedAt ?? (DateTime?)r.StartedAt ?? (DateTime?)r.QueuedAt) >= weekCutoff)
                .CountAsync(ct);
            if (recentRunCount >= maxRunsPerWeek)
            {
                _logger.LogInformation(
                    "OptimizationWorker: weekly velocity cap - {Count} runs in last 7 days (limit {Limit})",
                    recentRunCount,
                    maxRunsPerWeek);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OptimizationWorker: velocity cap check failed (non-fatal)");
        }

        var activeStrategies = await db.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .AsNoTracking()
            .Select(s => new { s.Id, s.Name, s.Symbol, s.Timeframe, s.ParametersJson })
            .ToListAsync(ct);

        if (activeStrategies.Count == 0)
            return;

        var pendingOptIds = await db.Set<OptimizationRun>()
            .Where(r => (r.Status == OptimizationRunStatus.Queued || r.Status == OptimizationRunStatus.Running) && !r.IsDeleted)
            .Select(r => r.StrategyId)
            .Distinct()
            .ToListAsync(ct);
        var pendingSet = new HashSet<long>(pendingOptIds);

        var cooldownThreshold = UtcNow.AddDays(-config.CooldownDays);
        var extendedCooldownThreshold = UtcNow.AddDays(-config.CooldownDays * 2);

        var recentOptIds = await db.Set<OptimizationRun>()
            .Where(r => (r.Status == OptimizationRunStatus.Completed
                      || r.Status == OptimizationRunStatus.Approved
                      || r.Status == OptimizationRunStatus.Rejected)
                     && !r.IsDeleted
                     && r.CompletedAt >= cooldownThreshold)
            .Select(r => r.StrategyId)
            .Distinct()
            .ToListAsync(ct);
        var recentOptSet = new HashSet<long>(recentOptIds);

        var recentExtendedOptIds = await db.Set<OptimizationRun>()
            .Where(r => (r.Status == OptimizationRunStatus.Completed
                      || r.Status == OptimizationRunStatus.Approved
                      || r.Status == OptimizationRunStatus.Rejected)
                     && !r.IsDeleted
                     && r.CompletedAt >= extendedCooldownThreshold)
            .Select(r => r.StrategyId)
            .Distinct()
            .ToListAsync(ct);
        var recentExtendedOptSet = new HashSet<long>(recentExtendedOptIds);

        var chronicFailureSet = new HashSet<long>();
        if (config.MaxConsecutiveFailuresBeforeEscalation > 0)
        {
            var strategiesWithRecentRuns = await db.Set<OptimizationRun>()
                .Where(r => !r.IsDeleted
                         && (r.Status == OptimizationRunStatus.Completed
                          || r.Status == OptimizationRunStatus.Approved
                          || r.Status == OptimizationRunStatus.Rejected)
                         && r.CompletedAt >= extendedCooldownThreshold)
                .GroupBy(r => r.StrategyId)
                .Select(g => new
                {
                    StrategyId = g.Key,
                    RecentStatuses = g.OrderByDescending(r => r.CompletedAt)
                        .Take(config.MaxConsecutiveFailuresBeforeEscalation + 1)
                        .Select(r => r.Status)
                        .ToList()
                })
                .ToListAsync(ct);

            foreach (var s in strategiesWithRecentRuns)
            {
                int consecutiveFailures = 0;
                foreach (var status in s.RecentStatuses)
                {
                    if (status == OptimizationRunStatus.Approved)
                        break;

                    consecutiveFailures++;
                }

                if (consecutiveFailures >= config.MaxConsecutiveFailuresBeforeEscalation
                    && recentExtendedOptSet.Contains(s.StrategyId))
                {
                    chronicFailureSet.Add(s.StrategyId);
                }
            }
        }

        var recentBacktests = await db.Set<BacktestRun>()
            .Where(r => r.Status == RunStatus.Completed && !r.IsDeleted)
            .GroupBy(r => r.StrategyId)
            .Select(g => g.OrderByDescending(r => r.CompletedAt)
                .Select(r => new
                {
                    r.StrategyId,
                    r.TotalTrades,
                    r.WinRate,
                    r.ProfitFactor,
                    r.MaxDrawdownPct,
                    r.SharpeRatio,
                    r.FinalBalance,
                    r.TotalReturn
                })
                .First())
            .ToListAsync(ct);
        var backtestMap = recentBacktests.ToDictionary(r => r.StrategyId);

        var activeStrategyIds = activeStrategies.Select(s => s.Id).ToList();
        var allRecentSnapshots = await db.Set<StrategyPerformanceSnapshot>()
            .Where(s => activeStrategyIds.Contains(s.StrategyId) && !s.IsDeleted)
            .GroupBy(s => s.StrategyId)
            .Select(g => new
            {
                StrategyId = g.Key,
                Scores = g.OrderByDescending(s => s.EvaluatedAt)
                    .Take(3)
                    .Select(s => s.HealthScore)
                    .ToList()
            })
            .ToListAsync(ct);
        var snapshotMap = allRecentSnapshots.ToDictionary(s => s.StrategyId, s => s.Scores);

        var candidates = new List<(long StrategyId, string Name, string ParamsJson, int Priority, decimal Severity, decimal WinRate, decimal ProfitFactor)>();

        foreach (var strategy in activeStrategies)
        {
            ct.ThrowIfCancellationRequested();

            if (pendingSet.Contains(strategy.Id) || recentOptSet.Contains(strategy.Id))
                continue;

            if (chronicFailureSet.Contains(strategy.Id))
            {
                _logger.LogDebug(
                    "OptimizationWorker: strategy {Id} ({Name}) under extended cooldown (chronic auto-approval failure)",
                    strategy.Id,
                    strategy.Name);
                continue;
            }

            if (!backtestMap.TryGetValue(strategy.Id, out var backtestMetricsRow))
                continue;

            if (!global::LascodiaTradingEngine.Application.Backtesting.BacktestRunMetricsReader.TryRead(
                    backtestMetricsRow.TotalTrades,
                    backtestMetricsRow.WinRate,
                    backtestMetricsRow.ProfitFactor,
                    backtestMetricsRow.MaxDrawdownPct,
                    backtestMetricsRow.SharpeRatio,
                    backtestMetricsRow.FinalBalance,
                    backtestMetricsRow.TotalReturn,
                    out var result))
                continue;

            bool meetsGate = result.TotalTrades >= config.MinTotalTrades
                && (double)result.WinRate >= config.MinWinRate
                && (double)result.ProfitFactor >= config.MinProfitFactor;

            if (meetsGate)
            {
                snapshotMap.TryGetValue(strategy.Id, out var recentSnapshots);
                recentSnapshots ??= [];

                bool deteriorating = OptimizationPolicyHelpers.IsMeaningfullyDeteriorating(
                    recentSnapshots,
                    out decimal predictedDecline);

                if (!deteriorating)
                {
                    _logger.LogDebug(
                        "OptimizationWorker: strategy {Id} ({Name}) meets performance gate - no optimization needed",
                        strategy.Id,
                        strategy.Name);
                    continue;
                }

                candidates.Add((strategy.Id, strategy.Name, strategy.ParametersJson, 1, predictedDecline, result.WinRate, result.ProfitFactor));
            }
            else
            {
                decimal healthScore = OptimizationHealthScorer.ComputeHealthScore(
                    result.WinRate,
                    result.ProfitFactor,
                    result.MaxDrawdownPct,
                    result.SharpeRatio,
                    result.TotalTrades);
                candidates.Add((strategy.Id, strategy.Name, strategy.ParametersJson, 0, 1m - healthScore, result.WinRate, result.ProfitFactor));
            }
        }

        if (candidates.Count > 1)
        {
            var strategyIds = candidates.Select(c => c.StrategyId).ToList();
            var approvalRates = await db.Set<OptimizationRun>()
                .Where(r => strategyIds.Contains(r.StrategyId)
                         && (r.Status == OptimizationRunStatus.Completed
                          || r.Status == OptimizationRunStatus.Approved
                          || r.Status == OptimizationRunStatus.Rejected)
                         && !r.IsDeleted)
                .GroupBy(r => r.StrategyId)
                .Select(g => new
                {
                    StrategyId = g.Key,
                    Total = g.Count(),
                    Approved = g.Count(r => r.Status == OptimizationRunStatus.Approved),
                })
                .ToListAsync(ct);
            var roiMap = approvalRates.ToDictionary(a => a.StrategyId, a => a.Total > 0 ? (double)a.Approved / a.Total : 0.5);

            candidates = candidates.Select(c =>
            {
                double roi = roiMap.GetValueOrDefault(c.StrategyId, 0.5);
                if (roi < 0.2 && roiMap.ContainsKey(c.StrategyId))
                {
                    _logger.LogDebug(
                        "OptimizationWorker: deprioritizing strategy {Id} ({Name}) - optimization ROI={Roi:P0}",
                        c.StrategyId,
                        c.Name,
                        roi);
                    return (c.StrategyId, c.Name, c.ParamsJson, c.Priority + 2, c.Severity * 0.5m, c.WinRate, c.ProfitFactor);
                }

                return c;
            }).ToList();
        }

        var toSchedule = candidates
            .OrderBy(c => c.Priority)
            .ThenByDescending(c => c.Severity)
            .Take(config.MaxQueuedPerCycle);

        foreach (var candidate in toSchedule)
        {
            var scheduledRun = new OptimizationRun
            {
                StrategyId = candidate.StrategyId,
                TriggerType = TriggerType.Scheduled,
                Status = OptimizationRunStatus.Queued,
                BaselineParametersJson = CanonicalParameterJson.Normalize(candidate.ParamsJson),
                StartedAt = UtcNow,
            };
            writeDb.Set<OptimizationRun>().Add(scheduledRun);

            try
            {
                await writeCtx.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "OptimizationWorker: auto-queued optimization for strategy {Id} ({Name}) - priority={Prio}, severity={Sev:F2}, WR={WR:P1} PF={PF:F2}",
                    candidate.StrategyId,
                    candidate.Name,
                    candidate.Priority,
                    candidate.Severity,
                    (double)candidate.WinRate,
                    (double)candidate.ProfitFactor);
            }
            catch (DbUpdateException ex) when (OptimizationDbExceptionClassifier.IsActiveQueueConstraintViolation(ex))
            {
                writeDb.Entry(scheduledRun).State = EntityState.Detached;
                _logger.LogInformation(
                    "OptimizationWorker: skipped duplicate auto-queue for strategy {Id} ({Name}) - another worker already queued or claimed it",
                    candidate.StrategyId,
                    candidate.Name);
            }
        }
    }
}
