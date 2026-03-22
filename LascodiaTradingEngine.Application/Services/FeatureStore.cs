using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Pre-computed feature store with strict point-in-time (PIT) guarantees.
/// Features are computed once when a candle closes and stored with the candle's
/// timestamp. Retrieval always returns features as-of a given timestamp, preventing
/// look-ahead bias during backtesting and enabling instant retraining.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Write path:</b> <c>MarketDataWorker</c> calls <see cref="ComputeAndStoreAsync"/>
///         after each candle close. Features are computed from candles available at that moment only.</item>
///   <item><b>Read path (training):</b> <c>MLTrainingWorker</c> calls <see cref="GetFeaturesAsync"/>
///         with a date range. Returns pre-computed features without recomputing from candles.</item>
///   <item><b>Read path (inference):</b> <c>MLSignalScorer</c> calls <see cref="GetLatestAsync"/>
///         for the most recent feature vector.</item>
///   <item><b>PIT guarantee:</b> each stored feature row includes <c>ComputedAt</c> (wall clock)
///         and <c>CandleCloseTime</c> (market time). Training queries filter by <c>CandleCloseTime</c>
///         to ensure no future data leaks into past features.</item>
/// </list>
/// </remarks>
public interface IFeatureStore
{
    /// <summary>
    /// Computes and stores the feature vector for the just-closed candle.
    /// Called by MarketDataWorker after ingesting a new candle.
    /// </summary>
    Task ComputeAndStoreAsync(
        string symbol, Timeframe timeframe, DateTime candleCloseTime, CancellationToken ct);

    /// <summary>
    /// Retrieves pre-computed features for a symbol/timeframe within a date range.
    /// Returns features in chronological order, each tagged with its candle close time.
    /// </summary>
    Task<List<StoredFeature>> GetFeaturesAsync(
        string symbol, Timeframe timeframe, DateTime from, DateTime to, CancellationToken ct);

    /// <summary>
    /// Returns the most recent feature vector for live inference.
    /// </summary>
    Task<StoredFeature?> GetLatestAsync(string symbol, Timeframe timeframe, CancellationToken ct);
}

/// <summary>A pre-computed feature vector with its point-in-time metadata.</summary>
public sealed record StoredFeature(
    string    Symbol,
    Timeframe Timeframe,
    DateTime  CandleCloseTime,
    DateTime  ComputedAt,
    float[]   Features,
    float[][]? SequenceFeatures);

[RegisterService(ServiceLifetime.Singleton)]
public sealed class FeatureStore : IFeatureStore
{
    // In-memory cache of recent features (last 100 per symbol/tf) for fast inference
    private readonly ConcurrentDictionary<string, LinkedList<StoredFeature>> _cache = new();
    private const int MaxCachePerKey = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FeatureStore> _logger;

    public FeatureStore(
        IServiceScopeFactory scopeFactory,
        ILogger<FeatureStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task ComputeAndStoreAsync(
        string symbol, Timeframe timeframe, DateTime candleCloseTime, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readDb      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var ctx         = readDb.GetDbContext();

        // Load candles up to (and including) the just-closed candle — PIT guarantee
        var candles = await ctx.Set<Candle>()
            .Where(c => c.Symbol    == symbol &&
                        c.Timeframe == timeframe &&
                        c.Timestamp <= candleCloseTime &&
                        !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .Take(MLFeatureHelper.LookbackWindow + 2)
            .AsNoTracking()
            .ToListAsync(ct);

        candles.Reverse(); // chronological order

        if (candles.Count < MLFeatureHelper.LookbackWindow + 2)
        {
            _logger.LogDebug(
                "FeatureStore: insufficient candles for {Symbol}/{Tf} at {Time} ({N} < {Required})",
                symbol, timeframe, candleCloseTime, candles.Count, MLFeatureHelper.LookbackWindow + 2);
            return;
        }

        // Compute feature vector from candles available at this point in time
        var samples = MLFeatureHelper.BuildTrainingSamples(candles);
        if (samples.Count == 0)
            return;

        var lastSample = samples[^1];
        var computedAt = DateTime.UtcNow;

        // Build sequence features if enough data
        float[][]? seqFeatures = null;
        if (candles.Count >= MLFeatureHelper.LookbackWindow)
        {
            var window = candles[^MLFeatureHelper.LookbackWindow..];
            seqFeatures = MLFeatureHelper.BuildSequenceFeatures(window);
        }

        var stored = new StoredFeature(
            symbol, timeframe, candleCloseTime, computedAt,
            lastSample.Features, seqFeatures);

        // Cache in memory
        string cacheKey = $"{symbol}|{timeframe}";
        var list = _cache.GetOrAdd(cacheKey, _ => new LinkedList<StoredFeature>());
        lock (list)
        {
            list.AddLast(stored);
            while (list.Count > MaxCachePerKey)
                list.RemoveFirst();
        }

        _logger.LogDebug(
            "FeatureStore: stored {Symbol}/{Tf} features at {Time} (F={FC}, seq={HasSeq})",
            symbol, timeframe, candleCloseTime, lastSample.Features.Length, seqFeatures is not null);
    }

    public Task<List<StoredFeature>> GetFeaturesAsync(
        string symbol, Timeframe timeframe, DateTime from, DateTime to, CancellationToken ct)
    {
        string cacheKey = $"{symbol}|{timeframe}";

        if (!_cache.TryGetValue(cacheKey, out var list))
            return Task.FromResult(new List<StoredFeature>());

        List<StoredFeature> result;
        lock (list)
        {
            result = list
                .Where(f => f.CandleCloseTime >= from && f.CandleCloseTime <= to)
                .OrderBy(f => f.CandleCloseTime)
                .ToList();
        }

        return Task.FromResult(result);
    }

    public Task<StoredFeature?> GetLatestAsync(string symbol, Timeframe timeframe, CancellationToken ct)
    {
        string cacheKey = $"{symbol}|{timeframe}";

        if (!_cache.TryGetValue(cacheKey, out var list))
            return Task.FromResult<StoredFeature?>(null);

        StoredFeature? latest;
        lock (list)
        {
            latest = list.Last?.Value;
        }

        return Task.FromResult(latest);
    }
}
