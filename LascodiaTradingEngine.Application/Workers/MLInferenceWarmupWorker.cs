using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// On startup, pre-loads all active ML models and runs a dummy inference to warm the JIT
/// and model deserialization cache. Eliminates first-tick cold-start latency (~200ms).
/// Runs once on startup, then exits.
/// </summary>
public class MLInferenceWarmupWorker : BackgroundService
{
    private readonly ILogger<MLInferenceWarmupWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public MLInferenceWarmupWorker(
        ILogger<MLInferenceWarmupWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay to let other services initialize first
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        _logger.LogInformation("MLInferenceWarmupWorker: warming up ML model cache");
        var sw = Stopwatch.StartNew();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

            // Load all active models
            var activeModels = await readCtx.GetDbContext()
                .Set<MLModel>()
                .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
                .Select(m => new { m.Id, m.Symbol, m.Timeframe })
                .ToListAsync(stoppingToken);

            _logger.LogInformation(
                "MLInferenceWarmupWorker: found {Count} active models to warm up",
                activeModels.Count);

            // For each active model, trigger a cache load by resolving the scorer
            // The MLSignalScorer's snapshot cache is populated on first access per model
            var mlScorer = scope.ServiceProvider.GetRequiredService<IMLSignalScorer>();

            foreach (var model in activeModels)
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    // Create a minimal dummy signal to trigger model loading
                    var dummySignal = new TradeSignal
                    {
                        Symbol    = model.Symbol,
                        Direction = TradeDirection.Buy,
                        EntryPrice = 1.0m,
                        SuggestedLotSize = 0.01m
                    };

                    // Score with empty candle list — the scorer will load and cache the model
                    // snapshot even if scoring fails due to insufficient candles
                    await mlScorer.ScoreAsync(dummySignal, Array.Empty<Candle>(), stoppingToken);
                }
                catch
                {
                    // Expected: scoring may fail with empty candles, but the model is now cached
                }
            }

            sw.Stop();
            _logger.LogInformation(
                "MLInferenceWarmupWorker: warmed {Count} models in {Elapsed}ms",
                activeModels.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MLInferenceWarmupWorker: warm-up failed");
        }

        // This worker runs once and exits — it does not loop
    }
}
