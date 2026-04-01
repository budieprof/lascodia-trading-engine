using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Backfills the feature store for historical candles that lack feature vectors.
/// Runs on a configurable interval, processing candles in batches.
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
        var featureStore = scope.ServiceProvider.GetRequiredService<Common.Interfaces.IFeatureStore>();

        // Find closed candles that don't have a feature vector at the current schema version
        var existingBarKeys = await readCtx.GetDbContext()
            .Set<FeatureVector>()
            .Where(f => f.SchemaVersion == featureStore.CurrentSchemaVersion && !f.IsDeleted)
            .Select(f => f.CandleId)
            .ToListAsync(ct);

        var existingSet = new HashSet<long>(existingBarKeys);

        var candlesNeedingFeatures = await readCtx.GetDbContext()
            .Set<Candle>()
            .Where(c => c.IsClosed && !c.IsDeleted)
            .OrderBy(c => c.Timestamp)
            .Take(_options.MaxCandlesPerRun)
            .ToListAsync(ct);

        var toProcess = candlesNeedingFeatures
            .Where(c => !existingSet.Contains(c.Id))
            .Take(_options.BackfillBatchSize)
            .ToList();

        if (toProcess.Count == 0)
        {
            _logger.LogDebug("FeatureStoreBackfillWorker: no candles need backfill");
            return;
        }

        // Build feature vectors — use a placeholder feature computation
        // In production, this would call MLFeatureHelper.BuildFeatureVector()
        var vectors = new List<StoredFeatureVector>();
        foreach (var candle in toProcess)
        {
            // Placeholder: store OHLCV as features. Real implementation would compute
            // the full 33-element vector using MLFeatureHelper with surrounding candle context.
            var features = new double[]
            {
                (double)candle.Open, (double)candle.High, (double)candle.Low,
                (double)candle.Close, (double)candle.Volume
            };

            vectors.Add(new StoredFeatureVector(
                candle.Id, candle.Symbol, candle.Timeframe, candle.Timestamp,
                features, featureStore.CurrentSchemaVersion,
                new[] { "Open", "High", "Low", "Close", "Volume" }));
        }

        await featureStore.PersistBatchAsync(vectors, ct);

        _logger.LogInformation("FeatureStoreBackfillWorker: backfilled {Count} candles", vectors.Count);
    }
}
