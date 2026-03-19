using System.Text.Json;
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
/// Performs online EMA-based adaptation of the ML model's decision threshold without
/// requiring a full retrain.
///
/// <para>
/// The worker loads the most recent N resolved <see cref="MLModelPredictionLog"/> records
/// for each active model, then sweeps threshold values from 0.3 to 0.7 in steps of 0.01
/// and picks the threshold that maximises the expected value (EV) on the recent window.
/// </para>
///
/// <para>
/// The new threshold is blended with the existing <see cref="ModelSnapshot.AdaptiveThreshold"/>
/// using an EMA: θ_new = α × θ_optimal + (1 − α) × θ_current, where α is configurable
/// (default 0.2). The blended threshold is then written back to <see cref="MLModel.ModelBytes"/>.
/// </para>
///
/// <para>
/// Adaptation is skipped when:
/// <list type="bullet">
///   <item>Fewer than <c>MLAdaptiveThreshold:MinResolvedPredictions</c> resolved logs are available.</item>
///   <item>The absolute change in threshold is below <c>MLAdaptiveThreshold:MinThresholdDrift</c>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MLAdaptiveThresholdWorker : BackgroundService
{
    private const string CK_PollSecs         = "MLAdaptiveThreshold:PollIntervalSeconds";
    private const string CK_WindowSize       = "MLAdaptiveThreshold:WindowSize";
    private const string CK_MinPredictions   = "MLAdaptiveThreshold:MinResolvedPredictions";
    private const string CK_EmaAlpha         = "MLAdaptiveThreshold:EmaAlpha";
    private const string CK_MinDrift         = "MLAdaptiveThreshold:MinThresholdDrift";

    private readonly IServiceScopeFactory                  _scopeFactory;
    private readonly ILogger<MLAdaptiveThresholdWorker>    _logger;

    public MLAdaptiveThresholdWorker(
        IServiceScopeFactory                 scopeFactory,
        ILogger<MLAdaptiveThresholdWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLAdaptiveThresholdWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await AdaptAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLAdaptiveThresholdWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLAdaptiveThresholdWorker stopping.");
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    private async Task AdaptAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowSize      = await GetConfigAsync<int>   (readCtx, CK_WindowSize,     500,  ct);
        int    minPredictions  = await GetConfigAsync<int>   (readCtx, CK_MinPredictions, 100,  ct);
        double emaAlpha        = await GetConfigAsync<double>(readCtx, CK_EmaAlpha,       0.2,  ct);
        double minDrift        = await GetConfigAsync<double>(readCtx, CK_MinDrift,       0.01, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .ToListAsync(ct);

        _logger.LogDebug("Adaptive threshold: {Count} active model(s).", activeModels.Count);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await AdaptModelAsync(model, readCtx, writeCtx,
                    windowSize, minPredictions, emaAlpha, minDrift, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Adaptive threshold failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task AdaptModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowSize,
        int                                     minPredictions,
        double                                  emaAlpha,
        double                                  minDrift,
        CancellationToken                       ct)
    {
        // ── Load resolved prediction logs ─────────────────────────────────────
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId      == model.Id &&
                        l.DirectionCorrect != null   &&
                        !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)
            .Take(windowSize)
            .ToListAsync(ct);

        if (logs.Count < minPredictions)
        {
            _logger.LogDebug(
                "Adaptive threshold skipped model {Id} — only {N} resolved logs (need {Min}).",
                model.Id, logs.Count, minPredictions);
            return;
        }

        // ── Sweep threshold to maximise EV ────────────────────────────────────
        double bestThreshold = 0.5;
        double bestEv        = double.MinValue;

        for (int step = 30; step <= 70; step++)
        {
            double thr = step / 100.0;
            double ev  = ComputeEvAtThreshold(logs, thr);
            if (ev > bestEv)
            {
                bestEv        = ev;
                bestThreshold = thr;
            }
        }

        // ── Deserialise snapshot ──────────────────────────────────────────────
        ModelSnapshot? snap = null;
        try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }

        if (snap is null) return;

        double currentThreshold = snap.AdaptiveThreshold > 0.0 ? snap.AdaptiveThreshold : 0.5;
        double newThreshold     = emaAlpha * bestThreshold + (1.0 - emaAlpha) * currentThreshold;
        double drift            = Math.Abs(newThreshold - currentThreshold);

        bool anyUpdate = false;

        if (drift >= minDrift)
        {
            snap.AdaptiveThreshold = newThreshold;
            anyUpdate = true;

            _logger.LogInformation(
                "Adaptive threshold updated model {Id} ({Symbol}/{Tf}): " +
                "{Old:F4} → {New:F4} (optimal={Opt:F4}, EV={Ev:F4}, drift={Drift:F4}).",
                model.Id, model.Symbol, model.Timeframe,
                currentThreshold, newThreshold, bestThreshold, bestEv, drift);
        }
        else
        {
            _logger.LogDebug(
                "Adaptive threshold model {Id}: global drift {Drift:F4} < minDrift {Min:F4} — skipping global update.",
                model.Id, drift, minDrift);
        }

        // ── Regime-conditioned threshold sweep ────────────────────────────────
        // Load recent regime snapshots to assign each prediction log to a regime
        var regimeSnapshots = await readCtx.Set<MarketRegimeSnapshot>()
            .AsNoTracking()
            .Where(r => r.Symbol    == model.Symbol    &&
                        r.Timeframe == model.Timeframe &&
                        !r.IsDeleted)
            .OrderByDescending(r => r.DetectedAt)
            .Take(2000)
            .ToListAsync(ct);

        if (regimeSnapshots.Count > 0)
        {
            // Group prediction logs by the closest prior regime snapshot
            var regimeGroups = new Dictionary<string, List<MLModelPredictionLog>>();

            foreach (var log in logs)
            {
                // Find the latest regime snapshot at or before this prediction time
                var regime = regimeSnapshots
                    .FirstOrDefault(r => r.DetectedAt <= log.PredictedAt);

                string regimeName = regime?.Regime.ToString() ?? "Unknown";
                if (!regimeGroups.TryGetValue(regimeName, out var group))
                {
                    group = [];
                    regimeGroups[regimeName] = group;
                }
                group.Add(log);
            }

            snap.RegimeThresholds ??= [];

            foreach (var (regimeName, regimeLogs) in regimeGroups)
            {
                if (regimeLogs.Count < 20) continue; // need minimum observations

                double regimeBestThr = 0.5;
                double regimeBestEv  = double.MinValue;

                for (int step = 30; step <= 70; step++)
                {
                    double thr = step / 100.0;
                    double ev  = ComputeEvAtThreshold(regimeLogs, thr);
                    if (ev > regimeBestEv) { regimeBestEv = ev; regimeBestThr = thr; }
                }

                double currentRegimeThr = snap.RegimeThresholds.TryGetValue(regimeName, out var existing)
                    ? existing : 0.5;
                double newRegimeThr = emaAlpha * regimeBestThr + (1.0 - emaAlpha) * currentRegimeThr;
                double regimeDrift  = Math.Abs(newRegimeThr - currentRegimeThr);

                if (regimeDrift >= minDrift)
                {
                    snap.RegimeThresholds[regimeName] = newRegimeThr;
                    anyUpdate = true;

                    _logger.LogInformation(
                        "Regime threshold updated model {Id} ({Symbol}/{Tf}) regime={Regime}: " +
                        "{Old:F4} → {New:F4} (optimal={Opt:F4}, EV={Ev:F4}, N={N}).",
                        model.Id, model.Symbol, model.Timeframe, regimeName,
                        currentRegimeThr, newRegimeThr, regimeBestThr, regimeBestEv, regimeLogs.Count);
                }
            }
        }

        if (!anyUpdate) return;

        // ── Write updated snapshot back to ModelBytes ─────────────────────────
        byte[] updatedBytes = JsonSerializer.SerializeToUtf8Bytes(snap);

        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == model.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.ModelBytes, updatedBytes),
                ct);
    }

    // ── EV computation ────────────────────────────────────────────────────────

    /// <summary>
    /// Simulates trading on the resolved prediction window using a fixed threshold and
    /// returns the mean per-trade expected value (win fraction − loss fraction).
    /// </summary>
    private static double ComputeEvAtThreshold(
        IReadOnlyList<MLModelPredictionLog> logs,
        double                              threshold)
    {
        int wins = 0, losses = 0;

        foreach (var log in logs)
        {
            if (log.DirectionCorrect is null) continue;

            // Reconstruct calibrated probability from direction + confidence score
            // Approximate inverse: calibP ≈ threshold + conf/2 (for Buy direction)
            double conf    = (double)(log.ConfidenceScore);
            double calibP  = log.PredictedDirection == TradeDirection.Buy
                ? threshold + conf / 2.0
                : threshold - conf / 2.0;

            bool predicted = calibP >= threshold;
            bool actual    = log.DirectionCorrect.Value;

            if (predicted == actual) wins++;
            else                     losses++;
        }

        int total = wins + losses;
        if (total == 0) return 0;
        return (double)(wins - losses) / total;
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
