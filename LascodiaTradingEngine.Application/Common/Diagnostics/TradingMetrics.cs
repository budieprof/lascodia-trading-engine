using System.Diagnostics.Metrics;

namespace LascodiaTradingEngine.Application.Common.Diagnostics;

/// <summary>
/// Centralized trading engine metrics using <see cref="System.Diagnostics.Metrics"/>.
/// Exposed via Prometheus at <c>/metrics</c>.
///
/// All instruments are created from a single <see cref="Meter"/> so they can be
/// discovered and exported together.
/// </summary>
public sealed class TradingMetrics
{
    public const string MeterName = "LascodiaTradingEngine";

    private readonly Meter _meter;

    // ── Orders ──────────────────────────────────────────────────────────────
    public Counter<long>     OrdersSubmitted        { get; }
    public Counter<long>     OrdersFilled           { get; }
    public Counter<long>     OrdersFailed           { get; }
    public Histogram<double> OrderFillLatencyMs     { get; }
    public Histogram<double> OrderSlippagePips      { get; }

    // ── Signals ─────────────────────────────────────────────────────────────
    public Counter<long>     SignalsGenerated       { get; }
    public Counter<long>     SignalsAccepted        { get; }
    public Counter<long>     SignalsRejected        { get; }
    public Counter<long>     SignalsFiltered        { get; }
    public Counter<long>     EvaluatorRejections    { get; }
    public Counter<long>     SignalsSuppressed      { get; }
    public Counter<long>     SignalCooldownSkips    { get; }
    public Counter<long>     TicksDroppedLockBusy   { get; }
    public Counter<long>     TicksDroppedBackpressure { get; }
    public Counter<long>     TicksSkippedNoEA       { get; }
    public Counter<long>     TicksSkippedHealthCritical { get; }
    public Counter<long>     TicksSkippedStale      { get; }
    public Counter<long>     StrategiesCircuitBroken { get; }
    public Histogram<double> StrategyEvaluationMs   { get; }

    // ── Positions ───────────────────────────────────────────────────────────
    public Counter<long>     PositionsOpened        { get; }
    public Counter<long>     PositionsClosed        { get; }
    public Histogram<double> PositionPnL            { get; }

    // ── ML ──────────────────────────────────────────────────────────────────
    public Counter<long>     MLTrainingRuns         { get; }
    public Counter<long>     MLTrainingFailures     { get; }
    public Counter<long>     MLPromotions           { get; }
    public Histogram<double> MLTrainingDurationSecs { get; }
    public Histogram<double> MLScoringLatencyMs     { get; }
    public Counter<long>     MLArchitectureSelected { get; }
    public Counter<long>     MLSelectorFallbackDepth { get; }
    public Counter<long>     MLSelectorTrendPenalty  { get; }

    // ── Workers ─────────────────────────────────────────────────────────────
    public Histogram<double> WorkerCycleDurationMs  { get; }
    public Counter<long>     WorkerErrors           { get; }

    // ── Integration Event Retry ─────────────────────────────────────────
    public Counter<long>     EventRetrySuccesses    { get; }
    public Counter<long>     EventRetryExhausted    { get; }

    // ── Strategy Generation ─────────────────────────────────────────────
    public Counter<long>     StrategyCandidatesScreened { get; }
    public Counter<long>     StrategyCandidatesCreated  { get; }
    public Counter<long>     StrategyCandidatesPruned   { get; }
    public Counter<long>     StrategyGenSymbolsSkipped  { get; }
    public Counter<long>     StrategyGenRegimeConfidenceSkipped { get; }
    public Counter<long>     StrategyGenRegimeTransitionSkipped { get; }
    public Counter<long>     StrategyGenDegradationRejected     { get; }
    public Counter<long>     StrategyGenEquityCurveRejected     { get; }
    public Counter<long>     StrategyGenTimeConcentrationRejected { get; }
    public Counter<long>     StrategyGenCorrelationSkipped      { get; }
    public Counter<long>     StrategyGenWalkForwardRejected     { get; }
    public Counter<long>     StrategyGenCircuitBreakerTripped   { get; }
    public Counter<long>     StrategyGenFeedbackBoosted         { get; }
    public Counter<long>     StrategyGenMonteCarloRejected      { get; }
    public Counter<long>     StrategyGenPortfolioDrawdownFiltered { get; }
    public Counter<long>     StrategyGenAdaptiveThresholdsApplied { get; }
    public Counter<long>     StrategyGenWeekendSkipped           { get; }
    public Counter<long>     StrategyGenOosDrawdownDegraded      { get; }
    public Counter<long>     StrategyGenReserveSpreadSkipped     { get; }
    public Counter<long>     StrategyGenScreeningRejections     { get; }
    public Counter<long>     StrategyGenCandleCacheEvictions    { get; }
    public Counter<long>     StrategyGenFeedbackAdaptiveContradictions { get; }
    public Counter<long>     StrategyGenTypeFaultDisabled       { get; }
    public Histogram<double> StrategyGenScreeningDurationMs    { get; }

    // ── Optimization ────────────────────────────────────────────────────
    public Counter<long>     OptimizationRunsProcessed  { get; }
    public Counter<long>     OptimizationRunsFailed     { get; }
    public Counter<long>     OptimizationCandidatesScreened { get; }
    public Counter<long>     OptimizationAutoApproved   { get; }
    public Counter<long>     OptimizationAutoRejected   { get; }
    public Counter<long>     OptimizationCheckpointRestored { get; }
    public Counter<long>     OptimizationCheckpointSaveFailures { get; }
    public Counter<long>     OptimizationLeaseReclaims { get; }
    public Counter<long>     OptimizationDuplicateFollowUpsPrevented { get; }
    public Counter<long>     OptimizationRunsDeferred   { get; }
    public Counter<long>     OptimizationFollowUpFailures { get; }
    public Histogram<double> OptimizationSurrogateImprovement { get; }
    public Counter<long>     OptimizationSurrogateBatchHits { get; }
    public Counter<long>     OptimizationSurrogateBatchMisses { get; }
    public Counter<long>     OptimizationEarlyStopSavings { get; }
    public Counter<long>     OptimizationParameterSpaceExhausted { get; }
    public Counter<long>     OptimizationCircuitBreakerTrips { get; }
    public Counter<long>     OptimizationGateRejections { get; }
    public Histogram<double> OptimizationGateDurationMs { get; }
    public Histogram<double> OptimizationComputeSeconds { get; }
    public Histogram<double> OptimizationPhaseDurationMs { get; }
    public Histogram<double> OptimizationCycleDurationMs { get; }
    public Histogram<double> EhviHypervolumeProgress { get; }
    public Histogram<double> EhviGpPredictionError { get; }
    public Counter<long>     OptimizationSurrogateClamps { get; }
    public Counter<long>     OptimizationDeferredRechecks { get; }
    public Counter<long>     HyperbandBracketsExecuted { get; }
    public Counter<long>     HyperbandBracketsSkipped { get; }
    public Histogram<double> HyperbandSurvivalRate { get; }

    // ── News Sentiment ──────────────────────────────────────────────────
    public Counter<long>     NewsSentimentCallsTotal { get; }
    public Counter<long>     NewsSentimentCallErrors { get; }
    public Counter<long>     NewsSentimentHeadlinesProcessed { get; }
    public Counter<long>     NewsSentimentEventsProcessed { get; }
    public Histogram<double> NewsSentimentCycleDurationMs { get; }
    public Histogram<double> DeepSeekApiLatencyMs { get; }
    public Counter<long>     DeepSeekTokensUsed { get; }

    // ── Candle Aggregator ──────────────────────────────────────────────
    public Counter<long>     CandlesClosed          { get; }
    public Counter<long>     CandlesSynthetic       { get; }
    public Counter<long>     CandleTicksDropped     { get; }
    public Counter<long>     CandleSlotsPurged      { get; }
    public Counter<long>     CandleTicksSpreadRejected { get; }

    // ── Economic Calendar ────────────────────────────────────────────────────
    public Counter<long>     EconEventsIngested     { get; }
    public Counter<long>     EconActualsPatched     { get; }
    public Counter<long>     EconFeedErrors         { get; }
    public Histogram<double> EconCycleDurationMs    { get; }

    // ── Economic Calendar Feed ──────────────────────────────────────────────
    public Counter<long>     EconFeedFetches        { get; }
    public Counter<long>     EconFeedCacheHits      { get; }
    public Counter<long>     EconFeedParseFailures  { get; }
    public Histogram<double> EconFeedFetchLatencyMs  { get; }

    // ── Economic Calendar (observable) ───────────────────────────────────────

    /// <summary>
    /// Registers an observable gauge that reports the number of consecutive polling cycles
    /// where the economic calendar feed returned zero events. Useful for alerting on feed
    /// degradation. Call once from the worker constructor.
    /// </summary>
    public void RegisterEconEmptyFetchGauge(Func<long> observeValue)
    {
        _meter.CreateObservableGauge(
            "trading.econ_calendar.consecutive_empty_fetches",
            observeValue,
            "cycles",
            "Consecutive polling cycles where the feed returned zero events");
    }

    // ── Tick Ingestion ───────────────────────────────────────────────────────
    public Counter<long>     TicksIngested          { get; }
    public Histogram<double> TickBatchSize          { get; }
    public Histogram<double> TickIngestionLatencyMs { get; }

    // ── Cache ───────────────────────────────────────────────────────────────
    public Counter<long>     PriceCacheMisses       { get; }

    // ── Simulated Broker ─────────────────────────────────────────────────
    public Counter<long>     SimBrokerFills         { get; }
    public Counter<long>     SimBrokerRejects       { get; }
    public Counter<long>     SimBrokerRequotes      { get; }
    public Counter<long>     SimBrokerDisconnects   { get; }
    public Counter<long>     SimBrokerStopOuts      { get; }
    public Counter<long>     SimBrokerMarginCalls   { get; }
    public Counter<long>     SimBrokerSlTpCloses    { get; }
    public Counter<long>     SimBrokerPendingFills  { get; }
    public Counter<long>     SimBrokerExpiredOrders { get; }
    public Histogram<double> SimBrokerMarginLevel   { get; }
    public Histogram<double> SimBrokerRealizedPnl   { get; }

    public TradingMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        // Orders
        OrdersSubmitted    = _meter.CreateCounter<long>("trading.orders.submitted", "orders", "Orders submitted to broker");
        OrdersFilled       = _meter.CreateCounter<long>("trading.orders.filled", "orders", "Orders filled by broker");
        OrdersFailed       = _meter.CreateCounter<long>("trading.orders.failed", "orders", "Orders that failed submission");
        OrderFillLatencyMs = _meter.CreateHistogram<double>("trading.orders.fill_latency", "ms", "Broker fill latency");
        OrderSlippagePips  = _meter.CreateHistogram<double>("trading.orders.slippage", "pips", "Fill slippage in pips");

        // Signals
        SignalsGenerated = _meter.CreateCounter<long>("trading.signals.generated", "signals", "Trade signals generated by strategies");
        SignalsAccepted  = _meter.CreateCounter<long>("trading.signals.accepted", "signals", "Signals that passed risk checks");
        SignalsRejected  = _meter.CreateCounter<long>("trading.signals.rejected", "signals", "Signals blocked by risk checks");
        SignalsFiltered  = _meter.CreateCounter<long>("trading.signals.filtered", "signals", "Signals filtered by pipeline stages (MTF, correlation, Hawkes, regime)");
        EvaluatorRejections = _meter.CreateCounter<long>("trading.signals.evaluator_rejections", "signals", "Signals rejected by evaluator-level filters (tag: evaluator, filter)");
        SignalsSuppressed = _meter.CreateCounter<long>("trading.signals.suppressed", "signals", "Signals suppressed by ML scoring or abstention");
        SignalCooldownSkips = _meter.CreateCounter<long>("trading.signals.cooldown_skips", "signals", "Signals skipped due to per-strategy cooldown");
        TicksDroppedLockBusy = _meter.CreateCounter<long>("trading.signals.ticks_dropped_lock_busy", "ticks", "Ticks dropped because strategy evaluation lock was busy");
        TicksDroppedBackpressure = _meter.CreateCounter<long>("trading.signals.ticks_dropped_backpressure", "ticks", "Ticks dropped because evaluation channel is full (backpressure)");
        TicksSkippedNoEA = _meter.CreateCounter<long>("trading.signals.ticks_skipped_no_ea", "ticks", "Ticks skipped because no active EA instance owns the symbol");
        TicksSkippedHealthCritical = _meter.CreateCounter<long>("trading.signals.ticks_skipped_health_critical", "ticks", "Strategies skipped because health status is Critical");
        TicksSkippedStale = _meter.CreateCounter<long>("trading.signals.ticks_skipped_stale", "ticks", "Ticks dropped because event timestamp exceeded MaxTickAgeSeconds");
        StrategiesCircuitBroken = _meter.CreateCounter<long>("trading.signals.strategies_circuit_broken", "strategies", "Strategies temporarily disabled due to consecutive evaluation failures");
        StrategyEvaluationMs = _meter.CreateHistogram<double>("trading.strategy.evaluation_duration", "ms", "Strategy evaluation pipeline duration per tick");

        // Positions
        PositionsOpened = _meter.CreateCounter<long>("trading.positions.opened", "positions", "Positions opened");
        PositionsClosed = _meter.CreateCounter<long>("trading.positions.closed", "positions", "Positions closed");
        PositionPnL     = _meter.CreateHistogram<double>("trading.positions.pnl", "USD", "Realized P&L per closed position");

        // ML
        MLTrainingRuns         = _meter.CreateCounter<long>("trading.ml.training_runs", "runs", "ML training runs started");
        MLTrainingFailures     = _meter.CreateCounter<long>("trading.ml.training_failures", "runs", "ML training runs that failed");
        MLPromotions           = _meter.CreateCounter<long>("trading.ml.promotions", "promotions", "ML models promoted to active");
        MLTrainingDurationSecs = _meter.CreateHistogram<double>("trading.ml.training_duration", "s", "ML training duration");
        MLScoringLatencyMs     = _meter.CreateHistogram<double>("trading.ml.scoring_latency", "ms", "ML signal scoring latency");
        MLArchitectureSelected  = _meter.CreateCounter<long>("trading.ml.architecture_selected", "selections", "ML architecture selections by TrainerSelector");
        MLSelectorFallbackDepth = _meter.CreateCounter<long>("trading.ml.selector_fallback_depth", "selections", "TrainerSelector fallback depth reached per selection");
        MLSelectorTrendPenalty  = _meter.CreateCounter<long>("trading.ml.selector_trend_penalty", "penalties", "Architecture score penalised due to declining performance trend");

        // Workers
        WorkerCycleDurationMs = _meter.CreateHistogram<double>("trading.workers.cycle_duration", "ms", "Worker poll cycle duration");
        WorkerErrors          = _meter.CreateCounter<long>("trading.workers.errors", "errors", "Unhandled worker errors");

        // Integration Event Retry
        EventRetrySuccesses = _meter.CreateCounter<long>("trading.events.retry_successes", "events", "Integration events successfully re-published by retry worker");
        EventRetryExhausted = _meter.CreateCounter<long>("trading.events.retry_exhausted", "events", "Integration events that exhausted retry attempts");

        // Strategy Generation
        StrategyCandidatesScreened = _meter.CreateCounter<long>("trading.strategy_generation.screened", "candidates", "Strategy candidates that completed screening backtest");
        StrategyCandidatesCreated  = _meter.CreateCounter<long>("trading.strategy_generation.created", "strategies", "Strategy candidates that passed screening and were created");
        StrategyCandidatesPruned   = _meter.CreateCounter<long>("trading.strategy_generation.pruned", "strategies", "Draft strategies pruned after repeated backtest failures");
        StrategyGenSymbolsSkipped  = _meter.CreateCounter<long>("trading.strategy_generation.symbols_skipped", "symbols", "Symbols skipped during strategy generation");
        StrategyGenRegimeConfidenceSkipped = _meter.CreateCounter<long>("trading.strategy_generation.regime_confidence_skipped", "symbols", "Symbols skipped due to low regime confidence");
        StrategyGenRegimeTransitionSkipped = _meter.CreateCounter<long>("trading.strategy_generation.regime_transition_skipped", "symbols", "Symbols skipped due to regime transition");
        StrategyGenDegradationRejected     = _meter.CreateCounter<long>("trading.strategy_generation.degradation_rejected", "candidates", "Candidates rejected due to excessive IS-to-OOS metric degradation");
        StrategyGenEquityCurveRejected     = _meter.CreateCounter<long>("trading.strategy_generation.equity_curve_rejected", "candidates", "Candidates rejected due to poor equity curve linearity (low R²)");
        StrategyGenTimeConcentrationRejected = _meter.CreateCounter<long>("trading.strategy_generation.time_concentration_rejected", "candidates", "Candidates rejected due to excessive trade time concentration");
        StrategyGenCorrelationSkipped      = _meter.CreateCounter<long>("trading.strategy_generation.correlation_skipped", "symbols", "Symbols skipped due to correlation group saturation");
        StrategyGenWalkForwardRejected     = _meter.CreateCounter<long>("trading.strategy_generation.walk_forward_rejected", "candidates", "Candidates rejected by walk-forward mini-validation");
        StrategyGenCircuitBreakerTripped   = _meter.CreateCounter<long>("trading.strategy_generation.circuit_breaker_tripped", "cycles", "Generation cycles skipped due to circuit breaker");
        StrategyGenFeedbackBoosted         = _meter.CreateCounter<long>("trading.strategy_generation.feedback_boosted", "candidates", "Candidates boosted or penalised by performance feedback");
        StrategyGenMonteCarloRejected      = _meter.CreateCounter<long>("trading.strategy_generation.monte_carlo_rejected", "candidates", "Candidates rejected by Monte Carlo permutation significance test");
        StrategyGenPortfolioDrawdownFiltered = _meter.CreateCounter<long>("trading.strategy_generation.portfolio_drawdown_filtered", "candidates", "Candidates removed by portfolio-level correlated drawdown check");
        StrategyGenAdaptiveThresholdsApplied = _meter.CreateCounter<long>("trading.strategy_generation.adaptive_thresholds_applied", "cycles", "Cycles where adaptive threshold calibration was applied");
        StrategyGenWeekendSkipped            = _meter.CreateCounter<long>("trading.strategy_generation.weekend_skipped", "cycles", "Cycles skipped because of weekend/holiday");
        StrategyGenOosDrawdownDegraded       = _meter.CreateCounter<long>("trading.strategy_generation.oos_drawdown_degraded", "candidates", "Candidates rejected for OOS drawdown degradation");
        StrategyGenReserveSpreadSkipped      = _meter.CreateCounter<long>("trading.strategy_generation.reserve_spread_skipped", "candidates", "Reserve candidates skipped due to spread/ATR filter");
        StrategyGenScreeningRejections       = _meter.CreateCounter<long>("trading.strategy_generation.screening_rejections", "candidates", "Candidates rejected by screening gates (tag: gate)");
        StrategyGenCandleCacheEvictions       = _meter.CreateCounter<long>("trading.strategy_generation.candle_cache_evictions", "evictions", "LRU candle cache evictions during strategy generation");
        StrategyGenFeedbackAdaptiveContradictions = _meter.CreateCounter<long>("trading.strategy_generation.feedback_adaptive_contradictions", "contradictions", "Strategy types where feedback boosts but adaptive thresholds tighten (tag: strategy_type)");
        StrategyGenTypeFaultDisabled         = _meter.CreateCounter<long>("trading.strategy_generation.type_fault_disabled", "types", "Strategy types disabled mid-cycle due to repeated screening faults (tag: strategy_type)");
        StrategyGenScreeningDurationMs       = _meter.CreateHistogram<double>("trading.strategy_generation.screening_duration", "ms", "Per-candidate screening pipeline duration (tag: strategy_type)");

        // Optimization
        OptimizationRunsProcessed  = _meter.CreateCounter<long>("trading.optimization.runs_processed", "runs", "Optimization runs completed");
        OptimizationRunsFailed     = _meter.CreateCounter<long>("trading.optimization.runs_failed", "runs", "Optimization runs that failed with errors");
        OptimizationCandidatesScreened = _meter.CreateCounter<long>("trading.optimization.candidates_screened", "candidates", "Parameter candidates backtested during optimization");
        OptimizationAutoApproved   = _meter.CreateCounter<long>("trading.optimization.auto_approved", "runs", "Optimization runs auto-approved");
        OptimizationAutoRejected   = _meter.CreateCounter<long>("trading.optimization.auto_rejected", "runs", "Optimization runs sent to manual review");
        OptimizationCheckpointRestored = _meter.CreateCounter<long>("trading.optimization.checkpoint_restored", "runs", "Optimization runs resumed from checkpoint state");
        OptimizationCheckpointSaveFailures = _meter.CreateCounter<long>("trading.optimization.checkpoint_save_failures", "failures", "Checkpoint persistence failures during optimization");
        OptimizationLeaseReclaims = _meter.CreateCounter<long>("trading.optimization.lease_reclaims", "runs", "Stale running optimization runs reclaimed by lease recovery");
        OptimizationDuplicateFollowUpsPrevented = _meter.CreateCounter<long>("trading.optimization.duplicate_followups_prevented", "runs", "Duplicate validation follow-up creation attempts prevented");
        OptimizationRunsDeferred = _meter.CreateCounter<long>("trading.optimization.runs_deferred", "runs", "Optimization runs deferred back to queue (tagged by reason)");
        OptimizationFollowUpFailures = _meter.CreateCounter<long>("trading.optimization.followup_failures", "runs", "Validation follow-up backtests/walk-forwards that failed for an approved optimization");
        OptimizationSurrogateImprovement = _meter.CreateHistogram<double>("trading.optimization.surrogate_improvement", "score", "Per-batch best score improvement over previous best during surrogate search");
        OptimizationSurrogateBatchHits = _meter.CreateCounter<long>("trading.optimization.surrogate_batch_hits", "batches", "Surrogate-guided batches that improved the global best score");
        OptimizationSurrogateBatchMisses = _meter.CreateCounter<long>("trading.optimization.surrogate_batch_misses", "batches", "Surrogate-guided batches that did not improve the global best score");
        OptimizationEarlyStopSavings = _meter.CreateCounter<long>("trading.optimization.early_stop_savings", "evaluations", "Evaluations saved by early stopping in surrogate search");
        OptimizationParameterSpaceExhausted = _meter.CreateCounter<long>("trading.optimization.parameter_space_exhausted", "runs", "Optimization runs that found no fresh parameter candidates after grid expansion");
        OptimizationCircuitBreakerTrips = _meter.CreateCounter<long>("trading.optimization.circuit_breaker_trips", "trips", "Times the backtest circuit breaker tripped due to consecutive failures");
        OptimizationGateRejections = _meter.CreateCounter<long>("trading.optimization.gate_rejections", "rejections", "Validation gate rejections during optimization (tagged by gate name)");
        OptimizationGateDurationMs = _meter.CreateHistogram<double>("trading.optimization.gate_duration_ms", "ms", "Duration of individual validation gates (tagged by gate name)");
        OptimizationComputeSeconds = _meter.CreateHistogram<double>("trading.optimization.compute_seconds", "seconds", "Total backtest compute time per optimization run");
        OptimizationPhaseDurationMs = _meter.CreateHistogram<double>("trading.optimization.phase_duration_ms", "ms", "Duration of individual optimization phases (tagged by phase)");
        OptimizationCycleDurationMs = _meter.CreateHistogram<double>("trading.optimization.cycle_duration_ms", "ms", "Duration of a single optimization run");
        EhviHypervolumeProgress = _meter.CreateHistogram<double>("trading.optimization.ehvi_hypervolume", "volume", "Pareto front hypervolume after each EHVI batch (tracks multi-objective search progress)");
        EhviGpPredictionError = _meter.CreateHistogram<double>("trading.optimization.ehvi_gp_prediction_error", "error", "Per-objective GP mean absolute prediction error per batch");
        OptimizationSurrogateClamps = _meter.CreateCounter<long>("trading.optimization.surrogate_clamps", "clamps", "GP Cholesky diagonal clamps indicating ill-conditioned surrogate");
        OptimizationDeferredRechecks = _meter.CreateCounter<long>("trading.optimization.deferred_rechecks", "rechecks", "Times a deferred run was rechecked before its condition cleared");
        HyperbandBracketsExecuted = _meter.CreateCounter<long>("trading.optimization.hyperband_brackets_executed", "brackets", "Hyperband brackets that completed successfully");
        HyperbandBracketsSkipped = _meter.CreateCounter<long>("trading.optimization.hyperband_brackets_skipped", "brackets", "Hyperband brackets skipped (budget exhausted or failure)");
        HyperbandSurvivalRate = _meter.CreateHistogram<double>("trading.optimization.hyperband_survival_rate", "ratio", "Fraction of candidates surviving each Hyperband bracket (0-1)");

        // Candle Aggregator
        CandlesClosed      = _meter.CreateCounter<long>("trading.candles.closed", "candles", "Candle bars closed by aggregator");
        CandlesSynthetic   = _meter.CreateCounter<long>("trading.candles.synthetic", "candles", "Synthetic gap-fill candles emitted");
        CandleTicksDropped = _meter.CreateCounter<long>("trading.candles.ticks_dropped", "ticks", "Out-of-order ticks dropped by aggregator");
        CandleSlotsPurged  = _meter.CreateCounter<long>("trading.candles.slots_purged", "slots", "Stale candle slots purged");
        CandleTicksSpreadRejected = _meter.CreateCounter<long>("trading.candles.ticks_spread_rejected", "ticks", "Ticks rejected due to excessive spread");

        // News Sentiment
        NewsSentimentCallsTotal = _meter.CreateCounter<long>("trading.news_sentiment.calls_total", "calls", "Total DeepSeek API calls for news sentiment");
        NewsSentimentCallErrors = _meter.CreateCounter<long>("trading.news_sentiment.call_errors", "errors", "DeepSeek API call failures");
        NewsSentimentHeadlinesProcessed = _meter.CreateCounter<long>("trading.news_sentiment.headlines_processed", "headlines", "Headlines analyzed by DeepSeek");
        NewsSentimentEventsProcessed = _meter.CreateCounter<long>("trading.news_sentiment.events_processed", "events", "Economic events analyzed by DeepSeek");
        NewsSentimentCycleDurationMs = _meter.CreateHistogram<double>("trading.news_sentiment.cycle_duration_ms", "ms", "NewsSentimentWorker cycle duration");
        DeepSeekApiLatencyMs = _meter.CreateHistogram<double>("trading.news_sentiment.deepseek_latency_ms", "ms", "DeepSeek API call latency");
        DeepSeekTokensUsed = _meter.CreateCounter<long>("trading.news_sentiment.deepseek_tokens_used", "tokens", "Total tokens consumed by DeepSeek API");

        // Economic Calendar
        EconEventsIngested  = _meter.CreateCounter<long>("trading.econ_calendar.events_ingested", "events", "Economic events ingested from feed");
        EconActualsPatched  = _meter.CreateCounter<long>("trading.econ_calendar.actuals_patched", "events", "Economic events patched with actual values");
        EconFeedErrors      = _meter.CreateCounter<long>("trading.econ_calendar.feed_errors", "errors", "Economic calendar feed errors");
        EconCycleDurationMs = _meter.CreateHistogram<double>("trading.econ_calendar.cycle_duration", "ms", "Economic calendar worker full cycle duration");

        // Economic Calendar Feed
        EconFeedFetches       = _meter.CreateCounter<long>("trading.econ_calendar.feed_fetches", "fetches", "HTTP fetches to calendar feed");
        EconFeedCacheHits     = _meter.CreateCounter<long>("trading.econ_calendar.feed_cache_hits", "hits", "Calendar feed cache hits (HTML or parsed)");
        EconFeedParseFailures = _meter.CreateCounter<long>("trading.econ_calendar.feed_parse_failures", "failures", "Calendar pages fetched but parsed zero events");
        EconFeedFetchLatencyMs = _meter.CreateHistogram<double>("trading.econ_calendar.feed_fetch_latency", "ms", "Calendar feed HTTP fetch latency");

        // Tick Ingestion
        TicksIngested          = _meter.CreateCounter<long>("trading.ticks.ingested", "ticks", "Total ticks received from EA instances");
        TickBatchSize          = _meter.CreateHistogram<double>("trading.ticks.batch_size", "ticks", "Number of ticks per EA batch submission");
        TickIngestionLatencyMs = _meter.CreateHistogram<double>("trading.ticks.ingestion_latency", "ms", "Tick batch processing latency");

        // Cache
        PriceCacheMisses = _meter.CreateCounter<long>("trading.cache.price_misses", "misses", "Live price cache misses");

        // Simulated Broker
        SimBrokerFills         = _meter.CreateCounter<long>("trading.sim_broker.fills", "fills", "Simulated broker order fills");
        SimBrokerRejects       = _meter.CreateCounter<long>("trading.sim_broker.rejects", "rejects", "Simulated broker order rejections");
        SimBrokerRequotes      = _meter.CreateCounter<long>("trading.sim_broker.requotes", "requotes", "Simulated broker requotes issued");
        SimBrokerDisconnects   = _meter.CreateCounter<long>("trading.sim_broker.disconnects", "disconnects", "Simulated broker connection failures");
        SimBrokerStopOuts      = _meter.CreateCounter<long>("trading.sim_broker.stop_outs", "stop_outs", "Simulated broker stop-out liquidations");
        SimBrokerMarginCalls   = _meter.CreateCounter<long>("trading.sim_broker.margin_calls", "margin_calls", "Simulated broker margin call warnings");
        SimBrokerSlTpCloses    = _meter.CreateCounter<long>("trading.sim_broker.sl_tp_closes", "closes", "Simulated broker SL/TP position closes");
        SimBrokerPendingFills  = _meter.CreateCounter<long>("trading.sim_broker.pending_fills", "fills", "Simulated broker pending order triggers");
        SimBrokerExpiredOrders = _meter.CreateCounter<long>("trading.sim_broker.expired_orders", "orders", "Simulated broker expired pending orders");
        SimBrokerMarginLevel   = _meter.CreateHistogram<double>("trading.sim_broker.margin_level", "%", "Simulated broker margin level at evaluation");
        SimBrokerRealizedPnl   = _meter.CreateHistogram<double>("trading.sim_broker.realized_pnl", "USD", "Simulated broker realized P&L per close");
    }
}
