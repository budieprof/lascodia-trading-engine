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
/// Monitors per-feature distribution drift in active ML models by computing the
/// Population Stability Index (PSI) comparing the training-time feature distribution
/// (stored in <see cref="ModelSnapshot.FeatureQuantileBreakpoints"/>) against the
/// distribution of features derived from a recent candle window.
///
/// <para>
/// PSI = Σ_i (A_i − E_i) × ln(A_i / E_i) across 10 quantile bins.
/// Thresholds: &lt;0.1 = stable, 0.1–0.25 = moderate shift, &gt;0.25 = major shift.
/// </para>
///
/// An alert is raised for each feature whose PSI exceeds the configured threshold
/// (default 0.25). When the majority of features show major shift, a full retrain
/// is also queued.
/// </summary>
public sealed class MLFeaturePsiWorker : BackgroundService
{
    private const string CK_PollSecs         = "MLFeaturePsi:PollIntervalSeconds";
    private const string CK_CandleWindowDays = "MLFeaturePsi:CandleWindowDays";
    private const string CK_PsiAlertThresh   = "MLFeaturePsi:PsiAlertThreshold";
    private const string CK_PsiRetrainThresh = "MLFeaturePsi:PsiRetrainThreshold";
    private const string CK_AlertDest        = "MLFeaturePsi:AlertDestination";

    private readonly IServiceScopeFactory         _scopeFactory;
    private readonly ILogger<MLFeaturePsiWorker>  _logger;

    public MLFeaturePsiWorker(
        IServiceScopeFactory        scopeFactory,
        ILogger<MLFeaturePsiWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeaturePsiWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 7200;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 7200, stoppingToken);

                await CheckAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLFeaturePsiWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLFeaturePsiWorker stopping.");
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    private async Task CheckAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    candleWindowDays = await GetConfigAsync<int>   (readCtx, CK_CandleWindowDays, 14,      ct);
        double psiAlertThresh   = await GetConfigAsync<double>(readCtx, CK_PsiAlertThresh,   0.25,    ct);
        double psiRetrainThresh = await GetConfigAsync<double>(readCtx, CK_PsiRetrainThresh, 0.40,    ct);
        string alertDest        = await GetConfigAsync<string>(readCtx, CK_AlertDest,        "ml-ops", ct);

        var activeModels = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .ToListAsync(ct);

        _logger.LogDebug("PSI check: {Count} active model(s).", activeModels.Count);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckModelAsync(model, readCtx, writeCtx,
                    candleWindowDays, psiAlertThresh, psiRetrainThresh, alertDest, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PSI check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task CheckModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     candleWindowDays,
        double                                  psiAlertThresh,
        double                                  psiRetrainThresh,
        string                                  alertDest,
        CancellationToken                       ct)
    {
        // ── Deserialise snapshot ──────────────────────────────────────────────
        ModelSnapshot? snap = null;
        try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }

        if (snap is null || snap.FeatureQuantileBreakpoints.Length == 0)
        {
            _logger.LogDebug("PSI skipped model {Id} — no quantile breakpoints in snapshot.", model.Id);
            return;
        }

        int featureCount = snap.Features.Length > 0 ? snap.Features.Length : MLFeatureHelper.FeatureCount;

        // ── Load recent candles ───────────────────────────────────────────────
        var since = DateTime.UtcNow.AddDays(-candleWindowDays);
        var candles = await readCtx.Set<Candle>()
            .AsNoTracking()
            .Where(c => c.Symbol    == model.Symbol    &&
                        c.Timeframe == model.Timeframe &&
                        c.Timestamp >= since           &&
                        !c.IsDeleted)
            .OrderBy(c => c.Timestamp)
            .ToListAsync(ct);

        if (candles.Count < MLFeatureHelper.LookbackWindow + 5)
        {
            _logger.LogDebug("PSI skipped model {Id} — only {N} candles in window.", model.Id, candles.Count);
            return;
        }

        // ── Build recent feature vectors ──────────────────────────────────────
        var recentSamples = MLFeatureHelper.BuildTrainingSamples(candles);
        if (recentSamples.Count == 0) return;

        // Standardise using snapshot means/stds
        var recentStandardised = recentSamples
            .Select(s => MLFeatureHelper.Standardize(s.Features, snap.Means, snap.Stds))
            .ToList();

        // ── Compute PSI per feature ───────────────────────────────────────────
        int    highPsiCount   = 0;
        int    retrainTrigger = 0;
        var    psiValues      = new Dictionary<string, double>();

        for (int j = 0; j < Math.Min(featureCount, snap.FeatureQuantileBreakpoints.Length); j++)
        {
            double[] binEdges = snap.FeatureQuantileBreakpoints[j];
            if (binEdges.Length == 0) continue;

            // Build current (actual) feature distribution from recent standardised values
            double[] recentVals = recentStandardised.Select(f => j < f.Length ? (double)f[j] : 0.0).ToArray();

            // Training distribution is implicitly uniform across quantile bins (by construction)
            // We approximate by treating each bin edge as a uniform-population bin edge.
            // We use a synthetic "training" array that perfectly fills the bins equally.
            int    trainN   = recentVals.Length;     // same count for fair comparison
            double[] trainVals = GenerateUniformFromEdges(binEdges, trainN);

            double psi = MLFeatureHelper.ComputeFeaturePsi(binEdges, trainVals, recentVals);

            string featureName = j < snap.Features.Length ? snap.Features[j] : $"F{j}";
            psiValues[featureName] = psi;

            if (psi >= psiAlertThresh)
            {
                highPsiCount++;
                _logger.LogWarning(
                    "PSI model {Id} ({Symbol}/{Tf}) feature [{Name}]: PSI={Psi:F4} >= threshold={Thr:F4}",
                    model.Id, model.Symbol, model.Timeframe, featureName, psi, psiAlertThresh);
            }

            if (psi >= psiRetrainThresh)
                retrainTrigger++;
        }

        _logger.LogInformation(
            "PSI model {Id} ({Symbol}/{Tf}): {High}/{Total} features above alert threshold {Thr:F2}.",
            model.Id, model.Symbol, model.Timeframe, highPsiCount, psiValues.Count, psiAlertThresh);

        if (highPsiCount == 0) return;

        // ── Raise alert for distribution drift ───────────────────────────────
        bool retrainQueued = await writeCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running),
                      ct);

        if (retrainQueued)
        {
            _logger.LogDebug("PSI alert suppressed for model {Id} — retrain already queued.", model.Id);
            return;
        }

        writeCtx.Set<Alert>().Add(new Alert
        {
            Symbol        = model.Symbol,
            AlertType     = AlertType.MLModelDegraded,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = JsonSerializer.Serialize(new
            {
                DetectorType     = "FeaturePSI",
                ModelId          = model.Id,
                Timeframe        = model.Timeframe.ToString(),
                HighPsiFeatures  = highPsiCount,
                TotalFeatures    = psiValues.Count,
                AlertThreshold   = psiAlertThresh,
                RetrainThreshold = psiRetrainThresh,
                TopPsiFeatures   = psiValues
                    .OrderByDescending(kv => kv.Value)
                    .Take(5)
                    .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 4)),
            }),
            IsActive = true,
        });

        // Queue retrain when majority of features show severe drift
        if (retrainTrigger > psiValues.Count / 2)
        {
            writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol      = model.Symbol,
                Timeframe   = model.Timeframe,
                TriggerType = TriggerType.AutoDegrading,
                Status      = RunStatus.Queued,
                FromDate    = DateTime.UtcNow.AddDays(-365),
                ToDate      = DateTime.UtcNow,
                StartedAt   = DateTime.UtcNow,
            });

            _logger.LogWarning(
                "PSI model {Id} ({Symbol}/{Tf}): {N}/{T} features exceed retrain threshold — queuing retrain.",
                model.Id, model.Symbol, model.Timeframe, retrainTrigger, psiValues.Count);
        }

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a synthetic sample array that distributes uniformly across the provided
    /// quantile bin edges, representing the "expected" training distribution for PSI comparison.
    /// </summary>
    private static double[] GenerateUniformFromEdges(double[] edges, int n)
    {
        int bins = edges.Length + 1;
        int perBin = Math.Max(1, n / bins);
        var result = new List<double>(bins * perBin);

        double prevEdge = edges.Length > 0 ? edges[0] - (edges[^1] - edges[0]) * 0.5 : -3.0;

        for (int b = 0; b < bins; b++)
        {
            double lo = b == 0      ? prevEdge             : edges[b - 1];
            double hi = b < edges.Length ? edges[b]        : edges[^1] + (edges[^1] - edges[0]) * 0.5;
            double mid = (lo + hi) / 2.0;
            for (int i = 0; i < perBin; i++)
                result.Add(mid);
        }

        return [.. result];
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
