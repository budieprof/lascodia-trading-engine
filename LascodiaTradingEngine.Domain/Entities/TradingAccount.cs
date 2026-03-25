using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a single trading account connected to the engine via an EA instance on MetaTrader 5.
/// Stores broker identity, authentication credentials, and the current financial state
/// (balance, equity, margin) as last reported by the EA.
/// </summary>
/// <remarks>
/// Trading accounts are created when an EA registers via <c>POST /auth/register</c>.
/// The unique key is <see cref="AccountId"/> + <see cref="BrokerServer"/>.
/// Financial figures are refreshed from EA position/deal snapshots and used by the
/// risk engine to evaluate drawdown thresholds and position-sizing constraints.
/// </remarks>
public class TradingAccount : Entity<long>
{
    /// <summary>
    /// Broker-assigned account identifier (e.g. "12345678" from MT5 <c>AccountInfoInteger(ACCOUNT_LOGIN)</c>).
    /// Combined with <see cref="BrokerServer"/> to form the unique login key.
    /// </summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Human-readable display name (e.g. "Primary USD Live Account").</summary>
    public string AccountName { get; set; } = string.Empty;

    /// <summary>
    /// MT5 broker server identifier (e.g. "ICMarkets-Demo", "Pepperstone-Live").
    /// Read from <c>AccountInfoString(ACCOUNT_SERVER)</c> by the EA at registration.
    /// </summary>
    public string BrokerServer { get; set; } = string.Empty;

    /// <summary>Broker display name (e.g. "IC Markets", "Pepperstone").</summary>
    public string BrokerName { get; set; } = string.Empty;

    /// <summary>
    /// Account leverage ratio (e.g. 50 for 50:1).
    /// Used by the risk engine for margin calculations.
    /// </summary>
    public decimal Leverage { get; set; }

    /// <summary>
    /// The type of account on the broker side.
    /// Determines UI filtering and conditional logic in order routing.
    /// </summary>
    public AccountType AccountType { get; set; } = AccountType.Demo;

    /// <summary>
    /// The margin accounting mode configured on the broker.
    /// Affects how the engine interprets position netting vs hedging.
    /// </summary>
    public MarginMode MarginMode { get; set; } = MarginMode.Hedging;

    /// <summary>
    /// AES-256-GCM encrypted password for web dashboard authentication.
    /// Decryptable at runtime via <c>FieldEncryption</c>. Auto-generated on registration
    /// if not supplied; traders set their own via <c>PUT /trading-account/{id}/password</c>.
    /// </summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    /// <summary>
    /// AES-256-GCM encrypted API key for EA authentication.
    /// Auto-generated on registration. The EA receives the plain-text key in the
    /// registration response and must include it in all subsequent login requests.
    /// Rotatable via <c>POST /trading-account/{id}/rotate-api-key</c>.
    /// </summary>
    public string? EncryptedApiKey { get; set; }

    /// <summary>Account base currency code (e.g. "USD", "EUR", "GBP").</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// Cash balance in the account currency, excluding unrealised P&amp;L.
    /// Updated from EA position/deal snapshots.
    /// </summary>
    public decimal Balance { get; set; }

    /// <summary>
    /// Total equity = <see cref="Balance"/> + sum of all unrealised P&amp;L on open positions.
    /// Used by the risk engine for drawdown calculations.
    /// </summary>
    public decimal Equity { get; set; }

    /// <summary>
    /// Margin currently locked by open positions.
    /// Required margin = lot size × contract size / <see cref="Leverage"/>.
    /// </summary>
    public decimal MarginUsed { get; set; }

    /// <summary>
    /// Free margin available for opening new positions.
    /// = <see cref="Equity"/> − <see cref="MarginUsed"/>.
    /// </summary>
    public decimal MarginAvailable { get; set; }

    /// <summary>
    /// Margin level as a percentage (Equity / MarginUsed × 100).
    /// 0 when no open positions.
    /// </summary>
    public decimal MarginLevel { get; set; }

    /// <summary>Current floating (unrealised) profit or loss across all open positions.</summary>
    public decimal Profit { get; set; }

    /// <summary>Broker-granted credit on the account.</summary>
    public decimal Credit { get; set; }

    /// <summary>
    /// Stop-out mode configured by the broker: "Percent" or "Money".
    /// Determines how <see cref="MarginSoCall"/> and <see cref="MarginSoStopOut"/> are interpreted.
    /// </summary>
    public string MarginSoMode { get; set; } = "Percent";

    /// <summary>Margin call trigger level (percent or money depending on <see cref="MarginSoMode"/>).</summary>
    public decimal MarginSoCall { get; set; }

    /// <summary>Stop-out trigger level (percent or money depending on <see cref="MarginSoMode"/>).</summary>
    public decimal MarginSoStopOut { get; set; }

    /// <summary>
    /// When <c>true</c>, this account is actively used for order routing and strategy evaluation.
    /// Only one account should be active at a time.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Maximum absolute monetary loss allowed in a single trading day (in account currency).
    /// When the day's loss reaches this amount, the risk engine blocks further orders regardless of
    /// percentage-based drawdown limits. Zero means no absolute daily loss cap is enforced.
    /// </summary>
    public decimal MaxAbsoluteDailyLoss { get; set; }

    /// <summary>
    /// When <c>true</c>, orders are simulated and not forwarded to the broker.
    /// </summary>
    public bool IsPaper { get; set; }

    /// <summary>UTC timestamp of the most recent successful state sync from the EA.</summary>
    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token. Prevents lost updates when multiple EA instances
    /// or concurrent requests modify the same account's financial state simultaneously.
    /// </summary>
    public uint RowVersion { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>Orders placed through this account.</summary>
    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    /// <summary>EA instances connected to this account.</summary>
    public virtual ICollection<EAInstance> EAInstances { get; set; } = new List<EAInstance>();
}
