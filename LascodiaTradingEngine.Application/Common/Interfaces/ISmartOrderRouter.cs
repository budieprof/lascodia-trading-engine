using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Selects optimal execution venue when multiple EA instances or accounts can fill the same signal.
/// Ranks by spread, latency histogram, fill-rate, and available margin.
/// </summary>
public record OrderRoutingDecision(
    string SelectedInstanceId,
    long SelectedAccountId,
    decimal ExpectedSpread,
    decimal ExpectedSlippagePips,
    string RoutingReason);

public interface ISmartOrderRouter
{
    Task<OrderRoutingDecision> RouteAsync(
        TradeSignal signal,
        IReadOnlyList<EAInstance> activeInstances,
        IReadOnlyList<TradingAccount> eligibleAccounts,
        CancellationToken cancellationToken);
}
