using System.Diagnostics;
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
                }
                catch
                {
                    // Non-critical.
                }
            }

            var velocityCutoff = nowUtc.AddDays(-7);
            var recentAutoCount = await _cycleDataService.CountRecentAutoCandidatesAsync(db, velocityCutoff, ct);
            if (recentAutoCount >= config.MaxCandidatesPerWeek)
            {
                _logger.LogInformation(
                    "StrategyGenerationWorker: velocity cap — {Count} candidates created in last 7 days (limit {Limit}), skipping cycle",
                    recentAutoCount,
                    config.MaxCandidatesPerWeek);
                await CompleteCycleRunAsync(writeDb, writeCtx, cycleId, sw.Elapsed.TotalMilliseconds, 0, 0, 0, 0, 0, 0, 0, ct);
                return;
            }

            if (config.SeasonalBlackoutEnabled
                && _calendarPolicy.IsInBlackoutPeriod(config.BlackoutPeriods, config.BlackoutTimezone, nowUtc))
            {
                _logger.LogInformation("StrategyGenerationWorker: seasonal blackout — skipping cycle");
                await CompleteCycleRunAsync(writeDb, writeCtx, cycleId, sw.Elapsed.TotalMilliseconds, 0, 0, 0, 0, 0, 0, 0, ct);
                return;
            }

            if (config.SuppressDuringDrawdownRecovery && await _cycleDataService.IsInDrawdownRecoveryAsync(db, ct))
            {
                _logger.LogInformation("StrategyGenerationWorker: drawdown recovery — skipping cycle");
                await CompleteCycleRunAsync(writeDb, writeCtx, cycleId, sw.Elapsed.TotalMilliseconds, 0, 0, 0, 0, 0, 0, 0, ct);
                return;
            }

            var cycleData = await _cycleDataService.LoadCycleDataAsync(db, config, nowUtc, ct);
            var activePairs = cycleData.ActivePairs;
            var pairDataBySymbol = cycleData.PairDataBySymbol;

            if (activePairs.Count == 0)
            {
                _logger.LogInformation("StrategyGenerationWorker: no active currency pairs — skipping");
                await CompleteCycleRunAsync(writeDb, writeCtx, cycleId, sw.Elapsed.TotalMilliseconds, 0, 0, 0, 0, 0, 0, 0, ct);
                return;
            }

            if (config.SkipWeekends
                && _calendarPolicy.IsWeekendForAssetMix(
                    activePairs.Select(s => (s, pairDataBySymbol.GetValueOrDefault(s))),
                    nowUtc))
            {
                _logger.LogInformation("StrategyGenerationWorker: weekend — skipping cycle (non-crypto markets closed)");
                _metrics.StrategyGenWeekendSkipped.Add(1);
                await CompleteCycleRunAsync(writeDb, writeCtx, cycleId, sw.Elapsed.TotalMilliseconds, 0, 0, 0, 0, 0, 0, 0, ct);
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StrategyGenerationWorker: feedback decay — failed to record predictions");
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

            try
            {
                await eventService.SaveAndPublish(writeCtx, new StrategyGenerationCycleCompletedIntegrationEvent
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
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StrategyGenerationWorker: cycle summary event publish failed");
            }

            try
            {
                await _feedbackDecayMonitor.EvaluateAndAdjustAsync(db, writeDb, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StrategyGenerationWorker: feedback decay evaluation failed");
            }

            try
            {
                await _checkpointStore.ClearCheckpointAsync(writeDb, ct);
                await writeCtx.SaveChangesAsync(ct);
            }
            catch
            {
                // Non-critical.
            }

            await CompleteCycleRunAsync(
                writeDb,
                writeCtx,
                cycleId,
                sw.Elapsed.TotalMilliseconds,
                persisted,
                persistResult.ReservePersistedCount,
                screeningResult.CandidatesScreened,
                screeningResult.SymbolsProcessed,
                screeningResult.SymbolsSkipped,
                pruned,
                portfolioFilterRemoved,
                ct);
        }
        catch (Exception ex)
        {
            try
            {
                await _cycleRunStore.FailAsync(writeDb, cycleId, "cycle_execution", ex.Message, ct);
                await writeCtx.SaveChangesAsync(ct);
            }
            catch
            {
                // Best effort.
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
        try
        {
            await _cycleRunStore.StartAsync(writeDb, cycleId, null, ct);
            await writeCtx.SaveChangesAsync(ct);
        }
        catch
        {
            // Best effort.
        }
    }

    private async Task CompleteCycleRunAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        string cycleId,
        double durationMs,
        int persisted,
        int reservePersisted,
        int candidatesScreened,
        int symbolsProcessed,
        int symbolsSkipped,
        int pruned,
        int portfolioFilterRemoved,
        CancellationToken ct)
    {
        try
        {
            await _cycleRunStore.CompleteAsync(
                writeDb,
                cycleId,
                new StrategyGenerationCycleRunCompletion(
                    durationMs,
                    persisted,
                    reservePersisted,
                    candidatesScreened,
                    symbolsProcessed,
                    symbolsSkipped,
                    pruned,
                    portfolioFilterRemoved),
                ct);
            await writeCtx.SaveChangesAsync(ct);
        }
        catch
        {
            // Best effort.
        }
    }

}
