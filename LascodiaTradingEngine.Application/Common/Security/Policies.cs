using Microsoft.AspNetCore.Authorization;

namespace LascodiaTradingEngine.Application.Common.Security;

/// <summary>
/// Canonical operator-role names used across the engine. Keep the set small and stable —
/// permissions evolve via policy mappings, not by inventing new role strings.
/// </summary>
public static class OperatorRoleNames
{
    /// <summary>Read-only access to dashboards, lists, and detail pages.</summary>
    public const string Viewer   = nameof(Viewer);

    /// <summary>Manage own account, approve/reject signals, pause/activate strategies.</summary>
    public const string Trader   = nameof(Trader);

    /// <summary>Trigger ML training, hyperparameter searches, queue backtests, walk-forwards.</summary>
    public const string Analyst  = nameof(Analyst);

    /// <summary>Engine config, kill switches, EA management, batch cancel, ML rollback, paper toggle, risk profile CRUD.</summary>
    public const string Operator = nameof(Operator);

    /// <summary>Everything Operator can do, plus role assignment / revocation.</summary>
    public const string Admin    = nameof(Admin);

    /// <summary>EA-specific role — bypasses the operator policy ladder so EA tokens always pass.</summary>
    public const string EA       = nameof(EA);

    /// <summary>All canonical roles. Anything not in this set should be considered an unknown / stale grant.</summary>
    public static readonly IReadOnlyList<string> AllRoles =
    [
        Viewer, Trader, Analyst, Operator, Admin, EA,
    ];

    /// <summary>True when <paramref name="role"/> is one of the canonical role names (case-insensitive).</summary>
    public static bool IsCanonical(string? role) =>
        !string.IsNullOrWhiteSpace(role) &&
        AllRoles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Authorization policy names + registration helper. Policies cascade — Admin satisfies every
/// policy below it, Operator satisfies Trader/Analyst/Viewer, and so on. EA always passes the
/// Viewer policy so EA-scoped read endpoints stay accessible.
/// </summary>
public static class Policies
{
    public const string Viewer   = nameof(Viewer);
    public const string Trader   = nameof(Trader);
    public const string Analyst  = nameof(Analyst);
    public const string Operator = nameof(Operator);
    public const string Admin    = nameof(Admin);

    /// <summary>Adds the policy ladder to <paramref name="o"/>. Idempotent.</summary>
    public static void Register(AuthorizationOptions o)
    {
        o.AddPolicy(Viewer,   p => p.RequireRole(
            OperatorRoleNames.Viewer, OperatorRoleNames.Trader, OperatorRoleNames.Analyst,
            OperatorRoleNames.Operator, OperatorRoleNames.Admin, OperatorRoleNames.EA));

        o.AddPolicy(Trader,   p => p.RequireRole(
            OperatorRoleNames.Trader, OperatorRoleNames.Operator, OperatorRoleNames.Admin));

        o.AddPolicy(Analyst,  p => p.RequireRole(
            OperatorRoleNames.Analyst, OperatorRoleNames.Operator, OperatorRoleNames.Admin));

        o.AddPolicy(Operator, p => p.RequireRole(
            OperatorRoleNames.Operator, OperatorRoleNames.Admin));

        o.AddPolicy(Admin,    p => p.RequireRole(OperatorRoleNames.Admin));
    }
}
