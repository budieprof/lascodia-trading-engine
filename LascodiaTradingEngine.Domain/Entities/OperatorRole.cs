using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Grants a single role to a <see cref="TradingAccount"/>. An account can hold many rows
/// here (one per role); the issued JWT carries the union as <c>ClaimTypes.Role</c> claims.
/// </summary>
/// <remarks>
/// Role names are kept as plain strings rather than an enum so that future role additions
/// don't require a coordinated entity-shape migration. The full canonical set lives in
/// <c>OperatorRoles.AllRoles</c> in the Application layer.
/// </remarks>
public class OperatorRole : Entity<long>
{
    /// <summary>FK to the trading account this role is granted to.</summary>
    public long     TradingAccountId    { get; set; }

    /// <summary>
    /// Role name. Canonical values: <c>Viewer</c>, <c>Trader</c>, <c>Analyst</c>,
    /// <c>Operator</c>, <c>Admin</c>, <c>EA</c>.
    /// </summary>
    public string   Role                { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the role was granted.</summary>
    public DateTime AssignedAt          { get; set; } = DateTime.UtcNow;

    /// <summary>FK to the account that performed the assignment. Null for system seeds.</summary>
    public long?    AssignedByAccountId { get; set; }

    /// <summary>Soft-delete flag — revoking a role flips this rather than dropping the row.</summary>
    public bool     IsDeleted           { get; set; }
}
