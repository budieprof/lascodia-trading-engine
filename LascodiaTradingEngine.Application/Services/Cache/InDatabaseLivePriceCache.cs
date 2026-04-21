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
/// Prices older than <see cref="StalePriceTtl"/> are evicted and not returned by Get().
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class InDatabaseLivePriceCache : ILivePriceCache
{
    private static readonly TimeSpan StalePriceTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> _store = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InDatabaseLivePriceCache> _logger;
    private Timer? _evictionTimer;
    private int _persistFailures;        // Consecutive persist failures (for alerting)
    private DateTime _lastPersistWarn = DateTime.MinValue;
    // Count of consecutive eviction cycles in which ≥3 symbols went stale. Used to
    // distinguish a transient broker-connection flap (one cycle) from a sustained data
    // feed interruption (multiple consecutive cycles). Only the latter deserves a CRIT.
    private int _consecutiveMassEvictionCycles;

    public InDatabaseLivePriceCache(
        IServiceScopeFactory scopeFactory,
        ILogger<InDatabaseLivePriceCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;

        // Evict stale prices every 60 seconds
        _evictionTimer = new Timer(EvictStalePrices, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    // ── ILivePriceCache ──────────────────────────────────────────────────────

    public void Update(string symbol, decimal bid, decimal ask, DateTime timestamp)
    {
        _store[symbol] = (bid, ask, timestamp);
        // Fire-and-forget but surface errors so DB outages are detected
        _ = PersistAsync(symbol, bid, ask, timestamp).ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogError(t.Exception?.InnerException ?? t.Exception,
                    "LivePriceCache: persistence failed for {Symbol} — prices may be lost on restart", symbol);
        }, TaskScheduler.Default);
    }

    public (decimal Bid, decimal Ask, DateTime Timestamp)? Get(string symbol)
    {
        if (!_store.TryGetValue(symbol, out var price))
            return null;

        // Do not serve stale prices — treat as unavailable
        if (DateTime.UtcNow - price.Timestamp > StalePriceTtl)
            return null;

        return price;
    }

    public IReadOnlyDictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> GetAll()
        => _store;

    private void EvictStalePrices(object? state)
    {
        var cutoff = DateTime.UtcNow - StalePriceTtl;
        int evicted = 0;
        var staleSymbols = new List<string>();
        foreach (var kvp in _store)
        {
            if (kvp.Value.Timestamp < cutoff && _store.TryRemove(kvp.Key, out _))
            {
                evicted++;
                staleSymbols.Add(kvp.Key);
            }
        }
        if (evicted > 0)
        {
            _logger.LogWarning(
                "Evicted {Count} stale prices from live price cache (TTL={Ttl}). Symbols: {Symbols}",
                evicted, StalePriceTtl, string.Join(",", staleSymbols));

            // Escalate to CRIT only when mass evictions persist across two consecutive
            // cycles (≈2 × TTL ≈ 10 min of no ticks). A single mass-evict is usually a
            // broker-side connection flap that resolves within minutes — CRIT on every
            // one spams operator alerts with transient events.
            if (evicted >= 3)
            {
                _consecutiveMassEvictionCycles++;
                if (_consecutiveMassEvictionCycles >= 2)
                {
                    _logger.LogCritical(
                        "PRICE FEED ALERT: {Count} symbols simultaneously stale across {Cycles} cycles — " +
                        "likely EA disconnect or data feed interruption. Stale symbols: {Symbols}",
                        evicted, _consecutiveMassEvictionCycles, string.Join(",", staleSymbols));
                }
            }
            else
            {
                _consecutiveMassEvictionCycles = 0;
            }
        }
        else
        {
            // Any cycle with zero evictions means feed is healthy again — reset the counter.
            _consecutiveMassEvictionCycles = 0;
        }
    }

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
            Interlocked.Exchange(ref _persistFailures, 0); // Reset on success
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref _persistFailures);
            // Rate-limit warnings: log at most once per minute to avoid flooding
            if (failures >= 5 && (DateTime.UtcNow - _lastPersistWarn).TotalSeconds > 60)
            {
                _lastPersistWarn = DateTime.UtcNow;
                _logger.LogWarning("Live price persistence failing repeatedly ({Failures} consecutive failures). " +
                    "In-memory cache is still serving reads, but prices won't survive a restart. Last error: {Error}",
                    failures, ex.Message);
            }
            else
            {
                _logger.LogDebug(ex, "Failed to persist live price for {Symbol} (failure #{Count}).", symbol, failures);
            }
        }
    }
}
