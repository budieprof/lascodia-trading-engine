using System.Diagnostics;
using System.Text.Json;
using MediatR;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;
using static LascodiaTradingEngine.Application.StrategyGeneration.StrategyGenerationHelpers;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationCycleRunner))]
internal sealed class StrategyGenerationCycleRunner : IStrategyGenerationCycleRunner
{
    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRegimeStrategyMapper _regimeMapper;
    private readonly TradingMetrics _metrics;
    private readonly IFeedbackDecayMonitor _feedbackDecayMonitor;
    private readonly IStrategyGenerationConfigProvider _configProvider;
    private readonly IStrategyGenerationCalendarPolicy _calendarPolicy;
    private readonly IStrategyGenerationCycleDataService _cycleDataService;
    private readonly IStrategyScreeningEngineFactory _screeningEngineFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IStrategyGenerationFeedbackCoordinator _feedbackCoordinator;
    private readonly IStrategyGenerationScreeningCoordinator _screeningCoordinator;
    private readonly IStrategyGenerationPersistenceCoordinator _persistenceCoordinator;
    private readonly IStrategyGenerationPruningCoordinator _pruningCoordinator;
    private readonly IStrategyGenerationCycleRunStore _cycleRunStore;
    private readonly IStrategyGenerationFailureStore _failureStore;
    private readonly IStrategyGenerationCheckpointStore _checkpointStore;
    private readonly IStrategyGenerationCheckpointCoordinator _checkpointCoordinator;
    private readonly IStrategyGenerationHealthStore _healthStore;

    public StrategyGenerationCycleRunner(
        ILogger<StrategyGenerationWorker> logger,
        IServiceScopeFactory scopeFactory,
        IRegimeStrategyMapper regimeMapper,
        TradingMetrics metrics,
        IFeedbackDecayMonitor feedbackDecayMonitor,
        IStrategyGenerationConfigProvider configProvider,
        IStrategyGenerationCalendarPolicy calendarPolicy,
        IStrategyGenerationCycleDataService cycleDataService,
        IStrategyScreeningEngineFactory screeningEngineFactory,
        IStrategyGenerationFeedbackCoordinator feedbackCoordinator,
        IStrategyGenerationScreeningCoordinator screeningCoordinator,
        IStrategyGenerationPersistenceCoordinator persistenceCoordinator,
        IStrategyGenerationPruningCoordinator pruningCoordinator,
        IStrategyGenerationCycleRunStore cycleRunStore,
        IStrategyGenerationFailureStore failureStore,
        IStrategyGenerationCheckpointStore checkpointStore,
        IStrategyGenerationCheckpointCoordinator checkpointCoordinator,
        IStrategyGenerationHealthStore healthStore,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _regimeMapper = regimeMapper;
        _metrics = metrics;
        _feedbackDecayMonitor = feedbackDecayMonitor;
        _configProvider = configProvider;
        _calendarPolicy = calendarPolicy;
        _cycleDataService = cycleDataService;
        _screeningEngineFactory = screeningEngineFactory;
        _feedbackCoordinator = feedbackCoordinator;
        _screeningCoordinator = screeningCoordinator;
        _persistenceCoordinator = persistenceCoordinator;
        _pruningCoordinator = pruningCoordinator;
        _cycleRunStore = cycleRunStore;
        _failureStore = failureStore;
        _checkpointStore = checkpointStore;
        _checkpointCoordinator = checkpointCoordinator;
        _healthStore = healthStore;
        _timeProvider = timeProvider;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        using var scope = _scopeFactory.CreateScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var eventService = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();
        var eventLogReader = scope.ServiceProvider.GetRequiredService<IEventLogReader>();
        var db = readCtx.GetDbContext();
        var writeDb = writeCtx.GetDbContext();
        var auditLogger = new ScreeningAuditLogger(mediator);

        var spreadProfileProvider = scope.ServiceProvider.GetService<ISpreadProfileProvider>();
        var liveBenchmark = scope.ServiceProvider.GetService<ILivePerformanceBenchmark>();
        var portfolioProvider = scope.ServiceProvider.GetService<IPortfolioEquityCurveProvider>();

        var configSnapshot = await _configProvider.LoadAsync(db, ct);
        var config = configSnapshot.Config;
        var rawConfigs = configSnapshot.RawConfigs;
        var symbolOverridesBySymbol = configSnapshot.SymbolOverridesBySymbol;
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        string cycleId = $"{nowUtc:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";

        await TryStartCycleRunAsync(writeDb, writeCtx, cycleId, ct);

        try
        {
            await ReconcilePendingSummaryDispatchesAsync(db, writeCtx, eventLogReader, ct);
            await _persistenceCoordinator.ReplayPendingPostPersistArtifactsAsync(db, writeCtx, eventService, auditLogger, ct);
            await _feedbackCoordinator.RefreshDynamicTemplatesAsync(db, ct);

            var unreportedFailures = await _failureStore.LoadUnreportedFailuresAsync(db, ct);
            if (unreportedFailures.Count > 0)
            {
                _logger.LogWarning(
                    "StrategyGenerationWorker: {Count} candidates remain unresolved from prior persistence failures: {Keys}",
                    unreportedFailures.Count,
                    string.Join("; ", unreportedFailures.Select(f => $"{f.CandidateId}:{f.FailureStage}:{f.FailureReason}")));

                try
                {
                    await _failureStore.MarkFailuresReportedAsync(
                        writeDb,
                        unreportedFailures.Select(f => f.Id).ToArray(),
                        ct);
                    await writeCtx.SaveChangesAsync(ct);
                    _healthStore.RecordPhaseSuccess("failure_report_mark", 0, _timeProvider.GetUtcNow().UtcDateTime);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "StrategyGenerationWorker: failed to mark persistence failures as reported");
                    _healthStore.RecordPhaseFailure("failure_report_mark", ex.Message, _timeProvider.GetUtcNow().UtcDateTime);
                }
            }

            await UpdateUnresolvedFailureCountAsync(db, ct);

            var velocityCutoff = nowUtc.AddDays(-7);
            var recentAutoCount = await _cycleDataService.CountRecentAutoCandidatesAsync(db, velocityCutoff, ct);
            if (recentAutoCount >= config.MaxCandidatesPerWeek)
            {
                _logger.LogInformation(
                    "StrategyGenerationWorker: velocity cap — {Count} candidates created in last 7 days (limit {Limit}), skipping cycle",
                    recentAutoCount,
                    config.MaxCandidatesPerWeek);
                RecordSkip("velocity_cap");
                await PublishCycleSummaryAsync(
                    writeDb,
                    writeCtx,
                    cycleId,
                    eventService,
                    eventLogReader,
                    new StrategyGenerationCycleRunCompletion(
                        sw.Elapsed.TotalMilliseconds,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0),
                    new StrategyGenerationCycleCompletedIntegrationEvent
                    {
                        DurationMs = sw.Elapsed.TotalMilliseconds,
                        CircuitBreakerActive = false,
                        ConsecutiveFailures = 0,
                        Skipped = true,
                        SkipReason = "velocity_cap",
                        CompletedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                    },
                    ct);
                return;
            }

            if (config.SeasonalBlackoutEnabled
                && _calendarPolicy.IsInBlackoutPeriod(config.BlackoutPeriods, config.BlackoutTimezone, nowUtc))
            {
                _logger.LogInformation("StrategyGenerationWorker: seasonal blackout — skipping cycle");
                RecordSkip("seasonal_blackout");
                await PublishCycleSummaryAsync(
                    writeDb,
                    writeCtx,
                    cycleId,
                    eventService,
                    eventLogReader,
                    new StrategyGenerationCycleRunCompletion(
                        sw.Elapsed.TotalMilliseconds,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0),
                    new StrategyGenerationCycleCompletedIntegrationEvent
                    {
                        DurationMs = sw.Elapsed.TotalMilliseconds,
                        CircuitBreakerActive = false,
                        ConsecutiveFailures = 0,
                        Skipped = true,
                        SkipReason = "seasonal_blackout",
                        CompletedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                    },
                    ct);
                return;
            }

            if (config.SuppressDuringDrawdownRecovery && await _cycleDataService.IsInDrawdownRecoveryAsync(db, ct))
            {
                _logger.LogInformation("StrategyGenerationWorker: drawdown recovery — skipping cycle");
                RecordSkip("drawdown_recovery");
                await PublishCycleSummaryAsync(
                    writeDb,
                    writeCtx,
                    cycleId,
                    eventService,
                    eventLogReader,
                    new StrategyGenerationCycleRunCompletion(
                        sw.Elapsed.TotalMilliseconds,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0),
                    new StrategyGenerationCycleCompletedIntegrationEvent
                    {
                        DurationMs = sw.Elapsed.TotalMilliseconds,
                        CircuitBreakerActive = false,
                        ConsecutiveFailures = 0,
                        Skipped = true,
                        SkipReason = "drawdown_recovery",
                        CompletedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                    },
                    ct);
                return;
            }

            var cycleData = await _cycleDataService.LoadCycleDataAsync(db, config, nowUtc, ct);
            var activePairs = cycleData.ActivePairs;
            var pairDataBySymbol = cycleData.PairDataBySymbol;

            if (activePairs.Count == 0)
            {
                _logger.LogInformation("StrategyGenerationWorker: no active currency pairs — skipping");
                RecordSkip("no_active_pairs");
                await PublishCycleSummaryAsync(
                    writeDb,
                    writeCtx,
                    cycleId,
                    eventService,
                    eventLogReader,
                    new StrategyGenerationCycleRunCompletion(
                        sw.Elapsed.TotalMilliseconds,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0),
                    new StrategyGenerationCycleCompletedIntegrationEvent
                    {
                        DurationMs = sw.Elapsed.TotalMilliseconds,
                        CircuitBreakerActive = false,
                        ConsecutiveFailures = 0,
                        Skipped = true,
                        SkipReason = "no_active_pairs",
                        CompletedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                    },
                    ct);
                return;
            }

            if (config.SkipWeekends
                && _calendarPolicy.IsWeekendForAssetMix(
                    activePairs.Select(s => (s, pairDataBySymbol.GetValueOrDefault(s))),
                    nowUtc))
            {
                _logger.LogInformation("StrategyGenerationWorker: weekend — skipping cycle (non-crypto markets closed)");
                _metrics.StrategyGenWeekendSkipped.Add(1);
                RecordSkip("weekend");
                await PublishCycleSummaryAsync(
                    writeDb,
                    writeCtx,
                    cycleId,
                    eventService,
                    eventLogReader,
                    new StrategyGenerationCycleRunCompletion(
                        sw.Elapsed.TotalMilliseconds,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0),
                    new StrategyGenerationCycleCompletedIntegrationEvent
                    {
                        DurationMs = sw.Elapsed.TotalMilliseconds,
                        CircuitBreakerActive = false,
                        ConsecutiveFailures = 0,
                        Skipped = true,
                        SkipReason = "weekend",
                        CompletedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                    },
                    ct);
                return;
            }

            var existing = cycleData.ExistingStrategies;

            var existingSet = new HashSet<CandidateCombo>(
                existing.Select(s => new CandidateCombo(s.StrategyType, s.Symbol, s.Timeframe)));

            var activeCountBySymbol = cycleData.ActiveCountBySymbol;
            config.ActiveStrategyCount = activeCountBySymbol.Values.Sum();
            foreach (var _ in cycleData.LowConfidenceSymbols)
            {
                _metrics.StrategyGenRegimeConfidenceSkipped.Add(1);
                _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "low_confidence"));
            }
            foreach (var _ in cycleData.TransitionSymbols)
                _metrics.StrategyGenRegimeTransitionSkipped.Add(1);

            var halfLifeDays = _feedbackDecayMonitor.GetEffectiveHalfLifeDays();
            var (feedbackRates, templateSurvivalRates) = await _feedbackCoordinator.LoadPerformanceFeedbackAsync(db, writeCtx, halfLifeDays, ct);

            _regimeMapper.RefreshFromFeedback(StrategyGenerationFeedbackCoordinator.AggregateFeedbackRatesForMapper(feedbackRates));

            try
            {
                await _feedbackDecayMonitor.RecordPredictionsAsync(
                    db,
                    writeDb,
                    StrategyGenerationFeedbackCoordinator.AggregateFeedbackRatesForMapper(feedbackRates),
                    ct);
                _healthStore.RecordPhaseSuccess("feedback_decay_record", 0, _timeProvider.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StrategyGenerationWorker: feedback decay — failed to record predictions");
                _healthStore.RecordPhaseFailure("feedback_decay_record", ex.Message, _timeProvider.GetUtcNow().UtcDateTime);
            }

            var adaptiveAdjustmentsByContext = config.AdaptiveThresholdsEnabled
                ? await _feedbackCoordinator.ComputeAdaptiveThresholdsAsync(db, config, ct)
                : new Dictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments>();

            _feedbackCoordinator.DetectFeedbackAdaptiveContradictions(feedbackRates, adaptiveAdjustmentsByContext);

            var correlationGroupCounts = _screeningCoordinator.BuildInitialCorrelationGroupCounts(
                existing.Where(s => s.Status == StrategyStatus.Active).Select(s => s.Symbol).ToList());

            HaircutRatios? haircuts = null;
            if (liveBenchmark != null)
            {
                try
                {
                    haircuts = await liveBenchmark.GetCachedHaircutsAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "StrategyGenerationWorker: live benchmark haircut load failed");
                    _healthStore.RecordPhaseFailure("haircut_cache_load", ex.Message, _timeProvider.GetUtcNow().UtcDateTime);
                }
            }

            if (haircuts == null || haircuts == HaircutRatios.Neutral)
            {
                if (liveBenchmark != null)
                {
                    try
                    {
                        haircuts = await liveBenchmark.ComputeBootstrappedHaircutsAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "StrategyGenerationWorker: bootstrapped haircut failed");
                        _healthStore.RecordPhaseFailure("haircut_bootstrap_load", ex.Message, _timeProvider.GetUtcNow().UtcDateTime);
                    }
                }
            }

            IReadOnlyList<(DateTime Date, decimal Equity)>? portfolioEquityCurve = null;
            if (portfolioProvider != null)
            {
                try
                {
                    portfolioEquityCurve = await portfolioProvider.GetPortfolioEquityCurveAsync(90, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "StrategyGenerationWorker: portfolio equity curve load failed");
                    _healthStore.RecordPhaseFailure("portfolio_equity_curve_load", ex.Message, _timeProvider.GetUtcNow().UtcDateTime);
                }
            }

            var screeningEngine = _screeningEngineFactory.Create(
                gate => _metrics.StrategyGenScreeningRejections.Add(1, new KeyValuePair<string, object?>("gate", gate)));
            var screeningContext = new StrategyGenerationScreeningContext
            {
                CycleId = cycleId,
                Config = config,
                RawConfigs = rawConfigs,
                SymbolOverridesBySymbol = symbolOverridesBySymbol,
                ScreeningEngine = screeningEngine,
                Existing = existing,
                ExistingSet = existingSet,
                PrunedTemplates = cycleData.PrunedTemplates,
                FullyPrunedCombos = cycleData.FullyPrunedCombos,
                ActiveCountBySymbol = activeCountBySymbol,
                RegimeBySymbol = cycleData.RegimeBySymbol,
                RegimeBySymbolTf = cycleData.RegimeBySymbolTf,
                PairDataBySymbol = pairDataBySymbol,
                FeedbackRates = feedbackRates,
                AdaptiveAdjustmentsByContext = adaptiveAdjustmentsByContext,
                CorrelationGroupCounts = correlationGroupCounts,
                RegimeBudget = new Dictionary<MarketRegimeEnum, int>(),
                ActivePairs = activePairs,
                AuditLogger = auditLogger,
                RegimeConfidenceBySymbol = cycleData.RegimeConfidenceBySymbol,
                FaultTracker = new StrategyGenerationFaultTracker(config.MaxFaultsPerStrategyType),
                TemplateSurvivalRates = templateSurvivalRates,
                RegimeTransitions = cycleData.RegimeTransitions,
                RegimeDetectedAtBySymbol = cycleData.RegimeDetectedAtBySymbol,
                TransitionSymbols = cycleData.TransitionSymbols,
                LowConfidenceSymbols = cycleData.LowConfidenceSymbols,
                Haircuts = haircuts,
                PortfolioEquityCurve = portfolioEquityCurve,
                SpreadProfileProvider = spreadProfileProvider,
            };

            await TryAttachCycleFingerprintAsync(writeDb, writeCtx, cycleId, screeningContext, ct);

            var screeningResult = await _screeningCoordinator.ScreenAllCandidatesAsync(db, writeCtx, screeningContext, ct);
            var pendingCandidates = screeningResult.Candidates;

            int portfolioFilterRemoved = 0;
            if (config.PortfolioBacktestEnabled && pendingCandidates.Count > 0)
            {
                var (survivors, portfolioDrawdown, removedCount) = StrategyScreeningEngine.RunPortfolioDrawdownFilter(
                    pendingCandidates,
                    config.MaxPortfolioDrawdownPct,
                    config.ScreeningInitialBalance,
                    config.PortfolioCorrelationWeight);
                if (removedCount > 0)
                {
                    pendingCandidates = survivors;
                    portfolioFilterRemoved = removedCount;
                    _metrics.StrategyGenPortfolioDrawdownFiltered.Add(removedCount);
                    _logger.LogInformation(
                        "StrategyGenerationWorker: portfolio filter removed {Count} (DD={DD:P1}, limit={Limit:P1})",
                        removedCount,
                        portfolioDrawdown,
                        config.MaxPortfolioDrawdownPct);
                }
            }

            if (pendingCandidates.Count == 0 && activePairs.All(symbol => !cycleData.RegimeBySymbol.ContainsKey(symbol)))
            {
                _logger.LogInformation("StrategyGenerationWorker: no symbols with fresh regime data — skipping persist");
            }

            var persistResult = await _persistenceCoordinator.PersistCandidatesAsync(
                readCtx,
                writeCtx,
                eventService,
                auditLogger,
                pendingCandidates,
                config,
                ct);
            int persisted = persistResult.PersistedCount;

            int pruned = await _pruningCoordinator.PruneStaleStrategiesAsync(
                readCtx,
                writeCtx,
                auditLogger,
                config.PruneAfterFailed,
                ct);

            sw.Stop();
            _metrics.WorkerCycleDurationMs.Record(
                sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("worker", "StrategyGenerationWorker"));

            _logger.LogInformation(
                "StrategyGenerationWorker: cycle complete — {Created} created, {Pruned} pruned in {Duration:F0}ms",
                persisted,
                pruned,
                sw.Elapsed.TotalMilliseconds);

            var previousCompletedCycle = await _cycleRunStore.LoadPreviousCompletedAsync(db, cycleId, ct);
            if (previousCompletedCycle != null)
            {
                int candidateDelta = persisted - previousCompletedCycle.CandidatesCreated;
                int prunedDelta = pruned - previousCompletedCycle.StrategiesPruned;
                _logger.LogInformation(
                    "StrategyGenerationWorker: cycle deltas vs previous — candidates: {CDelta:+#;-#;0}, pruned: {PDelta:+#;-#;0}",
                    candidateDelta,
                    prunedDelta);
            }

            await PublishCycleSummaryAsync(
                writeDb,
                writeCtx,
                cycleId,
                eventService,
                eventLogReader,
                new StrategyGenerationCycleRunCompletion(
                    sw.Elapsed.TotalMilliseconds,
                    persisted,
                    persistResult.ReservePersistedCount,
                    screeningResult.CandidatesScreened,
                    screeningResult.SymbolsProcessed,
                    screeningResult.SymbolsSkipped,
                    pruned,
                    portfolioFilterRemoved),
                new StrategyGenerationCycleCompletedIntegrationEvent
                {
                    SymbolsProcessed = screeningResult.SymbolsProcessed,
                    CandidatesCreated = persisted,
                    ReserveCandidatesCreated = persistResult.ReservePersistedCount,
                    CandidatesScreened = screeningResult.CandidatesScreened,
                    StrategiesPruned = pruned,
                    PortfolioFilterRemoved = portfolioFilterRemoved,
                    SymbolsSkipped = screeningResult.SymbolsSkipped,
                    DurationMs = sw.Elapsed.TotalMilliseconds,
                    CircuitBreakerActive = false,
                    ConsecutiveFailures = 0,
                    Skipped = false,
                    CompletedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                },
                ct);

            try
            {
                await _feedbackDecayMonitor.EvaluateAndAdjustAsync(db, writeDb, ct);
                _healthStore.RecordPhaseSuccess("feedback_decay_evaluate", 0, _timeProvider.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StrategyGenerationWorker: feedback decay evaluation failed");
                _healthStore.RecordPhaseFailure("feedback_decay_evaluate", ex.Message, _timeProvider.GetUtcNow().UtcDateTime);
            }

            try
            {
                await _checkpointStore.ClearCheckpointAsync(writeDb, ct);
                await writeCtx.SaveChangesAsync(ct);
                var clearNowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                _healthStore.UpdateState(state => state with
                {
                    LastCheckpointClearedAtUtc = clearNowUtc,
                    LastCheckpointClearFailureAtUtc = null,
                    LastCheckpointClearFailureMessage = null,
                    CapturedAtUtc = clearNowUtc,
                });
                _healthStore.RecordPhaseSuccess("checkpoint_clear", 0, clearNowUtc);
            }
            catch (Exception ex)
            {
                var clearNowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                _logger.LogWarning(ex, "StrategyGenerationWorker: failed to clear checkpoint state");
                _healthStore.UpdateState(state => state with
                {
                    CheckpointClearFailures = state.CheckpointClearFailures + 1,
                    LastCheckpointClearFailureAtUtc = clearNowUtc,
                    LastCheckpointClearFailureMessage = TruncateMessage(ex.Message),
                    CapturedAtUtc = clearNowUtc,
                });
                _healthStore.RecordPhaseFailure("checkpoint_clear", ex.Message, clearNowUtc);
            }

            ClearSkipReason();

        }
        catch (Exception ex)
        {
            try
            {
                await _cycleRunStore.FailAsync(writeDb, cycleId, "cycle_execution", ex.Message, ct);
                await writeCtx.SaveChangesAsync(ct);
                _healthStore.RecordPhaseSuccess("cycle_run_fail", 0, _timeProvider.GetUtcNow().UtcDateTime);
            }
            catch (Exception failEx)
            {
                _logger.LogWarning(failEx, "StrategyGenerationWorker: failed to persist cycle failure state");
                _healthStore.RecordPhaseFailure("cycle_run_fail", failEx.Message, _timeProvider.GetUtcNow().UtcDateTime);
            }

            throw;
        }
    }

    private async Task TryStartCycleRunAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        string cycleId,
        CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            await _cycleRunStore.StartAsync(writeDb, cycleId, null, ct);
            await writeCtx.SaveChangesAsync(ct);
            _healthStore.RecordPhaseSuccess(
                "cycle_run_start",
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                _timeProvider.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: failed to start cycle-run tracking for {CycleId}", cycleId);
            _healthStore.RecordPhaseFailure("cycle_run_start", ex.Message, _timeProvider.GetUtcNow().UtcDateTime);
        }
    }

    private async Task TryAttachCycleFingerprintAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        string cycleId,
        StrategyGenerationScreeningContext screeningContext,
        CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            string fingerprint = _checkpointCoordinator.ComputeFingerprint(screeningContext);
            await _cycleRunStore.AttachFingerprintAsync(writeDb, cycleId, fingerprint, ct);
            await writeCtx.SaveChangesAsync(ct);
            _healthStore.RecordPhaseSuccess(
                "cycle_fingerprint_attach",
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                _timeProvider.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: failed to attach cycle fingerprint for {CycleId}", cycleId);
            _healthStore.RecordPhaseFailure("cycle_fingerprint_attach", ex.Message, _timeProvider.GetUtcNow().UtcDateTime);
        }
    }

    private async Task PublishCycleSummaryAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        string cycleId,
        IIntegrationEventService eventService,
        IEventLogReader eventLogReader,
        StrategyGenerationCycleRunCompletion completion,
        StrategyGenerationCycleCompletedIntegrationEvent evt,
        CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        string payloadJson = JsonSerializer.Serialize(evt);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            await _cycleRunStore.StageCompletionAsync(
                writeDb,
                cycleId,
                completion,
                ct);
            await _cycleRunStore.StageSummaryDispatchAttemptAsync(
                writeDb,
                cycleId,
                evt.Id,
                payloadJson,
                nowUtc,
                ct);
            await eventService.SaveAndPublish(writeCtx, evt);

            var statuses = await eventLogReader.GetEventStatusSnapshotsAsync([evt.Id], ct);
            if (statuses.TryGetValue(evt.Id, out var status)
                && status.State == Lascodia.Trading.Engine.IntegrationEventLogEF.EventStateEnum.Published)
            {
                await _cycleRunStore.MarkSummaryDispatchPublishedAsync(writeDb, cycleId, nowUtc, ct);
                await writeCtx.SaveChangesAsync(ct);
                _healthStore.UpdateState(state => state with
                {
                    PendingSummaryDispatches = Math.Max(0, state.PendingSummaryDispatches - 1),
                    LastSummaryPublishedAtUtc = nowUtc,
                    LastSummaryPublishFailureAtUtc = null,
                    LastSummaryPublishFailureMessage = null,
                    CapturedAtUtc = nowUtc,
                });
                _healthStore.RecordPhaseSuccess(
                    "cycle_run_complete",
                    (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    nowUtc);
                _healthStore.RecordPhaseSuccess(
                    "cycle_summary_publish",
                    (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    nowUtc);
                return;
            }

            var publishFailure = new InvalidOperationException(BuildSummaryDispatchStateMessage(evt.Id, statuses));
            await TryRecordCycleSummaryFailureAsync(writeDb, writeCtx, cycleId, evt.Id, payloadJson, publishFailure, nowUtc, ct);
            _healthStore.UpdateState(state => state with
            {
                PendingSummaryDispatches = state.PendingSummaryDispatches + 1,
                SummaryPublishFailures = state.SummaryPublishFailures + 1,
                LastSummaryPublishFailureAtUtc = nowUtc,
                LastSummaryPublishFailureMessage = TruncateMessage(publishFailure.Message),
                CapturedAtUtc = nowUtc,
            });
            _healthStore.RecordPhaseFailure("cycle_summary_publish", publishFailure.Message, nowUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: cycle summary event publish failed");
            try
            {
                await _cycleRunStore.StageCompletionAsync(writeDb, cycleId, completion, ct);
            }
            catch (Exception completeEx)
            {
                _logger.LogWarning(completeEx, "StrategyGenerationWorker: failed to stage cycle completion after summary publish failure");
                _healthStore.RecordPhaseFailure("cycle_run_complete", completeEx.Message, _timeProvider.GetUtcNow().UtcDateTime);
            }
            await TryRecordCycleSummaryFailureAsync(writeDb, writeCtx, cycleId, evt.Id, payloadJson, ex, nowUtc, ct);
            _healthStore.UpdateState(state => state with
            {
                PendingSummaryDispatches = state.PendingSummaryDispatches + 1,
                SummaryPublishFailures = state.SummaryPublishFailures + 1,
                LastSummaryPublishFailureAtUtc = nowUtc,
                LastSummaryPublishFailureMessage = TruncateMessage(ex.Message),
                CapturedAtUtc = nowUtc,
            });
            _healthStore.RecordPhaseFailure("cycle_summary_publish", ex.Message, nowUtc);
        }
    }

    private async Task ReconcilePendingSummaryDispatchesAsync(
        DbContext readDb,
        IWriteApplicationDbContext writeCtx,
        IEventLogReader eventLogReader,
        CancellationToken ct)
    {
        var pendingDispatches = await _cycleRunStore.LoadPendingSummaryDispatchesAsync(readDb, ct);
        if (pendingDispatches.Count == 0)
        {
            _healthStore.UpdateState(state => state with
            {
                PendingSummaryDispatches = 0,
                CapturedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            });
            return;
        }

        var statuses = await eventLogReader.GetEventStatusSnapshotsAsync(
            pendingDispatches.Select(d => d.EventId).ToArray(),
            ct);
        int remaining = 0;
        foreach (var pendingDispatch in pendingDispatches)
        {
            if (statuses.TryGetValue(pendingDispatch.EventId, out var status)
                && status.State == Lascodia.Trading.Engine.IntegrationEventLogEF.EventStateEnum.Published)
            {
                await _cycleRunStore.MarkSummaryDispatchPublishedAsync(
                    writeCtx.GetDbContext(),
                    pendingDispatch.CycleId,
                    _timeProvider.GetUtcNow().UtcDateTime,
                    ct);
                _healthStore.UpdateState(state => state with
                {
                    LastSummaryPublishedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                    LastSummaryPublishFailureAtUtc = null,
                    LastSummaryPublishFailureMessage = null,
                    CapturedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                });
            }
            else
            {
                remaining++;
            }
        }

        await writeCtx.SaveChangesAsync(ct);
        _healthStore.UpdateState(state => state with
        {
            PendingSummaryDispatches = remaining,
            CapturedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
        });
    }

    private static string BuildSummaryDispatchStateMessage(
        Guid eventId,
        IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot> statuses)
    {
        if (!statuses.TryGetValue(eventId, out var status))
            return $"Cycle summary event {eventId} is missing from the integration event log.";

        return $"Cycle summary event {eventId} is awaiting outbox publication (state={status.State}, attempts={status.TimesSent}).";
    }

    private async Task TryRecordCycleSummaryFailureAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        string cycleId,
        Guid eventId,
        string payloadJson,
        Exception failure,
        DateTime failedAtUtc,
        CancellationToken ct)
    {
        try
        {
            await _cycleRunStore.RecordSummaryDispatchFailureAsync(
                writeDb,
                cycleId,
                eventId,
                payloadJson,
                failure.Message,
                failedAtUtc,
                ct);
            await writeCtx.SaveChangesAsync(ct);
            _healthStore.RecordPhaseSuccess("cycle_run_complete", 0, failedAtUtc);
        }
        catch (Exception recordEx)
        {
            _logger.LogWarning(recordEx, "StrategyGenerationWorker: failed to persist cycle summary dispatch failure state");
            _healthStore.RecordPhaseFailure("cycle_summary_failure_record", recordEx.Message, _timeProvider.GetUtcNow().UtcDateTime);
        }
    }

    private async Task UpdateUnresolvedFailureCountAsync(DbContext db, CancellationToken ct)
    {
        try
        {
            int unresolvedFailures = await db.Set<StrategyGenerationFailure>()
                .AsNoTracking()
                .CountAsync(f => !f.IsDeleted && f.ResolvedAtUtc == null, ct);
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            _healthStore.UpdateState(state => state with
            {
                UnresolvedFailures = unresolvedFailures,
                CapturedAtUtc = nowUtc,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StrategyGenerationWorker: failed to update unresolved failure health snapshot");
            _healthStore.RecordPhaseFailure("unresolved_failure_count", ex.Message, _timeProvider.GetUtcNow().UtcDateTime);
        }
    }

    private void RecordSkip(string reason)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        _healthStore.UpdateState(state => state with
        {
            LastSkipReason = reason,
            LastSkippedAtUtc = nowUtc,
            CapturedAtUtc = nowUtc,
        });
    }

    private void ClearSkipReason()
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        _healthStore.UpdateState(state => state with
        {
            LastSkipReason = null,
            CapturedAtUtc = nowUtc,
        });
    }

    private static string TruncateMessage(string message)
        => message.Length <= 500 ? message : message[..500];

}
