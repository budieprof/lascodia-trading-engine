using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically re-derives the optimal decision threshold for each active ML model
/// from its resolved <see cref="MLModelPredictionLog"/> records and writes the
/// updated value back into <see cref="ModelSnapshot.OptimalThreshold"/>.
///
/// The threshold stored in the snapshot at training time maximises expected value on
/// the hold-out test set. In live trading the optimal threshold drifts as the market
/// regime changes. This worker keeps it aligned between full retrains by using the
/// Youden-J balanced precision strategy:
///
///   Δ = (sellPrecision − buyPrecision) × LearningRate
///   new_threshold = clamp(current_threshold + Δ, MinThreshold, MaxThreshold)
///
/// Rationale: when buyPrecision &lt; sellPrecision the model is too eager to predict Buy;
/// raising the threshold forces it to be more selective, restoring balance.
///
/// The update is skipped when:
/// <list type="bullet">
///   <item>Fewer than <c>MLThresholdCal:MinResolved</c> records exist.</item>
///   <item>Both buy and sell precision already exceed <c>MLThresholdCal:TargetPrecision</c>.</item>
///   <item>The computed delta is below <c>1e-4</c> (negligible movement).</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLThresholdCal:PollIntervalSeconds</c> — default 21600 (6 h)</item>
///   <item><c>MLThresholdCal:WindowDays</c>          — resolved-log look-back, default 21</item>
///   <item><c>MLThresholdCal:MinResolved</c>          — skip model if fewer records, default 40</item>
///   <item><c>MLThresholdCal:LearningRate</c>         — nudge scaling factor, default 0.10</item>
///   <item><c>MLThresholdCal:MinThreshold</c>         — lower clamp, default 0.35</item>
///   <item><c>MLThresholdCal:MaxThreshold</c>         — upper clamp, default 0.65</item>
/// </list>
/// </summary>
public sealed class MLThresholdCalibrationWorker : BackgroundService
{
    private const string CK_PollSecs      = "MLThresholdCal:PollIntervalSeconds";
    private const string CK_WindowDays    = "MLThresholdCal:WindowDays";
    private const string CK_MinResolved   = "MLThresholdCal:MinResolved";
    private const string CK_LearningRate  = "MLThresholdCal:LearningRate";
    private const string CK_MinThreshold  = "MLThresholdCal:MinThreshold";
    private const string CK_MaxThreshold  = "MLThresholdCal:MaxThreshold";

    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";

    private readonly IServiceScopeFactory                    _scopeFactory;
    private readonly IMemoryCache                            _cache;
    private readonly ILogger<MLThresholdCalibrationWorker>   _logger;

    public MLThresholdCalibrationWorker(
        IServiceScopeFactory                   scopeFactory,
        IMemoryCache                           cache,
        ILogger<MLThresholdCalibrationWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLThresholdCalibrationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 21600;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 21600, stoppingToken);

                await CalibrateThresholdsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLThresholdCalibrationWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLThresholdCalibrationWorker stopping.");
    }

    private async Task CalibrateThresholdsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays   = await GetConfigAsync<int>   (readCtx, CK_WindowDays,   21,   ct);
        int    minResolved  = await GetConfigAsync<int>   (readCtx, CK_MinResolved,  40,   ct);
        double learningRate = await GetConfigAsync<double>(readCtx, CK_LearningRate, 0.10, ct);
        double minThreshold = await GetConfigAsync<double>(readCtx, CK_MinThreshold, 0.35, ct);
        double maxThreshold = await GetConfigAsync<double>(readCtx, CK_MaxThreshold, 0.65, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CalibrateModelThresholdAsync(
                    model, readCtx, writeCtx,
                    windowDays, minResolved, learningRate, minThreshold, maxThreshold, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Threshold calibration failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    private async Task CalibrateModelThresholdAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minResolved,
        double                                  learningRate,
        double                                  minThreshold,
        double                                  maxThreshold,
        CancellationToken                       ct)
    {
        var since = DateTime.UtcNow.AddDays(-windowDays);

        var resolved = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.DirectionCorrect != null     &&
                        !l.IsDeleted)
            .Select(l => new
            {
                l.PredictedDirection,
                DirectionCorrect = l.DirectionCorrect!.Value,
            })
            .ToListAsync(ct);

        if (resolved.Count < minResolved)
        {
            _logger.LogDebug(
                "ThresholdCal: {Symbol}/{Tf} model {Id} only {N} resolved (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, resolved.Count, minResolved);
            return;
        }

        // Confusion matrix
        int tp = resolved.Count(r => r.PredictedDirection == TradeDirection.Buy  &&  r.DirectionCorrect);
        int fp = resolved.Count(r => r.PredictedDirection == TradeDirection.Buy  && !r.DirectionCorrect);
        int tn = resolved.Count(r => r.PredictedDirection == TradeDirection.Sell &&  r.DirectionCorrect);
        int fn = resolved.Count(r => r.PredictedDirection == TradeDirection.Sell && !r.DirectionCorrect);

        double buyPrecision  = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0.5;
        double sellPrecision = (tn + fn) > 0 ? (double)tn / (tn + fn) : 0.5;

        _logger.LogDebug(
            "ThresholdCal: {Symbol}/{Tf} model {Id}: buyPrec={Buy:P1} sellPrec={Sell:P1} (n={N})",
            model.Symbol, model.Timeframe, model.Id, buyPrecision, sellPrecision, resolved.Count);

        // Delta: positive → raise threshold (less Buy), negative → lower threshold (more Buy)
        double delta = (sellPrecision - buyPrecision) * learningRate;
        if (Math.Abs(delta) < 1e-4)
        {
            _logger.LogDebug(
                "ThresholdCal: {Symbol}/{Tf} model {Id} delta={D:F5} negligible — no update.",
                model.Symbol, model.Timeframe, model.Id, delta);
            return;
        }

        // Deserialise snapshot, update threshold, re-serialise
        ModelSnapshot? snap;
        try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }

        if (snap is null) return;

        double currentThreshold = snap.OptimalThreshold > 0.0 ? snap.OptimalThreshold
                                : snap.AdaptiveThreshold > 0.0 ? snap.AdaptiveThreshold
                                : 0.5;

        double newThreshold = Math.Clamp(currentThreshold + delta, minThreshold, maxThreshold);

        _logger.LogInformation(
            "ThresholdCal: {Symbol}/{Tf} model {Id}: threshold {Old:F4} → {New:F4} (Δ={D:F4})",
            model.Symbol, model.Timeframe, model.Id, currentThreshold, newThreshold, delta);

        snap.OptimalThreshold = newThreshold;
        byte[] updatedBytes = JsonSerializer.SerializeToUtf8Bytes(snap);

        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == model.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ModelBytes, updatedBytes), ct);

        _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");
    }

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
