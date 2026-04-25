using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Autonomous promotion worker that closes the strategy lifecycle loop by advancing
/// auto-generated strategies through <c>BacktestQualified → Approved → Active</c>.
///
/// <para>
/// <b>Pipeline position:</b> This worker is the missing link between the validation
/// pipeline (BacktestWorker → WalkForwardWorker) and live signal generation (StrategyWorker).
/// Without it, auto-generated strategies stay at <c>Draft</c>/<c>BacktestQualified</c>
/// indefinitely and the feedback survival signal in StrategyGenerationWorker never fires.
/// </para>
///
/// <para>
/// <b>Promotion criteria (BacktestQualified → Approved):</b>
/// <list type="bullet">
///   <item>Strategy has been at <c>BacktestQualified</c> for at least <c>MinObservationDays</c> (default 7).</item>
///   <item>At least <c>MinHealthySnapshots</c> (default 3) recent <see cref="StrategyPerformanceSnapshot"/>
///         records exist with <c>HealthScore ≥ MinHealthScore</c> (default 0.55).</item>
///   <item>No snapshot in the observation window shows <c>Critical</c> health status.</item>
///   <item><b>Live/backtest Sharpe ratio gate:</b> when
///         <c>MinLiveVsBacktestSharpeRatio</c> &gt; 0 (default 0.5) and the strategy has a
///         completed backtest with positive Sharpe, the average Sharpe across recent
///         snapshots must be at least that fraction of the best backtest Sharpe. Blocks
///         backtest-overfit strategies whose live edge has silently collapsed.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Auto-activation (Approved → Active) and the human-vs-auto boundary:</b>
/// Auto-generated strategies are identified by their <c>Auto-</c> name prefix and ONLY
/// they are eligible for this worker. When
/// <c>StrategyPromotion:AutoActivateEnabled</c> is true (default), auto-generated
/// strategies that reach <c>Approved</c> are automatically activated. This is the
/// controlled bypass of the <c>IPromotionGateValidator</c> (Deflated Sharpe / PBO /
/// TCA EV / paper-trade duration / regime coverage / max pairwise correlation) in
/// <c>ActivateStrategyCommand</c>. The gate still runs for <b>human-introduced</b>
/// strategies (operator-created, imported from research notebooks, etc.) that have
/// not passed the generation pipeline's 12-gate screening. In short: the CLAUDE.md
/// "no manual gates" rule applies to the autonomous generation/optimization pipeline;
/// <c>IPromotionGateValidator</c> is retained purely for strategies that never ran
/// through that pipeline. Set <c>AutoActivateEnabled</c> to false to force ALL
/// strategies (including auto-generated) back through the manual command path — used
/// for emergency lockdown or during pipeline debugging.
/// </para>
///
/// <para>
/// <b>Safety gates:</b>
/// <list type="bullet">
///   <item>Only auto-generated strategies (<c>Name.StartsWith("Auto-")</c>) are promoted.</item>
///   <item>Strategies with no performance snapshots are skipped (insufficient evidence).</item>
///   <item>Per-symbol active strategy cap (<c>MaxActivePerSymbol</c>) prevents over-concentration.</item>
///   <item>All transitions are audit-logged via <see cref="LogDecisionCommand"/>.</item>
///   <item>A <see cref="StrategyActivatedIntegrationEvent"/> is published on activation so
///         downstream workers (StrategyHealthWorker, ML training) react immediately.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Config namespace:</b> <c>StrategyPromotion:*</c> (all hot-reloadable via EngineConfig).
/// </para>
/// </summary>
public sealed class StrategyPromotionWorker : InstrumentedBackgroundService
{
    private const string CK_Enabled             = "StrategyPromotion:Enabled";
    private const string CK_PollMinutes         = "StrategyPromotion:PollIntervalMinutes";
    private const string CK_MinObservationDays  = "StrategyPromotion:MinObservationDays";
    private const string CK_MinHealthScore      = "StrategyPromotion:MinHealthScore";
    private const string CK_MinHealthySnapshots = "StrategyPromotion:MinHealthySnapshots";
    private const string CK_AutoActivate        = "StrategyPromotion:AutoActivateEnabled";
    private const string CK_MaxActivePerSymbol  = "StrategyPromotion:MaxActivePerSymbol";
    private const string CK_MinLiveVsBacktestSharpeRatio = "StrategyPromotion:MinLiveVsBacktestSharpeRatio";
    private const string CK_ChampionChallengerEnabled = "StrategyPromotion:ChampionChallengerEnabled";
    private const string CK_MinChallengerQualityDelta = "StrategyPromotion:MinChallengerQualityScoreDelta";
    private const string CK_MinChallengerSelectionDelta = "StrategyPromotion:MinChallengerSelectionScoreDelta";

    private const int DefaultPollMinutes = 60;

    private readonly ILogger<StrategyPromotionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private readonly IWorkerHealthMonitor? _healthMonitor;

    private int _consecutiveFailures;

    public StrategyPromotionWorker(
        ILogger<StrategyPromotionWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics,
        IWorkerHealthMonitor? healthMonitor = null)
        : base(healthMonitor, logger)
    {
        _logger        = logger;
        _scopeFactory  = scopeFactory;
        _metrics       = metrics;
        _healthMonitor = healthMonitor;
    }

    protected override async Task ExecuteInstrumentedAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyPromotionWorker starting");

        _healthMonitor?.RecordWorkerMetadata(
            nameof(StrategyPromotionWorker),
            "Promotes BacktestQualified → Approved → Active strategies after observation window.",
            TimeSpan.FromMinutes(DefaultPollMinutes));

        // Brief startup delay to let BacktestWorker/WalkForwardWorker populate initial data.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollMinutes = DefaultPollMinutes;
            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(nameof(StrategyPromotionWorker));
                await RunPromotionCycleAsync(stoppingToken);
                _consecutiveFailures = 0;

                await using var configScope = _scopeFactory.CreateAsyncScope();
                var configCtx = configScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                pollMinutes = await GetConfigAsync(configCtx.GetDbContext(), CK_PollMinutes, DefaultPollMinutes, stoppingToken);
                pollMinutes = Math.Max(1, pollMinutes);

                _healthMonitor?.RecordCycleSuccess(nameof(StrategyPromotionWorker), 0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _healthMonitor?.RecordCycleFailure(nameof(StrategyPromotionWorker), ex.Message);
                _logger.LogError(ex,
                    "StrategyPromotionWorker: error during promotion cycle (consecutive failures: {Failures})",
                    _consecutiveFailures);
                _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", "StrategyPromotionWorker"));
            }

            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromMinutes(Math.Min(pollMinutes * Math.Pow(2, _consecutiveFailures - 1), 120))
                : TimeSpan.FromMinutes(pollMinutes);

            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("StrategyPromotionWorker stopped");
    }

    internal async Task RunPromotionCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();
        var eventService = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();
        var db           = readCtx.GetDbContext();
        var writeDb      = writeCtx.GetDbContext();

        bool enabled = await GetConfigAsync(db, CK_Enabled, true, ct);
        if (!enabled) return;

        int minObservationDays   = Math.Max(1, await GetConfigAsync(db, CK_MinObservationDays, 7, ct));
        decimal minHealthScore   = await GetConfigAsync(db, CK_MinHealthScore, 0.55m, ct);
        int minHealthySnapshots  = Math.Max(1, await GetConfigAsync(db, CK_MinHealthySnapshots, 3, ct));
        bool autoActivateEnabled = await GetConfigAsync(db, CK_AutoActivate, true, ct);
        int maxActivePerSymbol   = Math.Max(1, await GetConfigAsync(db, CK_MaxActivePerSymbol, 5, ct));
        bool championChallengerEnabled = await GetConfigAsync(db, CK_ChampionChallengerEnabled, true, ct);
        double minChallengerQualityDelta = Math.Max(0.0, await GetConfigAsync(db, CK_MinChallengerQualityDelta, 0.0, ct));
        double minChallengerSelectionDelta = Math.Max(0.0, await GetConfigAsync(db, CK_MinChallengerSelectionDelta, 0.0, ct));
        // Live-vs-backtest Sharpe ratio floor. When >0, promotion requires the average Sharpe
        // across recent healthy snapshots to be at least this fraction of the strategy's best
        // completed backtest Sharpe. Guards against strategies that looked good in backtest
        // but are quietly decaying live — they would still pass the health-score floor but
        // their realised Sharpe has collapsed. Default 0.5 (live must retain at least 50%
        // of backtested edge). Set to 0 to disable the gate entirely.
        decimal minLiveVsBacktestSharpeRatio =
            Math.Max(0m, await GetConfigAsync(db, CK_MinLiveVsBacktestSharpeRatio, 0.5m, ct));

        int promoted = 0;
        int activated = 0;

        // ── Phase 1: BacktestQualified → Approved ──────────────────────────
        var observationCutoff = DateTime.UtcNow.AddDays(-minObservationDays);

        var candidates = await writeDb.Set<Strategy>()
            .Where(s => !s.IsDeleted
                     && s.Name.StartsWith("Auto-")
                     && s.LifecycleStage == StrategyLifecycleStage.BacktestQualified
                     && s.LifecycleStageEnteredAt != null
                     && s.LifecycleStageEnteredAt <= observationCutoff)
            .ToListAsync(ct);

        if (candidates.Count > 0)
        {
            var candidateIds = candidates.Select(c => c.Id).ToList();

            // Batch-load recent performance snapshots for all candidates
            var snapshotsByStrategy = await db.Set<StrategyPerformanceSnapshot>()
                .Where(s => candidateIds.Contains(s.StrategyId)
                         && !s.IsDeleted
                         && s.EvaluatedAt >= observationCutoff)
                .GroupBy(s => s.StrategyId)
                .Select(g => new
                {
                    StrategyId = g.Key,
                    Snapshots = g.OrderByDescending(s => s.EvaluatedAt)
                        .Select(s => new { s.HealthScore, s.HealthStatus, s.SharpeRatio })
                        .ToList()
                })
                .ToDictionaryAsync(g => g.StrategyId, g => g.Snapshots, ct);

            // Batch-load the best completed backtest Sharpe per candidate for the
            // live-vs-backtest ratio gate. A nullable result lets us distinguish
            // "no backtest Sharpe available" (gate not applicable) from "Sharpe == 0".
            Dictionary<long, decimal?> bestBacktestSharpeByStrategy;
            if (minLiveVsBacktestSharpeRatio > 0m)
            {
                bestBacktestSharpeByStrategy = await db.Set<BacktestRun>()
                    .Where(b => candidateIds.Contains(b.StrategyId)
                             && !b.IsDeleted
                             && b.Status == RunStatus.Completed
                             && b.SharpeRatio != null)
                    .GroupBy(b => b.StrategyId)
                    .Select(g => new
                    {
                        StrategyId = g.Key,
                        BestSharpe = g.Max(b => b.SharpeRatio)
                    })
                    .ToDictionaryAsync(g => g.StrategyId, g => g.BestSharpe, ct);
            }
            else
            {
                bestBacktestSharpeByStrategy = new Dictionary<long, decimal?>();
            }

            foreach (var strategy in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (!snapshotsByStrategy.TryGetValue(strategy.Id, out var snapshots)
                    || snapshots.Count < minHealthySnapshots)
                {
                    _metrics.PromotionGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "insufficient_snapshots"));
                    _logger.LogDebug(
                        "StrategyPromotionWorker: strategy {Id} ({Name}) has insufficient snapshots ({Count}/{Required}) — skipping",
                        strategy.Id, strategy.Name, snapshots?.Count ?? 0, minHealthySnapshots);
                    continue;
                }

                // Reject if any snapshot in the window was Critical
                if (snapshots.Any(s => s.HealthStatus == StrategyHealthStatus.Critical))
                {
                    _metrics.PromotionGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "critical_snapshot"));
                    _logger.LogDebug(
                        "StrategyPromotionWorker: strategy {Id} ({Name}) had Critical health during observation — skipping",
                        strategy.Id, strategy.Name);
                    continue;
                }

                int healthyCount = snapshots.Count(s => s.HealthScore >= minHealthScore);
                if (healthyCount < minHealthySnapshots)
                {
                    _metrics.PromotionGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "insufficient_healthy"));
                    _logger.LogDebug(
                        "StrategyPromotionWorker: strategy {Id} ({Name}) has {Healthy}/{Required} healthy snapshots (score >= {Threshold}) — skipping",
                        strategy.Id, strategy.Name, healthyCount, minHealthySnapshots, minHealthScore);
                    continue;
                }

                // ── Live-vs-backtest Sharpe ratio gate ─────────────────────
                // Only enforced when (a) the ratio threshold is configured above zero, and
                // (b) the strategy has a completed backtest with positive Sharpe to compare
                // against. A nonpositive backtest Sharpe means the gate would divide by zero
                // or compare to a negative baseline — we fall through rather than reject, so
                // strategies without usable backtest data aren't penalised.
                if (minLiveVsBacktestSharpeRatio > 0m
                    && bestBacktestSharpeByStrategy.TryGetValue(strategy.Id, out var backtestSharpeNullable)
                    && backtestSharpeNullable is decimal backtestSharpe
                    && backtestSharpe > 0m)
                {
                    decimal liveSharpe = snapshots.Average(s => s.SharpeRatio);
                    decimal ratio = liveSharpe / backtestSharpe;
                    if (ratio < minLiveVsBacktestSharpeRatio)
                    {
                        _metrics.PromotionGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "live_vs_backtest_sharpe"));
                        _logger.LogInformation(
                            "StrategyPromotionWorker: strategy {Id} ({Name}) live/backtest Sharpe ratio " +
                            "{Ratio:F2} (live={Live:F2}, backtest={Backtest:F2}) below {Min:F2} — skipping",
                            strategy.Id, strategy.Name, ratio, liveSharpe, backtestSharpe, minLiveVsBacktestSharpeRatio);
                        try
                        {
                            await mediator.Send(new LogDecisionCommand
                            {
                                EntityType   = "Strategy",
                                EntityId     = strategy.Id,
                                DecisionType = "PromotionGate",
                                Outcome      = "Rejected",
                                Reason       = $"Live/backtest Sharpe ratio {ratio:F2} " +
                                               $"(live={liveSharpe:F2}, backtest={backtestSharpe:F2}) " +
                                               $"< floor {minLiveVsBacktestSharpeRatio:F2}",
                                Source       = "StrategyPromotionWorker"
                            }, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex,
                                "StrategyPromotionWorker: audit log failed for strategy {Id} Sharpe-gate rejection (non-fatal)",
                                strategy.Id);
                        }
                        continue;
                    }
                }

                // Promote to Approved
                strategy.LifecycleStage = StrategyLifecycleStage.Approved;
                strategy.LifecycleStageEnteredAt = DateTime.UtcNow;

                // Save each promotion individually to avoid losing all progress on crash.
                await writeCtx.SaveChangesAsync(ct);
                promoted++;

                _logger.LogInformation(
                    "StrategyPromotionWorker: strategy {Id} ({Name}) promoted to Approved — " +
                    "{Healthy}/{Total} healthy snapshots, avg={Avg:F2}",
                    strategy.Id, strategy.Name, healthyCount, snapshots.Count,
                    snapshots.Average(s => s.HealthScore));

                try
                {
                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType = "Strategy", EntityId = strategy.Id,
                        DecisionType = "LifecyclePromotion",
                        Outcome = "BacktestQualified→Approved",
                        Reason = $"{healthyCount}/{snapshots.Count} snapshots healthy (score >= {minHealthScore:F2}), " +
                                 $"avg={snapshots.Average(s => s.HealthScore):F2}, " +
                                 $"observation={minObservationDays}d",
                        Source = "StrategyPromotionWorker"
                    }, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "StrategyPromotionWorker: audit log failed for strategy {Id} promotion (non-fatal)",
                        strategy.Id);
                }
            }
        }

        // ── Phase 2: Approved → Active (auto-activation) ───────────────────
        if (!autoActivateEnabled)
        {
            if (promoted > 0)
                _logger.LogInformation(
                    "StrategyPromotionWorker: promoted {Count} strategies to Approved (auto-activation disabled)",
                    promoted);
            return;
        }

        var approvedStrategies = await writeDb.Set<Strategy>()
            .Where(s => !s.IsDeleted
                     && s.Name.StartsWith("Auto-")
                     && s.LifecycleStage == StrategyLifecycleStage.Approved
                     && s.Status == StrategyStatus.Paused)
            .ToListAsync(ct);

        if (approvedStrategies.Count == 0)
        {
            if (promoted > 0)
                _logger.LogInformation(
                    "StrategyPromotionWorker: promoted {Count} strategies to Approved, none ready for activation",
                    promoted);
            return;
        }

        // Per-symbol active cohort for capacity and champion/challenger enforcement.
        var activeStrategies = await db.Set<Strategy>()
            .Where(s => !s.IsDeleted && s.Status == StrategyStatus.Active)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Symbol,
                s.ScreeningMetricsJson,
            })
            .ToListAsync(ct);
        var activeCountBySymbol = activeStrategies
            .GroupBy(s => s.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var activeStrategiesBySymbol = activeStrategies
            .GroupBy(s => s.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var strategy in approvedStrategies)
        {
            ct.ThrowIfCancellationRequested();

            int currentActive = activeCountBySymbol.GetValueOrDefault(strategy.Symbol);
            if (currentActive >= maxActivePerSymbol)
            {
                _logger.LogDebug(
                    "StrategyPromotionWorker: strategy {Id} ({Name}) skipped — {Symbol} already has {Count}/{Max} active strategies",
                    strategy.Id, strategy.Name, strategy.Symbol, currentActive, maxActivePerSymbol);
                continue;
            }

            if (championChallengerEnabled
                && activeStrategiesBySymbol.TryGetValue(strategy.Symbol, out var activeCohort)
                && !PassesChampionChallengerGate(
                    strategy,
                    activeCohort.Select(s => (s.Id, s.Name, s.ScreeningMetricsJson)).ToList(),
                    minChallengerQualityDelta,
                    minChallengerSelectionDelta,
                    out var challengerReason))
            {
                _metrics.PromotionGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "champion_challenger"));
                _logger.LogInformation(
                    "StrategyPromotionWorker: strategy {Id} ({Name}) held at Approved by champion/challenger gate — {Reason}",
                    strategy.Id,
                    strategy.Name,
                    challengerReason);
                try
                {
                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType = "Strategy",
                        EntityId = strategy.Id,
                        DecisionType = "PromotionGate",
                        Outcome = "Rejected",
                        Reason = challengerReason,
                        Source = "StrategyPromotionWorker"
                    }, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "StrategyPromotionWorker: audit log failed for strategy {Id} champion/challenger rejection (non-fatal)",
                        strategy.Id);
                }
                continue;
            }

            strategy.Status = StrategyStatus.Active;
            strategy.LifecycleStage = StrategyLifecycleStage.Active;
            strategy.LifecycleStageEnteredAt = DateTime.UtcNow;

            // Save each activation individually to reduce crash risk.
            await writeCtx.SaveChangesAsync(ct);
            activated++;
            activeCountBySymbol[strategy.Symbol] = currentActive + 1;

            _logger.LogInformation(
                "StrategyPromotionWorker: strategy {Id} ({Name}) ACTIVATED — {Symbol}/{Tf}",
                strategy.Id, strategy.Name, strategy.Symbol, strategy.Timeframe);

            try
            {
                await mediator.Send(new LogDecisionCommand
                {
                    EntityType = "Strategy", EntityId = strategy.Id,
                    DecisionType = "LifecyclePromotion",
                    Outcome = "Approved→Active",
                    Reason = "Auto-activation: strategy passed all validation gates and health observation period",
                    Source = "StrategyPromotionWorker"
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "StrategyPromotionWorker: audit log failed for strategy {Id} activation (non-fatal)",
                    strategy.Id);
            }

            // Publish activation event immediately after each save
            try
            {
                await eventService.SaveAndPublish(writeCtx, new StrategyActivatedIntegrationEvent
                {
                    StrategyId  = strategy.Id,
                    Name        = strategy.Name,
                    Symbol      = strategy.Symbol,
                    Timeframe   = strategy.Timeframe,
                    ActivatedAt = strategy.LifecycleStageEnteredAt ?? DateTime.UtcNow,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "StrategyPromotionWorker: activation event publish failed for strategy {Id} (non-fatal)",
                    strategy.Id);
            }
        }

        _logger.LogInformation(
            "StrategyPromotionWorker: cycle complete — {Promoted} promoted to Approved, {Activated} activated",
            promoted, activated);
    }

    private static async Task<T> GetConfigAsync<T>(
        DbContext ctx, string key, T defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    private static bool PassesChampionChallengerGate(
        Strategy candidate,
        IReadOnlyCollection<(long Id, string Name, string? ScreeningMetricsJson)> activeCohort,
        double minQualityDelta,
        double minSelectionDelta,
        out string reason)
    {
        var candidateMetrics = ScreeningMetrics.FromJson(candidate.ScreeningMetricsJson);
        if (candidateMetrics == null)
        {
            reason = "No candidate scorecard available; champion/challenger gate not applicable.";
            return true;
        }

        var activeMetrics = activeCohort
            .Select(s => (s.Id, s.Name, Metrics: ScreeningMetrics.FromJson(s.ScreeningMetricsJson)))
            .Where(s => s.Metrics != null && (s.Metrics.QualityScore > 0 || Math.Abs(s.Metrics.SelectionScore) > 0.0001))
            .ToList();
        if (activeMetrics.Count == 0)
        {
            reason = "No active scorecards available; champion/challenger gate not applicable.";
            return true;
        }

        double bestQuality = activeMetrics.Max(s => s.Metrics!.QualityScore);
        double bestSelection = activeMetrics.Max(s => s.Metrics!.SelectionScore);
        bool qualityPass = candidateMetrics.QualityScore > 0
            && candidateMetrics.QualityScore >= bestQuality + minQualityDelta;
        bool selectionPass = Math.Abs(candidateMetrics.SelectionScore) > 0.0001
            && candidateMetrics.SelectionScore >= bestSelection + minSelectionDelta;

        if (qualityPass || selectionPass)
        {
            reason = "Candidate beat the active cohort on quality or selection score.";
            return true;
        }

        reason =
            $"Candidate score below active champion: quality {candidateMetrics.QualityScore:F2} < {bestQuality + minQualityDelta:F2}, " +
            $"selection {candidateMetrics.SelectionScore:F2} < {bestSelection + minSelectionDelta:F2}.";
        return false;
    }
}
