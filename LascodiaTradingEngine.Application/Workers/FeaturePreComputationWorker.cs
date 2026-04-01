using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Pre-computes feature vectors for active symbol/timeframe combinations after each candle close.
/// Reduces ML scoring latency by having features ready before the next tick arrives.
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

        foreach (var pair in activeSymbolTimeframes)
        {
            if (ct.IsCancellationRequested) break;

            // Get the latest closed candle for this symbol/timeframe
            var latestCandle = await readCtx.GetDbContext()
                .Set<Candle>()
                .Where(c => c.Symbol == pair.Symbol && c.Timeframe == pair.Timeframe
                         && c.IsClosed && !c.IsDeleted)
                .OrderByDescending(c => c.Timestamp)
                .FirstOrDefaultAsync(ct);

            if (latestCandle is null) continue;

            // Check if feature vector already exists for this candle
            var existing = await featureStore.GetAsync(
                pair.Symbol, pair.Timeframe, latestCandle.Timestamp, ct);

            if (existing is not null) continue;

            // Build and persist feature vector (placeholder: OHLCV features)
            var features = new double[]
            {
                (double)latestCandle.Open, (double)latestCandle.High,
                (double)latestCandle.Low, (double)latestCandle.Close,
                (double)latestCandle.Volume
            };

            await featureStore.PersistAsync(new StoredFeatureVector(
                latestCandle.Id, pair.Symbol, pair.Timeframe, latestCandle.Timestamp,
                features, featureStore.CurrentSchemaVersion,
                new[] { "Open", "High", "Low", "Close", "Volume" }), ct);

            preComputed++;
        }

        if (preComputed > 0)
            _logger.LogInformation("FeaturePreComputationWorker: pre-computed {Count} feature vectors", preComputed);
    }
}
