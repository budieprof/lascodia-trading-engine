using System.Diagnostics;
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
/// On startup, pre-loads all active ML models and runs a real inference pass with
/// actual candle data to fully warm the JIT, model deserialization cache, and feature
/// engineering code paths. Eliminates first-tick cold-start latency (~200ms).
/// Validates that each model can score successfully before live trading begins.
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
        // Allow other services (DB connections, event bus) to initialize first
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        _logger.LogInformation("MLInferenceWarmupWorker: warming up ML model cache");
        var sw = Stopwatch.StartNew();

        int warmed = 0, failed = 0, skippedNoCandles = 0;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var mlScorer = scope.ServiceProvider.GetRequiredService<IMLSignalScorer>();
            var readDb   = readCtx.GetDbContext();

            var activeModels = await readDb
                .Set<MLModel>()
                .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
                .Select(m => new { m.Id, m.Symbol, m.Timeframe })
                .ToListAsync(stoppingToken);

            _logger.LogInformation(
                "MLInferenceWarmupWorker: found {Count} active models to warm up",
                activeModels.Count);

            // Group by symbol/timeframe to share candle loads across models on the same pair
            var groups = activeModels.GroupBy(m => (m.Symbol, m.Timeframe));

            foreach (var group in groups)
            {
                if (stoppingToken.IsCancellationRequested) break;

                var (symbol, timeframe) = group.Key;

                // Load real candles for this symbol/timeframe (need LookbackWindow + 2)
                int requiredCandles = MLFeatureHelper.LookbackWindow + 2;
                var candles = await readDb
                    .Set<Candle>()
                    .Where(c => c.Symbol == symbol && c.Timeframe == timeframe
                             && c.IsClosed && !c.IsDeleted)
                    .OrderByDescending(c => c.Timestamp)
                    .Take(requiredCandles)
                    .ToListAsync(stoppingToken);

                if (candles.Count < requiredCandles)
                {
                    skippedNoCandles += group.Count();
                    _logger.LogDebug(
                        "MLInferenceWarmupWorker: skipping {Symbol}/{Tf} — only {Count}/{Required} candles available",
                        symbol, timeframe, candles.Count, requiredCandles);
                    continue;
                }

                // Reverse to chronological order
                candles.Reverse();

                foreach (var model in group)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    var modelSw = Stopwatch.StartNew();
                    try
                    {
                        var dummySignal = new TradeSignal
                        {
                            Symbol           = model.Symbol,
                            Direction        = TradeDirection.Buy,
                            EntryPrice       = candles[^1].Close,
                            SuggestedLotSize = 0.01m
                        };

                        // Score with real candle data to exercise the full code path:
                        // model deserialization → feature engineering → inference → calibration
                        await mlScorer.ScoreAsync(dummySignal, candles, stoppingToken);
                        warmed++;

                        _logger.LogDebug(
                            "MLInferenceWarmupWorker: warmed model {Id} ({Symbol}/{Tf}) in {Ms}ms",
                            model.Id, model.Symbol, model.Timeframe, modelSw.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex,
                            "MLInferenceWarmupWorker: warm-up scoring failed for model {Id} ({Symbol}/{Tf}). " +
                            "Model may have stale snapshot or incompatible feature schema.",
                            model.Id, model.Symbol, model.Timeframe);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MLInferenceWarmupWorker: warm-up aborted due to critical error");
        }

        sw.Stop();
        _logger.LogInformation(
            "MLInferenceWarmupWorker: completed in {Elapsed}ms — warmed={Warmed}, failed={Failed}, skipped={Skipped}",
            sw.ElapsedMilliseconds, warmed, failed, skippedNoCandles);

        if (failed > 0)
            _logger.LogWarning(
                "MLInferenceWarmupWorker: {Failed} models failed warm-up scoring — check model snapshots",
                failed);

        // This worker runs once and exits — it does not loop
    }
}
