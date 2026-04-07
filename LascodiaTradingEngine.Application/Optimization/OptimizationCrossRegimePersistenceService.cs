using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class OptimizationCrossRegimePersistenceService
{
    private readonly OptimizationValidator _validator;
    private readonly OptimizationApprovalArtifactStore _artifactStore;
    private readonly ILogger<OptimizationCrossRegimePersistenceService> _logger;

    public OptimizationCrossRegimePersistenceService(
        OptimizationValidator validator,
        OptimizationApprovalArtifactStore artifactStore,
        ILogger<OptimizationCrossRegimePersistenceService> logger)
    {
        _validator = validator;
        _artifactStore = artifactStore;
        _logger = logger;
    }

    internal async Task PersistAsync(
        OptimizationRun run,
        Strategy strategy,
        ApprovalConfig config,
        MarketRegimeEnum? currentRegime,
        DateTime candleLookbackStart,
        BacktestOptions screeningOptions,
        string approvedParamsJson,
        DbContext db,
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        CancellationToken ct,
        CancellationToken runCt)
    {
        if (!currentRegime.HasValue)
            return;

        using var crossRegimeCts = CancellationTokenSource.CreateLinkedTokenSource(runCt);
        crossRegimeCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(2, config.MaxRunTimeoutMinutes / 4)));
        var crossRegimeCt = crossRegimeCts.Token;

        try
        {
            var lookbackEndUtc = DateTime.UtcNow;
            var snapshotHistory = await db.Set<MarketRegimeSnapshot>()
                .Where(s => s.Symbol == strategy.Symbol
                         && s.Timeframe == strategy.Timeframe
                         && s.DetectedAt <= lookbackEndUtc
                         && !s.IsDeleted)
                .OrderBy(s => s.DetectedAt)
                .ToListAsync(crossRegimeCt);

            var otherRegimes = snapshotHistory
                .Where(s => s.DetectedAt >= candleLookbackStart && s.Regime != currentRegime.Value)
                .Select(s => s.Regime)
                .Distinct()
                .Take(config.MaxCrossRegimeEvals)
                .ToList();

            var lookbackCandles = await db.Set<Candle>()
                .Where(c => c.Symbol == strategy.Symbol
                         && c.Timeframe == strategy.Timeframe
                         && c.Timestamp >= candleLookbackStart
                         && c.Timestamp <= lookbackEndUtc
                         && c.IsClosed
                         && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(crossRegimeCt);

            var regimeCandleSets = new List<(MarketRegimeEnum Regime, List<Candle> Candles)>();
            foreach (var otherRegime in otherRegimes)
            {
                try
                {
                    var regimeIntervals = OptimizationRegimeIntervalBuilder.BuildRegimeIntervals(
                        snapshotHistory,
                        otherRegime,
                        candleLookbackStart,
                        lookbackEndUtc);
                    if (regimeIntervals.Count == 0)
                        continue;

                    var regimeCandles = OptimizationRegimeIntervalBuilder.FilterCandlesByIntervals(lookbackCandles, regimeIntervals);
                    if (regimeCandles.Count >= 50)
                        regimeCandleSets.Add((otherRegime, regimeCandles));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "OptimizationCrossRegimePersistenceService: cross-regime candle load for {Regime} failed (non-fatal)",
                        otherRegime);
                }
            }

            var crossRegimeResults = new List<(MarketRegimeEnum Regime, decimal Score)>();
            var crossRegimeLock = new object();
            await Parallel.ForEachAsync(regimeCandleSets, new ParallelOptions
            {
                MaxDegreeOfParallelism = config.MaxParallelBacktests,
                CancellationToken = crossRegimeCt,
            }, async (entry, pCt) =>
            {
                try
                {
                    var regimeResult = await _validator.RunWithTimeoutAsync(
                        strategy,
                        approvedParamsJson,
                        entry.Candles,
                        screeningOptions,
                        config.ScreeningTimeoutSeconds,
                        pCt);
                    decimal regimeScore = OptimizationHealthScorer.ComputeHealthScore(regimeResult);
                    lock (crossRegimeLock)
                        crossRegimeResults.Add((entry.Regime, regimeScore));
                }
                catch (OperationCanceledException) when (pCt.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "OptimizationCrossRegimePersistenceService: cross-regime evaluation for {Regime} failed (non-fatal)",
                        entry.Regime);
                }
            });

            foreach (var (regime, regimeScore) in crossRegimeResults)
            {
                if (regimeScore >= config.AutoApprovalMinHealthScore * 0.80m)
                {
                    await _artifactStore.SaveRegimeParamsAsync(
                        writeDb,
                        writeCtx,
                        strategy,
                        run,
                        approvedParamsJson,
                        regimeScore,
                        regimeScore * 0.85m,
                        regime,
                        ct);
                    _logger.LogDebug(
                        "OptimizationCrossRegimePersistenceService: cross-regime save for {Symbol}/{Regime} — score={Score:F2}",
                        strategy.Symbol,
                        regime,
                        regimeScore);
                }
            }
        }
        catch (OperationCanceledException) when (crossRegimeCts.IsCancellationRequested && !runCt.IsCancellationRequested)
        {
            _logger.LogInformation(
                "OptimizationCrossRegimePersistenceService: run {RunId} cross-regime evaluation timed out ({Limit}min) — primary regime params already saved, continuing with follow-ups",
                run.Id,
                Math.Max(2, config.MaxRunTimeoutMinutes / 4));
        }
        catch (OperationCanceledException) when (runCt.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OptimizationRunProgressTracker.RecordOperationalIssue(
                run,
                "CrossRegimePersistenceFailed",
                $"Cross-regime evaluation degraded after approval: {ex.Message}",
                DateTime.UtcNow);

            await writeCtx.SaveChangesAsync(CancellationToken.None);
            _logger.LogWarning(ex,
                "OptimizationCrossRegimePersistenceService: cross-regime persistence failed for approved run {RunId} (non-fatal)",
                run.Id);
        }
    }
}
