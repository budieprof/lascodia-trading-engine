using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Pre-computes the full 33-element ML feature vector for active symbol/timeframe
/// combinations after each candle close. Uses <see cref="MLFeatureHelper.BuildFeatureVector"/>
/// to guarantee training/serving parity. Reduces ML scoring latency by having features
/// ready before the next tick arrives.
/// </summary>
public class FeaturePreComputationWorker : BackgroundService
{
    private readonly ILogger<FeaturePreComputationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private const int DefaultPollSeconds = 60;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private int _consecutiveFailures;

    public FeaturePreComputationWorker(
        ILogger<FeaturePreComputationWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FeaturePreComputationWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PreComputeFeaturesAsync(stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "FeaturePreComputationWorker error (failure #{Count})", _consecutiveFailures);
            }

            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(DefaultPollSeconds * Math.Pow(2, _consecutiveFailures - 1), MaxBackoff.TotalSeconds))
                : TimeSpan.FromSeconds(DefaultPollSeconds);
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task PreComputeFeaturesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var featureStore = scope.ServiceProvider.GetRequiredService<IFeatureStore>();

        // Find active strategy symbol/timeframe combinations
        var activeSymbolTimeframes = await readCtx.GetDbContext()
            .Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .Select(s => new { s.Symbol, s.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        int preComputed = 0;
        int skippedInsufficient = 0;

        foreach (var pair in activeSymbolTimeframes)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                // Load the lookback window + 2 (current + previous) of closed candles
                var candles = await readCtx.GetDbContext()
                    .Set<Candle>()
                    .Where(c => c.Symbol == pair.Symbol && c.Timeframe == pair.Timeframe
                             && c.IsClosed && !c.IsDeleted)
                    .OrderByDescending(c => c.Timestamp)
                    .Take(MLFeatureHelper.LookbackWindow + 2)
                    .ToListAsync(ct);

                // Need at least LookbackWindow + 2 candles (window + current + previous)
                if (candles.Count < MLFeatureHelper.LookbackWindow + 2)
                {
                    skippedInsufficient++;
                    continue;
                }

                // Reverse to chronological order
                candles.Reverse();

                var current  = candles[^1];
                var previous = candles[^2];
                // Window must include `previous` as its last element to match
                // BuildTrainingSamples: window = candles[i-LookbackWindow..i-1]
                var window   = candles.GetRange(candles.Count - 1 - MLFeatureHelper.LookbackWindow,
                                                MLFeatureHelper.LookbackWindow);

                // Check if feature vector already exists for this candle
                var existing = await featureStore.GetAsync(
                    pair.Symbol, pair.Timeframe, current.Timestamp, ct);

                if (existing is not null) continue;

                // Load COT data for the base currency (if available)
                var cotEntry = await LoadCotEntryAsync(readCtx, pair.Symbol, ct);

                // Build the full 33-element feature vector using the shared helper
                float[] floatFeatures = MLFeatureHelper.BuildFeatureVector(window, current, previous, cotEntry);
                double[] features = Array.ConvertAll(floatFeatures, f => (double)f);

                await featureStore.PersistAsync(new StoredFeatureVector(
                    current.Id, pair.Symbol, pair.Timeframe, current.Timestamp,
                    features, featureStore.CurrentSchemaVersion,
                    MLFeatureHelper.FeatureNames), ct);

                preComputed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FeaturePreComputationWorker: failed to compute features for {Symbol}/{Tf}",
                    pair.Symbol, pair.Timeframe);
            }
        }

        if (preComputed > 0 || skippedInsufficient > 0)
            _logger.LogInformation(
                "FeaturePreComputationWorker: pre-computed {Count} feature vectors, skipped {Skipped} (insufficient candles)",
                preComputed, skippedInsufficient);
    }

    /// <summary>
    /// Loads the latest COT report for the base currency of the given symbol.
    /// Returns <see cref="CotFeatureEntry.Zero"/> if no report is available.
    /// </summary>
    private static async Task<CotFeatureEntry> LoadCotEntryAsync(
        IReadApplicationDbContext readCtx, string symbol, CancellationToken ct)
    {
        if (symbol.Length < 3) return CotFeatureEntry.Zero;

        string baseCurrency = symbol[..3];

        var latestCot = await readCtx.GetDbContext()
            .Set<COTReport>()
            .Where(c => c.Currency == baseCurrency && !c.IsDeleted)
            .OrderByDescending(c => c.ReportDate)
            .FirstOrDefaultAsync(ct);

        if (latestCot is null) return CotFeatureEntry.Zero;

        // Load the previous week's report for momentum calculation
        var previousCot = await readCtx.GetDbContext()
            .Set<COTReport>()
            .Where(c => c.Currency == baseCurrency && !c.IsDeleted
                     && c.ReportDate < latestCot.ReportDate)
            .OrderByDescending(c => c.ReportDate)
            .FirstOrDefaultAsync(ct);

        float netNorm = (float)(latestCot.NetNonCommercialPositioning / 100_000m);
        float momentum = previousCot is not null
            ? (float)((latestCot.NetNonCommercialPositioning - previousCot.NetNonCommercialPositioning) / 10_000m)
            : 0f;

        return new CotFeatureEntry(
            Math.Clamp(netNorm, -3f, 3f),
            Math.Clamp(momentum, -3f, 3f),
            HasData: true);
    }
}
