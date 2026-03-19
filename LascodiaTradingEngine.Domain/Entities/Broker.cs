using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a configured broker integration — the connection credentials and current
/// health state for a single broker account (e.g. Oanda, FXCM, Interactive Brokers).
/// </summary>
/// <remarks>
/// Only one broker should have <see cref="IsActive"/> = <c>true</c> at any time.
/// The <c>BrokerFailoverService</c> manages runtime switching: it deactivates all brokers
/// and activates the target, setting its <see cref="Status"/> to
/// <see cref="BrokerStatus.Connected"/>. The <c>BrokerHealthWorker</c> periodically
/// checks connectivity and updates <see cref="Status"/> accordingly.
/// </remarks>
public class Broker : Entity<long>
{
    /// <summary>Human-readable display name for this broker configuration (e.g. "Oanda Practice").</summary>
    public string  Name          { get; set; } = string.Empty;

    /// <summary>
    /// The broker vendor this record represents.
    /// Determines which adapter implementation (<c>IOandaAdapter</c>, etc.) is used.
    /// </summary>
    public BrokerType  BrokerType    { get; set; } = BrokerType.Oanda;

    /// <summary>
    /// Whether this is a live (<c>Live</c>) or practice (<c>Practice</c>) environment.
    /// Practice environments simulate trading without real capital.
    /// </summary>
    public BrokerEnvironment  Environment   { get; set; } = BrokerEnvironment.Practice;

    /// <summary>
    /// The REST API base URL for this broker environment.
    /// e.g. <c>https://api-fxtrade.oanda.com</c> for Oanda live.
    /// </summary>
    public string  BaseUrl       { get; set; } = string.Empty;

    /// <summary>
    /// API key / access token used to authenticate requests to the broker.
    /// Stored encrypted at rest; never logged.
    /// </summary>
    public string? ApiKey        { get; set; }

    /// <summary>
    /// API secret or secondary credential for brokers that use two-part authentication.
    /// Null for brokers that use a single API key.
    /// </summary>
    public string? ApiSecret     { get; set; }

    /// <summary>
    /// When <c>true</c>, this is the currently active broker that the engine routes
    /// all orders and price feeds through. Exactly one broker should be active at a time.
    /// </summary>
    public bool    IsActive      { get; set; }

    /// <summary>
    /// When <c>true</c>, all orders placed through this broker are simulated (paper trading)
    /// regardless of the <see cref="Environment"/> setting.
    /// </summary>
    public bool    IsPaper       { get; set; }

    /// <summary>
    /// Current connectivity and operational state of the broker integration.
    /// Updated by the broker health worker and the failover service.
    /// </summary>
    public BrokerStatus  Status        { get; set; } = BrokerStatus.Disconnected;

    /// <summary>
    /// Optional human-readable message describing the current status or last error.
    /// e.g. "Connection timed out after 30s" or "Rate limit exceeded".
    /// </summary>
    public string? StatusMessage { get; set; }

    /// <summary>UTC timestamp when this broker record was created.</summary>
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted     { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>Trading accounts held with this broker.</summary>
    public virtual ICollection<TradingAccount> TradingAccounts { get; set; } = new List<TradingAccount>();
}
