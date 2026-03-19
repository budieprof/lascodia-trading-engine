using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a single trading account held with a <see cref="Broker"/>.
/// Stores the current financial state (balance, equity, margin) as last synced from the broker.
/// </summary>
/// <remarks>
/// A broker may have multiple accounts (e.g. a live USD account and a practice EUR account).
/// The account's <see cref="Balance"/>, <see cref="Equity"/>, and margin figures are
/// periodically refreshed from the broker API by the account sync worker and used by the
/// risk engine to evaluate drawdown thresholds and position-sizing constraints.
/// </remarks>
public class TradingAccount : Entity<long>
{
    /// <summary>Foreign key to the <see cref="Broker"/> that holds this account.</summary>
    public long    BrokerId        { get; set; }

    /// <summary>
    /// Broker-assigned account identifier string.
    /// e.g. "101-001-12345678-001" for Oanda practice accounts.
    /// Used when submitting orders and querying account state via the broker API.
    /// </summary>
    public string  AccountId       { get; set; } = string.Empty;

    /// <summary>Human-readable account display name (e.g. "Primary USD Live Account").</summary>
    public string  AccountName     { get; set; } = string.Empty;

    /// <summary>Account base currency code (e.g. "USD", "EUR", "GBP").</summary>
    public string  Currency        { get; set; } = "USD";

    /// <summary>
    /// Cash balance of the account in the account currency, excluding unrealised P&amp;L.
    /// Updated on every broker sync.
    /// </summary>
    public decimal Balance         { get; set; }

    /// <summary>
    /// Total equity = <see cref="Balance"/> + sum of all unrealised P&amp;L on open positions.
    /// This is the value used by the risk engine for drawdown calculations.
    /// </summary>
    public decimal Equity          { get; set; }

    /// <summary>
    /// Margin currently locked by open positions.
    /// Required margin = lot size × contract size / leverage.
    /// </summary>
    public decimal MarginUsed      { get; set; }

    /// <summary>
    /// Free margin available for opening new positions.
    /// = <see cref="Equity"/> − <see cref="MarginUsed"/>.
    /// </summary>
    public decimal MarginAvailable { get; set; }

    /// <summary>
    /// When <c>true</c>, this account is the one actively used for order routing.
    /// Only one account should be active per broker at a time.
    /// </summary>
    public bool    IsActive        { get; set; }

    /// <summary>
    /// When <c>true</c>, orders routed to this account are simulated (paper trading)
    /// and not forwarded to the live broker infrastructure.
    /// </summary>
    public bool    IsPaper         { get; set; }

    /// <summary>UTC timestamp of the most recent successful balance/equity sync from the broker.</summary>
    public DateTime LastSyncedAt   { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted       { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The broker that holds this account.</summary>
    public virtual Broker Broker { get; set; } = null!;

    /// <summary>Orders placed through this account.</summary>
    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();
}
