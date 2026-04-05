using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Security;

/// <summary>
/// Security boundary that validates EA instance ownership before allowing data ingestion
/// or command execution.
///
/// Every EA-facing endpoint (tick streaming, candle ingestion, heartbeat, command acknowledgement,
/// etc.) must verify that the authenticated caller actually owns the EA instance they reference.
/// Without this guard, a compromised or misconfigured EA could spoof another account's instance ID
/// and inject false market data, acknowledge commands meant for a different instance, or read
/// pending commands belonging to another trader.
///
/// The guard extracts the caller's identity from the JWT <c>tradingAccountId</c> claim (set at
/// login/registration) and compares it against the <see cref="Domain.Entities.EAInstance.TradingAccountId"/>
/// stored in the database. This is a read-only check — it never modifies state.
///
/// Usage pattern in command handlers:
/// <code>
///   if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, ct))
///       return ResponseData.Init(null, false, "Unauthorized", "-403");
/// </code>
///
/// Registered as Scoped in DI (depends on <see cref="IHttpContextAccessor"/> and
/// <see cref="IReadApplicationDbContext"/>, both scoped).
/// </summary>
public interface IEAOwnershipGuard
{
    /// <summary>
    /// Extracts the <c>tradingAccountId</c> claim from the current HTTP request's JWT token.
    /// Returns <c>null</c> if the claim is missing, the user is unauthenticated, or the claim
    /// value is not a valid <see cref="long"/>. Callers should treat a <c>null</c> result as
    /// "no authenticated trading account" and deny the operation.
    /// </summary>
    long? GetCallerAccountId();

    /// <summary>
    /// Checks whether the EA instance identified by <paramref name="instanceId"/> belongs to
    /// the currently authenticated trading account.
    ///
    /// Returns <c>false</c> in any of these cases:
    /// <list type="bullet">
    ///   <item>No <c>tradingAccountId</c> claim in the JWT (unauthenticated or malformed token).</item>
    ///   <item>No EA instance found with the given <paramref name="instanceId"/> (never registered).</item>
    ///   <item>The instance exists but is soft-deleted (<see cref="Domain.Entities.EAInstance.IsDeleted"/>).</item>
    ///   <item>The instance belongs to a different trading account.</item>
    /// </list>
    ///
    /// The query uses <c>AsNoTracking</c> for performance since this is a read-only authorization
    /// check that runs on every EA request.
    /// </summary>
    Task<bool> IsOwnerAsync(string instanceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IEAOwnershipGuard"/>.
///
/// Resolves the caller's trading account from the JWT and performs a single read-only DB
/// lookup to verify EA instance ownership. The query hits <see cref="IReadApplicationDbContext"/>
/// (not the write context) to avoid polluting the change tracker on high-frequency paths like
/// tick ingestion.
///
/// Thread safety: this class is stateless beyond its injected dependencies. Multiple concurrent
/// requests each get their own scoped instance via DI, so there are no shared-state concerns.
/// </summary>
public class EAOwnershipGuard : IEAOwnershipGuard
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IReadApplicationDbContext _readContext;

    public EAOwnershipGuard(
        IHttpContextAccessor httpContextAccessor,
        IReadApplicationDbContext readContext)
    {
        _httpContextAccessor = httpContextAccessor;
        _readContext = readContext;
    }

    /// <inheritdoc />
    public long? GetCallerAccountId()
    {
        // The "tradingAccountId" claim is embedded in the JWT by TradingAccountTokenGenerator
        // during login or registration. It uniquely identifies the trading account (not the trader)
        // because a single trader can have multiple accounts across different brokers.
        var claim = _httpContextAccessor.HttpContext?.User?.FindFirst("tradingAccountId");

        // Defensive parse: if the claim exists but contains a non-numeric value (shouldn't happen
        // with our token generator, but guards against tampered tokens), return null rather than throw.
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }

    /// <inheritdoc />
    public async Task<bool> IsOwnerAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        // Step 1: Extract caller identity from JWT. If missing, fail fast — no DB round-trip needed.
        var callerAccountId = GetCallerAccountId();
        if (callerAccountId is null)
            return false;

        // Step 2: Look up the EA instance by its string-based InstanceId (assigned by the EA at
        // registration time, typically a GUID or composite like "EURUSD-12345"). The soft-delete
        // filter (!IsDeleted) ensures deregistered instances can't be impersonated.
        // AsNoTracking: this is a read-only authorization check — no need to track the entity.
        var instance = await _readContext.GetDbContext()
            .Set<Domain.Entities.EAInstance>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.InstanceId == instanceId && !x.IsDeleted,
                cancellationToken);

        // Step 3: Compare the instance's owning account against the caller's account.
        // Null-safe: if instance is null (not found / deleted), the comparison yields false.
        return instance?.TradingAccountId == callerAccountId;
    }
}
