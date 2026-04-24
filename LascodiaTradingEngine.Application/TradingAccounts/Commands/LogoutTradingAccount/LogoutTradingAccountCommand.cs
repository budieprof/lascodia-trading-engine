using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.LogoutTradingAccount;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Revokes a single JWT by its <c>jti</c>. The next time the token is presented to the
/// API it will be rejected by the JWT validation pipeline. Idempotent — a duplicate logout
/// for the same <c>jti</c> succeeds without error so the client can retry safely.
/// </summary>
/// <remarks>
/// The controller extracts <see cref="Jti"/>, <see cref="ExpiresAt"/> and
/// <see cref="TradingAccountId"/> from the calling principal so the API surface stays simple
/// (no body — just hit <c>POST /auth/logout</c> with the bearer token attached).
/// </remarks>
public class LogoutTradingAccountCommand : IRequest<ResponseData<string>>
{
    /// <summary>The <c>jti</c> claim value of the token to revoke.</summary>
    public string   Jti              { get; set; } = string.Empty;

    /// <summary>FK to the trading account the token was issued for.</summary>
    public long     TradingAccountId { get; set; }

    /// <summary>Original UTC expiry of the token, copied so the cleanup worker can GC it.</summary>
    public DateTime ExpiresAt        { get; set; }

    /// <summary>Optional reason — defaults to <c>"UserLogout"</c>.</summary>
    public string?  Reason           { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Inserts a <see cref="RevokedToken"/> row and warms the in-process MemoryCache so the
/// next request on this engine instance short-circuits without a DB hit.
/// </summary>
public class LogoutTradingAccountCommandHandler
    : IRequestHandler<LogoutTradingAccountCommand, ResponseData<string>>
{
    /// <summary>
    /// Cache-key prefix for the per-jti revocation hot layer. Kept here so the validation
    /// hook in <c>Program.cs</c> and this handler share a single source of truth.
    /// </summary>
    public const string RevokedCacheKeyPrefix = "revoked-jti:";

    /// <summary>How long the hot-layer cache entry lives. Five minutes matches the design doc.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IWriteApplicationDbContext _context;
    private readonly IMemoryCache _cache;

    public LogoutTradingAccountCommandHandler(IWriteApplicationDbContext context, IMemoryCache cache)
    {
        _context = context;
        _cache   = cache;
    }

    public async Task<ResponseData<string>> Handle(
        LogoutTradingAccountCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Jti))
            return ResponseData<string>.Init(null, false, "Missing token id (jti).", "-11");

        var db  = _context.GetDbContext();
        var set = db.Set<RevokedToken>();

        var alreadyRevoked = await set
            .AsNoTracking()
            .AnyAsync(x => x.Jti == request.Jti, cancellationToken);

        if (!alreadyRevoked)
        {
            set.Add(new RevokedToken
            {
                Jti              = request.Jti,
                TradingAccountId = request.TradingAccountId,
                ExpiresAt        = request.ExpiresAt,
                RevokedAt        = DateTime.UtcNow,
                Reason           = string.IsNullOrWhiteSpace(request.Reason) ? "UserLogout" : request.Reason,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        // Hot-layer cache so the validation hook on this instance doesn't need a DB hit
        // for the next 5 minutes. Other instances will pay one DB lookup until their
        // own caches warm.
        _cache.Set(RevokedCacheKeyPrefix + request.Jti, true, CacheTtl);

        return ResponseData<string>.Init("Logged out", true, "Successful", "00");
    }
}
