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

    /// <summary>
    /// Initialises the worker with a DI scope factory (used to create per-cycle scoped
    /// services such as the read/write DbContexts) and a logger.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create a fresh DI scope each polling cycle,
    /// ensuring EF Core DbContexts are properly scoped and disposed.</param>
    /// <param name="logger">Structured logger for PSI diagnostics and alerts.</param>
    public MLFeaturePsiWorker(
        IServiceScopeFactory        scopeFactory,
        ILogger<MLFeaturePsiWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs a continuous polling loop that:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope to obtain scoped read/write DbContexts.</item>
    ///   <item>Reads the poll interval from <see cref="EngineConfig"/> (key
    ///         <c>MLFeaturePsi:PollIntervalSeconds</c>, default 7200 s = 2 h).</item>
    ///   <item>Delegates the actual PSI computation to <see cref="CheckAllModelsAsync"/>.</item>
    ///   <item>Waits for the configured interval before repeating.</item>
    /// </list>
    /// The loop exits cleanly on <see cref="OperationCanceledException"/> caused by the
    /// <paramref name="stoppingToken"/>, allowing the host to shut down gracefully.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled by the host when the
    /// application is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeaturePsiWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval (2 hours). Overridden from EngineConfig each cycle
            // so operators can tune the interval without restarting the service.
            int pollSecs = 7200;

            try
            {
                // Create a new DI scope per cycle so EF Core DbContexts are disposed
                // correctly and do not accumulate tracked entities across iterations.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Re-read poll interval every cycle so live config changes take effect.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 7200, stoppingToken);

                await CheckAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — exit the loop without logging an error.
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

    /// <summary>
    /// Loads all runtime configuration values and iterates over every active ML model
    /// that has a serialised <see cref="ModelSnapshot"/> (i.e. <c>ModelBytes != null</c>).
    /// Each model is checked independently; failures for one model are caught and logged
    /// so that a corrupt snapshot cannot block the remaining models.
    /// </summary>
    /// <param name="readCtx">Read-only EF Core context for querying models and candles.</param>
    /// <param name="writeCtx">Write EF Core context for persisting alerts and training runs.</param>
    /// <param name="ct">Cancellation token propagated from the hosting loop.</param>
    private async Task CheckAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Read all PSI configuration values once per cycle to avoid per-model DB round-trips.
        int    candleWindowDays = await GetConfigAsync<int>   (readCtx, CK_CandleWindowDays, 14,      ct);
        double psiAlertThresh   = await GetConfigAsync<double>(readCtx, CK_PsiAlertThresh,   0.25,    ct);
        double psiRetrainThresh = await GetConfigAsync<double>(readCtx, CK_PsiRetrainThresh, 0.40,    ct);
        string alertDest        = await GetConfigAsync<string>(readCtx, CK_AlertDest,        "ml-ops", ct);

        // Only check models that have a stored snapshot (ModelBytes != null); models that
        // were imported without snapshot data are silently skipped.
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
                // Log and continue — a bad model must not prevent other models from being checked.
                _logger.LogWarning(ex, "PSI check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Performs the full PSI analysis for a single ML model:
    /// <list type="number">
    ///   <item>Deserialises the stored <see cref="ModelSnapshot"/> to obtain the training-time
    ///         quantile breakpoints, feature names, and normalisation statistics.</item>
    ///   <item>Loads recent candles within <paramref name="candleWindowDays"/> and builds
    ///         feature vectors using <see cref="MLFeatureHelper.BuildTrainingSamples"/>.</item>
    ///   <item>Standardises each recent feature vector using the snapshot's means and stds
    ///         so that the scale matches the training distribution.</item>
    ///   <item>For each feature, computes PSI and compares against <paramref name="psiAlertThresh"/>
    ///         and <paramref name="psiRetrainThresh"/>.</item>
    ///   <item>If any feature exceeds the alert threshold, creates an <see cref="Alert"/> of
    ///         type <see cref="AlertType.MLModelDegraded"/> and optionally queues a retrain.</item>
    /// </list>
    /// </summary>
    /// <param name="model">The active <see cref="MLModel"/> to evaluate.</param>
    /// <param name="readCtx">Read DbContext for querying candle history.</param>
    /// <param name="writeCtx">Write DbContext for persisting alerts and training runs.</param>
    /// <param name="candleWindowDays">Number of past days of candles to form the "actual" distribution.</param>
    /// <param name="psiAlertThresh">PSI value at or above which an alert is emitted (default 0.25 = "major shift").</param>
    /// <param name="psiRetrainThresh">PSI value at or above which a feature contributes to the retrain counter (default 0.40).</param>
    /// <param name="alertDest">Webhook/destination string embedded in the alert record.</param>
    /// <param name="ct">Cancellation token.</param>
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
        // The ModelSnapshot contains the training-time statistics needed for PSI:
        //   • FeatureQuantileBreakpoints: per-feature bin edges (10 bins) defining
        //     the training distribution shape.
        //   • Means / Stds: used to standardise the recent live feature values so
        //     they can be compared on the same scale as the training data.
        //   • Features: human-readable feature names for alert payloads.
        ModelSnapshot? snap = null;
        try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }

        if (snap is null || snap.FeatureQuantileBreakpoints.Length == 0)
        {
            // Models without quantile breakpoints cannot be PSI-checked.
            // This is expected for externally imported models or very old snapshots.
            _logger.LogDebug("PSI skipped model {Id} — no quantile breakpoints in snapshot.", model.Id);
            return;
        }

        int featureCount = snap.Features.Length > 0 ? snap.Features.Length : MLFeatureHelper.FeatureCount;

        // ── Load recent candles ───────────────────────────────────────────────
        // The "actual" distribution is derived from candles in the configured rolling window.
        // A minimum candle count (LookbackWindow + 5) is needed to form at least one valid
        // feature vector via the indicator lookback calculations.
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
        // BuildTrainingSamples applies the same indicator pipeline used during training,
        // producing (Features[], Direction, Magnitude) tuples for each valid candle row.
        var recentSamples = MLFeatureHelper.BuildTrainingSamples(candles);
        if (recentSamples.Count == 0) return;

        // Standardise using snapshot means/stds so the live features are on the same
        // numerical scale as the quantile breakpoints recorded at training time.
        var recentStandardised = recentSamples
            .Select(s => s with { Features = MLFeatureHelper.Standardize(s.Features, snap.Means, snap.Stds) })
            .ToList();

        if (snap.FracDiffD > 0.0)
            recentStandardised = MLFeatureHelper.ApplyFractionalDifferencing(recentStandardised, featureCount, snap.FracDiffD);

        if (snap.ActiveFeatureMask is { Length: > 0 } activeMask && activeMask.Length == featureCount)
        {
            for (int i = 0; i < recentStandardised.Count; i++)
            {
                var features = (float[])recentStandardised[i].Features.Clone();
                for (int j = 0; j < featureCount; j++)
                    if (!activeMask[j]) features[j] = 0f;
                recentStandardised[i] = recentStandardised[i] with { Features = features };
            }
        }

        // ── Compute PSI per feature ───────────────────────────────────────────
        // PSI = Σ_i (A_i − E_i) × ln(A_i / E_i)
        //   where A_i = proportion of "actual" (recent) samples falling in bin i
        //         E_i = proportion of "expected" (training) samples falling in bin i
        //
        // Interpretation:
        //   PSI < 0.10  → no significant change
        //   0.10 ≤ PSI < 0.25 → moderate shift; monitor closely
        //   PSI ≥ 0.25  → major shift; alert and consider retraining
        //   PSI ≥ 0.40  → severe shift; contributes to automatic retrain trigger
        int    highPsiCount   = 0;   // Features that breach the alert threshold.
        int    retrainTrigger = 0;   // Features that breach the stricter retrain threshold.
        var    psiValues      = new Dictionary<string, double>();

        for (int j = 0; j < Math.Min(featureCount, snap.FeatureQuantileBreakpoints.Length); j++)
        {
            // binEdges are the decile boundaries (9 edges → 10 bins) stored in the snapshot.
            // The training distribution is uniform across these bins by construction, so the
            // expected distribution is represented by a synthetic uniform sample.
            double[] binEdges = snap.FeatureQuantileBreakpoints[j];
            if (binEdges.Length == 0) continue;

            // Build current (actual) feature distribution from recent standardised values.
            double[] recentVals = recentStandardised.Select(s => j < s.Features.Length ? (double)s.Features[j] : 0.0).ToArray();

            // Training distribution is implicitly uniform across quantile bins (by construction)
            // We approximate by treating each bin edge as a uniform-population bin edge.
            // We use a synthetic "training" array that perfectly fills the bins equally.
            int    trainN   = recentVals.Length;     // same count for fair comparison
            double[] trainVals = GenerateUniformFromEdges(binEdges, trainN);

            // Delegate PSI calculation to the shared helper, which bins both arrays
            // against binEdges and applies the standard PSI formula.
            double psi = MLFeatureHelper.ComputeFeaturePsi(binEdges, trainVals, recentVals);

            string featureName = j < snap.Features.Length ? snap.Features[j] : $"F{j}";
            psiValues[featureName] = psi;

            if (psi >= psiAlertThresh)
            {
                // PSI ≥ 0.25 means the feature's live distribution has shifted substantially
                // from its training-time shape — the model may no longer "see" this feature
                // in the way it was trained to expect.
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
        // Suppress the alert if a retrain is already queued or running for this
        // symbol/timeframe combination — no need to double-alert.
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

        // Persist the alert with a structured JSON payload describing which features drifted.
        // The top-5 features by PSI are included to help operators prioritise investigation.
        writeCtx.Set<Alert>().Add(new Alert
        {
            Symbol        = model.Symbol,
            AlertType     = AlertType.MLModelDegraded,
            ConditionJson = JsonSerializer.Serialize(new
            {
                DetectorType     = "FeaturePSI",
                ModelId          = model.Id,
                Timeframe        = model.Timeframe.ToString(),
                HighPsiFeatures  = highPsiCount,
                TotalFeatures    = psiValues.Count,
                AlertThreshold   = psiAlertThresh,
                RetrainThreshold = psiRetrainThresh,
                // Top 5 drifting features allow quick triage without reading full logs.
                TopPsiFeatures   = psiValues
                    .OrderByDescending(kv => kv.Value)
                    .Take(5)
                    .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 4)),
            }),
            IsActive = true,
        });

        // Queue retrain when majority of features show severe drift.
        // Threshold: more than half of all checked features exceed psiRetrainThresh.
        // A full retrain over the past year is queued with TriggerType.AutoDegrading
        // so the MLTrainingWorker can pick it up in the next cycle.
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
    /// <remarks>
    /// <para>
    /// During training, quantile breakpoints are computed such that each bin contains
    /// an equal proportion of the training samples — i.e. the training distribution is
    /// uniform by construction. To compute PSI we need an explicit "training" value array
    /// that reflects this uniformity.
    /// </para>
    /// <para>
    /// This method fills each bin with <c>n / (edges.Length + 1)</c> copies of the bin
    /// midpoint. The midpoint is used instead of random values so PSI computation is
    /// deterministic and reproducible across polling cycles.
    /// </para>
    /// <para>
    /// The outermost bin boundaries are extrapolated by half the total range in each
    /// direction, ensuring all bins have finite, non-zero widths even at the extremes.
    /// </para>
    /// </remarks>
    /// <param name="edges">Sorted quantile breakpoints from the training snapshot (e.g. 9 edges = 10 bins).</param>
    /// <param name="n">Total number of synthetic samples to generate, matching the live sample count
    /// so PSI bin proportions are directly comparable.</param>
    /// <returns>Array of synthetic uniformly-distributed values spanning the same range as the edges.</returns>
    private static double[] GenerateUniformFromEdges(double[] edges, int n)
    {
        // Number of bins = number of edges + 1 (one bin per gap plus two tail bins).
        int bins = edges.Length + 1;
        int perBin = Math.Max(1, n / bins);
        var result = new List<double>(bins * perBin);

        // Extrapolate the left tail boundary as half the total range to the left of edges[0].
        double prevEdge = edges.Length > 0 ? edges[0] - (edges[^1] - edges[0]) * 0.5 : -3.0;

        for (int b = 0; b < bins; b++)
        {
            // Determine lower and upper boundary of this bin.
            double lo = b == 0      ? prevEdge             : edges[b - 1];
            double hi = b < edges.Length ? edges[b]        : edges[^1] + (edges[^1] - edges[0]) * 0.5;
            // Use the bin midpoint to represent all samples in this bin — deterministic and stable.
            double mid = (lo + hi) / 2.0;
            for (int i = 0; i < perBin; i++)
                result.Add(mid);
        }

        return [.. result];
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key is absent or the stored
    /// value cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target type (e.g. <see cref="int"/>, <see cref="double"/>, <see cref="string"/>).</typeparam>
    /// <param name="ctx">EF Core DbContext to query.</param>
    /// <param name="key">Configuration key, e.g. <c>"MLFeaturePsi:PollIntervalSeconds"</c>.</param>
    /// <param name="defaultValue">Fallback value used when the key is missing or conversion fails.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed configuration value, or <paramref name="defaultValue"/>.</returns>
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
