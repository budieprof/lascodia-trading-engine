using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Determines how long a pending order remains active in the simulated broker.
/// </summary>
public enum SimulatedTimeInForce
{
    /// <summary>
    /// Good-Til-Cancelled: the order stays in the queue until explicitly cancelled, filled,
    /// or removed by stop-out. No automatic time-based expiry.
    /// </summary>
    GTC = 0,

    /// <summary>
    /// Good-Til-Date: the order expires at a specific UTC timestamp (<c>ExpiresAtUtc</c>).
    /// If no explicit expiry is provided, <see cref="SimulatedBrokerOptions.PendingOrderExpiryMinutes"/>
    /// is used relative to the order's creation time.
    /// </summary>
    GTD = 1,

    /// <summary>
    /// Day order: the order expires at the end of the current trading day, defined as the
    /// next occurrence of <see cref="SimulatedBrokerOptions.SwapRolloverHourUtc"/>.
    /// </summary>
    DAY = 2
}

/// <summary>
/// Determines how the simulated broker generates tick prices.
/// </summary>
public enum SimulatedTickSource
{
    /// <summary>
    /// Read prices from <see cref="LascodiaTradingEngine.Application.Common.Interfaces.ILivePriceCache"/>
    /// as populated by an external feed. Ticks are only emitted when prices change.
    /// </summary>
    Cache = 0,

    /// <summary>
    /// Generate synthetic prices using a random walk around configurable seed prices.
    /// Writes generated prices to <see cref="LascodiaTradingEngine.Application.Common.Interfaces.ILivePriceCache"/>
    /// so all consumers see consistent data.
    /// </summary>
    Synthetic = 1,

    /// <summary>
    /// Replay historical candle data from the database as ticks.
    /// Each candle is decomposed into Open → High → Low → Close ticks.
    /// </summary>
    Replay = 2
}

/// <summary>
/// Configuration for the simulated broker adapter used in paper trading and failover.
/// Bound from the <c>SimulatedBrokerOptions</c> section in appsettings.json.
/// </summary>
public class SimulatedBrokerOptions : ConfigurationOption<SimulatedBrokerOptions>
{
    /// <summary>
    /// Base adverse slippage added to market/stop fills, in pips. The actual slippage is
    /// scaled by <see cref="SlippageVolatilityScaling"/> and lot size when enabled.
    /// Defaults to 0.5.
    /// </summary>
    public decimal SlippagePips { get; set; } = 0.5m;

    /// <summary>
    /// When true, slippage scales with the current spread (as a proxy for volatility) and
    /// with order size. Larger orders and wider spreads produce more slippage. Defaults to false.
    /// </summary>
    public bool SlippageVolatilityScaling { get; set; } = false;

    /// <summary>
    /// When true, slippage is sampled from a log-normal distribution rather than applied
    /// as a fixed value. The base slippage (<see cref="SlippagePips"/>) becomes the median
    /// of the distribution, with occasional large slippage events matching real-world
    /// market microstructure. Defaults to false (fixed slippage).
    /// </summary>
    public bool SlippageLogNormal { get; set; } = false;

    /// <summary>
    /// Standard deviation (sigma) of the underlying normal distribution when
    /// <see cref="SlippageLogNormal"/> is enabled. Higher values produce a fatter tail
    /// (more frequent large slippage events). Defaults to 0.5.
    /// Typical values: 0.3 (tight) to 1.0 (very fat tail).
    /// </summary>
    public decimal SlippageLogNormalSigma { get; set; } = 0.5m;

    /// <summary>
    /// Minimum simulated broker processing latency in milliseconds. When
    /// <see cref="FillDelayMaxMs"/> is greater than this value, actual latency is
    /// randomly sampled from [FillDelayMs, FillDelayMaxMs]. Defaults to 50.
    /// </summary>
    public int FillDelayMs { get; set; } = 50;

    /// <summary>
    /// Maximum simulated broker processing latency in milliseconds. Set equal to
    /// <see cref="FillDelayMs"/> to disable jitter. Defaults to 50 (no jitter).
    /// </summary>
    public int FillDelayMaxMs { get; set; } = 50;

    /// <summary>Starting balance for the simulated account. Defaults to 100,000.</summary>
    public decimal SimulatedBalance { get; set; } = 100_000m;

    /// <summary>Leverage ratio (e.g. 30 = 30:1). Used to compute margin requirements. Defaults to 30.</summary>
    public int Leverage { get; set; } = 30;

    /// <summary>
    /// Probability (0.0–1.0) that a market order receives a partial fill instead of a full fill.
    /// The filled quantity will be a random fraction (50–90%) of the requested amount.
    /// Defaults to 0 (disabled).
    /// </summary>
    public decimal PartialFillProbability { get; set; } = 0m;

    /// <summary>
    /// Minimum fraction of the requested quantity that a partial fill will produce (0.0–1.0).
    /// Defaults to 0.5 (50%).
    /// </summary>
    public decimal PartialFillMinRatio { get; set; } = 0.5m;

    /// <summary>
    /// Maximum fraction of the requested quantity that a partial fill will produce (0.0–1.0).
    /// Defaults to 0.9 (90%).
    /// </summary>
    public decimal PartialFillMaxRatio { get; set; } = 0.9m;

    /// <summary>
    /// Margin level percentage at which a warning notification is fired via
    /// <see cref="SimulatedBrokerAdapter.OnMarginCallWarning"/>. Must be higher than
    /// <see cref="StopOutLevelPercent"/> to give the caller a chance to act before liquidation.
    /// Margin level = (Equity / MarginUsed) * 100. Defaults to 100 (100%).
    /// Set to 0 to disable margin call warnings.
    /// </summary>
    public decimal MarginCallWarningLevelPercent { get; set; } = 100m;

    /// <summary>
    /// Margin level percentage at which the broker force-closes the most losing position.
    /// Margin level = (Equity / MarginUsed) * 100. Defaults to 50 (50%).
    /// Set to 0 to disable stop-out.
    /// </summary>
    public decimal StopOutLevelPercent { get; set; } = 50m;

    /// <summary>
    /// Default maximum time in minutes a pending (limit/stop) order can stay in the queue
    /// before being automatically expired. Used as the fallback when a pending order does not
    /// specify its own <see cref="SimulatedTimeInForce"/>. Defaults to 1440 (24 hours).
    /// Set to 0 to disable time-based expiry (effectively GTC).
    /// </summary>
    public int PendingOrderExpiryMinutes { get; set; } = 1440;

    /// <summary>
    /// Default time-in-force policy for pending orders that do not specify one explicitly.
    /// <list type="bullet">
    ///   <item><b>GTC</b> — Good-Til-Cancelled: stays until filled, cancelled, or stop-out.</item>
    ///   <item><b>GTD</b> — Good-Til-Date: expires when the order's <c>ExpiresAtUtc</c> is reached.</item>
    ///   <item><b>DAY</b> — Day order: expires at the end of the current trading day (next rollover hour).</item>
    /// </list>
    /// Defaults to <see cref="SimulatedTimeInForce.GTD"/> with <see cref="PendingOrderExpiryMinutes"/>
    /// as the duration.
    /// </summary>
    public SimulatedTimeInForce DefaultTimeInForce { get; set; } = SimulatedTimeInForce.GTD;

    /// <summary>
    /// Interval in milliseconds between simulated tick emissions in <c>SubscribeAsync</c>.
    /// Each tick reads the current price from <see cref="ILivePriceCache"/> and invokes
    /// the <c>onTick</c> callback. Also triggers pending order / SL / TP / stop-out evaluation.
    /// Defaults to 500 (2 ticks per second).
    /// </summary>
    public int TickIntervalMs { get; set; } = 500;

    /// <summary>
    /// Determines how the simulated broker generates tick prices.
    /// Defaults to <see cref="SimulatedTickSource.Cache"/>.
    /// </summary>
    public SimulatedTickSource TickSource { get; set; } = SimulatedTickSource.Cache;

    // ── Synthetic mode settings ──────────────────────────────────────────────

    /// <summary>
    /// Seed prices for synthetic tick generation keyed by symbol.
    /// Example: <c>{ "EURUSD": 1.0850, "GBPUSD": 1.2650, "USDJPY": 154.50 }</c>.
    /// If a symbol is not listed, defaults to <see cref="SyntheticDefaultSeedPrice"/>.
    /// </summary>
    public Dictionary<string, decimal> SyntheticSeedPrices { get; set; } = new();

    /// <summary>
    /// Default seed price used when a symbol is not in <see cref="SyntheticSeedPrices"/>.
    /// Defaults to 1.0000.
    /// </summary>
    public decimal SyntheticDefaultSeedPrice { get; set; } = 1.0000m;

    /// <summary>
    /// Maximum per-tick price movement in pips for synthetic generation.
    /// Each tick moves the mid-price by a random amount in [-Volatility, +Volatility] pips.
    /// Defaults to 1.0.
    /// </summary>
    public decimal SyntheticVolatilityPips { get; set; } = 1.0m;

    /// <summary>
    /// Half-spread in pips applied to the synthetic mid-price to produce bid/ask.
    /// Defaults to 0.8 (bid = mid - 0.8 pips, ask = mid + 0.8 pips).
    /// </summary>
    public decimal SyntheticSpreadPips { get; set; } = 0.8m;

    // ── Replay mode settings ─────────────────────────────────────────────────

    /// <summary>
    /// Timeframe to replay candles from (e.g. "M1", "M5"). Parsed as <see cref="Domain.Enums.Timeframe"/>.
    /// Defaults to "M1".
    /// </summary>
    public string ReplayTimeframe { get; set; } = "M1";

    /// <summary>
    /// Start date (UTC) for the candle replay window. Defaults to 30 days ago.
    /// </summary>
    public DateTime? ReplayFrom { get; set; }

    /// <summary>
    /// End date (UTC) for the candle replay window. Defaults to now.
    /// </summary>
    public DateTime? ReplayTo { get; set; }

    /// <summary>
    /// Speed multiplier for replay mode. 1.0 = real-time based on <see cref="TickIntervalMs"/>,
    /// 2.0 = twice as fast, etc. Defaults to 1.0.
    /// </summary>
    public decimal ReplaySpeedMultiplier { get; set; } = 1.0m;

    // ── Position limits ───────────────────────────────────────────────────────

    /// <summary>
    /// Maximum number of open positions allowed at any time. New orders are rejected when
    /// this limit is reached. Defaults to 0 (unlimited).
    /// </summary>
    public int MaxOpenPositions { get; set; } = 0;

    /// <summary>
    /// Maximum number of open positions allowed per symbol. Prevents a single instrument
    /// from dominating the position book. Defaults to 0 (unlimited).
    /// </summary>
    public int MaxPositionsPerSymbol { get; set; } = 0;

    /// <summary>
    /// Maximum total notional exposure in account currency across all open positions.
    /// New orders are rejected if filling them would breach this limit.
    /// Defaults to 0 (unlimited).
    /// </summary>
    public decimal MaxNotionalExposure { get; set; } = 0m;

    // ── Position mode settings ────────────────────────────────────────────────

    /// <summary>
    /// When true, new fills net against existing positions on the same symbol
    /// (matching OANDA's default behaviour). An opposite-direction fill reduces or
    /// closes the existing position rather than opening a second one.
    /// When false (default), each fill creates an independent position (hedging mode).
    /// </summary>
    public bool NettingMode { get; set; } = false;

    // ── Trailing stop activation ──────────────────────────────────────────

    /// <summary>
    /// Minimum unrealised profit in pips before the trailing stop begins to trail.
    /// Until the position reaches this profit threshold, the trailing stop logic is
    /// inactive and the original SL (if any) remains in place. This prevents the
    /// trailing stop from tightening the SL before the position has moved into profit.
    /// Defaults to 0 (no activation distance — trailing starts immediately).
    /// </summary>
    public decimal TrailingStopActivationPips { get; set; } = 0m;

    // ── Pip unit overrides ──────────────────────────────────────────────────

    /// <summary>
    /// Per-symbol pip unit overrides for non-standard instruments (indices, metals, crypto).
    /// Example: <c>{ "XAUUSD": 0.01, "US30": 1.0, "BTCUSD": 0.01 }</c>.
    /// Symbols not listed fall back to the default forex logic (0.01 for JPY pairs, 0.0001 otherwise).
    /// </summary>
    public Dictionary<string, decimal> PipUnitOverrides { get; set; } = new();

    // ── Commission & swap settings ───────────────────────────────────────────

    /// <summary>
    /// Round-turn commission per standard lot in account currency.
    /// Deducted from balance on each fill. Used as the base rate when
    /// <see cref="CommissionTiers"/> is empty. Defaults to 7.00 (typical ECN commission).
    /// Set to 0 to disable.
    /// </summary>
    public decimal CommissionPerLot { get; set; } = 7.00m;

    /// <summary>
    /// Volume-based commission tiers. Each entry specifies a cumulative monthly volume threshold
    /// (in lots) and the commission rate per lot that applies once that threshold is reached.
    /// Tiers must be ordered by ascending <see cref="CommissionTier.MonthlyVolumeLots"/>.
    /// When empty, the flat <see cref="CommissionPerLot"/> rate is used.
    /// Example: <c>[{ "MonthlyVolumeLots": 0, "CommissionPerLot": 7.0 }, { "MonthlyVolumeLots": 100, "CommissionPerLot": 5.0 }]</c>.
    /// </summary>
    public List<CommissionTier> CommissionTiers { get; set; } = new();

    /// <summary>
    /// Per-symbol commission overrides. When a symbol is listed here, its commission rate
    /// is used instead of <see cref="CommissionPerLot"/> or <see cref="CommissionTiers"/>.
    /// Example: <c>{ "XAUUSD": 10.0, "BTCUSD": 15.0 }</c>.
    /// </summary>
    public Dictionary<string, decimal> CommissionPerSymbol { get; set; } = new();

    /// <summary>
    /// Daily swap (overnight) charge/credit per standard lot for long positions, in account currency.
    /// Applied by <see cref="SimulatedBrokerAdapter.EvaluateAsync"/> when a position crosses the rollover hour.
    /// Negative values represent a charge; positive values represent a credit. Defaults to -0.50.
    /// </summary>
    public decimal SwapLongPerLot { get; set; } = -0.50m;

    /// <summary>
    /// Daily swap (overnight) charge/credit per standard lot for short positions, in account currency.
    /// Defaults to -0.30.
    /// </summary>
    public decimal SwapShortPerLot { get; set; } = -0.30m;

    /// <summary>
    /// UTC hour at which the daily swap is applied (broker rollover time). Defaults to 21 (9 PM UTC).
    /// </summary>
    public int SwapRolloverHourUtc { get; set; } = 21;

    // ── Spread widening settings ─────────────────────────────────────────────

    /// <summary>
    /// Multiplier applied to the base spread during high-impact news windows.
    /// For example, 3.0 means the spread triples near news events. Defaults to 3.0.
    /// Set to 1.0 to disable news-based spread widening.
    /// </summary>
    public decimal NewsSpreadMultiplier { get; set; } = 3.0m;

    /// <summary>
    /// Minutes before and after a high-impact economic event during which the spread is widened.
    /// Defaults to 5.
    /// </summary>
    public int NewsSpreadWindowMinutes { get; set; } = 5;

    /// <summary>
    /// Multiplier applied to the base spread during low-liquidity sessions (outside the symbol's
    /// high-liquidity window). Defaults to 1.5. Set to 1.0 to disable.
    /// </summary>
    public decimal LowLiquiditySpreadMultiplier { get; set; } = 1.5m;

    /// <summary>
    /// Default high-liquidity window start hour (UTC). Used for symbols not listed in
    /// <see cref="HighLiquidityWindows"/>. Defaults to 13 (London/NY overlap start).
    /// </summary>
    public int DefaultHighLiquidityStartHourUtc { get; set; } = 13;

    /// <summary>
    /// Default high-liquidity window end hour (UTC). Used for symbols not listed in
    /// <see cref="HighLiquidityWindows"/>. Defaults to 17 (London/NY overlap end).
    /// </summary>
    public int DefaultHighLiquidityEndHourUtc { get; set; } = 17;

    /// <summary>
    /// Per-symbol high-liquidity windows as UTC hour ranges. Spreads widen outside these windows.
    /// Each entry maps a symbol to a list of [startHour, endHour] pairs, allowing multiple
    /// windows per symbol (e.g. Tokyo + London sessions for JPY pairs).
    /// Example: <c>{ "USDJPY": [[0, 9], [13, 17]], "EURUSD": [[7, 17]] }</c>.
    /// Symbols not listed fall back to <see cref="DefaultHighLiquidityStartHourUtc"/>/<see cref="DefaultHighLiquidityEndHourUtc"/>.
    /// </summary>
    public Dictionary<string, List<int[]>> HighLiquidityWindows { get; set; } = new();

    // ── State persistence settings ───────────────────────────────────────────

    /// <summary>
    /// When true, the simulated broker persists its state (balance, open positions, pending orders)
    /// to the file specified in <see cref="StateFilePath"/> and restores it on startup.
    /// Defaults to false.
    /// </summary>
    public bool PersistState { get; set; } = false;

    /// <summary>
    /// File path for state persistence. Defaults to "simulated_broker_state.json" in the working directory.
    /// </summary>
    public string StateFilePath { get; set; } = "simulated_broker_state.json";

    /// <summary>
    /// Interval in seconds between automatic state snapshots. Defaults to 60.
    /// </summary>
    public int StateSnapshotIntervalSeconds { get; set; } = 60;

    // ── Reject / requote simulation ──────────────────────────────────────────

    /// <summary>
    /// Probability (0.0–1.0) that a market order is rejected outright (simulates broker
    /// rejections or requotes during high volatility). Defaults to 0 (disabled).
    /// </summary>
    public decimal RejectProbability { get; set; } = 0m;

    /// <summary>
    /// Error message returned when a simulated reject occurs. Defaults to a generic
    /// "Order rejected by broker (simulated requote)." message.
    /// </summary>
    public string RejectMessage { get; set; } = "Order rejected by broker (simulated requote).";

    /// <summary>
    /// Probability (0.0–1.0) that a market order receives a requote instead of an immediate
    /// fill. A requote returns a new price that the caller can accept or decline via
    /// <see cref="ISimulatedBroker.AcceptRequoteAsync"/> / <see cref="ISimulatedBroker.DeclineRequote"/>.
    /// Evaluated after reject probability (so a reject takes priority). Defaults to 0 (disabled).
    /// </summary>
    public decimal RequoteProbability { get; set; } = 0m;

    /// <summary>
    /// Maximum adverse price deviation in pips for the requoted price relative to the current
    /// market price. The actual deviation is randomly sampled from [0, RequoteDeviationPips].
    /// Defaults to 2.0.
    /// </summary>
    public decimal RequoteDeviationPips { get; set; } = 2.0m;

    /// <summary>
    /// Time in milliseconds that a requote remains valid for acceptance. After this window,
    /// <see cref="ISimulatedBroker.AcceptRequoteAsync"/> will reject the requote as expired.
    /// Defaults to 3000 (3 seconds).
    /// </summary>
    public int RequoteExpiryMs { get; set; } = 3000;

    // ── Replay batching ──────────────────────────────────────────────────────

    /// <summary>
    /// Number of candles to load per batch in replay mode. Candles are streamed in
    /// batches of this size rather than loaded all at once to control memory usage
    /// for multi-year, multi-symbol replays. Defaults to 10000.
    /// </summary>
    public int ReplayBatchSize { get; set; } = 10_000;

    // ── Weekend / holiday simulation ─────────────────────────────────────────

    /// <summary>
    /// When true, the synthetic tick loop skips tick emission during weekends
    /// and holidays, and applies gap + spread widening on the first tick after
    /// a market-closed period. Defaults to true.
    /// </summary>
    public bool SkipWeekends { get; set; } = true;

    /// <summary>
    /// Multiplier applied to the base spread on the first tick after a weekend or holiday gap.
    /// Simulates the wider spreads seen at market open. Defaults to 5.0.
    /// Set to 1.0 to disable gap spread widening.
    /// </summary>
    public decimal WeekendGapSpreadMultiplier { get; set; } = 5.0m;

    /// <summary>
    /// UTC hour at which the forex market closes on Friday. Combined with
    /// <see cref="MarketOpenHourUtc"/> and <see cref="MarketOpenDay"/> to define the
    /// weekend close window. Defaults to 22 (Friday 22:00 UTC for most forex brokers).
    /// </summary>
    public int MarketCloseHourUtc { get; set; } = 22;

    /// <summary>
    /// Day of the week the market closes (start of the weekend window).
    /// Defaults to <see cref="DayOfWeek.Friday"/>.
    /// </summary>
    public DayOfWeek MarketCloseDay { get; set; } = DayOfWeek.Friday;

    /// <summary>
    /// UTC hour at which the forex market opens after the weekend. Defaults to 22
    /// (Sunday 22:00 UTC for most forex brokers).
    /// </summary>
    public int MarketOpenHourUtc { get; set; } = 22;

    /// <summary>
    /// Day of the week the market re-opens (end of the weekend window).
    /// Defaults to <see cref="DayOfWeek.Sunday"/>.
    /// </summary>
    public DayOfWeek MarketOpenDay { get; set; } = DayOfWeek.Sunday;

    /// <summary>
    /// Explicit holiday dates (UTC) on which the market is closed for the entire day.
    /// The tick loop skips these dates and applies gap spread widening on the first tick
    /// after the holiday, just like weekends.
    /// Example: <c>["2026-12-25", "2026-01-01"]</c>.
    /// Defaults to empty (no holidays).
    /// </summary>
    public List<DateTime> Holidays { get; set; } = new();

    // ── Market depth / liquidity simulation ───────────────────────────────

    /// <summary>
    /// Available liquidity depth in standard lots at the top of book. Orders whose size
    /// exceeds this depth incur additional market-impact slippage proportional to
    /// (orderSize / LiquidityDepthLots) ^ <see cref="LiquidityImpactExponent"/>.
    /// Defaults to 0 (disabled — no depth-based impact).
    /// </summary>
    public decimal LiquidityDepthLots { get; set; } = 0m;

    /// <summary>
    /// Scaling exponent for market-impact slippage: impact = base * (size / depth) ^ exponent.
    /// A value of 0.5 produces square-root impact (empirically realistic). Defaults to 0.5.
    /// </summary>
    public decimal LiquidityImpactExponent { get; set; } = 0.5m;

    /// <summary>
    /// When true, the liquidity depth model is stateful: each fill depletes the available
    /// liquidity pool for that symbol, and liquidity replenishes over time at a rate of
    /// <see cref="LiquidityReplenishRatePerSecond"/> lots per second. This produces realistic
    /// short-term market impact where rapid consecutive fills face increasing slippage.
    /// Requires <see cref="LiquidityDepthLots"/> > 0 to have any effect. Defaults to false.
    /// </summary>
    public bool StatefulLiquidity { get; set; } = false;

    /// <summary>
    /// Rate at which depleted liquidity replenishes, in lots per second.
    /// Only used when <see cref="StatefulLiquidity"/> is true.
    /// Defaults to 1.0 (1 lot replenished per second).
    /// </summary>
    public decimal LiquidityReplenishRatePerSecond { get; set; } = 1.0m;

    // ── Margin interest / funding cost ────────────────────────────────────

    /// <summary>
    /// Annual interest rate (as a decimal, e.g. 0.05 = 5%) charged on margin used.
    /// Applied once per day at <see cref="SwapRolloverHourUtc"/> alongside swap fees.
    /// Defaults to 0 (disabled).
    /// </summary>
    public decimal MarginInterestRateAnnual { get; set; } = 0m;

    /// <summary>
    /// Annual interest rate (as a decimal) credited on free margin / unused balance.
    /// Applied once per day at rollover. Defaults to 0 (disabled).
    /// </summary>
    public decimal BalanceInterestRateAnnual { get; set; } = 0m;

    // ── Per-symbol funding rates (CFDs / crypto) ──────────────────────────

    /// <summary>
    /// Per-symbol daily funding rate for long positions (as a decimal, e.g. -0.0001 = -0.01%/day).
    /// Applied at <see cref="SwapRolloverHourUtc"/> alongside swap fees. Funding is calculated
    /// as: notionalValue * fundingRate. Negative = charge, positive = credit.
    /// Overrides <see cref="SwapLongPerLot"/> for symbols that are listed here.
    /// Example: <c>{ "BTCUSD": -0.0003, "US500": -0.0001 }</c>. Defaults to empty.
    /// </summary>
    public Dictionary<string, decimal> FundingRateLong { get; set; } = new();

    /// <summary>
    /// Per-symbol daily funding rate for short positions. See <see cref="FundingRateLong"/>
    /// for details. Overrides <see cref="SwapShortPerLot"/> for listed symbols.
    /// </summary>
    public Dictionary<string, decimal> FundingRateShort { get; set; } = new();

    // ── Dividend adjustments ─────────────────────────────────────────────

    /// <summary>
    /// Dividend schedule: maps a symbol to a list of ex-dividend events. On each event date
    /// at <see cref="SwapRolloverHourUtc"/>, long positions receive a credit and short positions
    /// are debited by (dividendPerUnit * lots * 100,000). Defaults to empty.
    /// </summary>
    public Dictionary<string, List<DividendEvent>> DividendSchedule { get; set; } = new();

    // ── Cross-currency PnL conversion ────────────────────────────────────

    /// <summary>
    /// The account's denomination currency (e.g. "USD"). When set, PnL for instruments
    /// whose quote currency differs from this is converted using live rates from
    /// the price cache. Defaults to "USD".
    /// </summary>
    public string AccountCurrency { get; set; } = "USD";

    // ── Replay intra-candle randomness ───────────────────────────────────

    /// <summary>
    /// Number of random intermediate ticks inserted between each pair of OHLC anchor points
    /// when replaying candles. Higher values produce more realistic intra-candle price
    /// action but slow down replay. Defaults to 0 (disabled — only O/H/L/C emitted).
    /// </summary>
    public int ReplayIntermediateTicksPerSegment { get; set; } = 0;

    // ── News safety cache TTL ────────────────────────────────────────────

    /// <summary>
    /// Time-to-live in seconds for the news-safety cache used by spread widening.
    /// Prevents creating a DI scope on every tick. Defaults to 30 seconds.
    /// </summary>
    public int NewsSafetyCacheTtlSeconds { get; set; } = 30;

    // ── Connection state simulation ──────────────────────────────────────

    /// <summary>
    /// Probability (0.0–1.0) that any broker call experiences a simulated transient
    /// disconnect (throws an exception). Defaults to 0 (disabled).
    /// </summary>
    public decimal DisconnectProbability { get; set; } = 0m;

    /// <summary>
    /// Probability (0.0–1.0) that a SubmitOrder call returns an ambiguous result:
    /// success is unknown, the order may or may not have been filled. The caller must
    /// reconcile. Defaults to 0 (disabled).
    /// </summary>
    public decimal AmbiguousResultProbability { get; set; } = 0m;

    /// <summary>
    /// Maximum additional latency in milliseconds added when a simulated timeout occurs.
    /// Timeouts are triggered at the same probability as <see cref="DisconnectProbability"/>.
    /// Defaults to 5000.
    /// </summary>
    public int TimeoutDelayMs { get; set; } = 5000;

    /// <summary>
    /// Error message returned for simulated disconnects.
    /// </summary>
    public string DisconnectMessage { get; set; } = "Broker connection lost (simulated disconnect).";

    // ── Negative balance protection ──────────────────────────────────────

    /// <summary>
    /// When true, the account balance is floored at zero after any deduction (stop-out,
    /// SL fill, commission). Mirrors retail broker negative balance protection policies.
    /// Defaults to true.
    /// </summary>
    public bool NegativeBalanceProtection { get; set; } = true;

    // ── Trade history ─────────────────────────────────────────────────────

    /// <summary>
    /// Maximum number of completed fill records to retain in the in-memory trade history
    /// ring buffer. Older entries are evicted when the buffer is full. Enables reconciliation
    /// testing and audit queries via <see cref="SimulatedBrokerAdapter.GetTradeHistory"/>.
    /// Defaults to 1000. Set to 0 to disable trade history.
    /// </summary>
    public int TradeHistoryCapacity { get; set; } = 1000;

    // ── Deterministic randomness ─────────────────────────────────────────

    /// <summary>
    /// When set to a non-null value, all random decisions (slippage, partial fills,
    /// synthetic ticks, rejects, disconnects) use a seeded <see cref="Random"/> instance
    /// so that results are fully reproducible. When null (default), uses
    /// <see cref="Random.Shared"/> for non-deterministic behaviour.
    /// </summary>
    public int? RandomSeed { get; set; }

    // ── L2 order book simulation ─────────────────────────────────────────

    /// <summary>
    /// When true, enables a simulated Level-2 order book with configurable price levels.
    /// Large orders walk the book and receive a volume-weighted average fill price.
    /// Replaces the statistical <see cref="LiquidityDepthLots"/> model when enabled.
    /// Defaults to false.
    /// </summary>
    public bool OrderBookEnabled { get; set; } = false;

    /// <summary>
    /// Number of price levels on each side (bid/ask) of the simulated order book.
    /// Defaults to 5.
    /// </summary>
    public int OrderBookLevels { get; set; } = 5;

    /// <summary>
    /// Base liquidity (in lots) available at each price level. Actual per-level
    /// liquidity decreases linearly from this value at the best price to
    /// half this value at the deepest level. Defaults to 10.
    /// </summary>
    public decimal OrderBookBaseLiquidity { get; set; } = 10m;

    /// <summary>
    /// Price step (in pips) between consecutive order book levels. Defaults to 0.1.
    /// </summary>
    public decimal OrderBookLevelStepPips { get; set; } = 0.1m;

    /// <summary>
    /// Rate at which order book levels replenish after being consumed, in lots per second.
    /// Defaults to 5.0.
    /// </summary>
    public decimal OrderBookReplenishRatePerSecond { get; set; } = 5.0m;

    // ── Multi-account / sub-account support ──────────────────────────────

    /// <summary>
    /// When true, enables multi-account mode. Each account is identified by a string
    /// account ID. The default account is <c>"default"</c>. Accounts are created
    /// on first use with <see cref="SimulatedBalance"/> as the starting balance.
    /// Defaults to false (single-account mode using the root balance).
    /// </summary>
    public bool MultiAccountEnabled { get; set; } = false;

    /// <summary>
    /// Pre-configured sub-accounts with custom starting balances. Each entry maps
    /// an account ID to its initial balance. Accounts not listed here are created
    /// with <see cref="SimulatedBalance"/> on first use.
    /// Example: <c>{ "aggressive": 50000, "conservative": 200000 }</c>.
    /// </summary>
    public Dictionary<string, decimal> SubAccounts { get; set; } = new();
}

/// <summary>
/// A volume-based commission tier. When the trader's cumulative monthly volume (in lots)
/// reaches <see cref="MonthlyVolumeLots"/>, the <see cref="CommissionPerLot"/> rate applies
/// to subsequent fills.
/// </summary>
public class CommissionTier
{
    /// <summary>Cumulative monthly volume threshold in lots.</summary>
    public decimal MonthlyVolumeLots { get; set; }

    /// <summary>Commission per lot (account currency) once this tier is reached.</summary>
    public decimal CommissionPerLot { get; set; }
}

/// <summary>
/// An ex-dividend event for a symbol. On this date, positions are credited (long) or
/// debited (short) the dividend amount per unit.
/// </summary>
public class DividendEvent
{
    /// <summary>Ex-dividend date (UTC, date-only).</summary>
    public DateTime ExDate { get; set; }

    /// <summary>Dividend amount per unit of the instrument in quote currency.</summary>
    public decimal DividendPerUnit { get; set; }
}
