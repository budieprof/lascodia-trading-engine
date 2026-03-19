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
/// Performs lightweight Platt + isotonic recalibration of active ML models when
/// Expected Calibration Error (ECE) rises above an acceptable threshold.
///
/// Unlike a full retrain, only the Platt (A, B) parameters and the isotonic
/// regression breakpoints are re-fitted on a rolling window of recently resolved
/// <see cref="MLModelPredictionLog"/> records whose outcomes are known.
/// The new parameters are patched directly into <see cref="MLModel.ModelBytes"/>
/// and the in-memory scorer cache is invalidated.
///
/// The recalibration is skipped when:
/// <list type="bullet">
///   <item>Fewer than <c>MLRecalibration:MinSamples</c> resolved predictions are available.</item>
///   <item>The current ECE is already below <c>MLRecalibration:MaxEce</c>.</item>
///   <item>A full retrain (<see cref="MLTrainingRun"/>) is already queued for the same symbol/timeframe.</item>
/// </list>
/// </summary>
public sealed class MLRecalibrationWorker : BackgroundService
{
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    private const string CK_PollSecs      = "MLRecalibration:PollIntervalSeconds";
    private const string CK_WindowDays    = "MLRecalibration:WindowDays";
    private const string CK_MinSamples    = "MLRecalibration:MinSamples";
    private const string CK_MaxEce        = "MLRecalibration:MaxEce";
    private const string CK_PlattLr       = "MLRecalibration:PlattLearningRate";
    private const string CK_PlattEpochs   = "MLRecalibration:PlattEpochs";

    // ── Cache key prefix (matches MLSignalScorer) ────────────────────────────
    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly IMemoryCache                      _cache;
    private readonly ILogger<MLRecalibrationWorker>    _logger;

    public MLRecalibrationWorker(
        IServiceScopeFactory             scopeFactory,
        IMemoryCache                     cache,
        ILogger<MLRecalibrationWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLRecalibrationWorker started.");

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

                await RecalibrateActiveModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLRecalibrationWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLRecalibrationWorker stopping.");
    }

    // ── Per-poll recalibration loop ───────────────────────────────────────────

    private async Task RecalibrateActiveModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int windowDays  = await GetConfigAsync<int>(readCtx,    CK_WindowDays,  30,    ct);
        int minSamples  = await GetConfigAsync<int>(readCtx,    CK_MinSamples,  50,    ct);
        double maxEce   = await GetConfigAsync<double>(readCtx, CK_MaxEce,      0.07,  ct);
        double plattLr  = await GetConfigAsync<double>(readCtx, CK_PlattLr,     0.01,  ct);
        int    plattEpochs = await GetConfigAsync<int>(readCtx, CK_PlattEpochs, 200,   ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        _logger.LogDebug("MLRecalibrationWorker: {N} active model(s) to check.", activeModels.Count);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await RecalibrateModelAsync(
                    model, readCtx, writeCtx,
                    windowDays, minSamples, maxEce, plattLr, plattEpochs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Recalibration failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    private async Task RecalibrateModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minSamples,
        double                                  maxEce,
        double                                  plattLr,
        int                                     plattEpochs,
        CancellationToken                       ct)
    {
        // Skip if a full retrain is already queued for this symbol/timeframe
        bool retrainQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol == model.Symbol && r.Timeframe == model.Timeframe
                           && r.Status == RunStatus.Queued, ct);
        if (retrainQueued)
        {
            _logger.LogDebug(
                "Recalibration skipped for {Symbol}/{Tf} — full retrain queued.",
                model.Symbol, model.Timeframe);
            return;
        }

        // Load resolved prediction logs within the recalibration window
        var since = DateTime.UtcNow.AddDays(-windowDays);
        var logs  = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId           == model.Id &&
                        l.PredictedAt         >= since    &&
                        l.DirectionCorrect.HasValue       &&
                        !l.IsDeleted)
            .OrderBy(l => l.PredictedAt)
            .AsNoTracking()
            .ToListAsync(ct);

        if (logs.Count < minSamples)
        {
            _logger.LogDebug(
                "Recalibration skipped for {Symbol}/{Tf}: only {N} resolved predictions (need {Min}).",
                model.Symbol, model.Timeframe, logs.Count, minSamples);
            return;
        }

        // Deserialise snapshot
        ModelSnapshot? snap;
        try
        {
            snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Recalibration: failed to deserialise snapshot for model {Id} — skipping.", model.Id);
            return;
        }

        if (snap is null) return;

        // Compute current ECE from prediction logs (using stored predicted probability)
        double currentEce = ComputeEceFromLogs(logs);
        _logger.LogDebug(
            "Model {Symbol}/{Tf} id={Id}: ECE={Ece:F4} (max={Max:F4}, samples={N})",
            model.Symbol, model.Timeframe, model.Id, currentEce, maxEce, logs.Count);

        if (currentEce <= maxEce)
        {
            _logger.LogDebug(
                "Recalibration not needed for {Symbol}/{Tf} — ECE {Ece:F4} ≤ {Max:F4}.",
                model.Symbol, model.Timeframe, currentEce, maxEce);
            return;
        }

        // Build per-log temporal decay weights (more recent = higher weight)
        double recalDecayLambda = await GetConfigAsync<double>(readCtx, "MLRecalibration:DecayLambda", 0.0, ct);
        double[] logWeights = BuildTemporalWeights(logs, recalDecayLambda);

        // Refit Platt scaling (A, B) using temporally-weighted SGD on the resolved prediction logs
        var (newPlattA, newPlattB) = RefitPlattFromLogs(logs, logWeights, plattLr, plattEpochs);

        // Refit isotonic calibration breakpoints
        var newIsotonicBp = RefitIsotonicFromLogs(logs, newPlattA, newPlattB);

        double newEce = ComputeEceFromLogsWithPlatt(logs, newPlattA, newPlattB);
        if (newEce >= currentEce)
        {
            _logger.LogDebug(
                "Recalibration for {Symbol}/{Tf}: new ECE {New:F4} not better than {Old:F4} — skipping update.",
                model.Symbol, model.Timeframe, newEce, currentEce);
            return;
        }

        // Refit class-conditional Platt (separate scalers for Buy and Sell)
        var (newABuy, newBBuy, newASell, newBSell) = RefitClassConditionalPlatt(logs, plattLr, plattEpochs);

        // Patch snapshot with new calibration parameters
        snap.PlattA              = newPlattA;
        snap.PlattB              = newPlattB;
        snap.IsotonicBreakpoints = newIsotonicBp;
        if (newABuy  != 0.0) { snap.PlattABuy  = newABuy;  snap.PlattBBuy  = newBBuy;  }
        if (newASell != 0.0) { snap.PlattASell = newASell; snap.PlattBSell = newBSell; }

        byte[] updatedBytes;
        try
        {
            updatedBytes = JsonSerializer.SerializeToUtf8Bytes(snap);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recalibration: failed to re-serialise snapshot for model {Id}.", model.Id);
            return;
        }

        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == model.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ModelBytes, updatedBytes), ct);

        // Invalidate scorer cache
        _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");

        _logger.LogInformation(
            "Recalibration applied for {Symbol}/{Tf} model {Id}: " +
            "ECE {Old:F4} → {New:F4}, PlattA={A:F4} B={B:F4}, {N} resolved predictions.",
            model.Symbol, model.Timeframe, model.Id,
            currentEce, newEce, newPlattA, newPlattB, logs.Count);
    }

    // ── Calibration helpers ───────────────────────────────────────────────────

    private static double ComputeEceFromLogs(IReadOnlyList<MLModelPredictionLog> logs)
    {
        if (logs.Count == 0) return 0.0;
        const int NumBins = 10;
        var binConfSum = new double[NumBins];
        var binCorrect = new int[NumBins];
        var binCount   = new int[NumBins];

        foreach (var log in logs)
        {
            double p   = (double)log.ConfidenceScore;
            int    bin = Math.Clamp((int)(p * NumBins), 0, NumBins - 1);
            binConfSum[bin] += p;
            bool correct = (p >= 0.5) == (log.DirectionCorrect == true);
            if (correct) binCorrect[bin]++;
            binCount[bin]++;
        }

        double ece = 0;
        int    n   = logs.Count;
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf = binConfSum[b] / binCount[b];
            double acc     = binCorrect[b] / (double)binCount[b];
            ece += Math.Abs(acc - avgConf) * binCount[b] / n;
        }
        return ece;
    }

    private static double ComputeEceFromLogsWithPlatt(
        IReadOnlyList<MLModelPredictionLog> logs,
        double                              plattA,
        double                              plattB)
    {
        if (logs.Count == 0) return 0.0;
        const int NumBins = 10;
        var binConfSum = new double[NumBins];
        var binCorrect = new int[NumBins];
        var binCount   = new int[NumBins];

        foreach (var log in logs)
        {
            double rawP    = (double)log.ConfidenceScore;
            double logit   = MLFeatureHelper.Logit(rawP);
            double calibP  = MLFeatureHelper.Sigmoid(plattA * logit + plattB);
            int    bin     = Math.Clamp((int)(calibP * NumBins), 0, NumBins - 1);
            binConfSum[bin] += calibP;
            bool correct    = (calibP >= 0.5) == (log.DirectionCorrect == true);
            if (correct) binCorrect[bin]++;
            binCount[bin]++;
        }

        double ece = 0;
        int    n   = logs.Count;
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf = binConfSum[b] / binCount[b];
            double acc     = binCorrect[b] / (double)binCount[b];
            ece += Math.Abs(acc - avgConf) * binCount[b] / n;
        }
        return ece;
    }

    /// <summary>
    /// Builds per-log temporal decay weights: w_i = exp(−λ × days_since_prediction).
    /// Normalised to sum to logs.Count. When λ = 0, returns uniform weights (all = 1.0).
    /// </summary>
    private static double[] BuildTemporalWeights(
        IReadOnlyList<MLModelPredictionLog> logs,
        double                              lambda)
    {
        int    n       = logs.Count;
        var    weights = new double[n];
        var    now     = DateTime.UtcNow;

        if (lambda <= 0.0)
        {
            for (int i = 0; i < n; i++) weights[i] = 1.0;
            return weights;
        }

        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double days = (now - logs[i].PredictedAt).TotalDays;
            weights[i]  = Math.Exp(-lambda * days);
            sum        += weights[i];
        }
        // Normalise so that the effective gradient scale equals the unweighted scale
        if (sum > 1e-15)
            for (int i = 0; i < n; i++) weights[i] = weights[i] / sum * n;
        return weights;
    }

    private static (double A, double B) RefitPlattFromLogs(
        IReadOnlyList<MLModelPredictionLog> logs,
        double[]                            sampleWeights,
        double                              lr,
        int                                 epochs)
    {
        double plattA = 1.0, plattB = 0.0;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double dA = 0, dB = 0, wSum = 0;
            for (int i = 0; i < logs.Count; i++)
            {
                var    log    = logs[i];
                double w      = sampleWeights[i];
                double rawP   = (double)log.ConfidenceScore;
                double logit  = MLFeatureHelper.Logit(rawP);
                double calibP = MLFeatureHelper.Sigmoid(plattA * logit + plattB);
                double y      = log.DirectionCorrect == true ? 1.0 : 0.0;
                double err    = (calibP - y) * w;
                dA   += err * logit;
                dB   += err;
                wSum += w;
            }
            if (wSum < 1e-15) break;
            plattA -= lr * dA / wSum;
            plattB -= lr * dB / wSum;
        }

        return (plattA, plattB);
    }

    private static double[] RefitIsotonicFromLogs(
        IReadOnlyList<MLModelPredictionLog> logs,
        double                              plattA,
        double                              plattB)
    {
        if (logs.Count < 10) return [];

        var pairs = logs
            .Select(l =>
            {
                double rawP   = (double)l.ConfidenceScore;
                double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawP) + plattB);
                return (P: calibP, Y: l.DirectionCorrect == true ? 1.0 : 0.0);
            })
            .OrderBy(x => x.P)
            .ToList();

        // Pool Adjacent Violators Algorithm (PAVA)
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

        var bp = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            bp[i * 2]     = stack[i].SumP / stack[i].Count;
            bp[i * 2 + 1] = stack[i].SumY / stack[i].Count;
        }
        return bp;
    }

    // ── Class-conditional Platt ───────────────────────────────────────────────

    /// <summary>
    /// Refits separate Platt scalers for the Buy-predicted subset (ConfidenceScore ≥ 0.5)
    /// and the Sell-predicted subset (ConfidenceScore &lt; 0.5) of the resolved prediction logs.
    /// Returns (0,0,0,0) when either class subset has fewer than 5 samples.
    /// </summary>
    private static (double ABuy, double BBuy, double ASell, double BSell)
        RefitClassConditionalPlatt(
            IReadOnlyList<MLModelPredictionLog> logs,
            double                              lr,
            int                                 epochs)
    {
        var buyPairs  = new List<(double Logit, double Y)>();
        var sellPairs = new List<(double Logit, double Y)>();

        foreach (var log in logs)
        {
            double rawP  = (double)log.ConfidenceScore;
            double logit = MLFeatureHelper.Logit(rawP);
            double y     = log.DirectionCorrect == true ? 1.0 : 0.0;
            if (rawP >= 0.5) buyPairs.Add((logit, y));
            else             sellPairs.Add((logit, y));
        }

        static (double A, double B) FitSgd(List<(double Logit, double Y)> pairs, double lr, int epochs)
        {
            if (pairs.Count < 5) return (0.0, 0.0);
            double a = 1.0, b = 0.0;
            for (int ep = 0; ep < epochs; ep++)
            {
                double dA = 0, dB = 0;
                foreach (var (logit, y) in pairs)
                {
                    double calibP = MLFeatureHelper.Sigmoid(a * logit + b);
                    double err    = calibP - y;
                    dA += err * logit;
                    dB += err;
                }
                int n = pairs.Count;
                a -= lr * dA / n;
                b -= lr * dB / n;
            }
            return (a, b);
        }

        var (aBuy,  bBuy)  = FitSgd(buyPairs,  lr, epochs);
        var (aSell, bSell) = FitSgd(sellPairs, lr, epochs);
        return (aBuy, bBuy, aSell, bSell);
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
