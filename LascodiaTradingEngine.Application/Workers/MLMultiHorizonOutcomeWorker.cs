using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Resolves the candle-based multi-horizon outcome fields on
/// <see cref="MLModelPredictionLog"/>: <c>HorizonCorrect3</c>,
/// <c>HorizonCorrect6</c>, and <c>HorizonCorrect12</c>.
///
/// The primary prediction outcome worker resolves the next-bar/trade-level result.
/// This worker separately asks whether the model's predicted direction was still
/// correct after 3, 6, and 12 closed candles, which feeds
/// <see cref="MLHorizonAccuracyWorker"/>.
/// </summary>
public sealed class MLMultiHorizonOutcomeWorker : BackgroundService
{
    private const string CK_PollSecs     = "MLMultiHorizon:PollIntervalSeconds";
    private const string CK_BatchSize    = "MLMultiHorizon:BatchSize";
    private const string CK_MaxGapFactor = "MLMultiHorizon:MaxCandleGapFactor";

    internal static readonly int[] Horizons = [3, 6, 12];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLMultiHorizonOutcomeWorker> _logger;

    public MLMultiHorizonOutcomeWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLMultiHorizonOutcomeWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLMultiHorizonOutcomeWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 300;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readCtx = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs = NormalizePollSeconds(
                    await GetConfigAsync<int>(readCtx, CK_PollSecs, 300, stoppingToken));
                int batchSize = NormalizeBatchSize(
                    await GetConfigAsync<int>(readCtx, CK_BatchSize, 500, stoppingToken));
                double maxGapFactor = NormalizeGapFactor(
                    await GetConfigAsync<double>(readCtx, CK_MaxGapFactor, 3.0, stoppingToken));

                int resolved = await ResolveHorizonsAsync(readCtx, writeCtx, batchSize, maxGapFactor, stoppingToken);

                if (resolved > 0)
                    _logger.LogInformation("MLMultiHorizonOutcomeWorker: resolved {Count} horizon fields.", resolved);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLMultiHorizonOutcomeWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLMultiHorizonOutcomeWorker stopping.");
    }

    internal async Task<int> ResolveHorizonsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int batchSize,
        double maxGapFactor,
        CancellationToken ct)
    {
        batchSize = NormalizeBatchSize(batchSize);
        maxGapFactor = NormalizeGapFactor(maxGapFactor);

        DateTime cutoff = DateTime.UtcNow - (TimeframeDurationHelper.BarDuration(Timeframe.D1) * 12) - TimeSpan.FromMinutes(5);

        var candidates = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => !l.IsDeleted &&
                        (l.HorizonCorrect3 == null ||
                         l.HorizonCorrect6 == null ||
                         l.HorizonCorrect12 == null) &&
                        l.PredictedAt <= DateTime.UtcNow.AddMinutes(-3))
            .OrderBy(l => l.PredictedAt)
            .Take(batchSize)
            .AsNoTracking()
            .Select(l => new
            {
                l.Id,
                l.Symbol,
                l.Timeframe,
                l.PredictedAt,
                l.PredictedDirection,
                l.HorizonCorrect3,
                l.HorizonCorrect6,
                l.HorizonCorrect12,
            })
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        int resolvedFields = 0;

        foreach (var group in candidates.GroupBy(l => (l.Symbol, l.Timeframe)))
        {
            ct.ThrowIfCancellationRequested();

            var (symbol, timeframe) = group.Key;
            TimeSpan expectedGap = TimeframeDurationHelper.BarDuration(timeframe);
            TimeSpan minimumAge = expectedGap * Horizons.Max() + TimeSpan.FromMinutes(5);
            DateTime groupCutoff = DateTime.UtcNow.Subtract(minimumAge);

            var logs = group
                .Where(l => l.PredictedAt <= groupCutoff || l.PredictedAt <= cutoff)
                .OrderBy(l => l.PredictedAt)
                .ToList();

            if (logs.Count == 0) continue;

            DateTime earliest = logs.First().PredictedAt.Subtract(expectedGap * 2);
            DateTime latest = logs.Last().PredictedAt.Add(expectedGap * (Horizons.Max() + 2));

            var candles = await readCtx.Set<Candle>()
                .Where(c => c.Symbol == symbol &&
                            c.Timeframe == timeframe &&
                            c.Timestamp >= earliest &&
                            c.Timestamp <= latest &&
                            c.IsClosed &&
                            !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .AsNoTracking()
                .ToListAsync(ct);

            if (candles.Count < Horizons.Min() + 1) continue;

            foreach (var log in logs)
            {
                var prev = candles.LastOrDefault(c => c.Timestamp <= log.PredictedAt);
                if (prev is null) continue;

                bool? h3 = log.HorizonCorrect3;
                bool? h6 = log.HorizonCorrect6;
                bool? h12 = log.HorizonCorrect12;

                h3 ??= ResolveHorizon(log.PredictedDirection, prev, candles, 3, expectedGap, maxGapFactor);
                h6 ??= ResolveHorizon(log.PredictedDirection, prev, candles, 6, expectedGap, maxGapFactor);
                h12 ??= ResolveHorizon(log.PredictedDirection, prev, candles, 12, expectedGap, maxGapFactor);

                if (h3 == log.HorizonCorrect3 &&
                    h6 == log.HorizonCorrect6 &&
                    h12 == log.HorizonCorrect12)
                    continue;

                int updated = await writeCtx.Set<MLModelPredictionLog>()
                    .Where(l => l.Id == log.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.HorizonCorrect3, h3)
                        .SetProperty(l => l.HorizonCorrect6, h6)
                        .SetProperty(l => l.HorizonCorrect12, h12),
                        ct);

                if (updated > 0)
                {
                    if (log.HorizonCorrect3 == null && h3 != null) resolvedFields++;
                    if (log.HorizonCorrect6 == null && h6 != null) resolvedFields++;
                    if (log.HorizonCorrect12 == null && h12 != null) resolvedFields++;
                }
            }
        }

        return resolvedFields;
    }

    internal static bool? ResolveHorizon(
        TradeDirection predictedDirection,
        Candle baseline,
        IReadOnlyList<Candle> sortedCandles,
        int horizonBars,
        TimeSpan expectedGap,
        double maxGapFactor)
    {
        var future = sortedCandles
            .Where(c => c.Timestamp > baseline.Timestamp)
            .OrderBy(c => c.Timestamp)
            .Take(horizonBars)
            .ToList();

        if (future.Count < horizonBars) return null;

        DateTime previousTimestamp = baseline.Timestamp;
        double maxGapMinutes = expectedGap.TotalMinutes * maxGapFactor;

        foreach (var candle in future)
        {
            double gapMinutes = (candle.Timestamp - previousTimestamp).TotalMinutes;
            if (gapMinutes > maxGapMinutes) return null;
            previousTimestamp = candle.Timestamp;
        }

        decimal move = future[^1].Close - baseline.Close;
        if (move == 0m) return false;

        return predictedDirection == TradeDirection.Buy
            ? move > 0m
            : move < 0m;
    }

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    internal static int NormalizePollSeconds(int value)
        => value is >= 1 and <= 86_400 ? value : 300;

    internal static int NormalizeBatchSize(int value)
        => value is >= 1 and <= 10_000 ? value : 500;

    internal static double NormalizeGapFactor(double value)
        => double.IsFinite(value) && value >= 1.0 && value <= 20.0 ? value : 3.0;
}
