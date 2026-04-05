using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

[RegisterService(ServiceLifetime.Scoped, typeof(ISpreadProfileProvider))]
public class SpreadProfileProvider : ISpreadProfileProvider
{
    private readonly IReadApplicationDbContext _db;
    private readonly ILivePriceCache _livePriceCache;
    private readonly ILogger<SpreadProfileProvider> _logger;

    public SpreadProfileProvider(
        IReadApplicationDbContext db,
        ILivePriceCache livePriceCache,
        ILogger<SpreadProfileProvider> logger)
    {
        _db = db;
        _livePriceCache = livePriceCache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SpreadProfile>> GetProfilesAsync(string symbol, CancellationToken ct)
    {
        return await _db.GetDbContext()
            .Set<SpreadProfile>()
            .Where(p => p.Symbol == symbol && !p.IsDeleted)
            .ToListAsync(ct);
    }

    public Func<DateTime, decimal>? BuildSpreadFunction(string symbol, IReadOnlyList<SpreadProfile> profiles)
    {
        if (string.IsNullOrWhiteSpace(symbol) || profiles.Count == 0)
            return null;

        // Build lookup: (HourUtc, DayOfWeek?) -> SpreadP50
        var lookup = new Dictionary<(int Hour, DayOfWeek? Day), decimal>();
        foreach (var p in profiles)
        {
            lookup[(p.HourUtc, p.DayOfWeek)] = p.SpreadP50;
        }

        // Capture references for the closure
        var cache = _livePriceCache;
        var sym = symbol;

        return (DateTime barTimestamp) =>
        {
            // Try exact match: (Hour, DayOfWeek)
            if (lookup.TryGetValue((barTimestamp.Hour, barTimestamp.DayOfWeek), out decimal spread))
                return spread;

            // Fallback: all-day aggregate (Hour, null)
            if (lookup.TryGetValue((barTimestamp.Hour, null), out spread))
                return spread;

            // Fallback: live cache if price is fresh (< 2 hours)
            var live = cache.Get(sym);
            if (live.HasValue && (DateTime.UtcNow - live.Value.Timestamp).TotalHours < 2)
                return live.Value.Ask - live.Value.Bid;

            // Nothing found — return 0 so caller falls back to fixed SpreadPriceUnits
            return 0m;
        };
    }
}
