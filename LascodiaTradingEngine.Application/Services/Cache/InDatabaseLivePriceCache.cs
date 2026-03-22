using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.Cache;

/// <summary>
/// Write-through live price cache.
/// Reads are served from an in-memory ConcurrentDictionary (zero latency).
/// Every Update() also fire-and-forgets an upsert to the database so the
/// latest prices survive a process restart.
/// Call InitializeAsync() at startup to pre-warm the dictionary from the DB.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class InDatabaseLivePriceCache : ILivePriceCache
{
    private readonly ConcurrentDictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> _store = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InDatabaseLivePriceCache> _logger;

    public InDatabaseLivePriceCache(
        IServiceScopeFactory scopeFactory,
        ILogger<InDatabaseLivePriceCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    // ── ILivePriceCache ──────────────────────────────────────────────────────

    public void Update(string symbol, decimal bid, decimal ask, DateTime timestamp)
    {
        _store[symbol] = (bid, ask, timestamp);
        _ = PersistAsync(symbol, bid, ask, timestamp);   // fire-and-forget
    }

    public (decimal Bid, decimal Ask, DateTime Timestamp)? Get(string symbol)
        => _store.TryGetValue(symbol, out var price) ? price : null;

    public IReadOnlyDictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> GetAll()
        => _store;

    // ── Startup warm-up ──────────────────────────────────────────────────────

    /// <summary>
    /// Pre-warms the in-memory store from the database.
    /// Call once from the application host startup before the market-data worker starts.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var rows = await readContext.GetDbContext()
            .Set<LivePrice>()
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
            _store[row.Symbol] = (row.Bid, row.Ask, row.Timestamp);

        _logger.LogInformation("InDatabaseLivePriceCache pre-warmed with {Count} symbols.", rows.Count);
    }

    // ── Internal DB upsert ───────────────────────────────────────────────────

    private async Task PersistAsync(string symbol, decimal bid, decimal ask, DateTime timestamp)
    {
        try
        {
            using var scope        = _scopeFactory.CreateScope();
            var writeContext       = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var db                 = writeContext.GetDbContext();

            var existing = await db.Set<LivePrice>()
                .FirstOrDefaultAsync(x => x.Symbol == symbol);

            if (existing is null)
            {
                await db.Set<LivePrice>().AddAsync(
                    new LivePrice { Symbol = symbol, Bid = bid, Ask = ask, Timestamp = timestamp });
            }
            else
            {
                existing.Bid       = bid;
                existing.Ask       = ask;
                existing.Timestamp = timestamp;
            }

            await writeContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist live price for {Symbol}.", symbol);
        }
    }
}
