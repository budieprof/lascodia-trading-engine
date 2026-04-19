using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Snapshot of the two <c>DrawdownRecovery</c> EngineConfig values the signal-generation
/// hot path needs. <c>Mode</c> mirrors <c>DrawdownRecovery:ActiveMode</c>; <c>ReducedLotMultiplier</c>
/// mirrors <c>DrawdownRecovery:ReducedLotMultiplier</c>. Defaults are applied here so
/// callers never need to re-parse or re-default.
/// </summary>
public sealed record DrawdownRecoverySnapshot(string Mode, decimal ReducedLotMultiplier)
{
    public bool IsReduced => string.Equals(Mode, "Reduced", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Caches the <c>DrawdownRecovery:*</c> EngineConfig values so the signal-generation hot
/// path (<see cref="Workers.StrategyWorker"/>) does not hit the database twice per signal.
///
/// Mode transitions are rare (driven by <see cref="Workers.DrawdownRecoveryWorker"/> on a
/// ~30s poll) and the worker calls <see cref="Invalidate"/> whenever it writes a new mode,
/// so the cache tracks truth closely: event-driven when the engine flips, TTL-bounded
/// otherwise.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public sealed class DrawdownRecoveryModeProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;

    private const string CacheKey = "drawdown-recovery-mode";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly DrawdownRecoverySnapshot DefaultSnapshot = new("Normal", 0.5m);

    public DrawdownRecoveryModeProvider(IServiceScopeFactory scopeFactory, IMemoryCache cache)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
    }

    public async Task<DrawdownRecoverySnapshot> GetAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out DrawdownRecoverySnapshot? cached) && cached is not null)
            return cached;

        using var scope = _scopeFactory.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        // Single round-trip: fetch both keys in one query rather than two sequential ones.
        var rows = await readContext.GetDbContext()
            .Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => !c.IsDeleted &&
                        (c.Key == "DrawdownRecovery:ActiveMode" ||
                         c.Key == "DrawdownRecovery:ReducedLotMultiplier"))
            .Select(c => new { c.Key, c.Value })
            .ToListAsync(ct);

        var mode = rows.FirstOrDefault(r => r.Key == "DrawdownRecovery:ActiveMode")?.Value ?? "Normal";
        var multiplier = DefaultSnapshot.ReducedLotMultiplier;
        var rawMultiplier = rows.FirstOrDefault(r => r.Key == "DrawdownRecovery:ReducedLotMultiplier")?.Value;
        if (rawMultiplier is not null && decimal.TryParse(rawMultiplier, out var parsed) && parsed > 0)
            multiplier = parsed;

        var snapshot = new DrawdownRecoverySnapshot(mode, multiplier);
        _cache.Set(CacheKey, snapshot, CacheTtl);
        return snapshot;
    }

    /// <summary>
    /// Drops the cached snapshot so the next <see cref="GetAsync"/> re-reads from the DB.
    /// Called from <see cref="Workers.DrawdownRecoveryWorker"/> after it persists a mode change.
    /// </summary>
    public void Invalidate() => _cache.Remove(CacheKey);
}
