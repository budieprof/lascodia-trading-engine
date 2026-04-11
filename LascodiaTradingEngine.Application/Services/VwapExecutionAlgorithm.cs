using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Tick-Activity-Weighted Average Price execution: distributes child orders
/// proportionally to a historical intraday tick-activity profile.
/// <para>
/// Forex is a decentralised OTC market with no consolidated volume tape. MT5's
/// "tick volume" (<c>TickRecord.TickVolume</c>) represents the number of price changes
/// at the broker, NOT actual traded notional. Academic research (Easley, López de Prado,
/// O'Hara 2012) shows tick activity correlates ~0.85 with true interbank volume for
/// major pairs, making it the best available proxy for retail forex VWAP.
/// </para>
/// When empirical tick data is available for the symbol (from the last 30 days of
/// TickRecord), builds a data-driven hourly activity profile. Falls back to a static
/// U-shaped profile when insufficient data exists.
/// Registered as Singleton — uses IServiceScopeFactory for DB access and a
/// ConcurrentDictionary cache to avoid synchronous async calls in GenerateSlices.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class VwapExecutionAlgorithm : IExecutionAlgorithm
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VwapExecutionAlgorithm> _logger;

    /// <summary>Cached per-symbol volume profiles with timestamp for 24h expiry.</summary>
    private readonly ConcurrentDictionary<string, CachedProfile> _profileCache = new();

    /// <summary>Tracks symbols currently being refreshed to avoid duplicate refresh tasks.</summary>
    private readonly ConcurrentDictionary<string, bool> _refreshInProgress = new();

    /// <summary>Minimum tick count required to build an empirical profile.</summary>
    private const int MinTickThreshold = 1000;

    /// <summary>How long to cache an empirical profile before re-querying.</summary>
    private static readonly TimeSpan ProfileCacheDuration = TimeSpan.FromHours(24);

    /// <summary>Proactive refresh threshold: trigger background refresh when cache age exceeds 80% of TTL.</summary>
    private static readonly TimeSpan ProactiveRefreshThreshold = TimeSpan.FromHours(20);

    public ExecutionAlgorithmType AlgorithmType => ExecutionAlgorithmType.VWAP;

    /// <summary>
    /// Default intraday volume profile weights (24 hourly buckets, normalized).
    /// U-shaped: higher at session opens/closes, lower mid-day.
    /// Used when insufficient tick data is available for a symbol.
    /// </summary>
    private static readonly decimal[] DefaultVolumeProfile =
    [
        0.06m, 0.04m, 0.03m, 0.03m, 0.03m, 0.03m, 0.03m, 0.04m, // 00:00–07:00 (Asian)
        0.06m, 0.07m, 0.06m, 0.05m, 0.04m, 0.05m, 0.06m, 0.07m, // 08:00–15:00 (London)
        0.06m, 0.05m, 0.04m, 0.03m, 0.03m, 0.02m, 0.02m, 0.03m  // 16:00–23:00 (NY close)
    ];

    public VwapExecutionAlgorithm(
        IServiceScopeFactory scopeFactory,
        ILogger<VwapExecutionAlgorithm> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Refreshes the cached hourly volume profile for a symbol by querying the last 30 days
    /// of TickRecord data. If fewer than <see cref="MinTickThreshold"/> ticks exist, the
    /// cache entry is set to <see cref="DefaultVolumeProfile"/>.
    /// Call this periodically (e.g. daily) or before the first VWAP execution for a symbol.
    /// </summary>
    public async Task RefreshProfileAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

            var cutoff = DateTime.UtcNow.AddDays(-30);

            var ticks = await readContext.GetDbContext()
                .Set<TickRecord>()
                .Where(t => t.Symbol == symbol && t.TickTimestamp >= cutoff && !t.IsDeleted)
                .Select(t => new { t.TickTimestamp.Hour, t.TickVolume })
                .ToListAsync(cancellationToken);

            if (ticks.Count < MinTickThreshold)
            {
                _logger.LogDebug(
                    "VWAP: insufficient tick data for {Symbol} ({Count} < {Threshold}), using default profile",
                    symbol, ticks.Count, MinTickThreshold);
                _profileCache[symbol] = new CachedProfile(DefaultVolumeProfile, DateTime.UtcNow);
                return;
            }

            // Build empirical hourly activity profile from broker tick volume.
            // NOTE: Forex has no consolidated volume tape. TickVolume is the broker's
            // tick count (price changes per interval), which is a proxy for true volume
            // (correlation ~0.85 for major pairs). This is the industry-standard approach
            // for retail forex VWAP when exchange-level volume data is unavailable.
            var hourlyVolume = new decimal[24];
            foreach (var tick in ticks)
            {
                hourlyVolume[tick.Hour] += tick.TickVolume > 0 ? tick.TickVolume : 1;
            }

            // Normalize to sum to 1.0
            decimal total = hourlyVolume.Sum();
            if (total > 0)
            {
                for (int i = 0; i < 24; i++)
                    hourlyVolume[i] /= total;
            }
            else
            {
                _profileCache[symbol] = new CachedProfile(DefaultVolumeProfile, DateTime.UtcNow);
                return;
            }

            _profileCache[symbol] = new CachedProfile(hourlyVolume, DateTime.UtcNow);
            _logger.LogInformation(
                "VWAP: refreshed empirical volume profile for {Symbol} from {Count} ticks",
                symbol, ticks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VWAP: failed to refresh profile for {Symbol}, using default", symbol);
            _profileCache[symbol] = new CachedProfile(DefaultVolumeProfile, DateTime.UtcNow);
        }
    }

    public IReadOnlyList<ChildOrderSlice> GenerateSlices(
        Order parentOrder,
        int sliceCount,
        int durationSeconds,
        decimal currentPrice,
        decimal lotStep = 0.01m)
    {
        if (sliceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sliceCount), "Slice count must be positive");
        if (durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be positive");
        if (lotStep <= 0)
            lotStep = 0.01m; // Fallback to 0.01 if invalid (default for most forex brokers)

        var totalQuantity = parentOrder.Quantity;
        var intervalMs    = (long)durationSeconds * 1000 / sliceCount;
        var startTime     = DateTime.UtcNow;

        // Use cached empirical profile if fresh, otherwise fall back to default
        var volumeProfile = DefaultVolumeProfile;
        if (_profileCache.TryGetValue(parentOrder.Symbol, out var cached)
            && DateTime.UtcNow - cached.ComputedAt < ProfileCacheDuration)
        {
            volumeProfile = cached.HourlyWeights;

            // Proactive background refresh: when cache age exceeds 80% of TTL,
            // trigger an async refresh so the next access gets fresh data without waiting
            var cacheAge = DateTime.UtcNow - cached.ComputedAt;
            if (cacheAge > ProactiveRefreshThreshold && !_refreshInProgress.ContainsKey(parentOrder.Symbol))
            {
                _refreshInProgress[parentOrder.Symbol] = true;
                _ = Task.Run(async () =>
                {
                    try { await RefreshProfileAsync(parentOrder.Symbol); }
                    finally { _refreshInProgress.TryRemove(parentOrder.Symbol, out _); }
                });
            }
        }

        // Extract the relevant portion of the volume profile for this execution window
        var weights   = new decimal[sliceCount];
        decimal weightSum = 0;

        for (int i = 0; i < sliceCount; i++)
        {
            var sliceTime  = startTime.AddMilliseconds(intervalMs * i);
            var hourBucket = sliceTime.Hour % 24;
            weights[i]     = volumeProfile[hourBucket];
            weightSum     += weights[i];
        }

        // Normalize and allocate quantities
        var slices     = new List<ChildOrderSlice>(sliceCount);
        decimal allocated = 0;

        for (int i = 0; i < sliceCount; i++)
        {
            decimal qty;
            if (i == sliceCount - 1)
            {
                // Last slice gets remainder to ensure exact total
                qty = totalQuantity - allocated;
            }
            else
            {
                // Round down to broker lot step (from CurrencyPair.VolumeStep, default 0.01)
                qty = weightSum > 0
                    ? Math.Floor(totalQuantity * weights[i] / weightSum / lotStep) * lotStep
                    : Math.Floor(totalQuantity / sliceCount / lotStep) * lotStep;
            }

            if (qty <= 0) continue;

            allocated += qty;
            slices.Add(new ChildOrderSlice(
                SliceIndex: i,
                Quantity: qty,
                LimitPrice: null,
                ScheduledAt: startTime.AddMilliseconds(intervalMs * i)));
        }

        Debug.Assert(
            slices.Sum(s => s.Quantity) == totalQuantity,
            $"VWAP sliced quantity {slices.Sum(s => s.Quantity)} does not match parent order quantity {totalQuantity}");

        return slices;
    }

    // TODO: Future enhancement — implement real-time VWAP tracking that adjusts remaining
    // slice quantities based on actual vs expected volume participation during execution.
    // If actual volume is running ahead of the profile, subsequent slices should be reduced,
    // and vice versa. This requires a stateful execution context per active VWAP order.

    private sealed record CachedProfile(decimal[] HourlyWeights, DateTime ComputedAt);
}
