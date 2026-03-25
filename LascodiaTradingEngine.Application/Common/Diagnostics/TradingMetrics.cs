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

        // Candle Aggregator
        CandlesClosed      = _meter.CreateCounter<long>("trading.candles.closed", "candles", "Candle bars closed by aggregator");
        CandlesSynthetic   = _meter.CreateCounter<long>("trading.candles.synthetic", "candles", "Synthetic gap-fill candles emitted");
        CandleTicksDropped = _meter.CreateCounter<long>("trading.candles.ticks_dropped", "ticks", "Out-of-order ticks dropped by aggregator");
        CandleSlotsPurged  = _meter.CreateCounter<long>("trading.candles.slots_purged", "slots", "Stale candle slots purged");
        CandleTicksSpreadRejected = _meter.CreateCounter<long>("trading.candles.ticks_spread_rejected", "ticks", "Ticks rejected due to excessive spread");

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
