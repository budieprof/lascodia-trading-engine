using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

public record BrokerOrderResult(
    bool    Success,
    string? BrokerOrderId,
    decimal? FilledPrice,
    decimal? FilledQuantity,
    string? ErrorMessage);

/// <summary>Live account balance and margin snapshot returned by the broker.</summary>
public record BrokerAccountSummary(
    decimal Balance,
    decimal Equity,
    decimal MarginUsed,
    decimal MarginAvailable);

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
}
