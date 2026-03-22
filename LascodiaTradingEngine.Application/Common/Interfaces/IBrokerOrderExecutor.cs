using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

public record BrokerOrderResult(
    bool    Success,
    string? BrokerOrderId,
    decimal? FilledPrice,
    decimal? FilledQuantity,
    string? ErrorMessage,
    string? RequoteId = null,
    decimal? RequotedPrice = null);

/// <summary>Live account balance and margin snapshot returned by the broker.</summary>
public record BrokerAccountSummary(
    decimal Balance,
    decimal Equity,
    decimal MarginUsed,
    decimal MarginAvailable);

/// <summary>Current status of an order at the broker.</summary>
public record BrokerOrderStatus(
    string BrokerOrderId,
    string Status,
    decimal? FilledPrice,
    decimal? FilledQuantity,
    DateTime? LastUpdatedUtc);

public interface IBrokerOrderExecutor
{
    Task<BrokerOrderResult>    SubmitOrderAsync(Order order, CancellationToken cancellationToken);
    Task<BrokerOrderResult>    CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken);
    Task<BrokerOrderResult>    ModifyOrderAsync(string brokerOrderId, decimal? stopLoss, decimal? takeProfit, CancellationToken cancellationToken);
    Task<BrokerOrderResult>    ClosePositionAsync(string brokerPositionId, decimal lots, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the live account balance, equity, and margin figures from the broker.
    /// Returns <c>null</c> if the broker connection is unavailable.
    /// </summary>
    Task<BrokerAccountSummary?> GetAccountSummaryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Queries the broker for the current status of an order (pending, filled, cancelled, etc.).
    /// Returns <c>null</c> if the order cannot be found or the broker connection is unavailable.
    /// </summary>
    Task<BrokerOrderStatus?> GetOrderStatusAsync(string brokerOrderId, CancellationToken cancellationToken);
}
