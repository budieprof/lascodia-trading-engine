using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records a JWT that has been explicitly revoked before its natural expiry.
/// Looked up in the JWT validation pipeline; if a token's <c>jti</c> matches a row here,
/// the request is rejected as if the signature were invalid.
/// </summary>
/// <remarks>
/// The <see cref="ExpiresAt"/> column lets a daily cleanup job drop rows whose underlying
/// tokens would have expired anyway, keeping the table small and the per-request lookup cheap.
/// Soft-delete is not applied — revoked tokens are either active or garbage-collected.
/// </remarks>
public class RevokedToken : Entity<long>
{
    /// <summary>The <c>jti</c> claim of the revoked JWT (a Guid string).</summary>
    public string   Jti              { get; set; } = string.Empty;

    /// <summary>FK to the <see cref="TradingAccount"/> the token was issued for.</summary>
    public long     TradingAccountId { get; set; }

    /// <summary>UTC expiry of the revoked token. After this point the row may be GC'd.</summary>
    public DateTime ExpiresAt        { get; set; }

    /// <summary>UTC time at which the revocation was recorded.</summary>
    public DateTime RevokedAt        { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Free-form reason — <c>"UserLogout"</c>, <c>"AdminForceLogout"</c>, <c>"Compromised"</c>, etc.
    /// Used for audit and operator forensics.
    /// </summary>
    public string?  Reason           { get; set; }
}
