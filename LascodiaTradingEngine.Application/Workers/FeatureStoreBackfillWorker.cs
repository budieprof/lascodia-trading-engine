using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Backfills the feature store for historical candles that lack feature vectors.
/// Uses <see cref="MLFeatureHelper.BuildFeatureVector"/> to compute the full 33-element
/// vector with proper lookback context, guaranteeing training/serving parity.
/// Runs on a configurable interval, processing candles in batches grouped by symbol/timeframe.
/// </summary>
public class FeatureStoreBackfillWorker : BackgroundService
{
    private readonly ILogger<FeatureStoreBackfillWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FeatureStoreOptions _options;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);
    private int _consecutiveFailures;

    public FeatureStoreBackfillWorker(
        ILogger<FeatureStoreBackfillWorker> logger,
        IServiceScopeFactory scopeFactory,
        FeatureStoreOptions options)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _options      = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FeatureStoreBackfillWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BackfillAsync(stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "FeatureStoreBackfillWorker error (failure #{Count})", _consecutiveFailures);
            }

            var baseInterval = TimeSpan.FromSeconds(_options.BackfillPollIntervalSeconds);
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    baseInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : baseInterval;

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task BackfillAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var featureStore = scope.ServiceProvider.GetRequiredService<IFeatureStore>();

        // Find closed candles that don't have a feature vector at the current schema version.
        // Previously this loaded every FeatureVector.CandleId into an in-memory HashSet
        // then loaded the first MaxCandlesPerRun closed Candles across ALL symbols/timeframes
        // and filtered in-app — a full-table scan on Candle (806 ms logged) that could return
        // zero unprocessed rows when the leading timestamps were already backfilled. This now
        // uses a NOT EXISTS / anti-join so Postgres rejects already-backfilled candles at the
        // query planner stage, letting the index on Timestamp short-circuit once enough
        // unprocessed rows are found.
        var schemaVersion = featureStore.CurrentSchemaVersion;
        var readDb = readCtx.GetDbContext();

        var toProcess = await readDb.Set<Candle>()
            .Where(c => c.IsClosed && !c.IsDeleted &&
                        !readDb.Set<FeatureVector>().Any(f =>
                            f.CandleId == c.Id &&
                            f.SchemaVersion == schemaVersion &&
                            !f.IsDeleted))
            .OrderBy(c => c.Timestamp)
            .Take(_options.BackfillBatchSize)
            .ToListAsync(ct);

        if (toProcess.Count == 0)
        {
            _logger.LogDebug("FeatureStoreBackfillWorker: no candles need backfill");
            return;
        }

        // Group by symbol/timeframe so we can bulk-load lookback context efficiently
        var groups = toProcess
            .GroupBy(c => (c.Symbol, c.Timeframe))
            .ToList();

        // Pre-load COT data per base currency (shared across all timeframes of the same symbol)
        var cotLookupCache = new Dictionary<string, CotFeatureLookupSnapshot>(StringComparer.OrdinalIgnoreCase);

        var vectors = new List<StoredFeatureVector>();
        int skippedInsufficient = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            var (symbol, timeframe) = group.Key;
            var groupCandles = group.OrderBy(c => c.Timestamp).ToList();

            // Load all candles for this symbol/timeframe in the range we need,
            // including lookback context before the earliest candle in the batch
            var latestTimestamp = groupCandles[groupCandles.Count - 1].Timestamp;

            var contextCandles = await readCtx.GetDbContext()
                .Set<Candle>()
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe
                         && c.IsClosed && !c.IsDeleted
                         && c.Timestamp <= latestTimestamp)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            // Build an index for fast positional lookup
            var candleIndex = new Dictionary<long, int>();
            for (int i = 0; i < contextCandles.Count; i++)
                candleIndex[contextCandles[i].Id] = i;

            // Load COT data for this symbol's base currency
            if (!cotLookupCache.TryGetValue(symbol, out var cotLookup))
            {
                cotLookup = await CotFeatureLookupSnapshot.LoadAsync(readCtx.GetDbContext(), symbol, ct);
                cotLookupCache[symbol] = cotLookup;
            }

            foreach (var candle in groupCandles)
            {
                if (!candleIndex.TryGetValue(candle.Id, out int idx)) continue;

                // Need at least LookbackWindow candles before current for the window,
                // plus the previous candle: idx >= LookbackWindow
                if (idx < MLFeatureHelper.LookbackWindow)
                {
                    skippedInsufficient++;
                    continue;
                }

                var current  = contextCandles[idx];
                var previous = contextCandles[idx - 1];
                // Window must include `previous` as its last element to match
                // BuildTrainingSamples: window = candles[i-LookbackWindow..i-1]
                var window   = contextCandles.GetRange(
                    idx - MLFeatureHelper.LookbackWindow,
                    MLFeatureHelper.LookbackWindow);

                try
                {
                    float[] floatFeatures = MLFeatureHelper.BuildFeatureVector(
                        window,
                        current,
                        previous,
                        cotLookup.Resolve(current.Timestamp));
                    double[] features = Array.ConvertAll(floatFeatures, f => (double)f);

                    vectors.Add(new StoredFeatureVector(
                        candle.Id, symbol, timeframe, candle.Timestamp,
                        features, featureStore.CurrentSchemaVersion,
                        MLFeatureHelper.ResolveFeatureNames(features.Length))
                    {
                        SchemaHash = featureStore.CurrentSchemaHash
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "FeatureStoreBackfillWorker: failed to compute features for candle {Id} ({Symbol}/{Tf})",
                        candle.Id, symbol, timeframe);
                }
            }
        }

        if (vectors.Count > 0)
            await featureStore.PersistBatchAsync(vectors, ct);

        _logger.LogInformation(
            "FeatureStoreBackfillWorker: backfilled {Count} candles, skipped {Skipped} (insufficient lookback)",
            vectors.Count, skippedInsufficient);
    }
}
