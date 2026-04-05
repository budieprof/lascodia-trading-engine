using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background worker that drives the parameter optimisation pipeline. Polls for queued
/// <see cref="OptimizationRun"/> records and performs Bayesian optimization (TPE) with
/// purged K-fold cross-validation, multi-objective Pareto selection, parameter sensitivity
/// analysis, bootstrap confidence intervals, transaction cost stress testing, and
/// regime-conditional parameter storage.
///
/// <b>Phase 1 (TPE exploration):</b> Tree-structured Parzen Estimator proposes candidates
/// guided by a surrogate model. Each candidate evaluated via purged K-fold CV with embargo
/// gaps. Latin Hypercube Sampling seeds the initial batch.
///
/// <b>Phase 2 (Pareto selection + fine validation):</b> Multi-objective non-dominated
/// sorting (Sharpe, drawdown, win rate) selects Pareto-optimal candidates. Survivors
/// re-evaluated on full IS data.
///
/// <b>Post-selection validation:</b> Parameter sensitivity analysis (±10% perturbation),
/// OOS validation with bootstrap 95% CI, 2x transaction cost stress test, walk-forward
/// stability on OOS-only data, temporal signal correlation check.
///
/// <b>Health score formula (5-factor):</b>
/// <c>0.25*WinRate + 0.20*min(1,PF/2) + 0.20*max(0,1-DD/20) + 0.15*min(1,max(0,Sharpe)/2) + 0.20*min(1,Trades/50)</c>
///
/// <b>Auto-approval:</b> OOS improvement >= configured threshold AND absolute OOS score
/// >= configured minimum AND walk-forward stable AND higher TF regime compatible AND
/// winner params not too similar to another active strategy (correlation guard).
///
/// <b>Configuration (via EngineConfig, hot-reloadable):</b>
/// <list type="bullet">
///   <item><c>Optimization:SchedulePollSeconds</c> — auto-scheduling interval (default 7200)</item>
///   <item><c>Optimization:CooldownDays</c> — min days between optimisations per strategy (default 14)</item>
///   <item><c>Optimization:MaxQueuedPerCycle</c> — max auto-scheduled per scan (default 3)</item>
///   <item><c>Optimization:AutoScheduleEnabled</c> — master switch for auto-scheduling (default true)</item>
///   <item><c>Optimization:AutoApprovalImprovementThreshold</c> — min OOS improvement for auto-approval (default 0.10)</item>
///   <item><c>Optimization:AutoApprovalMinHealthScore</c> — min absolute OOS score for auto-approval (default 0.55)</item>
///   <item><c>Optimization:TopNCandidates</c> — survivors from coarse phase (default 5)</item>
///   <item><c>Optimization:CoarsePhaseThreshold</c> — min candidates to use two-phase flow (default 10)</item>
///   <item><c>Optimization:ScreeningTimeoutSeconds</c> — per-backtest timeout (default 30)</item>
///   <item><c>Optimization:ScreeningSpreadPoints</c> — spread in broker points (default 20)</item>
///   <item><c>Optimization:ScreeningCommissionPerLot</c> — round-trip commission (default 7.0)</item>
///   <item><c>Optimization:ScreeningSlippagePips</c> — slippage in pips (default 1.0)</item>
///   <item><c>Optimization:MaxOosDegradationPct</c> — max IS-to-OOS metric drop (default 0.60)</item>
///   <item><c>Optimization:SuppressDuringDrawdownRecovery</c> — skip during recovery (default true)</item>
///   <item><c>Optimization:SeasonalBlackoutEnabled</c> — skip during thin-liquidity periods (default true)</item>
///   <item><c>Optimization:BlackoutPeriods</c> — comma-separated MM/DD-MM/DD ranges (default "12/20-01/05")</item>
///   <item><c>Optimization:MaxRunTimeoutMinutes</c> — aggregate timeout for entire run (default 30)</item>
///   <item><c>Optimization:MaxParallelBacktests</c> — max concurrent backtest evaluations (default 4)</item>
///   <item><c>Optimization:MinCandidateTrades</c> — min trades for a candidate to qualify (default 10)</item>
///   <item><c>Optimization:EmbargoRatio</c> — fraction of candles to skip at IS/OOS boundary (default 0.05)</item>
///   <item><c>Optimization:CorrelationParamThreshold</c> — max param similarity to existing active strategy (default 0.15)</item>
///   <item><c>Optimization:TpeBudget</c> — total TPE evaluation budget per run (default 50)</item>
///   <item><c>Optimization:TpeInitialSamples</c> — initial Latin Hypercube samples before TPE surrogate kicks in (default 15)</item>
///   <item><c>Optimization:PurgedKFolds</c> — K-fold count for IS candidate evaluation with embargo (default 5)</item>
///   <item><c>Optimization:SensitivityPerturbPct</c> — perturbation % for parameter sensitivity check (default 0.10)</item>
///   <item><c>Optimization:BootstrapIterations</c> — bootstrap resampling iterations for OOS CI (default 1000)</item>
///   <item><c>Optimization:MinBootstrapCILower</c> — min 95% CI lower bound for auto-approval (default 0.40)</item>
///   <item><c>Optimization:CostSensitivityEnabled</c> — enable 2x transaction cost stress test (default true)</item>
///   <item><c>Optimization:AdaptiveBoundsEnabled</c> — narrow TPE bounds from historical approvals (default true)</item>
///   <item><c>Optimization:TemporalOverlapThreshold</c> — max temporal signal overlap with other strategies (default 0.70)</item>
///   <item><c>Optimization:DataScarcityThreshold</c> — candle count below which expanding-window protocol is used (default 200)</item>
///   <item><c>Optimization:ScreeningInitialBalance</c> — initial balance for backtest/bootstrap/permutation tests (default 10000)</item>
///   <item><c>Optimization:PortfolioCorrelationThreshold</c> — max daily PnL correlation with other active strategies (default 0.80)</item>
///   <item><c>Optimization:MaxConsecutiveFailuresBeforeEscalation</c> — consecutive auto-approval failures before alert escalation (default 3)</item>
///   <item><c>Optimization:CheckpointEveryN</c> — persist intermediate results every N evaluations for crash recovery (default 10)</item>
///   <item><c>Optimization:GpEarlyStopPatience</c> — stagnant batches before early stop when using GP surrogate (default 4)</item>
///   <item><c>Optimization:SensitivityDegradationTolerance</c> — max fractional score drop before a perturbation is flagged (default 0.20)</item>
///   <item><c>Optimization:WalkForwardMinMaxRatio</c> — min(score)/max(score) threshold for walk-forward stability (default 0.50)</item>
///   <item><c>Optimization:CostStressMultiplier</c> — multiplier applied to spread/commission/slippage in cost stress test (default 2.0)</item>
///   <item><c>Optimization:MinOosCandlesForValidation</c> — minimum OOS candles for full validation (default 50)</item>
///   <item><c>Optimization:MaxCvCoefficientOfVariation</c> — max CV across K-fold scores for consistency (default 0.50)</item>
///   <item><c>Optimization:PermutationIterations</c> — Monte Carlo permutation test iterations (default 1000)</item>
///   <item><c>Optimization:MaxRetryAttempts</c> — max retry attempts for transiently failed runs (default 2)</item>
///   <item><c>Optimization:CandleLookbackMonths</c> — months of candle history to load (default 6; D1 strategies may need 12+)</item>
///   <item><c>Optimization:CandleLookbackAutoScale</c> — auto-scale lookback by timeframe (D1=24mo, H4=12mo, H1=6mo, M15=3mo, M5/M1=2mo); when false, CandleLookbackMonths is used as-is (default true)</item>
///   <item><c>Optimization:RequireEADataAvailability</c> — defer runs when no active EA feeds the symbol (default true)</item>
///   <item><c>Optimization:MaxConcurrentRuns</c> — max optimization runs executing simultaneously across all workers (default 3)</item>
///   <item><c>Optimization:UseSymbolSpecificSpread</c> — use CurrencyPair.SpreadPoints instead of fixed config spread (default true)</item>
///   <item><c>Optimization:RegimeBlendRatio</c> — fraction of non-regime candles blended into regime-filtered data (default 0.20)</item>
///   <item><c>Optimization:CpcvNFolds</c> — number of temporal folds for CPCV (default 6)</item>
///   <item><c>Optimization:CpcvTestFoldCount</c> — number of folds held out as OOS per CPCV combination (default 2)</item>
///   <item><c>Optimization:CpcvMaxCombinations</c> — max C(N,K) combinations to evaluate (default 15)</item>
///   <item><c>Optimization:CircuitBreakerThreshold</c> — consecutive backtest failures before aborting run (default 10)</item>
///   <item><c>Optimization:SuccessiveHalvingRungs</c> — comma-separated fidelity levels for multi-rung screening, e.g. "0.25,0.50" or "0.125,0.25,0.50" (default "0.25,0.50")</item>
///   <item><c>Backtest:Gate:MinWinRate</c> — auto-scheduling gate (default 0.60)</item>
///   <item><c>Backtest:Gate:MinProfitFactor</c> — auto-scheduling gate (default 1.0)</item>
///   <item><c>Backtest:Gate:MinTotalTrades</c> — auto-scheduling gate (default 10)</item>
/// </list>
/// <b>Authoritative config source:</b> <see cref="LoadConfigurationAsync"/> — this doc list may
/// lag behind the code; always consult the loader method for the definitive parameter list.
/// </summary>
public partial class OptimizationWorker : BackgroundService
{
    // ── Inner types ─────────────────────────────────────────────────────────

    /// <summary>All hot-reloadable configuration for a single optimisation cycle.</summary>
    internal sealed record OptimizationConfig
    {
        // Scheduling
        public required int SchedulePollSeconds { get; init; }
        public required int CooldownDays { get; init; }
        public required int MaxQueuedPerCycle { get; init; }
        public required bool AutoScheduleEnabled { get; init; }
        public required int MaxRunsPerWeek { get; init; }

        // Performance gates
        public required double MinWinRate { get; init; }
        public required double MinProfitFactor { get; init; }
        public required int MinTotalTrades { get; init; }

        // Approval thresholds
        public required decimal AutoApprovalImprovementThreshold { get; init; }
        public required decimal AutoApprovalMinHealthScore { get; init; }

        // Search
        public required int TopNCandidates { get; init; }
        public required int CoarsePhaseThreshold { get; init; }
        public required int TpeBudget { get; init; }
        public required int TpeInitialSamples { get; init; }
        public required int PurgedKFolds { get; init; }
        public required bool AdaptiveBoundsEnabled { get; init; }
        public required int GpEarlyStopPatience { get; init; }
        public required string PresetName { get; init; }
        public required bool HyperbandEnabled { get; init; }
        public required int HyperbandEta { get; init; }
        public required bool UseEhviAcquisition { get; init; }
        public required bool UseParegoScalarization { get; init; }

        // Screening / backtesting
        public required int ScreeningTimeoutSeconds { get; init; }
        public required double ScreeningSpreadPoints { get; init; }
        public required double ScreeningCommissionPerLot { get; init; }
        public required double ScreeningSlippagePips { get; init; }
        public required decimal ScreeningInitialBalance { get; init; }
        public required int MaxParallelBacktests { get; init; }
        public required int MinCandidateTrades { get; init; }
        public required int MaxRunTimeoutMinutes { get; init; }
        public required int CircuitBreakerThreshold { get; init; }
        public required string SuccessiveHalvingRungs { get; init; }

        // Validation gates
        public required double MaxOosDegradationPct { get; init; }
        public required double EmbargoRatio { get; init; }
        public required double CorrelationParamThreshold { get; init; }
        public required double SensitivityPerturbPct { get; init; }
        public required double SensitivityDegradationTolerance { get; init; }
        public required int BootstrapIterations { get; init; }
        public required decimal MinBootstrapCILower { get; init; }
        public required bool CostSensitivityEnabled { get; init; }
        public required double CostStressMultiplier { get; init; }
        public required double TemporalOverlapThreshold { get; init; }
        public required double PortfolioCorrelationThreshold { get; init; }
        public required double WalkForwardMinMaxRatio { get; init; }
        public required int MinOosCandlesForValidation { get; init; }
        public required double MaxCvCoefficientOfVariation { get; init; }
        public required int PermutationIterations { get; init; }
        public required double MinEquityCurveR2 { get; init; }
        public required double MaxTradeTimeConcentration { get; init; }

        // CPCV
        public required int CpcvNFolds { get; init; }
        public required int CpcvTestFoldCount { get; init; }
        public required int CpcvMaxCombinations { get; init; }

        // Data loading
        public required int DataScarcityThreshold { get; init; }
        public required int CandleLookbackMonths { get; init; }
        public required bool CandleLookbackAutoScale { get; init; }
        public required bool UseSymbolSpecificSpread { get; init; }
        public required double RegimeBlendRatio { get; init; }
        public required int MaxCrossRegimeEvals { get; init; }
        public required int RegimeStabilityHours { get; init; }

        // Suppression / deferral
        public required bool SuppressDuringDrawdownRecovery { get; init; }
        public required bool SeasonalBlackoutEnabled { get; init; }
        public required string BlackoutPeriods { get; init; }
        public required bool RequireEADataAvailability { get; init; }

        // Retry / escalation
        public required int MaxRetryAttempts { get; init; }
        public required int MaxConsecutiveFailuresBeforeEscalation { get; init; }
        public required int CheckpointEveryN { get; init; }
        public required int MaxConcurrentRuns { get; init; }
    }

    /// <summary>
    /// A scored parameter candidate from the screening phases.
    /// Check <see cref="TradesTrimmed"/> before accessing <c>Result.Trades</c> —
    /// trade lists are cleared from low-scoring candidates to reduce heap pressure.
    /// </summary>
    internal sealed record ScoredCandidate(
        string ParamsJson,
        decimal HealthScore,
        BacktestResult Result,
        double CvCoefficientOfVariation = 0.0)
    {
        /// <summary>True after trade lists have been cleared to reduce memory pressure.</summary>
        public bool TradesTrimmed { get; set; }
    }

    private sealed record OptimizationConfigSnapshot(
        int Version,
        OptimizationConfig Config);

    private sealed record RunMetadataSnapshot(
        int Version,
        int DeterministicSeed,
        string Surrogate,
        string Symbol,
        Timeframe Timeframe,
        DateTime CandleFromUtc,
        DateTime CandleToUtc,
        int CandleCount,
        int TrainCandles,
        int TestCandles,
        int EmbargoCandles,
        bool ResumedFromCheckpoint,
        string? CurrentRegime,
        int WarmStartedObservations,
        int Iterations = 0,
        decimal? BaselineHealthScore = null,
        decimal? BaselineComparisonScore = null,
        decimal? OosHealthScore = null,
        bool? AutoApproved = null);

    /// <summary>Structured result from validating a single Pareto candidate through all gates.</summary>
    internal sealed record CandidateValidationResult(
        bool Passed,
        ScoredCandidate Winner,
        decimal OosHealthScore,
        BacktestResult OosResult,
        decimal CILower, decimal CIMedian, decimal CIUpper,
        double PermPValue, double PermCorrectedAlpha, bool PermSignificant,
        bool SensitivityOk, string SensitivityReport,
        bool CostSensitiveOk, decimal PessimisticScore,
        bool DegradationFailed,
        decimal WfAvgScore, bool WfStable,
        bool MtfCompatible,
        bool CorrelationSafe,
        bool TemporalCorrelationSafe, double TemporalMaxOverlap,
        bool PortfolioCorrelationSafe, double PortfolioMaxCorrelation,
        bool CvConsistent, double CvValue,
        string ApprovalReportJson,
        string FailureReason,
        IReadOnlyList<(int Rank, string Params, string Reason, decimal Score)>? FailedCandidates = null);

    /// <summary>Results from the data loading + baseline evaluation phase.</summary>
    internal sealed record DataLoadResult(
        Strategy Strategy,
        List<Candle> AllCandles,
        List<Candle> TrainCandles,
        List<Candle> TestCandles,
        int EmbargoSize,
        BacktestOptions ScreeningOptions,
        OptimizationGridBuilder.DataProtocol Protocol,
        DateTime CandleLookbackStart,
        MarketRegimeEnum? CurrentRegimeForBaseline,
        decimal BaselineComparisonScore,
        string BaselineParametersJson,
        CurrencyPair? PairInfo = null);

    /// <summary>Results from the Bayesian search phase.</summary>
    internal sealed record SearchResult(
        List<ScoredCandidate> EvaluatedCandidates,
        int TotalIterations,
        string SurrogateKind,
        int WarmStartedObservations,
        bool ResumedFromCheckpoint);

    /// <summary>Bundles DI services and tokens shared across optimization stages.</summary>
    internal sealed record RunContext(
        OptimizationRun Run,
        Strategy Strategy,
        OptimizationConfig Config,
        decimal BaselineComparisonScore,
        DbContext Db,
        DbContext WriteDb,
        IWriteApplicationDbContext WriteCtx,
        IMediator Mediator,
        IAlertDispatcher AlertDispatcher,
        IIntegrationEventService EventService,
        CancellationToken Ct,
        CancellationToken RunCt);

    internal static bool ShouldPreservePersistedResult(bool completionPersisted, OptimizationRunStatus status)
        => completionPersisted
        && status is OptimizationRunStatus.Completed
                 or OptimizationRunStatus.Approved
                 or OptimizationRunStatus.Rejected;

    // ── Fields ──────────────────────────────────────────────────────────────

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExecutionLeaseDuration = TimeSpan.FromMinutes(10);
    private const int ConfigSnapshotVersion = 1;
    private const int RunMetadataVersion = 1;

    private readonly ILogger<OptimizationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBacktestEngine _backtestEngine;
    private readonly TradingMetrics _metrics;
    private readonly OptimizationGridBuilder _gridBuilder;
    private readonly OptimizationValidator _validator;

    private DateTime _nextScheduleScanUtc = DateTime.MinValue;

    // ── Constructor ─────────────────────────────────────────────────────────

    public OptimizationWorker(
        ILogger<OptimizationWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBacktestEngine backtestEngine,
        TradingMetrics metrics)
    {
        _logger         = logger;
        _scopeFactory   = scopeFactory;
        _backtestEngine = backtestEngine;
        _metrics        = metrics;
        _gridBuilder    = new OptimizationGridBuilder(logger);
        _validator      = new OptimizationValidator(backtestEngine);
    }

    // ── Main loop ───────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OptimizationWorker starting");

        // Crash recovery: reclaim any runs left in Running state from a prior crash.
        // These runs lost their in-memory state, so re-queue them for a fresh attempt.
        try
        {
            await using var recoveryScope = _scopeFactory.CreateAsyncScope();
            var recoveryWriteCtx = recoveryScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var recoveryDb       = recoveryWriteCtx.GetDbContext();
            var nowUtc           = DateTime.UtcNow;

            // Only re-queue runs whose strategy still exists (not deleted)
            var activeStrategyIds = await recoveryDb.Set<Strategy>()
                .Where(s => !s.IsDeleted)
                .Select(s => s.Id)
                .ToListAsync(stoppingToken);
            var activeStrategySet = new HashSet<long>(activeStrategyIds);

            int recovered = await recoveryDb.Set<OptimizationRun>()
                .Where(r => r.Status == OptimizationRunStatus.Running && !r.IsDeleted
                          && (r.ExecutionLeaseExpiresAt == null || r.ExecutionLeaseExpiresAt < nowUtc)
                          && activeStrategySet.Contains(r.StrategyId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, OptimizationRunStatus.Queued)
                    .SetProperty(r => r.StartedAt, nowUtc)
                    .SetProperty(r => r.FailureCategory, (OptimizationFailureCategory?)null)
                    .SetProperty(r => r.DeferredUntilUtc, (DateTime?)null)
                    .SetProperty(r => r.ExecutionLeaseExpiresAt, (DateTime?)null), stoppingToken);

            // Mark orphaned runs (deleted strategy) as failed instead of re-queuing
            int orphaned = await recoveryDb.Set<OptimizationRun>()
                .Where(r => r.Status == OptimizationRunStatus.Running && !r.IsDeleted
                          && (r.ExecutionLeaseExpiresAt == null || r.ExecutionLeaseExpiresAt < nowUtc)
                          && !activeStrategySet.Contains(r.StrategyId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, OptimizationRunStatus.Failed)
                    .SetProperty(r => r.FailureCategory, OptimizationFailureCategory.StrategyRemoved)
                    .SetProperty(r => r.ErrorMessage, "Strategy deleted during optimization run")
                    .SetProperty(r => r.CompletedAt, nowUtc), stoppingToken);

            if (orphaned > 0)
                _logger.LogWarning(
                    "OptimizationWorker: marked {Count} orphaned Running run(s) as Failed (strategy deleted)",
                    orphaned);

            if (recovered > 0)
            {
                _metrics.OptimizationLeaseReclaims.Add(recovered);
                _logger.LogWarning(
                    "OptimizationWorker: recovered {Count} stale Running run(s) from prior crash — re-queued",
                    recovered);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OptimizationWorker: crash recovery check failed (non-fatal)");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RequeueExpiredRunningRunsAsync(stoppingToken);
                await RetryFailedRunsAsync(stoppingToken);
                await MonitorFollowUpResultsAsync(stoppingToken);

                if (DateTime.UtcNow >= _nextScheduleScanUtc)
                {
                    await using var schedScope = _scopeFactory.CreateAsyncScope();
                    var readCtx  = schedScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                    var writeCtx = schedScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var db       = readCtx.GetDbContext();

                    var config = await LoadConfigurationAsync(db, stoppingToken);
                    _nextScheduleScanUtc = DateTime.UtcNow.AddSeconds(config.SchedulePollSeconds);

                    if (config.AutoScheduleEnabled)
                    {
                        await AutoScheduleUnderperformersAsync(readCtx, writeCtx, config, stoppingToken);
                    }
                }

                await ProcessNextQueuedRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OptimizationWorker: unexpected error in polling loop");
                _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", "OptimizationWorker"));
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("OptimizationWorker stopped");
    }

    // ── Core optimisation orchestrator ───────────────────────────────────────

    /// <summary>
    /// Claims the next queued optimization run (atomically) and executes the full
    /// two-phase grid search with OOS validation, walk-forward checks, and auto-approval.
    /// Internal for unit test access (InternalsVisibleTo).
    /// </summary>
    internal async Task ProcessNextQueuedRunAsync(CancellationToken ct)
    {
        await using var scope  = _scopeFactory.CreateAsyncScope();
        var readCtx           = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx          = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator          = scope.ServiceProvider.GetRequiredService<IMediator>();
        var alertDispatcher   = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();
        var eventService      = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();
        var db                = readCtx.GetDbContext();
        var writeDb           = writeCtx.GetDbContext();

        // ── Stage 0+1: Atomic claim with integrated concurrency limit ───
        int maxConcurrentRuns;
        {
            var config0 = await LoadConfigurationAsync(db, ct);
            maxConcurrentRuns = config0.MaxConcurrentRuns;
        }
        var claimedRunId = await OptimizationRunClaimer.ClaimNextRunAsync(
            writeDb, maxConcurrentRuns, ExecutionLeaseDuration, ct);

        if (!claimedRunId.HasValue)
        {
            // Check for cold-start: no queued runs AND no active strategies
            bool anyActive = await db.Set<Strategy>()
                .AnyAsync(s => s.Status == StrategyStatus.Active && !s.IsDeleted, ct);
            if (!anyActive)
            {
                _logger.LogInformation(
                    "OptimizationWorker: no queued runs and no active strategies — system may be in cold start. " +
                    "Ensure strategies are created and activated via StrategyGenerationWorker or manual configuration");
            }
            return;
        }

        var run = await writeDb.Set<OptimizationRun>()
            .FirstOrDefaultAsync(x => x.Id == claimedRunId.Value, ct);
        if (run is null) return;

        // Track how often previously-deferred runs are rechecked. High counts for a
        // single run indicate a condition that won't self-resolve (e.g., dead EA symbol).
        if (run.DeferredUntilUtc.HasValue)
            _metrics.OptimizationDeferredRechecks.Add(1);

        var sw = Stopwatch.StartNew();
        OptimizationConfig? config = null;
        CancellationTokenSource? runCts = null;
        CancellationTokenSource? leaseHeartbeatCts = null;
        Task? leaseHeartbeatTask = null;
        var runCt = ct;
        bool completionPersisted = false;

        try
        {
            leaseHeartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            leaseHeartbeatTask = MaintainExecutionLeaseAsync(run.Id, leaseHeartbeatCts.Token);

            // ── Stage 2: Load config + pre-flight checks ────────────────────
            config = await LoadRunScopedConfigurationAsync(run, db, writeCtx, ct);

            // Config validation (warn on suspicious values before formal validation)
            if (config.MaxConcurrentRuns <= 0) _logger.LogWarning("OptimizationWorker: MaxConcurrentRuns={V} must be positive", config.MaxConcurrentRuns);
            if (config.BootstrapIterations < 100) _logger.LogWarning("OptimizationWorker: BootstrapIterations={V} is very low", config.BootstrapIterations);
            if (config.PermutationIterations < 100) _logger.LogWarning("OptimizationWorker: PermutationIterations={V} is very low", config.PermutationIterations);
            if (config.CooldownDays < 1) _logger.LogWarning("OptimizationWorker: CooldownDays={V} must be >= 1", config.CooldownDays);
            if (config.MaxOosDegradationPct is <= 0 or > 1) _logger.LogWarning("OptimizationWorker: MaxOosDegradationPct={V} outside (0,1]", config.MaxOosDegradationPct);
            if (config.TpeBudget < config.TpeInitialSamples) _logger.LogWarning("OptimizationWorker: TpeBudget ({B}) < TpeInitialSamples ({S}) — search will be purely random", config.TpeBudget, config.TpeInitialSamples);
            if (config.ScreeningTimeoutSeconds <= 0) _logger.LogWarning("OptimizationWorker: ScreeningTimeoutSeconds={V} must be positive", config.ScreeningTimeoutSeconds);

            _validator.SetInitialBalance(config.ScreeningInitialBalance);
            _validator.EnableCache();
            EnsureDeterministicSeed(run);
            await HeartbeatRunAsync(run, writeCtx, ct);

            var configIssues = OptimizationConfigValidator.Validate(
                config.AutoApprovalImprovementThreshold, config.AutoApprovalMinHealthScore,
                config.MinBootstrapCILower, config.EmbargoRatio, config.TpeBudget,
                config.TpeInitialSamples, config.MaxParallelBacktests, config.ScreeningTimeoutSeconds,
                config.CorrelationParamThreshold, config.SensitivityPerturbPct,
                config.GpEarlyStopPatience, config.CooldownDays, config.CheckpointEveryN, _logger,
                config.SensitivityDegradationTolerance, config.WalkForwardMinMaxRatio,
                config.CostStressMultiplier,
                config.CpcvNFolds, config.CpcvTestFoldCount,
                config.MinOosCandlesForValidation, config.CircuitBreakerThreshold,
                config.MinCandidateTrades, config.SuccessiveHalvingRungs,
                config.RegimeBlendRatio, config.MinEquityCurveR2, config.MaxTradeTimeConcentration);

            if (configIssues.Count > 0)
            {
                var issueStr = string.Join("; ", configIssues);
                _logger.LogError("OptimizationWorker: invalid configuration — {Issues}", issueStr);
                run.FailureCategory = OptimizationFailureCategory.ConfigError;
                OptimizationRunStateMachine.Transition(
                    run,
                    OptimizationRunStatus.Failed,
                    DateTime.UtcNow,
                    $"Invalid configuration: {issueStr}");
                await writeCtx.SaveChangesAsync(ct);
                return;
            }

            if (config.SeasonalBlackoutEnabled && IsInBlackoutPeriod(config.BlackoutPeriods))
            {
                _logger.LogInformation("OptimizationWorker: seasonal blackout active — deferring run {RunId}", run.Id);
                _metrics.OptimizationRunsDeferred.Add(1, new KeyValuePair<string, object?>("reason", "seasonal_blackout"));
                OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, DateTime.UtcNow);
                run.DeferredUntilUtc = DateTime.UtcNow.AddHours(6);
                await writeCtx.SaveChangesAsync(ct);
                return;
            }

            if (config.SuppressDuringDrawdownRecovery && await IsInDrawdownRecoveryAsync(db, ct))
            {
                _logger.LogInformation("OptimizationWorker: drawdown recovery active — deferring run {RunId}", run.Id);
                _metrics.OptimizationRunsDeferred.Add(1, new KeyValuePair<string, object?>("reason", "drawdown_recovery"));
                OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, DateTime.UtcNow);
                run.DeferredUntilUtc = DateTime.UtcNow.AddMinutes(30);
                await writeCtx.SaveChangesAsync(ct);
                return;
            }

            // Pre-flight: load strategy symbol/timeframe once for both regime transition
            // and EA availability guards (avoids duplicate queries on the same row).
            var preflightStrategy = await db.Set<Strategy>()
                .Where(s => s.Id == run.StrategyId && !s.IsDeleted)
                .Select(s => new { s.Symbol, s.Timeframe })
                .FirstOrDefaultAsync(ct);

            // Regime transition guard: if the regime for this strategy's symbol changed
            // within the last N hours, parameters optimized during the transition may
            // fit transitional noise rather than the new regime's characteristics.
            if (preflightStrategy is not null)
            {
                int regimeStabilityHours = config.RegimeStabilityHours;
                var recentRegimes = await db.Set<MarketRegimeSnapshot>()
                    .Where(s => s.Symbol == preflightStrategy.Symbol
                             && s.Timeframe == preflightStrategy.Timeframe
                             && s.DetectedAt >= DateTime.UtcNow.AddHours(-regimeStabilityHours)
                             && !s.IsDeleted)
                    .Select(s => s.Regime)
                    .Distinct()
                    .CountAsync(ct);

                if (recentRegimes > 1)
                {
                    _logger.LogInformation(
                        "OptimizationWorker: regime transition detected for {Symbol}/{Tf} in last {Hours}h — deferring run {RunId}",
                        preflightStrategy.Symbol, preflightStrategy.Timeframe, regimeStabilityHours, run.Id);
                    _metrics.OptimizationRunsDeferred.Add(1, new KeyValuePair<string, object?>("reason", "regime_transition"));
                    OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, DateTime.UtcNow);
                    run.DeferredUntilUtc = DateTime.UtcNow.AddHours(regimeStabilityHours);
                    await writeCtx.SaveChangesAsync(ct);
                    return;
                }
            }

            // EA data availability guard: if the strategy's symbol has no active EA instances
            // feeding data, we'd be optimizing against stale candles. Defer until data resumes.
            if (config.RequireEADataAvailability && preflightStrategy is not null)
            {
                var maxHeartbeatAge = TimeSpan.FromSeconds(60);
                bool hasActiveEA = await db.Set<EAInstance>()
                    .ActiveAndFreshForSymbol(preflightStrategy.Symbol, maxHeartbeatAge)
                    .AnyAsync(ct);

                if (!hasActiveEA)
                {
                    _logger.LogInformation(
                        "OptimizationWorker: no active EA instance for {Symbol} — deferring run {RunId} (DATA_UNAVAILABLE)",
                        preflightStrategy.Symbol, run.Id);
                    _metrics.OptimizationRunsDeferred.Add(1, new KeyValuePair<string, object?>("reason", "ea_data_unavailable"));
                    OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, DateTime.UtcNow);
                    run.DeferredUntilUtc = DateTime.UtcNow.AddMinutes(15);
                    await writeCtx.SaveChangesAsync(ct);
                    return;
                }
            }

            _logger.LogInformation(
                "OptimizationWorker: processing run {RunId} for strategy {StrategyId}", run.Id, run.StrategyId);

            // Aggregate timeout: cap the entire optimisation run to prevent indefinite blocking
            runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runCts.CancelAfter(TimeSpan.FromMinutes(config.MaxRunTimeoutMinutes));
            runCt = runCts.Token;

            var strategy = await db.Set<Strategy>()
                .FirstOrDefaultAsync(x => x.Id == run.StrategyId && !x.IsDeleted, runCt);

            if (strategy is null)
                throw new InvalidOperationException($"Strategy {run.StrategyId} not found.");

            // ── Stages 3–4: Load candles, validate, split, build cost options, run baseline ──
            var phase = PhaseTimer.Start(_logger, _metrics, run.Id, run.StrategyId, "DataLoad");

            var dataLoad = await LoadAndValidateCandlesAsync(db, run, strategy, config, runCt);

            phase.Dispose();

            var candles          = dataLoad.AllCandles;
            var trainCandles     = dataLoad.TrainCandles;
            var testCandles      = dataLoad.TestCandles;
            int embargoSize      = dataLoad.EmbargoSize;
            var screeningOptions = dataLoad.ScreeningOptions;
            var protocol         = dataLoad.Protocol;
            var candleLookbackStart = dataLoad.CandleLookbackStart;
            var currentRegimeForBaseline = dataLoad.CurrentRegimeForBaseline;
            var baselineComparisonScore = dataLoad.BaselineComparisonScore;
            var baselineParamsJson = dataLoad.BaselineParametersJson;
            var pairInfo = dataLoad.PairInfo;

            // ── Stages 5–6: Bayesian search with purged K-fold ──────────
            phase = PhaseTimer.Start(_logger, _metrics, run.Id, run.StrategyId, "Search");
            var searchResult = await RunBayesianSearchAsync(
                db, run, strategy, config, trainCandles, candles, screeningOptions,
                protocol, embargoSize, currentRegimeForBaseline, writeCtx, ct, runCt);

            var allEvaluated = searchResult.EvaluatedCandidates;
            int totalIters   = searchResult.TotalIterations;

            if (allEvaluated.Count == 0)
                throw new InvalidOperationException("All parameter candidates failed during TPE search.");

            // ── Variables used downstream in RunMetadataSnapshot ──
            var surrogateKind          = searchResult.SurrogateKind;
            var warmStarted            = searchResult.WarmStartedObservations;
            var resumedFromCheckpoint  = searchResult.ResumedFromCheckpoint;

            phase.Dispose();

            await HeartbeatRunAsync(run, writeCtx, ct);

            // ── Stage 6b: Memory pressure mitigation ──────────────────
            // Trim trade lists from non-top candidates to reduce heap pressure.
            // The Pareto selector only needs BacktestResult metrics (Sharpe, DD, WR),
            // not individual trades. Keep full trades only for the top N candidates
            // that might proceed to OOS validation.
            var evaluatedList = allEvaluated
                .OrderByDescending(c => c.HealthScore)
                .ToList();

            int keepTradesCount = Math.Max(config.TopNCandidates * 2, 10);
            for (int i = keepTradesCount; i < evaluatedList.Count; i++)
            {
                evaluatedList[i].Result.Trades?.Clear();
                evaluatedList[i].TradesTrimmed = true;
            }

            // ── Stage 7: Multi-objective Pareto selection ───────────────
            var topCandidates = ParetoFrontSelector.RankByNonDominatedSorting(
                evaluatedList,
                config.TopNCandidates,
                c => (double)c.Result.SharpeRatio,
                c => -(double)c.Result.MaxDrawdownPct,
                c => (double)c.Result.WinRate);

            phase = PhaseTimer.Start(_logger, _metrics, run.Id, run.StrategyId, "Validation");

            // Fine validation of Pareto winners on full IS data
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = config.MaxParallelBacktests,
                CancellationToken      = runCt,
            };
            var fineRanked = new List<ScoredCandidate>();
            var fineLock = new object();
            await Parallel.ForEachAsync(topCandidates, parallelOptions, async (candidate, pCt) =>
            {
                try
                {
                    var result = await _validator.RunWithTimeoutAsync(
                        strategy, candidate.ParamsJson, trainCandles, screeningOptions,
                        config.ScreeningTimeoutSeconds, pCt);
                    Interlocked.Increment(ref totalIters);
                    lock (fineLock) fineRanked.Add(new ScoredCandidate(
                        candidate.ParamsJson,
                        OptimizationHealthScorer.ComputeHealthScore(result),
                        result,
                        candidate.CvCoefficientOfVariation));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "OptimizationWorker: fine-validation backtest failed for Pareto candidate");
                    Interlocked.Increment(ref totalIters);
                }
            });

            var rankedCandidates = fineRanked.Count == 0
                ? topCandidates
                : fineRanked.OrderByDescending(r => r.HealthScore).ToList();

            // ── Stage 7b–11d: Post-selection validation with Pareto fallback ──
            var vr = await ValidateParetoCandidatesAsync(
                rankedCandidates, strategy, run, trainCandles, testCandles,
                screeningOptions, protocol, config, db, totalIters, baselineComparisonScore,
                baselineParamsJson, writeCtx, pairInfo, ct, runCt);
            await HeartbeatRunAsync(run, writeCtx, ct);

            var currentRegime = await db.Set<MarketRegimeSnapshot>()
                .Where(s => s.Symbol == strategy.Symbol
                         && s.Timeframe == strategy.Timeframe
                         && !s.IsDeleted)
                .OrderByDescending(s => s.DetectedAt)
                .Select(s => (MarketRegimeEnum?)s.Regime)
                .FirstOrDefaultAsync(runCt);

            phase.Dispose();

            // ── Stage 12: Persist results + benchmarking ────────────────
            phase = PhaseTimer.Start(_logger, _metrics, run.Id, run.StrategyId, "Persist");
            run.Iterations              = totalIters;
            run.IntermediateResultsJson  = null; // Clear checkpoint data — run completed successfully
            run.CheckpointVersion        = 0;
            run.BestParametersJson = CanonicalParameterJson.Normalize(vr.Winner.ParamsJson);
            run.BestHealthScore    = vr.OosHealthScore;
            // Store per-objective metrics for EHVI warm-start in future runs
            run.BestSharpeRatio    = vr.OosResult.SharpeRatio;
            run.BestMaxDrawdownPct = vr.OosResult.MaxDrawdownPct;
            run.BestWinRate        = vr.OosResult.WinRate;
            run.ApprovalReportJson = OptimizationCheckpointStore.LimitJsonPayload(
                vr.ApprovalReportJson,
                OptimizationCheckpointStore.MaxApprovalReportChars,
                "approval report",
                _logger);
            StampHeartbeat(run);
            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Completed, DateTime.UtcNow);
            // NOTE: SaveChangesAsync calls below intentionally use the parent `ct` (not `runCt`)
            // so that result persistence survives a run-level timeout. The run is already complete
            // at this point — we must persist results even if the aggregate timeout fires.
            run.RunMetadataJson = OptimizationCheckpointStore.LimitJsonPayload(JsonSerializer.Serialize(new RunMetadataSnapshot(
                RunMetadataVersion,
                run.DeterministicSeed,
                surrogateKind,
                strategy.Symbol,
                strategy.Timeframe,
                candles[0].Timestamp,
                candles[^1].Timestamp,
                candles.Count,
                trainCandles.Count,
                testCandles.Count,
                embargoSize,
                resumedFromCheckpoint,
                currentRegime?.ToString(),
                warmStarted,
                totalIters,
                run.BaselineHealthScore,
                baselineComparisonScore,
                vr.OosHealthScore,
                vr.Passed)),
                OptimizationCheckpointStore.MaxMetadataChars,
                "run metadata",
                _logger);
            await writeCtx.SaveChangesAsync(ct);
            completionPersisted = true;

            sw.Stop();
            _metrics.OptimizationRunsProcessed.Add(1);
            _metrics.OptimizationCycleDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            _metrics.OptimizationComputeSeconds.Record(sw.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("strategy_type", strategy.StrategyType.ToString()));

            double candidatesPerSec = totalIters / Math.Max(1.0, sw.Elapsed.TotalSeconds);

            _logger.LogInformation(
                "OptimizationWorker: run {RunId} completed — Iter={Iter} ({CPS:F1}/s), IS={IS:F2}, OOS={OOS:F2}, " +
                "CI=[{CIL:F2},{CIU:F2}], WF={WF:F2}, Sens={Sens}, CostOk={Cost}, Baseline={Base:F2} in {Ms:F0}ms",
                run.Id, run.Iterations, candidatesPerSec, vr.Winner.HealthScore, vr.OosHealthScore,
                vr.CILower, vr.CIUpper, vr.WfAvgScore, vr.SensitivityOk, vr.CostSensitiveOk,
                run.BaselineHealthScore, sw.Elapsed.TotalMilliseconds);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType = "OptimizationRun", EntityId = run.Id,
                DecisionType = "OptimizationCompleted", Outcome = "Completed",
                Reason = $"Iter={run.Iterations}, IS={vr.Winner.HealthScore:F2}, OOS={vr.OosHealthScore:F2}, " +
                         $"CI95=[{vr.CILower:F2},{vr.CIUpper:F2}], WF_Avg={vr.WfAvgScore:F2}, WF_Stable={vr.WfStable}, " +
                         $"MTF={vr.MtfCompatible}, ParamCorr={!vr.CorrelationSafe}, TemporalCorr={vr.TemporalMaxOverlap:P0}, " +
                         $"PortfolioCorr={vr.PortfolioMaxCorrelation:P0}, " +
                         $"Sensitivity={vr.SensitivityOk}, CostSensitive={vr.CostSensitiveOk} (pess={vr.PessimisticScore:F2}), " +
                         $"PermTest p={vr.PermPValue:F3} α_corrected={vr.PermCorrectedAlpha:F4} sig={vr.PermSignificant} (N={totalIters}), " +
                         $"Baseline={run.BaselineHealthScore:F2}, Throughput={candidatesPerSec:F1}/s",
                Source = "OptimizationWorker"
            }, ct);

            try
            {
                await eventService.SaveAndPublish(writeCtx, new OptimizationCompletedIntegrationEvent
                {
                    OptimizationRunId = run.Id,
                    StrategyId        = run.StrategyId,
                    Symbol            = strategy.Symbol,
                    Timeframe         = strategy.Timeframe,
                    Iterations        = run.Iterations,
                    BaselineScore     = run.BaselineHealthScore ?? 0m,
                    BestOosScore      = vr.OosHealthScore,
                    CompletedAt       = run.CompletedAt ?? DateTime.UtcNow,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "OptimizationWorker: failed to persist completion event for run {RunId} after results were already saved — preserving status {Status} and continuing",
                    run.Id, run.Status);
                _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", "OptimizationWorker"));
            }

            phase.Dispose();

            // ── Stage 13: Auto-approval ──────────────────────────────────
            phase = PhaseTimer.Start(_logger, _metrics, run.Id, run.StrategyId, "Approval");

            var ctx = new RunContext(run, strategy, config, baselineComparisonScore, db, writeDb, writeCtx, mediator, alertDispatcher, eventService, ct, runCt);
            await ApplyApprovalDecisionAsync(
                ctx, vr, currentRegime, candleLookbackStart, screeningOptions);

            phase.Dispose();
        }
        catch (DataQualityException dqEx)
        {
            // Data quality issues are recoverable — defer back to queue instead of failing.
            // New candle data from EA instances may resolve the issue on the next attempt.
            _logger.LogWarning(
                "OptimizationWorker: run {RunId} deferred due to data quality issue — {Reason}",
                run.Id, dqEx.Message);
            _metrics.OptimizationRunsDeferred.Add(1, new KeyValuePair<string, object?>("reason", "data_quality"));
            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, DateTime.UtcNow);
            run.FailureCategory = OptimizationFailureCategory.DataQuality;
            run.DeferredUntilUtc = DateTime.UtcNow.AddHours(1);
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (runCts is not null && runCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            if (ShouldPreservePersistedResult(completionPersisted, run.Status))
            {
                _logger.LogWarning(
                    "OptimizationWorker: aggregate timeout fired after completion persistence for run {RunId} — keeping status {Status}",
                    run.Id, run.Status);
                return;
            }

            _logger.LogWarning("OptimizationWorker: run {RunId} exceeded aggregate timeout of {Minutes}min",
                run.Id, config?.MaxRunTimeoutMinutes ?? 0);
            run.FailureCategory = OptimizationFailureCategory.Timeout;
            if (OptimizationRunStateMachine.CanTransition(run.Status, OptimizationRunStatus.Failed))
            {
                OptimizationRunStateMachine.Transition(
                    run,
                    OptimizationRunStatus.Failed,
                    DateTime.UtcNow,
                    $"Aggregate timeout exceeded ({config?.MaxRunTimeoutMinutes ?? 0} minutes)");
            }
            await writeCtx.SaveChangesAsync(ct);

            _metrics.OptimizationRunsFailed.Add(1);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType = "OptimizationRun", EntityId = run.Id,
                DecisionType = "OptimizationFailed", Outcome = "Timeout",
                Reason = run.ErrorMessage ?? $"Aggregate timeout exceeded ({config.MaxRunTimeoutMinutes} minutes)",
                Source = "OptimizationWorker"
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (ShouldPreservePersistedResult(completionPersisted, run.Status))
            {
                _logger.LogWarning(
                    "OptimizationWorker: shutdown cancellation arrived after completion persistence for run {RunId} — keeping status {Status}",
                    run.Id, run.Status);
                return;
            }

            // Graceful shutdown: the parent stoppingToken was cancelled (app shutting down).
            // Persist intermediate results and re-queue the run so crash recovery doesn't
            // have to restart from scratch. The IntermediateResultsJson (if populated by
            // periodic checkpointing) is preserved for the next attempt.
            _logger.LogWarning(
                "OptimizationWorker: run {RunId} interrupted by shutdown — re-queuing with intermediate results",
                run.Id);
            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, DateTime.UtcNow);

            // Use a brief standalone timeout for the shutdown save — we can't use `ct`
            // (already cancelled) and we don't want to block shutdown indefinitely.
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await writeCtx.SaveChangesAsync(shutdownCts.Token);
                _logger.LogInformation(
                    "OptimizationWorker: run {RunId} successfully re-queued with checkpoint data", run.Id);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx,
                    "OptimizationWorker: failed to re-queue run {RunId} during shutdown — " +
                    "crash recovery will reclaim it on next startup", run.Id);
            }
        }
        catch (Exception ex)
        {
            if (ShouldPreservePersistedResult(completionPersisted, run.Status))
            {
                _logger.LogError(ex,
                    "OptimizationWorker: post-completion step failed for run {RunId} after result persistence — keeping status {Status}",
                    run.Id, run.Status);
                return;
            }

            _logger.LogError(ex, "OptimizationWorker: run {RunId} failed", run.Id);
            if (OptimizationRunStateMachine.CanTransition(run.Status, OptimizationRunStatus.Failed))
            {
                run.FailureCategory = ex switch
                {
                    InvalidOperationException when ex.Message.Contains("not found") => OptimizationFailureCategory.StrategyRemoved,
                    InvalidOperationException when ex.Message.Contains("candidates failed") => OptimizationFailureCategory.SearchExhausted,
                    _ => OptimizationFailureCategory.Transient,
                };
                OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Failed, DateTime.UtcNow, ex.Message);
            }
            else
            {
                _logger.LogWarning(
                    "OptimizationWorker: preserving terminal run status {Status} for run {RunId} after downstream error",
                    run.Status, run.Id);
            }
            await writeCtx.SaveChangesAsync(ct);

            _metrics.OptimizationRunsFailed.Add(1);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType = "OptimizationRun", EntityId = run.Id,
                DecisionType = "OptimizationFailed", Outcome = "Failed",
                Reason = ex.Message, Source = "OptimizationWorker"
            }, ct);
        }
        finally
        {
            if (leaseHeartbeatCts is not null)
            {
                leaseHeartbeatCts.Cancel();
                if (leaseHeartbeatTask is not null)
                {
                    try
                    {
                        await leaseHeartbeatTask;
                    }
                    catch (OperationCanceledException) when (leaseHeartbeatCts.IsCancellationRequested)
                    {
                        // Normal shutdown for the background lease heartbeat.
                    }
                }

                leaseHeartbeatCts.Dispose();
            }

            runCts?.Dispose();
            _validator.ClearCache();
        }
    }

    // ── Stage methods ───────────────────────────────────────────────────────

    private async Task RequeueExpiredRunningRunsAsync(CancellationToken ct)
    {
        await using var recoveryScope = _scopeFactory.CreateAsyncScope();
        var writeCtx = recoveryScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeCtx.GetDbContext();

        var (requeued, orphaned) = await OptimizationRunClaimer.RequeueExpiredRunsAsync(db, ct);

        if (requeued > 0)
            _metrics.OptimizationLeaseReclaims.Add(requeued);
        if (orphaned > 0)
            _logger.LogWarning(
                "OptimizationWorker: marked {Count} expired run(s) as Failed — strategy deleted",
                orphaned);
    }

    /// <summary>
    /// Re-queues recently failed runs that haven't exhausted their retry budget.
    /// Only retries runs that failed within the last hour (transient failures);
    /// older failures are considered permanent. Runs that have exhausted their retry
    /// budget are moved to <see cref="OptimizationRunStatus.Abandoned"/> (dead-letter queue)
    /// and an alert is fired for operator visibility.
    /// </summary>
    private async Task RetryFailedRunsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db       = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Single-key query instead of loading all ~52 config keys
        int maxRetryAttempts = await OptimizationGridBuilder.GetConfigAsync(db, "Optimization:MaxRetryAttempts", 2, ct);
        if (maxRetryAttempts <= 0) return;

        var nowUtc = DateTime.UtcNow;
        var retryWindowStart = nowUtc - GetRetryEligibilityWindow(maxRetryAttempts);

        // Re-queue retryable runs with exponential backoff: a run must wait
        // 15 * 2^RetryCount minutes after failure before becoming eligible again
        // (15m, 30m, 60m, ...). This prevents burning through the retry budget
        // on transient issues that take minutes to resolve (e.g. DB failover,
        // resource exhaustion).
        var retryableRuns = await writeDb.Set<OptimizationRun>()
            .Where(r => r.Status == OptimizationRunStatus.Failed
                     && !r.IsDeleted
                     && r.RetryCount < maxRetryAttempts
                     && r.FailureCategory != OptimizationFailureCategory.ConfigError
                     && r.FailureCategory != OptimizationFailureCategory.StrategyRemoved
                     && r.CompletedAt != null && r.CompletedAt >= retryWindowStart
                     && r.CompletedAt.Value.AddMinutes(15 << r.RetryCount) <= nowUtc)
            .OrderBy(r => r.CompletedAt)
            .ToListAsync(ct);

        int retried = 0;
        foreach (var run in retryableRuns)
        {
            ct.ThrowIfCancellationRequested();

            bool hasActiveRun = await writeDb.Set<OptimizationRun>()
                .AnyAsync(r => r.Id != run.Id
                            && r.StrategyId == run.StrategyId
                            && !r.IsDeleted
                            && (r.Status == OptimizationRunStatus.Queued || r.Status == OptimizationRunStatus.Running), ct);
            if (hasActiveRun)
            {
                _logger.LogInformation(
                    "OptimizationWorker: skipped retry for run {RunId} — strategy {StrategyId} already has an active optimization run",
                    run.Id, run.StrategyId);
                continue;
            }

            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, DateTime.UtcNow);
            run.RetryCount++;
            run.DeferredUntilUtc = null;

            try
            {
                await writeCtx.SaveChangesAsync(ct);
                retried++;
            }
            catch (DbUpdateException ex) when (IsActiveQueueConstraintViolation(ex))
            {
                await writeDb.Entry(run).ReloadAsync(ct);
                _logger.LogInformation(
                    "OptimizationWorker: skipped retry for run {RunId} — another worker queued or claimed the strategy first",
                    run.Id);
            }
        }

        if (retried > 0)
        {
            _logger.LogInformation(
                "OptimizationWorker: re-queued {Count} failed run(s) for retry", retried);
        }

        // Dead-letter: move runs to Abandoned when either:
        // (a) retry budget is spent (RetryCount >= max), OR
        // (b) the run aged out of the retry window AND still has retries remaining.
        // Case (b) prevents orphaned Failed runs that are too old for the transient retry
        // window but haven't exhausted their budget — they'd otherwise sit in Failed forever
        // with no alerting or operator visibility.
        var abandonedRuns = await writeDb.Set<OptimizationRun>()
            .Where(r => r.Status == OptimizationRunStatus.Failed
                     && !r.IsDeleted
                     && (r.RetryCount >= maxRetryAttempts
                         || (r.CompletedAt != null && r.CompletedAt < retryWindowStart)))
            .ToListAsync(ct);
        int abandoned = abandonedRuns.Count;

        foreach (var run in abandonedRuns)
        {
            string abandonmentMessage = string.IsNullOrWhiteSpace(run.ErrorMessage)
                ? $"Retry budget exhausted — moved to dead-letter queue [Abandoned after {run.RetryCount} retries]"
                : $"{run.ErrorMessage} [Abandoned after {run.RetryCount} retries]";
            OptimizationRunStateMachine.Transition(
                run,
                OptimizationRunStatus.Abandoned,
                DateTime.UtcNow,
                abandonmentMessage);
        }

        if (abandoned > 0)
            await writeCtx.SaveChangesAsync(ct);

        if (abandoned > 0)
        {
            _logger.LogWarning(
                "OptimizationWorker: moved {Count} permanently failed run(s) to dead-letter (Abandoned) — " +
                "retry budget exhausted, manual investigation required", abandoned);

            // Fire alert for operator attention — deduplicate to avoid alert fatigue.
            // If a dead-letter alert was already created within the last 24 hours, skip.
            try
            {
                bool recentAlertExists = await db.Set<Alert>()
                    .AnyAsync(a => a.Symbol == "OptimizationWorker:DeadLetter"
                               && a.LastTriggeredAt != null
                               && a.LastTriggeredAt >= DateTime.UtcNow.AddHours(-24)
                               && !a.IsDeleted, ct);

                if (!recentAlertExists)
                {
                    var alertDispatcher = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();
                    var alert = new Alert
                    {
                        AlertType       = AlertType.DataQualityIssue,
                        Symbol          = "OptimizationWorker:DeadLetter",
                        Channel         = AlertChannel.Webhook,
                        Destination     = string.Empty,
                        Severity        = AlertSeverity.High,
                        IsActive        = true,
                        LastTriggeredAt = DateTime.UtcNow,
                        ConditionJson   = JsonSerializer.Serialize(new
                        {
                            Type = "OptimizationDeadLetter",
                            AbandonedCount = abandoned,
                            MaxRetryAttempts = maxRetryAttempts,
                            Message = $"{abandoned} optimization run(s) moved to dead-letter queue after exhausting {maxRetryAttempts} retry attempts. Manual investigation required."
                        }),
                    };
                    writeDb.Set<Alert>().Add(alert);
                    await writeCtx.SaveChangesAsync(ct);

                    await alertDispatcher.DispatchBySeverityAsync(alert,
                        $"{abandoned} optimization run(s) permanently failed after {maxRetryAttempts} retries — moved to dead-letter queue", ct);
                }
                else
                {
                    _logger.LogDebug(
                        "OptimizationWorker: suppressed duplicate dead-letter alert ({Count} run(s)) — recent alert exists",
                        abandoned);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OptimizationWorker: dead-letter alert dispatch failed (non-fatal)");
            }
        }
    }

    private static TimeSpan GetRetryEligibilityWindow(int maxRetryAttempts)
    {
        int normalizedAttempts = Math.Max(1, maxRetryAttempts);
        int maxEligibleBackoffMinutes = 15 << (normalizedAttempts - 1);
        return TimeSpan.FromMinutes(maxEligibleBackoffMinutes + 15);
    }

    /// <summary>
    /// Checks completed validation follow-ups (backtests + walk-forwards) for recently
    /// approved optimization runs. If a follow-up shows poor results, fires an alert
    /// for the operator to investigate and potentially roll back the approval.
    /// </summary>
    private async Task MonitorFollowUpResultsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var alertDispatcher = scope.ServiceProvider.GetService<IAlertDispatcher>();
        var db       = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();
        decimal minBacktestHealthScore = 0.55m * 0.80m;
        int minCandidateTrades = 10;
        decimal maxWalkForwardCv = 0.50m;
        try
        {
            minBacktestHealthScore = await OptimizationGridBuilder.GetConfigAsync(
                db, "Optimization:AutoApprovalMinHealthScore", 0.55m, ct) * 0.80m;
            minCandidateTrades = await OptimizationGridBuilder.GetConfigAsync(
                db, "Optimization:MinCandidateTrades", 10, ct);
            maxWalkForwardCv = await OptimizationGridBuilder.GetConfigAsync(
                db, "Optimization:MaxCvCoefficientOfVariation", 0.50m, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "OptimizationWorker: follow-up monitor config load failed — using default quality thresholds");
        }

        // Find approved runs with pending follow-ups where the follow-ups have completed
        var pendingRuns = await db.Set<OptimizationRun>()
            .Where(r => r.Status == OptimizationRunStatus.Approved
                     && r.ValidationFollowUpStatus == ValidationFollowUpStatus.Pending
                     && !r.IsDeleted)
            .Take(10)
            .ToListAsync(ct);

        foreach (var run in pendingRuns)
        {
            var backtestRun = await db.Set<BacktestRun>()
                .Where(b => b.SourceOptimizationRunId == run.Id && !b.IsDeleted)
                .FirstOrDefaultAsync(ct);

            var wfRun = await db.Set<WalkForwardRun>()
                .Where(w => w.SourceOptimizationRunId == run.Id && !w.IsDeleted)
                .FirstOrDefaultAsync(ct);

            if (backtestRun is null || wfRun is null)
            {
                var repairRun = await writeDb.Set<OptimizationRun>()
                    .FirstOrDefaultAsync(r => r.Id == run.Id, ct);
                var strategy = await db.Set<Strategy>()
                    .FirstOrDefaultAsync(s => s.Id == run.StrategyId && !s.IsDeleted, ct);

                if (repairRun is not null && strategy is not null)
                {
                    var repairConfig = await LoadConfigurationAsync(db, ct);
                    bool alreadyComplete = await EnsureValidationFollowUpsAsync(
                        writeDb, repairRun, strategy, repairConfig, ct);
                    if (!alreadyComplete)
                    {
                        await writeCtx.SaveChangesAsync(ct);
                        _logger.LogWarning(
                            "OptimizationWorker: repaired missing follow-up validation rows for approved run {RunId} — backtest={HasBacktest}, walk-forward={HasWalkForward}",
                            run.Id, backtestRun is not null, wfRun is not null);
                        continue;
                    }
                }

                _logger.LogWarning(
                    "OptimizationWorker: follow-up validation rows missing for approved run {RunId} — backtest={HasBacktest}, walk-forward={HasWalkForward}",
                    run.Id, backtestRun is not null, wfRun is not null);
                continue;
            }

            bool backtestDone = backtestRun.Status is RunStatus.Completed or RunStatus.Failed;
            bool wfDone = wfRun.Status is RunStatus.Completed or RunStatus.Failed;

            if (!backtestDone || !wfDone) continue; // Still running

            bool backtestFailed = backtestRun.Status == RunStatus.Failed;
            bool wfFailed = wfRun.Status == RunStatus.Failed;
            string backtestReason = backtestFailed ? "backtest follow-up execution failed" : string.Empty;
            string walkForwardReason = wfFailed ? "walk-forward follow-up execution failed" : string.Empty;
            bool backtestQualityOk = !backtestFailed
                && OptimizationFollowUpQualityEvaluator.IsBacktestQualitySufficient(
                    backtestRun, minBacktestHealthScore, minCandidateTrades, out backtestReason);
            bool walkForwardQualityOk = !wfFailed
                && OptimizationFollowUpQualityEvaluator.IsWalkForwardQualitySufficient(
                    wfRun, maxWalkForwardCv, out walkForwardReason);

            var liveRun = await writeDb.Set<OptimizationRun>()
                .FirstOrDefaultAsync(r => r.Id == run.Id, ct);
            if (liveRun is null) continue;

            Alert? followUpAlert = null;
            string? followUpAlertMessage = null;
            if (backtestFailed || wfFailed || !backtestQualityOk || !walkForwardQualityOk)
            {
                liveRun.ValidationFollowUpStatus = ValidationFollowUpStatus.Failed;
                _metrics.OptimizationFollowUpFailures.Add(1);
                _logger.LogWarning(
                    "OptimizationWorker: follow-up validation FAILED for run {RunId} — " +
                    "backtest={BtStatus}, walk-forward={WfStatus}, backtestQualityOk={BacktestQualityOk}, " +
                    "walkForwardQualityOk={WalkForwardQualityOk}, backtestReason={BacktestReason}, walkForwardReason={WalkForwardReason}. " +
                    "Manual investigation recommended.",
                    run.Id, backtestRun.Status, wfRun.Status, backtestQualityOk,
                    walkForwardQualityOk, backtestReason, walkForwardReason);

                followUpAlert = await writeDb.Set<Alert>()
                    .FirstOrDefaultAsync(a => a.Symbol == BuildFollowUpFailureAlertSymbol(run.Id) && !a.IsDeleted, ct);

                if (followUpAlert is null)
                {
                    followUpAlert = new Alert();
                    writeDb.Set<Alert>().Add(followUpAlert);
                }

                followUpAlertMessage = PopulateFollowUpFailureAlert(
                    followUpAlert,
                    run.Id,
                    run.StrategyId,
                    backtestRun.Status,
                    wfRun.Status,
                    backtestQualityOk,
                    walkForwardQualityOk,
                    backtestReason,
                    walkForwardReason,
                    DateTime.UtcNow);
            }
            else
            {
                liveRun.ValidationFollowUpStatus = ValidationFollowUpStatus.Passed;
                _logger.LogInformation(
                    "OptimizationWorker: follow-up validation passed for run {RunId}", run.Id);
            }

            await writeCtx.SaveChangesAsync(ct);

            if (followUpAlert is not null && followUpAlertMessage is not null)
            {
                if (alertDispatcher is null)
                {
                    _logger.LogDebug(
                        "OptimizationWorker: follow-up failure alert for run {RunId} was persisted but no IAlertDispatcher is registered",
                        run.Id);
                    continue;
                }

                try
                {
                    await alertDispatcher.DispatchBySeverityAsync(followUpAlert, followUpAlertMessage, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "OptimizationWorker: immediate follow-up failure alert dispatch failed for run {RunId} (non-fatal)",
                        run.Id);
                }
            }
        }
    }

    private async Task<OptimizationConfig> LoadRunScopedConfigurationAsync(
        OptimizationRun run,
        DbContext db,
        IWriteApplicationDbContext writeCtx,
        CancellationToken ct)
    {
        bool hadExistingSnapshot = !string.IsNullOrWhiteSpace(run.ConfigSnapshotJson);
        if (hadExistingSnapshot)
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<OptimizationConfigSnapshot>(run.ConfigSnapshotJson);
                if (snapshot is not null && snapshot.Version == ConfigSnapshotVersion)
                    return snapshot.Config;
            }
            catch
            {
                // Fall back to live config below if the stored snapshot is malformed.
            }
        }

        var liveConfig = await LoadConfigurationAsync(db, ct);
        // Config typo detection: warn about any EngineConfig keys starting with
        // "Optimization:" or "Backtest:Gate:" that aren't in the known key set.
        await DetectUnknownConfigKeysAsync(db, ct);
        LogPresetOverrides(liveConfig);
        var newSnapshotJson = JsonSerializer.Serialize(new OptimizationConfigSnapshot(ConfigSnapshotVersion, liveConfig));

        // Config change audit trail: diff against the most recent prior run's snapshot.
        // Skip if this is a checkpoint resume with a malformed snapshot — the config was
        // already captured (and diffed) on the original run start; re-diffing on resume
        // would produce duplicate audit log entries.
        if (!hadExistingSnapshot)
        {
            try
            {
                var priorSnapshotJson = await db.Set<OptimizationRun>()
                    .Where(r => r.Id != run.Id && r.ConfigSnapshotJson != null && !r.IsDeleted)
                    .OrderByDescending(r => r.StartedAt)
                    .Select(r => r.ConfigSnapshotJson)
                    .FirstOrDefaultAsync(ct);

                if (priorSnapshotJson is not null)
                {
                    var changes = DiffConfigSnapshots(priorSnapshotJson, newSnapshotJson);
                    if (changes.Count > 0)
                    {
                        _logger.LogInformation(
                            "OptimizationWorker: config changed since last run — {Count} parameter(s) modified: {Changes}",
                            changes.Count, string.Join(", ", changes.Select(c => $"{c.Key}: {c.OldValue}→{c.NewValue}")));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OptimizationWorker: config diff failed (non-fatal)");
            }
        }

        run.ConfigSnapshotJson = newSnapshotJson;
        await writeCtx.SaveChangesAsync(ct);
        return liveConfig;
    }

    /// <summary>
    /// Returns preset-specific defaults for the ~15 most performance-sensitive config keys.
    /// Three presets provide coherent starting points; individual keys still override.
    /// </summary>
    private static (int TpeBudget, int TpeInitialSamples, int PurgedKFolds, int BootstrapIters,
        int PermutationIters, int MaxParallelBacktests, int CpcvNFolds, int MaxRunTimeoutMinutes,
        int CpcvMaxCombinations, int TopNCandidates, int CheckpointEveryN) GetPresetDefaults(string preset)
        => preset.ToLowerInvariant() switch
        {
            "conservative" => (30, 10, 3, 500, 500, 2, 4, 20, 10, 3, 5),
            "aggressive"   => (100, 25, 7, 2000, 2000, 8, 8, 60, 25, 8, 15),
            _              => (50, 15, 5, 1000, 1000, 4, 6, 30, 15, 5, 10), // "balanced" (default)
        };

    /// <summary>
    /// Compares the loaded config against the preset defaults and logs any keys where
    /// the operator explicitly overrode the preset value. This makes it visible at runtime
    /// which keys are preset-driven and which are manually tuned.
    /// </summary>
    private void LogPresetOverrides(OptimizationConfig config)
    {
        var presetName = config.PresetName;
        var p2 = GetPresetDefaults(presetName);
        var overrides = new List<string>();
        if (config.TpeBudget != p2.TpeBudget) overrides.Add($"TpeBudget={config.TpeBudget} (preset={p2.TpeBudget})");
        if (config.TpeInitialSamples != p2.TpeInitialSamples) overrides.Add($"TpeInitialSamples={config.TpeInitialSamples} (preset={p2.TpeInitialSamples})");
        if (config.PurgedKFolds != p2.PurgedKFolds) overrides.Add($"PurgedKFolds={config.PurgedKFolds} (preset={p2.PurgedKFolds})");
        if (config.BootstrapIterations != p2.BootstrapIters) overrides.Add($"BootstrapIterations={config.BootstrapIterations} (preset={p2.BootstrapIters})");
        if (config.PermutationIterations != p2.PermutationIters) overrides.Add($"PermutationIterations={config.PermutationIterations} (preset={p2.PermutationIters})");
        if (config.MaxParallelBacktests != p2.MaxParallelBacktests) overrides.Add($"MaxParallelBacktests={config.MaxParallelBacktests} (preset={p2.MaxParallelBacktests})");
        if (config.CpcvNFolds != p2.CpcvNFolds) overrides.Add($"CpcvNFolds={config.CpcvNFolds} (preset={p2.CpcvNFolds})");
        if (config.MaxRunTimeoutMinutes != p2.MaxRunTimeoutMinutes) overrides.Add($"MaxRunTimeoutMinutes={config.MaxRunTimeoutMinutes} (preset={p2.MaxRunTimeoutMinutes})");
        if (config.CpcvMaxCombinations != p2.CpcvMaxCombinations) overrides.Add($"CpcvMaxCombinations={config.CpcvMaxCombinations} (preset={p2.CpcvMaxCombinations})");
        if (config.TopNCandidates != p2.TopNCandidates) overrides.Add($"TopNCandidates={config.TopNCandidates} (preset={p2.TopNCandidates})");
        if (config.CheckpointEveryN != p2.CheckpointEveryN) overrides.Add($"CheckpointEveryN={config.CheckpointEveryN} (preset={p2.CheckpointEveryN})");

        if (overrides.Count > 0)
        {
            _logger.LogInformation(
                "OptimizationWorker: using preset '{Preset}' with {Count} override(s): {Overrides}",
                presetName, overrides.Count, string.Join(", ", overrides));
        }
        else
        {
            _logger.LogDebug("OptimizationWorker: using preset '{Preset}' with no overrides", presetName);
        }
    }

    private static async Task<OptimizationConfig> LoadConfigurationAsync(DbContext db, CancellationToken ct)
    {
        // Single batch query for all config keys instead of ~50 individual queries
        var allKeys = new[]
        {
            "Optimization:Preset",
            "Optimization:SchedulePollSeconds", "Optimization:CooldownDays", "Optimization:MaxQueuedPerCycle",
            "Optimization:AutoScheduleEnabled", "Backtest:Gate:MinWinRate", "Backtest:Gate:MinProfitFactor",
            "Backtest:Gate:MinTotalTrades", "Optimization:AutoApprovalImprovementThreshold",
            "Optimization:AutoApprovalMinHealthScore", "Optimization:TopNCandidates",
            "Optimization:CoarsePhaseThreshold", "Optimization:ScreeningTimeoutSeconds",
            "Optimization:ScreeningSpreadPoints", "Optimization:ScreeningCommissionPerLot",
            "Optimization:ScreeningSlippagePips", "Optimization:MaxOosDegradationPct",
            "Optimization:SuppressDuringDrawdownRecovery", "Optimization:SeasonalBlackoutEnabled",
            "Optimization:BlackoutPeriods", "Optimization:MaxRunTimeoutMinutes",
            "Optimization:MaxParallelBacktests", "Optimization:MinCandidateTrades",
            "Optimization:EmbargoRatio", "Optimization:CorrelationParamThreshold",
            "Optimization:TpeBudget", "Optimization:TpeInitialSamples", "Optimization:PurgedKFolds",
            "Optimization:SensitivityPerturbPct", "Optimization:BootstrapIterations",
            "Optimization:MinBootstrapCILower", "Optimization:CostSensitivityEnabled",
            "Optimization:AdaptiveBoundsEnabled", "Optimization:TemporalOverlapThreshold",
            "Optimization:DataScarcityThreshold", "Optimization:ScreeningInitialBalance",
            "Optimization:PortfolioCorrelationThreshold", "Optimization:MaxConsecutiveFailuresBeforeEscalation",
            "Optimization:CheckpointEveryN", "Optimization:GpEarlyStopPatience",
            "Optimization:SensitivityDegradationTolerance", "Optimization:WalkForwardMinMaxRatio",
            "Optimization:CostStressMultiplier", "Optimization:MinOosCandlesForValidation",
            "Optimization:MaxCvCoefficientOfVariation", "Optimization:PermutationIterations",
            "Optimization:RegimeStabilityHours",
            "Optimization:MaxRetryAttempts", "Optimization:CandleLookbackMonths", "Optimization:CandleLookbackAutoScale",
            "Optimization:RequireEADataAvailability", "Optimization:MaxConcurrentRuns",
            "Optimization:UseSymbolSpecificSpread", "Optimization:RegimeBlendRatio",
            "Optimization:CpcvNFolds", "Optimization:CpcvTestFoldCount",
            "Optimization:CpcvMaxCombinations", "Optimization:CircuitBreakerThreshold",
            "Optimization:SuccessiveHalvingRungs", "Optimization:MaxCrossRegimeEvals",
            "Optimization:HyperbandEnabled", "Optimization:HyperbandEta",
            "Optimization:MaxRunsPerWeek", "Optimization:UseEhviAcquisition",
            "Optimization:UseParegoScalarization", "Optimization:MinEquityCurveR2",
            "Optimization:MaxTradeTimeConcentration",
        };

        var b = await OptimizationGridBuilder.GetConfigBatchAsync(db, allKeys, ct);

        // Preset provides coherent defaults for the performance-sensitive keys.
        // Individual key overrides still take precedence (GetConfigValue checks
        // the batch first, falling back to the preset default).
        var presetName = OptimizationGridBuilder.GetConfigValue(b, "Optimization:Preset", "balanced");
        var p = GetPresetDefaults(presetName);

        return new OptimizationConfig
        {
            // Scheduling
            SchedulePollSeconds              = OptimizationGridBuilder.GetConfigValue(b, "Optimization:SchedulePollSeconds", 7200),
            CooldownDays                     = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CooldownDays", 14),
            MaxQueuedPerCycle                = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxQueuedPerCycle", 3),
            AutoScheduleEnabled              = OptimizationGridBuilder.GetConfigValue(b, "Optimization:AutoScheduleEnabled", true),
            MaxRunsPerWeek                   = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxRunsPerWeek", 20),

            // Performance gates
            MinWinRate                       = OptimizationGridBuilder.GetConfigValue(b, "Backtest:Gate:MinWinRate", 0.60),
            MinProfitFactor                  = OptimizationGridBuilder.GetConfigValue(b, "Backtest:Gate:MinProfitFactor", 1.0),
            MinTotalTrades                   = OptimizationGridBuilder.GetConfigValue(b, "Backtest:Gate:MinTotalTrades", 10),

            // Approval thresholds
            AutoApprovalImprovementThreshold = OptimizationGridBuilder.GetConfigValue(b, "Optimization:AutoApprovalImprovementThreshold", 0.10m),
            AutoApprovalMinHealthScore       = OptimizationGridBuilder.GetConfigValue(b, "Optimization:AutoApprovalMinHealthScore", 0.55m),

            // Search
            TopNCandidates                   = OptimizationGridBuilder.GetConfigValue(b, "Optimization:TopNCandidates", p.TopNCandidates),
            CoarsePhaseThreshold             = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CoarsePhaseThreshold", 10),
            TpeBudget                        = OptimizationGridBuilder.GetConfigValue(b, "Optimization:TpeBudget", p.TpeBudget),
            TpeInitialSamples                = OptimizationGridBuilder.GetConfigValue(b, "Optimization:TpeInitialSamples", p.TpeInitialSamples),
            PurgedKFolds                     = OptimizationGridBuilder.GetConfigValue(b, "Optimization:PurgedKFolds", p.PurgedKFolds),
            AdaptiveBoundsEnabled            = OptimizationGridBuilder.GetConfigValue(b, "Optimization:AdaptiveBoundsEnabled", true),
            GpEarlyStopPatience              = OptimizationGridBuilder.GetConfigValue(b, "Optimization:GpEarlyStopPatience", 4),
            PresetName                       = presetName,
            HyperbandEnabled                 = OptimizationGridBuilder.GetConfigValue(b, "Optimization:HyperbandEnabled", true),
            HyperbandEta                     = OptimizationGridBuilder.GetConfigValue(b, "Optimization:HyperbandEta", 3),
            UseEhviAcquisition               = OptimizationGridBuilder.GetConfigValue(b, "Optimization:UseEhviAcquisition", false),
            UseParegoScalarization           = OptimizationGridBuilder.GetConfigValue(b, "Optimization:UseParegoScalarization", false),

            // Screening / backtesting
            ScreeningTimeoutSeconds          = OptimizationGridBuilder.GetConfigValue(b, "Optimization:ScreeningTimeoutSeconds", 30),
            ScreeningSpreadPoints            = OptimizationGridBuilder.GetConfigValue(b, "Optimization:ScreeningSpreadPoints", 20.0),
            ScreeningCommissionPerLot        = OptimizationGridBuilder.GetConfigValue(b, "Optimization:ScreeningCommissionPerLot", 7.0),
            ScreeningSlippagePips            = OptimizationGridBuilder.GetConfigValue(b, "Optimization:ScreeningSlippagePips", 1.0),
            ScreeningInitialBalance          = OptimizationGridBuilder.GetConfigValue(b, "Optimization:ScreeningInitialBalance", 10_000m),
            MaxParallelBacktests             = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxParallelBacktests", p.MaxParallelBacktests),
            MinCandidateTrades               = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MinCandidateTrades", 10),
            MaxRunTimeoutMinutes             = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxRunTimeoutMinutes", p.MaxRunTimeoutMinutes),
            CircuitBreakerThreshold          = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CircuitBreakerThreshold", 10),
            SuccessiveHalvingRungs           = OptimizationGridBuilder.GetConfigValue(b, "Optimization:SuccessiveHalvingRungs", "0.25,0.50"),

            // Validation gates
            MaxOosDegradationPct             = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxOosDegradationPct", 0.60),
            EmbargoRatio                     = OptimizationGridBuilder.GetConfigValue(b, "Optimization:EmbargoRatio", 0.05),
            CorrelationParamThreshold        = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CorrelationParamThreshold", 0.15),
            SensitivityPerturbPct            = OptimizationGridBuilder.GetConfigValue(b, "Optimization:SensitivityPerturbPct", 0.10),
            SensitivityDegradationTolerance  = OptimizationGridBuilder.GetConfigValue(b, "Optimization:SensitivityDegradationTolerance", 0.20),
            BootstrapIterations              = OptimizationGridBuilder.GetConfigValue(b, "Optimization:BootstrapIterations", p.BootstrapIters),
            MinBootstrapCILower              = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MinBootstrapCILower", 0.40m),
            CostSensitivityEnabled           = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CostSensitivityEnabled", true),
            CostStressMultiplier             = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CostStressMultiplier", 2.0),
            TemporalOverlapThreshold         = OptimizationGridBuilder.GetConfigValue(b, "Optimization:TemporalOverlapThreshold", 0.70),
            PortfolioCorrelationThreshold    = OptimizationGridBuilder.GetConfigValue(b, "Optimization:PortfolioCorrelationThreshold", 0.80),
            WalkForwardMinMaxRatio           = OptimizationGridBuilder.GetConfigValue(b, "Optimization:WalkForwardMinMaxRatio", 0.50),
            MinOosCandlesForValidation       = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MinOosCandlesForValidation", 50),
            MaxCvCoefficientOfVariation      = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxCvCoefficientOfVariation", 0.50),
            PermutationIterations            = OptimizationGridBuilder.GetConfigValue(b, "Optimization:PermutationIterations", p.PermutationIters),
            MinEquityCurveR2                 = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MinEquityCurveR2", 0.60),
            MaxTradeTimeConcentration        = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxTradeTimeConcentration", 0.60),

            // CPCV
            CpcvNFolds                       = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CpcvNFolds", p.CpcvNFolds),
            CpcvTestFoldCount                = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CpcvTestFoldCount", 2),
            CpcvMaxCombinations              = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CpcvMaxCombinations", p.CpcvMaxCombinations),

            // Data loading
            DataScarcityThreshold            = OptimizationGridBuilder.GetConfigValue(b, "Optimization:DataScarcityThreshold", 200),
            CandleLookbackMonths             = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CandleLookbackMonths", 6),
            CandleLookbackAutoScale          = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CandleLookbackAutoScale", true),
            UseSymbolSpecificSpread          = OptimizationGridBuilder.GetConfigValue(b, "Optimization:UseSymbolSpecificSpread", true),
            RegimeBlendRatio                 = OptimizationGridBuilder.GetConfigValue(b, "Optimization:RegimeBlendRatio", 0.20),
            MaxCrossRegimeEvals              = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxCrossRegimeEvals", 4),
            RegimeStabilityHours             = OptimizationGridBuilder.GetConfigValue(b, "Optimization:RegimeStabilityHours", 6),

            // Suppression / deferral
            SuppressDuringDrawdownRecovery   = OptimizationGridBuilder.GetConfigValue(b, "Optimization:SuppressDuringDrawdownRecovery", true),
            SeasonalBlackoutEnabled          = OptimizationGridBuilder.GetConfigValue(b, "Optimization:SeasonalBlackoutEnabled", true),
            BlackoutPeriods                  = OptimizationGridBuilder.GetConfigValue(b, "Optimization:BlackoutPeriods", "12/20-01/05"),
            RequireEADataAvailability        = OptimizationGridBuilder.GetConfigValue(b, "Optimization:RequireEADataAvailability", true),

            // Retry / escalation
            MaxRetryAttempts                 = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxRetryAttempts", 2),
            MaxConsecutiveFailuresBeforeEscalation = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxConsecutiveFailuresBeforeEscalation", 3),
            CheckpointEveryN                 = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CheckpointEveryN", p.CheckpointEveryN),
            MaxConcurrentRuns                = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxConcurrentRuns", 3),
        };
    }

    // ── Auto-scheduling, escalation, and helpers are in OptimizationWorker.Scheduling.cs ──

    // ── Config change audit ───────────────────────────────────────────────────

    private sealed record ConfigChange(string Key, string OldValue, string NewValue);

    /// <summary>
    /// Diffs two JSON-serialised <see cref="OptimizationConfigSnapshot"/> payloads and
    /// returns the list of fields whose values changed. Uses type-aware comparison
    /// (numeric, boolean, string) to avoid false positives from serializer differences
    /// (e.g. "0.5" vs "0.50", "true" vs "True").
    /// </summary>
    private static List<ConfigChange> DiffConfigSnapshots(string priorJson, string currentJson)
    {
        var changes = new List<ConfigChange>();
        try
        {
            using var priorDoc   = JsonDocument.Parse(priorJson);
            using var currentDoc = JsonDocument.Parse(currentJson);

            if (!priorDoc.RootElement.TryGetProperty("Config", out var priorConfig)) return changes;
            if (!currentDoc.RootElement.TryGetProperty("Config", out var currentConfig)) return changes;

            foreach (var prop in currentConfig.EnumerateObject())
            {
                if (!priorConfig.TryGetProperty(prop.Name, out var priorVal))
                {
                    changes.Add(new ConfigChange(prop.Name, "(absent)", prop.Value.ToString()));
                    continue;
                }

                // Type-aware comparison to avoid false positives from serializer differences
                bool equal = (prop.Value.ValueKind, priorVal.ValueKind) switch
                {
                    (JsonValueKind.Number, JsonValueKind.Number) =>
                        prop.Value.TryGetDouble(out double a) && priorVal.TryGetDouble(out double b) && a == b,
                    (JsonValueKind.True or JsonValueKind.False, JsonValueKind.True or JsonValueKind.False) =>
                        prop.Value.ValueKind == priorVal.ValueKind,
                    _ => prop.Value.ToString() == priorVal.ToString()
                };

                if (!equal)
                    changes.Add(new ConfigChange(prop.Name, priorVal.ToString(), prop.Value.ToString()));
            }
        }
        catch (JsonException) { /* malformed snapshot JSON — return empty diff */ }
        return changes;
    }

    // ── Stage: Approval Decision (Stage 13) ───────────────────────────────

    /// <summary>
    /// Applies the auto-approval decision: if all validation gates passed, updates the
    /// live strategy parameters, saves regime-conditional params, runs cross-regime
    /// evaluation, and queues validation follow-ups. Otherwise logs rejection and
    /// escalates chronic failures.
    /// </summary>
    internal async Task ApplyApprovalDecisionAsync(
        RunContext ctx, CandidateValidationResult vr,
        MarketRegimeEnum? currentRegime, DateTime candleLookbackStart,
        BacktestOptions screeningOptions)
    {
        var run = ctx.Run;
        var strategy = ctx.Strategy;
        var config = ctx.Config;
        var db = ctx.Db;
        var writeDb = ctx.WriteDb;
        var writeCtx = ctx.WriteCtx;
        var mediator = ctx.Mediator;
        var alertDispatcher = ctx.AlertDispatcher;
        var eventService = ctx.EventService;
        var ct = ctx.Ct;
        var runCt = ctx.RunCt;
        var oosHealthScore = vr.OosHealthScore;
        var oosResult = vr.OosResult;
        var winner = vr.Winner;
        decimal improvement = oosHealthScore - ctx.BaselineComparisonScore;

        if (vr.Passed)
        {
            var liveStrategy = await writeDb.Set<Strategy>()
                .FirstOrDefaultAsync(x => x.Id == run.StrategyId && !x.IsDeleted, runCt);

            if (liveStrategy is null)
            {
                OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Rejected, DateTime.UtcNow);
                run.FailureCategory = OptimizationFailureCategory.StrategyRemoved;

                try
                {
                    var rejectionReport = string.IsNullOrWhiteSpace(run.ApprovalReportJson)
                        ? new Dictionary<string, object?>()
                        : JsonSerializer.Deserialize<Dictionary<string, object?>>(run.ApprovalReportJson) ?? [];
                    rejectionReport["topCandidateFailureReason"] = "Strategy removed before approved parameters could be applied.";
                    rejectionReport["approvalBlockedReason"] = "StrategyRemoved";
                    run.ApprovalReportJson = JsonSerializer.Serialize(rejectionReport);
                }
                catch
                {
                    run.ApprovalReportJson = JsonSerializer.Serialize(new
                    {
                        topCandidateFailureReason = "Strategy removed before approved parameters could be applied.",
                        approvalBlockedReason = "StrategyRemoved",
                    });
                }

                await writeCtx.SaveChangesAsync(ct);

                _logger.LogWarning(
                    "OptimizationWorker: strategy {StrategyId} disappeared before approval for run {RunId} — rejecting auto-approval",
                    run.StrategyId, run.Id);

                try
                {
                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType = "OptimizationRun",
                        EntityId = run.Id,
                        DecisionType = "AutoApproval",
                        Outcome = "Rejected",
                        Reason = "Strategy removed before approved parameters could be applied",
                        Source = "OptimizationWorker"
                    }, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "OptimizationWorker: approval rejection audit log failed for run {RunId} (non-fatal)",
                        run.Id);
                }

                return;
            }

            var originalRunStatus = run.Status;
            var originalApprovedAt = run.ApprovedAt;
            var originalCompletedAt = run.CompletedAt;
            var originalErrorMessage = run.ErrorMessage;
            var originalValidationFollowUpsCreatedAt = run.ValidationFollowUpsCreatedAt;
            var originalValidationFollowUpStatus = run.ValidationFollowUpStatus;

            string? originalRollbackParametersJson = liveStrategy?.RollbackParametersJson;
            string? originalStrategyParametersJson = liveStrategy?.ParametersJson;
            int? originalRolloutPct = liveStrategy?.RolloutPct;
            DateTime? originalRolloutStartedAt = liveStrategy?.RolloutStartedAt;
            long? originalRolloutOptimizationRunId = liveStrategy?.RolloutOptimizationRunId;
            decimal? originalEstimatedCapacityLots = liveStrategy?.EstimatedCapacityLots;

            void RestorePreApprovalState()
            {
                run.Status = originalRunStatus;
                run.ApprovedAt = originalApprovedAt;
                run.CompletedAt = originalCompletedAt;
                run.ErrorMessage = originalErrorMessage;
                run.ValidationFollowUpsCreatedAt = originalValidationFollowUpsCreatedAt;
                run.ValidationFollowUpStatus = originalValidationFollowUpStatus;

                if (liveStrategy is null)
                    return;

                liveStrategy.RollbackParametersJson = originalRollbackParametersJson;
                liveStrategy.ParametersJson = originalStrategyParametersJson ?? liveStrategy.ParametersJson;
                liveStrategy.RolloutPct = originalRolloutPct;
                liveStrategy.RolloutStartedAt = originalRolloutStartedAt;
                liveStrategy.RolloutOptimizationRunId = originalRolloutOptimizationRunId;
                liveStrategy.EstimatedCapacityLots = originalEstimatedCapacityLots;
            }

            if (liveStrategy is not null)
            {
                // Gradual rollout: start at 25% traffic with automatic promotion/rollback
                GradualRolloutManager.StartRollout(liveStrategy, run.BestParametersJson!, run.Id, initialPct: 25);
                _logger.LogInformation(
                    "OptimizationWorker: initiated gradual rollout for strategy {Id} at 25% traffic",
                    liveStrategy.Id);

                if (oosResult.TotalTrades > 0 && oosResult.Trades is not null && oosResult.Trades.Count >= 2)
                {
                    var oosSpanDays = (oosResult.Trades[^1].ExitTime - oosResult.Trades[0].EntryTime).TotalDays;
                    if (oosSpanDays > 0)
                    {
                        double tradesPerDay = oosResult.TotalTrades / oosSpanDays;
                        decimal avgLotSize  = oosResult.Trades.Average(t => t.LotSize);
                        liveStrategy.EstimatedCapacityLots = avgLotSize * (decimal)Math.Max(1.0, tradesPerDay);
                    }
                }
            }

            bool followUpsAlreadyPresent = await EnsureValidationFollowUpsAsync(writeDb, run, strategy, config, runCt);
            if (followUpsAlreadyPresent)
                _metrics.OptimizationDuplicateFollowUpsPrevented.Add(1);

            if (currentRegime.HasValue)
            {
                await SaveRegimeParamsAsync(writeDb, writeCtx, strategy, run, vr.Winner.ParamsJson,
                    vr.OosHealthScore, vr.CILower, currentRegime.Value, runCt, persistChanges: false);
            }

            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Approved, DateTime.UtcNow);
            var approvedEvent = new OptimizationApprovedIntegrationEvent
            {
                OptimizationRunId = run.Id,
                StrategyId        = run.StrategyId,
                Symbol            = strategy.Symbol,
                Timeframe         = strategy.Timeframe,
                Improvement       = improvement,
                OosScore          = vr.OosHealthScore,
                ApprovedAt        = run.ApprovedAt ?? DateTime.UtcNow,
            };

            try
            {
                await eventService.SaveAndPublish(writeCtx, approvedEvent);
            }
            catch (DbUpdateException ex) when (IsDuplicateFollowUpConstraintViolation(ex))
            {
                DetachPendingValidationFollowUps(writeDb, run.Id);
                run.ValidationFollowUpsCreatedAt ??= DateTime.UtcNow;
                run.ValidationFollowUpStatus ??= Domain.Enums.ValidationFollowUpStatus.Pending;
                _metrics.OptimizationDuplicateFollowUpsPrevented.Add(1);
                try
                {
                    await eventService.SaveAndPublish(writeCtx, approvedEvent);
                }
                catch (Exception retryEx)
                {
                    RestorePreApprovalState();
                    RollbackTrackedApprovalArtifacts(writeDb, run.Id, strategy.Id);
                    MarkRunFailedForRetry(
                        run,
                        $"Failed to persist approved optimization changes after duplicate follow-up retry: {retryEx.Message}",
                        OptimizationFailureCategory.Transient,
                        DateTime.UtcNow);
                    await writeCtx.SaveChangesAsync(ct);
                    _logger.LogError(retryEx,
                        "OptimizationWorker: failed to persist approval for run {RunId} after duplicate follow-up retry — marked Failed for retry",
                        run.Id);
                    return;
                }
            }
            catch (Exception ex)
            {
                RestorePreApprovalState();
                RollbackTrackedApprovalArtifacts(writeDb, run.Id, strategy.Id);
                MarkRunFailedForRetry(
                    run,
                    $"Failed to persist approved optimization changes: {ex.Message}",
                    OptimizationFailureCategory.Transient,
                    DateTime.UtcNow);
                await writeCtx.SaveChangesAsync(ct);
                _logger.LogError(ex,
                    "OptimizationWorker: failed to persist approval for run {RunId} — marked Failed for retry",
                    run.Id);
                return;
            }

            _metrics.OptimizationAutoApproved.Add(1);

            _logger.LogInformation(
                "OptimizationWorker: run {RunId} AUTO-APPROVED — improvement={Imp:+0.00;-0.00}, OOS={Score:F2}, CI_lower={CIL:F2}, WF_Avg={WF:F2}",
                run.Id, improvement, vr.OosHealthScore, vr.CILower, vr.WfAvgScore);

            try
            {
                await mediator.Send(new LogDecisionCommand
                {
                    EntityType = "OptimizationRun", EntityId = run.Id,
                    DecisionType = "AutoApproval", Outcome = "Approved",
                    Reason = $"OOS={vr.OosHealthScore:F2}, CI95=[{vr.CILower:F2},{vr.CIUpper:F2}], " +
                             $"WF={vr.WfAvgScore:F2}, Sens=pass, CostPess={vr.PessimisticScore:F2}; params applied",
                    Source = "OptimizationWorker"
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "OptimizationWorker: approval audit log failed for run {RunId} (non-fatal)",
                    run.Id);
            }

            // Cross-regime evaluation with scoped timeout
            using var crossRegimeCts = CancellationTokenSource.CreateLinkedTokenSource(runCt);
            crossRegimeCts.CancelAfter(TimeSpan.FromMinutes(
                Math.Max(2, config.MaxRunTimeoutMinutes / 4)));
            var crossRegimeCt = crossRegimeCts.Token;

            if (currentRegime.HasValue)
            {
                try
                {
                    var lookbackEndUtc = DateTime.UtcNow;
                    var snapshotHistory = await db.Set<MarketRegimeSnapshot>()
                        .Where(s => s.Symbol == strategy.Symbol
                                 && s.Timeframe == strategy.Timeframe
                                 && s.DetectedAt <= lookbackEndUtc
                                 && !s.IsDeleted)
                        .OrderBy(s => s.DetectedAt)
                        .ToListAsync(crossRegimeCt);

                    var otherRegimes = snapshotHistory
                        .Where(s => s.DetectedAt >= candleLookbackStart && s.Regime != currentRegime.Value)
                        .Select(s => s.Regime)
                        .Distinct()
                        .Take(config.MaxCrossRegimeEvals)
                        .ToList();

                    var lookbackCandles = await db.Set<Candle>()
                        .Where(c => c.Symbol == strategy.Symbol
                                 && c.Timeframe == strategy.Timeframe
                                 && c.Timestamp >= candleLookbackStart
                                 && c.Timestamp <= lookbackEndUtc
                                 && c.IsClosed
                                 && !c.IsDeleted)
                        .OrderBy(c => c.Timestamp)
                        .ToListAsync(crossRegimeCt);

                    var regimeCandleSets = new List<(MarketRegimeEnum Regime, List<Candle> Candles)>();
                    foreach (var otherRegime in otherRegimes)
                    {
                        try
                        {
                            var regimeIntervals = BuildRegimeIntervals(
                                snapshotHistory,
                                otherRegime,
                                candleLookbackStart,
                                lookbackEndUtc);

                            if (regimeIntervals.Count == 0) continue;
                            var regimeCandles = FilterCandlesByIntervals(lookbackCandles, regimeIntervals);

                            if (regimeCandles.Count >= 50)
                                regimeCandleSets.Add((otherRegime, regimeCandles));
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex,
                                "OptimizationWorker: cross-regime candle load for {Regime} failed (non-fatal)",
                                otherRegime);
                        }
                    }

                    var crossRegimeResults = new List<(MarketRegimeEnum Regime, decimal Score)>();
                    var crossRegimeLock = new object();
                    await Parallel.ForEachAsync(regimeCandleSets, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = config.MaxParallelBacktests,
                        CancellationToken = crossRegimeCt,
                    }, async (entry, pCt) =>
                    {
                        try
                        {
                            var regimeResult = await _validator.RunWithTimeoutAsync(
                                strategy, vr.Winner.ParamsJson, entry.Candles,
                                screeningOptions, config.ScreeningTimeoutSeconds, pCt);
                            decimal regimeScore = OptimizationHealthScorer.ComputeHealthScore(regimeResult);
                            lock (crossRegimeLock) crossRegimeResults.Add((entry.Regime, regimeScore));
                        }
                        catch (OperationCanceledException) when (pCt.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex,
                                "OptimizationWorker: cross-regime evaluation for {Regime} failed (non-fatal)",
                                entry.Regime);
                        }
                    });

                    foreach (var (regime, regimeScore) in crossRegimeResults)
                    {
                        if (regimeScore >= config.AutoApprovalMinHealthScore * 0.80m)
                        {
                            await SaveRegimeParamsAsync(writeDb, writeCtx, strategy, run, vr.Winner.ParamsJson,
                                regimeScore, regimeScore * 0.85m, regime, ct);
                            _logger.LogDebug(
                                "OptimizationWorker: cross-regime save for {Symbol}/{Regime} — score={Score:F2}",
                                strategy.Symbol, regime, regimeScore);
                        }
                    }
                }
                catch (OperationCanceledException) when (crossRegimeCts.IsCancellationRequested && !runCt.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "OptimizationWorker: run {RunId} cross-regime evaluation timed out ({Limit}min) — " +
                        "primary regime params already saved, continuing with follow-ups",
                        run.Id, Math.Max(2, config.MaxRunTimeoutMinutes / 4));
                }
                catch (OperationCanceledException) when (runCt.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "OptimizationWorker: cross-regime persistence failed for approved run {RunId} (non-fatal)",
                        run.Id);
                }
            }

        }
        else
        {
            _metrics.OptimizationAutoRejected.Add(1);

            // Persist top 3 failed candidates info for debugging / dead-letter diagnostics
            try
            {
                var failedCandidates = (vr.FailedCandidates ?? [])
                    .Select(f => new { Rank = f.Rank, Params = f.Params, Reason = f.Reason, Score = f.Score })
                    .ToArray();

                // If no failed candidates were collected, fall back to the top result
                if (failedCandidates.Length == 0)
                {
                    failedCandidates = [new
                    {
                        Rank = 1,
                        Params = vr.Winner?.ParamsJson ?? "?",
                        Reason = vr.FailureReason ?? "unknown",
                        Score = vr.OosHealthScore,
                    }];
                }

                var existingReport = run.ApprovalReportJson;
                if (string.IsNullOrWhiteSpace(existingReport))
                {
                    run.ApprovalReportJson = JsonSerializer.Serialize(new
                    {
                        failedCandidates,
                        topCandidateFailureReason = vr.FailureReason,
                    });
                }
                else
                {
                    // Merge with existing report — append failure diagnostics
                    var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingReport)
                        ?? new Dictionary<string, JsonElement>();
                    existing["failedCandidates"] = JsonSerializer.SerializeToElement(failedCandidates);
                    existing["topCandidateFailureReason"] = JsonSerializer.SerializeToElement(vr.FailureReason);
                    existing["failedCandidateScore"] = JsonSerializer.SerializeToElement(vr.OosHealthScore);
                    run.ApprovalReportJson = JsonSerializer.Serialize(existing);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OptimizationWorker: failed to persist rejection diagnostics for run {RunId}", run.Id);
            }

            await writeCtx.SaveChangesAsync(ct);

            _logger.LogInformation(
                "OptimizationWorker: run {RunId} requires manual review — {Reason}",
                run.Id, vr.FailureReason);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType = "OptimizationRun", EntityId = run.Id,
                DecisionType = "AutoApproval", Outcome = "ManualReviewRequired",
                Reason = vr.FailureReason,
                Source = "OptimizationWorker"
            }, ct);

            await EscalateChronicFailuresAsync(
                db, writeDb, writeCtx, mediator, alertDispatcher, run.StrategyId,
                strategy.Name, config.MaxConsecutiveFailuresBeforeEscalation,
                config.CooldownDays, ct);
        }
    }

    // ── Stage: Load & Validate Candles (Stages 3–4) ──────────────────────────

    /// <summary>
    /// Loads regime-aware candles, validates data quality, splits into train/test with
    /// embargo, builds transaction cost options, and runs the baseline backtest.
    /// </summary>
    internal async Task<DataLoadResult> LoadAndValidateCandlesAsync(
        DbContext db, OptimizationRun run, Strategy strategy,
        OptimizationConfig config, CancellationToken runCt)
    {
        // Auto-scale lookback by timeframe: higher timeframes need more months to
        // accumulate enough candles. When CandleLookbackAutoScale is true, the
        // configured CandleLookbackMonths is treated as a base and overridden per
        // timeframe. When false, the configured value is used as-is.
        int effectiveLookbackMonths = config.CandleLookbackMonths;
        if (config.CandleLookbackAutoScale)
        {
            effectiveLookbackMonths = strategy.Timeframe switch
            {
                Timeframe.D1  => 24,
                Timeframe.H4  => 12,
                Timeframe.H1  => 6,
                Timeframe.M15 => 3,
                Timeframe.M5  => 2,
                Timeframe.M1  => 2,
                _             => 6,
            };
        }
        var candleLookbackStart = DateTime.UtcNow.AddMonths(-effectiveLookbackMonths);
        var allCandles = await db.Set<Candle>()
            .Where(x => x.Symbol == strategy.Symbol
                     && x.Timeframe == strategy.Timeframe
                     && x.Timestamp >= candleLookbackStart
                     && x.Timestamp <= DateTime.UtcNow
                     && x.IsClosed && !x.IsDeleted)
            .OrderBy(x => x.Timestamp)
            .ToListAsync(runCt);

        if (allCandles.Count == 0)
            throw new DataQualityException(
                $"No candles found for {strategy.Symbol}/{strategy.Timeframe} in the last {effectiveLookbackMonths} months.");

        var candles = await GetRegimeAwareCandlesAsync(db, strategy.Symbol, strategy.Timeframe, allCandles, runCt, config.RegimeBlendRatio);

        var pairInfo = await db.Set<CurrencyPair>()
            .FirstOrDefaultAsync(p => p.Symbol == strategy.Symbol && !p.IsDeleted, runCt);
        var strategyCurrencies = ResolveStrategyCurrencies(strategy.Symbol, pairInfo);

        // Data quality validation (holiday-aware)
        var holidayQuery = db.Set<EconomicEvent>()
            .Where(e => e.Impact == EconomicImpact.Holiday
                     && e.ScheduledAt >= candleLookbackStart
                     && e.ScheduledAt <= DateTime.UtcNow
                     && !e.IsDeleted);
        if (strategyCurrencies.Count > 0)
            holidayQuery = holidayQuery.Where(e => strategyCurrencies.Contains(e.Currency));

        var holidayDates = await holidayQuery
            .Select(e => e.ScheduledAt.Date)
            .Distinct()
            .ToListAsync(runCt);
        var holidaySet = new HashSet<DateTime>(holidayDates);

        // Impute minor candle gaps (1-2 missing bars) before hard validation so
        // harmless feed blemishes do not defer an otherwise healthy optimization run.
        var (imputedCandles, imputedCount) = OptimizationValidator.ImputeMinorGaps(
            candles, strategy.Timeframe, maxImputeBars: 2, holidayDates: holidaySet);
        if (imputedCount > 0)
        {
            _logger.LogDebug(
                "OptimizationWorker: imputed {Count} minor candle gap(s) for {Symbol}/{Tf}",
                imputedCount, strategy.Symbol, strategy.Timeframe);
            candles = imputedCandles;
        }

        var (dataValid, dataIssues) = OptimizationValidator.ValidateCandleQuality(
            candles, strategy.Timeframe, holidayDates: holidaySet);
        if (!dataValid)
        {
            _logger.LogWarning(
                "OptimizationWorker: run {RunId} data quality check failed for {Symbol}/{Tf} — {Issues}",
                run.Id, strategy.Symbol, strategy.Timeframe, dataIssues);
            throw new DataQualityException(
                $"Data quality validation failed for {strategy.Symbol}/{strategy.Timeframe}: {dataIssues}");
        }

        // Data scarcity protocol + train/test split
        var protocol   = OptimizationGridBuilder.GetDataProtocol(candles.Count, config.DataScarcityThreshold);
        int splitIndex = (int)(candles.Count * protocol.TrainRatio);
        int embargoSize = Math.Max(1, (int)(candles.Count * config.EmbargoRatio));
        var trainCandles = candles.Take(splitIndex).ToList();
        var testCandles  = candles.Skip(splitIndex + embargoSize).ToList();

        if (trainCandles.Count < 50)
            throw new DataQualityException(
                $"Insufficient training candles ({trainCandles.Count}) for {strategy.Symbol}/{strategy.Timeframe}.");
        if (testCandles.Count == 0)
            throw new DataQualityException(
                $"No OOS candles after embargo for {strategy.Symbol}/{strategy.Timeframe} " +
                $"(total={candles.Count}, split={splitIndex}, embargo={embargoSize}).");

        // Build transaction cost options from symbol metadata
        var pointSize = pairInfo != null && pairInfo.DecimalPlaces > 0
            ? 1.0m / (decimal)Math.Pow(10, pairInfo.DecimalPlaces)
            : 0.00001m;

        double effectiveSpreadPoints = config.ScreeningSpreadPoints;
        if (config.UseSymbolSpecificSpread && pairInfo is not null && pairInfo.SpreadPoints > 0)
        {
            // Prefer 95th percentile spread from recent tick data when available.
            // This captures realistic worst-case spreads (news spikes, low liquidity)
            // rather than the average which underestimates real trading costs.
            double? p95Spread = null;
            var spreadCutoff = DateTime.UtcNow.AddDays(-7);
            try
            {
                // Native PostgreSQL PERCENTILE_CONT: computes the 95th percentile
                // server-side in a single pass without transferring rows to the app.
                var p95Result = await db.Database.SqlQueryRaw<double>(
                    @"SELECT COALESCE(PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY ""SpreadPoints""), 0)
                      FROM ""TickRecords""
                      WHERE ""Symbol"" = {0}
                        AND ""IsDeleted"" = false
                        AND ""SpreadPoints"" > 0
                        AND ""TickTimestamp"" >= {1}
                      HAVING COUNT(*) >= 100",
                    strategy.Symbol, spreadCutoff)
                    .FirstOrDefaultAsync(runCt);

                if (p95Result > 0)
                    p95Spread = p95Result;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OptimizationWorker: native P95 spread query failed for {Symbol}, falling back to Skip/Take", strategy.Symbol);

                // Fallback to EF Core Skip/Take for non-PostgreSQL databases
                try
                {
                    var tickCount = await db.Set<TickRecord>()
                        .Where(tr => tr.Symbol == strategy.Symbol && !tr.IsDeleted
                                  && tr.SpreadPoints > 0
                                  && tr.TickTimestamp >= spreadCutoff)
                        .CountAsync(runCt);

                    if (tickCount >= 100)
                    {
                        int skipCount = (int)(tickCount * 0.95);
                        var p95Values = await db.Set<TickRecord>()
                            .Where(tr => tr.Symbol == strategy.Symbol && !tr.IsDeleted
                                      && tr.SpreadPoints > 0
                                      && tr.TickTimestamp >= spreadCutoff)
                            .OrderBy(tr => tr.SpreadPoints)
                            .Skip(skipCount)
                            .Take(1)
                            .Select(tr => tr.SpreadPoints)
                            .ToListAsync(runCt);

                        if (p95Values.Count > 0)
                            p95Spread = (double)p95Values[0];
                    }
                }
                catch (Exception innerEx)
                {
                    _logger.LogDebug(innerEx, "OptimizationWorker: fallback P95 spread query also failed for {Symbol} (non-fatal)", strategy.Symbol);
                }
            }

            if (p95Spread.HasValue && p95Spread.Value > 0)
            {
                effectiveSpreadPoints = Math.Max(config.ScreeningSpreadPoints, p95Spread.Value);
                _logger.LogDebug(
                    "OptimizationWorker: using P95 spread for {Symbol}: {Spread:F1} points (from {Window} recent ticks)",
                    strategy.Symbol, effectiveSpreadPoints, "7d");
            }
            else
            {
                effectiveSpreadPoints = Math.Max(config.ScreeningSpreadPoints, pairInfo.SpreadPoints * 1.5);
                _logger.LogDebug(
                    "OptimizationWorker: using symbol-specific spread for {Symbol}: {Spread:F1} points (avg={Avg:F1}×1.5, no P95 data)",
                    strategy.Symbol, effectiveSpreadPoints, pairInfo.SpreadPoints);
            }
        }

        var screeningOptions = new BacktestOptions
        {
            SpreadPriceUnits   = pointSize * (decimal)effectiveSpreadPoints,
            CommissionPerLot   = (decimal)config.ScreeningCommissionPerLot,
            SlippagePriceUnits = pointSize * (decimal)config.ScreeningSlippagePips * 10,
            ContractSize       = pairInfo?.ContractSize ?? 100_000m,
        };

        // P1: Wire dynamic spread function from SpreadProfile for realistic cost modeling
        try
        {
            await using var spreadScope = _scopeFactory.CreateAsyncScope();
            var spreadProfileProvider = spreadScope.ServiceProvider.GetService<ISpreadProfileProvider>();
            if (spreadProfileProvider != null)
            {
                var profiles = await spreadProfileProvider.GetProfilesAsync(strategy.Symbol, runCt);
                var spreadFunc = spreadProfileProvider.BuildSpreadFunction(strategy.Symbol, profiles);
                if (spreadFunc != null)
                    screeningOptions.SpreadFunction = spreadFunc;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OptimizationWorker: spread profile load failed for {Symbol} (non-fatal)", strategy.Symbol);
        }

        // Baseline (regime-aware)
        run.BaselineParametersJson = CanonicalParameterJson.Normalize(strategy.ParametersJson);
        string baselineParamsJson  = CanonicalParameterJson.Normalize(strategy.ParametersJson);

        var currentRegimeForBaseline = await db.Set<MarketRegimeSnapshot>()
            .Where(s => s.Symbol == strategy.Symbol && s.Timeframe == strategy.Timeframe && !s.IsDeleted)
            .OrderByDescending(s => s.DetectedAt)
            .Select(s => (MarketRegimeEnum?)s.Regime)
            .FirstOrDefaultAsync(runCt);

        if (currentRegimeForBaseline.HasValue)
        {
            var regimeParams = await db.Set<StrategyRegimeParams>()
                .Where(p => p.StrategyId == strategy.Id
                         && p.Regime == currentRegimeForBaseline.Value
                         && !p.IsDeleted)
                .Select(p => p.ParametersJson)
                .FirstOrDefaultAsync(runCt);
            if (!string.IsNullOrWhiteSpace(regimeParams))
            {
                baselineParamsJson = CanonicalParameterJson.Normalize(regimeParams);
                _logger.LogDebug(
                    "OptimizationWorker: using regime-conditional params as baseline for {Symbol}/{Regime}",
                    strategy.Symbol, currentRegimeForBaseline.Value);
            }
        }

        var baselineResult = await _validator.RunWithTimeoutAsync(
            strategy, baselineParamsJson, trainCandles, screeningOptions, config.ScreeningTimeoutSeconds, runCt);
        run.BaselineHealthScore    = OptimizationHealthScorer.ComputeHealthScore(baselineResult);
        run.BaselineParametersJson = baselineParamsJson;
        decimal baselineComparisonScore;
        if (testCandles.Count >= config.MinOosCandlesForValidation)
        {
            var baselineOosResult = await _validator.RunWithTimeoutAsync(
                strategy, baselineParamsJson, testCandles, screeningOptions, config.ScreeningTimeoutSeconds, runCt);
            baselineComparisonScore = OptimizationHealthScorer.ComputeHealthScore(baselineOosResult);
        }
        else
        {
            baselineComparisonScore = (run.BaselineHealthScore.Value - protocol.ScorePenalty) * 0.85m;
        }

        return new DataLoadResult(strategy, candles, trainCandles, testCandles, embargoSize,
            screeningOptions, protocol, candleLookbackStart, currentRegimeForBaseline,
            baselineComparisonScore, baselineParamsJson, pairInfo);
    }

    // ── Regime-aware candle selection ────────────────────────────────────────

    /// <summary>
    /// Returns candles weighted toward the current regime, blended with a configurable
    /// ratio of non-regime candles to prevent survivorship bias. Pure regime filtering
    /// finds params that fit the current regime but has no evidence they'll survive a
    /// transition. Blending ensures the optimizer sees some out-of-regime data too.
    /// </summary>
    /// <param name="blendRatio">
    /// Fraction of non-regime candles to include (0.0 = pure regime, 1.0 = all candles).
    /// Default 0.20 means 80% regime candles + 20% non-regime candles.
    /// </param>
    private async Task<List<Candle>> GetRegimeAwareCandlesAsync(
        DbContext db, string symbol, Timeframe timeframe, List<Candle> allCandles,
        CancellationToken ct, double blendRatio = 0.20)
    {
        if (allCandles.Count < 100) return allCandles;
        if (blendRatio >= 1.0) return allCandles;

        var latestRegime = await db.Set<MarketRegimeSnapshot>()
            .Where(s => s.Symbol == symbol && s.Timeframe == timeframe && !s.IsDeleted)
            .OrderByDescending(s => s.DetectedAt)
            .FirstOrDefaultAsync(ct);

        if (latestRegime is null) return allCandles;

        DateTime regimeStartedAt = latestRegime.DetectedAt;
        var previousDifferentAt = await db.Set<MarketRegimeSnapshot>()
            .Where(s => s.Symbol == symbol
                     && s.Timeframe == timeframe
                     && !s.IsDeleted
                     && s.DetectedAt < latestRegime.DetectedAt
                     && s.Regime != latestRegime.Regime)
            .OrderByDescending(s => s.DetectedAt)
            .Select(s => (DateTime?)s.DetectedAt)
            .FirstOrDefaultAsync(ct);

        var earliestCurrentInStreak = await db.Set<MarketRegimeSnapshot>()
            .Where(s => s.Symbol == symbol
                     && s.Timeframe == timeframe
                     && !s.IsDeleted
                     && s.Regime == latestRegime.Regime
                     && (!previousDifferentAt.HasValue || s.DetectedAt > previousDifferentAt.Value))
            .OrderBy(s => s.DetectedAt)
            .Select(s => (DateTime?)s.DetectedAt)
            .FirstOrDefaultAsync(ct);

        if (earliestCurrentInStreak.HasValue)
        {
            regimeStartedAt = earliestCurrentInStreak.Value;
        }

        var regimeCandles = allCandles.Where(c => c.Timestamp >= regimeStartedAt).ToList();

        if (regimeCandles.Count >= 100)
        {
            // Blend: include a portion of non-regime candles to prevent overfitting
            // to the current regime. The non-regime candles are sampled evenly from
            // the pre-regime period to maintain temporal diversity.
            if (blendRatio > 0.0 && blendRatio < 1.0)
            {
                var nonRegimeCandles = allCandles.Where(c => c.Timestamp < regimeStartedAt).ToList();
                int blendCount = (int)(regimeCandles.Count * blendRatio / (1.0 - blendRatio));
                blendCount = Math.Min(blendCount, nonRegimeCandles.Count);

                if (blendCount > 0)
                {
                    // Deterministic seeded sample from non-regime candles. Using a
                    // Fisher-Yates partial shuffle seeded by the candle count gives
                    // better temporal diversity than the previous fixed-stride approach
                    // while remaining reproducible across runs with the same data.
                    var indices = Enumerable.Range(0, nonRegimeCandles.Count).ToArray();
                    var rng = new DeterministicRandom(nonRegimeCandles.Count ^ blendCount);
                    for (int i = 0; i < Math.Min(blendCount, indices.Length); i++)
                    {
                        int j = i + rng.Next(indices.Length - i);
                        (indices[i], indices[j]) = (indices[j], indices[i]);
                    }
                    var sampled = indices
                        .Take(blendCount)
                        .Order()
                        .Select(i => nonRegimeCandles[i])
                        .ToList();

                    var blended = sampled.Concat(regimeCandles)
                        .OrderBy(c => c.Timestamp)
                        .ToList();

                    _logger.LogDebug(
                        "OptimizationWorker: using blended regime candles for {Symbol} ({Regime}, {RegimeCount} regime + {BlendCount} non-regime bars)",
                        symbol, latestRegime.Regime, regimeCandles.Count, sampled.Count);
                    return blended;
                }
            }

            _logger.LogDebug(
                "OptimizationWorker: using regime-filtered candles for {Symbol} ({Regime}, {Count} bars from {Start:d})",
                symbol, latestRegime.Regime, regimeCandles.Count, regimeStartedAt);
            return regimeCandles;
        }

        _logger.LogDebug(
            "OptimizationWorker: regime segment too short ({Count} bars) for {Symbol} — using full candle window",
            regimeCandles.Count, symbol);
        return allCandles;
    }

    internal static HashSet<string> ResolveStrategyCurrencies(string symbol, CurrencyPair? pairInfo)
    {
        var currencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddCurrency(HashSet<string> target, string? currency)
        {
            if (string.IsNullOrWhiteSpace(currency))
                return;

            var normalized = currency.Trim().ToUpperInvariant();
            if (normalized.Length == 3)
                target.Add(normalized);
        }

        if (pairInfo is not null)
        {
            AddCurrency(currencies, pairInfo.BaseCurrency);
            AddCurrency(currencies, pairInfo.QuoteCurrency);
        }

        if (currencies.Count == 0 && symbol.Length >= 6)
        {
            AddCurrency(currencies, symbol[..3]);
            AddCurrency(currencies, symbol[3..6]);
        }

        return currencies;
    }

    private OptimizationCheckpointStore.State RestoreCheckpoint(string? checkpointJson)
        => OptimizationCheckpointStore.Restore(checkpointJson, _logger);

    private static void EnsureDeterministicSeed(OptimizationRun run)
    {
        if (run.DeterministicSeed != 0)
            return;

        run.DeterministicSeed = HashCode.Combine(run.Id, run.StrategyId, run.StartedAt);
    }

    private static async Task HeartbeatRunAsync(
        OptimizationRun run,
        IWriteApplicationDbContext writeCtx,
        CancellationToken ct)
    {
        StampHeartbeat(run);
        await writeCtx.SaveChangesAsync(ct);
    }

    private static void StampHeartbeat(OptimizationRun run)
        => OptimizationRunClaimer.StampHeartbeat(run, ExecutionLeaseDuration);

    internal static TimeSpan GetExecutionLeaseHeartbeatInterval()
    {
        long quarterLeaseTicks = ExecutionLeaseDuration.Ticks / 4;
        long minIntervalTicks = TimeSpan.FromMinutes(1).Ticks;
        long maxIntervalTicks = TimeSpan.FromMinutes(3).Ticks;
        long boundedTicks = Math.Max(minIntervalTicks, Math.Min(maxIntervalTicks, quarterLeaseTicks));
        return TimeSpan.FromTicks(boundedTicks);
    }

    private async Task MaintainExecutionLeaseAsync(long runId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(GetExecutionLeaseHeartbeatInterval());

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var db = writeCtx.GetDbContext();
                    var nowUtc = DateTime.UtcNow;

                    int updated = await db.Set<OptimizationRun>()
                        .Where(r => r.Id == runId
                                 && !r.IsDeleted
                                 && r.Status == OptimizationRunStatus.Running)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(r => r.LastHeartbeatAt, nowUtc)
                            .SetProperty(r => r.ExecutionLeaseExpiresAt, nowUtc.Add(ExecutionLeaseDuration)), ct);

                    if (updated == 0)
                        break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "OptimizationWorker: background lease heartbeat failed for run {RunId} (non-fatal)",
                        runId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when the owning run completes or the worker shuts down.
        }
    }

    private static void MarkRunFailedForRetry(
        OptimizationRun run,
        string errorMessage,
        OptimizationFailureCategory failureCategory,
        DateTime utcNow)
    {
        run.FailureCategory = failureCategory;
        OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Failed, utcNow, errorMessage);
        run.ApprovedAt = null;
        run.DeferredUntilUtc = null;
    }

    internal static string BuildFollowUpFailureAlertSymbol(long optimizationRunId)
        => $"OptimizationRun:{optimizationRunId}:FollowUp";

    internal static string PopulateFollowUpFailureAlert(
        Alert alert,
        long optimizationRunId,
        long strategyId,
        RunStatus backtestStatus,
        RunStatus walkForwardStatus,
        bool backtestQualityOk,
        bool walkForwardQualityOk,
        string backtestReason,
        string walkForwardReason,
        DateTime utcNow)
    {
        string message =
            $"Optimization follow-up validation failed for run {optimizationRunId} (strategy {strategyId}). " +
            $"Backtest={backtestStatus}, WalkForward={walkForwardStatus}, " +
            $"BacktestReason={backtestReason}, WalkForwardReason={walkForwardReason}.";

        alert.AlertType = AlertType.DataQualityIssue;
        alert.Symbol = BuildFollowUpFailureAlertSymbol(optimizationRunId);
        alert.Channel = AlertChannel.Webhook;
        alert.Destination = string.Empty;
        alert.Severity = AlertSeverity.High;
        alert.IsActive = true;
        alert.LastTriggeredAt = utcNow;
        alert.ConditionJson = JsonSerializer.Serialize(new
        {
            Type = "OptimizationFollowUpFailure",
            OptimizationRunId = optimizationRunId,
            StrategyId = strategyId,
            BacktestStatus = backtestStatus.ToString(),
            WalkForwardStatus = walkForwardStatus.ToString(),
            BacktestQualityOk = backtestQualityOk,
            WalkForwardQualityOk = walkForwardQualityOk,
            BacktestReason = backtestReason,
            WalkForwardReason = walkForwardReason,
            Message = message,
        });

        return message;
    }

    private static async Task<bool> EnsureValidationFollowUpsAsync(
        DbContext writeDb,
        OptimizationRun run,
        Strategy strategy,
        OptimizationConfig config,
        CancellationToken ct)
    {
        var fromDate = DateTime.UtcNow.AddYears(-1);
        var toDate = DateTime.UtcNow;
        string followUpParamsJson = CanonicalParameterJson.Normalize(run.BestParametersJson ?? strategy.ParametersJson);

        var existingBacktest = await writeDb.Set<BacktestRun>()
            .FirstOrDefaultAsync(r => r.SourceOptimizationRunId == run.Id && !r.IsDeleted, ct);
        bool hasBacktest = existingBacktest is not null;
        if (existingBacktest is null)
        {
            writeDb.Set<BacktestRun>().Add(new BacktestRun
            {
                StrategyId = run.StrategyId,
                Symbol = strategy.Symbol,
                Timeframe = strategy.Timeframe,
                FromDate = fromDate,
                ToDate = toDate,
                InitialBalance = config.ScreeningInitialBalance,
                Status = RunStatus.Queued,
                SourceOptimizationRunId = run.Id,
                ParametersSnapshotJson = followUpParamsJson
            });
        }
        else if (string.IsNullOrWhiteSpace(existingBacktest.ParametersSnapshotJson))
        {
            existingBacktest.ParametersSnapshotJson = followUpParamsJson;
        }

        var existingWalkForward = await writeDb.Set<WalkForwardRun>()
            .FirstOrDefaultAsync(r => r.SourceOptimizationRunId == run.Id && !r.IsDeleted, ct);
        bool hasWalkForward = existingWalkForward is not null;
        if (existingWalkForward is null)
        {
            writeDb.Set<WalkForwardRun>().Add(new WalkForwardRun
            {
                StrategyId = run.StrategyId,
                Symbol = strategy.Symbol,
                Timeframe = strategy.Timeframe,
                FromDate = fromDate,
                ToDate = toDate,
                InSampleDays = 90,
                OutOfSampleDays = 30,
                InitialBalance = config.ScreeningInitialBalance,
                ReOptimizePerFold = false,
                Status = RunStatus.Queued,
                StartedAt = DateTime.UtcNow,
                SourceOptimizationRunId = run.Id,
                ParametersSnapshotJson = followUpParamsJson
            });
        }
        else
        {
            if (string.IsNullOrWhiteSpace(existingWalkForward.ParametersSnapshotJson))
                existingWalkForward.ParametersSnapshotJson = followUpParamsJson;
            existingWalkForward.ReOptimizePerFold = false;
        }

        bool hadAllFollowUpsBeforeRepair = hasBacktest && hasWalkForward;
        if (!run.ValidationFollowUpsCreatedAt.HasValue || !hadAllFollowUpsBeforeRepair)
            run.ValidationFollowUpsCreatedAt = DateTime.UtcNow;
        run.ValidationFollowUpStatus = Domain.Enums.ValidationFollowUpStatus.Pending;
        return hadAllFollowUpsBeforeRepair;
    }

    private static bool IsDuplicateFollowUpConstraintViolation(DbUpdateException ex)
    {
        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("IX_BacktestRun_SourceOptimizationRunId", StringComparison.OrdinalIgnoreCase)
            || message.Contains("IX_WalkForwardRun_SourceOptimizationRunId", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
               && message.Contains("SourceOptimizationRunId", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActiveQueueConstraintViolation(DbUpdateException ex)
    {
        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("IX_OptimizationRun_ActivePerStrategy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
               && message.Contains("OptimizationRun", StringComparison.OrdinalIgnoreCase)
               && message.Contains("StrategyId", StringComparison.OrdinalIgnoreCase);
    }

    private static void DetachPendingValidationFollowUps(DbContext writeDb, long optimizationRunId)
    {
        foreach (var entry in writeDb.ChangeTracker.Entries<BacktestRun>()
                     .Where(e => e.State == EntityState.Added && e.Entity.SourceOptimizationRunId == optimizationRunId)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entry in writeDb.ChangeTracker.Entries<WalkForwardRun>()
                     .Where(e => e.State == EntityState.Added && e.Entity.SourceOptimizationRunId == optimizationRunId)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static void RollbackTrackedApprovalArtifacts(DbContext writeDb, long optimizationRunId, long strategyId)
    {
        try
        {
            DetachPendingValidationFollowUps(writeDb, optimizationRunId);

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
            // Best-effort cleanup only. If tracking metadata is unavailable, the run is still
            // marked failed so the retry loop can recover with a fresh DbContext next cycle.
        }
    }

    /// <summary>Upserts regime-conditional parameters for the strategy.</summary>
    private static async Task SaveRegimeParamsAsync(
        DbContext writeDb, IWriteApplicationDbContext writeCtx, Strategy strategy,
        OptimizationRun run, string paramsJson, decimal healthScore, decimal ciLower,
        MarketRegimeEnum regime, CancellationToken ct, bool persistChanges = true)
    {
        var existing = await writeDb.Set<StrategyRegimeParams>()
            .FirstOrDefaultAsync(p => p.StrategyId == strategy.Id && p.Regime == regime && !p.IsDeleted, ct);

        if (existing is not null)
        {
            existing.ParametersJson    = CanonicalParameterJson.Normalize(paramsJson);
            existing.HealthScore       = healthScore;
            existing.HealthScoreCILower = ciLower;
            existing.OptimizationRunId = run.Id;
            existing.OptimizedAt       = DateTime.UtcNow;
        }
        else
        {
            writeDb.Set<StrategyRegimeParams>().Add(new StrategyRegimeParams
            {
                StrategyId         = strategy.Id,
                Regime             = regime,
                ParametersJson     = CanonicalParameterJson.Normalize(paramsJson),
                HealthScore        = healthScore,
                HealthScoreCILower = ciLower,
                OptimizationRunId  = run.Id,
                OptimizedAt        = DateTime.UtcNow,
            });
        }

        if (persistChanges)
            await writeCtx.SaveChangesAsync(ct);
    }

    private static List<(DateTime StartUtc, DateTime EndUtc)> BuildRegimeIntervals(
        IReadOnlyList<MarketRegimeSnapshot> snapshots,
        MarketRegimeEnum regime,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc)
    {
        var intervals = new List<(DateTime StartUtc, DateTime EndUtc)>();
        if (snapshots.Count == 0 || rangeStartUtc >= rangeEndUtc)
            return intervals;

        for (int i = 0; i < snapshots.Count; i++)
        {
            var snapshot = snapshots[i];
            if (snapshot.Regime != regime)
                continue;

            var startUtc = snapshot.DetectedAt > rangeStartUtc ? snapshot.DetectedAt : rangeStartUtc;
            var endUtc = i + 1 < snapshots.Count
                ? snapshots[i + 1].DetectedAt
                : rangeEndUtc;
            if (endUtc > rangeEndUtc)
                endUtc = rangeEndUtc;

            if (startUtc < endUtc)
                intervals.Add((startUtc, endUtc));
        }

        return intervals;
    }

    private static List<Candle> FilterCandlesByIntervals(
        IReadOnlyList<Candle> candles,
        IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> intervals)
    {
        if (candles.Count == 0 || intervals.Count == 0)
            return [];

        var filtered = new List<Candle>();
        int intervalIndex = 0;

        foreach (var candle in candles)
        {
            while (intervalIndex < intervals.Count && candle.Timestamp >= intervals[intervalIndex].EndUtc)
                intervalIndex++;

            if (intervalIndex >= intervals.Count)
                break;

            var interval = intervals[intervalIndex];
            if (candle.Timestamp >= interval.StartUtc && candle.Timestamp < interval.EndUtc)
                filtered.Add(candle);
        }

        return filtered;
    }

    /// <summary>
    /// Detects EngineConfig keys prefixed with "Optimization:" or "Backtest:Gate:" that
    /// don't match any known configuration key. Almost always indicates an operator typo
    /// causing an intended override to silently fall back to defaults.
    /// </summary>
    private async Task DetectUnknownConfigKeysAsync(DbContext db, CancellationToken ct)
    {
        try
        {
            var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Optimization:Preset",
                "Optimization:SchedulePollSeconds", "Optimization:CooldownDays", "Optimization:MaxQueuedPerCycle",
                "Optimization:AutoScheduleEnabled", "Backtest:Gate:MinWinRate", "Backtest:Gate:MinProfitFactor",
                "Backtest:Gate:MinTotalTrades", "Optimization:AutoApprovalImprovementThreshold",
                "Optimization:AutoApprovalMinHealthScore", "Optimization:TopNCandidates",
                "Optimization:CoarsePhaseThreshold", "Optimization:ScreeningTimeoutSeconds",
                "Optimization:ScreeningSpreadPoints", "Optimization:ScreeningCommissionPerLot",
                "Optimization:ScreeningSlippagePips", "Optimization:MaxOosDegradationPct",
                "Optimization:SuppressDuringDrawdownRecovery", "Optimization:SeasonalBlackoutEnabled",
                "Optimization:BlackoutPeriods", "Optimization:MaxRunTimeoutMinutes",
                "Optimization:MaxParallelBacktests", "Optimization:MinCandidateTrades",
                "Optimization:EmbargoRatio", "Optimization:CorrelationParamThreshold",
                "Optimization:TpeBudget", "Optimization:TpeInitialSamples", "Optimization:PurgedKFolds",
                "Optimization:SensitivityPerturbPct", "Optimization:BootstrapIterations",
                "Optimization:MinBootstrapCILower", "Optimization:CostSensitivityEnabled",
                "Optimization:AdaptiveBoundsEnabled", "Optimization:TemporalOverlapThreshold",
                "Optimization:DataScarcityThreshold", "Optimization:ScreeningInitialBalance",
                "Optimization:PortfolioCorrelationThreshold", "Optimization:MaxConsecutiveFailuresBeforeEscalation",
                "Optimization:CheckpointEveryN", "Optimization:GpEarlyStopPatience",
                "Optimization:SensitivityDegradationTolerance", "Optimization:WalkForwardMinMaxRatio",
                "Optimization:CostStressMultiplier", "Optimization:MinOosCandlesForValidation",
                "Optimization:MaxCvCoefficientOfVariation", "Optimization:PermutationIterations",
                "Optimization:RegimeStabilityHours",
                "Optimization:MaxRetryAttempts", "Optimization:CandleLookbackMonths", "Optimization:CandleLookbackAutoScale",
                "Optimization:RequireEADataAvailability", "Optimization:MaxConcurrentRuns",
                "Optimization:UseSymbolSpecificSpread", "Optimization:RegimeBlendRatio",
                "Optimization:CpcvNFolds", "Optimization:CpcvTestFoldCount",
                "Optimization:CpcvMaxCombinations", "Optimization:CircuitBreakerThreshold",
                "Optimization:SuccessiveHalvingRungs", "Optimization:MaxCrossRegimeEvals",
                "Optimization:HyperbandEnabled", "Optimization:HyperbandEta",
                "Optimization:MaxRunsPerWeek", "Optimization:UseEhviAcquisition",
                "Optimization:UseParegoScalarization", "Optimization:MinEquityCurveR2",
                "Optimization:MaxTradeTimeConcentration",
            };

            var dbKeys = await db.Set<EngineConfig>()
                .Where(c => !c.IsDeleted
                          && (c.Key.StartsWith("Optimization:") || c.Key.StartsWith("Backtest:Gate:")))
                .Select(c => c.Key)
                .ToListAsync(ct);

            var unrecognized = dbKeys.Where(k => !knownKeys.Contains(k)).ToList();
            if (unrecognized.Count > 0)
            {
                _logger.LogWarning(
                    "OptimizationWorker: {Count} unrecognized config key(s) found — possible typos that will use defaults instead: {Keys}",
                    unrecognized.Count, string.Join(", ", unrecognized));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OptimizationWorker: config typo detection failed (non-fatal)");
        }
    }
}
