using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

/// <summary>
/// Fully functional simulated broker for paper trading and failover testing.
/// Tracks open positions, computes margin/equity in real time, supports limit/stop
/// orders via a pending-order queue, evaluates SL/TP on open positions, enforces
/// stop-out, and simulates configurable slippage, latency, partial fills,
/// commissions, swap fees, spread widening, market depth impact, margin interest,
/// cross-currency PnL conversion, and connection failures.
///
/// Use cases:
/// <list type="bullet">
///   <item><b>Paper trading mode</b> — realistic order flow without real money.</item>
///   <item><b>Failover target</b> — <see cref="BrokerFailoverService"/> can switch here
///         when the live broker (OANDA/FXCM) is unhealthy.</item>
///   <item><b>Backtesting</b> — provides deterministic fills for replay scenarios.</item>
/// </list>
/// </summary>
[RegisterKeyedService(typeof(IBrokerOrderExecutor), BrokerType.Paper, ServiceLifetime.Singleton)]
[RegisterKeyedService(typeof(IBrokerDataFeed), BrokerType.Paper, ServiceLifetime.Singleton)]
[RegisterKeyedService(typeof(ISimulatedBroker), BrokerType.Paper, ServiceLifetime.Singleton)]
public sealed class SimulatedBrokerAdapter : ISimulatedBroker, IDisposable
{
    private readonly ILivePriceCache _priceCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SimulatedBrokerAdapter> _logger;
    private readonly SimulatedBrokerOptions _options;
    private readonly TradingMetrics _metrics;

    private readonly decimal _slippagePips;
    private readonly int     _fillDelayMs;
    private readonly int     _fillDelayMaxMs;
    private readonly int     _leverage;
    private readonly decimal _partialFillProbability;
    private readonly decimal _partialFillMinRatio;
    private readonly decimal _partialFillMaxRatio;
    private readonly decimal _stopOutLevelPercent;
    private readonly int     _pendingOrderExpiryMinutes;
    private readonly int     _tickIntervalMs;
    private readonly decimal _commissionPerLot;
    private readonly decimal _swapLongPerLot;
    private readonly decimal _swapShortPerLot;
    private readonly int     _swapRolloverHourUtc;
    private readonly decimal _rejectProbability;

    // ── Thread-safe state (singleton) ────────────────────────────────────────

    /// <summary>
    /// Guards all balance mutations AND margin checks so they are atomic.
    /// Also used when reading balance in <see cref="GetAccountSummaryAsync"/>.
    /// </summary>
    private readonly Lock _stateLock = new();
    private decimal _balance;

    private readonly ConcurrentDictionary<string, SimulatedPosition> _openPositions = new();
    private readonly ConcurrentDictionary<string, PendingOrderSnapshot> _pendingOrders = new();

    /// <summary>
    /// Reverse index from symbol to broker order ID for O(1) netting lookups.
    /// Only maintained when <see cref="SimulatedBrokerOptions.NettingMode"/> is enabled.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _symbolToOrderId = new();

    /// <summary>
    /// Externally injected prices for the <see cref="SimulatedTickSource.Cache"/> mode.
    /// Callers can push prices via <see cref="InjectPrice"/> for controlled testing.
    /// </summary>
    private readonly ConcurrentDictionary<string, (decimal Bid, decimal Ask)> _injectedPrices = new();

    private long _orderIdCounter;

    /// <summary>
    /// Bounded ring buffer of completed fill records for reconciliation and audit.
    /// When the buffer reaches <see cref="SimulatedBrokerOptions.TradeHistoryCapacity"/>,
    /// the oldest entry is evicted. Thread-safe via <see cref="_tradeHistoryLock"/>.
    /// </summary>
    private readonly Queue<TradeHistoryEntry> _tradeHistory = new();
    private readonly Lock _tradeHistoryLock = new();

    /// <summary>
    /// Tracks the last UTC date on which swap was applied per position, so swap is
    /// charged exactly once per rollover crossing.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastSwapDate = new();

    /// <summary>
    /// Tracks the last time state was persisted to disk, to throttle writes.
    /// </summary>
    private DateTime _lastStatePersistUtc = DateTime.MinValue;

    /// <summary>
    /// Bounded set of recently processed fill IDs (broker order IDs) used to prevent duplicate
    /// fills if <see cref="EvaluateAsync"/> runs concurrently from multiple threads.
    /// Guarded by <see cref="_processedFillsLock"/>.
    /// </summary>
    private readonly Queue<string> _processedFillIds = new();
    private readonly HashSet<string> _processedFillSet = new();
    private readonly Lock _processedFillsLock = new();
    private const int ProcessedFillsCapacity = 2000;

    /// <summary>
    /// Per-symbol cache of the last news-safety check result, updated asynchronously
    /// in tick loops so <see cref="GetSpreadMultiplier"/> never blocks on an async call.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _newsUnsafeCache = new();

    /// <summary>
    /// UTC timestamp of the last news-safety cache refresh. Used with
    /// <see cref="SimulatedBrokerOptions.NewsSafetyCacheTtlSeconds"/> to avoid
    /// creating a DI scope on every tick.
    /// </summary>
    private DateTime _lastNewsCacheRefreshUtc = DateTime.MinValue;

    /// <summary>
    /// Simulated clock used during replay mode so all time-dependent logic (swap, sessions,
    /// tick timestamps) uses the candle's historical time instead of wall-clock time.
    /// Stored as ticks and accessed via <see cref="Interlocked"/> for thread safety.
    /// Defaults to <see cref="DateTime.UtcNow"/> in non-replay modes.
    /// </summary>
    private long _simulatedUtcNowTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// Tracks whether the previous tick was in a weekend window, so the first tick after
    /// a weekend crossing can widen the spread. Marked volatile so writes from one tick
    /// iteration are visible to subsequent iterations if the tick loop is ever invoked
    /// from a different thread context.
    /// </summary>
    private volatile bool _wasWeekend;

    /// <summary>Standard lot size for forex (100,000 units of base currency).</summary>
    private const decimal StandardLotSize = 100_000m;

    /// <summary>
    /// Pre-computed set of holiday dates (UTC, date-only) for O(1) lookup in
    /// <see cref="IsMarketClosed"/>. Built from <see cref="SimulatedBrokerOptions.Holidays"/>
    /// at construction time.
    /// </summary>
    private readonly HashSet<DateTime> _holidays;

    // ── Requote state ──────────────────────────────────────────────────────

    /// <summary>
    /// Pending requotes awaiting accept/decline. Keyed by requote ID.
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingRequote> _pendingRequotes = new();

    // ── Commission tracking ─────────────────────────────────────────────

    /// <summary>
    /// Cumulative monthly volume in lots, used for tiered commission lookups.
    /// Reset when the month changes.
    /// </summary>
    private decimal _cumulativeMonthlyVolume;
    private int _currentCommissionMonth;

    // ── Dividend tracking ───────────────────────────────────────────────

    /// <summary>
    /// Set of (positionKey, exDate) pairs that have already been processed to avoid
    /// double-applying dividends. Keyed per-position so multiple positions on the same
    /// symbol each receive their dividend adjustment.
    /// </summary>
    private readonly HashSet<(string PositionKey, DateTime ExDate)> _appliedDividends = new();

    // ── L2 order book state ─────────────────────────────────────────────

    /// <summary>
    /// Simulated L2 order book per symbol. Each book contains bid and ask levels
    /// with available liquidity that depletes on fills and replenishes over time.
    /// </summary>
    private readonly ConcurrentDictionary<string, SimulatedOrderBook> _orderBooks = new();

    // ── Multi-account state ─────────────────────────────────────────────

    /// <summary>
    /// Sub-account balances keyed by account ID. Only used when
    /// <see cref="SimulatedBrokerOptions.MultiAccountEnabled"/> is true.
    /// </summary>
    private readonly ConcurrentDictionary<string, SubAccountState> _subAccounts = new();

    /// <summary>The currently active account ID for multi-account mode.</summary>
    private string _activeAccountId = "default";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Optional callback invoked when a position is closed by SL, TP, or stop-out.
    /// Set via <see cref="OnPositionClosed"/> to receive fill notifications.
    /// Marked volatile so writes from <see cref="OnPositionClosed"/> are visible to tick-loop threads.
    /// </summary>
    private volatile Func<SimulatedFillNotification, Task>? _onPositionClosed;

    /// <summary>
    /// Optional callback invoked when the margin level drops below the warning threshold.
    /// </summary>
    private volatile Func<MarginCallWarning, Task>? _onMarginCallWarning;

    /// <summary>
    /// Tracks whether a margin call warning has already been fired in the current breach.
    /// Reset to false once the margin level recovers above the threshold. Marked volatile
    /// so reads from concurrent <see cref="EvaluateAsync"/> calls see the latest write.
    /// </summary>
    private volatile bool _marginCallWarningFired;

    /// <summary>
    /// Cached currency pair metadata keyed by symbol, used for cross-currency PnL conversion.
    /// Populated lazily via <see cref="PreloadCurrencyPairMetadataAsync"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, CurrencyPairMetadata> _currencyPairCache = new();

    /// <summary>
    /// Per-symbol available liquidity pool for stateful market depth simulation.
    /// Depleted by fills and replenished over time. Only used when
    /// <see cref="SimulatedBrokerOptions.StatefulLiquidity"/> is true.
    /// </summary>
    private readonly ConcurrentDictionary<string, LiquidityPool> _liquidityPools = new();

    /// <summary>
    /// Random instance used for all stochastic decisions. When <see cref="SimulatedBrokerOptions.RandomSeed"/>
    /// is set, this is a seeded instance for reproducible results; otherwise delegates to
    /// <see cref="Random.Shared"/>.
    /// </summary>
    private readonly Random _random;

    /// <summary>
    /// Guards <see cref="_random"/> when it is a seeded (non-thread-safe) instance.
    /// Null when using <see cref="Random.Shared"/> (which is already thread-safe).
    /// </summary>
    private readonly Lock? _randomLock;

    public SimulatedBrokerAdapter(
        ILivePriceCache priceCache,
        IServiceScopeFactory scopeFactory,
        ILogger<SimulatedBrokerAdapter> logger,
        SimulatedBrokerOptions options,
        TradingMetrics metrics)
    {
        _priceCache                = priceCache;
        _scopeFactory              = scopeFactory;
        _logger                    = logger;
        _options                   = options;
        _metrics                   = metrics;
        _slippagePips              = options.SlippagePips;
        _fillDelayMs               = options.FillDelayMs;
        _fillDelayMaxMs            = Math.Max(options.FillDelayMs, options.FillDelayMaxMs);
        _leverage                  = options.Leverage > 0 ? options.Leverage : 30;
        _balance                   = options.SimulatedBalance;
        _partialFillProbability    = options.PartialFillProbability;
        _partialFillMinRatio       = options.PartialFillMinRatio;
        _partialFillMaxRatio       = options.PartialFillMaxRatio;
        _stopOutLevelPercent       = options.StopOutLevelPercent;
        _pendingOrderExpiryMinutes = options.PendingOrderExpiryMinutes;
        _tickIntervalMs            = options.TickIntervalMs > 0 ? options.TickIntervalMs : 500;
        _commissionPerLot          = options.CommissionPerLot;
        _swapLongPerLot            = options.SwapLongPerLot;
        _swapShortPerLot           = options.SwapShortPerLot;
        _swapRolloverHourUtc       = options.SwapRolloverHourUtc;
        _rejectProbability         = options.RejectProbability;
        _random                    = options.RandomSeed.HasValue
                                       ? new Random(options.RandomSeed.Value)
                                       : Random.Shared;
        _randomLock                = options.RandomSeed.HasValue ? new Lock() : null;
        _holidays                  = new HashSet<DateTime>(
                                       options.Holidays.Select(d => d.Date));

        if (options.PersistState)
            RestoreState();
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_options.PersistState)
            PersistState();
    }

    // ── Fill notification registration ───────────────────────────────────────

    /// <summary>
    /// Registers a callback that is invoked whenever a position is closed automatically
    /// (SL, TP, stop-out, or pending order fill). This pushes fill events into the
    /// order/position pipeline so they are not silently lost.
    /// </summary>
    public void OnPositionClosed(Func<SimulatedFillNotification, Task> callback)
        => _onPositionClosed = callback;

    /// <inheritdoc />
    public void OnMarginCallWarning(Func<MarginCallWarning, Task> callback)
        => _onMarginCallWarning = callback;

    // ── Requote accept / decline ────────────────────────────────────────

    public async Task<BrokerOrderResult> AcceptRequoteAsync(string requoteId, CancellationToken cancellationToken = default)
    {
        SimulateConnectionFailure();
        await SimulateLatencyAsync(cancellationToken);

        if (!_pendingRequotes.TryRemove(requoteId, out var requote))
        {
            _logger.LogWarning("SimulatedBroker: requote {RequoteId} not found (already expired or declined)", requoteId);
            return Rejected($"Requote {requoteId} not found — it may have expired or been declined");
        }

        if (UtcNow > requote.ExpiresAtUtc)
        {
            _logger.LogInformation("SimulatedBroker: requote {RequoteId} expired at {Expiry}", requoteId, requote.ExpiresAtUtc);
            return Rejected($"Requote {requoteId} has expired");
        }

        // Fill at the requoted price — use it as both bid and ask since the price is agreed
        var order = requote.Order;
        bool isBuy = order.OrderType == OrderType.Buy;
        decimal bid = isBuy ? 0 : requote.RequotedPrice;
        decimal ask = isBuy ? requote.RequotedPrice : 0;

        _logger.LogInformation(
            "SimulatedBroker: requote {RequoteId} accepted — filling {OrderType} {Symbol} {Lots} lots @ {Price:F5}",
            requoteId, order.OrderType, order.Symbol, order.Quantity, requote.RequotedPrice);

        return ExecuteFill(order, bid, ask);
    }

    public void DeclineRequote(string requoteId)
    {
        if (_pendingRequotes.TryRemove(requoteId, out _))
        {
            _logger.LogInformation("SimulatedBroker: requote {RequoteId} declined", requoteId);
        }
    }

    // ── Multi-account operations ─────────────────────────────────────────

    /// <summary>
    /// Switches the active account for subsequent operations. Creates the account
    /// with the default balance if it doesn't exist.
    /// </summary>
    public void SetActiveAccount(string accountId)
    {
        if (!_options.MultiAccountEnabled)
            throw new InvalidOperationException("Multi-account mode is not enabled");

        _activeAccountId = accountId;
        GetOrCreateSubAccount(accountId);
        _logger.LogInformation("SimulatedBroker: active account switched to {AccountId}", accountId);
    }

    /// <summary>Returns the current active account ID.</summary>
    public string GetActiveAccountId() => _options.MultiAccountEnabled ? _activeAccountId : "default";

    /// <summary>Returns a snapshot of all sub-account balances.</summary>
    public IReadOnlyDictionary<string, decimal> GetSubAccountBalances()
    {
        if (!_options.MultiAccountEnabled)
            return new Dictionary<string, decimal> { ["default"] = _balance };

        return _subAccounts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Balance);
    }

    /// <summary>
    /// Returns a snapshot of the trade history ring buffer, ordered oldest to newest.
    /// Useful for reconciliation testing, audit queries, and performance analysis.
    /// </summary>
    public IReadOnlyList<TradeHistoryEntry> GetTradeHistory()
    {
        lock (_tradeHistoryLock)
        {
            return [.. _tradeHistory];
        }
    }

    // ── IBrokerOrderExecutor ────────────────────────────────────────────────

    public async Task<BrokerOrderResult> SubmitOrderAsync(Order order, CancellationToken cancellationToken)
    {
        SimulateConnectionFailure(); // may throw

        await SimulateLatencyAsync(cancellationToken);

        // Ambiguous result simulation — caller cannot tell if order was filled
        if (_options.AmbiguousResultProbability > 0 &&
            (decimal)NextRandomDouble() < _options.AmbiguousResultProbability)
        {
            _logger.LogWarning(
                "SimulatedBroker: ambiguous result for {OrderType} {Symbol} {Lots} lots (order {Id}) — " +
                "fill status unknown, caller must reconcile",
                order.OrderType, order.Symbol, order.Quantity, order.Id);
            return new BrokerOrderResult(
                Success: false,
                BrokerOrderId: null,
                FilledPrice: null,
                FilledQuantity: null,
                ErrorMessage: "Order result ambiguous: connection was interrupted mid-execution. The order may or may not have been filled.");
        }

        // Reject simulation
        if (_rejectProbability > 0 && (decimal)NextRandomDouble() < _rejectProbability)
        {
            _metrics.SimBrokerRejects.Add(1, new KeyValuePair<string, object?>("symbol", order.Symbol));
            _logger.LogInformation(
                "SimulatedBroker: simulated reject for {OrderType} {Symbol} {Lots} lots (order {Id})",
                order.OrderType, order.Symbol, order.Quantity, order.Id);
            return Rejected(_options.RejectMessage);
        }

        // Requote simulation — offer a new price instead of filling
        if (_options.RequoteProbability > 0 && (decimal)NextRandomDouble() < _options.RequoteProbability
            && order.ExecutionType == ExecutionType.Market)
        {
            var reqPriceData = _priceCache.Get(order.Symbol);
            if (reqPriceData != null)
            {
                decimal pipUnit = GetPipUnit(order.Symbol);
                decimal deviation = (decimal)NextRandomDouble() * _options.RequoteDeviationPips * pipUnit;
                bool isBuy = order.OrderType == OrderType.Buy;
                decimal requotedPrice = isBuy
                    ? reqPriceData.Value.Ask + deviation
                    : reqPriceData.Value.Bid - deviation;
                requotedPrice = Math.Max(0, requotedPrice);

                string requoteId = $"RQ-{Interlocked.Increment(ref _orderIdCounter):D10}";
                var pending = new PendingRequote(
                    SnapshotOrder(order), requotedPrice, UtcNow,
                    UtcNow.AddMilliseconds(_options.RequoteExpiryMs));
                _pendingRequotes.TryAdd(requoteId, pending);

                _metrics.SimBrokerRequotes.Add(1, new KeyValuePair<string, object?>("symbol", order.Symbol));
                _logger.LogInformation(
                    "SimulatedBroker: requote issued for {OrderType} {Symbol} {Lots} lots — " +
                    "requoteId={RequoteId}, requotedPrice={Price:F5}, expiresIn={ExpiryMs}ms",
                    order.OrderType, order.Symbol, order.Quantity, requoteId, requotedPrice, _options.RequoteExpiryMs);

                return new BrokerOrderResult(
                    Success: false, BrokerOrderId: null, FilledPrice: null,
                    FilledQuantity: null,
                    ErrorMessage: $"Price has moved. New price: {requotedPrice:F5}. Accept or decline the requote.",
                    RequoteId: requoteId, RequotedPrice: requotedPrice);
            }
        }

        var priceData = _priceCache.Get(order.Symbol);
        if (priceData == null)
        {
            _logger.LogWarning(
                "SimulatedBroker: no price available for {Symbol} — order {Id} rejected",
                order.Symbol, order.Id);
            return Rejected($"No live price available for {order.Symbol}");
        }

        // Limit / Stop / StopLimit — queue as pending if not immediately triggerable
        if (order.ExecutionType != ExecutionType.Market)
        {
            var snapshot = SnapshotOrder(order);
            if (!IsPendingOrderTriggered(snapshot, priceData.Value.Bid, priceData.Value.Ask))
            {
                return EnqueuePendingOrder(snapshot);
            }

            return ExecuteFill(snapshot, priceData.Value.Bid, priceData.Value.Ask);
        }

        return ExecuteFill(SnapshotOrder(order), priceData.Value.Bid, priceData.Value.Ask);
    }

    /// <summary>
    /// Submits a pending order with explicit time-in-force and optional OCO group.
    /// This is an extension beyond the <see cref="IBrokerOrderExecutor"/> interface
    /// for full pending order lifecycle control.
    /// </summary>
    /// <param name="order">The order to submit.</param>
    /// <param name="timeInForce">Time-in-force policy (GTC, GTD, or DAY).</param>
    /// <param name="expiresAtUtc">Explicit expiry for GTD orders. Ignored for GTC/DAY.</param>
    /// <param name="ocoGroupId">
    /// Optional OCO group identifier. When one order in the group fills, all others
    /// in the same group are automatically cancelled.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<BrokerOrderResult> SubmitPendingOrderAsync(
        Order order,
        SimulatedTimeInForce timeInForce,
        DateTime? expiresAtUtc = null,
        string? ocoGroupId = null,
        CancellationToken cancellationToken = default)
    {
        SimulateConnectionFailure();
        await SimulateLatencyAsync(cancellationToken);

        if (_rejectProbability > 0 && (decimal)NextRandomDouble() < _rejectProbability)
            return Rejected(_options.RejectMessage);

        var priceData = _priceCache.Get(order.Symbol);
        if (priceData == null)
            return Rejected($"No live price available for {order.Symbol}");

        var snapshot = SnapshotOrder(order);

        // If triggered immediately, fill (and cancel OCO siblings)
        if (order.ExecutionType != ExecutionType.Market &&
            IsPendingOrderTriggered(snapshot, priceData.Value.Bid, priceData.Value.Ask))
        {
            var result = ExecuteFill(snapshot, priceData.Value.Bid, priceData.Value.Ask);
            if (result.Success && ocoGroupId != null)
                CancelOcoGroup(ocoGroupId, result.BrokerOrderId);
            return result;
        }

        return EnqueuePendingOrder(snapshot, timeInForce, expiresAtUtc, ocoGroupId);
    }

    public async Task<BrokerOrderResult> CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken)
    {
        SimulateConnectionFailure();
        await SimulateLatencyAsync(cancellationToken);

        if (_pendingOrders.TryRemove(brokerOrderId, out _))
        {
            _logger.LogInformation("SimulatedBroker: cancelled pending order {BrokerOrderId}", brokerOrderId);
            return new BrokerOrderResult(true, brokerOrderId, null, null, null);
        }

        _logger.LogWarning(
            "SimulatedBroker: cancel failed — order {BrokerOrderId} not found in pending orders",
            brokerOrderId);
        return Rejected($"Order {brokerOrderId} not found — it may have already been filled, expired, or never existed");
    }

    public async Task<BrokerOrderResult> ModifyOrderAsync(
        string brokerOrderId, decimal? stopLoss, decimal? takeProfit, CancellationToken cancellationToken)
    {
        SimulateConnectionFailure();
        await SimulateLatencyAsync(cancellationToken);

        // Use lock to prevent race with concurrent Evaluate() reads
        lock (_stateLock)
        {
            if (_pendingOrders.TryGetValue(brokerOrderId, out var pending))
            {
                pending.StopLoss   = stopLoss;
                pending.TakeProfit = takeProfit;
                _logger.LogInformation(
                    "SimulatedBroker: modified pending order {BrokerOrderId} SL={SL} TP={TP}",
                    brokerOrderId, stopLoss, takeProfit);
            }
            else if (_openPositions.TryGetValue(brokerOrderId, out var position))
            {
                position.StopLoss   = stopLoss;
                position.TakeProfit = takeProfit;
                _logger.LogInformation(
                    "SimulatedBroker: modified position {BrokerOrderId} SL={SL} TP={TP}",
                    brokerOrderId, stopLoss, takeProfit);
            }
            else
            {
                _logger.LogWarning(
                    "SimulatedBroker: modify failed — order/position {BrokerOrderId} not found",
                    brokerOrderId);
                return Rejected($"Order or position {brokerOrderId} not found");
            }
        }

        return new BrokerOrderResult(true, brokerOrderId, null, null, null);
    }

    /// <summary>
    /// Modifies a pending order's trigger price and/or quantity in addition to SL/TP.
    /// This is an extension beyond the <see cref="IBrokerOrderExecutor"/> interface
    /// for full pending order management.
    /// </summary>
    public async Task<BrokerOrderResult> ModifyPendingOrderAsync(
        string brokerOrderId, decimal? triggerPrice, decimal? quantity,
        decimal? stopLoss, decimal? takeProfit, CancellationToken cancellationToken)
    {
        SimulateConnectionFailure();
        await SimulateLatencyAsync(cancellationToken);

        lock (_stateLock)
        {
            if (!_pendingOrders.TryGetValue(brokerOrderId, out var pending))
            {
                _logger.LogWarning(
                    "SimulatedBroker: modify pending failed — order {BrokerOrderId} not found",
                    brokerOrderId);
                return Rejected($"Pending order {brokerOrderId} not found");
            }

            if (triggerPrice.HasValue && triggerPrice.Value > 0)
            {
                pending.Order = pending.Order with { Price = triggerPrice.Value };
            }

            if (quantity.HasValue && quantity.Value > 0)
            {
                pending.Order = pending.Order with { Quantity = quantity.Value };
            }

            if (stopLoss.HasValue)
                pending.StopLoss = stopLoss;

            if (takeProfit.HasValue)
                pending.TakeProfit = takeProfit;

            _logger.LogInformation(
                "SimulatedBroker: modified pending order {BrokerOrderId} price={Price} qty={Qty} SL={SL} TP={TP}",
                brokerOrderId, pending.Order.Price, pending.Order.Quantity, pending.StopLoss, pending.TakeProfit);
        }

        return new BrokerOrderResult(true, brokerOrderId, null, null, null);
    }

    public async Task<BrokerOrderResult> ClosePositionAsync(
        string brokerPositionId, decimal lots, CancellationToken cancellationToken)
    {
        SimulateConnectionFailure();

        await SimulateLatencyAsync(cancellationToken);
        return ClosePositionInternal(brokerPositionId, lots);
    }

    public Task<BrokerAccountSummary?> GetAccountSummaryAsync(CancellationToken cancellationToken)
    {
        SimulateConnectionFailure();

        lock (_stateLock)
        {
            var (unrealisedPnl, marginUsed) = ComputeUnrealisedPnlAndMargin();
            decimal equity          = _balance + unrealisedPnl;
            decimal marginAvailable = Math.Max(0, equity - marginUsed);

            return Task.FromResult<BrokerAccountSummary?>(new BrokerAccountSummary(
                Balance:         _balance,
                Equity:          equity,
                MarginUsed:      Math.Round(marginUsed, 2),
                MarginAvailable: Math.Round(marginAvailable, 2)));
        }
    }

    public Task<BrokerOrderStatus?> GetOrderStatusAsync(
        string brokerOrderId, CancellationToken cancellationToken)
    {
        SimulateConnectionFailure();

        if (_openPositions.TryGetValue(brokerOrderId, out var position))
        {
            return Task.FromResult<BrokerOrderStatus?>(new BrokerOrderStatus(
                BrokerOrderId: brokerOrderId,
                Status: "Filled",
                FilledPrice: position.EntryPrice,
                FilledQuantity: position.Lots,
                LastUpdatedUtc: DateTime.UtcNow));
        }

        if (_pendingOrders.TryGetValue(brokerOrderId, out var pending))
        {
            return Task.FromResult<BrokerOrderStatus?>(new BrokerOrderStatus(
                BrokerOrderId: brokerOrderId,
                Status: "Pending",
                FilledPrice: null,
                FilledQuantity: null,
                LastUpdatedUtc: pending.CreatedAtUtc));
        }

        return Task.FromResult<BrokerOrderStatus?>(null);
    }

    // ── IBrokerDataFeed ─────────────────────────────────────────────────────

    public async Task SubscribeAsync(
        IEnumerable<string> symbols, Func<Tick, Task> onTick, CancellationToken cancellationToken)
    {
        var symbolList = symbols.ToList();
        _logger.LogInformation(
            "SimulatedBroker: starting tick loop for {Symbols} (source={Source}, interval={IntervalMs}ms)",
            string.Join(", ", symbolList), _options.TickSource, _tickIntervalMs);

        // Pre-load currency pair metadata for cross-currency conversion
        await PreloadCurrencyPairMetadataAsync(symbolList);

        switch (_options.TickSource)
        {
            case SimulatedTickSource.Synthetic:
                await RunSyntheticTickLoop(symbolList, onTick, cancellationToken);
                break;

            case SimulatedTickSource.Replay:
                await RunReplayTickLoop(symbolList, onTick, cancellationToken);
                break;

            case SimulatedTickSource.Cache:
            default:
                await RunCacheTickLoop(symbolList, onTick, cancellationToken);
                break;
        }

        _logger.LogInformation("SimulatedBroker: tick loop stopped");
    }

    /// <summary>
    /// Injects a price for a symbol. The injected price is written to <see cref="ILivePriceCache"/>
    /// so all consumers see it. Use this for controlled testing or external price feeds.
    /// </summary>
    public void InjectPrice(string symbol, decimal bid, decimal ask)
    {
        _injectedPrices[symbol] = (bid, ask);
        _priceCache.Update(symbol, bid, ask, UtcNow);
    }

    public async Task<IReadOnlyList<BrokerCandle>> GetCandlesAsync(
        string symbol, string timeframe, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<Timeframe>(timeframe, ignoreCase: true, out var tf))
        {
            _logger.LogWarning(
                "SimulatedBroker: GetCandlesAsync — unrecognised timeframe '{Timeframe}', returning empty",
                timeframe);
            return Array.Empty<BrokerCandle>();
        }

        using var scope = _scopeFactory.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db = readContext.GetDbContext();

        var candles = await db.Set<Candle>()
            .Where(c => c.Symbol == symbol
                      && c.Timeframe == tf
                      && c.Timestamp >= from
                      && c.Timestamp <= to
                      && !c.IsDeleted)
            .OrderBy(c => c.Timestamp)
            .Select(c => new BrokerCandle(
                c.Symbol,
                c.Timeframe.ToString(),
                c.Open,
                c.High,
                c.Low,
                c.Close,
                c.Volume,
                c.Timestamp,
                c.IsClosed))
            .ToListAsync(cancellationToken);

        _logger.LogDebug(
            "SimulatedBroker: GetCandlesAsync returned {Count} candles for {Symbol} {Timeframe} ({From} → {To})",
            candles.Count, symbol, timeframe, from, to);

        return candles;
    }

    // ── Tick source implementations ─────────────────────────────────────────

    /// <summary>
    /// Cache mode: reads from <see cref="ILivePriceCache"/> (populated externally or via
    /// <see cref="InjectPrice"/>). Only emits ticks when prices change.
    /// </summary>
    private async Task RunCacheTickLoop(
        List<string> symbols, Func<Tick, Task> onTick, CancellationToken ct)
    {
        var lastPrices = new Dictionary<string, (decimal Bid, decimal Ask)>();

        while (!ct.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _simulatedUtcNowTicks, DateTime.UtcNow.Ticks);
            await MaybeRefreshNewsSafetyCacheAsync(symbols);

            foreach (var symbol in symbols)
            {
                var priceData = _priceCache.Get(symbol);
                if (priceData == null) continue;

                var current = (priceData.Value.Bid, priceData.Value.Ask);

                if (lastPrices.TryGetValue(symbol, out var last) &&
                    last.Bid == current.Bid && last.Ask == current.Ask)
                    continue;

                lastPrices[symbol] = current;
                await EmitTick(symbol, current.Bid, current.Ask, onTick);
            }

            await EvaluateAsync();
            MaybePersistState();
            await DelayOrBreak(ct);
        }
    }

    /// <summary>
    /// Synthetic mode: generates a random-walk price series around configurable seed prices.
    /// Writes each tick to <see cref="ILivePriceCache"/> so all consumers see consistent data.
    /// </summary>
    private async Task RunSyntheticTickLoop(
        List<string> symbols, Func<Tick, Task> onTick, CancellationToken ct)
    {
        // Initialise mid-prices from seed config
        var midPrices = new Dictionary<string, decimal>();
        foreach (var symbol in symbols)
        {
            midPrices[symbol] = _options.SyntheticSeedPrices.TryGetValue(symbol, out var seed)
                ? seed
                : _options.SyntheticDefaultSeedPrice;
        }

        while (!ct.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _simulatedUtcNowTicks, DateTime.UtcNow.Ticks);

            // Weekend handling: skip tick emission during market-closed hours
            if (_options.SkipWeekends && IsMarketClosed(UtcNow))
            {
                _wasWeekend = true;
                await DelayOrBreak(ct);
                continue;
            }

            // Detect transition from weekend to weekday for gap spread widening
            bool isWeekendOpen = _wasWeekend;
            _wasWeekend = false;

            await MaybeRefreshNewsSafetyCacheAsync(symbols);

            foreach (var symbol in symbols)
            {
                decimal pipUnit    = GetPipUnit(symbol);
                decimal volatility = _options.SyntheticVolatilityPips * pipUnit;
                decimal baseHalfSpread = _options.SyntheticSpreadPips * pipUnit;
                decimal spreadMultiplier = GetSpreadMultiplier(symbol);

                // Apply weekend-open gap spread widening on the first tick after market opens
                if (isWeekendOpen && _options.WeekendGapSpreadMultiplier > 1.0m)
                    spreadMultiplier *= _options.WeekendGapSpreadMultiplier;

                decimal halfSpread = baseHalfSpread * spreadMultiplier;

                // Random walk: move mid-price by [-volatility, +volatility]
                decimal delta = ((decimal)NextRandomDouble() * 2 - 1) * volatility;
                decimal mid   = Math.Max(pipUnit, midPrices[symbol] + delta);
                midPrices[symbol] = mid;

                decimal bid = Math.Max(0, mid - halfSpread);
                decimal ask = mid + halfSpread;

                _priceCache.Update(symbol, bid, ask, UtcNow);
                await EmitTick(symbol, bid, ask, onTick);
            }

            await EvaluateAsync();
            MaybePersistState();
            await DelayOrBreak(ct);
        }
    }

    /// <summary>
    /// Replay mode: loads historical candles from the database and replays them as ticks.
    /// Each candle is decomposed into four anchor points with High/Low order determined by the
    /// candle direction: bullish candles use O→L→H→C, bearish candles use O→H→L→C.
    /// When <see cref="SimulatedBrokerOptions.ReplayIntermediateTicksPerSegment"/> > 0,
    /// random intermediate ticks are inserted between each pair of anchor points to produce
    /// more realistic intra-candle price action.
    /// Writes each tick to <see cref="ILivePriceCache"/>.
    ///
    /// <b>Price convention:</b> Candle OHLC values are treated as mid-prices. The spread is
    /// applied symmetrically (±halfSpread) to derive bid/ask. If your candle data is bid-based
    /// (common with some brokers), the reconstructed bid will be slightly lower than the true
    /// historical bid. Adjust <see cref="SimulatedBrokerOptions.SyntheticSpreadPips"/> or
    /// pre-process candles to mid-prices for best accuracy.
    /// </summary>
    private async Task RunReplayTickLoop(
        List<string> symbols, Func<Tick, Task> onTick, CancellationToken ct)
    {
        if (!Enum.TryParse<Timeframe>(_options.ReplayTimeframe, ignoreCase: true, out var timeframe))
            timeframe = Timeframe.M1;

        var from = _options.ReplayFrom ?? DateTime.UtcNow.AddDays(-30);
        var to   = _options.ReplayTo   ?? DateTime.UtcNow;
        int batchSize = _options.ReplayBatchSize > 0 ? _options.ReplayBatchSize : 10_000;
        int intermediateTicksPerSegment = Math.Max(0, _options.ReplayIntermediateTicksPerSegment);

        // Compute delay per sub-tick: each candle produces (4 + 3*intermediate) ticks
        int ticksPerCandle = 4 + 3 * intermediateTicksPerSegment;
        int delayPerSubTick = Math.Max(1, (int)(_tickIntervalMs / _options.ReplaySpeedMultiplier));

        // Stream candles in batches to control memory usage for multi-year replays
        var cursor = from;
        long totalReplayed = 0;
        bool firstBatch = true;

        while (!ct.IsCancellationRequested && cursor <= to)
        {
            List<Candle> batch;
            using (var scope = _scopeFactory.CreateScope())
            {
                var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var db = readContext.GetDbContext();

                batch = await db.Set<Candle>()
                    .Where(c => symbols.Contains(c.Symbol)
                             && c.Timeframe == timeframe
                             && c.Timestamp >= cursor
                             && c.Timestamp <= to
                             && c.IsClosed
                             && !c.IsDeleted)
                    .OrderBy(c => c.Timestamp)
                    .ThenBy(c => c.Symbol)
                    .Take(batchSize)
                    .ToListAsync(ct);
            }

            if (batch.Count == 0)
            {
                if (firstBatch)
                {
                    _logger.LogWarning(
                        "SimulatedBroker: no candles found for replay (symbols={Symbols}, tf={Tf}, from={From}, to={To})",
                        string.Join(",", symbols), timeframe, from, to);
                }
                break;
            }

            if (firstBatch)
            {
                _logger.LogInformation(
                    "SimulatedBroker: starting batched replay ({Tf}) from {From} to {To} (batchSize={BatchSize}, intermediateTicksPerSegment={IntTicks})",
                    timeframe, from, to, batchSize, intermediateTicksPerSegment);
                firstBatch = false;
            }

            // Group candles by timestamp so all symbols at the same time step are processed
            // together. This ensures cross-symbol SL/TP evaluation sees fresh prices for all
            // instruments, not stale prices from a prior timestamp.
            var candlesByTimestamp = batch
                .GroupBy(c => c.Timestamp)
                .OrderBy(g => g.Key);

            foreach (var group in candlesByTimestamp)
            {
                if (ct.IsCancellationRequested) break;

                // Advance the simulated clock to this candle group's timestamp
                Interlocked.Exchange(ref _simulatedUtcNowTicks, group.Key.Ticks);

                // Refresh news safety cache per time step so spread widening stays current
                await MaybeRefreshNewsSafetyCacheAsync(symbols);

                // Build lightweight metadata per candle — anchors and spread only.
                // Expanded tick prices are computed lazily per sub-tick index to avoid
                // materializing all intermediate ticks in memory at once (important for
                // wide symbol lists with high ReplayIntermediateTicksPerSegment).
                var candleInfos = new List<(Candle Candle, decimal[] Anchors, decimal HalfSpread)>();
                foreach (var candle in group)
                {
                    decimal halfSpread = _options.SyntheticSpreadPips * GetPipUnit(candle.Symbol)
                                       * GetSpreadMultiplier(candle.Symbol);
                    bool isBullish = candle.Close >= candle.Open;
                    decimal[] anchors = isBullish
                        ? [candle.Open, candle.Low, candle.High, candle.Close]
                        : [candle.Open, candle.High, candle.Low, candle.Close];

                    candleInfos.Add((candle, anchors, halfSpread));
                }

                // Interleave sub-ticks: emit tick i for all symbols, then tick i+1, etc.
                // Each candle produces (4 + 3 * intermediateTicksPerSegment) ticks.
                for (int i = 0; i < ticksPerCandle; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    foreach (var (candle, anchors, halfSpread) in candleInfos)
                    {
                        decimal mid = ComputeSubTickPrice(anchors, intermediateTicksPerSegment, i);
                        decimal bid = Math.Max(0, mid - halfSpread);
                        decimal ask = mid + halfSpread;

                        _priceCache.Update(candle.Symbol, bid, ask, UtcNow);
                        await EmitTick(candle.Symbol, bid, ask, onTick);
                    }

                    // Evaluate after all symbols have been updated for this sub-tick
                    await EvaluateAsync();

                    try { await Task.Delay(delayPerSubTick, ct); }
                    catch (OperationCanceledException) { return; }
                }
            }

            totalReplayed += batch.Count;

            // Advance cursor past the last candle in this batch to avoid re-reading it.
            // Add 1 tick to ensure we don't re-fetch the last timestamp.
            cursor = batch[^1].Timestamp.AddTicks(1);

            _logger.LogDebug(
                "SimulatedBroker: replayed batch of {Count} candles (total={Total}, cursor={Cursor})",
                batch.Count, totalReplayed, cursor);
        }

        _logger.LogInformation(
            "SimulatedBroker: candle replay complete — {Total} candles replayed", totalReplayed);
    }

    /// <summary>
    /// Computes the price for a single sub-tick at the given index without materializing
    /// the full expanded tick list. The sub-tick index maps into the anchor segments:
    /// each segment contains 1 anchor + N intermediate ticks, plus a final anchor at the end.
    /// For intermediate ticks, a biased random walk toward the segment endpoint is used,
    /// matching the behavior of <see cref="ExpandAnchorsWithIntermediateTicks"/>.
    /// </summary>
    private decimal ComputeSubTickPrice(decimal[] anchors, int intermediatePerSegment, int tickIndex)
    {
        if (intermediatePerSegment <= 0)
            return anchors[Math.Min(tickIndex, anchors.Length - 1)];

        int ticksPerSegment = 1 + intermediatePerSegment; // anchor + intermediates
        int totalTicks = (anchors.Length - 1) * ticksPerSegment + 1; // + final anchor

        if (tickIndex >= totalTicks)
            return anchors[^1];

        // Final anchor
        if (tickIndex == totalTicks - 1)
            return anchors[^1];

        int segment = tickIndex / ticksPerSegment;
        int posInSegment = tickIndex % ticksPerSegment;

        // Segment anchor point
        if (posInSegment == 0)
            return anchors[segment];

        // Intermediate tick within segment
        decimal start = anchors[segment];
        decimal end   = anchors[segment + 1];
        decimal min   = Math.Min(start, end);
        decimal max   = Math.Max(start, end);
        decimal range = max - min;

        decimal progress = (decimal)posInSegment / (intermediatePerSegment + 1);
        decimal target   = start + (end - start) * progress;
        decimal noise    = ((decimal)NextRandomDouble() * 2 - 1) * range * 0.3m;
        decimal value    = target + noise;

        return Math.Max(min, Math.Min(max, value));
    }

    // ── Tick emission helpers ────────────────────────────────────────────────

    private async Task EmitTick(string symbol, decimal bid, decimal ask, Func<Tick, Task> onTick)
    {
        var tick = new Tick(symbol, bid, ask, UtcNow);
        try
        {
            await onTick(tick);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SimulatedBroker: onTick callback failed for {Symbol}", symbol);
        }
    }

    /// <summary>
    /// Simulates broker processing latency with optional jitter. When <see cref="_fillDelayMaxMs"/>
    /// exceeds <see cref="_fillDelayMs"/>, the delay is randomly sampled from the range.
    /// </summary>
    private async Task SimulateLatencyAsync(CancellationToken ct)
    {
        if (_fillDelayMs <= 0) return;

        int delay = _fillDelayMaxMs > _fillDelayMs
            ? NextRandomInt(_fillDelayMs, _fillDelayMaxMs + 1)
            : _fillDelayMs;

        await Task.Delay(delay, ct);
    }

    private async Task DelayOrBreak(CancellationToken ct)
    {
        try { await Task.Delay(_tickIntervalMs, ct); }
        catch (OperationCanceledException) { /* loop will exit via ct check */ }
    }

    // ── Connection state simulation ─────────────────────────────────────────

    /// <summary>
    /// Simulates transient broker disconnects by throwing an exception with a configurable
    /// probability. Called at the start of every public broker operation.
    /// </summary>
    private void SimulateConnectionFailure()
    {
        if (_options.DisconnectProbability <= 0)
            return;

        if ((decimal)NextRandomDouble() < _options.DisconnectProbability)
        {
            _metrics.SimBrokerDisconnects.Add(1);
            _logger.LogWarning("SimulatedBroker: simulated connection failure");
            throw new BrokerConnectionException(_options.DisconnectMessage);
        }
    }

    // ── Spread widening ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current spread multiplier for a symbol, combining news-based and
    /// low-liquidity widening. The multipliers stack multiplicatively.
    /// News safety is read from <see cref="_newsUnsafeCache"/> (populated asynchronously
    /// by <see cref="MaybeRefreshNewsSafetyCacheAsync"/>) to avoid blocking the tick loop.
    /// </summary>
    private decimal GetSpreadMultiplier(string symbol)
    {
        decimal multiplier = 1.0m;

        // Low-liquidity widening: outside the symbol's high-liquidity window(s)
        if (_options.LowLiquiditySpreadMultiplier > 1.0m)
        {
            if (!IsInHighLiquidityWindow(symbol, UtcNow.Hour))
                multiplier *= _options.LowLiquiditySpreadMultiplier;
        }

        // News-based widening: read from async-populated cache
        if (_options.NewsSpreadMultiplier > 1.0m &&
            _newsUnsafeCache.TryGetValue(symbol, out bool isUnsafe) && isUnsafe)
        {
            multiplier *= _options.NewsSpreadMultiplier;
        }

        return multiplier;
    }

    /// <summary>
    /// Returns true if the given UTC hour falls within a high-liquidity window for the symbol.
    /// Checks per-symbol windows from <see cref="SimulatedBrokerOptions.HighLiquidityWindows"/>
    /// first, then falls back to the default window.
    /// </summary>
    private bool IsInHighLiquidityWindow(string symbol, int hour)
    {
        if (_options.HighLiquidityWindows.TryGetValue(symbol, out var windows) && windows.Count > 0)
        {
            foreach (var window in windows)
            {
                if (window.Length >= 2 && hour >= window[0] && hour < window[1])
                    return true;
            }
            return false;
        }

        return hour >= _options.DefaultHighLiquidityStartHourUtc
            && hour < _options.DefaultHighLiquidityEndHourUtc;
    }

    /// <summary>
    /// Refreshes the news-safety cache for the given symbols only if the TTL has elapsed.
    /// Prevents creating a DI scope on every tick iteration.
    /// </summary>
    private async Task MaybeRefreshNewsSafetyCacheAsync(List<string> symbols)
    {
        if (_options.NewsSpreadMultiplier <= 1.0m)
            return;

        var now = UtcNow;
        if ((now - _lastNewsCacheRefreshUtc).TotalSeconds < _options.NewsSafetyCacheTtlSeconds)
            return;

        _lastNewsCacheRefreshUtc = now;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var newsFilter = scope.ServiceProvider.GetService<INewsFilter>();
            if (newsFilter == null) return;

            foreach (var symbol in symbols)
            {
                bool isSafe = await newsFilter.IsSafeToTradeAsync(
                    symbol, now,
                    _options.NewsSpreadWindowMinutes,
                    _options.NewsSpreadWindowMinutes,
                    CancellationToken.None);

                _newsUnsafeCache[symbol] = !isSafe;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SimulatedBroker: news filter check failed for spread widening");
        }
    }

    // ── Periodic evaluation (call from a worker on each price tick) ─────────

    /// <summary>
    /// Evaluates all pending limit/stop orders against current prices, checks SL/TP
    /// on open positions, expires stale pending orders, enforces stop-out, applies
    /// overnight swap charges, and accrues margin interest. Call this periodically
    /// (e.g. from a worker or on each price update).
    /// </summary>
    public async Task<SimulatedBrokerEvaluation> EvaluateAsync()
    {
        var notifications   = new List<Task>();
        var filledOrders    = EvaluatePendingOrders(notifications);
        var expiredOrders   = ExpireStalePendingOrders();
        EvaluateTrailingStops();
        var slTpCloses      = EvaluateStopLossTakeProfit(notifications);
        var stopOutCloses   = EvaluateStopOut(notifications);
        EvaluateSwapAndInterest();
        EvaluateMarginCallWarning(notifications);

        if (notifications.Count > 0)
            await Task.WhenAll(notifications);

        // Emit evaluation-cycle metrics
        if (filledOrders.Count > 0)
            _metrics.SimBrokerPendingFills.Add(filledOrders.Count);
        if (expiredOrders.Count > 0)
            _metrics.SimBrokerExpiredOrders.Add(expiredOrders.Count);
        if (slTpCloses.Count > 0)
            _metrics.SimBrokerSlTpCloses.Add(slTpCloses.Count);
        if (stopOutCloses.Count > 0)
            _metrics.SimBrokerStopOuts.Add(stopOutCloses.Count);

        // Record margin level snapshot when positions are open
        lock (_stateLock)
        {
            var (unrealised, marginUsed) = ComputeUnrealisedPnlAndMargin();
            if (marginUsed > 0)
            {
                decimal equity = _balance + unrealised;
                double marginLevel = (double)((equity / marginUsed) * 100m);
                _metrics.SimBrokerMarginLevel.Record(marginLevel);
            }
        }

        return new SimulatedBrokerEvaluation(filledOrders, expiredOrders, slTpCloses, stopOutCloses);
    }

    // ── Private: pending order evaluation ───────────────────────────────────

    private IReadOnlyList<(string BrokerOrderId, BrokerOrderResult Result)> EvaluatePendingOrders(List<Task> notifications)
    {
        var filled = new List<(string, BrokerOrderResult)>();

        foreach (var kvp in _pendingOrders)
        {
            // Trigger check, removal, and fill all under the same lock so the price
            // used for the trigger decision is the same price used for the fill.
            lock (_stateLock)
            {
                // Idempotency guard: skip if this order was already processed by a
                // concurrent EvaluateAsync call that ran between our enumeration start
                // and this iteration.
                if (IsAlreadyProcessed(kvp.Key))
                    continue;

                var pending = kvp.Value;
                var priceData = _priceCache.Get(pending.Order.Symbol);
                if (priceData == null) continue;

                decimal bid = priceData.Value.Bid;
                decimal ask = priceData.Value.Ask;

                if (!IsPendingOrderTriggered(pending, bid, ask))
                    continue;

                if (!_pendingOrders.TryRemove(kvp.Key, out var removed))
                    continue;

                MarkAsProcessed(kvp.Key);

                var result = ExecuteFill(removed.Order, bid, ask, TradeHistoryReason.PendingOrderFill);
                filled.Add((kvp.Key, result));
                notifications.Add(NotifyFillAsync(kvp.Key, result, SimulatedFillReason.PendingOrderTriggered));

                // Cancel OCO siblings outside the lock (they have their own TryRemove)
                if (result.Success && removed.OcoGroupId != null)
                    CancelOcoGroup(removed.OcoGroupId, kvp.Key);
            }
        }

        return filled;
    }

    private IReadOnlyList<string> ExpireStalePendingOrders()
    {
        var expired = new List<string>();
        var now = UtcNow;

        foreach (var kvp in _pendingOrders)
        {
            var pending = kvp.Value;
            bool shouldExpire = pending.TimeInForce switch
            {
                // GTC never expires by time
                SimulatedTimeInForce.GTC => false,

                // GTD with explicit expiry
                SimulatedTimeInForce.GTD when pending.ExpiresAtUtc.HasValue
                    => now >= pending.ExpiresAtUtc.Value,

                // GTD without explicit expiry: fall back to PendingOrderExpiryMinutes
                SimulatedTimeInForce.GTD
                    => _pendingOrderExpiryMinutes > 0 &&
                       pending.CreatedAtUtc < now.AddMinutes(-_pendingOrderExpiryMinutes),

                // DAY: explicit expiry is always set by EnqueuePendingOrder
                SimulatedTimeInForce.DAY when pending.ExpiresAtUtc.HasValue
                    => now >= pending.ExpiresAtUtc.Value,

                SimulatedTimeInForce.DAY
                    => now >= ComputeEndOfDay(pending.CreatedAtUtc),

                // Unknown TIF: fall back to legacy behaviour
                _ => _pendingOrderExpiryMinutes > 0 &&
                     pending.CreatedAtUtc < now.AddMinutes(-_pendingOrderExpiryMinutes)
            };

            if (shouldExpire && _pendingOrders.TryRemove(kvp.Key, out _))
            {
                expired.Add(kvp.Key);
                _logger.LogInformation(
                    "SimulatedBroker: expired pending order {BrokerOrderId} (TIF={TIF}, expiry={Expiry})",
                    kvp.Key, pending.TimeInForce,
                    pending.ExpiresAtUtc?.ToString("u") ?? "default");
            }
        }

        return expired;
    }

    // ── Private: trailing stop evaluation ───────────────────────────────────

    /// <summary>
    /// Updates trailing stop levels on all open positions that have trailing stops enabled.
    /// Tracks the best price seen and moves the stop-loss to lock in profit as price advances.
    /// Must be called BEFORE <see cref="EvaluateStopLossTakeProfit"/> so the updated SL is
    /// evaluated in the same tick.
    /// </summary>
    private void EvaluateTrailingStops()
    {
        foreach (var kvp in _openPositions)
        {
            var pos = kvp.Value;
            if (!pos.TrailingStopEnabled || pos.TrailingStopValue is null or <= 0)
                continue;

            var priceData = _priceCache.Get(pos.Symbol);
            if (priceData == null) continue;

            // All BestPrice and StopLoss reads/writes under the same lock to prevent
            // torn values if EvaluateAsync is called concurrently.
            lock (_stateLock)
            {
                // Current favorable price: ask for shorts (want it to go down), bid for longs
                decimal currentPrice = pos.IsBuy ? priceData.Value.Bid : priceData.Value.Ask;

                // Update best price
                bool improved = pos.IsBuy
                    ? currentPrice > pos.BestPrice
                    : currentPrice < pos.BestPrice;

                if (improved)
                    pos.BestPrice = currentPrice;

                // Activation distance gate: only start trailing once the position has
                // moved into profit by at least TrailingStopActivationPips.
                if (_options.TrailingStopActivationPips > 0)
                {
                    decimal pipUnit = GetPipUnit(pos.Symbol);
                    decimal profitPips = pos.IsBuy
                        ? (currentPrice - pos.EntryPrice) / pipUnit
                        : (pos.EntryPrice - currentPrice) / pipUnit;

                    if (profitPips < _options.TrailingStopActivationPips)
                        continue;
                }

                // Compute the trailing stop distance
                decimal distance = pos.TrailingStopType switch
                {
                    TrailingStopType.FixedPips => pos.TrailingStopValue.Value * GetPipUnit(pos.Symbol),
                    TrailingStopType.Percentage => pos.BestPrice * (pos.TrailingStopValue.Value / 100m),
                    TrailingStopType.ATR => pos.TrailingStopValue.Value, // ATR value is already in price units
                    _ => 0m
                };

                if (distance <= 0) continue;

                // Compute new trailing stop level
                decimal newStopLevel = pos.IsBuy
                    ? pos.BestPrice - distance
                    : pos.BestPrice + distance;

                // Only move the stop in the favorable direction (never widen it)
                bool shouldUpdate = pos.StopLoss is null ||
                    (pos.IsBuy ? newStopLevel > pos.StopLoss.Value : newStopLevel < pos.StopLoss.Value);

                if (shouldUpdate)
                {
                    pos.StopLoss = newStopLevel;
                    _logger.LogDebug(
                        "SimulatedBroker: trailing stop updated {Symbol} {Direction} — best={Best:F5}, SL={SL:F5}",
                        pos.Symbol, pos.IsBuy ? "long" : "short", pos.BestPrice, newStopLevel);
                }
            }
        }
    }

    // ── Private: SL/TP evaluation on open positions ─────────────────────────

    private IReadOnlyList<(string BrokerOrderId, BrokerOrderResult Result)> EvaluateStopLossTakeProfit(List<Task> notifications)
    {
        var closed = new List<(string, BrokerOrderResult)>();

        foreach (var kvp in _openPositions)
        {
            var pos       = kvp.Value;
            var priceData = _priceCache.Get(pos.Symbol);
            if (priceData == null) continue;

            // Current exit price: bid for longs, ask for shorts
            decimal exitPrice = pos.IsBuy ? priceData.Value.Bid : priceData.Value.Ask;

            // Single lock: read SL/TP, check triggers, remove position, and update balance
            // atomically to prevent a race where ClosePositionAsync removes the position
            // between the SL/TP check and the TryRemove.
            lock (_stateLock)
            {
                // Idempotency guard: skip if already closed by a concurrent EvaluateAsync
                if (IsAlreadyProcessed(kvp.Key))
                    continue;

                decimal? stopLoss   = pos.StopLoss;
                decimal? takeProfit = pos.TakeProfit;

                bool slTriggered = stopLoss.HasValue &&
                    (pos.IsBuy ? exitPrice <= stopLoss.Value : exitPrice >= stopLoss.Value);

                bool tpTriggered = takeProfit.HasValue &&
                    (pos.IsBuy ? exitPrice >= takeProfit.Value : exitPrice <= takeProfit.Value);

                if (!slTriggered && !tpTriggered)
                    continue;

                if (!_openPositions.TryRemove(kvp.Key, out var removed))
                {
                    _logger.LogDebug(
                        "SimulatedBroker: SL/TP triggered for {BrokerOrderId} but position was already removed (concurrent close)",
                        kvp.Key);
                    continue;
                }

                MarkAsProcessed(kvp.Key);

                // SL is a stop order — fills at the worse of market vs SL + slippage.
                // TP is a limit order — fills at the TP price (price guaranteed).
                decimal fillPrice;
                string reason;
                SimulatedFillReason fillReason;
                if (slTriggered)
                {
                    decimal pipUnit  = GetPipUnit(removed.Symbol);
                    decimal slippage = ComputeSlippage(removed.Symbol, removed.Lots, priceData.Value.Bid, priceData.Value.Ask, pipUnit);
                    // In a gap, market (exitPrice) is worse than SL — fill at market + slippage
                    fillPrice = removed.IsBuy
                        ? Math.Min(exitPrice, removed.StopLoss!.Value) - slippage
                        : Math.Max(exitPrice, removed.StopLoss!.Value) + slippage;
                    fillPrice = Math.Max(0, fillPrice);
                    reason = "stop-loss";
                    fillReason = SimulatedFillReason.StopLoss;
                }
                else
                {
                    fillPrice = removed.TakeProfit!.Value;
                    reason = "take-profit";
                    fillReason = SimulatedFillReason.TakeProfit;
                }

                decimal pnl = ConvertPnlToAccountCurrency(
                    removed.Symbol,
                    CalculatePositionPnl(removed, fillPrice, removed.Lots));
                decimal commission = GetCommission(removed.Symbol, removed.Lots);
                _balance += pnl;
                _balance -= commission;
                ApplyNegativeBalanceProtection();
                _lastSwapDate.TryRemove(kvp.Key, out _);
                if (_options.NettingMode)
                    _symbolToOrderId.TryRemove(removed.Symbol, out _);

                _logger.LogInformation(
                    "SimulatedBroker: {Reason} triggered — closed {Symbol} {Lots} lots @ {Price:F5} (PnL={PnL:F2}, commission={Commission:F2})",
                    reason, removed.Symbol, removed.Lots, fillPrice, pnl, commission);

                RecordTradeHistory(kvp.Key, removed.Symbol, !removed.IsBuy, removed.Lots,
                    fillPrice, pnl, commission,
                    slTriggered ? TradeHistoryReason.StopLoss : TradeHistoryReason.TakeProfit);

                var result = new BrokerOrderResult(true, kvp.Key, fillPrice, removed.Lots, null);
                closed.Add((kvp.Key, result));
                notifications.Add(NotifyFillAsync(kvp.Key, result, fillReason));
            }
        }

        return closed;
    }

    // ── Private: stop-out enforcement ───────────────────────────────────────

    private IReadOnlyList<(string BrokerOrderId, BrokerOrderResult Result)> EvaluateStopOut(List<Task> notifications)
    {
        if (_stopOutLevelPercent <= 0)
            return [];

        var closed = new List<(string, BrokerOrderResult)>();

        // Keep closing the worst position until margin level is restored.
        // All state reads + mutations are under _stateLock to prevent false stop-outs
        // caused by seeing a position removed but balance not yet updated.
        while (true)
        {
            lock (_stateLock)
            {
                var (unrealised, marginUsed) = ComputeUnrealisedPnlAndMargin();
                if (marginUsed <= 0) break;

                decimal equity      = _balance + unrealised;
                decimal marginLevel = (equity / marginUsed) * 100m;
                if (marginLevel >= _stopOutLevelPercent) break;

                // Find the position with the worst unrealised PnL
                string? worstKey = null;
                decimal worstPnl = decimal.MaxValue;

                foreach (var kvp in _openPositions)
                {
                    var pos       = kvp.Value;
                    var priceData = _priceCache.Get(pos.Symbol);
                    if (priceData == null) continue;

                    decimal exitPrice = pos.IsBuy ? priceData.Value.Bid : priceData.Value.Ask;
                    decimal pnl       = ConvertPnlToAccountCurrency(
                        pos.Symbol,
                        CalculatePositionPnl(pos, exitPrice, pos.Lots));
                    if (pnl < worstPnl)
                    {
                        worstPnl = pnl;
                        worstKey = kvp.Key;
                    }
                }

                if (worstKey == null) break;

                if (!_openPositions.TryRemove(worstKey, out var removed))
                    break;

                var removedPrice = _priceCache.Get(removed.Symbol);
                if (removedPrice == null) break;

                decimal pipUnit  = GetPipUnit(removed.Symbol);
                decimal slippage = ComputeSlippage(removed.Symbol, removed.Lots, removedPrice.Value.Bid, removedPrice.Value.Ask, pipUnit);
                decimal closePrice = Math.Max(0, removed.IsBuy
                    ? removedPrice.Value.Bid - slippage
                    : removedPrice.Value.Ask + slippage);

                decimal closePnl = ConvertPnlToAccountCurrency(
                    removed.Symbol,
                    CalculatePositionPnl(removed, closePrice, removed.Lots));
                decimal closeCommission = GetCommission(removed.Symbol, removed.Lots);
                _balance += closePnl;
                _balance -= closeCommission;
                ApplyNegativeBalanceProtection();
                _lastSwapDate.TryRemove(worstKey, out _);
                if (_options.NettingMode)
                    _symbolToOrderId.TryRemove(removed.Symbol, out _);

                _logger.LogWarning(
                    "SimulatedBroker: STOP-OUT — force-closed {Symbol} {Lots} lots @ {Price:F5} " +
                    "(PnL={PnL:F2}, commission={Commission:F2}, marginLevel was {Level:F1}%)",
                    removed.Symbol, removed.Lots, closePrice, closePnl, closeCommission, marginLevel);

                RecordTradeHistory(worstKey, removed.Symbol, !removed.IsBuy, removed.Lots,
                    closePrice, closePnl, closeCommission, TradeHistoryReason.StopOut);

                var result = new BrokerOrderResult(true, worstKey, closePrice, removed.Lots, null);
                closed.Add((worstKey, result));
                notifications.Add(NotifyFillAsync(worstKey, result, SimulatedFillReason.StopOut));
            }
        }

        return closed;
    }

    // ── Private: margin call warning ──────────────────────────────────────────

    /// <summary>
    /// Checks the current margin level against <see cref="SimulatedBrokerOptions.MarginCallWarningLevelPercent"/>.
    /// Fires <see cref="_onMarginCallWarning"/> once when the level is breached, and resets
    /// when the margin level recovers above the threshold.
    /// </summary>
    private void EvaluateMarginCallWarning(List<Task> notifications)
    {
        if (_options.MarginCallWarningLevelPercent <= 0 || _onMarginCallWarning == null)
            return;

        lock (_stateLock)
        {
            var (unrealised, marginUsed) = ComputeUnrealisedPnlAndMargin();
            if (marginUsed <= 0)
            {
                _marginCallWarningFired = false;
                return;
            }

            decimal equity      = _balance + unrealised;
            decimal marginLevel = (equity / marginUsed) * 100m;

            if (marginLevel < _options.MarginCallWarningLevelPercent)
            {
                if (!_marginCallWarningFired)
                {
                    _marginCallWarningFired = true;
                    _metrics.SimBrokerMarginCalls.Add(1);

                    _logger.LogWarning(
                        "SimulatedBroker: MARGIN CALL WARNING — margin level {Level:F1}% is below threshold {Threshold:F1}% " +
                        "(equity={Equity:F2}, marginUsed={MarginUsed:F2}, balance={Balance:F2})",
                        marginLevel, _options.MarginCallWarningLevelPercent, equity, marginUsed, _balance);

                    var warning = new MarginCallWarning(
                        Math.Round(marginLevel, 2), Math.Round(equity, 2),
                        Math.Round(marginUsed, 2), Math.Round(_balance, 2), UtcNow);

                    var callback = _onMarginCallWarning;
                    if (callback != null)
                    {
                        notifications.Add(Task.Run(async () =>
                        {
                            try { await callback(warning); }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "SimulatedBroker: margin call warning callback failed");
                            }
                        }));
                    }
                }
            }
            else
            {
                // Margin level recovered — reset so the warning can fire again on the next breach
                if (_marginCallWarningFired)
                {
                    _marginCallWarningFired = false;
                    _logger.LogInformation(
                        "SimulatedBroker: margin level recovered to {Level:F1}% (above threshold {Threshold:F1}%)",
                        marginLevel, _options.MarginCallWarningLevelPercent);
                }
            }
        }
    }

    // ── Private: swap (overnight) fee + margin interest evaluation ───────────

    private void EvaluateSwapAndInterest()
    {
        var now = UtcNow;
        if (now.Hour != _swapRolloverHourUtc)
            return;

        bool hasSwap     = _swapLongPerLot != 0 || _swapShortPerLot != 0;
        bool hasInterest = _options.MarginInterestRateAnnual != 0 || _options.BalanceInterestRateAnnual != 0;
        bool hasFunding  = _options.FundingRateLong.Count > 0 || _options.FundingRateShort.Count > 0;
        bool hasDividend = _options.DividendSchedule.Count > 0;
        if (!hasSwap && !hasInterest && !hasFunding && !hasDividend)
            return;

        var today = now.Date;

        lock (_stateLock)
        {
            // ── Swap fees on open positions ──────────────────────────────────
            if (hasSwap)
            {
                foreach (var kvp in _openPositions)
                {
                    var pos = kvp.Value;

                    // Skip symbols that use per-symbol funding rates (they are handled separately)
                    var fundingDict = pos.IsBuy ? _options.FundingRateLong : _options.FundingRateShort;
                    if (fundingDict.ContainsKey(pos.Symbol))
                        continue;

                    // Only apply swap once per day per position
                    if (_lastSwapDate.TryGetValue(kvp.Key, out var lastDate) && lastDate == today)
                        continue;

                    decimal swapPerLot = pos.IsBuy ? _swapLongPerLot : _swapShortPerLot;
                    decimal swapCharge = swapPerLot * pos.Lots;

                    // Triple swap on Wednesday (covers Saturday + Sunday)
                    if (now.DayOfWeek == DayOfWeek.Wednesday)
                        swapCharge *= 3;

                    _balance += swapCharge;
                    ApplyNegativeBalanceProtection();
                    _lastSwapDate[kvp.Key] = today;

                    _logger.LogDebug(
                        "SimulatedBroker: swap applied {Symbol} {Direction} {Lots} lots = {Swap:F2} (balance={Balance:F2})",
                        pos.Symbol, pos.IsBuy ? "long" : "short", pos.Lots, swapCharge, _balance);
                }
            }

            // ── Per-symbol funding rates & dividend adjustments ────────────
            if (hasFunding || hasDividend)
            {
                EvaluateFundingAndDividends(today);
                ApplyNegativeBalanceProtection();
            }

            // ── Margin interest / balance interest ───────────────────────────
            if (hasInterest)
            {
                // Check if interest was already applied today (use a sentinel key)
                const string interestKey = "__interest__";
                if (_lastSwapDate.TryGetValue(interestKey, out var lastInterestDate) && lastInterestDate == today)
                    return;

                _lastSwapDate[interestKey] = today;

                var (unrealisedPnl, marginUsed) = ComputeUnrealisedPnlAndMargin();
                decimal equity = _balance + unrealisedPnl;

                // Margin interest: charged on margin used (financing cost for leverage)
                if (_options.MarginInterestRateAnnual != 0 && marginUsed > 0)
                {
                    decimal dailyRate = _options.MarginInterestRateAnnual / 365m;
                    decimal charge = -marginUsed * dailyRate; // negative = cost
                    // Triple charge on Wednesday for weekend coverage
                    if (now.DayOfWeek == DayOfWeek.Wednesday)
                        charge *= 3;

                    _balance += charge;
                    ApplyNegativeBalanceProtection();
                    _logger.LogDebug(
                        "SimulatedBroker: margin interest applied {Charge:F2} (marginUsed={MarginUsed:F2}, rate={Rate:P2}, balance={Balance:F2})",
                        charge, marginUsed, _options.MarginInterestRateAnnual, _balance);
                }

                // Balance interest: credited on free margin / excess equity
                if (_options.BalanceInterestRateAnnual != 0)
                {
                    decimal freeMargin = Math.Max(0, equity - marginUsed);
                    if (freeMargin > 0)
                    {
                        decimal dailyRate = _options.BalanceInterestRateAnnual / 365m;
                        decimal credit = freeMargin * dailyRate;
                        if (now.DayOfWeek == DayOfWeek.Wednesday)
                            credit *= 3;

                        _balance += credit;
                        _logger.LogDebug(
                            "SimulatedBroker: balance interest applied {Credit:F2} (freeMargin={FreeMargin:F2}, rate={Rate:P2}, balance={Balance:F2})",
                            credit, freeMargin, _options.BalanceInterestRateAnnual, _balance);
                    }
                }
            }
        }
    }

    // ── Private: fill execution ─────────────────────────────────────────────

    private BrokerOrderResult ExecuteFill(OrderSnapshot order, decimal bid, decimal ask, TradeHistoryReason historyReason = TradeHistoryReason.MarketFill)
    {
        // L2 order book: walk the book for a VWAP fill (replaces statistical slippage)
        if (_options.OrderBookEnabled && order.ExecutionType == ExecutionType.Market)
        {
            bool isBuy = order.OrderType == OrderType.Buy;
            decimal fillPrice = ComputeOrderBookFillPrice(order.Symbol, order.Quantity, isBuy, bid, ask);

            // Still apply partial fill simulation, margin checks, etc. via the normal path
            // by substituting the VWAP as the effective bid/ask
            decimal effectiveBid = isBuy ? 0 : fillPrice;
            decimal effectiveAsk = isBuy ? fillPrice : 0;
            return ExecuteFillCore(order, effectiveBid, effectiveAsk, 0m, historyReason);
        }

        decimal pipUnit  = GetPipUnit(order.Symbol);
        decimal slippage = ComputeSlippage(order.Symbol, order.Quantity, bid, ask, pipUnit);

        return ExecuteFillCore(order, bid, ask, slippage, historyReason);
    }

    private BrokerOrderResult ExecuteFillCore(OrderSnapshot order, decimal bid, decimal ask, decimal slippage, TradeHistoryReason historyReason)
    {
        decimal fillPrice = ComputeFillPrice(order, bid, ask, slippage);

        // StopLimit rejection: market has gapped past the limit price
        if (fillPrice < 0)
        {
            _logger.LogInformation(
                "SimulatedBroker: StopLimit {OrderType} {Symbol} {Lots} lots rejected — market gapped past limit price {Price:F5}",
                order.OrderType, order.Symbol, order.Quantity, order.Price);
            return Rejected(
                $"StopLimit order rejected: market gapped past limit price {order.Price:F5}");
        }

        // Determine fill quantity (partial fill simulation)
        decimal fillQuantity = order.Quantity;
        decimal remainderQuantity = 0m;
        if (_partialFillProbability > 0)
        {
            decimal roll = (decimal)NextRandomDouble();
            if (roll < _partialFillProbability)
            {
                decimal ratio = _partialFillMinRatio +
                    (decimal)NextRandomDouble() * (_partialFillMaxRatio - _partialFillMinRatio);
                fillQuantity = Math.Round(order.Quantity * ratio, 2);
                if (fillQuantity <= 0) fillQuantity = order.Quantity;
                else remainderQuantity = order.Quantity - fillQuantity;
            }
        }

        // Atomic margin check + position/exposure limits + balance reservation
        decimal notional       = fillQuantity * StandardLotSize * fillPrice;
        decimal marginRequired = notional / _leverage;

        lock (_stateLock)
        {
            // Enforce max open positions limit
            if (_options.MaxOpenPositions > 0 && _openPositions.Count >= _options.MaxOpenPositions)
            {
                _logger.LogWarning(
                    "SimulatedBroker: max open positions ({Max}) reached — order {Id} {Symbol} rejected",
                    _options.MaxOpenPositions, order.OrderId, order.Symbol);
                return Rejected(
                    $"Max open positions limit reached ({_options.MaxOpenPositions})");
            }

            // Enforce per-symbol position limit
            if (_options.MaxPositionsPerSymbol > 0)
            {
                int symbolCount = _openPositions.Count(kvp => kvp.Value.Symbol == order.Symbol);
                if (symbolCount >= _options.MaxPositionsPerSymbol)
                {
                    _logger.LogWarning(
                        "SimulatedBroker: max positions per symbol ({Max}) reached for {Symbol} — order {Id} rejected",
                        _options.MaxPositionsPerSymbol, order.Symbol, order.OrderId);
                    return Rejected(
                        $"Max positions per symbol limit reached for {order.Symbol} ({_options.MaxPositionsPerSymbol})");
                }
            }

            // Enforce max notional exposure limit
            if (_options.MaxNotionalExposure > 0)
            {
                decimal currentExposure = ComputeTotalNotionalExposure();
                if (currentExposure + notional > _options.MaxNotionalExposure)
                {
                    _logger.LogWarning(
                        "SimulatedBroker: max notional exposure ({Max:F2}) would be breached " +
                        "(current={Current:F2}, order={Order:F2}) — order {Id} {Symbol} rejected",
                        _options.MaxNotionalExposure, currentExposure, notional, order.OrderId, order.Symbol);
                    return Rejected(
                        $"Max notional exposure would be breached: current {currentExposure:F2} + order {notional:F2} > limit {_options.MaxNotionalExposure:F2}");
                }
            }

            var (unrealisedPnl, currentMarginUsed) = ComputeUnrealisedPnlAndMargin();
            decimal currentEquity   = _balance + unrealisedPnl;
            decimal marginAvailable = currentEquity - currentMarginUsed;

            if (marginRequired > marginAvailable)
            {
                _logger.LogWarning(
                    "SimulatedBroker: insufficient margin for {OrderType} {Symbol} {Lots} lots " +
                    "(required={Required:F2}, available={Available:F2}) — order {Id} rejected",
                    order.OrderType, order.Symbol, fillQuantity, marginRequired, marginAvailable, order.OrderId);
                return Rejected(
                    $"Insufficient margin: required {marginRequired:F2}, available {marginAvailable:F2}");
            }

            // Deduct commission from balance
            decimal commission = GetCommission(order.Symbol, fillQuantity);
            _balance -= commission;
            ApplyNegativeBalanceProtection();

            string brokerOrderId = $"SIM-{Interlocked.Increment(ref _orderIdCounter):D10}";
            bool   isBuy         = order.OrderType == OrderType.Buy;

            // ── Netting mode: reduce or flip existing position on the same symbol ──
            if (_options.NettingMode)
            {
                string? existingKey = _symbolToOrderId.TryGetValue(order.Symbol, out var eid) &&
                                      _openPositions.ContainsKey(eid) ? eid : null;

                if (existingKey != null)
                {
                    var ep = _openPositions[existingKey];

                    if (ep.IsBuy == isBuy)
                    {
                        // Same direction: increase position with blended entry price
                        decimal totalLots = ep.Lots + fillQuantity;
                        decimal blendedEntry = ((ep.EntryPrice * ep.Lots) + (fillPrice * fillQuantity)) / totalLots;
                        var merged = new SimulatedPosition(
                            ep.Symbol, ep.IsBuy, totalLots, blendedEntry,
                            order.StopLoss ?? ep.StopLoss, order.TakeProfit ?? ep.TakeProfit);
                        _openPositions[existingKey] = merged;

                        _logger.LogInformation(
                            "SimulatedBroker: netting — increased {Symbol} {Direction} to {Lots} lots @ blended {Entry:F5}",
                            ep.Symbol, isBuy ? "long" : "short", totalLots, blendedEntry);

                        RecordTradeHistory(existingKey, order.Symbol, isBuy, fillQuantity,
                            fillPrice, 0m, commission, TradeHistoryReason.MarketFill);

                        return new BrokerOrderResult(true, existingKey, fillPrice, fillQuantity, null);
                    }

                    // Opposite direction: reduce, close, or flip
                    if (fillQuantity < ep.Lots)
                    {
                        // Partial close of existing position
                        decimal pnl = ConvertPnlToAccountCurrency(
                            ep.Symbol,
                            CalculatePositionPnl(ep, fillPrice, fillQuantity));
                        decimal closeCommission = GetCommission(order.Symbol, fillQuantity);
                        _balance += pnl;
                        _balance -= closeCommission;
                        ApplyNegativeBalanceProtection();
                        var reduced = new SimulatedPosition(
                            ep.Symbol, ep.IsBuy, ep.Lots - fillQuantity, ep.EntryPrice,
                            ep.StopLoss, ep.TakeProfit);
                        _openPositions[existingKey] = reduced;

                        _logger.LogInformation(
                            "SimulatedBroker: netting — reduced {Symbol} {Direction} by {Lots} lots (PnL={PnL:F2}, commission={Commission:F2})",
                            ep.Symbol, ep.IsBuy ? "long" : "short", fillQuantity, pnl, closeCommission);

                        RecordTradeHistory(brokerOrderId, order.Symbol, !isBuy, fillQuantity,
                            fillPrice, pnl, closeCommission, TradeHistoryReason.NettingReduce);

                        return new BrokerOrderResult(true, brokerOrderId, fillPrice, fillQuantity, null);
                    }

                    if (fillQuantity == ep.Lots)
                    {
                        // Exact close
                        decimal pnl = ConvertPnlToAccountCurrency(
                            ep.Symbol,
                            CalculatePositionPnl(ep, fillPrice, fillQuantity));
                        decimal closeCommission = GetCommission(order.Symbol, fillQuantity);
                        _balance += pnl;
                        _balance -= closeCommission;
                        ApplyNegativeBalanceProtection();
                        _openPositions.TryRemove(existingKey, out _);
                        _lastSwapDate.TryRemove(existingKey, out _);
                        _symbolToOrderId.TryRemove(order.Symbol, out _);

                        _logger.LogInformation(
                            "SimulatedBroker: netting — closed {Symbol} {Direction} {Lots} lots (PnL={PnL:F2}, commission={Commission:F2})",
                            ep.Symbol, ep.IsBuy ? "long" : "short", fillQuantity, pnl, closeCommission);

                        RecordTradeHistory(brokerOrderId, order.Symbol, !isBuy, fillQuantity,
                            fillPrice, pnl, closeCommission, TradeHistoryReason.NettingClose);

                        return new BrokerOrderResult(true, brokerOrderId, fillPrice, fillQuantity, null);
                    }

                    // Flip: close existing and open remainder in opposite direction
                    decimal closePnl = ConvertPnlToAccountCurrency(
                        ep.Symbol,
                        CalculatePositionPnl(ep, fillPrice, ep.Lots));
                    decimal flipCloseCommission = GetCommission(ep.Symbol, ep.Lots);
                    _balance += closePnl;
                    _balance -= flipCloseCommission;
                    ApplyNegativeBalanceProtection();
                    _openPositions.TryRemove(existingKey, out _);
                    _lastSwapDate.TryRemove(existingKey, out _);

                    decimal flipLots = fillQuantity - ep.Lots;
                    var flipped = new SimulatedPosition(
                        order.Symbol, isBuy, flipLots, fillPrice,
                        order.StopLoss, order.TakeProfit,
                        order.TrailingStopEnabled, order.TrailingStopType, order.TrailingStopValue);
                    if (!_openPositions.TryAdd(brokerOrderId, flipped))
                    {
                        _logger.LogWarning(
                            "SimulatedBroker: netting flip failed to add new position {BrokerOrderId} for {Symbol} — position lost",
                            brokerOrderId, order.Symbol);
                    }
                    _symbolToOrderId[order.Symbol] = brokerOrderId;

                    _logger.LogInformation(
                        "SimulatedBroker: netting — flipped {Symbol} from {OldDir} to {NewDir} {Lots} lots (closePnL={PnL:F2}, commission={Commission:F2})",
                        order.Symbol, ep.IsBuy ? "long" : "short", isBuy ? "long" : "short", flipLots, closePnl, flipCloseCommission);

                    RecordTradeHistory(brokerOrderId, order.Symbol, !isBuy, ep.Lots,
                        fillPrice, closePnl, flipCloseCommission, TradeHistoryReason.NettingFlip);

                    return new BrokerOrderResult(true, brokerOrderId, fillPrice, fillQuantity, null);
                }
            }

            // ── Hedging mode (default): each fill creates an independent position ──
            var position = new SimulatedPosition(
                order.Symbol, isBuy, fillQuantity, fillPrice,
                order.StopLoss, order.TakeProfit,
                order.TrailingStopEnabled, order.TrailingStopType, order.TrailingStopValue);
            _openPositions.TryAdd(brokerOrderId, position);
            if (_options.NettingMode)
                _symbolToOrderId[order.Symbol] = brokerOrderId;

            _logger.LogInformation(
                "SimulatedBroker: filled {OrderType} {Symbol} {Lots} lots @ {Price:F5} " +
                "(slippage={Slip:F1} pips, margin={Margin:F2}, commission={Commission:F2})",
                order.OrderType, order.Symbol, fillQuantity, fillPrice, _slippagePips, marginRequired, commission);

            RecordTradeHistory(brokerOrderId, order.Symbol, isBuy, fillQuantity,
                fillPrice, 0m, commission, historyReason);

            // If partial fill, re-queue the remainder as a new pending order
            if (remainderQuantity > 0)
            {
                var remainder = new OrderSnapshot(
                    order.OrderId, order.Symbol, order.OrderType, order.ExecutionType,
                    remainderQuantity, order.Price, order.StopLoss, order.TakeProfit);
                EnqueuePendingOrder(remainder);

                _logger.LogInformation(
                    "SimulatedBroker: partial fill — {Remainder} lots re-queued as pending for {Symbol}",
                    remainderQuantity, order.Symbol);
            }

            return new BrokerOrderResult(
                Success:        true,
                BrokerOrderId:  brokerOrderId,
                FilledPrice:    fillPrice,
                FilledQuantity: fillQuantity,
                ErrorMessage:   null);
        }
    }

    private BrokerOrderResult EnqueuePendingOrder(
        OrderSnapshot order,
        SimulatedTimeInForce? timeInForce = null,
        DateTime? expiresAtUtc = null,
        string? ocoGroupId = null)
    {
        string brokerOrderId = $"SIM-P-{Interlocked.Increment(ref _orderIdCounter):D10}";

        var tif = timeInForce ?? _options.DefaultTimeInForce;

        // Compute expiry for DAY orders: next occurrence of the rollover hour
        DateTime? effectiveExpiry = tif switch
        {
            SimulatedTimeInForce.GTC => null,
            SimulatedTimeInForce.DAY => ComputeEndOfDay(UtcNow),
            SimulatedTimeInForce.GTD => expiresAtUtc, // null means use PendingOrderExpiryMinutes in ExpireStalePendingOrders
            _ => expiresAtUtc
        };

        var pending = new PendingOrderSnapshot(order, brokerOrderId, UtcNow)
        {
            TimeInForce = tif,
            ExpiresAtUtc = effectiveExpiry,
            OcoGroupId = ocoGroupId
        };
        _pendingOrders.TryAdd(brokerOrderId, pending);

        _logger.LogInformation(
            "SimulatedBroker: queued {ExecutionType} {OrderType} {Symbol} {Lots} lots @ {Price:F5} as pending {BrokerOrderId} (TIF={TIF}, expires={Expiry}, oco={Oco})",
            order.ExecutionType, order.OrderType, order.Symbol, order.Quantity, order.Price,
            brokerOrderId, tif, effectiveExpiry?.ToString("u") ?? "none", ocoGroupId ?? "none");

        return new BrokerOrderResult(
            Success:        true,
            BrokerOrderId:  brokerOrderId,
            FilledPrice:    null,
            FilledQuantity: null,
            ErrorMessage:   null);
    }

    /// <summary>
    /// Computes the end-of-day expiry: the next occurrence of <see cref="_swapRolloverHourUtc"/>
    /// after the given time. If the rollover hour has already passed today, returns tomorrow's.
    /// </summary>
    private DateTime ComputeEndOfDay(DateTime utcNow)
    {
        var todayRollover = utcNow.Date.AddHours(_swapRolloverHourUtc);
        return utcNow < todayRollover ? todayRollover : todayRollover.AddDays(1);
    }

    /// <summary>
    /// Cancels all pending orders that share the given OCO group, except the order identified
    /// by <paramref name="exceptBrokerOrderId"/> (the one that was just filled/triggered).
    /// Returns the list of cancelled broker order IDs.
    /// </summary>
    private IReadOnlyList<string> CancelOcoGroup(string ocoGroupId, string? exceptBrokerOrderId)
    {
        var cancelled = new List<string>();

        foreach (var kvp in _pendingOrders)
        {
            if (kvp.Value.OcoGroupId != ocoGroupId)
                continue;
            if (kvp.Key == exceptBrokerOrderId)
                continue;

            if (_pendingOrders.TryRemove(kvp.Key, out _))
            {
                cancelled.Add(kvp.Key);
                _logger.LogInformation(
                    "SimulatedBroker: OCO cancel — removed pending order {BrokerOrderId} (group={OcoGroup}, triggered by {TriggeredBy})",
                    kvp.Key, ocoGroupId, exceptBrokerOrderId);
            }
        }

        return cancelled;
    }

    private BrokerOrderResult ClosePositionInternal(string brokerPositionId, decimal lots)
    {
        // Atomic: remove + price + balance update all under the same lock so
        // ComputeUnrealisedPnlAndMargin never sees position gone but PnL not yet realised.
        lock (_stateLock)
        {
            if (!_openPositions.TryRemove(brokerPositionId, out var position))
            {
                _logger.LogWarning(
                    "SimulatedBroker: position {PositionId} not found for close",
                    brokerPositionId);
                return Rejected($"Position {brokerPositionId} not found");
            }

            var priceData = _priceCache.Get(position.Symbol);
            if (priceData == null)
            {
                _openPositions.TryAdd(brokerPositionId, position);
                return Rejected($"No live price available to close position on {position.Symbol}");
            }

            decimal closeLots = Math.Min(lots, position.Lots);
            decimal pipUnit  = GetPipUnit(position.Symbol);
            decimal slippage = ComputeSlippage(position.Symbol, closeLots, priceData.Value.Bid, priceData.Value.Ask, pipUnit);

            decimal closePrice = Math.Max(0, position.IsBuy
                ? priceData.Value.Bid - slippage
                : priceData.Value.Ask + slippage);
            decimal pnl = ConvertPnlToAccountCurrency(
                position.Symbol,
                CalculatePositionPnl(position, closePrice, closeLots));
            decimal commission = GetCommission(position.Symbol, closeLots);

            _balance += pnl;
            _balance -= commission;
            ApplyNegativeBalanceProtection();

            if (closeLots < position.Lots)
            {
                var remaining = new SimulatedPosition(
                    position.Symbol, position.IsBuy, position.Lots - closeLots,
                    position.EntryPrice, position.StopLoss, position.TakeProfit,
                    position.TrailingStopEnabled, position.TrailingStopType, position.TrailingStopValue);
                remaining.BestPrice = position.BestPrice;
                _openPositions.TryAdd(brokerPositionId, remaining);
            }
            else
            {
                _lastSwapDate.TryRemove(brokerPositionId, out _);
                if (_options.NettingMode)
                    _symbolToOrderId.TryRemove(position.Symbol, out _);
            }

            _logger.LogInformation(
                "SimulatedBroker: closed {Lots} lots of position {PositionId} @ {Price:F5} (PnL={PnL:F2}, commission={Commission:F2})",
                closeLots, brokerPositionId, closePrice, pnl, commission);

            RecordTradeHistory(brokerPositionId, position.Symbol, !position.IsBuy, closeLots,
                closePrice, pnl, commission, TradeHistoryReason.ManualClose);

            return new BrokerOrderResult(true, brokerPositionId, closePrice, closeLots, null);
        }
    }

    // ── Private: fill notification ──────────────────────────────────────────

    private async Task NotifyFillAsync(string brokerOrderId, BrokerOrderResult result, SimulatedFillReason reason)
    {
        if (_onPositionClosed == null)
            return;

        try
        {
            await _onPositionClosed(new SimulatedFillNotification(brokerOrderId, result, reason));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SimulatedBroker: fill notification callback failed for {BrokerOrderId} ({Reason})",
                brokerOrderId, reason);
        }
    }

    // ── Private: state persistence ──────────────────────────────────────────

    private void MaybePersistState()
    {
        if (!_options.PersistState)
            return;

        var now = UtcNow;
        if ((now - _lastStatePersistUtc).TotalSeconds < _options.StateSnapshotIntervalSeconds)
            return;

        _lastStatePersistUtc = now;
        PersistState();
    }

    private void PersistState()
    {
        try
        {
            SimulatedBrokerState state;
            lock (_stateLock)
            {
                state = new SimulatedBrokerState
                {
                    Balance = _balance,
                    OrderIdCounter = Interlocked.Read(ref _orderIdCounter),
                    OpenPositions = _openPositions.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new PersistedPosition(
                            kvp.Value.Symbol, kvp.Value.IsBuy, kvp.Value.Lots,
                            kvp.Value.EntryPrice, kvp.Value.StopLoss, kvp.Value.TakeProfit,
                            kvp.Value.TrailingStopEnabled, kvp.Value.TrailingStopType,
                            kvp.Value.TrailingStopValue, kvp.Value.BestPrice)),
                    PendingOrders = _pendingOrders.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new PersistedPendingOrder(
                            kvp.Value.Order.OrderId, kvp.Value.Order.Symbol,
                            kvp.Value.Order.OrderType, kvp.Value.Order.ExecutionType,
                            kvp.Value.Order.Quantity, kvp.Value.Order.Price,
                            kvp.Value.StopLoss, kvp.Value.TakeProfit,
                            kvp.Value.CreatedAtUtc,
                            kvp.Value.TimeInForce, kvp.Value.ExpiresAtUtc,
                            kvp.Value.OcoGroupId)),
                    LastSwapDates = _lastSwapDate.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    TradeHistory = GetTradeHistory().ToList(),
                    SavedAtUtc = UtcNow
                };
            }

            var json = JsonSerializer.Serialize(state, s_jsonOptions);
            var tempPath = _options.StateFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _options.StateFilePath, overwrite: true);

            // Write a SHA256 checksum file so RestoreState can detect corruption
            var checksum = ComputeSha256(json);
            File.WriteAllText(_options.StateFilePath + ".sha256", checksum);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SimulatedBroker: failed to persist state to {Path}", _options.StateFilePath);
        }
    }

    private void RestoreState()
    {
        try
        {
            if (!File.Exists(_options.StateFilePath))
                return;

            var json = File.ReadAllText(_options.StateFilePath);

            // Validate checksum if a .sha256 file exists alongside the state file
            var checksumPath = _options.StateFilePath + ".sha256";
            if (File.Exists(checksumPath))
            {
                var expectedChecksum = File.ReadAllText(checksumPath).Trim();
                var actualChecksum = ComputeSha256(json);
                if (!string.Equals(expectedChecksum, actualChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError(
                        "SimulatedBroker: state file {Path} failed integrity check " +
                        "(expected SHA256={Expected}, actual={Actual}) — starting with fresh state",
                        _options.StateFilePath, expectedChecksum, actualChecksum);
                    return;
                }
            }

            var state = JsonSerializer.Deserialize<SimulatedBrokerState>(json);
            if (state == null) return;

            if (state.Version > CurrentStateVersion)
            {
                _logger.LogWarning(
                    "SimulatedBroker: state file {Path} has version {FileVersion} which is newer than " +
                    "current schema version {CurrentVersion} — ignoring to prevent data corruption. " +
                    "The broker will start with a fresh state",
                    _options.StateFilePath, state.Version, CurrentStateVersion);
                return;
            }

            if (state.Version < CurrentStateVersion)
            {
                _logger.LogInformation(
                    "SimulatedBroker: migrating state file from version {OldVersion} to {NewVersion}",
                    state.Version, CurrentStateVersion);
                // Version 1 → 2: PendingOrderSnapshot gained TimeInForce, ExpiresAtUtc, OcoGroupId.
                // These default to GTD/null/null in the record definition, so no explicit migration needed.
                // Future migrations can be added here as version-guarded blocks.
            }

            _balance = state.Balance;
            Interlocked.Exchange(ref _orderIdCounter, state.OrderIdCounter);

            foreach (var (key, p) in state.OpenPositions)
            {
                var pos = new SimulatedPosition(
                    p.Symbol, p.IsBuy, p.Lots, p.EntryPrice, p.StopLoss, p.TakeProfit,
                    p.TrailingStopEnabled, p.TrailingStopType, p.TrailingStopValue);
                if (p.BestPrice.HasValue)
                    pos.BestPrice = p.BestPrice.Value;
                _openPositions.TryAdd(key, pos);
                if (_options.NettingMode)
                    _symbolToOrderId[p.Symbol] = key;
            }

            foreach (var (key, po) in state.PendingOrders)
            {
                var orderSnapshot = new OrderSnapshot(
                    po.OrderId, po.Symbol, po.OrderType, po.ExecutionType,
                    po.Quantity, po.Price, po.StopLoss, po.TakeProfit);
                var pending = new PendingOrderSnapshot(orderSnapshot, key, po.CreatedAtUtc)
                {
                    TimeInForce = po.TimeInForce,
                    ExpiresAtUtc = po.ExpiresAtUtc,
                    OcoGroupId = po.OcoGroupId
                };
                _pendingOrders.TryAdd(key, pending);
            }

            foreach (var (key, date) in state.LastSwapDates)
            {
                _lastSwapDate.TryAdd(key, date);
            }

            if (state.TradeHistory.Count > 0)
            {
                lock (_tradeHistoryLock)
                {
                    foreach (var entry in state.TradeHistory)
                    {
                        if (_tradeHistory.Count >= _options.TradeHistoryCapacity)
                            break;
                        _tradeHistory.Enqueue(entry);
                    }
                }
            }

            _logger.LogInformation(
                "SimulatedBroker: restored state from {Path} (balance={Balance:F2}, positions={Positions}, pending={Pending}, tradeHistory={TradeHistory}, savedAt={SavedAt})",
                _options.StateFilePath, _balance, _openPositions.Count, _pendingOrders.Count, _tradeHistory.Count, state.SavedAtUtc);

            CatchUpMissedSwaps(state.SavedAtUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SimulatedBroker: failed to restore state from {Path}", _options.StateFilePath);
        }
    }

    /// <summary>
    /// Applies swap charges for any rollover crossings that were missed between the last
    /// state save and now. Each missed day charges one swap per position (triple on Wednesdays).
    /// Called during <see cref="RestoreState"/> so positions held overnight during downtime
    /// are correctly charged.
    /// </summary>
    private void CatchUpMissedSwaps(DateTime savedAtUtc)
    {
        bool hasSwap = _swapLongPerLot != 0 || _swapShortPerLot != 0;
        if (!hasSwap || _openPositions.IsEmpty)
            return;

        var now = DateTime.UtcNow;
        if (savedAtUtc >= now)
            return;

        // Walk forward from the save time, finding each rollover hour crossing
        // Start from the first rollover hour after the save time
        var cursor = savedAtUtc.Date.AddHours(_swapRolloverHourUtc);
        if (cursor <= savedAtUtc)
            cursor = cursor.AddDays(1);

        int missedDays = 0;
        decimal totalSwapCharged = 0m;

        lock (_stateLock)
        {
            while (cursor < now)
            {
                var cursorDate = cursor.Date;

                foreach (var kvp in _openPositions)
                {
                    var pos = kvp.Value;

                    // Skip if swap was already applied for this date
                    if (_lastSwapDate.TryGetValue(kvp.Key, out var lastDate) && lastDate >= cursorDate)
                        continue;

                    decimal swapPerLot = pos.IsBuy ? _swapLongPerLot : _swapShortPerLot;
                    decimal swapCharge = swapPerLot * pos.Lots;

                    // Triple swap on Wednesdays (covers Saturday + Sunday)
                    if (cursor.DayOfWeek == DayOfWeek.Wednesday)
                        swapCharge *= 3;

                    _balance += swapCharge;
                    totalSwapCharged += swapCharge;
                    _lastSwapDate[kvp.Key] = cursorDate;
                }

                missedDays++;
                cursor = cursor.AddDays(1);
            }

            if (missedDays > 0)
                ApplyNegativeBalanceProtection();
        }

        if (missedDays > 0)
        {
            _logger.LogInformation(
                "SimulatedBroker: caught up {Days} missed swap day(s) since {SavedAt} — total swap={Swap:F2}, balance={Balance:F2}",
                missedDays, savedAtUtc, totalSwapCharged, _balance);
        }
    }

    // ── Private: cross-currency PnL conversion ──────────────────────────────

    /// <summary>
    /// Converts a raw PnL value (denominated in the instrument's quote currency) to the
    /// account currency using live rates from <see cref="ILivePriceCache"/>. If the quote
    /// currency matches the account currency, returns the PnL unchanged.
    /// Falls back to 1:1 if no conversion rate is available.
    /// </summary>
    private decimal ConvertPnlToAccountCurrency(string symbol, decimal rawPnl)
    {
        if (rawPnl == 0)
            return 0;

        string accountCcy = _options.AccountCurrency;
        if (string.IsNullOrEmpty(accountCcy))
            return rawPnl;

        // Look up the instrument's quote currency from cached metadata.
        // Metadata is pre-loaded at tick-loop start via PreloadCurrencyPairMetadataAsync.
        // On cache miss, fall back to 1:1 rather than blocking on a sync-over-async DB call
        // (which could deadlock or stall the tick loop under load).
        if (!_currencyPairCache.TryGetValue(symbol, out var metadata))
        {
            _logger.LogDebug(
                "SimulatedBroker: no cached currency pair metadata for {Symbol} — using 1:1 conversion. " +
                "Ensure the symbol is included in the subscription list for PreloadCurrencyPairMetadataAsync",
                symbol);
            return rawPnl;
        }

        string quoteCcy = metadata.QuoteCurrency;

        // If quote currency matches account currency, no conversion needed
        if (string.Equals(quoteCcy, accountCcy, StringComparison.OrdinalIgnoreCase))
            return rawPnl;

        // Try direct pair: {quoteCcy}{accountCcy} — e.g. GBPUSD if quote=GBP, account=USD
        decimal? rate = TryGetConversionRate(quoteCcy, accountCcy);
        if (rate.HasValue)
            return rawPnl * rate.Value;

        // Try inverse pair: {accountCcy}{quoteCcy} — e.g. USDGBP
        rate = TryGetConversionRate(accountCcy, quoteCcy);
        if (rate.HasValue && rate.Value != 0)
            return rawPnl / rate.Value;

        _logger.LogWarning(
            "SimulatedBroker: no conversion rate found for {QuoteCcy} → {AccountCcy}, using 1:1 for {Symbol}. " +
            "Ensure the conversion pair is included in the subscription list",
            quoteCcy, accountCcy, symbol);
        return rawPnl;
    }

    /// <summary>
    /// Attempts to find a live mid-price for the pair {fromCcy}{toCcy} in the price cache.
    /// Returns null if not available.
    /// </summary>
    private decimal? TryGetConversionRate(string fromCcy, string toCcy)
    {
        string pairSymbol = $"{fromCcy}{toCcy}";
        var priceData = _priceCache.Get(pairSymbol);
        if (priceData == null)
            return null;

        return (priceData.Value.Bid + priceData.Value.Ask) / 2m;
    }

    /// <summary>
    /// Pre-loads currency pair metadata (quote currency) for the given symbols from the database.
    /// Called once at the start of the tick loop so conversion lookups are O(1).
    /// </summary>
    private async Task PreloadCurrencyPairMetadataAsync(List<string> symbols)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var db = readContext.GetDbContext();

            var pairs = await db.Set<CurrencyPair>()
                .Where(cp => symbols.Contains(cp.Symbol) && !cp.IsDeleted)
                .Select(cp => new { cp.Symbol, cp.QuoteCurrency })
                .ToListAsync(CancellationToken.None);

            foreach (var pair in pairs)
            {
                _currencyPairCache[pair.Symbol] = new CurrencyPairMetadata(pair.QuoteCurrency);
            }

            _logger.LogDebug(
                "SimulatedBroker: loaded currency pair metadata for {Count}/{Total} symbols",
                pairs.Count, symbols.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SimulatedBroker: failed to load currency pair metadata — PnL conversion will use 1:1");
        }
    }

    // ── Private: shared computations ────────────────────────────────────────

    private static bool IsPendingOrderTriggered(OrderSnapshot order, decimal bid, decimal ask)
    {
        decimal triggerPrice = order.Price;

        return order.ExecutionType switch
        {
            ExecutionType.Limit when order.OrderType == OrderType.Buy  => ask <= triggerPrice,
            ExecutionType.Limit when order.OrderType == OrderType.Sell => bid >= triggerPrice,
            ExecutionType.Stop when order.OrderType == OrderType.Buy   => ask >= triggerPrice,
            ExecutionType.Stop when order.OrderType == OrderType.Sell  => bid <= triggerPrice,
            ExecutionType.StopLimit when order.OrderType == OrderType.Buy  => ask >= triggerPrice,
            ExecutionType.StopLimit when order.OrderType == OrderType.Sell => bid <= triggerPrice,
            _ => true
        };
    }

    private static bool IsPendingOrderTriggered(PendingOrderSnapshot pending, decimal bid, decimal ask)
        => IsPendingOrderTriggered(pending.Order, bid, ask);

    /// <summary>
    /// Computes the fill price based on execution type:
    /// <list type="bullet">
    ///   <item><b>Market</b> — fills at ask (buy) or bid (sell) + adverse slippage.</item>
    ///   <item><b>Limit</b> — fills at the limit price or better (price improvement).</item>
    ///   <item><b>Stop / StopLimit</b> — becomes a market order once triggered; fills at the
    ///     worse of market vs stop price + slippage (gap-aware).</item>
    /// </list>
    /// Result is clamped to a minimum of zero.
    /// </summary>
    private static decimal ComputeFillPrice(OrderSnapshot order, decimal bid, decimal ask, decimal slippage)
    {
        decimal price = order.ExecutionType switch
        {
            // Limit buy: fill at the better of ask or the limit price (price improvement)
            ExecutionType.Limit when order.OrderType == OrderType.Buy
                => Math.Min(ask, order.Price),

            // Limit sell: fill at the better of bid or the limit price (price improvement)
            ExecutionType.Limit when order.OrderType == OrderType.Sell
                => Math.Max(bid, order.Price),

            // Stop buy: once triggered, fills at market; in a gap, market is worse than stop
            ExecutionType.Stop when order.OrderType == OrderType.Buy
                => Math.Max(ask, order.Price) + slippage,

            // Stop sell: once triggered, fills at market; in a gap, market is worse than stop
            ExecutionType.Stop when order.OrderType == OrderType.Sell
                => Math.Min(bid, order.Price) - slippage,

            // StopLimit: once triggered, converts to a limit order — fill at the trigger
            // price or better (no slippage). Returns -1 if the market has gapped past the
            // limit, signalling that the fill should be rejected.
            ExecutionType.StopLimit when order.OrderType == OrderType.Buy
                => ask <= order.Price ? ask : -1m,

            ExecutionType.StopLimit when order.OrderType == OrderType.Sell
                => bid >= order.Price ? bid : -1m,

            // Market: fill at ask/bid + adverse slippage
            _ => order.OrderType == OrderType.Buy
                ? ask + slippage
                : bid - slippage
        };

        // Preserve negative sentinel for StopLimit rejection; clamp all other prices to 0
        return price < 0 ? price : Math.Max(0, price);
    }

    private static decimal CalculatePositionPnl(SimulatedPosition position, decimal currentPrice, decimal lots)
    {
        decimal priceDelta = position.IsBuy
            ? currentPrice - position.EntryPrice
            : position.EntryPrice - currentPrice;

        return priceDelta * lots * StandardLotSize;
    }

    /// <summary>
    /// Computes unrealised PnL (converted to account currency) and total margin used
    /// across all open positions.
    /// Does NOT acquire <see cref="_stateLock"/> — caller must hold it when balance consistency matters.
    /// </summary>
    private (decimal UnrealisedPnl, decimal MarginUsed) ComputeUnrealisedPnlAndMargin()
    {
        decimal unrealisedPnl = 0m;
        decimal marginUsed    = 0m;

        foreach (var kvp in _openPositions)
        {
            var pos       = kvp.Value;
            var priceData = _priceCache.Get(pos.Symbol);
            if (priceData != null)
            {
                decimal currentPrice = pos.IsBuy ? priceData.Value.Bid : priceData.Value.Ask;
                decimal rawPnl = CalculatePositionPnl(pos, currentPrice, pos.Lots);
                unrealisedPnl += ConvertPnlToAccountCurrency(pos.Symbol, rawPnl);
            }

            decimal notional = pos.Lots * StandardLotSize * pos.EntryPrice;
            marginUsed += notional / _leverage;
        }

        return (unrealisedPnl, marginUsed);
    }

    /// <summary>
    /// Computes total notional exposure across all open positions.
    /// Does NOT acquire <see cref="_stateLock"/> — caller must hold it.
    /// </summary>
    private decimal ComputeTotalNotionalExposure()
    {
        decimal total = 0m;
        foreach (var kvp in _openPositions)
        {
            var pos = kvp.Value;
            var priceData = _priceCache.Get(pos.Symbol);
            decimal currentPrice = priceData != null
                ? (pos.IsBuy ? priceData.Value.Bid : priceData.Value.Ask)
                : pos.EntryPrice;
            total += pos.Lots * StandardLotSize * currentPrice;
        }
        return total;
    }

    /// <summary>
    /// Computes effective slippage for a fill, combining up to four components:
    /// <list type="number">
    ///   <item>Base slippage: <see cref="_slippagePips"/> * pipUnit.</item>
    ///   <item>Log-normal distribution (when <see cref="SimulatedBrokerOptions.SlippageLogNormal"/>
    ///     is enabled): samples slippage from a log-normal distribution where the base slippage
    ///     is the median, producing realistic skewed slippage with occasional large events.</item>
    ///   <item>Volatility/size scaling (when <see cref="SimulatedBrokerOptions.SlippageVolatilityScaling"/>
    ///     is enabled): scales by current spread and sqrt of lot size.</item>
    ///   <item>Market depth impact (when <see cref="SimulatedBrokerOptions.LiquidityDepthLots"/> > 0):
    ///     adds additional slippage for orders that exceed the available liquidity depth.</item>
    /// </list>
    /// </summary>
    private decimal ComputeSlippage(string symbol, decimal lots, decimal bid, decimal ask, decimal pipUnit)
    {
        decimal baseSlippage = _slippagePips * pipUnit;

        // Log-normal distribution: sample slippage from a skewed distribution
        // where baseSlippage is the median (most fills get near-zero slippage,
        // but occasional fills get significantly more — matching real markets).
        if (_options.SlippageLogNormal && baseSlippage > 0)
        {
            double sigma = (double)_options.SlippageLogNormalSigma;
            // mu is set so that exp(mu) = baseSlippage, making baseSlippage the median
            double mu = Math.Log((double)baseSlippage);
            double normalSample = SampleStandardNormal();
            baseSlippage = (decimal)Math.Exp(mu + sigma * normalSample);
        }

        // Volatility/size scaling
        if (_options.SlippageVolatilityScaling)
        {
            decimal spread = ask - bid;
            decimal normalSpread = pipUnit; // 1 pip as baseline
            decimal spreadFactor = normalSpread > 0 ? Math.Max(1.0m, spread / normalSpread) : 1.0m;
            decimal sizeFactor = Math.Max(1.0m, (decimal)Math.Sqrt((double)lots));
            baseSlippage *= spreadFactor * sizeFactor;
        }

        // Market depth / liquidity impact
        if (_options.LiquidityDepthLots > 0 && lots > 0)
        {
            decimal effectiveDepth = _options.LiquidityDepthLots;

            // Stateful liquidity: use the depleted pool depth instead of the static max
            if (_options.StatefulLiquidity)
            {
                var pool = GetOrCreateLiquidityPool(symbol);
                lock (pool.SyncRoot)
                {
                    ReplenishLiquidity(pool);
                    effectiveDepth = Math.Max(0.01m, pool.AvailableLots);

                    // Deplete the pool by the fill size
                    pool.AvailableLots = Math.Max(0, pool.AvailableLots - lots);
                    pool.LastUpdateUtc = UtcNow;
                }
            }

            decimal depthRatio = lots / effectiveDepth;
            if (depthRatio > 1.0m)
            {
                // Impact follows a power law: impact = base * (excess_ratio) ^ exponent
                decimal exponent = _options.LiquidityImpactExponent > 0
                    ? _options.LiquidityImpactExponent
                    : 0.5m;
                decimal impact = pipUnit * (decimal)Math.Pow((double)depthRatio, (double)exponent);
                baseSlippage += impact;

                _logger.LogDebug(
                    "SimulatedBroker: market depth impact for {Symbol} {Lots} lots: " +
                    "depthRatio={Ratio:F2}, impact={Impact:F6} price units, poolRemaining={Remaining:F2}",
                    symbol, lots, depthRatio, impact, effectiveDepth - lots);
            }
        }

        return baseSlippage;
    }

    /// <summary>
    /// Returns the liquidity pool for a symbol, creating one at full depth if it doesn't exist.
    /// </summary>
    private LiquidityPool GetOrCreateLiquidityPool(string symbol)
    {
        return _liquidityPools.GetOrAdd(symbol, _ => new LiquidityPool
        {
            AvailableLots = _options.LiquidityDepthLots,
            MaxLots = _options.LiquidityDepthLots,
            LastUpdateUtc = UtcNow
        });
    }

    /// <summary>
    /// Replenishes a liquidity pool based on elapsed time since the last update.
    /// Liquidity grows linearly at <see cref="SimulatedBrokerOptions.LiquidityReplenishRatePerSecond"/>
    /// lots per second, capped at the pool's maximum depth.
    /// </summary>
    private void ReplenishLiquidity(LiquidityPool pool)
    {
        var now = UtcNow;
        var elapsed = (decimal)(now - pool.LastUpdateUtc).TotalSeconds;
        if (elapsed <= 0)
            return;

        decimal replenished = elapsed * _options.LiquidityReplenishRatePerSecond;
        pool.AvailableLots = Math.Min(pool.MaxLots, pool.AvailableLots + replenished);
        pool.LastUpdateUtc = now;
    }

    /// <summary>
    /// Samples from a standard normal distribution (mean=0, stddev=1) using the
    /// Box-Muller transform. Used by <see cref="ComputeSlippage"/> for log-normal sampling.
    /// </summary>
    private double SampleStandardNormal()
    {
        double u1 = 1.0 - NextRandomDouble(); // (0, 1] to avoid log(0)
        double u2 = NextRandomDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// Returns the pip unit for a given symbol. Checks <see cref="SimulatedBrokerOptions.PipUnitOverrides"/>
    /// first, then falls back to the standard forex convention (0.01 for JPY pairs, 0.0001 otherwise).
    /// </summary>
    private decimal GetPipUnit(string symbol)
    {
        if (_options.PipUnitOverrides.TryGetValue(symbol, out decimal pipUnit))
            return pipUnit;

        return symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase) ? 0.01m : 0.0001m;
    }

    /// <summary>
    /// Returns the current effective time. In replay mode this is the historical candle
    /// timestamp; in all other modes it returns <see cref="DateTime.UtcNow"/>.
    /// Thread-safe: reads via <see cref="Interlocked.Read"/>.
    /// </summary>
    private DateTime UtcNow => new(Interlocked.Read(ref _simulatedUtcNowTicks), DateTimeKind.Utc);

    /// <summary>
    /// Thread-safe wrapper for <see cref="Random.NextDouble"/>. When using a seeded
    /// (non-thread-safe) instance, access is serialised via <see cref="_randomLock"/>.
    /// </summary>
    private double NextRandomDouble()
    {
        if (_randomLock is null)
            return _random.NextDouble();

        lock (_randomLock)
            return _random.NextDouble();
    }

    /// <summary>
    /// Thread-safe wrapper for <see cref="Random.Next(int, int)"/>. When using a seeded
    /// (non-thread-safe) instance, access is serialised via <see cref="_randomLock"/>.
    /// </summary>
    private int NextRandomInt(int minValue, int maxValue)
    {
        if (_randomLock is null)
            return _random.Next(minValue, maxValue);

        lock (_randomLock)
            return _random.Next(minValue, maxValue);
    }

    /// <summary>
    /// Returns true if the given UTC time falls within a market-closed window.
    /// Checks three conditions:
    /// <list type="number">
    ///   <item>Weekend window: from <see cref="SimulatedBrokerOptions.MarketCloseDay"/> at
    ///     <see cref="SimulatedBrokerOptions.MarketCloseHourUtc"/> through
    ///     <see cref="SimulatedBrokerOptions.MarketOpenDay"/> at
    ///     <see cref="SimulatedBrokerOptions.MarketOpenHourUtc"/>.</item>
    ///   <item>All-day Saturday (always closed for standard forex).</item>
    ///   <item>Explicit holiday dates from <see cref="SimulatedBrokerOptions.Holidays"/>.</item>
    /// </list>
    /// </summary>
    private bool IsMarketClosed(DateTime utcNow)
    {
        // Check explicit holidays first (full-day closures)
        if (_holidays.Contains(utcNow.Date))
            return true;

        var day  = utcNow.DayOfWeek;
        int hour = utcNow.Hour;

        // Saturday is always within the weekend window for standard forex
        if (day == DayOfWeek.Saturday)
            return true;

        // Close day: closed from the close hour onward
        if (day == _options.MarketCloseDay && hour >= _options.MarketCloseHourUtc)
            return true;

        // Open day: closed until the open hour
        if (day == _options.MarketOpenDay && hour < _options.MarketOpenHourUtc)
            return true;

        return false;
    }

    // ── Idempotency helpers ──────────────────────────────────────────────

    /// <summary>
    /// Returns true if the given broker order ID was already processed by a recent fill.
    /// Must be called under <see cref="_stateLock"/>.
    /// </summary>
    private bool IsAlreadyProcessed(string brokerOrderId)
    {
        lock (_processedFillsLock)
        {
            return _processedFillSet.Contains(brokerOrderId);
        }
    }

    /// <summary>
    /// Records a broker order ID as processed. Evicts the oldest entry when the bounded
    /// capacity is reached. Must be called under <see cref="_stateLock"/>.
    /// </summary>
    private void MarkAsProcessed(string brokerOrderId)
    {
        lock (_processedFillsLock)
        {
            if (_processedFillSet.Add(brokerOrderId))
            {
                _processedFillIds.Enqueue(brokerOrderId);
                while (_processedFillIds.Count > ProcessedFillsCapacity)
                {
                    var evicted = _processedFillIds.Dequeue();
                    _processedFillSet.Remove(evicted);
                }
            }
        }
    }

    /// <summary>
    /// Applies negative balance protection if enabled: floors the balance at zero.
    /// Must be called under <see cref="_stateLock"/> after any balance deduction.
    /// </summary>
    private void ApplyNegativeBalanceProtection()
    {
        if (_options.NegativeBalanceProtection && _balance < 0)
        {
            _logger.LogWarning(
                "SimulatedBroker: negative balance protection activated — balance {Balance:F2} floored to 0",
                _balance);
            _balance = 0;
        }
    }

    // ── Tiered / per-symbol commission ─────────────────────────────────────

    /// <summary>
    /// Computes the commission for a fill, checking (in order):
    /// 1. Per-symbol override from <see cref="SimulatedBrokerOptions.CommissionPerSymbol"/>.
    /// 2. Volume-based tiers from <see cref="SimulatedBrokerOptions.CommissionTiers"/>.
    /// 3. Flat rate from <see cref="SimulatedBrokerOptions.CommissionPerLot"/>.
    /// Also updates <see cref="_cumulativeMonthlyVolume"/> for tier tracking.
    /// Must be called under <see cref="_stateLock"/>.
    /// </summary>
    private decimal GetCommission(string symbol, decimal lots)
    {
        // Per-symbol override takes highest priority
        if (_options.CommissionPerSymbol.TryGetValue(symbol, out decimal perSymbol))
            return perSymbol * lots;

        // Volume-based tiers
        if (_options.CommissionTiers.Count > 0)
        {
            // Reset monthly volume if the month has changed
            var now = UtcNow;
            int month = now.Year * 100 + now.Month;
            if (month != _currentCommissionMonth)
            {
                _currentCommissionMonth = month;
                _cumulativeMonthlyVolume = 0m;
            }

            // Find the applicable tier (tiers are sorted ascending by volume threshold)
            decimal rate = _commissionPerLot; // fallback
            for (int i = _options.CommissionTiers.Count - 1; i >= 0; i--)
            {
                if (_cumulativeMonthlyVolume >= _options.CommissionTiers[i].MonthlyVolumeLots)
                {
                    rate = _options.CommissionTiers[i].CommissionPerLot;
                    break;
                }
            }

            _cumulativeMonthlyVolume += lots;
            return rate * lots;
        }

        // Flat rate
        return _commissionPerLot * lots;
    }

    // ── Funding rate / dividend evaluation ──────────────────────────────────

    /// <summary>
    /// Evaluates per-symbol funding rates and dividend adjustments on open positions.
    /// Funding rates override flat swap for symbols that have them configured.
    /// Called from <see cref="EvaluateSwapAndInterest"/> at rollover time.
    /// Must be called under <see cref="_stateLock"/>.
    /// </summary>
    private void EvaluateFundingAndDividends(DateTime today)
    {
        // ── Per-symbol funding rates ─────────────────────────────────────
        bool hasFunding = _options.FundingRateLong.Count > 0 || _options.FundingRateShort.Count > 0;
        if (hasFunding)
        {
            foreach (var kvp in _openPositions)
            {
                var pos = kvp.Value;
                string symbol = pos.Symbol;

                var fundingDict = pos.IsBuy ? _options.FundingRateLong : _options.FundingRateShort;
                if (!fundingDict.TryGetValue(symbol, out decimal dailyRate))
                    continue;

                // Skip if already applied today
                string fundingKey = $"__funding__{kvp.Key}";
                if (_lastSwapDate.TryGetValue(fundingKey, out var lastDate) && lastDate == today)
                    continue;

                // Funding = notional * daily rate
                var priceData = _priceCache.Get(symbol);
                decimal currentPrice = priceData != null
                    ? (pos.IsBuy ? priceData.Value.Bid : priceData.Value.Ask)
                    : pos.EntryPrice;
                decimal notional = pos.Lots * StandardLotSize * currentPrice;
                decimal fundingCharge = notional * dailyRate;

                // Triple on Wednesday
                if (UtcNow.DayOfWeek == DayOfWeek.Wednesday)
                    fundingCharge *= 3;

                _balance += fundingCharge;
                _lastSwapDate[fundingKey] = today;

                _logger.LogDebug(
                    "SimulatedBroker: funding applied {Symbol} {Direction} {Lots} lots = {Charge:F2} (rate={Rate:P4}, notional={Notional:F2})",
                    symbol, pos.IsBuy ? "long" : "short", pos.Lots, fundingCharge, dailyRate, notional);
            }
        }

        // ── Dividend adjustments ─────────────────────────────────────────
        if (_options.DividendSchedule.Count > 0)
        {
            foreach (var kvp in _openPositions)
            {
                var pos = kvp.Value;
                if (!_options.DividendSchedule.TryGetValue(pos.Symbol, out var events))
                    continue;

                foreach (var divEvent in events)
                {
                    if (divEvent.ExDate.Date != today)
                        continue;

                    var key = (kvp.Key, divEvent.ExDate.Date);
                    if (!_appliedDividends.Add(key))
                        continue; // already applied for this position+date

                    // Long positions receive dividend credit; shorts are debited
                    decimal adjustment = divEvent.DividendPerUnit * pos.Lots * StandardLotSize;
                    if (!pos.IsBuy)
                        adjustment = -adjustment;

                    _balance += adjustment;

                    _logger.LogInformation(
                        "SimulatedBroker: dividend adjustment {Symbol} {Direction} {Lots} lots = {Adjustment:F2} (divPerUnit={Div:F6})",
                        pos.Symbol, pos.IsBuy ? "long" : "short", pos.Lots, adjustment, divEvent.DividendPerUnit);
                }
            }
        }
    }

    // ── L2 order book simulation ────────────────────────────────────────────

    /// <summary>
    /// Computes a volume-weighted average fill price by walking the simulated L2 order book.
    /// Returns the VWAP and total slippage vs the best price. Depletes book levels as lots
    /// are consumed; levels replenish over time.
    /// </summary>
    private decimal ComputeOrderBookFillPrice(string symbol, decimal lots, bool isBuy, decimal bestBid, decimal bestAsk)
    {
        var book = GetOrCreateOrderBook(symbol, bestBid, bestAsk);
        decimal pipUnit = GetPipUnit(symbol);
        decimal stepSize = _options.OrderBookLevelStepPips * pipUnit;

        lock (book.SyncRoot)
        {
            ReplenishOrderBook(book);

            var levels = isBuy ? book.AskLevels : book.BidLevels;
            decimal remaining = lots;
            decimal totalCost = 0m;
            decimal totalFilled = 0m;

            for (int i = 0; i < levels.Length && remaining > 0; i++)
            {
                decimal available = Math.Min(remaining, levels[i].AvailableLots);
                if (available <= 0) continue;

                totalCost += available * levels[i].Price;
                totalFilled += available;
                levels[i].AvailableLots -= available;
                remaining -= available;
            }

            // If order exceeds all book levels, fill the remainder at the worst level + extra step
            if (remaining > 0 && levels.Length > 0)
            {
                decimal worstPrice = levels[^1].Price + (isBuy ? stepSize : -stepSize);
                worstPrice = Math.Max(0, worstPrice);
                totalCost += remaining * worstPrice;
                totalFilled += remaining;
            }

            book.LastUpdateUtc = UtcNow;

            return totalFilled > 0 ? totalCost / totalFilled : (isBuy ? bestAsk : bestBid);
        }
    }

    private SimulatedOrderBook GetOrCreateOrderBook(string symbol, decimal bestBid, decimal bestAsk)
    {
        return _orderBooks.GetOrAdd(symbol, _ => BuildOrderBook(symbol, bestBid, bestAsk));
    }

    private SimulatedOrderBook BuildOrderBook(string symbol, decimal bestBid, decimal bestAsk)
    {
        int levels = Math.Max(1, _options.OrderBookLevels);
        decimal pipUnit = GetPipUnit(symbol);
        decimal stepSize = _options.OrderBookLevelStepPips * pipUnit;
        decimal baseLiquidity = _options.OrderBookBaseLiquidity;

        var bidLevels = new OrderBookLevel[levels];
        var askLevels = new OrderBookLevel[levels];

        for (int i = 0; i < levels; i++)
        {
            // Liquidity decreases linearly from full at best price to half at deepest
            decimal liquidityFactor = 1.0m - (0.5m * i / levels);
            decimal liquidity = baseLiquidity * liquidityFactor;

            bidLevels[i] = new OrderBookLevel
            {
                Price = Math.Max(0, bestBid - stepSize * i),
                AvailableLots = liquidity,
                MaxLots = liquidity
            };

            askLevels[i] = new OrderBookLevel
            {
                Price = bestAsk + stepSize * i,
                AvailableLots = liquidity,
                MaxLots = liquidity
            };
        }

        return new SimulatedOrderBook
        {
            Symbol = symbol,
            BidLevels = bidLevels,
            AskLevels = askLevels,
            LastUpdateUtc = UtcNow
        };
    }

    private void ReplenishOrderBook(SimulatedOrderBook book)
    {
        var elapsed = (decimal)(UtcNow - book.LastUpdateUtc).TotalSeconds;
        if (elapsed <= 0) return;

        decimal replenished = elapsed * _options.OrderBookReplenishRatePerSecond;

        foreach (var level in book.BidLevels)
            level.AvailableLots = Math.Min(level.MaxLots, level.AvailableLots + replenished);

        foreach (var level in book.AskLevels)
            level.AvailableLots = Math.Min(level.MaxLots, level.AvailableLots + replenished);

        // Re-anchor level prices to the current market so the book tracks price movement
        // rather than staying fixed at the prices when it was first created.
        if (book.Symbol != null)
        {
            var priceData = _priceCache.Get(book.Symbol);
            if (priceData != null)
            {
                decimal pipUnit = GetPipUnit(book.Symbol);
                decimal stepSize = _options.OrderBookLevelStepPips * pipUnit;

                for (int i = 0; i < book.BidLevels.Length; i++)
                    book.BidLevels[i].Price = Math.Max(0, priceData.Value.Bid - stepSize * i);

                for (int i = 0; i < book.AskLevels.Length; i++)
                    book.AskLevels[i].Price = priceData.Value.Ask + stepSize * i;
            }
        }
    }

    // ── Multi-account helpers ───────────────────────────────────────────────

    private SubAccountState GetOrCreateSubAccount(string accountId)
    {
        return _subAccounts.GetOrAdd(accountId, id =>
        {
            decimal startingBalance = _options.SubAccounts.TryGetValue(id, out decimal bal)
                ? bal
                : _options.SimulatedBalance;
            return new SubAccountState { Balance = startingBalance };
        });
    }

    /// <summary>
    /// Computes a lowercase hex-encoded SHA256 hash of the given text.
    /// </summary>
    private static string ComputeSha256(string text)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }

    private static BrokerOrderResult Rejected(string reason)
        => new(Success: false, BrokerOrderId: null, FilledPrice: null, FilledQuantity: null, ErrorMessage: reason);

    /// <summary>
    /// Snapshots the fields we need from a domain <see cref="Order"/> entity so the singleton
    /// never holds a reference to a scoped EF-tracked object.
    /// </summary>
    private static OrderSnapshot SnapshotOrder(Order order)
        => new(order.Id, order.Symbol, order.OrderType, order.ExecutionType,
               order.Quantity, order.Price, order.StopLoss, order.TakeProfit,
               order.TrailingStopEnabled, order.TrailingStopType, order.TrailingStopValue);

    // ── Internal state models (value snapshots, no EF references) ───────────

    private sealed record OrderSnapshot(
        long          OrderId,
        string        Symbol,
        OrderType     OrderType,
        ExecutionType ExecutionType,
        decimal       Quantity,
        decimal       Price,
        decimal?      StopLoss,
        decimal?      TakeProfit,
        bool              TrailingStopEnabled = false,
        TrailingStopType? TrailingStopType    = null,
        decimal?          TrailingStopValue   = null);

    private sealed class SimulatedPosition(
        string symbol, bool isBuy, decimal lots, decimal entryPrice,
        decimal? stopLoss, decimal? takeProfit,
        bool trailingStopEnabled = false, TrailingStopType? trailingStopType = null,
        decimal? trailingStopValue = null)
    {
        public string   Symbol     { get; } = symbol;
        public bool     IsBuy      { get; } = isBuy;
        public decimal  Lots       { get; } = lots;
        public decimal  EntryPrice { get; } = entryPrice;
        public decimal? StopLoss   { get; set; } = stopLoss;
        public decimal? TakeProfit { get; set; } = takeProfit;

        // ── Trailing stop fields ─────────────────────────────────────────
        public bool              TrailingStopEnabled { get; } = trailingStopEnabled;
        public TrailingStopType? TrailingStopType    { get; } = trailingStopType;
        public decimal?          TrailingStopValue   { get; } = trailingStopValue;

        /// <summary>
        /// Tracks the best price seen since the position was opened, used to compute
        /// the trailing stop level. Updated by <see cref="EvaluateTrailingStops"/>.
        /// </summary>
        public decimal BestPrice { get; set; } = entryPrice;
    }

    private sealed class PendingOrderSnapshot(OrderSnapshot order, string brokerOrderId, DateTime createdAtUtc)
    {
        public OrderSnapshot Order         { get; set; } = order;
        public string        BrokerOrderId { get; } = brokerOrderId;
        public DateTime      CreatedAtUtc  { get; init; } = createdAtUtc;
        public decimal?      StopLoss      { get; set; } = order.StopLoss;
        public decimal?      TakeProfit    { get; set; } = order.TakeProfit;

        /// <summary>Time-in-force policy for this pending order.</summary>
        public SimulatedTimeInForce TimeInForce { get; init; } = SimulatedTimeInForce.GTD;

        /// <summary>
        /// Explicit expiry timestamp for GTD orders. Null means the order uses the default
        /// <see cref="SimulatedBrokerOptions.PendingOrderExpiryMinutes"/> relative to <see cref="CreatedAtUtc"/>.
        /// Ignored for GTC orders.
        /// </summary>
        public DateTime? ExpiresAtUtc { get; init; }

        /// <summary>
        /// OCO group identifier. When a pending order in this group is filled or triggered,
        /// all other pending orders sharing the same <see cref="OcoGroupId"/> are automatically cancelled.
        /// Null means the order is not part of any OCO group.
        /// </summary>
        public string? OcoGroupId { get; init; }
    }

    /// <summary>Cached metadata for a currency pair, used for cross-currency PnL conversion.</summary>
    private sealed record CurrencyPairMetadata(string QuoteCurrency);

    /// <summary>
    /// Tracks available liquidity for a symbol. Depleted by fills, replenished over time.
    /// All reads and writes to mutable state must be performed under <see cref="SyncRoot"/>
    /// to prevent torn values when <see cref="SimulatedBrokerAdapter.EvaluateAsync"/> is
    /// called concurrently with <see cref="SimulatedBrokerAdapter.ExecuteFill"/>.
    /// </summary>
    private sealed class LiquidityPool
    {
        public readonly Lock SyncRoot = new();
        public decimal AvailableLots { get; set; }
        public decimal MaxLots { get; set; }
        public DateTime LastUpdateUtc { get; set; }
    }

    /// <summary>A pending requote awaiting accept/decline.</summary>
    private sealed record PendingRequote(
        OrderSnapshot Order,
        decimal RequotedPrice,
        DateTime IssuedAtUtc,
        DateTime ExpiresAtUtc);

    /// <summary>Simulated L2 order book for a single symbol.</summary>
    private sealed class SimulatedOrderBook
    {
        public readonly Lock SyncRoot = new();
        public string Symbol { get; set; } = string.Empty;
        public OrderBookLevel[] BidLevels { get; set; } = [];
        public OrderBookLevel[] AskLevels { get; set; } = [];
        public DateTime LastUpdateUtc { get; set; }
    }

    /// <summary>A single price level in the simulated order book.</summary>
    private sealed class OrderBookLevel
    {
        public decimal Price { get; set; }
        public decimal AvailableLots { get; set; }
        public decimal MaxLots { get; set; }
    }

    /// <summary>Balance and state for a single sub-account in multi-account mode.</summary>
    private sealed class SubAccountState
    {
        public decimal Balance { get; set; }
    }

    // ── State persistence models ────────────────────────────────────────────

    /// <summary>
    /// Current schema version. Increment this when the shape of
    /// <see cref="SimulatedBrokerState"/> changes in a way that requires migration.
    /// </summary>
    private const int CurrentStateVersion = 2;

    private sealed class SimulatedBrokerState
    {
        /// <summary>
        /// Schema version of the persisted state. Used during <see cref="RestoreState"/>
        /// to detect incompatible state files and apply migrations if needed.
        /// </summary>
        public int Version { get; set; } = CurrentStateVersion;
        public decimal Balance { get; set; }
        public long OrderIdCounter { get; set; }
        public Dictionary<string, PersistedPosition> OpenPositions { get; set; } = new();
        public Dictionary<string, PersistedPendingOrder> PendingOrders { get; set; } = new();
        public Dictionary<string, DateTime> LastSwapDates { get; set; } = new();
        public List<TradeHistoryEntry> TradeHistory { get; set; } = new();
        public DateTime SavedAtUtc { get; set; }
    }

    private sealed record PersistedPosition(
        string Symbol, bool IsBuy, decimal Lots, decimal EntryPrice,
        decimal? StopLoss, decimal? TakeProfit,
        bool TrailingStopEnabled = false, TrailingStopType? TrailingStopType = null,
        decimal? TrailingStopValue = null, decimal? BestPrice = null);

    private sealed record PersistedPendingOrder(
        long OrderId, string Symbol, OrderType OrderType, ExecutionType ExecutionType,
        decimal Quantity, decimal Price, decimal? StopLoss, decimal? TakeProfit,
        DateTime CreatedAtUtc,
        SimulatedTimeInForce TimeInForce = SimulatedTimeInForce.GTD,
        DateTime? ExpiresAtUtc = null,
        string? OcoGroupId = null);

    // ── Trade history ────────────────────────────────────────────────────────

    /// <summary>
    /// Records a completed fill into the bounded trade history ring buffer.
    /// Evicts the oldest entry when the buffer reaches capacity.
    /// </summary>
    private void RecordTradeHistory(
        string brokerOrderId, string symbol, bool isBuy, decimal lots,
        decimal fillPrice, decimal pnl, decimal commission, TradeHistoryReason reason)
    {
        // Emit metrics for every recorded fill
        _metrics.SimBrokerFills.Add(1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("reason", reason.ToString()));
        if (pnl != 0)
            _metrics.SimBrokerRealizedPnl.Record((double)pnl,
                new KeyValuePair<string, object?>("symbol", symbol));

        if (_options.TradeHistoryCapacity <= 0)
            return;

        var entry = new TradeHistoryEntry(
            brokerOrderId, symbol, isBuy, lots, fillPrice, pnl, commission, reason, UtcNow);

        lock (_tradeHistoryLock)
        {
            while (_tradeHistory.Count >= _options.TradeHistoryCapacity)
                _tradeHistory.Dequeue();

            _tradeHistory.Enqueue(entry);
        }
    }
}

/// <summary>
/// Reason a simulated position was automatically closed or a pending order was filled.
/// </summary>
public enum SimulatedFillReason
{
    StopLoss,
    TakeProfit,
    StopOut,
    PendingOrderTriggered
}

/// <summary>
/// Notification emitted when the simulated broker automatically closes a position
/// or fills a pending order. Register via <see cref="SimulatedBrokerAdapter.OnPositionClosed"/>.
/// </summary>
public sealed record SimulatedFillNotification(
    string BrokerOrderId,
    BrokerOrderResult Result,
    SimulatedFillReason Reason);

/// <summary>
/// Result of a <see cref="SimulatedBrokerAdapter.EvaluateAsync"/> cycle, containing
/// all actions taken during the evaluation.
/// </summary>
public sealed record SimulatedBrokerEvaluation(
    IReadOnlyList<(string BrokerOrderId, BrokerOrderResult Result)> FilledOrders,
    IReadOnlyList<string> ExpiredOrders,
    IReadOnlyList<(string BrokerOrderId, BrokerOrderResult Result)> StopLossTakeProfitCloses,
    IReadOnlyList<(string BrokerOrderId, BrokerOrderResult Result)> StopOutCloses);

/// <summary>
/// Reason a trade was recorded in the trade history ring buffer.
/// </summary>
public enum TradeHistoryReason
{
    MarketFill,
    PendingOrderFill,
    StopLoss,
    TakeProfit,
    StopOut,
    ManualClose,
    NettingReduce,
    NettingClose,
    NettingFlip
}

/// <summary>
/// A completed fill record stored in the simulated broker's trade history ring buffer.
/// </summary>
public sealed record TradeHistoryEntry(
    string BrokerOrderId,
    string Symbol,
    bool IsBuy,
    decimal Lots,
    decimal FillPrice,
    decimal RealizedPnl,
    decimal Commission,
    TradeHistoryReason Reason,
    DateTime TimestampUtc);

/// <summary>
/// Warning emitted when the margin level drops below the configured
/// <see cref="SimulatedBrokerOptions.MarginCallWarningLevelPercent"/> threshold.
/// </summary>
public sealed record MarginCallWarning(
    decimal MarginLevelPercent,
    decimal Equity,
    decimal MarginUsed,
    decimal Balance,
    DateTime TimestampUtc);

/// <summary>
/// Thrown when the simulated broker adapter simulates a transient connection failure.
/// Callers (e.g. <see cref="BrokerFailoverService"/>) should catch this to trigger failover.
/// </summary>
public class BrokerConnectionException : Exception
{
    public BrokerConnectionException(string message) : base(message) { }
    public BrokerConnectionException(string message, Exception innerException) : base(message, innerException) { }
}
