using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services.BrokerAdapters;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Extended broker interface for simulated/paper trading operations beyond
/// the standard <see cref="IBrokerOrderExecutor"/> and <see cref="IBrokerDataFeed"/> contracts.
/// Provides pending order lifecycle control (time-in-force, OCO groups), price injection
/// for controlled testing, tick-loop evaluation, trade history access, and fill notifications.
/// </summary>
public interface ISimulatedBroker : IBrokerOrderExecutor, IBrokerDataFeed
{
    /// <summary>
    /// Submits a pending order with explicit time-in-force and optional OCO group.
    /// </summary>
    /// <param name="order">The order to submit.</param>
    /// <param name="timeInForce">Time-in-force policy (GTC, GTD, or DAY).</param>
    /// <param name="expiresAtUtc">Explicit expiry for GTD orders. Ignored for GTC/DAY.</param>
    /// <param name="ocoGroupId">
    /// Optional OCO group identifier. When one order in the group fills, all others
    /// in the same group are automatically cancelled.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<BrokerOrderResult> SubmitPendingOrderAsync(
        Order order,
        SimulatedTimeInForce timeInForce,
        DateTime? expiresAtUtc = null,
        string? ocoGroupId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Modifies a pending order's trigger price and/or quantity in addition to SL/TP.
    /// </summary>
    Task<BrokerOrderResult> ModifyPendingOrderAsync(
        string brokerOrderId, decimal? triggerPrice, decimal? quantity,
        decimal? stopLoss, decimal? takeProfit, CancellationToken cancellationToken);

    /// <summary>
    /// Injects a price for a symbol, writing it to the live price cache so all consumers see it.
    /// Use for controlled testing or external price feeds.
    /// </summary>
    void InjectPrice(string symbol, decimal bid, decimal ask);

    /// <summary>
    /// Evaluates all pending limit/stop orders against current prices, checks SL/TP
    /// on open positions, expires stale pending orders, enforces stop-out, applies
    /// overnight swap charges, and accrues margin interest.
    /// </summary>
    Task<SimulatedBrokerEvaluation> EvaluateAsync();

    /// <summary>
    /// Returns a snapshot of the trade history ring buffer, ordered oldest to newest.
    /// </summary>
    IReadOnlyList<TradeHistoryEntry> GetTradeHistory();

    /// <summary>
    /// Registers a callback invoked whenever a position is closed automatically
    /// (SL, TP, stop-out, or pending order fill).
    /// </summary>
    void OnPositionClosed(Func<SimulatedFillNotification, Task> callback);

    /// <summary>
    /// Registers a callback invoked when the margin level drops below the configured
    /// margin call warning threshold. Fires at most once per evaluation cycle per breach.
    /// </summary>
    void OnMarginCallWarning(Func<MarginCallWarning, Task> callback);

    /// <summary>
    /// Accepts a previously issued requote, filling the order at the requoted price.
    /// The requote must not have expired (see <see cref="SimulatedBrokerOptions.RequoteExpiryMs"/>).
    /// </summary>
    /// <param name="requoteId">The requote identifier returned in <see cref="BrokerOrderResult.RequoteId"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<BrokerOrderResult> AcceptRequoteAsync(string requoteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Declines a previously issued requote. The pending requote is removed and no fill occurs.
    /// </summary>
    /// <param name="requoteId">The requote identifier returned in <see cref="BrokerOrderResult.RequoteId"/>.</param>
    void DeclineRequote(string requoteId);

    /// <summary>
    /// Switches the active sub-account. Subsequent operations use this account's balance.
    /// Only available when <see cref="SimulatedBrokerOptions.MultiAccountEnabled"/> is true.
    /// </summary>
    void SetActiveAccount(string accountId);

    /// <summary>Returns the current active account ID.</summary>
    string GetActiveAccountId();

    /// <summary>Returns a snapshot of all sub-account balances.</summary>
    IReadOnlyDictionary<string, decimal> GetSubAccountBalances();
}
