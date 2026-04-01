using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records the allocation of a single trade signal across multiple trading accounts.
/// Each row represents one account's share of a signal, enabling pro-rata or equal-risk
/// distribution and providing a full audit trail of the allocation decision.
/// </summary>
public class SignalAllocation : Entity<long>
{
    /// <summary>FK to the approved trade signal being allocated.</summary>
    public long TradeSignalId { get; set; }

    /// <summary>FK to the trading account receiving this allocation slice.</summary>
    public long TradingAccountId { get; set; }

    /// <summary>FK to the order created for this account (null until order placed).</summary>
    public long? OrderId { get; set; }

    /// <summary>Lot size allocated to this account.</summary>
    public decimal AllocatedLotSize { get; set; }

    /// <summary>Allocation method used (ProRataEquity, EqualRisk, KellyOptimal).</summary>
    public string AllocationMethod { get; set; } = string.Empty;

    /// <summary>Account equity at time of allocation, used for pro-rata calculation.</summary>
    public decimal AccountEquityAtAllocation { get; set; }

    /// <summary>Fraction of total signal allocated to this account (0–1).</summary>
    public decimal AllocationFraction { get; set; }

    /// <summary>Whether Tier 2 risk check passed for this account.</summary>
    public bool RiskCheckPassed { get; set; }

    /// <summary>Tier 2 risk check failure reason (null if passed).</summary>
    public string? RiskCheckBlockReason { get; set; }

    /// <summary>When this allocation was computed.</summary>
    public DateTime AllocatedAt { get; set; } = DateTime.UtcNow;

    public virtual TradeSignal TradeSignal { get; set; } = null!;
    public virtual TradingAccount TradingAccount { get; set; } = null!;
    public virtual Order? Order { get; set; }

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
