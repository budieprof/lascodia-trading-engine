using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Refits the isotonic (PAVA) calibration curve for each active ML model using recent
/// resolved <see cref="MLModelPredictionLog"/> records, then writes the updated
/// <see cref="ModelSnapshot.IsotonicBreakpoints"/> back to the database without requiring
/// a full retrain.
///
/// At training time, isotonic breakpoints are fitted on a held-out calibration set whose
/// distribution matches the training period. In live trading the predicted-probability
/// distribution can drift, causing the calibrated probabilities to over- or under-estimate
/// the true empirical accuracy. This worker corrects that drift between retrains.
///
/// Algorithm:
/// <list type="number">
///   <item>Reconstruct approximate calibP for each resolved prediction:
///         <list type="bullet">
///           <item>Buy:  calibP ≈ threshold + ConfidenceScore × (1 − threshold)</item>
///           <item>Sell: calibP ≈ threshold − ConfidenceScore × threshold</item>
///         </list>
///         where <c>threshold</c> = snapshot's OptimalThreshold (or 0.5 if unset).
///   </item>
///   <item>Sort (calibP, label) pairs by calibP ascending.</item>
///   <item>Apply the Pool Adjacent Violators Algorithm (PAVA) to fit a non-decreasing
///         calibration curve that maps calibP → empirical accuracy.</item>
///   <item>Store the resulting breakpoints as the new
///         <see cref="ModelSnapshot.IsotonicBreakpoints"/>.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLIsotonicRecal:PollIntervalSeconds</c> — default 28800 (8 h)</item>
///   <item><c>MLIsotonicRecal:WindowDays</c>          — look-back window, default 30</item>
///   <item><c>MLIsotonicRecal:MinResolved</c>          — skip if fewer records, default 50</item>
/// </list>
/// </summary>
public sealed class MLIsotonicRecalibrationWorker : BackgroundService
{
    private const string CK_PollSecs    = "MLIsotonicRecal:PollIntervalSeconds";
    private const string CK_WindowDays  = "MLIsotonicRecal:WindowDays";
    private const string CK_MinResolved = "MLIsotonicRecal:MinResolved";

    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";

    private readonly IServiceScopeFactory                     _scopeFactory;
    private readonly IMemoryCache                             _cache;
    private readonly ILogger<MLIsotonicRecalibrationWorker>   _logger;

    public MLIsotonicRecalibrationWorker(
        IServiceScopeFactory                    scopeFactory,
        IMemoryCache                            cache,
        ILogger<MLIsotonicRecalibrationWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLIsotonicRecalibrationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 28800;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 28800, stoppingToken);

                await RecalibrateModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLIsotonicRecalibrationWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLIsotonicRecalibrationWorker stopping.");
    }

    private async Task RecalibrateModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int windowDays  = await GetConfigAsync<int>(readCtx, CK_WindowDays,  30, ct);
        int minResolved = await GetConfigAsync<int>(readCtx, CK_MinResolved, 50, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await RecalibrateModelAsync(model, readCtx, writeCtx, windowDays, minResolved, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Isotonic recalibration failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    private async Task RecalibrateModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minResolved,
        CancellationToken                       ct)
    {
        ModelSnapshot? snap;
        try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }

        if (snap is null) return;

        double threshold = snap.OptimalThreshold > 0.0 ? snap.OptimalThreshold
                         : snap.AdaptiveThreshold > 0.0 ? snap.AdaptiveThreshold
                         : 0.5;

        var since = DateTime.UtcNow.AddDays(-windowDays);

        var resolved = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.DirectionCorrect != null     &&
                        !l.IsDeleted)
            .Select(l => new
            {
                l.PredictedDirection,
                l.ConfidenceScore,
                DirectionCorrect = l.DirectionCorrect!.Value,
            })
            .ToListAsync(ct);

        if (resolved.Count < minResolved)
        {
            _logger.LogDebug(
                "IsotonicRecal: {Symbol}/{Tf} model {Id} only {N} resolved (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, resolved.Count, minResolved);
            return;
        }

        // Reconstruct approximate calibP from ConfidenceScore + PredictedDirection + threshold.
        // Buy:  calibP ≈ T + ConfidenceScore × (1 − T)  → maps [0,1] confidence to [T, 1]
        // Sell: calibP ≈ T − ConfidenceScore × T         → maps [0,1] confidence to [T, 0]
        var pairs = resolved
            .Select(r =>
            {
                double conf    = Math.Clamp((double)r.ConfidenceScore, 0.0, 1.0);
                double calibP  = r.PredictedDirection == TradeDirection.Buy
                    ? threshold + conf * (1.0 - threshold)
                    : threshold - conf * threshold;
                double label   = r.DirectionCorrect ? 1.0 : 0.0;
                return (P: calibP, Y: label);
            })
            .OrderBy(p => p.P)
            .ToList();

        double[] newBreakpoints = FitPAVA(pairs);

        if (newBreakpoints.Length < 4)
        {
            _logger.LogDebug(
                "IsotonicRecal: {Symbol}/{Tf} model {Id} PAVA produced < 2 segments — skip.",
                model.Symbol, model.Timeframe, model.Id);
            return;
        }

        _logger.LogInformation(
            "IsotonicRecal: {Symbol}/{Tf} model {Id}: fitted {N} PAVA breakpoints from {Count} samples.",
            model.Symbol, model.Timeframe, model.Id, newBreakpoints.Length / 2, resolved.Count);

        snap.IsotonicBreakpoints = newBreakpoints;
        byte[] updatedBytes = JsonSerializer.SerializeToUtf8Bytes(snap);

        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == model.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ModelBytes, updatedBytes), ct);

        _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");
    }

    // ── PAVA (Pool Adjacent Violators Algorithm) ──────────────────────────────

    /// <summary>
    /// Fits a monotone non-decreasing calibration curve via PAVA.
    /// Returns interleaved [x₀,y₀,x₁,y₁,…] breakpoints compatible with
    /// <see cref="BaggedLogisticTrainer.ApplyIsotonicCalibration"/>.
    /// </summary>
    private static double[] FitPAVA(List<(double P, double Y)> pairs)
    {
        if (pairs.Count < 10) return [];

        // Stack-based PAVA: each entry = (sumY, sumP, count)
        var stack = new List<(double SumY, double SumP, int Count)>(pairs.Count);
        foreach (var (P, Y) in pairs)
        {
            stack.Add((Y, P, 1));
            while (stack.Count >= 2)
            {
                var last = stack[^1];
                var prev = stack[^2];
                if (prev.SumY / prev.Count > last.SumY / last.Count)
                {
                    stack.RemoveAt(stack.Count - 1);
                    stack[^1] = (prev.SumY + last.SumY,
                                 prev.SumP + last.SumP,
                                 prev.Count + last.Count);
                }
                else break;
            }
        }

        // Build interleaved [x,y] breakpoints: x = mean calibP, y = mean label
        var bp = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            bp[i * 2]     = stack[i].SumP / stack[i].Count;
            bp[i * 2 + 1] = stack[i].SumY / stack[i].Count;
        }
        return bp;
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
