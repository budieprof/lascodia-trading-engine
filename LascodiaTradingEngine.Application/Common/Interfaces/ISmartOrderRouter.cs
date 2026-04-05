using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Selects optimal execution venue when multiple EA instances or accounts can fill the same signal.
/// Ranks by spread, latency histogram, fill-rate, and available margin.
/// </summary>
/// <summary>
/// Result of smart order routing: the selected EA instance, account, and expected execution costs.
/// </summary>
public record OrderRoutingDecision(
    string SelectedInstanceId,
    long SelectedAccountId,
    decimal ExpectedSpread,
    decimal ExpectedSlippagePips,
    string RoutingReason);

public interface ISmartOrderRouter
{
    /// <summary>
    /// Selects the best EA instance and trading account to execute the given signal
    /// based on spread, latency, fill-rate, and available margin.
    /// </summary>
    Task<OrderRoutingDecision> RouteAsync(
        TradeSignal signal,
        IReadOnlyList<EAInstance> activeInstances,
        IReadOnlyList<TradingAccount> eligibleAccounts,
        CancellationToken cancellationToken);
}
