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
/// Detects covariate (feature distribution) shift in active ML models using the
/// Population Stability Index (PSI).
///
/// <para>
/// Covariate shift occurs when the statistical distribution of input features drifts away
/// from the distribution seen during training — even before prediction accuracy visibly
/// degrades. PSI provides an early-warning signal, typically catching market-regime changes
/// that <see cref="MLDriftMonitorWorker"/> (which only watches accuracy) misses.
/// </para>
///
/// Algorithm:
/// <list type="number">
///   <item>For every active model, deserialise the stored <see cref="ModelSnapshot"/> to read
///         the training-time means and standard deviations.</item>
///   <item>Load <c>MLCovariate:WindowDays</c> days of recent closed candles for the model's
///         symbol/timeframe and recompute the 28-element feature vectors.</item>
///   <item>Z-score standardise the recent features using the <b>training</b> means/stds.
///         Values that are still ~N(0,1) indicate no shift; values far from zero signal drift.</item>
///   <item>Compute PSI per feature by binning the standardised values into 10 equal-width
///         buckets over [−3, +3] plus two tail buckets, and comparing against the expected
///         N(0,1) bucket probabilities.</item>
///   <item>If <b>any feature</b> PSI exceeds <c>MLCovariate:PsiThreshold</c> (default 0.20),
///         queue an <see cref="MLTrainingRun"/> with <see cref="TriggerType.AutoDegrading"/>
///         — the same trigger used by the accuracy-based drift monitor.</item>
/// </list>
///
/// PSI interpretation: &lt;0.10 no change; 0.10–0.20 moderate; &gt;0.20 significant shift.
/// </summary>
public sealed class MLCovariateShiftWorker : BackgroundService
{
    // ── Config keys ────────────────────────────────────────────────────────────
    private const string CK_PollSecs              = "MLCovariate:PollIntervalSeconds";
    private const string CK_WindowDays            = "MLCovariate:WindowDays";
    private const string CK_PsiThreshold          = "MLCovariate:PsiThreshold";
    private const string CK_MinCandles            = "MLCovariate:MinCandles";
    private const string CK_TrainingDays          = "MLTraining:TrainingDataWindowDays";
    // Multivariate drift: mean squared z-score (expected = 1.0 under N(0,1))
    private const string CK_MultivariateThreshold = "MLCovariate:MultivariateThreshold";
    // Per-feature PSI threshold for individual feature drift alerting
    private const string CK_PerFeaturePsiThreshold = "MLCovariate:PerFeaturePsiThreshold";

    // ── PSI binning constants ─────────────────────────────────────────────────
    // 10 equal-width bins over [−3, +3] + 2 tail bins = 12 bins total.
    // Expected bin probability under N(0,1):
    //   tail bins: ~0.135 % each (z < −3 or z > +3)
    //   inner bins: width 0.6, probability ≈ Φ(b+0.3) − Φ(b−0.3)
    private const int    NumInnerBins = 10;
    private const double BinWidth     = 6.0 / NumInnerBins;   // 0.6 std-widths per bin
    private const double ZMin         = -3.0;

    // Pre-computed expected N(0,1) probability for each bin (outer two are tails)
    // Using the normal CDF approximation. Bin 0 = left tail (z < -3), bins 1-10 = inner, bin 11 = right tail.
    private static readonly double[] ExpectedBinProb = ComputeExpectedBinProbs();

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLCovariateShiftWorker>   _logger;

    public MLCovariateShiftWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<MLCovariateShiftWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLCovariateShiftWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600; // default hourly

            try
            {
                await using var scope    = _scopeFactory.CreateAsyncScope();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>   (ctx, CK_PollSecs,              3600, stoppingToken);
                int    windowDays           = await GetConfigAsync<int>   (ctx, CK_WindowDays,            30,   stoppingToken);
                double psiThreshold         = await GetConfigAsync<double>(ctx, CK_PsiThreshold,          0.20, stoppingToken);
                int    minCandles           = await GetConfigAsync<int>   (ctx, CK_MinCandles,            100,  stoppingToken);
                int    trainingDays         = await GetConfigAsync<int>   (ctx, CK_TrainingDays,          365,  stoppingToken);
                double multivariateThreshold = await GetConfigAsync<double>(ctx, CK_MultivariateThreshold, 1.5,  stoppingToken);
                double perFeaturePsiThreshold = await GetConfigAsync<double>(ctx, CK_PerFeaturePsiThreshold, 0.25, stoppingToken);

                await CheckAllModelsAsync(
                    ctx, writeCtx, windowDays, psiThreshold, minCandles, trainingDays,
                    multivariateThreshold, perFeaturePsiThreshold, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLCovariateShiftWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLCovariateShiftWorker stopping.");
    }

    // ── Per-model covariate check ─────────────────────────────────────────────

    /// <summary>
    /// Iterates over all active <see cref="MLModel"/> records that have serialised
    /// <c>ModelBytes</c> (i.e. a <see cref="MLModels.Shared.ModelSnapshot"/> with training
    /// means/stds), and dispatches each to <see cref="CheckModelCovariateShiftAsync"/>.
    ///
    /// Models without <c>ModelBytes</c> are skipped — they cannot be evaluated because
    /// the training-time feature distribution statistics are stored inside the snapshot.
    /// </summary>
    private async Task CheckAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        double                                  psiThreshold,
        int                                     minCandles,
        int                                     trainingDays,
        double                                  multivariateThreshold,
        double                                  perFeaturePsiThreshold,
        CancellationToken                       ct)
    {
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        _logger.LogDebug(
            "Covariate shift check for {Count} active model(s) (window={Days}d psi={T:F2} mv={M:F2}).",
            activeModels.Count, windowDays, psiThreshold, multivariateThreshold);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();
            await CheckModelCovariateShiftAsync(
                model, readCtx, writeCtx, windowDays, psiThreshold, minCandles, trainingDays,
                multivariateThreshold, perFeaturePsiThreshold, ct);
        }
    }

    /// <summary>
    /// Checks a single <see cref="MLModel"/> for covariate shift by comparing the input feature
    /// distribution of recent market data against the distribution observed during training.
    ///
    /// <para><b>Two complementary shift detectors run in parallel:</b></para>
    /// <list type="bullet">
    ///   <item><b>Univariate PSI</b> — PSI is computed for each feature independently using
    ///         importance-weighted aggregation (see <see cref="ComputePsi"/>). Any feature
    ///         exceeding <paramref name="psiThreshold"/> (default 0.20 = "significant shift")
    ///         triggers the detector. Importance weighting ensures high-signal features
    ///         (large <c>FeatureImportance</c> values) carry more weight in the aggregate PSI.</item>
    ///   <item><b>Multivariate mean-sq-z-score</b> — computes the mean of all squared
    ///         z-scores across every feature and every recent sample. Under the training
    ///         distribution (N(0,1) after standardisation) the expected value is 1.0.
    ///         A value significantly above 1.0 signals joint distributional shift even when
    ///         no single feature PSI exceeds the threshold — for example, when several features
    ///         shift modestly but in a correlated direction that PSI alone misses.</item>
    /// </list>
    ///
    /// <para>When either detector fires and no retraining run is already queued, a new
    /// <see cref="MLTrainingRun"/> with <see cref="TriggerType.AutoDegrading"/> is created.
    /// This is the same trigger used by <see cref="MLDriftMonitorWorker"/> so the two workers
    /// produce identical run records and share the same deduplication logic.</para>
    /// </summary>
    /// <param name="model">The active model to evaluate for covariate shift.</param>
    /// <param name="readCtx">EF read context — used for all SELECT queries.</param>
    /// <param name="writeCtx">EF write context — used only to persist a new training run.</param>
    /// <param name="windowDays">How many days of recent closed candles to load for comparison.</param>
    /// <param name="psiThreshold">PSI value above which a feature is considered shifted (0.10 = moderate, 0.20 = significant).</param>
    /// <param name="minCandles">Minimum closed candles required to compute reliable feature statistics.</param>
    /// <param name="trainingDays">Window size (days) for the queued retraining run.</param>
    /// <param name="multivariateThreshold">Mean squared z-score threshold above which multivariate shift fires.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task CheckModelCovariateShiftAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        double                                  psiThreshold,
        int                                     minCandles,
        int                                     trainingDays,
        double                                  multivariateThreshold,
        double                                  perFeaturePsiThreshold,
        CancellationToken                       ct)
    {
        // ── 1. Deserialise snapshot to get training means/stds ────────────────
        ModelSnapshot? snap;
        try
        {
            snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Model {Id}: could not deserialise snapshot — skipping PSI check.", model.Id);
            return;
        }

        if (snap is null || snap.Means.Length == 0 || snap.Stds.Length == 0)
        {
            _logger.LogDebug("Model {Id}: snapshot has no means/stds — skipping PSI check.", model.Id);
            return;
        }

        // ── 2. Load recent candles ────────────────────────────────────────────
        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        var candles = await readCtx.Set<Candle>()
            .Where(c => c.Symbol    == model.Symbol    &&
                        c.Timeframe == model.Timeframe &&
                        c.Timestamp >= windowStart     &&
                        c.IsClosed)
            .OrderBy(c => c.Timestamp)
            .AsNoTracking()
            .ToListAsync(ct);

        int required = MLFeatureHelper.LookbackWindow + minCandles;
        if (candles.Count < required)
        {
            _logger.LogDebug(
                "Model {Id} ({Symbol}/{Tf}): only {N} recent candles (need {Min}) — skipping PSI.",
                model.Id, model.Symbol, model.Timeframe, candles.Count, required);
            return;
        }

        // ── 3. Build feature vectors from recent candles ──────────────────────
        var recentSamples = MLFeatureHelper.BuildTrainingSamples(candles);

        if (recentSamples.Count < 20)
        {
            _logger.LogDebug(
                "Model {Id}: insufficient feature samples ({N}) for PSI — skipping.",
                model.Id, recentSamples.Count);
            return;
        }

        int featureCount = snap.Means.Length;

        // ── 4. Z-score recent features using TRAINING means/stds ─────────────
        // Values will be ~N(0,1) if distribution hasn't shifted.
        var recentStd = recentSamples.Select(s =>
        {
            var z = new float[featureCount];
            for (int j = 0; j < featureCount && j < s.Features.Length; j++)
            {
                float std = snap.Stds[j] > 1e-8f ? snap.Stds[j] : 1f;
                z[j] = (s.Features[j] - snap.Means[j]) / std;
            }
            return z;
        }).ToList();

        // ── 5a. Build feature importance weights from snapshot ────────────────
        // FeatureImportance is normalised to sum to 1.0 by the trainer.
        // Fall back to equal weighting when not yet computed.
        bool hasImportance = snap.FeatureImportance.Length == featureCount &&
                             snap.FeatureImportance.Sum() > 0;

        // ── 5b. Compute per-feature PSI with importance weighting ─────────────
        double maxPsi          = 0;
        int    maxFeature      = 0;
        double weightedPsiSum  = 0;
        double importanceSum   = 0;
        var    perFeaturePsiValues = new double[featureCount];

        for (int j = 0; j < featureCount; j++)
        {
            double psi        = ComputePsi(recentStd, j);
            double importance = hasImportance ? snap.FeatureImportance[j] : 1.0 / featureCount;

            perFeaturePsiValues[j] = psi;
            weightedPsiSum += psi * importance;
            importanceSum  += importance;

            if (psi > maxPsi)
            {
                maxPsi    = psi;
                maxFeature = j;
            }
        }

        double weightedPsi = importanceSum > 0 ? weightedPsiSum / importanceSum : maxPsi;

        string featureName = maxFeature < MLFeatureHelper.FeatureNames.Length
            ? MLFeatureHelper.FeatureNames[maxFeature]
            : $"feature[{maxFeature}]";

        // ── 5d. Per-feature PSI breakdown: identify individually drifted features ──
        var driftedFeatures = new List<(string Name, double Psi)>();
        for (int j = 0; j < featureCount; j++)
        {
            if (perFeaturePsiValues[j] > perFeaturePsiThreshold)
            {
                string fname = j < MLFeatureHelper.FeatureNames.Length
                    ? MLFeatureHelper.FeatureNames[j]
                    : $"feature[{j}]";

                driftedFeatures.Add((fname, perFeaturePsiValues[j]));
            }
        }

        // Emit ONE aggregated warning per model per cycle instead of N per-feature warnings.
        // Persist the full per-feature JSON to EngineConfig for downstream workers that need
        // the detail (e.g. MLFeatureRankShiftWorker). Previously this loop produced ~20
        // warnings per model per cycle, dominating log volume.
        if (driftedFeatures.Count > 0)
        {
            var topDrifted = driftedFeatures
                .OrderByDescending(f => f.Psi)
                .Take(3)
                .Select(f => $"{f.Name}(PSI={f.Psi:F2})");
            _logger.LogWarning(
                "Covariate drift for {Symbol}/{Tf}: {Count}/{Total} features above PSI threshold (top: {Top})",
                model.Symbol, model.Timeframe, driftedFeatures.Count, featureCount,
                string.Join(", ", topDrifted));

            string driftedJson = System.Text.Json.JsonSerializer.Serialize(
                driftedFeatures.Select(f => new { featureName = f.Name, psi = f.Psi }));

            string configKey = $"MLCovariate:{model.Symbol}:{model.Timeframe}:DriftedFeatures";
            await UpsertConfigAsync(writeCtx, configKey, driftedJson, ct);
        }

        // ── 5c. Multivariate drift: mean of squared z-scores across all features ──
        // Under N(0, 1) training distribution the expected value is 1.0 per feature.
        // Values significantly above 1.0 indicate joint distributional shift even when
        // individual PSI scores remain below the threshold.
        double multivariateScore = 0;
        if (recentStd.Count > 0)
        {
            double totalSqZ = 0;
            foreach (var z in recentStd)
                for (int j = 0; j < featureCount && j < z.Length; j++)
                    totalSqZ += z[j] * z[j];
            multivariateScore = totalSqZ / ((double)recentStd.Count * featureCount);
        }

        bool univariateShift   = weightedPsi >= psiThreshold;
        bool multivariateShift = multivariateScore >= multivariateThreshold;

        _logger.LogDebug(
            "Model {Id} ({Symbol}/{Tf}): weightedPSI={WPsi:F4} maxPSI={Psi:F4} (feature={Feat}) " +
            "mvScore={Mv:F4} — psiThresh={PT:F2} mvThresh={MT:F2}",
            model.Id, model.Symbol, model.Timeframe,
            weightedPsi, maxPsi, featureName, multivariateScore,
            psiThreshold, multivariateThreshold);

        if (!univariateShift && !multivariateShift)
            return; // No significant shift detected

        // ── 6. Check if retraining is already queued ─────────────────────────
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running),
                      ct);

        if (alreadyQueued)
        {
            _logger.LogDebug(
                "Model {Id}: covariate shift detected (PSI={Psi:F4}) but retraining already queued.",
                model.Id, maxPsi);
            return;
        }

        // ── 7. Queue retraining ───────────────────────────────────────────────
        var now = DateTime.UtcNow;

        // Improvement #2: tag with covariate shift metadata (including per-feature drifted features)
        string covariateMetadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            maxPsi,
            psiFeature      = featureName,
            msz             = multivariateScore,
            driftedFeatures = driftedFeatures.Select(f => new { featureName = f.Name, psi = f.Psi }).ToArray(),
        });

        var run = new MLTrainingRun
        {
            Symbol            = model.Symbol,
            Timeframe         = model.Timeframe,
            TriggerType       = TriggerType.AutoDegrading,
            Status            = RunStatus.Queued,
            FromDate          = now.AddDays(-trainingDays),
            ToDate            = now,
            StartedAt         = now,
            DriftTriggerType  = "CovariateShift",
            DriftMetadataJson = covariateMetadata,
            Priority          = 1, // Improvement #9: drift-triggered = priority 1
        };

        writeCtx.Set<MLTrainingRun>().Add(run);
        await writeCtx.SaveChangesAsync(ct);

        string shiftKind = (univariateShift, multivariateShift) switch
        {
            (true,  true)  => $"univariate (wPSI={weightedPsi:F4} maxPSI={maxPsi:F4} on '{featureName}') + multivariate (mvScore={multivariateScore:F4})",
            (true,  false) => $"univariate (wPSI={weightedPsi:F4} maxPSI={maxPsi:F4} on '{featureName}')",
            (false, true)  => $"multivariate (mvScore={multivariateScore:F4})",
            _              => "unknown",
        };

        _logger.LogWarning(
            "Covariate shift detected for model {Id} ({Symbol}/{Tf}): {Kind}. " +
            "Queued retraining run {RunId}.",
            model.Id, model.Symbol, model.Timeframe, shiftKind, run.Id);
    }

    // ── PSI calculation ───────────────────────────────────────────────────────

    /// <summary>
    /// Computes the Population Stability Index for a single feature column
    /// (<paramref name="featureIdx"/>) in the standardised recent feature matrix.
    ///
    /// Bins: bin 0 = left tail (z &lt; −3), bins 1–10 = inner (width 0.6 each), bin 11 = right tail (z ≥ +3).
    /// Expected distribution = N(0, 1) probabilities per bin.
    /// PSI = Σ (actual_pct − expected_pct) × ln(actual_pct / expected_pct)
    /// </summary>
    private static double ComputePsi(List<float[]> recentStd, int featureIdx)
    {
        int   n       = recentStd.Count;
        int   numBins = NumInnerBins + 2; // includes two tails
        var   counts  = new int[numBins];

        foreach (var z in recentStd)
        {
            double v = featureIdx < z.Length ? z[featureIdx] : 0f;

            if (v < ZMin)
            {
                counts[0]++;
            }
            else if (v >= -ZMin)  // >= +3
            {
                counts[numBins - 1]++;
            }
            else
            {
                int bin = (int)((v - ZMin) / BinWidth);
                bin = Math.Clamp(bin, 0, NumInnerBins - 1);
                counts[bin + 1]++;  // +1 to skip left-tail bin
            }
        }

        double psi = 0;
        for (int b = 0; b < numBins; b++)
        {
            // Laplace smoothing: add 0.5 to avoid log(0)
            double actual   = (counts[b] + 0.5) / (n + 0.5 * numBins);
            double expected = ExpectedBinProb[b];

            psi += (actual - expected) * Math.Log(actual / expected);
        }

        return Math.Max(0, psi);
    }

    /// <summary>
    /// Pre-computes the expected probability for each bin under N(0, 1)
    /// using the error-function approximation of the normal CDF.
    /// </summary>
    private static double[] ComputeExpectedBinProbs()
    {
        int numBins = NumInnerBins + 2;
        var probs   = new double[numBins];

        // Left tail: P(Z < -3)
        probs[0] = NormalCdf(-3.0);

        // Inner bins: P(z_lo ≤ Z < z_hi)
        for (int b = 0; b < NumInnerBins; b++)
        {
            double lo = ZMin + b * BinWidth;
            double hi = lo + BinWidth;
            probs[b + 1] = NormalCdf(hi) - NormalCdf(lo);
        }

        // Right tail: P(Z >= +3)
        probs[numBins - 1] = 1.0 - NormalCdf(3.0);

        return probs;
    }

    /// <summary>
    /// Standard normal CDF approximation via the complementary error function.
    /// Accurate to within ~10⁻⁷ for |z| ≤ 8.
    /// </summary>
    private static double NormalCdf(double z) =>
        0.5 * (1.0 + Erf(z / Math.Sqrt(2.0)));

    /// <summary>
    /// Abramowitz and Stegun approximation for erf(x), maximum error &lt; 1.5 × 10⁻⁷.
    /// </summary>
    private static double Erf(double x)
    {
        const double p  =  0.3275911;
        const double a1 =  0.254829592;
        const double a2 = -0.284496736;
        const double a3 =  1.421413741;
        const double a4 = -1.453152027;
        const double a5 =  1.061405429;

        int    sign = x < 0 ? -1 : 1;
        double xAbs = Math.Abs(x);
        double t    = 1.0 / (1.0 + p * xAbs);
        double poly = t * (a1 + t * (a2 + t * (a3 + t * (a4 + t * a5))));
        return sign * (1.0 - poly * Math.Exp(-xAbs * xAbs));
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Upserts an <see cref="EngineConfig"/> entry. Updates the existing row if the key
    /// already exists, otherwise inserts a new one.
    /// </summary>
    private static Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(writeCtx, key, value, dataType: LascodiaTradingEngine.Domain.Enums.ConfigDataType.Json, ct: ct);

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/> or returns <paramref name="defaultValue"/>
    /// when the key is absent or its stored string cannot be parsed into <typeparamref name="T"/>.
    /// </summary>
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
