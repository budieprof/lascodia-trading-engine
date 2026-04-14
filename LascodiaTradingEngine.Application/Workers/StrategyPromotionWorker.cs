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
/// </list>
/// </para>
///
/// <para>
/// <b>Auto-activation (Approved → Active):</b>
/// When <c>StrategyPromotion:AutoActivateEnabled</c> is true (default), strategies that
/// reach <c>Approved</c> are automatically activated with <c>Status = Active</c>. This
/// bypasses the four-eyes approval gate in <c>ActivateStrategyCommand</c>, which remains
/// available for manually created strategies that require human sign-off.
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
                        .Select(s => new { s.HealthScore, s.HealthStatus })
                        .ToList()
                })
                .ToDictionaryAsync(g => g.StrategyId, g => g.Snapshots, ct);

            foreach (var strategy in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (!snapshotsByStrategy.TryGetValue(strategy.Id, out var snapshots)
                    || snapshots.Count < minHealthySnapshots)
                {
                    _logger.LogDebug(
                        "StrategyPromotionWorker: strategy {Id} ({Name}) has insufficient snapshots ({Count}/{Required}) — skipping",
                        strategy.Id, strategy.Name, snapshots?.Count ?? 0, minHealthySnapshots);
                    continue;
                }

                // Reject if any snapshot in the window was Critical
                if (snapshots.Any(s => s.HealthStatus == StrategyHealthStatus.Critical))
                {
                    _logger.LogDebug(
                        "StrategyPromotionWorker: strategy {Id} ({Name}) had Critical health during observation — skipping",
                        strategy.Id, strategy.Name);
                    continue;
                }

                int healthyCount = snapshots.Count(s => s.HealthScore >= minHealthScore);
                if (healthyCount < minHealthySnapshots)
                {
                    _logger.LogDebug(
                        "StrategyPromotionWorker: strategy {Id} ({Name}) has {Healthy}/{Required} healthy snapshots (score >= {Threshold}) — skipping",
                        strategy.Id, strategy.Name, healthyCount, minHealthySnapshots, minHealthScore);
                    continue;
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

        // Per-symbol active count for cap enforcement
        var activeCountBySymbol = await db.Set<Strategy>()
            .Where(s => !s.IsDeleted && s.Status == StrategyStatus.Active)
            .GroupBy(s => s.Symbol)
            .Select(g => new { Symbol = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Symbol, g => g.Count, ct);

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
}
