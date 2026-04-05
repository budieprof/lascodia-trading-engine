using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;
using static LascodiaTradingEngine.Application.StrategyGeneration.StrategyGenerationHelpers;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Autonomous strategy factory that systematically explores the strategy space, generates
/// regime-appropriate candidates, screens them with rapid backtests, and feeds survivors into
/// the existing validation pipeline (BacktestWorker → WalkForwardWorker → StrategyHealthWorker
/// → StrategyFeedbackWorker) for full qualification before they can generate live signals.
///
/// <b>Architecture (split across 4 files):</b>
/// <list type="bullet">
///   <item><c>StrategyGenerationWorker</c> (this file) — scheduling, orchestration, persistence, pruning.</item>
///   <item><see cref="StrategyScreeningEngine"/> — IS/OOS backtests, degradation, R², walk-forward,
///         Monte Carlo significance, portfolio drawdown filter.</item>
///   <item><see cref="StrategyGenerationHelpers"/> — pure static helpers: asset classification, ATR,
///         spread, thresholds, templates, timeframe scaling, blackout, weekend guard.</item>
///   <item><see cref="ScreeningMetrics"/> — structured JSON-serialisable screening results stored
///         on <c>Strategy.ScreeningMetricsJson</c>.</item>
/// </list>
///
/// <b>Pipeline stages:</b>
/// 1. Load hot-reloadable config from EngineConfig table.
/// 2. Pre-flight: seasonal blackout, drawdown recovery, weekend guard.
/// 3. Load symbols, regimes, existing strategies, failure memory, correlation groups.
/// 4. Screen candidates: regime-mapped types × templates × timeframes, parallel backtests.
/// 5. Strategic reserve: counter-regime candidates for regime rotation readiness.
/// 6. Portfolio drawdown filter: greedy removal of correlated candidates.
/// 7. Batch-persist with backtest queue dedup and priority ranking.
/// 8. Prune stale Draft strategies with repeated backtest failures.
/// 9. Persist last-run date and publish cycle summary event.
///
/// <b>Anti-bloat:</b> MaxCandidatesPerCycle, MaxActivePerSymbol, correlation group caps,
/// regime budget diversity, spread/ATR pre-filter, failure memory cooldown, candle staleness.
///
/// <b>Config namespace:</b> <c>StrategyGeneration:*</c> (45+ keys, all hot-reloadable).
/// Per-symbol overrides: <c>StrategyGeneration:Overrides:{SYMBOL}:{Key}</c>.
///
/// <b>DI lifetime:</b> Singleton (registered as IHostedService via auto-registration).
/// Creates scoped DI scopes per cycle for DB contexts and MediatR.
/// </summary>
public class StrategyGenerationWorker : BackgroundService
{
    // ── Inner types ─────────────────────────────────────────────────────────

    /// <summary>
    /// Per-strategy-type fault counter. When a type accumulates too many screening faults
    /// within a single cycle, it is skipped for the remainder of that cycle to prevent one
    /// broken evaluator from consuming the entire screening budget or tripping the global
    /// circuit breaker.
    /// </summary>
    /// <summary>Internal for unit test access (InternalsVisibleTo).</summary>
    internal sealed class PerTypeFaultTracker
    {
        private readonly ConcurrentDictionary<StrategyType, int> _faults = new();
        private readonly int _maxFaultsPerType;

        public PerTypeFaultTracker(int maxFaultsPerType) => _maxFaultsPerType = maxFaultsPerType;

        public void RecordFault(StrategyType type) => _faults.AddOrUpdate(type, 1, (_, c) => c + 1);

        public bool IsTypeDisabled(StrategyType type)
            => _faults.TryGetValue(type, out var count) && count >= _maxFaultsPerType;

        public IReadOnlyDictionary<StrategyType, int> GetFaultCounts()
            => _faults.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>All hot-reloadable configuration for a single generation cycle.</summary>
    private sealed record GenerationConfig
    {
        // ── Screening thresholds ──
        public int ScreeningMonths { get; init; }
        public double MinWinRate { get; init; }
        public double MinProfitFactor { get; init; }
        public int MinTotalTrades { get; init; }
        public double MaxDrawdownPct { get; init; }
        public double MinSharpe { get; init; }

        // ── Capacity limits ──
        public int MaxCandidates { get; init; }
        public int MaxActivePerSymbol { get; init; }
        public int MaxActivePerTypePerSymbol { get; init; }
        public int MaxPerCurrencyGroup { get; init; }
        public int MaxCorrelatedCandidates { get; init; }

        // ── Lifecycle ──
        public int PruneAfterFailed { get; init; }
        public int RegimeFreshnessHours { get; init; }
        public int RetryCooldownDays { get; init; }
        public int RegimeTransitionCooldownHours { get; init; }

        // ── Screening costs ──
        public double ScreeningSpreadPoints { get; init; }
        public double ScreeningCommissionPerLot { get; init; }
        public double ScreeningSlippagePips { get; init; }
        public double MaxSpreadToRangeRatio { get; init; }
        public decimal ScreeningInitialBalance { get; init; }

        // ── Regime & confidence ──
        public double MinRegimeConfidence { get; init; }
        public double MaxOosDegradationPct { get; init; }
        public double RegimeBudgetDiversityPct { get; init; }

        // ── Pre-flight guards ──
        public bool SuppressDuringDrawdownRecovery { get; init; }
        public bool SeasonalBlackoutEnabled { get; init; }
        public string BlackoutPeriods { get; init; } = "";
        public string BlackoutTimezone { get; init; } = "UTC";
        public bool SkipWeekends { get; init; }

        // ── Screening engine ──
        public int ScreeningTimeoutSeconds { get; init; }
        public IReadOnlyList<Timeframe> CandidateTimeframes { get; init; } = [];
        public int MaxTemplatesPerCombo { get; init; }
        public int MaxParallelBacktests { get; init; }
        public int MaxCandleCacheSize { get; init; }
        public int CandleChunkSize { get; init; }
        public int MaxCandleAgeHours { get; init; }

        // ── Quality gates ──
        public double MinEquityCurveR2 { get; init; }
        public double MaxTradeTimeConcentration { get; init; }

        // ── Walk-forward ──
        public int WalkForwardWindowCount { get; init; }
        public int WalkForwardMinWindowsPass { get; init; }
        public string WalkForwardSplitPcts { get; init; } = "0.40,0.55,0.70";

        // ── Monte Carlo ──
        public bool MonteCarloEnabled { get; init; }
        public int MonteCarloPermutations { get; init; }
        public double MonteCarloMinPValue { get; init; }
        public bool MonteCarloShuffleEnabled { get; init; }
        public int MonteCarloShufflePermutations { get; init; }
        public double MonteCarloShuffleMinPValue { get; init; }

        // ── Portfolio filter ──
        public bool PortfolioBacktestEnabled { get; init; }
        public double MaxPortfolioDrawdownPct { get; init; }
        public double PortfolioCorrelationWeight { get; init; }

        // ── Strategic reserve ──
        public int StrategicReserveQuota { get; init; }

        // ── Cross-cycle velocity cap ──
        public int MaxCandidatesPerWeek { get; init; }

        // ── Parallel symbol processing ──
        public int MaxParallelSymbols { get; init; }

        // ── Adaptive thresholds ──
        public bool AdaptiveThresholdsEnabled { get; init; }
        public int AdaptiveThresholdsMinSamples { get; init; }

        // ── Circuit breaker ──
        public int CircuitBreakerMaxFailures { get; init; }
        public int CircuitBreakerBackoffDays { get; init; }
        public int MaxFaultsPerStrategyType { get; init; }

        // ── Portfolio-level ──
        public int ActiveStrategyCount { get; set; }
    }

    /// <summary>Adaptive threshold multipliers computed from recent screening distributions.</summary>
    private sealed record AdaptiveThresholdAdjustments(
        double WinRateMultiplier, double ProfitFactorMultiplier,
        double SharpeMultiplier, double DrawdownMultiplier)
    {
        public static readonly AdaptiveThresholdAdjustments Neutral = new(1.0, 1.0, 1.0, 1.0);
    }

    /// <summary>Typed projection of existing strategy data loaded in Stage 4, replacing dynamic.</summary>
    private sealed record ExistingStrategyInfo(
        long Id, StrategyType StrategyType, string Symbol, Timeframe Timeframe,
        StrategyStatus Status, StrategyLifecycleStage LifecycleStage);

    /// <summary>Key metrics from a generation cycle, persisted for cross-cycle comparison.</summary>
    private sealed record CycleStats(int CandidatesCreated, int StrategiesPruned, int SymbolsProcessed, double DurationMs);

    /// <summary>Minimal projection of recently pruned strategies used for cooldown memory.</summary>
    private sealed record PrunedStrategyInfo(
        StrategyType StrategyType, string Symbol, Timeframe Timeframe, string? ParametersJson);

    /// <summary>
    /// Bundles all shared state needed by screening methods. Deliberately a class (not a record)
    /// because it holds mutable collections (ExistingSet, CorrelationGroupCounts, PrunedTemplates)
    /// that are modified during the screening cycle.
    /// </summary>
    private sealed class ScreeningContext
    {
        public required GenerationConfig Config { get; init; }
        public required Dictionary<string, string> RawConfigs { get; init; }
        public required StrategyScreeningEngine ScreeningEngine { get; init; }
        public required IReadOnlyList<ExistingStrategyInfo> Existing { get; init; }
        public required HashSet<(StrategyType, string, Timeframe)> ExistingSet { get; init; }
        public required Dictionary<(StrategyType, string, Timeframe), HashSet<string>> PrunedTemplates { get; init; }
        public required HashSet<(StrategyType, string, Timeframe)> FullyPrunedCombos { get; init; }
        public required Dictionary<string, int> ActiveCountBySymbol { get; init; }
        public required Dictionary<string, MarketRegimeEnum> RegimeBySymbol { get; init; }
        public required Dictionary<(string, Timeframe), MarketRegimeEnum> RegimeBySymbolTf { get; init; }
        public required Dictionary<string, CurrencyPair> PairDataBySymbol { get; init; }
        public required Dictionary<(StrategyType, MarketRegimeEnum), double> FeedbackRates { get; init; }
        public required AdaptiveThresholdAdjustments AdaptiveAdjustments { get; init; }
        public required Dictionary<int, int> CorrelationGroupCounts { get; init; }
        public required List<string> ActivePairs { get; init; }
        public required ScreeningAuditLogger AuditLogger { get; init; }
        public required Dictionary<string, double> RegimeConfidenceBySymbol { get; init; }
        public required PerTypeFaultTracker FaultTracker { get; init; }
        public required IReadOnlyDictionary<string, double> TemplateSurvivalRates { get; init; }
        public required Dictionary<string, MarketRegimeEnum> RegimeTransitions { get; init; }
        public required Dictionary<string, DateTime> RegimeDetectedAtBySymbol { get; init; }
        public required HashSet<string> TransitionSymbols { get; init; }
        public HaircutRatios? Haircuts { get; init; }
        public IReadOnlyList<(DateTime Date, decimal Equity)>? PortfolioEquityCurve { get; init; }
        public ISpreadProfileProvider? SpreadProfileProvider { get; init; }
    }

    /// <summary>Per-symbol/timeframe computed values passed to <see cref="BuildScreeningTasks"/>.</summary>
    private sealed record ScreeningTaskArgs(
        List<Candle> Candles, decimal Atr,
        string Symbol, Timeframe Timeframe, MarketRegimeEnum Regime,
        IReadOnlyList<StrategyType> SuitableTypes,
        Dictionary<StrategyType, int> ActiveTypeCountsForSymbol,
        BacktestOptions ScreeningOptions, ScreeningThresholds Thresholds,
        ScreeningConfig ScreeningConfig, int MaxTemplates, int CandidatesCreated);

    /// <summary>
    /// Simple LRU cache for candle data, keyed by (Symbol, Timeframe). Evicts the
    /// least-recently-used entry when the total candle count exceeds the configured limit,
    /// rather than silently skipping remaining symbol/timeframe combos.
    /// </summary>
    internal sealed class CandleLruCache
    {
        private readonly int _maxCandles;
        private readonly LinkedList<(string Symbol, Timeframe Tf)> _accessOrder = new();
        private readonly Dictionary<(string, Timeframe), (LinkedListNode<(string, Timeframe)> Node, List<Candle> Candles)> _entries = new();
        private int _totalCandles;

        public CandleLruCache(int maxCandles) => _maxCandles = maxCandles;
        public bool IsFull => _totalCandles >= _maxCandles;

        public bool TryGet((string, Timeframe) key, out List<Candle> candles)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                // Move to front (most recently used)
                _accessOrder.Remove(entry.Node);
                _accessOrder.AddFirst(entry.Node);
                candles = entry.Candles;
                return true;
            }
            candles = [];
            return false;
        }

        public void Put((string, Timeframe) key, List<Candle> candles)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _totalCandles -= existing.Candles.Count;
                _accessOrder.Remove(existing.Node);
                _entries.Remove(key);
            }
            var node = _accessOrder.AddFirst(key);
            _entries[key] = (node, candles);
            _totalCandles += candles.Count;
        }

        public (string Symbol, Timeframe Tf)? EvictLru()
        {
            if (_accessOrder.Last is null) return null;
            var lruKey = _accessOrder.Last.Value;
            if (_entries.TryGetValue(lruKey, out var entry))
            {
                _totalCandles -= entry.Candles.Count;
                _accessOrder.Remove(entry.Node);
                _entries.Remove(lruKey);
                return lruKey;
            }
            return null;
        }
    }

    /// <summary>
    /// Encapsulates all mutable scheduling state with explicit transition methods.
    /// Makes the four interleaved state paths (success, failure-with-retry, failure-exhausted,
    /// circuit-breaker-skip) formally auditable instead of scattered across ExecuteAsync.
    /// </summary>
    private sealed class SchedulingState
    {
        private const int MaxRetriesPerWindow = 2;

        public DateTime LastRunDateUtc { get; private set; } = DateTime.MinValue;
        public int ConsecutiveFailures { get; private set; }
        public DateTime CircuitBreakerUntilUtc { get; private set; } = DateTime.MinValue;
        public int RetriesThisWindow { get; private set; }
        public DateTime RetryWindowDateUtc { get; private set; } = DateTime.MinValue;
        public bool IsLoaded { get; private set; }
        private bool _wasInWindow;

        public bool IsCircuitBreakerActive => DateTime.UtcNow < CircuitBreakerUntilUtc;
        public bool HasRunToday => LastRunDateUtc >= DateTime.UtcNow.Date;
        public bool RetriesExhausted => RetriesThisWindow >= MaxRetriesPerWindow;

        /// <summary>Load persisted state from DB on first poll.</summary>
        public void LoadFromPersisted(
            DateTime lastRunDate,
            int consecutiveFailures,
            DateTime circuitBreakerUntil,
            int retriesThisWindow,
            DateTime retryWindowDate)
        {
            if (lastRunDate != DateTime.MinValue) LastRunDateUtc = lastRunDate;
            ConsecutiveFailures = consecutiveFailures;
            CircuitBreakerUntilUtc = circuitBreakerUntil;
            RetryWindowDateUtc = retryWindowDate.Date;
            RetriesThisWindow = RetryWindowDateUtc == DateTime.UtcNow.Date ? retriesThisWindow : 0;
            IsLoaded = true;
        }

        /// <summary>Transition: generation cycle completed successfully.</summary>
        public void OnSuccess()
        {
            LastRunDateUtc = DateTime.UtcNow.Date;
            ConsecutiveFailures = 0;
            RetriesThisWindow = 0;
            RetryWindowDateUtc = DateTime.MinValue;
            _wasInWindow = false;
        }

        /// <summary>Transition: generation cycle failed. Returns true if circuit breaker tripped.</summary>
        public bool OnFailure(int maxFailures, int backoffDays)
        {
            if (RetryWindowDateUtc != DateTime.UtcNow.Date)
                RetriesThisWindow = 0;

            ConsecutiveFailures++;
            RetriesThisWindow++;
            RetryWindowDateUtc = DateTime.UtcNow.Date;

            if (ConsecutiveFailures >= maxFailures)
            {
                CircuitBreakerUntilUtc = DateTime.UtcNow.Date.AddDays(backoffDays);
                return true;
            }
            return false;
        }

        /// <summary>Transition: retries exhausted within schedule window — skip to next day.</summary>
        public void OnRetriesExhausted()
        {
            LastRunDateUtc = DateTime.UtcNow.Date;
            RetriesThisWindow = 0;
            RetryWindowDateUtc = DateTime.MinValue;
            _wasInWindow = false;
        }

        /// <summary>Transition: circuit breaker active — skip today.</summary>
        public void OnCircuitBreakerSkip()
        {
            LastRunDateUtc = DateTime.UtcNow.Date;
            RetriesThisWindow = 0;
            RetryWindowDateUtc = DateTime.MinValue;
            _wasInWindow = false;
        }

        /// <summary>Signal that we are inside the schedule window (enables edge-triggered reset on exit).</summary>
        public void MarkInWindow() => _wasInWindow = true;

        /// <summary>Transition: schedule window passed — reset retry counter once on window exit.</summary>
        public void OnWindowPassed()
        {
            if (!_wasInWindow && RetryWindowDateUtc == DateTime.MinValue) return;
            RetriesThisWindow = 0;
            RetryWindowDateUtc = DateTime.MinValue;
            _wasInWindow = false;
        }
    }

    // ── Fields ──────────────────────────────────────────────────────────────

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBacktestEngine _backtestEngine;
    private readonly IRegimeStrategyMapper _regimeMapper;
    private readonly IStrategyParameterTemplateProvider _templateProvider;
    private readonly ILivePriceCache _livePriceCache;
    private readonly TradingMetrics _metrics;
    private readonly IFeedbackDecayMonitor _feedbackDecayMonitor;
    private readonly string[][] _correlationGroups;
    private readonly SchedulingState _scheduling = new();

    // ── Constructor (#5: null-guard correlation groups) ─────────────────────

    public StrategyGenerationWorker(
        ILogger<StrategyGenerationWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBacktestEngine backtestEngine,
        IRegimeStrategyMapper regimeMapper,
        IStrategyParameterTemplateProvider templateProvider,
        ILivePriceCache livePriceCache,
        TradingMetrics metrics,
        IFeedbackDecayMonitor feedbackDecayMonitor,
        CorrelationGroupOptions correlationGroupOptions)
    {
        _logger            = logger;
        _scopeFactory      = scopeFactory;
        _backtestEngine    = backtestEngine;
        _regimeMapper      = regimeMapper;
        _templateProvider  = templateProvider;
        _livePriceCache    = livePriceCache;
        _metrics               = metrics;
        _feedbackDecayMonitor  = feedbackDecayMonitor;
        _correlationGroups     = correlationGroupOptions?.Groups ?? Array.Empty<string[]>();
    }

    // ── Main loop ───────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyGenerationWorker starting");
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ExecutePollAsync(stoppingToken);
            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("StrategyGenerationWorker stopped");
    }

    internal async Task ExecutePollAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var db = ctx.GetDbContext();

            bool enabled = await GetConfigAsync<bool>(db, "StrategyGeneration:Enabled", true, stoppingToken);
            if (!enabled) return;

            int scheduleHour = await GetConfigAsync(db, "StrategyGeneration:ScheduleHourUtc", 2, stoppingToken);

            // One-time load of persisted state on startup.
            if (!_scheduling.IsLoaded)
            {
                var persistedDate = await GetConfigAsync(db, "StrategyGeneration:LastRunDateUtc", "", stoppingToken);
                DateTime.TryParse(persistedDate, out var parsedDate);
                var cbUntil = await GetConfigAsync(db, "StrategyGeneration:CircuitBreakerUntilUtc", "", stoppingToken);
                DateTime.TryParse(cbUntil, out var parsedCb);
                int failures = await GetConfigAsync(db, "StrategyGeneration:ConsecutiveFailures", 0, stoppingToken);
                int retriesThisWindow = await GetConfigAsync(db, "StrategyGeneration:RetriesThisWindow", 0, stoppingToken);
                var retryWindowDate = await GetConfigAsync(db, "StrategyGeneration:RetryWindowDateUtc", "", stoppingToken);
                DateTime.TryParse(retryWindowDate, out var parsedRetryWindow);
                _scheduling.LoadFromPersisted(
                    parsedDate.Date,
                    failures,
                    parsedCb,
                    retriesThisWindow,
                    parsedRetryWindow.Date);
            }

            // Not in schedule window or already ran today.
            if (DateTime.UtcNow.Hour != scheduleHour || _scheduling.HasRunToday)
            {
                if (DateTime.UtcNow.Hour != scheduleHour)
                    _scheduling.OnWindowPassed();
                return;
            }

            _scheduling.MarkInWindow();

            if (_scheduling.IsCircuitBreakerActive)
            {
                _logger.LogWarning("StrategyGenerationWorker: circuit breaker active until {Until:u}",
                    _scheduling.CircuitBreakerUntilUtc);
                _metrics.StrategyGenCircuitBreakerTripped.Add(1);
                _scheduling.OnCircuitBreakerSkip();
                await PersistSchedulingStateAsync(stoppingToken);
                return;
            }

            await RunGenerationCycleAsync(stoppingToken);
            _scheduling.OnSuccess();
            await PersistSchedulingStateAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StrategyGenerationWorker: error during generation cycle");
            _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", "StrategyGenerationWorker"));

            int maxFailures = 3, backoffDays = 2;
            try
            {
                using var cfgScope = _scopeFactory.CreateScope();
                var cfgCtx = cfgScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                maxFailures = await GetConfigAsync(cfgCtx.GetDbContext(), "StrategyGeneration:CircuitBreakerMaxFailures", 3, stoppingToken);
                backoffDays = await GetConfigAsync(cfgCtx.GetDbContext(), "StrategyGeneration:CircuitBreakerBackoffDays", 2, stoppingToken);
            }
            catch { /* Use defaults */ }

            bool tripped = _scheduling.OnFailure(maxFailures, backoffDays);
            if (tripped)
            {
                _logger.LogError("StrategyGenerationWorker: {Failures} failures — circuit breaker until {Until:u}",
                    _scheduling.ConsecutiveFailures, _scheduling.CircuitBreakerUntilUtc);
                _metrics.StrategyGenCircuitBreakerTripped.Add(1);
            }

            if (_scheduling.RetriesExhausted)
            {
                _scheduling.OnRetriesExhausted();
                _logger.LogWarning("StrategyGenerationWorker: exhausted retries within schedule window — skipping to next day");
            }

            await PersistSchedulingStateAsync(stoppingToken);
        }
    }

    // ── Core generation cycle ──────────────────────────────────────────────

    internal async Task RunGenerationCycleAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        using var scope = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db       = readCtx.GetDbContext();

        // P1/P2/P3: Resolve optional priority services (scoped — must come from scope, not ctor)
        var spreadProfileProvider = scope.ServiceProvider.GetService<ISpreadProfileProvider>();
        var liveBenchmark = scope.ServiceProvider.GetService<ILivePerformanceBenchmark>();
        var portfolioProvider = scope.ServiceProvider.GetService<IPortfolioEquityCurveProvider>();

        // ── Stage 1: Load configuration ─────────────────────────────────
        var (config, rawConfigs) = await LoadConfigurationAsync(db, ct);

        // ── Stage 1a: Validate configuration ────────────────────────────
        ValidateConfiguration(config);

        // ── Stage 1b: Refresh dynamic templates from promoted strategies ──
        await RefreshDynamicTemplatesAsync(db, ct);

        // ── Stage 1b1: Check for failed candidates from previous cycle ──
        var failedKeysJson = await GetConfigAsync(db, "StrategyGeneration:FailedCandidateKeys", "", ct);
        if (!string.IsNullOrEmpty(failedKeysJson))
        {
            try
            {
                var failedKeys = System.Text.Json.JsonSerializer.Deserialize<List<string>>(failedKeysJson);
                if (failedKeys is { Count: > 0 })
                {
                    _logger.LogWarning(
                        "StrategyGenerationWorker: {Count} candidates failed to persist in previous cycle: {Keys}",
                        failedKeys.Count, string.Join("; ", failedKeys));
                }
            }
            catch { /* corrupt data */ }

            // Clear the key so we don't log it again next cycle
            try
            {
                var writeDb = writeCtx.GetDbContext();
                await UpsertConfigAsync(db, writeDb, "StrategyGeneration:FailedCandidateKeys", "",
                    "Failed candidate keys from last cycle (auto-managed)", ct);
                await writeCtx.SaveChangesAsync(ct);
            }
            catch { /* Non-critical */ }
        }

        // ── Stage 1c: Cross-cycle velocity cap ──
        var velocityCutoff = DateTime.UtcNow.AddDays(-7);
        var recentAutoCount = await db.Set<Strategy>()
            .Where(s => !s.IsDeleted && s.Name.StartsWith("Auto-") && s.CreatedAt >= velocityCutoff)
            .CountAsync(ct);
        if (recentAutoCount >= config.MaxCandidatesPerWeek)
        {
            _logger.LogInformation(
                "StrategyGenerationWorker: velocity cap — {Count} candidates created in last 7 days (limit {Limit}), skipping cycle",
                recentAutoCount, config.MaxCandidatesPerWeek);
            return;
        }

        // ── Stage 2: Pre-flight checks ──────────────────────────────────
        if (config.SeasonalBlackoutEnabled && IsInBlackoutPeriod(config.BlackoutPeriods, config.BlackoutTimezone))
        {
            _logger.LogInformation("StrategyGenerationWorker: seasonal blackout — skipping cycle");
            return;
        }

        if (config.SuppressDuringDrawdownRecovery && await IsInDrawdownRecoveryAsync(db, ct))
        {
            _logger.LogInformation("StrategyGenerationWorker: drawdown recovery — skipping cycle");
            return;
        }

        // ── Stage 3: Load symbol data ───────────────────────────────────
        var activePairEntities = await db.Set<CurrencyPair>()
            .Where(p => !p.IsDeleted)
            .ToListAsync(ct);

        var activePairs = activePairEntities.Select(p => p.Symbol).Distinct().ToList();
        var pairDataBySymbol = activePairEntities
            .GroupBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (activePairs.Count == 0)
        {
            _logger.LogInformation("StrategyGenerationWorker: no active currency pairs — skipping");
            return;
        }

        // #7: Weekend guard — skip if non-crypto symbols and it's weekend
        if (config.SkipWeekends && IsWeekendForAssetMix(
            activePairs.Select(s => (s, pairDataBySymbol.GetValueOrDefault(s)))))
        {
            _logger.LogInformation("StrategyGenerationWorker: weekend — skipping cycle (non-crypto markets closed)");
            _metrics.StrategyGenWeekendSkipped.Add(1);
            return;
        }

        // ── Stage 4: Load existing strategies for dedup + capacity ──────
        var existing = await db.Set<Strategy>()
            .Where(s => !s.IsDeleted)
            .Select(s => new ExistingStrategyInfo(s.Id, s.StrategyType, s.Symbol, s.Timeframe, s.Status, s.LifecycleStage))
            .ToListAsync(ct);

        var existingSet = new HashSet<(StrategyType, string, Timeframe)>(
            existing.Select(s => (s.StrategyType, s.Symbol, s.Timeframe)));

        var activeCountBySymbol = existing
            .Where(s => s.Status == StrategyStatus.Active)
            .GroupBy(s => s.Symbol)
            .ToDictionary(g => g.Key, g => g.Count());

        config.ActiveStrategyCount = activeCountBySymbol.Values.Sum();

        // #1: Failure memory — IgnoreQueryFilters to include soft-deleted strategies.
        // Track at template level (ParametersJson) so untried templates can still be screened.
        // All filtering is done server-side to avoid loading the entire Strategy table.
        var retryCutoff = DateTime.UtcNow.AddDays(-config.RetryCooldownDays);
        var recentlyPruned = await db.Set<Strategy>()
            .IncludingSoftDeleted()
            .Where(s => s.IsDeleted && s.Name.StartsWith("Auto-") && s.CreatedAt >= retryCutoff)
            .Select(s => new PrunedStrategyInfo(s.StrategyType, s.Symbol, s.Timeframe, s.ParametersJson))
            .ToListAsync(ct);

        var prunedTemplates = new Dictionary<(StrategyType, string, Timeframe), HashSet<string>>();
        var fullyPrunedCombos = new HashSet<(StrategyType, string, Timeframe)>();
        foreach (var s in recentlyPruned)
        {
            var combo = (s.StrategyType, s.Symbol, s.Timeframe);
            if (string.IsNullOrWhiteSpace(s.ParametersJson))
            {
                fullyPrunedCombos.Add(combo);
                continue;
            }

            if (!prunedTemplates.TryGetValue(combo, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                prunedTemplates[combo] = set;
            }

            set.Add(s.ParametersJson);
        }

        // ── Stage 5: Load regime data with freshness + confidence + transition checks ──
        var regimeFreshnessCutoff = DateTime.UtcNow.AddHours(-config.RegimeFreshnessHours);
        var recentRegimeSnapshots = await db.Set<MarketRegimeSnapshot>()
            .Where(s => !s.IsDeleted && s.DetectedAt >= regimeFreshnessCutoff)
            .OrderByDescending(s => s.DetectedAt)
            .ToListAsync(ct);

        var regimeBySymbol = new Dictionary<string, MarketRegimeEnum>(StringComparer.OrdinalIgnoreCase);
        var regimeConfidenceBySymbol = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var regimeTransitions = new Dictionary<string, MarketRegimeEnum>(StringComparer.OrdinalIgnoreCase);
        var regimeDetectedAtBySymbol = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var transitionSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in recentRegimeSnapshots.GroupBy(s => s.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            var latest = group.First();
            if ((double)latest.Confidence < config.MinRegimeConfidence)
            {
                _metrics.StrategyGenRegimeConfidenceSkipped.Add(1);
                _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "low_confidence"));
                continue;
            }

            // #8: Regime transition cooldown — generate transition types instead of skipping
            var secondLatest = group.Skip(1).FirstOrDefault();
            if (secondLatest != null && secondLatest.Regime != latest.Regime)
            {
                regimeTransitions[latest.Symbol] = secondLatest.Regime;
                var transitionAge = latest.DetectedAt - secondLatest.DetectedAt;
                if (transitionAge.TotalHours < config.RegimeTransitionCooldownHours)
                {
                    _metrics.StrategyGenRegimeTransitionSkipped.Add(1);
                    transitionSymbols.Add(latest.Symbol);
                    // Don't skip — fall through to add symbol with transition types
                }
            }

            regimeBySymbol[latest.Symbol] = latest.Regime;
            regimeConfidenceBySymbol[latest.Symbol] = (double)latest.Confidence;
            regimeDetectedAtBySymbol[latest.Symbol] = latest.DetectedAt;
        }

        var regimeBySymbolTf = recentRegimeSnapshots
            .GroupBy(s => (s.Symbol.ToUpperInvariant(), s.Timeframe))
            .ToDictionary(g => g.Key, g => g.First().Regime);

        // ── Stage 5b: Performance feedback (#12: recency-weighted) ──────
        var halfLifeDays = _feedbackDecayMonitor.GetEffectiveHalfLifeDays();
        var (feedbackRates, templateSurvivalRates) = await LoadPerformanceFeedbackAsync(db, writeCtx, halfLifeDays, ct);

        // ── Stage 5b1: Data-driven regime mapping — promote types with proven survival ──
        _regimeMapper.RefreshFromFeedback(feedbackRates);

        // ── Stage 5b-decay: Record predictions for feedback decay monitoring ──
        try { await _feedbackDecayMonitor.RecordPredictionsAsync(db, writeCtx.GetDbContext(), feedbackRates, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "StrategyGenerationWorker: feedback decay — failed to record predictions"); }

        // ── Stage 5b2: Adaptive thresholds (#11: exclude pruned) ────────
        var adaptiveAdjustments = config.AdaptiveThresholdsEnabled
            ? await ComputeAdaptiveThresholdsAsync(db, config, ct)
            : AdaptiveThresholdAdjustments.Neutral;

        // ── Stage 5b3: Detect feedback/adaptive threshold contradictions ──
        DetectFeedbackAdaptiveContradictions(feedbackRates, adaptiveAdjustments);

        // ── Stage 5c: Correlation group counts ──────────────────────────
        var correlationGroupCounts = BuildCorrelationGroupCounts(existing
            .Where(s => s.Status == StrategyStatus.Active)
            .Select(s => s.Symbol).ToList());

        // ── Stage 5d: Load live performance haircuts (P2) ───────────────
        HaircutRatios? haircuts = null;
        if (liveBenchmark != null)
        {
            try { haircuts = await liveBenchmark.GetCachedHaircutsAsync(ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "StrategyGenerationWorker: live benchmark haircut load failed"); }
        }

        // R3: Bootstrapped haircut fallback when no live data exists
        if (haircuts == null || haircuts == HaircutRatios.Neutral)
        {
            if (liveBenchmark != null)
            {
                try { haircuts = await liveBenchmark.ComputeBootstrappedHaircutsAsync(ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "StrategyGenerationWorker: bootstrapped haircut failed"); }
            }
        }

        // ── Stage 5e: Load portfolio equity curve (P3) ─────────────────
        IReadOnlyList<(DateTime Date, decimal Equity)>? portfolioEquityCurve = null;
        if (portfolioProvider != null)
        {
            try { portfolioEquityCurve = await portfolioProvider.GetPortfolioEquityCurveAsync(90, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "StrategyGenerationWorker: portfolio equity curve load failed"); }
        }

        // ── Stage 6: Screen candidates ──────────────────────────────────
        var screeningEngine = new StrategyScreeningEngine(_backtestEngine, _logger,
            gate => _metrics.StrategyGenScreeningRejections.Add(1,
                new KeyValuePair<string, object?>("gate", gate)));
        var auditLogger = new ScreeningAuditLogger(mediator);
        var sctx = new ScreeningContext
        {
            Config = config,
            RawConfigs = rawConfigs,
            ScreeningEngine = screeningEngine,
            Existing = existing,
            ExistingSet = existingSet,
            PrunedTemplates = prunedTemplates,
            FullyPrunedCombos = fullyPrunedCombos,
            ActiveCountBySymbol = activeCountBySymbol,
            RegimeBySymbol = regimeBySymbol,
            RegimeBySymbolTf = regimeBySymbolTf,
            PairDataBySymbol = pairDataBySymbol,
            FeedbackRates = feedbackRates,
            AdaptiveAdjustments = adaptiveAdjustments,
            CorrelationGroupCounts = correlationGroupCounts,
            ActivePairs = activePairs,
            AuditLogger = auditLogger,
            RegimeConfidenceBySymbol = regimeConfidenceBySymbol,
            FaultTracker = new PerTypeFaultTracker(config.MaxFaultsPerStrategyType),
            TemplateSurvivalRates = templateSurvivalRates,
            RegimeTransitions = regimeTransitions,
            RegimeDetectedAtBySymbol = regimeDetectedAtBySymbol,
            TransitionSymbols = transitionSymbols,
            Haircuts = haircuts,
            PortfolioEquityCurve = portfolioEquityCurve,
            SpreadProfileProvider = spreadProfileProvider,
        };
        var (pendingCandidates, candidatesCreated, reserveCreated) = await ScreenAllCandidatesAsync(db, writeCtx, sctx, ct);

        // ── Stage 6c: Portfolio drawdown filter ─────────────────────────
        int portfolioFilterRemoved = 0;
        if (config.PortfolioBacktestEnabled && pendingCandidates.Count >= 2)
        {
            var (survivors, portfolioDD, removedCount) = StrategyScreeningEngine.RunPortfolioDrawdownFilter(
                pendingCandidates, config.MaxPortfolioDrawdownPct, config.ScreeningInitialBalance,
                config.PortfolioCorrelationWeight);
            if (removedCount > 0)
            {
                pendingCandidates = survivors;
                portfolioFilterRemoved = removedCount;
                _metrics.StrategyGenPortfolioDrawdownFiltered.Add(removedCount);
                _logger.LogInformation(
                    "StrategyGenerationWorker: portfolio filter removed {Count} (DD={DD:P1}, limit={Limit:P1})",
                    removedCount, portfolioDD, config.MaxPortfolioDrawdownPct);
            }
        }

        // ── Stage 7: Persist candidates (#2: dedup, #3: atomic, #17: priority) ──
        if (pendingCandidates.Count == 0 && activePairs.All(s => !regimeBySymbol.ContainsKey(s)))
        {
            _logger.LogInformation("StrategyGenerationWorker: no symbols with fresh regime data — skipping persist");
        }

        var eventService = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();
        int persisted = await PersistCandidatesAsync(readCtx, writeCtx, eventService, auditLogger, pendingCandidates, config, ct);

        // ── Stage 8: Prune stale drafts ─────────────────────────────────
        int pruned = await PruneStaleStrategiesAsync(readCtx, writeCtx, auditLogger, config.PruneAfterFailed, ct);

        // ── Stage 9: Persist last-run date ──────────────────────────────
        await PersistLastRunDateAsync(readCtx, writeCtx, ct);

        sw.Stop();
        _metrics.WorkerCycleDurationMs.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("worker", "StrategyGenerationWorker"));

        _logger.LogInformation(
            "StrategyGenerationWorker: cycle complete — {Created} created, {Pruned} pruned in {Duration:F0}ms",
            persisted, pruned, sw.Elapsed.TotalMilliseconds);

        // ── Stage 9b: Cycle comparison ──
        var prevStatsJson = await GetConfigAsync(db, "StrategyGeneration:PreviousCycleStats", "", ct);
        if (!string.IsNullOrEmpty(prevStatsJson))
        {
            try
            {
                var prev = System.Text.Json.JsonSerializer.Deserialize<CycleStats>(prevStatsJson);
                if (prev != null)
                {
                    int candidateDelta = persisted - prev.CandidatesCreated;
                    int prunedDelta = pruned - prev.StrategiesPruned;
                    _logger.LogInformation(
                        "StrategyGenerationWorker: cycle deltas vs previous — candidates: {CDelta:+#;-#;0}, pruned: {PDelta:+#;-#;0}",
                        candidateDelta, prunedDelta);
                }
            }
            catch { /* corrupt previous stats */ }
        }
        var currentStats = new CycleStats(persisted, pruned, regimeBySymbol.Count, sw.Elapsed.TotalMilliseconds);
        await UpsertConfigAsync(db, writeCtx.GetDbContext(), "StrategyGeneration:PreviousCycleStats",
            System.Text.Json.JsonSerializer.Serialize(currentStats), "Previous cycle stats (auto-managed)", ct);
        await writeCtx.SaveChangesAsync(ct);

        // #18: Publish cycle summary event
        await eventService.SaveAndPublish(writeCtx, new StrategyGenerationCycleCompletedIntegrationEvent
        {
            SymbolsProcessed = regimeBySymbol.Count,
            CandidatesCreated = persisted,
            ReserveCandidatesCreated = reserveCreated,
            StrategiesPruned = pruned,
            PortfolioFilterRemoved = portfolioFilterRemoved,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            CircuitBreakerActive = _scheduling.IsCircuitBreakerActive,
            ConsecutiveFailures = _scheduling.ConsecutiveFailures,
        });

        // ── Stage 9c: Evaluate feedback decay ──
        try { await _feedbackDecayMonitor.EvaluateAndAdjustAsync(db, writeCtx.GetDbContext(), ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "StrategyGenerationWorker: feedback decay evaluation failed"); }

        // ── Clear checkpoint on success ──
        try
        {
            await UpsertConfigAsync(db, writeCtx.GetDbContext(), GenerationCheckpointStore.ConfigKey, "",
                "Generation cycle checkpoint (auto-managed)", ct);
            await writeCtx.SaveChangesAsync(ct);
        }
        catch { /* non-critical */ }
    }

    // ── Stage 6: Candidate screening ────────────────────────────────────────

    private async Task<(List<ScreeningOutcome> Candidates, int Created, int ReserveCreated)> ScreenAllCandidatesAsync(
        DbContext db, IWriteApplicationDbContext writeCtx, ScreeningContext s, CancellationToken ct)
    {
        var config = s.Config;
        var totalCountBySymbol = s.Existing
            .GroupBy(e => e.Symbol)
            .ToDictionary(g => g.Key, g => g.Count());

        var prioritisedSymbols = s.ActivePairs
            .Where(sym => s.RegimeBySymbol.ContainsKey(sym))
            .OrderBy(sym => totalCountBySymbol.TryGetValue(sym, out var c) ? c : 0)
            .ToList();

        var candidatesPerCurrency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in s.Existing.Where(e => e.Status == StrategyStatus.Active))
        {
            var pair = s.PairDataBySymbol.GetValueOrDefault(e.Symbol);
            string bc = pair?.BaseCurrency ?? (e.Symbol.Length >= 6 ? e.Symbol[..3] : e.Symbol);
            string qc = pair?.QuoteCurrency ?? (e.Symbol.Length >= 6 ? e.Symbol[3..6] : "");
            candidatesPerCurrency[bc] = candidatesPerCurrency.GetValueOrDefault(bc) + 1;
            if (qc.Length > 0) candidatesPerCurrency[qc] = candidatesPerCurrency.GetValueOrDefault(qc) + 1;
        }

        // Regime budget
        var regimeCounts = s.RegimeBySymbol.Values.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        int totalRegimeSymbols = regimeCounts.Values.Sum();
        var regimeBudget = new Dictionary<MarketRegimeEnum, int>();
        foreach (var (regime, count) in regimeCounts)
        {
            double cappedShare = Math.Min((double)count / totalRegimeSymbols, config.RegimeBudgetDiversityPct);
            regimeBudget[regime] = Math.Max(1, (int)(config.MaxCandidates * cappedShare));
        }
        var regimeCandidatesCreated = regimeCounts.Keys.ToDictionary(r => r, _ => 0);

        int candidatesCreated = 0;
        var pendingCandidates = new List<ScreeningOutcome>();
        var candleCache = new CandleLruCache(config.MaxCandleCacheSize);
        string checkpointFingerprint = ComputeCheckpointFingerprint(s);

        // ── Checkpoint restore ──
        var completedSymbolSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cpJson = await db.Set<EngineConfig>().AsNoTracking()
                .Where(c => c.Key == GenerationCheckpointStore.ConfigKey)
                .Select(c => c.Value).FirstOrDefaultAsync(ct);
            var checkpoint = GenerationCheckpointStore.Restore(cpJson, DateTime.UtcNow, checkpointFingerprint, _logger);
            if (checkpoint != null)
            {
                completedSymbolSet = GenerationCheckpointStore.CompletedSymbolSet(checkpoint);
                candidatesCreated = checkpoint.CandidatesCreated;
                pendingCandidates = checkpoint.PendingCandidates
                    .Select(c => c.ToOutcome())
                    .Where(c => c.Passed)
                    .ToList();
                foreach (var pending in pendingCandidates)
                    s.ExistingSet.Add((pending.Strategy.StrategyType, pending.Strategy.Symbol, pending.Strategy.Timeframe));
                foreach (var (k, v) in checkpoint.CandidatesPerCurrency)
                    candidatesPerCurrency[k] = v;
                foreach (var (k, v) in checkpoint.RegimeCandidatesCreated)
                    if (Enum.TryParse<MarketRegimeEnum>(k, out var r))
                        regimeCandidatesCreated[r] = v;
                foreach (var (k, v) in checkpoint.CorrelationGroupCounts)
                    if (int.TryParse(k, out var idx))
                        s.CorrelationGroupCounts[idx] = v;
                _logger.LogInformation(
                    "StrategyGenerationWorker: resuming from checkpoint — {Completed} symbols done, {Candidates} candidates, {Pending} pending persists",
                    completedSymbolSet.Count, candidatesCreated, pendingCandidates.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: checkpoint restore failed — starting fresh");
        }

        // Batch-load candles per timeframe to reduce DB round trips
        foreach (var tf in config.CandidateTimeframes)
        {
            await ChunkedCandleLoader.LoadChunkedAsync(
                db, candleCache, config.ScreeningMonths, prioritisedSymbols, tf,
                config.CandleChunkSize > 0 ? config.CandleChunkSize : ChunkedCandleLoader.DefaultChunkSize,
                () => _metrics.StrategyGenCandleCacheEvictions.Add(1),
                _logger, ct);
        }

        // #4: SemaphoreSlim disposed via using
        using var screeningThrottle = new SemaphoreSlim(config.MaxParallelBacktests, config.MaxParallelBacktests);
        var screeningConfig = BuildScreeningConfig(config);

        // Pre-compute per-symbol active type counts once instead of O(n×m) per symbol iteration
        var activeTypeCountsBySymbol = s.Existing
            .Where(e => e.Status == StrategyStatus.Active)
            .GroupBy(e => e.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(e => e.StrategyType).ToDictionary(tg => tg.Key, tg => tg.Count()),
                StringComparer.OrdinalIgnoreCase);

        // ── Symbol prioritization log ──────────────────────────────────
        var skippedNoRegime = s.ActivePairs.Where(sym => !s.RegimeBySymbol.ContainsKey(sym)).ToList();
        _logger.LogInformation(
            "StrategyGenerationWorker: symbol priority — {Queued} queued (by ascending strategy count), " +
            "{SkippedNoRegime} skipped (no fresh regime data), regime budget: {RegimeBudget}",
            prioritisedSymbols.Count, skippedNoRegime.Count,
            string.Join(", ", regimeBudget.Select(kv => $"{kv.Key}={kv.Value}")));
        if (skippedNoRegime.Count > 0)
            _logger.LogDebug("StrategyGenerationWorker: symbols without regime data: {Symbols}",
                string.Join(", ", skippedNoRegime));

        var symbolSkipReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in prioritisedSymbols)
        {
            if (candidatesCreated >= config.MaxCandidates || ct.IsCancellationRequested) break;

            if (completedSymbolSet.Contains(symbol)) continue;

            if (s.ActiveCountBySymbol.TryGetValue(symbol, out var activeCount) && activeCount >= config.MaxActivePerSymbol)
            {
                _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "saturated"));
                symbolSkipReasons[symbol] = "saturated";
                continue;
            }

            var pairInfo = s.PairDataBySymbol.GetValueOrDefault(symbol);
            string baseCcy = pairInfo?.BaseCurrency ?? (symbol.Length >= 6 ? symbol[..3] : symbol);
            string quoteCcy = pairInfo?.QuoteCurrency ?? (symbol.Length >= 6 ? symbol[3..6] : "");

            candidatesPerCurrency.TryGetValue(baseCcy, out var baseCount);
            candidatesPerCurrency.TryGetValue(quoteCcy, out var quoteCount);
            if (baseCount >= config.MaxPerCurrencyGroup || (quoteCcy.Length > 0 && quoteCount >= config.MaxPerCurrencyGroup))
            {
                symbolSkipReasons[symbol] = "currency_group_cap";
                continue;
            }

            if (IsCorrelationGroupSaturated(symbol, s.CorrelationGroupCounts, config.MaxCorrelatedCandidates))
            {
                _metrics.StrategyGenCorrelationSkipped.Add(1);
                symbolSkipReasons[symbol] = "correlation_group_saturated";
                continue;
            }

            var regime = s.RegimeBySymbol[symbol];
            var suitableTypes = s.TransitionSymbols.Contains(symbol)
                ? GetTransitionTypes()
                : _regimeMapper.GetStrategyTypes(regime);
            if (suitableTypes.Count == 0)
            {
                symbolSkipReasons[symbol] = "no_suitable_types";
                continue;
            }

            suitableTypes = ApplyPerformanceFeedback(suitableTypes, regime, s.FeedbackRates);

            if (regimeBudget.TryGetValue(regime, out var budget)
                && regimeCandidatesCreated.GetValueOrDefault(regime) >= budget)
            {
                symbolSkipReasons[symbol] = $"regime_budget_exhausted({regime})";
                continue;
            }

            // #1: Scale max templates by regime confidence (61% → 1, 100% → full MaxTemplatesPerCombo)
            double confidence = s.RegimeConfidenceBySymbol.GetValueOrDefault(symbol, config.MinRegimeConfidence);
            double confidenceRange = 1.0 - config.MinRegimeConfidence;
            double confidenceFraction = confidenceRange > 0
                ? Math.Clamp((confidence - config.MinRegimeConfidence) / confidenceRange, 0, 1)
                : 1.0;
            double durationFactor = s.RegimeDetectedAtBySymbol.TryGetValue(symbol, out var detectedAt)
                ? ComputeRegimeDurationFactor(detectedAt) : 1.0;
            int confidenceScaledMaxTemplates = Math.Max(1,
                (int)Math.Ceiling(config.MaxTemplatesPerCombo * confidenceFraction * durationFactor));

            var activeTypeCountsForSymbol = activeTypeCountsBySymbol.GetValueOrDefault(symbol)
                ?? new Dictionary<StrategyType, int>();

            var assetClass = ClassifyAsset(symbol, pairInfo);
            var screeningOptions = BuildScreeningOptions(symbol, pairInfo, assetClass,
                config.ScreeningSpreadPoints, config.ScreeningCommissionPerLot,
                config.ScreeningSlippagePips, _livePriceCache);

            // P1: Wire dynamic spread function from SpreadProfile
            if (s.SpreadProfileProvider != null)
            {
                try
                {
                    var profiles = await s.SpreadProfileProvider.GetProfilesAsync(symbol, ct);
                    var spreadFunc = s.SpreadProfileProvider.BuildSpreadFunction(symbol, profiles);
                    if (spreadFunc != null)
                        screeningOptions.SpreadFunction = spreadFunc;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "StrategyGenerationWorker: spread profile load failed for {Symbol}", symbol);
                }
            }

            // Fix #4/#5: Symbol overrides from batch-loaded config — zero additional queries
            var symbolOverrides = ExtractSymbolOverrides(s.RawConfigs, symbol);

            // P5: Apply asset-class threshold multipliers
            var (acWR, acPF, acSh, acDD) = GetAssetClassThresholdMultipliers(assetClass);
            double baseWR = (symbolOverrides.MinWinRate ?? config.MinWinRate) * acWR;
            double basePF = (symbolOverrides.MinProfitFactor ?? config.MinProfitFactor) * acPF;
            double baseSh = (symbolOverrides.MinSharpe ?? config.MinSharpe) * acSh;
            double baseDD = (symbolOverrides.MaxDrawdownPct ?? config.MaxDrawdownPct) * acDD;

            foreach (var timeframe in config.CandidateTimeframes)
            {
                if (candidatesCreated >= config.MaxCandidates || ct.IsCancellationRequested) break;

                var candles = await LoadCandlesForScreeningAsync(db, candleCache, config, symbol, timeframe, ct);
                if (candles == null) continue;

                decimal atr = ComputeAtr(candles);
                if (!PassesSpreadFilter(atr, screeningOptions, assetClass, config.MaxSpreadToRangeRatio))
                    continue;

                var thresholds = BuildThresholdsForTimeframe(config, s.AdaptiveAdjustments, regime, timeframe,
                    baseWR, basePF, baseSh, baseDD, s.Haircuts);

                // Multi-timeframe confidence boost: scale templates by higher-TF regime agreement
                double mtfBoost = ComputeMultiTimeframeConfidenceBoost(regime, symbol, timeframe, s.RegimeBySymbolTf);
                int mtfScaledMaxTemplates = Math.Max(1,
                    (int)Math.Ceiling(confidenceScaledMaxTemplates * mtfBoost));

                var taskArgs = new ScreeningTaskArgs(candles, atr, symbol, timeframe, regime,
                    suitableTypes, activeTypeCountsForSymbol, screeningOptions, thresholds,
                    screeningConfig, mtfScaledMaxTemplates, candidatesCreated);
                var screeningTasks = BuildScreeningTasks(s, taskArgs, screeningThrottle, ct);

                if (screeningTasks.Count > 0)
                {
                    var results = await Task.WhenAll(screeningTasks.Select(f => f()));
                    foreach (var result in results.Where(r => r != null && r.Passed))
                    {
                        if (candidatesCreated >= config.MaxCandidates) break;
                        if (regimeBudget.TryGetValue(regime, out var rb) && regimeCandidatesCreated.GetValueOrDefault(regime) >= rb) break;

                        var combo = (result!.Strategy.StrategyType, result.Strategy.Symbol, result.Strategy.Timeframe);
                        if (s.ExistingSet.Contains(combo)) continue;

                        // Correlation pre-check: reject candidates highly correlated with already-accepted ones
                        if (pendingCandidates.Count > 0 &&
                            StrategyScreeningEngine.IsCorrelatedWithAccepted(result, pendingCandidates, config.ScreeningInitialBalance))
                        {
                            _metrics.StrategyGenScreeningRejections.Add(1,
                                new KeyValuePair<string, object?>("gate", "correlation_precheck"));
                            continue;
                        }

                        pendingCandidates.Add(result);
                        s.ExistingSet.Add(combo);
                        candidatesCreated++;
                        regimeCandidatesCreated[regime] = regimeCandidatesCreated.GetValueOrDefault(regime) + 1;
                        candidatesPerCurrency[baseCcy] = candidatesPerCurrency.GetValueOrDefault(baseCcy) + 1;
                        if (quoteCcy.Length > 0) candidatesPerCurrency[quoteCcy] = candidatesPerCurrency.GetValueOrDefault(quoteCcy) + 1;
                        IncrementCorrelationGroupCount(symbol, s.CorrelationGroupCounts);
                    }
                }
            }

            completedSymbolSet.Add(symbol);
            try
            {
                var cpState = new GenerationCheckpointStore.State
                {
                    CycleDateUtc = DateTime.UtcNow.Date,
                    Fingerprint = checkpointFingerprint,
                    CompletedSymbols = completedSymbolSet.ToList(),
                    CandidatesCreated = candidatesCreated,
                    PendingCandidates = pendingCandidates
                        .Select(GenerationCheckpointStore.PendingCandidateState.FromOutcome)
                        .ToList(),
                    CandidatesPerCurrency = new Dictionary<string, int>(candidatesPerCurrency),
                    RegimeCandidatesCreated = regimeCandidatesCreated.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    CorrelationGroupCounts = s.CorrelationGroupCounts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                };
                await UpsertConfigAsync(db, writeCtx.GetDbContext(), GenerationCheckpointStore.ConfigKey,
                    GenerationCheckpointStore.Serialize(cpState, _logger),
                    "Generation cycle checkpoint (auto-managed)", ct);
                await writeCtx.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StrategyGenerationWorker: checkpoint save failed for {Symbol}", symbol);
            }
        }

        // ── Symbol skip summary ──
        if (symbolSkipReasons.Count > 0)
        {
            var skipSummary = symbolSkipReasons
                .GroupBy(kv => kv.Value)
                .Select(g => $"{g.Key}={g.Count()}")
                .ToList();
            _logger.LogInformation(
                "StrategyGenerationWorker: symbol skip summary — {SkipSummary}",
                string.Join(", ", skipSummary));
            _logger.LogDebug(
                "StrategyGenerationWorker: per-symbol skips — {Details}",
                string.Join("; ", symbolSkipReasons.Select(kv => $"{kv.Key}:{kv.Value}")));
        }

        // Fault tracker summary
        var faultCounts = s.FaultTracker.GetFaultCounts();
        if (faultCounts.Count > 0)
            _logger.LogInformation(
                "StrategyGenerationWorker: per-type faults — {Faults}",
                string.Join(", ", faultCounts.Select(kv => $"{kv.Key}={kv.Value}")));

        // ── Stage 6b: Strategic reserve (#19: lifecycle tagging, #20: spread filter, #21: reuse screening engine) ──
        int reserveCreated = 0;
        if (candidatesCreated < config.MaxCandidates && config.StrategicReserveQuota > 0)
        {
            var reserveResult = await ScreenReserveCandidatesAsync(
                db, s, candleCache, pendingCandidates, candidatesCreated, ct);
            reserveCreated = reserveResult.ReserveCreated;
        }

        return (pendingCandidates, candidatesCreated + reserveCreated, reserveCreated);
    }

    // ── Extracted helpers for ScreenAllCandidatesAsync ────────────────────────

    /// <summary>Loads candles from cache or DB, validates count and staleness. Returns null if unusable.</summary>
    private async Task<List<Candle>?> LoadCandlesForScreeningAsync(
        DbContext db, CandleLruCache candleCache, GenerationConfig config,
        string symbol, Timeframe timeframe, CancellationToken ct)
    {
        int scaledMonths = ScaleScreeningWindowForTimeframe(config.ScreeningMonths, timeframe);
        var cacheKey = (symbol, timeframe);

        if (!candleCache.TryGet(cacheKey, out var candles))
        {
            while (candleCache.IsFull)
            {
                var evicted = candleCache.EvictLru();
                if (evicted == null) break;
                _metrics.StrategyGenCandleCacheEvictions.Add(1);
                _logger.LogDebug("StrategyGenerationWorker: LRU evicted candle cache entry {Symbol}/{Tf}",
                    evicted.Value.Symbol, evicted.Value.Tf);
            }
            var screeningFrom = DateTime.UtcNow.AddMonths(-scaledMonths);
            candles = await db.Set<Candle>()
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe
                         && c.Timestamp >= screeningFrom && c.IsClosed && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .Select(c => new Candle
                {
                    Id = c.Id, Open = c.Open, High = c.High, Low = c.Low,
                    Close = c.Close, Volume = c.Volume, Timestamp = c.Timestamp,
                })
                .ToListAsync(ct);
            candleCache.Put(cacheKey, candles);
        }

        if (candles.Count < 100) return null;

        if (config.MaxCandleAgeHours > 0)
        {
            var candleAge = DateTime.UtcNow - candles[^1].Timestamp;
            if (candleAge.TotalHours > config.MaxCandleAgeHours)
            {
                _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "stale_candles"));
                return null;
            }
        }

        return candles;
    }

    /// <summary>Checks whether a symbol's spread-to-ATR ratio passes the asset-class-specific limit.</summary>
    private static bool PassesSpreadFilter(decimal atr, BacktestOptions options, AssetClass assetClass, double maxRatio)
    {
        if (atr <= 0 || options.SpreadPriceUnits <= 0) return true;
        double spreadToRange = (double)(options.SpreadPriceUnits / atr);
        return spreadToRange <= GetSpreadToRangeLimit(assetClass, maxRatio);
    }

    /// <summary>Builds regime-scaled, adaptive-adjusted, haircut-corrected screening thresholds.</summary>
    private static ScreeningThresholds BuildThresholdsForTimeframe(
        GenerationConfig config, AdaptiveThresholdAdjustments adj,
        MarketRegimeEnum regime, Timeframe timeframe,
        double baseWR, double basePF, double baseSh, double baseDD,
        HaircutRatios? haircuts = null)
    {
        var (scaledWR, scaledPF, scaledSh, scaledDD) = ScaleThresholdsForRegime(baseWR, basePF, baseSh, baseDD, regime);
        scaledWR = ApplyAdaptiveAdjustment(scaledWR, adj.WinRateMultiplier);
        scaledPF = ApplyAdaptiveAdjustment(scaledPF, adj.ProfitFactorMultiplier);
        scaledSh = ApplyAdaptiveAdjustment(scaledSh, adj.SharpeMultiplier);
        scaledDD = ApplyAdaptiveAdjustment(scaledDD, adj.DrawdownMultiplier);

        // P2: Apply live performance haircut — tighten thresholds to account for
        // the observed degradation between backtest and live performance
        if (haircuts is { SampleCount: >= 5 or < 0 })  // Live with sufficient data, or bootstrapped
        {
            scaledWR /= Math.Max(0.5, haircuts.WinRateHaircut);
            scaledPF /= Math.Max(0.5, haircuts.ProfitFactorHaircut);
            scaledSh /= Math.Max(0.5, haircuts.SharpeHaircut);
            scaledDD *= Math.Max(0.5, haircuts.DrawdownInflation);
        }

        int adjustedMinTrades = AdjustMinTradesForTimeframe(config.MinTotalTrades, timeframe);
        return new ScreeningThresholds(scaledWR, scaledPF, scaledSh, scaledDD, adjustedMinTrades);
    }

    /// <summary>
    /// Builds throttled parallel screening tasks for all viable strategy types on a given
    /// symbol/timeframe. Each task runs a full screening pipeline with per-candidate timeout.
    /// </summary>
    private List<Func<Task<ScreeningOutcome?>>> BuildScreeningTasks(
        ScreeningContext s, ScreeningTaskArgs a,
        SemaphoreSlim screeningThrottle, CancellationToken ct)
    {
        var config = s.Config;
        double trainRatio = GetTrainSplitRatio(a.Candles.Count);
        int splitIndex = (int)(a.Candles.Count * trainRatio);
        var trainCandles = a.Candles.Take(splitIndex).ToList();
        var testCandles = a.Candles.Skip(splitIndex).ToList();

        var tasks = new List<Func<Task<ScreeningOutcome?>>>();

        foreach (var strategyType in a.SuitableTypes)
        {
            if (a.CandidatesCreated + tasks.Count >= config.MaxCandidates) break;

            if (s.FaultTracker.IsTypeDisabled(strategyType)) continue;

            if (a.ActiveTypeCountsForSymbol.TryGetValue(strategyType, out var typeCount)
                && typeCount >= config.MaxActivePerTypePerSymbol)
                continue;

            var combo = (strategyType, a.Symbol, a.Timeframe);
            if (s.ExistingSet.Contains(combo)) continue;
            if (s.FullyPrunedCombos.Contains(combo)) continue;

            var higherTf = GetHigherTimeframe(a.Timeframe);
            if (higherTf.HasValue
                && s.RegimeBySymbolTf.TryGetValue((a.Symbol.ToUpperInvariant(), higherTf.Value), out var higherRegime)
                && !StrategyGenerationHelpers.IsRegimeCompatibleWithStrategy(strategyType, higherRegime))
                continue;

            var templates = _templateProvider.GetTemplates(strategyType);
            if (templates.Count == 0) continue;

            var orderedTemplates = OrderTemplatesForRegime(templates, a.Regime, s.TemplateSurvivalRates);
            s.PrunedTemplates.TryGetValue(combo, out var failedParamsForCombo);
            int templatesQueued = 0;

            foreach (var parametersJson in orderedTemplates)
            {
                if (templatesQueued >= a.MaxTemplates) break;
                var enrichedParams = InjectAtrContext(parametersJson, a.Atr);

                if (failedParamsForCombo != null && failedParamsForCombo.Contains(enrichedParams))
                    continue;

                var capturedType = strategyType;
                var capturedIdx = templatesQueued;

                tasks.Add(async () =>
                {
                    await screeningThrottle.WaitAsync(ct);
                    try
                    {
                        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        taskCts.CancelAfter(TimeSpan.FromSeconds(config.ScreeningTimeoutSeconds));

                        MarketRegimeEnum? oosRegime = s.RegimeTransitions.TryGetValue(a.Symbol, out var prevRegime)
                            ? prevRegime : null;
                        var screeningSw = Stopwatch.StartNew();
                        var result = await s.ScreeningEngine.ScreenCandidateAsync(
                            capturedType, a.Symbol, a.Timeframe, enrichedParams, capturedIdx,
                            a.Candles, trainCandles, testCandles, a.ScreeningOptions, a.Thresholds,
                            a.ScreeningConfig, a.Regime, "Primary", taskCts.Token, oosRegime,
                            s.PortfolioEquityCurve);
                        screeningSw.Stop();
                        _metrics.StrategyGenScreeningDurationMs.Record(screeningSw.Elapsed.TotalMilliseconds,
                            new KeyValuePair<string, object?>("strategy_type", capturedType.ToString()));
                        if (result != null && !result.Passed)
                        {
                            await s.AuditLogger.LogFailureAsync(result, ct);
                            return null;
                        }
                        _metrics.StrategyCandidatesScreened.Add(1,
                            new KeyValuePair<string, object?>("strategy_type", capturedType.ToString()));
                        return result;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning("StrategyGenerationWorker: screening task timed out for {Type} on {Symbol}/{Tf}",
                            capturedType, a.Symbol, a.Timeframe);
                        _metrics.StrategyGenScreeningRejections.Add(1,
                            new KeyValuePair<string, object?>("gate", "timeout"));
                        return null;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "StrategyGenerationWorker: screening task faulted for {Type} on {Symbol}/{Tf}",
                            capturedType, a.Symbol, a.Timeframe);
                        _metrics.StrategyGenScreeningRejections.Add(1,
                            new KeyValuePair<string, object?>("gate", "task_fault"));
                        s.FaultTracker.RecordFault(capturedType);
                        if (s.FaultTracker.IsTypeDisabled(capturedType))
                        {
                            _logger.LogWarning(
                                "StrategyGenerationWorker: {Type} disabled for remainder of cycle — too many screening faults",
                                capturedType);
                            _metrics.StrategyGenTypeFaultDisabled.Add(1,
                                new KeyValuePair<string, object?>("strategy_type", capturedType.ToString()));
                        }
                        return null;
                    }
                    finally { screeningThrottle.Release(); }
                });
                templatesQueued++;
            }
        }

        return tasks;
    }

    // ── Stage 6b: Reserve screening (#19, #20, #21) ─────────────────────────

    private async Task<(int ReserveCreated, int _)> ScreenReserveCandidatesAsync(
        DbContext db, ScreeningContext s, CandleLruCache candleCache,
        List<ScreeningOutcome> pendingCandidates, int candidatesCreated,
        CancellationToken ct)
    {
        var config = s.Config;
        int reserveCreated = 0;
        int totalCreated = candidatesCreated;

        var activeTypesBySymbol = s.Existing
            .Where(e => e.Status == StrategyStatus.Active || e.Status == StrategyStatus.Paused)
            .GroupBy(e => e.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => e.StrategyType).ToHashSet(), StringComparer.OrdinalIgnoreCase);

        var prioritisedSymbols = s.RegimeBySymbol.Keys.ToList();
        var reserveScreeningConfig = BuildScreeningConfig(config);
        using var reserveThrottle = new SemaphoreSlim(config.MaxParallelBacktests, config.MaxParallelBacktests);

        foreach (var symbol in prioritisedSymbols)
        {
            if (reserveCreated >= config.StrategicReserveQuota || totalCreated >= config.MaxCandidates) break;
            if (!s.RegimeBySymbol.TryGetValue(symbol, out var regime)) continue;

            var currentTypes = activeTypesBySymbol.GetValueOrDefault(symbol) ?? [];
            var counterTypes = GetCounterRegimeTypes(regime).Where(t => !currentTypes.Contains(t)).ToList();
            if (counterTypes.Count == 0) continue;

            var pairInfo = s.PairDataBySymbol.GetValueOrDefault(symbol);
            var assetClass = ClassifyAsset(symbol, pairInfo);
            var reserveOptions = BuildScreeningOptions(symbol, pairInfo, assetClass,
                config.ScreeningSpreadPoints, config.ScreeningCommissionPerLot,
                config.ScreeningSlippagePips, _livePriceCache);

            // P1: Wire dynamic spread function for reserve screening (same as primary)
            if (s.SpreadProfileProvider != null)
            {
                try
                {
                    var profiles = await s.SpreadProfileProvider.GetProfilesAsync(symbol, ct);
                    var spreadFunc = s.SpreadProfileProvider.BuildSpreadFunction(symbol, profiles);
                    if (spreadFunc != null)
                        reserveOptions.SpreadFunction = spreadFunc;
                }
                catch { /* Non-critical — fall back to fixed spread */ }
            }

            foreach (var reserveTf in config.CandidateTimeframes)
            {
                if (reserveCreated >= config.StrategicReserveQuota || totalCreated >= config.MaxCandidates) break;

                var candles = await LoadCandlesForScreeningAsync(db, candleCache, config, symbol, reserveTf, ct);
                if (candles == null) continue;

                // #20: Spread/ATR pre-filter for reserve candidates
                decimal atr = ComputeAtr(candles);
                if (!PassesSpreadFilter(atr, reserveOptions, assetClass, config.MaxSpreadToRangeRatio))
                {
                    _metrics.StrategyGenReserveSpreadSkipped.Add(1);
                    continue;
                }

                int adjMinTrades = AdjustMinTradesForTimeframe(config.MinTotalTrades, reserveTf);

                // Relaxed thresholds for reserves: regime-scaled + adaptive + 85% relaxation
                var (scaledWR, scaledPF, scaledSh, scaledDD) =
                    ScaleThresholdsForRegime(config.MinWinRate, config.MinProfitFactor, config.MinSharpe, config.MaxDrawdownPct, regime);
                double reserveWR = ApplyAdaptiveAdjustment(scaledWR, s.AdaptiveAdjustments.WinRateMultiplier) * 0.85;
                double reservePF = ApplyAdaptiveAdjustment(scaledPF, s.AdaptiveAdjustments.ProfitFactorMultiplier) * 0.85;
                double reserveSh = ApplyAdaptiveAdjustment(scaledSh, s.AdaptiveAdjustments.SharpeMultiplier) * 0.85;
                double reserveDD = ApplyAdaptiveAdjustment(scaledDD, s.AdaptiveAdjustments.DrawdownMultiplier) * 1.15;
                var thresholds = new ScreeningThresholds(reserveWR, reservePF, reserveSh, reserveDD, adjMinTrades);
                var reserveTasks = new List<Func<Task<ScreeningOutcome?>>>();

                double trainRatio = GetTrainSplitRatio(candles.Count);
                int splitIdx = (int)(candles.Count * trainRatio);
                var trainC = candles.Take(splitIdx).ToList();
                var testC = candles.Skip(splitIdx).ToList();

                foreach (var counterType in counterTypes)
                {
                    if (reserveCreated + reserveTasks.Count >= config.StrategicReserveQuota
                        || totalCreated + reserveTasks.Count >= config.MaxCandidates) break;

                    if (s.FaultTracker.IsTypeDisabled(counterType)) continue;

                    var combo = (counterType, symbol, reserveTf);
                    if (s.ExistingSet.Contains(combo)) continue;
                    if (s.FullyPrunedCombos.Contains(combo)) continue;

                    var templates = _templateProvider.GetTemplates(counterType);
                    if (templates.Count == 0) continue;
                    var orderedTemplates = OrderTemplatesForRegime(templates, regime, s.TemplateSurvivalRates);
                    s.PrunedTemplates.TryGetValue(combo, out var failedParamsForCombo);

                    // Find first non-pruned template
                    string? selectedParams = null;
                    foreach (var templateJson in orderedTemplates)
                    {
                        var enriched = InjectAtrContext(templateJson, atr);
                        if (failedParamsForCombo != null && failedParamsForCombo.Contains(enriched))
                            continue;
                        selectedParams = enriched;
                        break;
                    }
                    if (selectedParams == null) continue;

                    var capturedType = counterType;
                    var capturedParams = selectedParams;
                    reserveTasks.Add(async () =>
                    {
                        await reserveThrottle.WaitAsync(ct);
                        try
                        {
                            using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            taskCts.CancelAfter(TimeSpan.FromSeconds(config.ScreeningTimeoutSeconds));

                            // Pass oosRegime and portfolioEquityCurve (same as primary screening)
                            MarketRegimeEnum? reserveOosRegime = s.RegimeTransitions.TryGetValue(symbol, out var prevR)
                                ? prevR : null;
                            return await s.ScreeningEngine.ScreenCandidateAsync(
                                capturedType, symbol, reserveTf, capturedParams, 0,
                                candles, trainC, testC, reserveOptions, thresholds,
                                reserveScreeningConfig, regime, "Reserve", taskCts.Token,
                                reserveOosRegime, s.PortfolioEquityCurve);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            return null;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "StrategyGenerationWorker: reserve screening faulted for {Type} on {Symbol}/{Tf}",
                                capturedType, symbol, reserveTf);
                            s.FaultTracker.RecordFault(capturedType);
                            return null;
                        }
                        finally { reserveThrottle.Release(); }
                    });
                }

                if (reserveTasks.Count > 0)
                {
                    var results = await Task.WhenAll(reserveTasks.Select(f => f()));
                    foreach (var result in results.Where(r => r != null && r.Passed))
                    {
                        if (reserveCreated >= config.StrategicReserveQuota || totalCreated >= config.MaxCandidates) break;

                        var combo = (result!.Strategy.StrategyType, result.Strategy.Symbol, result.Strategy.Timeframe);
                        if (s.ExistingSet.Contains(combo)) continue;

                        pendingCandidates.Add(result);
                        s.ExistingSet.Add(combo);
                        totalCreated++;
                        reserveCreated++;

                        _logger.LogInformation("StrategyGenerationWorker: reserve — {Name} (counter-{Regime})",
                            result.Strategy.Name, regime);
                    }
                }
            }
        }

        if (reserveCreated > 0)
            _logger.LogInformation("StrategyGenerationWorker: {Count} strategic reserve candidates", reserveCreated);

        return (reserveCreated, 0);
    }

    // ── Stage 7: Persist (#2: backtest dedup, #3: atomic save, #17: priority) ──

    private async Task<int> PersistCandidatesAsync(
        IReadApplicationDbContext readCtx, IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService, ScreeningAuditLogger auditLogger,
        List<ScreeningOutcome> candidates, GenerationConfig config, CancellationToken ct)
    {
        if (candidates.Count == 0) return 0;

        var db = readCtx.GetDbContext();
        var writeDb = writeCtx.GetDbContext();

        // Concurrency guard
        var concurrentlyCreated = await db.Set<Strategy>()
            .Where(s => !s.IsDeleted)
            .Select(s => new { s.StrategyType, s.Symbol, s.Timeframe })
            .ToListAsync(ct);
        var freshExistingSet = new HashSet<(StrategyType, string, Timeframe)>(
            concurrentlyCreated.Select(s => (s.StrategyType, s.Symbol, s.Timeframe)));

        var confirmed = candidates
            .Where(c => !freshExistingSet.Contains((c.Strategy.StrategyType, c.Strategy.Symbol, c.Strategy.Timeframe)))
            .ToList();
        if (confirmed.Count == 0) return 0;

        // #2: Check for existing queued backtests to prevent dedup
        var existingQueuedStrategyIds = await db.Set<BacktestRun>()
            .Where(r => r.Status == RunStatus.Queued && !r.IsDeleted)
            .Select(r => r.StrategyId)
            .ToListAsync(ct);
        var queuedSet = new HashSet<long>(existingQueuedStrategyIds);

        // #3: Atomic save — prefer DB transaction; compensate persisted drafts if transactions are unavailable
        try
        {
            await using var tx = await TryBeginTransactionAsync(writeDb, ct);
            foreach (var c in confirmed)
                writeDb.Set<Strategy>().Add(c.Strategy);
            await writeCtx.SaveChangesAsync(ct); // Need IDs first

            foreach (var c in confirmed)
            {
                if (queuedSet.Contains(c.Strategy.Id)) continue; // #2: dedup

                // #17: Priority based on IS Sharpe rank
                int priority = (int)((double)c.TrainResult.SharpeRatio * 100);
                writeDb.Set<BacktestRun>().Add(new BacktestRun
                {
                    StrategyId     = c.Strategy.Id,
                    Symbol         = c.Strategy.Symbol,
                    Timeframe      = c.Strategy.Timeframe,
                    FromDate       = DateTime.UtcNow.AddDays(-365),
                    ToDate         = DateTime.UtcNow,
                    InitialBalance = 10_000m,
                    Status         = RunStatus.Queued,
                    Priority       = priority,
                });
            }
            await writeCtx.SaveChangesAsync(ct);
            if (tx != null)
                await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: batch save failed — falling back to individual saves");
            foreach (var c in confirmed)
                await TryCompensateUnsafelyPersistedStrategyAsync(writeDb, writeCtx, c.Strategy, ct);
            foreach (var entry in writeDb.ChangeTracker.Entries().ToList())
                entry.State = EntityState.Detached;

            var recheckExisting = await db.Set<Strategy>()
                .Where(s => !s.IsDeleted)
                .Select(s => new { s.StrategyType, s.Symbol, s.Timeframe })
                .ToListAsync(ct);
            var recheckSet = new HashSet<(StrategyType, string, Timeframe)>(
                recheckExisting.Select(s => (s.StrategyType, s.Symbol, s.Timeframe)));
            confirmed = confirmed.Where(c => !recheckSet.Contains(
                (c.Strategy.StrategyType, c.Strategy.Symbol, c.Strategy.Timeframe))).ToList();

            var saved = new List<ScreeningOutcome>();
            var failedCandidateKeys = new List<string>();
            foreach (var c in confirmed)
            {
                try
                {
                    await using var tx = await TryBeginTransactionAsync(writeDb, ct);
                    writeDb.Set<Strategy>().Add(c.Strategy);
                    await writeCtx.SaveChangesAsync(ct);
                    int priority = (int)((double)c.TrainResult.SharpeRatio * 100);
                    writeDb.Set<BacktestRun>().Add(new BacktestRun
                    {
                        StrategyId = c.Strategy.Id, Symbol = c.Strategy.Symbol,
                        Timeframe = c.Strategy.Timeframe,
                        FromDate = DateTime.UtcNow.AddDays(-365), ToDate = DateTime.UtcNow,
                        InitialBalance = 10_000m, Status = RunStatus.Queued, Priority = priority,
                    });
                    await writeCtx.SaveChangesAsync(ct);
                    if (tx != null)
                        await tx.CommitAsync(ct);
                    saved.Add(c);
                }
                catch (Exception innerEx)
                {
                    _logger.LogWarning(innerEx, "StrategyGenerationWorker: save failed for {Name}", c.Strategy.Name);
                    await TryCompensateUnsafelyPersistedStrategyAsync(writeDb, writeCtx, c.Strategy, ct);
                    failedCandidateKeys.Add($"{c.Strategy.StrategyType}|{c.Strategy.Symbol}|{c.Strategy.Timeframe}|{c.Strategy.ParametersJson}");
                    foreach (var entry in writeDb.ChangeTracker.Entries()
                        .Where(e => e.State != EntityState.Unchanged).ToList())
                        entry.State = EntityState.Detached;
                }
            }

            // Persist failed candidate keys for operator visibility
            if (failedCandidateKeys.Count > 0)
            {
                try
                {
                    var failedJson = System.Text.Json.JsonSerializer.Serialize(failedCandidateKeys);
                    await UpsertConfigAsync(db, writeDb, "StrategyGeneration:FailedCandidateKeys", failedJson,
                        "Failed candidate keys from last cycle (auto-managed)", ct);
                    await writeCtx.SaveChangesAsync(ct);
                }
                catch { /* Non-critical — best effort */ }
            }

            confirmed = saved;
        }

        // Log and publish events
        foreach (var c in confirmed)
        {
            _metrics.StrategyCandidatesCreated.Add(1,
                new KeyValuePair<string, object?>("strategy_type", c.Strategy.StrategyType.ToString()));

            await auditLogger.LogCreationAsync(c, c.Strategy.Id, ct);

            await eventService.SaveAndPublish(writeCtx, new StrategyCandidateCreatedIntegrationEvent
            {
                StrategyId = c.Strategy.Id, Name = c.Strategy.Name,
                Symbol = c.Strategy.Symbol, Timeframe = c.Strategy.Timeframe,
                StrategyType = c.Strategy.StrategyType, Regime = c.Regime,
                CreatedAt = c.Strategy.CreatedAt,
            });
        }

        // P6: Fast-track auto-promote for elite candidates
        try
        {
            bool fastTrackEnabled = await GetConfigAsync(readCtx.GetDbContext(), "FastTrack:Enabled", false, ct);
            if (fastTrackEnabled)
            {
                double thresholdMult = await GetConfigAsync(readCtx.GetDbContext(), "FastTrack:ThresholdMultiplier", 2.0, ct);
                double minR2 = await GetConfigAsync(readCtx.GetDbContext(), "FastTrack:MinR2", 0.90, ct);
                double maxP = await GetConfigAsync(readCtx.GetDbContext(), "FastTrack:MaxMonteCarloPValue", 0.01, ct);

                foreach (var c in confirmed)
                {
                    if (c.Metrics == null) continue;
                    bool isElite = c.Metrics.IsSharpeRatio >= config.MinSharpe * thresholdMult
                        && c.Metrics.OosSharpeRatio >= config.MinSharpe * thresholdMult
                        && c.Metrics.EquityCurveR2 >= minR2
                        && c.Metrics.MonteCarloPValue is > 0 and var pVal && pVal <= maxP
                        && c.Metrics.WalkForwardWindowsPassed >= 3;

                    if (isElite)
                    {
                        c.Strategy.LifecycleStage = StrategyLifecycleStage.ShadowLive;
                        c.Strategy.LifecycleStageEnteredAt = DateTime.UtcNow;
                        _logger.LogInformation(
                            "StrategyGenerationWorker: fast-track — {Name} auto-promoted to ShadowLive",
                            c.Strategy.Name);

                        await eventService.SaveAndPublish(writeCtx, new StrategyAutoPromotedIntegrationEvent
                        {
                            StrategyId = c.Strategy.Id, Name = c.Strategy.Name,
                            Symbol = c.Strategy.Symbol, Timeframe = c.Strategy.Timeframe,
                            StrategyType = c.Strategy.StrategyType, Regime = c.Regime,
                            IsSharpeRatio = c.Metrics.IsSharpeRatio,
                            OosSharpeRatio = c.Metrics.OosSharpeRatio,
                            EquityCurveR2 = c.Metrics.EquityCurveR2,
                            MonteCarloPValue = c.Metrics.MonteCarloPValue,
                            WalkForwardWindowsPassed = c.Metrics.WalkForwardWindowsPassed,
                        });
                    }
                }
                await writeCtx.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: fast-track auto-promote failed");
        }

        return confirmed.Count;
    }

    private static async Task<IDbContextTransaction?> TryBeginTransactionAsync(DbContext writeDb, CancellationToken ct)
    {
        try { return await writeDb.Database.BeginTransactionAsync(ct); }
        catch { return null; }
    }

    private static async Task TryCompensateUnsafelyPersistedStrategyAsync(
        DbContext writeDb, IWriteApplicationDbContext writeCtx, Strategy strategy, CancellationToken ct)
    {
        if (strategy.Id <= 0) return;

        try
        {
            var persisted = await writeDb.Set<Strategy>().FindAsync([strategy.Id], ct);
            if (persisted == null) return;

            writeDb.Set<Strategy>().Remove(persisted);
            await writeCtx.SaveChangesAsync(ct);
        }
        catch
        {
            foreach (var entry in writeDb.ChangeTracker.Entries()
                .Where(e => e.State != EntityState.Unchanged).ToList())
                entry.State = EntityState.Detached;
        }
    }

    // ── Stage 8: Pruning ────────────────────────────────────────────────────

    private async Task<int> PruneStaleStrategiesAsync(
        IReadApplicationDbContext readCtx, IWriteApplicationDbContext writeCtx,
        ScreeningAuditLogger auditLogger, int pruneAfterFailed, CancellationToken ct)
    {
        var db = readCtx.GetDbContext();
        var writeDb = writeCtx.GetDbContext();

        var drafts = await db.Set<Strategy>()
            .Where(s => s.LifecycleStage == StrategyLifecycleStage.Draft
                     && s.Status == StrategyStatus.Paused
                     && s.Name.StartsWith("Auto-") && !s.IsDeleted)
            .Select(s => new { s.Id, s.Name }).ToListAsync(ct);

        if (drafts.Count == 0) return 0;

        var draftIds = drafts.Select(d => d.Id).ToList();
        var backtestCounts = await db.Set<BacktestRun>()
            .Where(r => draftIds.Contains(r.StrategyId) && !r.IsDeleted)
            .GroupBy(r => r.StrategyId)
            .Select(g => new { StrategyId = g.Key, Failed = g.Count(r => r.Status == RunStatus.Failed), Completed = g.Count(r => r.Status == RunStatus.Completed) })
            .ToListAsync(ct);

        var countMap = backtestCounts.ToDictionary(c => c.StrategyId);
        var toPrune = new List<(long Id, string Name, int Failed)>();

        foreach (var draft in drafts)
        {
            if (!countMap.TryGetValue(draft.Id, out var counts)) continue;
            if (counts.Failed < pruneAfterFailed || counts.Completed > 0) continue;
            var entity = await writeDb.Set<Strategy>().FindAsync([draft.Id], ct);
            if (entity is null) continue;
            entity.IsDeleted = true;
            toPrune.Add((draft.Id, draft.Name, counts.Failed));
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<bool> IsInDrawdownRecoveryAsync(DbContext db, CancellationToken ct)
    {
        var latest = await db.Set<DrawdownSnapshot>()
            .Where(s => !s.IsDeleted)
            .OrderByDescending(s => s.RecordedAt)
            .FirstOrDefaultAsync(ct);
        return latest != null && latest.RecoveryMode != RecoveryMode.Normal;
    }

    // ── Performance feedback (#12: recency-weighted, cached summary) ──────

    /// <summary>Serialisable feedback summary persisted to EngineConfig for cross-cycle caching.</summary>
    private sealed record FeedbackSummaryEntry(string StrategyType, string Regime, double SurvivalRate);
    private sealed record FeedbackStrategySnapshot(
        StrategyType StrategyType,
        string? Description,
        string? ScreeningMetricsJson,
        string? ParametersJson,
        StrategyLifecycleStage LifecycleStage,
        bool IsDeleted,
        DateTime CreatedAt);
    private sealed record FeedbackSummaryCache(string SignatureFingerprint, int StrategyCount, DateTime ComputedAtUtc, List<FeedbackSummaryEntry> Entries);

    private static async Task<(Dictionary<(StrategyType, MarketRegimeEnum), double> TypeRates, Dictionary<string, double> TemplateRates)>
        LoadPerformanceFeedbackAsync(DbContext db, IWriteApplicationDbContext writeCtx, double halfLifeDays, CancellationToken ct)
    {
        var feedbackCutoff = DateTime.UtcNow.AddDays(-180);

        var allAutoStrategies = await db.Set<Strategy>()
            .IncludingSoftDeleted()
            .Where(s => s.Name.StartsWith("Auto-") && s.CreatedAt >= feedbackCutoff)
            .Select(s => new FeedbackStrategySnapshot(
                s.StrategyType,
                s.Description,
                s.ScreeningMetricsJson,
                s.ParametersJson,
                s.LifecycleStage,
                s.IsDeleted,
                s.CreatedAt))
            .ToListAsync(ct);

        if (allAutoStrategies.Count == 0)
            return (new Dictionary<(StrategyType, MarketRegimeEnum), double>(), new Dictionary<string, double>(StringComparer.Ordinal));

        int strategyCount = allAutoStrategies.Count;
        string currentFingerprint = ComputeFeedbackFingerprint(allAutoStrategies);

        // Try to use cached summary if the underlying evidence fingerprint hasn't changed.
        var cachedJson = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key == "StrategyGeneration:FeedbackSummary")
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);

        if (cachedJson != null)
        {
            try
            {
                var cached = System.Text.Json.JsonSerializer.Deserialize<FeedbackSummaryCache>(cachedJson);
                if (cached != null
                    && cached.StrategyCount == strategyCount
                    && string.Equals(cached.SignatureFingerprint, currentFingerprint, StringComparison.Ordinal)
                    && (DateTime.UtcNow - cached.ComputedAtUtc).TotalHours < 24)
                {
                    var cachedTypeRates = DeserializeFeedbackRates(cached.Entries);
                    var freshTemplateRates = ComputeTemplateFeedbackRates(allAutoStrategies, halfLifeDays);
                    return (cachedTypeRates, freshTemplateRates);
                }
            }
            catch { /* Stale/corrupt cache — fall through to full recomputation */ }
        }

        // Full recomputation
        var (rates, templateRates) = ComputeFeedbackRates(allAutoStrategies, halfLifeDays);

        // Persist the computed type-rate summary for next cycle (best-effort, via write context)
        // Template rates are not cached — they change more frequently
        try
        {
            var writeDb = writeCtx.GetDbContext();
            var summary = new FeedbackSummaryCache(
                currentFingerprint, strategyCount, DateTime.UtcNow,
                rates.Select(kv => new FeedbackSummaryEntry(
                    kv.Key.Item1.ToString(), kv.Key.Item2.ToString(), kv.Value)).ToList());
            var summaryJson = System.Text.Json.JsonSerializer.Serialize(summary);

            await UpsertConfigAsync(db, writeDb, "StrategyGeneration:FeedbackSummary", summaryJson,
                "Cached performance feedback summary (auto-managed)", ct);
            await writeCtx.SaveChangesAsync(ct);
        }
        catch { /* Non-critical — next cycle will recompute */ }

        return (rates, templateRates);
    }

    private static string ComputeFeedbackFingerprint(IReadOnlyList<FeedbackStrategySnapshot> strategies)
    {
        var parts = strategies
            .OrderBy(s => s.StrategyType)
            .ThenBy(s => s.LifecycleStage)
            .ThenBy(s => s.IsDeleted)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.Description, StringComparer.Ordinal)
            .ThenBy(s => s.ScreeningMetricsJson, StringComparer.Ordinal)
            .Select(s => string.Join("|",
                s.StrategyType,
                s.LifecycleStage,
                s.IsDeleted ? "1" : "0",
                s.CreatedAt.ToUniversalTime().Ticks,
                s.Description ?? string.Empty,
                s.ScreeningMetricsJson ?? string.Empty));

        using var sha = SHA256.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", parts));
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    private static async Task<(Dictionary<(StrategyType, MarketRegimeEnum), double> TypeRates, Dictionary<string, double> TemplateRates)>
        ComputeFeedbackRatesAsync(DbContext db, DateTime feedbackCutoff, double halfLifeDays, CancellationToken ct)
    {
        var allAutoStrategies = await db.Set<Strategy>()
            .IncludingSoftDeleted()
            .Where(s => s.Name.StartsWith("Auto-") && s.CreatedAt >= feedbackCutoff)
            .Select(s => new FeedbackStrategySnapshot(
                s.StrategyType,
                s.Description,
                s.ScreeningMetricsJson,
                s.ParametersJson,
                s.LifecycleStage,
                s.IsDeleted,
                s.CreatedAt))
            .ToListAsync(ct);

        return ComputeFeedbackRates(allAutoStrategies, halfLifeDays);
    }

    private static (Dictionary<(StrategyType, MarketRegimeEnum), double> TypeRates, Dictionary<string, double> TemplateRates)
        ComputeFeedbackRates(IReadOnlyList<FeedbackStrategySnapshot> allAutoStrategies, double halfLifeDays)
    {
        var rates = new Dictionary<(StrategyType, MarketRegimeEnum), double>();
        var templateRates = ComputeTemplateFeedbackRates(allAutoStrategies, halfLifeDays);

        if (allAutoStrategies.Count == 0)
            return (rates, templateRates);

        foreach (var group in allAutoStrategies.GroupBy(s => s.StrategyType))
        {
            var byRegime = new Dictionary<MarketRegimeEnum, List<(bool Survived, DateTime CreatedAt)>>();

            foreach (var strategy in group)
            {
                MarketRegimeEnum? regime = null;
                var metrics = ScreeningMetrics.FromJson(strategy.ScreeningMetricsJson);
                if (metrics != null && Enum.TryParse<MarketRegimeEnum>(metrics.Regime, out var parsed))
                    regime = parsed;
                else
                    regime = ParseRegimeFromDescription(strategy.Description);

                if (regime == null) continue;

                if (!byRegime.TryGetValue(regime.Value, out var list))
                {
                    list = [];
                    byRegime[regime.Value] = list;
                }

                bool survived = !strategy.IsDeleted && strategy.LifecycleStage >= StrategyLifecycleStage.BacktestQualified;
                list.Add((survived, strategy.CreatedAt));
            }

            foreach (var (regime, strategies) in byRegime)
            {
                if (strategies.Count >= 3)
                    rates[(group.Key, regime)] = ComputeRecencyWeightedSurvivalRate(strategies, halfLifeDays);
            }
        }

        return (rates, templateRates);
    }

    private static Dictionary<string, double> ComputeTemplateFeedbackRates(
        IReadOnlyList<FeedbackStrategySnapshot> allAutoStrategies,
        double halfLifeDays)
    {
        var templateGroups = new Dictionary<string, List<(bool Survived, DateTime CreatedAt)>>(StringComparer.Ordinal);

        foreach (var strategy in allAutoStrategies)
        {
            if (string.IsNullOrWhiteSpace(strategy.ParametersJson))
                continue;

            if (!templateGroups.TryGetValue(strategy.ParametersJson, out var tList))
            {
                tList = [];
                templateGroups[strategy.ParametersJson] = tList;
            }

            bool survived = !strategy.IsDeleted && strategy.LifecycleStage >= StrategyLifecycleStage.BacktestQualified;
            tList.Add((survived, strategy.CreatedAt));
        }

        var templateRates = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (paramsJson, samples) in templateGroups)
        {
            if (samples.Count >= 2)
                templateRates[paramsJson] = ComputeRecencyWeightedSurvivalRate(samples, halfLifeDays);
        }

        return templateRates;
    }

    private static Dictionary<(StrategyType, MarketRegimeEnum), double> DeserializeFeedbackRates(
        List<FeedbackSummaryEntry> entries)
    {
        var rates = new Dictionary<(StrategyType, MarketRegimeEnum), double>();
        foreach (var e in entries)
        {
            if (Enum.TryParse<StrategyType>(e.StrategyType, out var st)
                && Enum.TryParse<MarketRegimeEnum>(e.Regime, out var regime))
                rates[(st, regime)] = e.SurvivalRate;
        }
        return rates;
    }

    private static MarketRegimeEnum? ParseRegimeFromDescription(string? description)
    {
        if (string.IsNullOrEmpty(description)) return null;
        foreach (var regime in Enum.GetValues<MarketRegimeEnum>())
            if (description.Contains(regime.ToString(), StringComparison.OrdinalIgnoreCase))
                return regime;
        return null;
    }

    /// <summary>
    /// Detects when performance feedback and adaptive thresholds give contradictory signals.
    /// Feedback boosts a strategy type (high survival rate) while adaptive thresholds tighten
    /// (multiplier &lt; 1.0) — meaning recent screening metrics for that type were poor despite
    /// historical survivors doing well. Logs a warning so operators can investigate.
    /// </summary>
    private void DetectFeedbackAdaptiveContradictions(
        Dictionary<(StrategyType, MarketRegimeEnum), double> feedbackRates,
        AdaptiveThresholdAdjustments adaptiveAdjustments)
    {
        if (feedbackRates.Count == 0 || adaptiveAdjustments == AdaptiveThresholdAdjustments.Neutral)
            return;

        // A "tightened" threshold means the adaptive multiplier is < 1.0 (recent candidates
        // are performing worse than the base threshold, so the engine raises the bar).
        bool thresholdsTightened = adaptiveAdjustments.WinRateMultiplier < 0.95
            || adaptiveAdjustments.ProfitFactorMultiplier < 0.95
            || adaptiveAdjustments.SharpeMultiplier < 0.95;

        if (!thresholdsTightened) return;

        // A "boosted" type has a survival rate > 0.6 (well above the 0.5 neutral default).
        const double boostThreshold = 0.6;
        foreach (var ((strategyType, regime), survivalRate) in feedbackRates)
        {
            if (survivalRate <= boostThreshold) continue;

            _logger.LogWarning(
                "StrategyGenerationWorker: contradiction — {Type} in {Regime} has {SurvivalRate:P0} survival " +
                "but adaptive thresholds are tightened (WR×{WR:F2}, PF×{PF:F2}, Sharpe×{Sh:F2}). " +
                "Historical survivors are strong but recent screening candidates are weak — " +
                "consider investigating regime shift or template staleness.",
                strategyType, regime, survivalRate,
                adaptiveAdjustments.WinRateMultiplier,
                adaptiveAdjustments.ProfitFactorMultiplier,
                adaptiveAdjustments.SharpeMultiplier);
            _metrics.StrategyGenFeedbackAdaptiveContradictions.Add(1,
                new KeyValuePair<string, object?>("strategy_type", strategyType.ToString()));
        }
    }

    private IReadOnlyList<StrategyType> ApplyPerformanceFeedback(
        IReadOnlyList<StrategyType> types, MarketRegimeEnum regime,
        Dictionary<(StrategyType, MarketRegimeEnum), double> feedbackRates)
    {
        if (feedbackRates.Count == 0 || types.Count <= 1) return types;
        if (!types.Any(t => feedbackRates.ContainsKey((t, regime)))) return types;

        return types.OrderByDescending(t =>
        {
            if (feedbackRates.TryGetValue((t, regime), out var rate))
            {
                _metrics.StrategyGenFeedbackBoosted.Add(1,
                    new KeyValuePair<string, object?>("strategy_type", t.ToString()));
                return rate;
            }
            return 0.5;
        }).ToList();
    }

    // ── Adaptive thresholds (#11: exclude pruned, #23: use structured metrics) ──

    private async Task<AdaptiveThresholdAdjustments> ComputeAdaptiveThresholdsAsync(
        DbContext db, GenerationConfig config, CancellationToken ct)
    {
        var recentCutoff = DateTime.UtcNow.AddDays(-90);

        // #11: Only include non-deleted strategies (exclude pruned false positives)
        var recentStrategies = await db.Set<Strategy>()
            .Where(s => s.Name.StartsWith("Auto-") && s.CreatedAt >= recentCutoff && !s.IsDeleted)
            .Select(s => new { s.ScreeningMetricsJson, s.Description })
            .ToListAsync(ct);

        if (recentStrategies.Count < config.AdaptiveThresholdsMinSamples)
            return AdaptiveThresholdAdjustments.Neutral;

        var winRates = new List<double>();
        var profitFactors = new List<double>();
        var sharpes = new List<double>();

        foreach (var s in recentStrategies)
        {
            // #23: Use structured metrics first
            var metrics = ScreeningMetrics.FromJson(s.ScreeningMetricsJson);
            if (metrics != null)
            {
                winRates.Add(metrics.IsWinRate);
                profitFactors.Add(metrics.IsProfitFactor);
                sharpes.Add(metrics.IsSharpeRatio);
            }
        }

        if (winRates.Count < config.AdaptiveThresholdsMinSamples)
            return AdaptiveThresholdAdjustments.Neutral;

        double wrMult = ComputeAdaptiveMultiplier(Median(winRates), config.MinWinRate);
        double pfMult = ComputeAdaptiveMultiplier(Median(profitFactors), config.MinProfitFactor);
        double shMult = ComputeAdaptiveMultiplier(Median(sharpes), config.MinSharpe);

        var adj = new AdaptiveThresholdAdjustments(wrMult, pfMult, shMult, 1.0);
        if (adj != AdaptiveThresholdAdjustments.Neutral)
        {
            _metrics.StrategyGenAdaptiveThresholdsApplied.Add(1);
            _logger.LogInformation(
                "StrategyGenerationWorker: adaptive thresholds — WR×{WR:F2}, PF×{PF:F2}, Sharpe×{Sh:F2} ({N} samples)",
                wrMult, pfMult, shMult, winRates.Count);
        }

        return adj;
    }

    // ── Screening config builder ─────────────────────────────────────────────

    private ScreeningConfig BuildScreeningConfig(GenerationConfig config)
    {
        var splitPcts = ParseWalkForwardSplitPcts(config.WalkForwardSplitPcts);

        // Validate walk-forward window count against split percentages
        if (config.WalkForwardWindowCount != splitPcts.Count)
        {
            _logger.LogWarning(
                "StrategyGenerationWorker: WalkForwardWindowCount={WindowCount} but WalkForwardSplitPcts has {SplitCount} entries — " +
                "the engine will use {SplitCount} windows. Update StrategyGeneration:WalkForwardWindowCount to match.",
                config.WalkForwardWindowCount, splitPcts.Count, splitPcts.Count);
        }

        if (config.WalkForwardMinWindowsPass > splitPcts.Count)
        {
            _logger.LogWarning(
                "StrategyGenerationWorker: WalkForwardMinWindowsPass={MinPass} exceeds available windows ({Count}) — " +
                "walk-forward will be impossible to pass. Clamping to {Count}.",
                config.WalkForwardMinWindowsPass, splitPcts.Count, splitPcts.Count);
        }

        return new ScreeningConfig
        {
            ScreeningTimeoutSeconds       = config.ScreeningTimeoutSeconds,
            ScreeningInitialBalance       = config.ScreeningInitialBalance,
            MaxOosDegradationPct          = config.MaxOosDegradationPct,
            MinEquityCurveR2              = config.MinEquityCurveR2,
            MaxTradeTimeConcentration     = config.MaxTradeTimeConcentration,
            MonteCarloEnabled             = config.MonteCarloEnabled,
            MonteCarloPermutations        = config.MonteCarloPermutations,
            MonteCarloMinPValue           = config.MonteCarloMinPValue,
            MonteCarloShuffleEnabled      = config.MonteCarloShuffleEnabled,
            WalkForwardWindowCount        = splitPcts.Count,
            WalkForwardMinWindowsPass     = Math.Min(config.WalkForwardMinWindowsPass, splitPcts.Count),
            WalkForwardSplitPcts          = splitPcts,
            MonteCarloShufflePermutations = config.MonteCarloShufflePermutations,
            MonteCarloShuffleMinPValue    = config.MonteCarloShuffleMinPValue,
            ActiveStrategyCount           = config.ActiveStrategyCount,
        };
    }

    private static List<double> ParseWalkForwardSplitPcts(string raw)
    {
        var result = new List<double>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (double.TryParse(part, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var val) && val > 0 && val < 1)
                result.Add(val);
        return result.Count >= 2 ? result : [0.40, 0.55, 0.70];
    }

    // ── Correlation group logic ─────────────────────────────────────────────

    private Dictionary<int, int> BuildCorrelationGroupCounts(IReadOnlyList<string> activeSymbols)
    {
        var counts = new Dictionary<int, int>();
        foreach (var symbol in activeSymbols)
        {
            int? groupIdx = FindCorrelationGroupIndex(symbol);
            if (groupIdx.HasValue) counts[groupIdx.Value] = counts.GetValueOrDefault(groupIdx.Value) + 1;
        }
        return counts;
    }

    private bool IsCorrelationGroupSaturated(string symbol, Dictionary<int, int> groupCounts, int maxPerGroup)
    {
        int? groupIdx = FindCorrelationGroupIndex(symbol);
        return groupIdx.HasValue && groupCounts.GetValueOrDefault(groupIdx.Value) >= maxPerGroup;
    }

    private void IncrementCorrelationGroupCount(string symbol, Dictionary<int, int> groupCounts)
    {
        int? groupIdx = FindCorrelationGroupIndex(symbol);
        if (groupIdx.HasValue) groupCounts[groupIdx.Value] = groupCounts.GetValueOrDefault(groupIdx.Value) + 1;
    }

    private int? FindCorrelationGroupIndex(string symbol)
    {
        var upper = symbol.ToUpperInvariant();
        for (int i = 0; i < _correlationGroups.Length; i++)
            if (_correlationGroups[i].Any(s => s.Equals(upper, StringComparison.OrdinalIgnoreCase)))
                return i;
        return null;
    }

    // ── Config validation (#13: range checks, #14: dependency enforcement) ──

    private void ValidateConfiguration(GenerationConfig config)
    {
        // Range checks
        if (config.MinWinRate is <= 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationWorker: MinWinRate={Value} is outside valid range (0, 1]", config.MinWinRate);
        if (config.MaxDrawdownPct is <= 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationWorker: MaxDrawdownPct={Value} is outside valid range (0, 1]", config.MaxDrawdownPct);
        if (config.MinRegimeConfidence is < 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationWorker: MinRegimeConfidence={Value} is outside valid range [0, 1]", config.MinRegimeConfidence);
        if (config.MaxOosDegradationPct is < 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationWorker: MaxOosDegradationPct={Value} is outside valid range [0, 1]", config.MaxOosDegradationPct);
        if (config.RegimeBudgetDiversityPct is <= 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationWorker: RegimeBudgetDiversityPct={Value} is outside valid range (0, 1]", config.RegimeBudgetDiversityPct);
        if (config.MaxCandidates <= 0)
            _logger.LogWarning("StrategyGenerationWorker: MaxCandidatesPerCycle={Value} must be positive", config.MaxCandidates);
        if (config.ScreeningMonths <= 0)
            _logger.LogWarning("StrategyGenerationWorker: ScreeningWindowMonths={Value} must be positive", config.ScreeningMonths);
        if (config.MinTotalTrades < 1)
            _logger.LogWarning("StrategyGenerationWorker: MinTotalTrades={Value} must be at least 1", config.MinTotalTrades);

        // Dependency enforcement
        if (config.MaxCandidates < config.StrategicReserveQuota)
            _logger.LogWarning(
                "StrategyGenerationWorker: MaxCandidatesPerCycle ({Max}) < StrategicReserveQuota ({Reserve}) — " +
                "primary candidates may never be generated",
                config.MaxCandidates, config.StrategicReserveQuota);
        if (config.MaxActivePerSymbol < config.MaxActivePerTypePerSymbol)
            _logger.LogWarning(
                "StrategyGenerationWorker: MaxActivePerSymbol ({PerSymbol}) < MaxActivePerTypePerSymbol ({PerType}) — " +
                "per-type limit is unreachable",
                config.MaxActivePerSymbol, config.MaxActivePerTypePerSymbol);
        if (config.MaxCandidatesPerWeek < config.MaxCandidates)
            _logger.LogWarning(
                "StrategyGenerationWorker: MaxCandidatesPerWeek ({Weekly}) < MaxCandidatesPerCycle ({Cycle}) — " +
                "a single cycle could exhaust the weekly budget",
                config.MaxCandidatesPerWeek, config.MaxCandidates);
        if (config.MonteCarloEnabled && config.MonteCarloPermutations < 100)
            _logger.LogWarning(
                "StrategyGenerationWorker: MonteCarloPermutations={Value} is very low — results may be unreliable",
                config.MonteCarloPermutations);
    }

    // ── Config ──────────────────────────────────────────────────────────────

    private static async Task<(GenerationConfig Config, Dictionary<string, string> RawConfigs)>
        LoadConfigurationAsync(DbContext db, CancellationToken ct)
    {
        // #3: Batch-load all StrategyGeneration:* keys in a single query
        var allConfigs = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("StrategyGeneration:"))
            .ToDictionaryAsync(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase, ct);

        T Get<T>(string key, T defaultValue)
        {
            if (!allConfigs.TryGetValue(key, out var raw) || raw is null) return defaultValue;
            try { return (T)Convert.ChangeType(raw, typeof(T)); }
            catch { return defaultValue; }
        }

        var config = new GenerationConfig
        {
            ScreeningMonths               = Get("StrategyGeneration:ScreeningWindowMonths", 6),
            MinWinRate                    = Get("StrategyGeneration:MinWinRate", 0.60),
            MinProfitFactor               = Get("StrategyGeneration:MinProfitFactor", 1.1),
            MinTotalTrades                = Get("StrategyGeneration:MinTotalTrades", 15),
            MaxDrawdownPct                = Get("StrategyGeneration:MaxDrawdownPct", 0.20),
            MinSharpe                     = Get("StrategyGeneration:MinSharpeRatio", 0.3),
            MaxCandidates                 = Get("StrategyGeneration:MaxCandidatesPerCycle", 50),
            MaxActivePerSymbol            = Get("StrategyGeneration:MaxActiveStrategiesPerSymbol", 3),
            MaxActivePerTypePerSymbol     = Get("StrategyGeneration:MaxActivePerTypePerSymbol", 2),
            PruneAfterFailed              = Get("StrategyGeneration:PruneAfterFailedBacktests", 3),
            RegimeFreshnessHours          = Get("StrategyGeneration:RegimeFreshnessHours", 48),
            RetryCooldownDays             = Get("StrategyGeneration:RetryCooldownDays", 30),
            MaxPerCurrencyGroup           = Get("StrategyGeneration:MaxCandidatesPerCurrencyGroup", 6),
            ScreeningSpreadPoints         = Get("StrategyGeneration:ScreeningSpreadPoints", 20.0),
            ScreeningCommissionPerLot     = Get("StrategyGeneration:ScreeningCommissionPerLot", 7.0),
            ScreeningSlippagePips         = Get("StrategyGeneration:ScreeningSlippagePips", 1.0),
            MinRegimeConfidence           = Get("StrategyGeneration:MinRegimeConfidence", 0.60),
            MaxOosDegradationPct          = Get("StrategyGeneration:MaxOosDegradationPct", 0.60),
            SuppressDuringDrawdownRecovery = Get("StrategyGeneration:SuppressDuringDrawdownRecovery", true),
            SeasonalBlackoutEnabled       = Get("StrategyGeneration:SeasonalBlackoutEnabled", true),
            BlackoutPeriods               = Get("StrategyGeneration:BlackoutPeriods", "12/20-01/05"),
            ScreeningTimeoutSeconds       = Get("StrategyGeneration:ScreeningTimeoutSeconds", 30),
            CandidateTimeframes           = ParseTimeframes(Get("StrategyGeneration:CandidateTimeframes", "H1,H4")),
            MaxTemplatesPerCombo          = Get("StrategyGeneration:MaxTemplatesPerCombo", 2),
            StrategicReserveQuota         = Get("StrategyGeneration:StrategicReserveQuota", 3),
            MaxCandidatesPerWeek          = Get("StrategyGeneration:MaxCandidatesPerWeek", 150),
            MaxParallelSymbols            = Get("StrategyGeneration:MaxParallelSymbols", 1),
            MaxSpreadToRangeRatio         = Get("StrategyGeneration:MaxSpreadToRangeRatio", 0.30),
            ScreeningInitialBalance       = Get("StrategyGeneration:ScreeningInitialBalance", 10_000m),
            MaxParallelBacktests          = Get("StrategyGeneration:MaxParallelBacktests", 3),
            RegimeBudgetDiversityPct      = Get("StrategyGeneration:RegimeBudgetDiversityPct", 0.60),
            MinEquityCurveR2              = Get("StrategyGeneration:MinEquityCurveR2", 0.70),
            MaxTradeTimeConcentration     = Get("StrategyGeneration:MaxTradeTimeConcentration", 0.60),
            CircuitBreakerMaxFailures     = Get("StrategyGeneration:CircuitBreakerMaxFailures", 3),
            CircuitBreakerBackoffDays     = Get("StrategyGeneration:CircuitBreakerBackoffDays", 2),
            MaxFaultsPerStrategyType      = Get("StrategyGeneration:MaxFaultsPerStrategyType", 3),
            MaxCandleCacheSize            = Get("StrategyGeneration:MaxCandleCacheSize", 500_000),
            CandleChunkSize               = Get("StrategyGeneration:CandleChunkSize", 5),
            MaxCorrelatedCandidates       = Get("StrategyGeneration:MaxCorrelatedCandidates", 4),
            AdaptiveThresholdsEnabled     = Get("StrategyGeneration:AdaptiveThresholdsEnabled", true),
            AdaptiveThresholdsMinSamples  = Get("StrategyGeneration:AdaptiveThresholdsMinSamples", 10),
            MonteCarloEnabled             = Get("StrategyGeneration:MonteCarloEnabled", true),
            MonteCarloPermutations        = Get("StrategyGeneration:MonteCarloPermutations", 500),
            MonteCarloMinPValue           = Get("StrategyGeneration:MonteCarloMinPValue", 0.05),
            MonteCarloShuffleEnabled      = Get("StrategyGeneration:MonteCarloShuffleEnabled", false),
            MonteCarloShufflePermutations = Get("StrategyGeneration:MonteCarloShufflePermutations", 0),
            MonteCarloShuffleMinPValue    = Get("StrategyGeneration:MonteCarloShuffleMinPValue", 0.0),
            PortfolioBacktestEnabled      = Get("StrategyGeneration:PortfolioBacktestEnabled", true),
            MaxPortfolioDrawdownPct       = Get("StrategyGeneration:MaxPortfolioDrawdownPct", 0.30),
            PortfolioCorrelationWeight   = Get("StrategyGeneration:PortfolioCorrelationWeight", 0.05),
            MaxCandleAgeHours             = Get("StrategyGeneration:MaxCandleAgeHours", 72),
            SkipWeekends                  = Get("StrategyGeneration:SkipWeekends", true),
            BlackoutTimezone              = Get("StrategyGeneration:BlackoutTimezone", "UTC"),
            RegimeTransitionCooldownHours = Get("StrategyGeneration:RegimeTransitionCooldownHours", 12),
            WalkForwardWindowCount        = Get("StrategyGeneration:WalkForwardWindowCount", 3),
            WalkForwardMinWindowsPass     = Get("StrategyGeneration:WalkForwardMinWindowsPass", 2),
            WalkForwardSplitPcts          = Get("StrategyGeneration:WalkForwardSplitPcts", "0.40,0.55,0.70"),
        };

        return (config, allConfigs);
    }

    /// <summary>
    /// Extracts per-symbol threshold overrides from the batch-loaded config dictionary.
    /// Keys: <c>StrategyGeneration:Overrides:{SYMBOL}:{Key}</c>. Zero additional DB queries.
    /// </summary>
    private static (double? MinWinRate, double? MinProfitFactor, double? MinSharpe, double? MaxDrawdownPct)
        ExtractSymbolOverrides(Dictionary<string, string> allConfigs, string symbol)
    {
        var prefix = $"StrategyGeneration:Overrides:{symbol}:";
        double? wr = null, pf = null, sh = null, dd = null;

        foreach (var (key, value) in allConfigs)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = key[prefix.Length..];
            if (suffix.Equals("MinWinRate", StringComparison.OrdinalIgnoreCase) && double.TryParse(value, out var v1)) wr = v1;
            else if (suffix.Equals("MinProfitFactor", StringComparison.OrdinalIgnoreCase) && double.TryParse(value, out var v2)) pf = v2;
            else if (suffix.Equals("MinSharpeRatio", StringComparison.OrdinalIgnoreCase) && double.TryParse(value, out var v3)) sh = v3;
            else if (suffix.Equals("MaxDrawdownPct", StringComparison.OrdinalIgnoreCase) && double.TryParse(value, out var v4)) dd = v4;
        }

        return (wr, pf, sh, dd);
    }

    private static async Task PersistLastRunDateAsync(
        IReadApplicationDbContext readCtx, IWriteApplicationDbContext writeCtx, CancellationToken ct)
    {
        await UpsertConfigAsync(readCtx.GetDbContext(), writeCtx.GetDbContext(),
            "StrategyGeneration:LastRunDateUtc", DateTime.UtcNow.Date.ToString("O"),
            "Last date the StrategyGenerationWorker ran (auto-managed)", ct);
        await writeCtx.SaveChangesAsync(ct);
    }

    private async Task PersistSchedulingStateAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var writeDb  = writeCtx.GetDbContext();
            var readDb   = readCtx.GetDbContext();

            await UpsertConfigAsync(readDb, writeDb, "StrategyGeneration:LastRunDateUtc",
                _scheduling.LastRunDateUtc == DateTime.MinValue ? string.Empty : _scheduling.LastRunDateUtc.ToString("O"),
                "Last date the StrategyGenerationWorker ran (auto-managed)", ct);
            await UpsertConfigAsync(readDb, writeDb, "StrategyGeneration:CircuitBreakerUntilUtc",
                _scheduling.CircuitBreakerUntilUtc.ToString("O"), "Circuit breaker expiry (auto-managed)", ct);
            await UpsertConfigAsync(readDb, writeDb, "StrategyGeneration:ConsecutiveFailures",
                _scheduling.ConsecutiveFailures.ToString(), "Consecutive cycle failures (auto-managed)", ct);
            await UpsertConfigAsync(readDb, writeDb, "StrategyGeneration:RetriesThisWindow",
                _scheduling.RetriesThisWindow.ToString(), "Retries consumed within the active schedule window (auto-managed)", ct);
            await UpsertConfigAsync(readDb, writeDb, "StrategyGeneration:RetryWindowDateUtc",
                _scheduling.RetryWindowDateUtc == DateTime.MinValue ? string.Empty : _scheduling.RetryWindowDateUtc.ToString("O"),
                "Date for the active retry window (auto-managed)", ct);

            await writeCtx.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: failed to persist scheduling state");
        }
    }

    private static async Task UpsertConfigAsync(
        DbContext readDb, DbContext writeDb, string key, string value, string description, CancellationToken ct)
    {
        var exists = await readDb.Set<EngineConfig>().AsNoTracking().AnyAsync(c => c.Key == key, ct);
        if (exists)
        {
            var tracked = await writeDb.Set<EngineConfig>().FirstOrDefaultAsync(c => c.Key == key, ct);
            if (tracked != null)
            {
                tracked.Value = value;
                tracked.LastUpdatedAt = DateTime.UtcNow;
            }
        }
        else
        {
            writeDb.Set<EngineConfig>().Add(new EngineConfig
            {
                Key = key, Value = value,
                Description = description,
                DataType = ConfigDataType.String, IsHotReloadable = true,
                LastUpdatedAt = DateTime.UtcNow,
            });
        }
    }

    private static async Task<T> GetConfigAsync<T>(DbContext ctx, string key, T defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>().AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct);
        if (entry?.Value is null) return defaultValue;
        try { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    private string ComputeCheckpointFingerprint(ScreeningContext s)
    {
        var parts = new List<string>();

        parts.AddRange(s.RawConfigs
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"cfg|{kv.Key}|{kv.Value}"));

        parts.AddRange(s.ActivePairs
            .OrderBy(sym => sym, StringComparer.OrdinalIgnoreCase)
            .Select(sym => $"pair|{sym}"));

        parts.AddRange(s.RegimeBySymbol
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                double confidence = s.RegimeConfidenceBySymbol.GetValueOrDefault(kv.Key);
                DateTime detectedAt = s.RegimeDetectedAtBySymbol.GetValueOrDefault(kv.Key);
                return $"regime|{kv.Key}|{kv.Value}|{confidence:F6}|{detectedAt.ToUniversalTime():O}";
            }));

        parts.AddRange(s.RegimeBySymbolTf
            .OrderBy(kv => kv.Key.Item1, StringComparer.OrdinalIgnoreCase)
            .ThenBy(kv => kv.Key.Item2)
            .Select(kv => $"regime_tf|{kv.Key.Item1}|{kv.Key.Item2}|{kv.Value}"));

        foreach (var strategyType in Enum.GetValues<StrategyType>().OrderBy(t => t))
        {
            var templates = _templateProvider.GetTemplates(strategyType) ?? [];
            for (int i = 0; i < templates.Count; i++)
                parts.Add($"template|{strategyType}|{i}|{templates[i]}");
        }

        using var sha = SHA256.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", parts));
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    // ── Dynamic template refresh ────────────────────────────────────────

    /// <summary>
    /// Loads optimized parameters from promoted Auto-generated strategies and feeds them
    /// back into the template provider so future generation cycles start with proven params.
    /// Only includes strategies that reached <c>BacktestQualified</c> or higher and were
    /// optimized within the last 180 days.
    /// </summary>
    private async Task RefreshDynamicTemplatesAsync(DbContext db, CancellationToken ct)
    {
        try
        {
            var promotedCutoff = DateTime.UtcNow.AddDays(-180);

            // Two-step query: load qualifying strategies, then load matching optimization runs.
            // Avoids LINQ Join which is fragile with some providers and test mocks.
            var qualifiedStrategies = await db.Set<Strategy>()
                .Where(s => !s.IsDeleted
                         && s.Name.StartsWith("Auto-")
                         && s.LifecycleStage >= StrategyLifecycleStage.BacktestQualified
                         && s.ParametersJson != null && s.ParametersJson != "{}")
                .Select(s => new { s.Id, s.StrategyType, s.ParametersJson })
                .ToListAsync(ct);

            if (qualifiedStrategies.Count == 0)
            {
                _templateProvider.RefreshDynamicTemplates(
                    new Dictionary<StrategyType, IReadOnlyList<string>>());
                return;
            }

            var qualifiedIds = qualifiedStrategies.Select(s => s.Id).ToList();
            var approvedOptRuns = await db.Set<OptimizationRun>()
                .Where(o => !o.IsDeleted
                         && o.Status == OptimizationRunStatus.Completed
                         && o.ApprovedAt != null
                         && o.ApprovedAt >= promotedCutoff
                         && qualifiedIds.Contains(o.StrategyId))
                .Select(o => new { o.StrategyId, o.ApprovedAt })
                .ToListAsync(ct);

            var approvedStrategyIds = new HashSet<long>(approvedOptRuns.Select(o => o.StrategyId));
            var promoted = qualifiedStrategies
                .Where(s => approvedStrategyIds.Contains(s.Id))
                .ToList();

            if (promoted.Count == 0)
            {
                _templateProvider.RefreshDynamicTemplates(
                    new Dictionary<StrategyType, IReadOnlyList<string>>());
                return;
            }

            // Order by most recently approved (via opt run approval date)
            var approvalDateByStrategy = approvedOptRuns
                .GroupBy(o => o.StrategyId)
                .ToDictionary(g => g.Key, g => g.Max(o => o.ApprovedAt));

            var grouped = promoted
                .OrderByDescending(s => approvalDateByStrategy.GetValueOrDefault(s.Id))
                .GroupBy(x => x.StrategyType)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)g
                        .Select(x => x.ParametersJson!)
                        .Distinct(StringComparer.Ordinal)
                        .ToList());

            _templateProvider.RefreshDynamicTemplates(grouped);

            int totalDynamic = grouped.Values.Sum(v => v.Count);
            if (totalDynamic > 0)
                _logger.LogInformation(
                    "StrategyGenerationWorker: refreshed {Count} dynamic templates from {Types} strategy types",
                    totalDynamic, grouped.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: failed to refresh dynamic templates — using static defaults");
        }
    }
}
