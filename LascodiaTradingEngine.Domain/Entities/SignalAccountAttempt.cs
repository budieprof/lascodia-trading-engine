using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records an attempt to create an order from an approved trade signal for a specific
/// trading account. Tracks whether the account-level (Tier 2) risk check passed or failed,
/// and the reason for failure if applicable.
/// </summary>
public class SignalAccountAttempt : Entity<long>
{
    public long TradeSignalId { get; set; }

    public long TradingAccountId { get; set; }

    /// <summary>Whether the Tier 2 risk check passed for this account.</summary>
    public bool Passed { get; set; }

    /// <summary>Reason the risk check failed. Null when <see cref="Passed"/> is true.</summary>
    public string? BlockReason { get; set; }

    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    public virtual TradeSignal TradeSignal { get; set; } = null!;
    public virtual TradingAccount TradingAccount { get; set; } = null!;
}
