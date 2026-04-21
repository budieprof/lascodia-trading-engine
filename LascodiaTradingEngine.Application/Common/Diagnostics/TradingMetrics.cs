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
    public Counter<long>     TicksSkippedNoLivePrice { get; }
    public Counter<long>     StrategiesCircuitBroken { get; }
    public Histogram<double> StrategyEvaluationMs   { get; }
    public Histogram<double> SignalDedupLatencyMs   { get; }
    public Counter<long>     SignalDedupDuplicates  { get; }
    public Counter<long>     EvaluatorMissing       { get; }
    public Counter<long>     PrefetchQueryTimeouts  { get; }
    public Counter<long>     StrategyMetricsCacheHits { get; }
    public Counter<long>     StrategyMetricsCacheMisses { get; }
    public Counter<long>     StrategyMetricsCacheInvalidations { get; }
    public Counter<long>     SignalTtlExtendedMarketClosed { get; }
    public Counter<long>     MLModelStaleRejections { get; }
    public Counter<long>     MLScoringTimeouts      { get; }
    public Counter<long>     Tier2PriceDriftRejections { get; }
    public Counter<long>     SignalExpirySkewToleranceApplied { get; }
    public Counter<long>     MarketRegimeCacheHits  { get; }
    public Counter<long>     MarketRegimeCacheMisses { get; }
    public Counter<long>     EngineConfigCacheHits  { get; }
    public Counter<long>     EngineConfigCacheMisses { get; }
    public Counter<long>     KillSwitchTriggered    { get; }
    public Counter<long>     CircuitBreakerTransitions { get; }
    public Counter<long>     CircuitBreakerShortCircuits { get; }
    public Counter<long>     DbBulkheadWaits        { get; }
    public Histogram<double> DbBulkheadWaitMs       { get; }
    public Counter<long>     MLScoringBatchCalls    { get; }
    public Histogram<double> MLScoringBatchSize     { get; }
    public Counter<long>     ConflictResolutionEarlyExits { get; }
    public Counter<long>     EaReconciliationDrift  { get; }
    public Counter<long>     RetentionRowsDeleted   { get; }
    public Histogram<double> StrategyLockAcquisitionMs { get; }
    public Counter<long>     RegimeParamsCacheHits  { get; }
    public Counter<long>     RegimeParamsCacheMisses { get; }
    public Counter<long>     SignalRejectionsAudited { get; }
    public Counter<long>     CalibrationSnapshotsWritten { get; }

    // ── Validation / promotion defaults telemetry ────────────────────────────
    // Tagged counters that let operators calibrate the new default floors added in
    // WalkForwardWorker (MinInSampleDays, MinOutOfSampleDays, MinCandlesPerFold,
    // MinTradesPerFold) and StrategyPromotionWorker (MinLiveVsBacktestSharpeRatio).
    // If these fire a lot, the floors are probably too aggressive.
    public Counter<long>     WalkForwardFoldRejections     { get; }
    public Counter<long>     PromotionGateRejections       { get; }

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
    public Counter<long>     MLConformalBreakerModelsEvaluated { get; }
    public Counter<long>     MLConformalBreakerModelsSkipped { get; }
    public Counter<long>     MLConformalBreakerTrips { get; }
    public Counter<long>     MLConformalBreakerRecoveries { get; }
    public Counter<long>     MLConformalBreakerRefreshes { get; }
    public Counter<long>     MLConformalBreakerLockAttempts { get; }
    public Counter<long>     MLConformalBreakerDuplicateRepairs { get; }
    public Counter<long>     MLConformalBreakerAlertsDispatched { get; }
    public Counter<long>     MLConformalBreakerAlertDispatchFailures { get; }
    public Histogram<double> MLConformalBreakerThresholdMismatchRate { get; }
    public Histogram<double> MLConformalBreakerEmpiricalCoverage { get; }
    public Histogram<double> MLConformalBreakerActive { get; }
    public Counter<long>     MLCorrelatedFailureModelsEvaluated { get; }
    public Counter<long>     MLCorrelatedFailureModelsFailing { get; }
    public Counter<long>     MLCorrelatedFailureModelsSkipped { get; }
    public Counter<long>     MLCorrelatedFailurePauseActivations { get; }
    public Counter<long>     MLCorrelatedFailurePauseRecoveries { get; }
    public Counter<long>     MLCorrelatedFailureLockAttempts { get; }
    public Counter<long>     MLCorrelatedFailureCooldownSkips { get; }
    public Histogram<double> MLCorrelatedFailureRatio { get; }
    public Histogram<double> MLCorrelatedFailureAffectedSymbols { get; }
    public Counter<long>     MLErgodicityModelsEvaluated { get; }
    public Counter<long>     MLErgodicityModelsSkipped { get; }
    public Counter<long>     MLErgodicityLogsWritten { get; }
    public Counter<long>     MLErgodicityLockAttempts { get; }
    public Histogram<double> MLErgodicityGap { get; }
    public Histogram<double> MLErgodicityAdjustedKelly { get; }
    public Histogram<double> MLErgodicityGrowthVariance { get; }
    public Counter<long>     MLFeatureConsensusSnapshots { get; }
    public Counter<long>     MLFeatureConsensusPairsSkipped { get; }
    public Counter<long>     MLFeatureConsensusModelRejects { get; }
    public Counter<long>     MLFeatureConsensusLockAttempts { get; }
    public Histogram<double> MLFeatureConsensusContributors { get; }
    public Histogram<double> MLFeatureConsensusMeanKendallTau { get; }
    public Counter<long>     MLCpcCandidates { get; }
    public Counter<long>     MLCpcPromotions { get; }
    public Counter<long>     MLCpcRejections { get; }
    public Histogram<double> MLCpcTrainingDurationMs { get; }
    public Histogram<double> MLCpcSequences { get; }
    public Histogram<double> MLCpcCandles { get; }
    public Histogram<double> MLCpcValidationLoss { get; }

    // ── Workers ─────────────────────────────────────────────────────────────
    public Histogram<double> WorkerCycleDurationMs  { get; }
    public Counter<long>     WorkerErrors           { get; }

    // ── Integration Event Retry ─────────────────────────────────────────
    public Counter<long>     EventRetrySuccesses    { get; }
    public Counter<long>     EventRetryExhausted    { get; }
    public Counter<long>     EventRetryDeadLettered { get; }

    // ── Degradation & Dead-Letter ─────────────────────────────────────────
    public Counter<long>     DegradationTransitions     { get; }
    public Counter<long>     DeadLetterSinkDbFailures   { get; }
    public Counter<long>     DeadLetterSinkFileWrites   { get; }
    public Counter<long>     DeadLetterSinkBufferOverflows { get; }
    public Histogram<double> DeadLetterSinkLatencyMs    { get; }

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
    public Counter<long>     StrategyGenCompensationCleanupFailures { get; }
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
    public Counter<long>     OptimizationFollowUpDeferredChecks { get; }
    public Counter<long>     OptimizationFollowUpRepairs { get; }
    public Histogram<double> OptimizationFollowUpQueueAgeMs { get; }
    public Histogram<double> OptimizationApprovalToFollowUpCreationMs { get; }
    public Histogram<double> OptimizationCompletionPublicationLagMs { get; }
    public Histogram<double> OptimizationCompletionReplayWaitMs { get; }
    public Counter<long>     OptimizationCompletionReplayAttempts { get; }
    public Counter<long>     OptimizationCompletionReplayFailures { get; }
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
    public Histogram<double> OptimizationClaimLatencyMs { get; }
    public Histogram<double> OptimizationQueueWaitAtClaimMs { get; }
    public Histogram<double> OptimizationActiveProcessingSlots { get; }
    public Histogram<double> OptimizationProcessingSlotUtilization { get; }
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
        TicksSkippedNoLivePrice = _meter.CreateCounter<long>("trading.signals.ticks_skipped_no_live_price", "ticks", "Per-strategy evaluations skipped because live price cache had no entry for the symbol");
        StrategiesCircuitBroken = _meter.CreateCounter<long>("trading.signals.strategies_circuit_broken", "strategies", "Strategies temporarily disabled due to consecutive evaluation failures");
        StrategyEvaluationMs = _meter.CreateHistogram<double>("trading.strategy.evaluation_duration", "ms", "Strategy evaluation pipeline duration per tick");
        SignalDedupLatencyMs = _meter.CreateHistogram<double>("trading.signals.dedup_latency", "ms", "Tier 1 signal dedup latency (in-process + DB atomic mark). Tagged with outcome=accepted|duplicate.");
        SignalDedupDuplicates = _meter.CreateCounter<long>("trading.signals.dedup_duplicates", "signals", "Duplicate signal deliveries caught by Tier 1 dedup. Tagged with layer=in_process|cross_instance.");
        EvaluatorMissing = _meter.CreateCounter<long>("trading.signals.evaluator_missing", "events", "Tick-time dispatches where no IStrategyEvaluator is registered for the strategy's StrategyType. Tagged with strategy_type.");
        PrefetchQueryTimeouts = _meter.CreateCounter<long>("trading.signals.prefetch_query_timeouts", "events", "Per-tick pre-fetch DB queries that exceeded their timeout (fail-closed path). Tagged with query={backtest_qualification|strategy_metrics|regime}.");
        StrategyMetricsCacheHits = _meter.CreateCounter<long>("trading.strategy_metrics_cache.hits", "lookups", "In-memory strategy-metrics cache hits on the hot tick path.");
        StrategyMetricsCacheMisses = _meter.CreateCounter<long>("trading.strategy_metrics_cache.misses", "lookups", "In-memory strategy-metrics cache misses that triggered a DB refresh.");
        StrategyMetricsCacheInvalidations = _meter.CreateCounter<long>("trading.strategy_metrics_cache.invalidations", "events", "Strategy-metrics cache entries invalidated by integration events. Tagged with trigger={backtest_completed|strategy_activated}.");
        SignalTtlExtendedMarketClosed = _meter.CreateCounter<long>("trading.signals.ttl_extended_market_closed", "signals", "Signals whose ExpiresAt was extended to cover a market-closed window before the next open. Tagged with symbol.");
        MLModelStaleRejections = _meter.CreateCounter<long>("trading.signals.ml_model_stale_rejections", "signals", "Signals rejected because their associated MLModel is no longer live (retired, suppressed, or inactive). Tagged with ml_model_id|symbol.");
        MLScoringTimeouts = _meter.CreateCounter<long>("trading.ml.scoring_timeouts", "events", "IMLSignalScorer.ScoreAsync invocations that exceeded MLScoringTimeoutSeconds and were treated as a fail-closed ML error. Tagged with symbol|strategy_id.");
        Tier2PriceDriftRejections = _meter.CreateCounter<long>("trading.signals.tier2_price_drift_rejections", "signals", "Tier-2 rejections because |live_mid − signal.EntryPrice| / EntryPrice exceeded MaxEntryPriceDriftPct. Tagged with symbol.");
        SignalExpirySkewToleranceApplied = _meter.CreateCounter<long>("trading.signals.expiry_skew_tolerance_applied", "signals", "Signals that would have been rejected as expired absent ClockSkewToleranceSeconds but were preserved by the tolerance window. Tagged with stage={tier1|tier2|reentry}.");
        MarketRegimeCacheHits = _meter.CreateCounter<long>("trading.market_regime_cache.hits", "lookups", "Cross-tick market regime cache hits.");
        MarketRegimeCacheMisses = _meter.CreateCounter<long>("trading.market_regime_cache.misses", "lookups", "Cross-tick market regime cache misses that triggered a DB refresh.");
        EngineConfigCacheHits = _meter.CreateCounter<long>("trading.engine_config_cache.hits", "lookups", "EngineConfig cache hits on the hot tick path.");
        EngineConfigCacheMisses = _meter.CreateCounter<long>("trading.engine_config_cache.misses", "lookups", "EngineConfig cache misses that triggered a DB refresh.");
        KillSwitchTriggered = _meter.CreateCounter<long>("trading.kill_switch.triggered", "events", "Decisions short-circuited by an active kill switch. Tagged with scope={global|strategy} and site={strategy_worker|signal_bridge}.");
        CircuitBreakerTransitions = _meter.CreateCounter<long>("trading.circuit_breaker.transitions", "transitions", "External-service circuit breaker state transitions. Tagged with service and state.");
        CircuitBreakerShortCircuits = _meter.CreateCounter<long>("trading.circuit_breaker.short_circuits", "events", "Calls skipped because the circuit breaker was open. Tagged with service.");
        DbBulkheadWaits = _meter.CreateCounter<long>("trading.db_bulkhead.waits", "events", "Callers that had to wait for an IDbOperationBulkhead slot. Tagged with group.");
        DbBulkheadWaitMs = _meter.CreateHistogram<double>("trading.db_bulkhead.wait_ms", "ms", "Time spent waiting for a DB-bulkhead slot. Tagged with group.");
        MLScoringBatchCalls = _meter.CreateCounter<long>("trading.ml.scoring_batch_calls", "calls", "IMLSignalScorer.ScoreBatchAsync invocations by StrategyWorker. Tagged with batch_size={1..N}.");
        MLScoringBatchSize = _meter.CreateHistogram<double>("trading.ml.scoring_batch_size", "signals", "Number of signals per batched ML scoring call.");
        ConflictResolutionEarlyExits = _meter.CreateCounter<long>("trading.signals.conflict_resolution_early_exits", "candidates", "Candidate signals skipped by pre-score filtering before expensive per-strategy work ran.");
        EaReconciliationDrift = _meter.CreateCounter<long>("trading.ea.reconciliation_drift", "events", "Non-zero drift findings persisted by the ReconciliationMonitor. Tagged with kind={orphaned_engine|unknown_broker|mismatched}.");
        RetentionRowsDeleted = _meter.CreateCounter<long>("trading.retention.rows_deleted", "rows", "Rows deleted by AuditRetentionWorker. Tagged with table.");
        StrategyLockAcquisitionMs = _meter.CreateHistogram<double>("trading.strategy.lock_acquisition_ms", "ms", "Wall-clock time spent inside IDistributedLock.TryAcquireAsync for strategy evaluation. Tagged with outcome={acquired|busy}.");
        RegimeParamsCacheHits = _meter.CreateCounter<long>("trading.strategy.regime_params_cache.hits", "lookups", "StrategyRegimeParams cache hits on the hot tick path.");
        RegimeParamsCacheMisses = _meter.CreateCounter<long>("trading.strategy.regime_params_cache.misses", "lookups", "StrategyRegimeParams cache misses that triggered a DB refresh.");
        SignalRejectionsAudited = _meter.CreateCounter<long>("trading.signals.rejections_audited", "rejections", "Rejections written to the SignalRejectionAudit table. Tagged with stage and reason.");
        CalibrationSnapshotsWritten = _meter.CreateCounter<long>("trading.calibration.snapshots_written", "snapshots", "CalibrationSnapshot rows written by CalibrationSnapshotWorker. Tagged with period.");

        WalkForwardFoldRejections = _meter.CreateCounter<long>("trading.walk_forward.fold_rejections", "runs", "Walk-forward runs rejected. Tagged with reason=min_in_sample_days|min_out_of_sample_days|min_candles_per_fold|min_trades_per_fold.");
        PromotionGateRejections = _meter.CreateCounter<long>("trading.strategy_promotion.gate_rejections", "strategies", "Strategies rejected by promotion gates. Tagged with gate=live_vs_backtest_sharpe|critical_snapshot|insufficient_snapshots|insufficient_healthy.");

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
        MLConformalBreakerModelsEvaluated = _meter.CreateCounter<long>("trading.ml.conformal_breaker.models_evaluated", "models", "Active ML models evaluated by the conformal breaker");
        MLConformalBreakerModelsSkipped = _meter.CreateCounter<long>("trading.ml.conformal_breaker.models_skipped", "models", "ML models skipped by the conformal breaker, tagged by reason");
        MLConformalBreakerTrips = _meter.CreateCounter<long>("trading.ml.conformal_breaker.trips", "breakers", "Conformal breaker suppressions, tagged by trip reason");
        MLConformalBreakerRecoveries = _meter.CreateCounter<long>("trading.ml.conformal_breaker.recoveries", "breakers", "Conformal breaker recoveries that lifted active breaker state");
        MLConformalBreakerRefreshes = _meter.CreateCounter<long>("trading.ml.conformal_breaker.refreshes", "breakers", "Active conformal breaker diagnostic refreshes without extending suspension");
        MLConformalBreakerLockAttempts = _meter.CreateCounter<long>("trading.ml.conformal_breaker.lock_attempts", "cycles", "Conformal breaker distributed-lock attempts, tagged by outcome=acquired|busy|unavailable");
        MLConformalBreakerDuplicateRepairs = _meter.CreateCounter<long>("trading.ml.conformal_breaker.duplicate_repairs", "breakers", "Duplicate active conformal breaker rows deactivated by repair logic");
        MLConformalBreakerAlertsDispatched = _meter.CreateCounter<long>("trading.ml.conformal_breaker.alerts_dispatched", "alerts", "Conformal breaker alerts dispatched successfully");
        MLConformalBreakerAlertDispatchFailures = _meter.CreateCounter<long>("trading.ml.conformal_breaker.alert_dispatch_failures", "alerts", "Conformal breaker alert dispatch failures after state persistence");
        MLConformalBreakerThresholdMismatchRate = _meter.CreateHistogram<double>("trading.ml.conformal_breaker.threshold_mismatch_rate", "ratio", "Share of evaluated logs whose served conformal threshold differs from the current calibration threshold");
        MLConformalBreakerEmpiricalCoverage = _meter.CreateHistogram<double>("trading.ml.conformal_breaker.empirical_coverage", "ratio", "Empirical conformal coverage observed by the breaker");
        MLConformalBreakerActive = _meter.CreateHistogram<double>("trading.ml.conformal_breaker.active", "breakers", "Active conformal breaker count");
        MLCorrelatedFailureModelsEvaluated = _meter.CreateCounter<long>("trading.ml.correlated_failure.models_evaluated", "models", "Active ML models with enough outcomes evaluated for systemic correlated failure");
        MLCorrelatedFailureModelsFailing = _meter.CreateCounter<long>("trading.ml.correlated_failure.models_failing", "models", "Evaluated ML models below the configured drift accuracy threshold");
        MLCorrelatedFailureModelsSkipped = _meter.CreateCounter<long>("trading.ml.correlated_failure.models_skipped", "models", "Active ML models skipped by correlated failure evaluation, tagged by reason");
        MLCorrelatedFailurePauseActivations = _meter.CreateCounter<long>("trading.ml.correlated_failure.pause_activations", "pauses", "Systemic ML training pause activations");
        MLCorrelatedFailurePauseRecoveries = _meter.CreateCounter<long>("trading.ml.correlated_failure.pause_recoveries", "recoveries", "Systemic ML training pause recoveries");
        MLCorrelatedFailureLockAttempts = _meter.CreateCounter<long>("trading.ml.correlated_failure.lock_attempts", "cycles", "Correlated failure distributed-lock attempts, tagged by outcome=acquired|busy|unavailable");
        MLCorrelatedFailureCooldownSkips = _meter.CreateCounter<long>("trading.ml.correlated_failure.cooldown_skips", "cycles", "Correlated failure state changes skipped because the state-change cooldown is active");
        MLCorrelatedFailureRatio = _meter.CreateHistogram<double>("trading.ml.correlated_failure.failure_ratio", "ratio", "Share of evaluated ML models classified as failing");
        MLCorrelatedFailureAffectedSymbols = _meter.CreateHistogram<double>("trading.ml.correlated_failure.affected_symbols", "symbols", "Number of distinct symbols affected by failing ML models");
        MLErgodicityModelsEvaluated = _meter.CreateCounter<long>("trading.ml.ergodicity.models_evaluated", "models", "Active ML models with enough resolved outcomes evaluated for ergodicity economics");
        MLErgodicityModelsSkipped = _meter.CreateCounter<long>("trading.ml.ergodicity.models_skipped", "models", "Active ML models skipped by ergodicity evaluation, tagged by reason");
        MLErgodicityLogsWritten = _meter.CreateCounter<long>("trading.ml.ergodicity.logs_written", "logs", "MLErgodicityLog rows written");
        MLErgodicityLockAttempts = _meter.CreateCounter<long>("trading.ml.ergodicity.lock_attempts", "cycles", "Ergodicity distributed-lock attempts, tagged by outcome=acquired|busy|unavailable");
        MLErgodicityGap = _meter.CreateHistogram<double>("trading.ml.ergodicity.gap", "return", "Ergodicity gap between ensemble and time-average growth");
        MLErgodicityAdjustedKelly = _meter.CreateHistogram<double>("trading.ml.ergodicity.adjusted_kelly", "fraction", "Ergodicity-adjusted Kelly fraction");
        MLErgodicityGrowthVariance = _meter.CreateHistogram<double>("trading.ml.ergodicity.growth_variance", "variance", "Variance of per-outcome return proxies used by ergodicity metrics");
        MLFeatureConsensusSnapshots = _meter.CreateCounter<long>("trading.ml.feature_consensus.snapshots", "snapshots", "Feature consensus snapshots written. Tagged by symbol and timeframe.");
        MLFeatureConsensusPairsSkipped = _meter.CreateCounter<long>("trading.ml.feature_consensus.pairs_skipped", "pairs", "Feature consensus pairs skipped. Tagged by reason.");
        MLFeatureConsensusModelRejects = _meter.CreateCounter<long>("trading.ml.feature_consensus.model_rejects", "models", "Models excluded from feature consensus. Tagged by reason.");
        MLFeatureConsensusLockAttempts = _meter.CreateCounter<long>("trading.ml.feature_consensus.lock_attempts", "cycles", "Feature consensus distributed-lock attempts, tagged by outcome=acquired|busy|unavailable.");
        MLFeatureConsensusContributors = _meter.CreateHistogram<double>("trading.ml.feature_consensus.contributors", "models", "Contributing models per written feature consensus snapshot.");
        MLFeatureConsensusMeanKendallTau = _meter.CreateHistogram<double>("trading.ml.feature_consensus.mean_kendall_tau", "tau", "Mean pairwise Kendall tau-b rank agreement per feature consensus snapshot.");
        MLCpcCandidates = _meter.CreateCounter<long>("trading.ml.cpc.candidates", "pairs", "CPC encoder candidates considered for training. Tagged by symbol, timeframe, regime, encoder_type.");
        MLCpcPromotions = _meter.CreateCounter<long>("trading.ml.cpc.promotions", "encoders", "CPC encoders promoted. Tagged by symbol, timeframe, regime, encoder_type.");
        MLCpcRejections = _meter.CreateCounter<long>("trading.ml.cpc.rejections", "encoders", "CPC training attempts rejected. Tagged by reason, symbol, timeframe, regime, encoder_type.");
        MLCpcTrainingDurationMs = _meter.CreateHistogram<double>("trading.ml.cpc.training_duration_ms", "ms", "CPC pretraining duration per candidate. Tagged by symbol, timeframe, regime, encoder_type.");
        MLCpcSequences = _meter.CreateHistogram<double>("trading.ml.cpc.sequences", "sequences", "CPC sequence counts. Tagged by split=train|validation and symbol/timeframe/regime.");
        MLCpcCandles = _meter.CreateHistogram<double>("trading.ml.cpc.candles", "candles", "CPC candle counts. Tagged by stage=loaded|regime_filtered and symbol/timeframe/regime.");
        MLCpcValidationLoss = _meter.CreateHistogram<double>("trading.ml.cpc.validation_loss", "loss", "Holdout contrastive loss for fitted CPC encoders. Tagged by symbol, timeframe, regime, encoder_type.");

        // Workers
        WorkerCycleDurationMs = _meter.CreateHistogram<double>("trading.workers.cycle_duration", "ms", "Worker poll cycle duration");
        WorkerErrors          = _meter.CreateCounter<long>("trading.workers.errors", "errors", "Unhandled worker errors");

        // Integration Event Retry
        EventRetrySuccesses    = _meter.CreateCounter<long>("trading.events.retry_successes", "events", "Integration events successfully re-published by retry worker");
        EventRetryExhausted    = _meter.CreateCounter<long>("trading.events.retry_exhausted", "events", "Integration events that exhausted retry attempts");
        EventRetryDeadLettered = _meter.CreateCounter<long>("trading.events.retry_dead_lettered", "events", "Integration events dead-lettered after retry exhaustion");

        // Degradation & Dead-Letter
        DegradationTransitions       = _meter.CreateCounter<long>("trading.degradation.transitions", "transitions", "Engine degradation mode transitions");
        DeadLetterSinkDbFailures     = _meter.CreateCounter<long>("trading.dead_letter.db_failures", "failures", "Dead-letter database write failures");
        DeadLetterSinkFileWrites     = _meter.CreateCounter<long>("trading.dead_letter.file_writes", "writes", "Dead-letter file fallback writes");
        DeadLetterSinkBufferOverflows = _meter.CreateCounter<long>("trading.dead_letter.buffer_overflows", "overflows", "Dead-letter emergency buffer overflow drops");
        DeadLetterSinkLatencyMs      = _meter.CreateHistogram<double>("trading.dead_letter.latency_ms", "ms", "Dead-letter sink write latency");

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
        StrategyGenCompensationCleanupFailures = _meter.CreateCounter<long>("trading.strategy_generation.compensation_cleanup_failures", "failures", "Failures encountered while cleaning up partially persisted strategy-generation candidates");
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
        OptimizationFollowUpDeferredChecks = _meter.CreateCounter<long>("trading.optimization.followup_deferred_checks", "checks", "Follow-up monitor checks deferred because downstream validation is still in-flight");
        OptimizationFollowUpRepairs = _meter.CreateCounter<long>("trading.optimization.followup_repairs", "repairs", "Follow-up validation repairs triggered for approved optimizations");
        OptimizationFollowUpQueueAgeMs = _meter.CreateHistogram<double>("trading.optimization.followup_queue_age_ms", "ms", "Age of an approved optimization when the follow-up monitor evaluates it");
        OptimizationApprovalToFollowUpCreationMs = _meter.CreateHistogram<double>("trading.optimization.approval_to_followup_creation_ms", "ms", "Delay between optimization approval and validation follow-up creation");
        OptimizationCompletionPublicationLagMs = _meter.CreateHistogram<double>("trading.optimization.completion_publication_lag_ms", "ms", "Delay between terminal optimization state and successful completion publication");
        OptimizationCompletionReplayWaitMs = _meter.CreateHistogram<double>("trading.optimization.completion_replay_wait_ms", "ms", "How long replay callers waited for detached completion publication work");
        OptimizationCompletionReplayAttempts = _meter.CreateCounter<long>("trading.optimization.completion_replay_attempts", "attempts", "Completion publication replay attempts");
        OptimizationCompletionReplayFailures = _meter.CreateCounter<long>("trading.optimization.completion_replay_failures", "failures", "Completion publication replay failures");
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
        OptimizationClaimLatencyMs = _meter.CreateHistogram<double>("trading.optimization.claim_latency_ms", "ms", "Latency of the atomic queued-run claim operation");
        OptimizationQueueWaitAtClaimMs = _meter.CreateHistogram<double>("trading.optimization.queue_wait_at_claim_ms", "ms", "How long a run waited in the queue before being claimed");
        OptimizationActiveProcessingSlots = _meter.CreateHistogram<double>("trading.optimization.active_processing_slots", "slots", "Active optimization processing slots observed by the coordinator");
        OptimizationProcessingSlotUtilization = _meter.CreateHistogram<double>("trading.optimization.processing_slot_utilization", "ratio", "Observed ratio of active optimization slots to configured max concurrency");
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

        // Observable gauges — sampled on scrape, not pushed
        PriceCacheSymbolCount  = _meter.CreateObservableGauge("trading.cache.symbol_count", () => _priceCacheSymbolCountFunc?.Invoke() ?? 0, "symbols", "Number of symbols in live price cache");
        PriceCacheEvictions    = _meter.CreateCounter<long>("trading.cache.evictions", "evictions", "Price cache stale evictions");
        PriceFeedStaleAlert    = _meter.CreateCounter<long>("trading.cache.feed_stale_alert", "alerts", "Multi-symbol stale price feed alerts");
    }

    // Observable gauge callback registration
    private Func<int>? _priceCacheSymbolCountFunc;
    public void RegisterPriceCacheGauge(Func<int> symbolCountFunc) => _priceCacheSymbolCountFunc = symbolCountFunc;
    public ObservableGauge<int> PriceCacheSymbolCount { get; }
    public Counter<long> PriceCacheEvictions { get; }
    public Counter<long> PriceFeedStaleAlert { get; }
}
