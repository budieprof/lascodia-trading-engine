using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes per-UTC-hour direction accuracy for each active ML model and persists
/// the results as <see cref="MLModelHourlyAccuracy"/> rows (24 buckets per model).
///
/// <b>Motivation:</b> The four-bucket <c>MLSessionAccuracyWorker</c> (Asian / London /
/// LondonNYOverlap / NewYork) is too coarse for intraday signal scheduling.
/// A model may perform well during the London session overall (07:00–12:59 UTC)
/// but be consistently wrong during the first hour (07:00 UTC) due to gap-open
/// volatility — an effect that is diluted in the session average. At hourly
/// granularity this weakness is directly visible and can be acted on.
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLHourlyAccuracy:PollIntervalSeconds</c> — default 3600 (1 h)</item>
///   <item><c>MLHourlyAccuracy:WindowDays</c>          — look-back window, default 30</item>
///   <item><c>MLHourlyAccuracy:MinPredictions</c>      — minimum per hour bucket, default 10</item>
/// </list>
/// </summary>
public sealed class MLTimeOfDayAccuracyWorker : BackgroundService
{
    private const string CK_PollSecs       = "MLHourlyAccuracy:PollIntervalSeconds";
    private const string CK_WindowDays     = "MLHourlyAccuracy:WindowDays";
    private const string CK_MinPredictions = "MLHourlyAccuracy:MinPredictions";

    private readonly IServiceScopeFactory                  _scopeFactory;
    private readonly ILogger<MLTimeOfDayAccuracyWorker>    _logger;

    public MLTimeOfDayAccuracyWorker(
        IServiceScopeFactory                   scopeFactory,
        ILogger<MLTimeOfDayAccuracyWorker>     logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLTimeOfDayAccuracyWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await ComputeHourlyAccuracyAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLTimeOfDayAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLTimeOfDayAccuracyWorker stopping.");
    }

    // ── Computation core ──────────────────────────────────────────────────────

    private async Task ComputeHourlyAccuracyAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int windowDays     = await GetConfigAsync<int>(readCtx, CK_WindowDays,     30, ct);
        int minPredictions = await GetConfigAsync<int>(readCtx, CK_MinPredictions, 10, ct);

        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ComputeForModelAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    windowStart, minPredictions,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "HourlyAccuracy: computation failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task ComputeForModelAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        DateTime                                windowStart,
        int                                     minPredictions,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId      &&
                        l.DirectionCorrect != null          &&
                        l.PredictedAt      >= windowStart   &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => new { l.PredictedAt, l.DirectionCorrect })
            .ToListAsync(ct);

        if (logs.Count == 0) return;

        // Group by UTC hour
        var byHour = logs
            .GroupBy(l => l.PredictedAt.Hour)
            .ToList();

        var now      = DateTime.UtcNow;
        int upserted = 0;

        foreach (var group in byHour)
        {
            int hourUtc = group.Key;
            int total   = group.Count();

            if (total < minPredictions) continue;

            int    correct  = group.Count(l => l.DirectionCorrect == true);
            double accuracy = (double)correct / total;

            int rows = await writeCtx.Set<MLModelHourlyAccuracy>()
                .Where(r => r.MLModelId == modelId && r.HourUtc == hourUtc)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.TotalPredictions,   total)
                    .SetProperty(r => r.CorrectPredictions, correct)
                    .SetProperty(r => r.Accuracy,           accuracy)
                    .SetProperty(r => r.WindowStart,        windowStart)
                    .SetProperty(r => r.ComputedAt,         now),
                    ct);

            if (rows == 0)
            {
                writeCtx.Set<MLModelHourlyAccuracy>().Add(new MLModelHourlyAccuracy
                {
                    MLModelId          = modelId,
                    Symbol             = symbol,
                    Timeframe          = timeframe,
                    HourUtc            = hourUtc,
                    TotalPredictions   = total,
                    CorrectPredictions = correct,
                    Accuracy           = accuracy,
                    WindowStart        = windowStart,
                    ComputedAt         = now,
                });
                await writeCtx.SaveChangesAsync(ct);
            }

            upserted++;

            _logger.LogDebug(
                "HourlyAccuracy: model {Id} {Symbol}/{Tf} hour={H:D2}:00 UTC — acc={Acc:P1} n={N}",
                modelId, symbol, timeframe, hourUtc, accuracy, total);
        }

        if (upserted > 0)
            _logger.LogInformation(
                "HourlyAccuracy: model {Id} ({Symbol}/{Tf}) — computed {N} hour bucket(s).",
                modelId, symbol, timeframe, upserted);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
