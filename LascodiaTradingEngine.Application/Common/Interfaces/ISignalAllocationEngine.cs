using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Distributes an approved trade signal across multiple eligible trading accounts
/// using pro-rata equity, equal-risk, or Kelly-optimal allocation.
/// </summary>
public interface ISignalAllocationEngine
{
    Task<IReadOnlyList<SignalAllocation>> AllocateAsync(
        TradeSignal signal,
        IReadOnlyList<TradingAccount> eligibleAccounts,
        string allocationMethod,
        CancellationToken cancellationToken);
}
