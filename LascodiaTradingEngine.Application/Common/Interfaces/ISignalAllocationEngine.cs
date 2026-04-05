using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Distributes an approved trade signal across multiple eligible trading accounts
/// using pro-rata equity, equal-risk, or Kelly-optimal allocation.
/// </summary>
public interface ISignalAllocationEngine
{
    /// <summary>
    /// Distributes the signal across eligible accounts using the specified allocation method.
    /// Returns one allocation per eligible account with the computed lot size.
    /// </summary>
    Task<IReadOnlyList<SignalAllocation>> AllocateAsync(
        TradeSignal signal,
        IReadOnlyList<TradingAccount> eligibleAccounts,
        string allocationMethod,
        CancellationToken cancellationToken);
}
