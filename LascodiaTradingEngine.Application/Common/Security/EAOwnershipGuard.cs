using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Security;

/// <summary>
/// Validates that the authenticated user (via JWT tradingAccountId claim) owns the
/// EA instance identified by <paramref name="instanceId"/>. Prevents cross-account
/// manipulation of EA instances.
/// </summary>
public interface IEAOwnershipGuard
{
    /// <summary>
    /// Returns the TradingAccountId from the JWT, or null if the claim is missing.
    /// </summary>
    long? GetCallerAccountId();

    /// <summary>
    /// Returns true if the EA instance belongs to the caller's trading account.
    /// Returns false if the instance does not exist, is deleted, or belongs to another account.
    /// </summary>
    Task<bool> IsOwnerAsync(string instanceId, CancellationToken cancellationToken = default);
}

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

    public long? GetCallerAccountId()
    {
        var claim = _httpContextAccessor.HttpContext?.User?.FindFirst("tradingAccountId");
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }

    public async Task<bool> IsOwnerAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var callerAccountId = GetCallerAccountId();
        if (callerAccountId is null)
            return false;

        var instance = await _readContext.GetDbContext()
            .Set<Domain.Entities.EAInstance>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.InstanceId == instanceId && !x.IsDeleted,
                cancellationToken);

        return instance?.TradingAccountId == callerAccountId;
    }
}
