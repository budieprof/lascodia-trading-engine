using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Scores trade signals using the active <see cref="MLModel"/> for the relevant
/// symbol/timeframe. Uses the same <see cref="MLFeatureHelper"/> as the trainer
/// to guarantee feature/inference parity.
///
/// Inference pipeline:
/// <list type="number">
///   <item>Detect the current market regime for the symbol; prefer a regime-specific
///         <see cref="MLModel"/> (<c>RegimeScope</c> matches), fall back to the global
///         model (<c>RegimeScope</c> is null).</item>
///   <item>Deserialise <see cref="ModelSnapshot"/> from <c>ModelBytes</c>
///         (cached in <see cref="IMemoryCache"/> keyed by model ID for 30 min).</item>
///   <item>Build the 29-element feature vector via <see cref="MLFeatureHelper.BuildFeatureVector"/>.</item>
///   <item>Apply COT min-max normalisation using training-time bounds stored in the snapshot
///         (falls back to the legacy ÷100 000 / ÷10 000 divisors when bounds are absent).</item>
///   <item>Z-score standardise using the snapshot's stored means/stds.</item>
///   <item>Apply fractional differencing (order <c>FracDiffD</c>) when the snapshot was
///         trained with it — blends standardised feature vectors from recent lags using
///         the same convolution weights as the trainer (best-effort; skipped when
///         candle history is too short).</item>
///   <item>Zero features excluded by the snapshot's <c>ActiveFeatureMask</c> (pruned features).</item>
///   <item>Run model inference → raw probability. Three paths: TCN (causal dilated conv),
///         QRF (GbmTree leaf-fraction forest with MetaWeights/GES/CalAccuracy aggregation),
///         or bagged ensemble (logistic / MLP / poly).</item>
///   <item>Apply Platt scaling: sigmoid(A × logit(p) + B).</item>
///   <item>Derive direction (Buy if calibP ≥ 0.5), magnitude, confidence, and inter-learner
///         disagreement (returned in <see cref="MLScoreResult.EnsembleDisagreement"/>).</item>
/// </list>
/// </summary>
public sealed class MLSignalScorer : IMLSignalScorer
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private const string SnapshotCacheKeyPrefix  = "MLSnapshot:";
    private static readonly TimeSpan SnapshotCacheDuration = TimeSpan.FromMinutes(30);

    private const string RegimeAccCacheKeyPrefix = "MLRegimeAcc:";
    private static readonly TimeSpan RegimeAccCacheDuration = TimeSpan.FromMinutes(15);

    private const string RegimePenaltyCacheKeyPrefix = "MLRegimePenalty:";
    private static readonly TimeSpan RegimePenaltyCacheDuration = TimeSpan.FromMinutes(5);

    private const string ColdStartCacheKeyPrefix = "MLColdStart:";
    private static readonly TimeSpan ColdStartCacheDuration = TimeSpan.FromMinutes(5);

    private const string CrossTfCacheKeyPrefix = "MLCrossTf:";
    private static readonly TimeSpan CrossTfCacheDuration = TimeSpan.FromMinutes(5);

    private const string KellyLiveCacheKeyPrefix = "MLKellyLive:";
    private static readonly TimeSpan KellyLiveCacheDuration = TimeSpan.FromMinutes(15);

    private const string CooldownCacheKeyPrefix = "MLCooldownExp:";
    private static readonly TimeSpan CooldownCacheDuration = TimeSpan.FromMinutes(2);

    private readonly IReadApplicationDbContext _context;
    private readonly IMemoryCache              _cache;
    private readonly ILogger<MLSignalScorer>   _logger;

    public MLSignalScorer(
        IReadApplicationDbContext context,
        IMemoryCache              cache,
        ILogger<MLSignalScorer>   logger)
    {
        _context = context;
        _cache   = cache;
        _logger  = logger;
    }

    public async Task<MLScoreResult> ScoreAsync(
        TradeSignal           signal,
        IReadOnlyList<Candle> candles,
        CancellationToken     cancellationToken)
    {
        // ── 1. Find active model (regime-aware, falls back to global) ────────
        var signalTimeframe = candles.Count > 0 ? candles[0].Timeframe : Timeframe.H1;
        var db              = _context.GetDbContext();

        // Detect current regime so we can prefer a regime-specific sub-model.
        string? currentRegime = null;
        try
        {
            var regimeSnap = await db.Set<MarketRegimeSnapshot>()
                .AsNoTracking()
                .Where(r => r.Symbol == signal.Symbol &&
                            r.Timeframe == signalTimeframe &&
                            !r.IsDeleted)
                .OrderByDescending(r => r.DetectedAt)
                .FirstOrDefaultAsync(cancellationToken);

            currentRegime = regimeSnap?.Regime.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Regime lookup failed for {Symbol}/{Tf} — using global model",
                signal.Symbol, signalTimeframe);
        }

        // Try regime-specific model first; fall back to global (RegimeScope == null).
        MLModel? model = null;
        if (currentRegime is not null)
        {
            model = await db.Set<MLModel>()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Symbol      == signal.Symbol &&
                         x.Timeframe   == signalTimeframe &&
                         x.RegimeScope == currentRegime &&
                         x.IsActive    &&
                         !x.IsDeleted,
                    cancellationToken);
        }

        if (model is null)
        {
            model = await db.Set<MLModel>()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Symbol      == signal.Symbol &&
                         x.Timeframe   == signalTimeframe &&
                         x.RegimeScope == null &&
                         x.IsActive    &&
                         !x.IsDeleted,
                    cancellationToken);
        }

        if (model?.ModelBytes is not { Length: > 0 })
        {
            _logger.LogDebug(
                "No active ML model for {Symbol}/{Tf} — signal proceeds unscored",
                signal.Symbol, signalTimeframe);
            return new MLScoreResult(null, null, null, null);
        }

        // ── 1b. Suppression gate (Round 11) ──────────────────────────────────
        // MLSignalSuppressionWorker sets IsSuppressed when rolling accuracy drops
        // below the hard floor. Before giving up, try the fallback champion that
        // MLSuppressionRollbackWorker may have activated (Round 13).
        if (model.IsSuppressed)
        {
            MLModel? fallback = null;
            try
            {
                fallback = await db.Set<MLModel>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        x => x.Symbol           == signal.Symbol      &&
                             x.Timeframe        == signalTimeframe     &&
                             x.IsFallbackChampion                      &&
                             x.IsActive         && !x.IsDeleted,
                        cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Fallback champion lookup failed for {Symbol}/{Tf}",
                    signal.Symbol, signalTimeframe);
            }

            if (fallback?.ModelBytes is not { Length: > 0 })
            {
                _logger.LogDebug(
                    "Scoring suppressed for {Symbol}/{Tf} model {Id} — no fallback champion available.",
                    signal.Symbol, signalTimeframe, model.Id);
                return new MLScoreResult(null, null, null, null);
            }

            _logger.LogDebug(
                "Scoring suppressed for {Symbol}/{Tf} primary model {Id} — " +
                "routing to fallback champion {FbId}.",
                signal.Symbol, signalTimeframe, model.Id, fallback.Id);
            model = fallback;
        }

        // ── 2. Deserialise model snapshot (cached) ───────────────────────────
        var cacheKey = $"{SnapshotCacheKeyPrefix}{model.Id}";
        if (!_cache.TryGetValue<ModelSnapshot>(cacheKey, out var snap) || snap is null)
        {
            try
            {
                snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialise ModelSnapshot for model {Id}", model.Id);
                return new MLScoreResult(null, null, null, null);
            }

            if (snap is not null)
                _cache.Set(cacheKey, snap, SnapshotCacheDuration);
        }

        if (snap is null || (snap.Weights.Length == 0 && string.IsNullOrEmpty(snap.ConvWeightsJson)))
        {
            _logger.LogWarning("Model {Id} snapshot is empty or has no weights", model.Id);
            return new MLScoreResult(null, null, null, null);
        }

        // ── 3. Validate candle window ────────────────────────────────────────
        if (candles.Count < MLFeatureHelper.LookbackWindow + 1)
        {
            _logger.LogDebug(
                "Insufficient candles ({Count}) for inference — need {Min}",
                candles.Count, MLFeatureHelper.LookbackWindow + 1);
            return new MLScoreResult(null, null, null, null);
        }

        // ── 4. Load COT data ─────────────────────────────────────────────────
        var baseCurrency = signal.Symbol.Length >= 3 ? signal.Symbol[..3] : signal.Symbol;
        var signalTs     = signal.GeneratedAt;

        COTReport? cotReport = null;
        try
        {
            cotReport = await db.Set<COTReport>()
                .AsNoTracking()
                .Where(c => c.Currency == baseCurrency && c.ReportDate <= signalTs)
                .OrderByDescending(c => c.ReportDate)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "COT lookup failed for {Currency} — using zero entry", baseCurrency);
        }

        // ── 5. Build COT entry using snapshot normalisation bounds ───────────
        CotFeatureEntry cotEntry;
        if (cotReport is null)
        {
            cotEntry = CotFeatureEntry.Zero;
        }
        else
        {
            cotEntry = BuildCotEntry(cotReport, snap);
        }

        // ── 6. Build feature vector ──────────────────────────────────────────
        var orderedCandles = candles.OrderBy(c => c.Timestamp).ToList();
        var current  = orderedCandles[^1];
        var previous = orderedCandles[^2];
        var window   = orderedCandles.TakeLast(MLFeatureHelper.LookbackWindow + 1)
                                     .Take(MLFeatureHelper.LookbackWindow)
                                     .ToList();

        float[] rawFeatures = MLFeatureHelper.BuildFeatureVector(window, current, previous, cotEntry);

        // ── 7. Standardise using snapshot's stored means and stds ────────────
        // Prefer per-regime means/stds when the current regime is known and the
        // snapshot was trained with regime-specific standardisation.
        int featureCount = snap.Features.Length > 0
            ? snap.Features.Length
            : rawFeatures.Length;

        float[] useMeans = snap.Means;
        float[] useStds  = snap.Stds;
        if (currentRegime is not null &&
            snap.RegimeMeans.TryGetValue(currentRegime, out var rMeans) &&
            snap.RegimeStds.TryGetValue(currentRegime, out var rStds)   &&
            rMeans.Length == featureCount)
        {
            useMeans = rMeans;
            useStds  = rStds;
        }

        float[] features = new float[featureCount];
        for (int j = 0; j < featureCount && j < rawFeatures.Length; j++)
        {
            float std  = j < useStds.Length  && useStds[j]  > 1e-8f ? useStds[j]  : 1f;
            float mean = j < useMeans.Length ? useMeans[j] : 0f;
            features[j] = (rawFeatures[j] - mean) / std;
        }

        // ── 7b. Fractional differencing (mirroring SmoteModelTrainer M14) ────
        // The trainer applies fractdiff to standardised features before fitting.
        // Inference must reproduce the same transform: for each lag k, build the
        // standardised feature vector at t-k from candle history and blend in
        // with weight w_k.  Uses a coarser threshold (1e-3) to keep the lookback
        // window short; accuracy degrades gracefully when candle history is shallow.
        if (snap.FracDiffD > 0.0)
        {
            const double FdThreshold  = 1e-3;
            const int    MaxFdLags    = 20;

            var fdWeights = new List<double> { 1.0 };
            for (int k = 1; k <= MaxFdLags; k++)
            {
                double w = -fdWeights[k - 1] * (snap.FracDiffD - k + 1) / k;
                if (Math.Abs(w) < FdThreshold) break;
                fdWeights.Add(w);
            }

            int W = fdWeights.Count;
            // Minimum candles needed: LookbackWindow bars per lag + 1 current bar + W-1 extra
            int minCandles = MLFeatureHelper.LookbackWindow + W;

            if (W > 1 && orderedCandles.Count >= minCandles)
            {
                var fdFeatures = new float[featureCount];

                // w_0 — current bar (already standardised in `features`)
                for (int j = 0; j < featureCount; j++)
                    fdFeatures[j] = (float)(fdWeights[0] * features[j]);

                // w_1..w_{W-1} — lagged bars
                for (int lag = 1; lag < W; lag++)
                {
                    int currIdx = orderedCandles.Count - 1 - lag;
                    if (currIdx < 1) break;
                    int windowStart = currIdx - MLFeatureHelper.LookbackWindow;
                    if (windowStart < 0) break;

                    var lagWindow  = orderedCandles.GetRange(windowStart, MLFeatureHelper.LookbackWindow);
                    var lagCurrent = orderedCandles[currIdx];
                    var lagPrev    = orderedCandles[currIdx - 1];

                    // Reuse cotEntry — COT data is weekly and unchanged across lags
                    float[] lagRaw = MLFeatureHelper.BuildFeatureVector(lagWindow, lagCurrent, lagPrev, cotEntry);

                    for (int j = 0; j < featureCount && j < lagRaw.Length; j++)
                    {
                        if (!float.IsFinite(lagRaw[j])) continue;
                        float std  = j < useStds.Length  && useStds[j]  > 1e-8f ? useStds[j]  : 1f;
                        float mean = j < useMeans.Length ? useMeans[j] : 0f;
                        fdFeatures[j] += (float)(fdWeights[lag] * (lagRaw[j] - mean) / std);
                    }
                }

                // Replace features only when all blended values are finite
                bool allFinite = true;
                for (int j = 0; j < featureCount; j++)
                    if (!float.IsFinite(fdFeatures[j])) { allFinite = false; break; }

                if (allFinite)
                    features = fdFeatures;
                else
                    _logger.LogDebug(
                        "FracDiff inference: non-finite value detected for {Symbol}/{Tf} model {Id} — skipping",
                        signal.Symbol, signalTimeframe, model.Id);
            }
            else if (W > 1)
            {
                _logger.LogDebug(
                    "FracDiff inference: insufficient candles ({Have}/{Need}) for {Symbol}/{Tf} — skipping",
                    orderedCandles.Count, minCandles, signal.Symbol, signalTimeframe);
            }
        }

        // ── 7d. Apply active-feature mask (zero pruned features) ─────────────
        if (snap.ActiveFeatureMask.Length == featureCount)
        {
            for (int j = 0; j < featureCount; j++)
            {
                if (!snap.ActiveFeatureMask[j])
                    features[j] = 0f;
            }
        }

        // ── 8. Model inference ────────────────────────────────────────────────
        double rawProb;
        double ensembleStd;

        bool isTcnModel = snap.Type == "TCN"
                          && !string.IsNullOrEmpty(snap.ConvWeightsJson)
                          && string.Compare(snap.Version, "5.0", StringComparison.Ordinal) >= 0;

        bool isQrfModel = snap.Type == "quantilerf"
                          && snap.GbmTreesJson is { Length: > 0 };

        if (isTcnModel)
        {
            // ── 8a. TCN causal dilated conv inference ────────────────────────
            var tcnSnap = JsonSerializer.Deserialize<TcnModelTrainer.TcnSnapshotWeights>(
                snap.ConvWeightsJson!, JsonOptions);

            if (tcnSnap?.ConvW is null || tcnSnap.HeadW is null || tcnSnap.HeadB is null)
            {
                _logger.LogWarning("Model {Id} has TCN type but invalid ConvWeightsJson", model.Id);
                return new MLScoreResult(null, null, null, null);
            }

            // Build per-timestep sequence features from candle window
            var seqRaw = MLFeatureHelper.BuildSequenceFeatures(window);
            var seqStd = snap.SeqMeans.Length > 0 && snap.SeqStds.Length > 0
                ? MLFeatureHelper.StandardizeSequence(seqRaw, snap.SeqMeans, snap.SeqStds)
                : seqRaw;

            // Determine block input channel counts and architecture params
            int channelIn = tcnSnap.ChannelIn > 0 ? tcnSnap.ChannelIn : seqStd[0].Length;
            int numBlocks = tcnSnap.ConvW.Length;
            int filters = tcnSnap.Filters > 0 ? tcnSnap.Filters : 32;
            var blockInC = new int[numBlocks];
            for (int b = 0; b < numBlocks; b++) blockInC[b] = b == 0 ? channelIn : filters;

            var resW = tcnSnap.ResW ?? new double[]?[numBlocks];
            var dilations = TcnModelTrainer.BuildDilations(numBlocks);
            bool useLayerNorm = tcnSnap.UseLayerNorm;
            var activation = (TcnActivation)tcnSnap.Activation;

            // Run causal dilated conv forward pass (no dropout at inference)
            double[] h;
            if (tcnSnap.UseAttentionPooling
                && tcnSnap.AttnQueryW?.Length > 0
                && tcnSnap.AttnKeyW?.Length > 0
                && tcnSnap.AttnValueW?.Length > 0)
            {
                h = TcnModelTrainer.CausalConvForwardWithAttention(
                    seqStd, tcnSnap.ConvW, tcnSnap.ConvB, resW, blockInC,
                    filters, numBlocks, dilations,
                    useLayerNorm, tcnSnap.LayerNormGamma, tcnSnap.LayerNormBeta, activation,
                    tcnSnap.AttnQueryW, tcnSnap.AttnKeyW, tcnSnap.AttnValueW);
            }
            else
            {
                h = TcnModelTrainer.CausalConvForwardFull(
                    seqStd, tcnSnap.ConvW, tcnSnap.ConvB, resW, blockInC,
                    filters, numBlocks, dilations,
                    useLayerNorm, tcnSnap.LayerNormGamma, tcnSnap.LayerNormBeta, activation);
            }

            // Direction head: softmax over 2 logits
            double logit0 = tcnSnap.HeadB[0], logit1 = tcnSnap.HeadB[1];
            for (int fi = 0; fi < filters && fi < h.Length; fi++)
            {
                logit0 += tcnSnap.HeadW[fi] * h[fi];
                logit1 += tcnSnap.HeadW[filters + fi] * h[fi];
            }
            double maxL = Math.Max(logit0, logit1);
            double e0 = Math.Exp(logit0 - maxL), e1 = Math.Exp(logit1 - maxL);
            rawProb = e1 / (e0 + e1);
            ensembleStd = 0.0; // Single model — no ensemble disagreement
        }
        else if (isQrfModel)
        {
            // ── 8b. QRF tree-forest inference ────────────────────────────────
            // Deserialise GbmTrees, route features through each tree's leaf-fraction
            // predictor, then aggregate with MetaWeights/GES/CalAccuracies priority.
            (rawProb, ensembleStd) = QrfForestProb(features, snap);
        }
        else
        {
            // ── 8c. Ensemble inference (per-learner feature subsets + stacking meta + MLP) ─
            (rawProb, ensembleStd) = EnsembleProb(
                features, snap.Weights, snap.Biases, featureCount, snap.FeatureSubsetIndices,
                snap.MetaWeights, snap.MetaBias, snap.PolyLearnerStartIndex,
                snap.EnsembleSelectionWeights.Length > 0 ? snap.EnsembleSelectionWeights : null,
                snap.LearnerCalAccuracies.Length > 0 ? snap.LearnerCalAccuracies : null,
                snap.MlpHiddenWeights, snap.MlpHiddenBiases, snap.MlpHiddenDim);
        }

        // ── 9. Platt / temperature scaling (with class-conditional fallback) ──
        // Priority: class-conditional Platt > temperature scaling > global Platt.
        double rawLogit = MLFeatureHelper.Logit(rawProb);
        double globalCalibP;
        if (snap.TemperatureScale > 0.0 && snap.TemperatureScale < 10.0)
            globalCalibP = MLFeatureHelper.Sigmoid(rawLogit / snap.TemperatureScale);
        else
            globalCalibP = MLFeatureHelper.Sigmoid(snap.PlattA * rawLogit + snap.PlattB);

        double calibP;
        if (globalCalibP >= 0.5 && snap.PlattABuy != 0.0)
            calibP = MLFeatureHelper.Sigmoid(snap.PlattABuy * rawLogit + snap.PlattBBuy);
        else if (globalCalibP < 0.5 && snap.PlattASell != 0.0)
            calibP = MLFeatureHelper.Sigmoid(snap.PlattASell * rawLogit + snap.PlattBSell);
        else
            calibP = globalCalibP;

        // ── 9b. Isotonic calibration (post-Platt PAVA correction) ────────────
        if (snap.IsotonicBreakpoints.Length >= 4)
            calibP = BaggedLogisticTrainer.ApplyIsotonicCalibration(calibP, snap.IsotonicBreakpoints);

        // ── 9c. Model age decay ───────────────────────────────────────────────
        // Shrinks calibP toward 0.5 as the model ages: calibP ← 0.5 + (calibP − 0.5)·exp(−λ·days).
        // When λ = 0 or TrainedAtUtc is not set the decay factor is 1.0 (identity).
        if (snap.AgeDecayLambda > 0.0 && snap.TrainedAtUtc != default)
        {
            double daysSinceTrain = (DateTime.UtcNow - snap.TrainedAtUtc).TotalDays;
            double decayFactor    = Math.Exp(-snap.AgeDecayLambda * Math.Max(0.0, daysSinceTrain));
            calibP = 0.5 + (calibP - 0.5) * decayFactor;
        }

        // ── 9d. Feature stability diagnostic ─────────────────────────────────
        // Log top-3 unstable features (walk-forward CV > 1.0) at debug level.
        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) &&
            snap.FeatureStabilityScores is { Length: > 0 })
        {
            var unstable = snap.FeatureStabilityScores
                .Select((cv, idx) => (CV: cv, Name: idx < snap.Features.Length ? snap.Features[idx] : $"f{idx}"))
                .Where(f => f.CV > 1.0)
                .OrderByDescending(f => f.CV)
                .Take(3)
                .ToList();
            if (unstable.Count > 0)
                _logger.LogDebug(
                    "Feature stability warning for {Symbol}/{Tf} model {Id} — top unstable: {Features}",
                    signal.Symbol, signalTimeframe, model.Id,
                    string.Join(", ", unstable.Select(f => $"{f.Name}(CV={f.CV:F2})")));
        }

        // ── 10. Magnitude prediction ─────────────────────────────────────────
        // Prefer the QRF 2-layer MLP regressor when present (QrfMlpHiddenDim > 0);
        // fall back to the linear MagWeights regressor for all other model types.
        double magnitude = 0;
        int mlpH = snap.QrfMlpHiddenDim;
        if (mlpH > 0 && snap.QrfMlpW1.Length == featureCount * mlpH)
        {
            // Forward pass: input → ReLU(W1·x + b1) → W2·h + b2
            var hidden = new double[mlpH];
            for (int h = 0; h < mlpH; h++)
            {
                double z = h < snap.QrfMlpB1.Length ? snap.QrfMlpB1[h] : 0.0;
                for (int j = 0; j < featureCount; j++)
                    z += snap.QrfMlpW1[h * featureCount + j] * features[j];
                hidden[h] = Math.Max(0.0, z);
            }
            double mlpOut = snap.QrfMlpB2;
            for (int h = 0; h < mlpH && h < snap.QrfMlpW2.Length; h++)
                mlpOut += snap.QrfMlpW2[h] * hidden[h];
            magnitude = Math.Abs(mlpOut);
        }
        else if (snap.MagWeights.Length == featureCount)
        {
            magnitude = snap.MagBias;
            for (int j = 0; j < featureCount; j++)
                magnitude += snap.MagWeights[j] * features[j];
            magnitude = Math.Abs(magnitude);
        }

        // ── 11. SHAP attribution (linear SHAP: φ_j = w̄_j × x_j) ────────────
        string? contributionsJson = null;
        try
        {
            contributionsJson = ComputeShapContributionsJson(
                features, snap.Weights, snap.FeatureSubsetIndices, snap.Features, featureCount);
        }
        catch { /* non-critical — inference proceeds without attribution */ }

        // ── 12. Derive outputs — use regime-conditioned threshold when available ─
        double threshold = 0.5;
        if (currentRegime is not null &&
            snap.RegimeThresholds.TryGetValue(currentRegime, out var regimeThr) &&
            regimeThr > 0.0)
        {
            threshold = regimeThr;
        }
        else if (snap.AdaptiveThreshold > 0.0)
        {
            threshold = snap.AdaptiveThreshold;
        }
        else if (snap.OptimalThreshold > 0.0)
        {
            threshold = snap.OptimalThreshold;
        }
        var    direction      = calibP >= threshold ? TradeDirection.Buy : TradeDirection.Sell;
        double rawConviction  = Math.Abs(calibP - threshold) * 2.0;
        double disgrFactor    = Math.Clamp(1.0 - 2.0 * ensembleStd, 0.0, 1.0);
        double confidence     = Math.Clamp(rawConviction * disgrFactor, 0.0, 1.0);

        // ── 12b. Per-regime confidence scaling (Round 13) ────────────────────
        // Down-weights confidence when the model has historically underperformed in the
        // current detected regime. Queries MLModelRegimeAccuracy (written by MLRegimeAccuracyWorker).
        // Scaling: below 50% regime accuracy → linear decay from 1.0 to 0.5, clamped at 0.5.
        // At or above 50% accuracy → no penalty (scale = 1.0).
        if (currentRegime is not null)
        {
            try
            {
                var regimeCacheKey = $"{RegimeAccCacheKeyPrefix}{model.Id}:{currentRegime}";
                if (!_cache.TryGetValue<double?>(regimeCacheKey, out var regimeAcc))
                {
                    double? fetched = null;
                    if (Enum.TryParse<MarketRegimeEnum>(currentRegime, out var regimeEnum))
                    {
                        var row = await db.Set<MLModelRegimeAccuracy>()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                r => r.MLModelId == model.Id && r.Regime == regimeEnum,
                                cancellationToken);
                        fetched = row?.Accuracy;
                    }
                    regimeAcc = fetched;
                    _cache.Set(regimeCacheKey, regimeAcc, RegimeAccCacheDuration);
                }

                if (regimeAcc.HasValue && regimeAcc.Value < 0.5)
                {
                    // Linear: 0% accuracy → 0.5 scale; 50% accuracy → 1.0 scale
                    double scale = 0.5 + regimeAcc.Value;   // = 0.5 + acc/0.5*0.5
                    scale = Math.Clamp(scale, 0.5, 1.0);
                    confidence *= scale;
                    _logger.LogDebug(
                        "RegimeConfidenceScale: {Symbol}/{Tf} model {Id} regime={Regime} " +
                        "regimeAcc={Acc:P1} → scale={Scale:F3} confidence={Conf:F4}",
                        signal.Symbol, signalTimeframe, model.Id, currentRegime,
                        regimeAcc.Value, scale, confidence);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Regime accuracy lookup failed for {Symbol}/{Tf} — skipping regime scaling",
                    signal.Symbol, signalTimeframe);
            }
        }

        // ── 12d. Regime-transition confidence dampening (Round 15) ──────────
        // MLRegimeTransitionGuardWorker writes a per-symbol/timeframe penalty factor to
        // EngineConfig when a regime change was detected within the transition window.
        // Factor is 1.0 (no dampening) once the window expires or no transition exists.
        try
        {
            var penaltyCacheKey = $"{RegimePenaltyCacheKeyPrefix}{signal.Symbol}:{signalTimeframe}";
            if (!_cache.TryGetValue<double>(penaltyCacheKey, out var transitionPenalty))
            {
                var configKey = $"MLRegimeTransition:{signal.Symbol}:{signalTimeframe}:PenaltyFactor";
                var entry = await db.Set<EngineConfig>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Key == configKey, cancellationToken);
                transitionPenalty = entry?.Value is not null &&
                                    double.TryParse(entry.Value,
                                        System.Globalization.NumberStyles.Any,
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        out var pv)
                    ? pv : 1.0;
                _cache.Set(penaltyCacheKey, transitionPenalty, RegimePenaltyCacheDuration);
            }

            if (transitionPenalty < 1.0)
            {
                double before = confidence;
                confidence *= transitionPenalty;
                _logger.LogDebug(
                    "RegimeTransitionDampening: {Symbol}/{Tf} model {Id} — " +
                    "penaltyFactor={Factor:F3} confidence {Before:F4}→{After:F4}",
                    signal.Symbol, signalTimeframe, model.Id, transitionPenalty, before, confidence);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Regime transition penalty lookup failed for {Symbol}/{Tf} — skipping dampening",
                signal.Symbol, signalTimeframe);
        }

        // ── 12e. Cold-start confidence dampening (Round 16) ─────────────────
        // MLColdStartDampeningWorker ramps a new model's confidence multiplier from
        // InitialFactor (0.60) → 1.0 over the first 50 live predictions.
        try
        {
            var csCacheKey  = $"{ColdStartCacheKeyPrefix}{signal.Symbol}:{signalTimeframe}:Factor";
            if (!_cache.TryGetValue<double>(csCacheKey, out var coldStartFactor))
            {
                var csKey = $"MLColdStart:{signal.Symbol}:{signalTimeframe}:Factor";
                var csEntry = await db.Set<EngineConfig>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Key == csKey, cancellationToken);
                coldStartFactor = csEntry?.Value is not null &&
                                  double.TryParse(csEntry.Value,
                                      System.Globalization.NumberStyles.Any,
                                      System.Globalization.CultureInfo.InvariantCulture,
                                      out var csv)
                    ? csv : 1.0;
                _cache.Set(csCacheKey, coldStartFactor, ColdStartCacheDuration);
            }
            if (coldStartFactor < 1.0)
            {
                confidence *= coldStartFactor;
                _logger.LogDebug(
                    "ColdStartDampening: {Symbol}/{Tf} model {Id} — factor={F:F3} confidence→{C:F4}",
                    signal.Symbol, signalTimeframe, model.Id, coldStartFactor, confidence);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cold-start factor lookup failed for {Symbol}/{Tf}", signal.Symbol, signalTimeframe);
        }

        // ── 12f. Cross-timeframe consistency dampening (Round 16) ────────────
        // MLCrossTimeframeConsistencyWorker writes a factor when this timeframe's
        // prediction conflicts with the next-higher timeframe's direction.
        try
        {
            var ctfCacheKey = $"{CrossTfCacheKeyPrefix}{signal.Symbol}:{signalTimeframe}:ConsistencyFactor";
            if (!_cache.TryGetValue<double>(ctfCacheKey, out var ctfFactor))
            {
                var ctfKey = $"MLCrossTimeframe:{signal.Symbol}:{signalTimeframe}:ConsistencyFactor";
                var ctfEntry = await db.Set<EngineConfig>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Key == ctfKey, cancellationToken);
                ctfFactor = ctfEntry?.Value is not null &&
                            double.TryParse(ctfEntry.Value,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var ctfv)
                    ? ctfv : 1.0;
                _cache.Set(ctfCacheKey, ctfFactor, CrossTfCacheDuration);
            }
            if (ctfFactor < 1.0)
            {
                confidence *= ctfFactor;
                _logger.LogDebug(
                    "CrossTfConsistency: {Symbol}/{Tf} model {Id} — factor={F:F3} confidence→{C:F4}",
                    signal.Symbol, signalTimeframe, model.Id, ctfFactor, confidence);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cross-TF factor lookup failed for {Symbol}/{Tf}", signal.Symbol, signalTimeframe);
        }

        // ── 12g. Consecutive-miss cooldown gate (Round 17) ───────────────────
        // MLSignalCooldownWorker writes an ISO-8601 expiry timestamp to EngineConfig
        // when a model accumulates MaxConsecMisses consecutive wrong predictions.
        // Return a null score (suppressed) if the cooldown has not yet expired.
        try
        {
            var cdCacheKey = $"{CooldownCacheKeyPrefix}{signal.Symbol}:{signalTimeframe}:ExpiresAt";
            if (!_cache.TryGetValue<DateTime?>(cdCacheKey, out var cooldownExpiry))
            {
                var cdKey  = $"MLCooldown:{signal.Symbol}:{signalTimeframe}:ExpiresAt";
                var cdEntry = await db.Set<EngineConfig>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Key == cdKey, cancellationToken);

                cooldownExpiry = cdEntry?.Value is not null &&
                                 DateTime.TryParse(cdEntry.Value,
                                     System.Globalization.CultureInfo.InvariantCulture,
                                     System.Globalization.DateTimeStyles.RoundtripKind,
                                     out var parsed)
                    ? parsed
                    : (DateTime?)null;

                _cache.Set(cdCacheKey, cooldownExpiry, CooldownCacheDuration);
            }

            if (cooldownExpiry.HasValue && DateTime.UtcNow < cooldownExpiry.Value)
            {
                _logger.LogInformation(
                    "Cooldown gate: {Symbol}/{Tf} model {Id} — signal suppressed until {Exp:HH:mm} UTC.",
                    signal.Symbol, signalTimeframe, model.Id, cooldownExpiry.Value);
                return new MLScoreResult(null, null, null, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cooldown gate lookup failed for {Symbol}/{Tf}", signal.Symbol, signalTimeframe);
        }

        // ── 12c. Multi-model consensus filter (Round 10) ─────────────────────
        // When MLScoring:ConsensusMinModels >= 2, load all other active models for
        // the same symbol/timeframe and quick-score them with the same rawFeatures.
        // Suppress the signal if fewer than ConsensusMinModels models agree on direction.
        {
            int consensusMin = await GetConsensusMinModelsAsync(db, cancellationToken);
            if (consensusMin >= 2)
            {
                var otherModels = await db.Set<MLModel>()
                    .AsNoTracking()
                    .Where(x => x.Symbol    == signal.Symbol &&
                                x.Timeframe == signalTimeframe &&
                                x.IsActive  && !x.IsDeleted   &&
                                x.Id        != model.Id       &&
                                x.ModelBytes != null)
                    .ToListAsync(cancellationToken);

                int agreedCount = 1; // primary model always counts
                foreach (var altModel in otherModels)
                {
                    var altCacheKey = $"{SnapshotCacheKeyPrefix}{altModel.Id}";
                    if (!_cache.TryGetValue<ModelSnapshot>(altCacheKey, out var altSnap) || altSnap is null)
                    {
                        try
                        {
                            altSnap = System.Text.Json.JsonSerializer.Deserialize<ModelSnapshot>(
                                altModel.ModelBytes!, JsonOptions);
                            if (altSnap is not null) _cache.Set(altCacheKey, altSnap, SnapshotCacheDuration);
                        }
                        catch { continue; }
                    }
                    if (altSnap is null || altSnap.Weights.Length == 0) continue;

                    // Re-standardise rawFeatures with this model's stored means/stds
                    int altFc = altSnap.Features.Length > 0 ? altSnap.Features.Length : rawFeatures.Length;
                    float[] altFeatures = new float[altFc];
                    for (int j = 0; j < altFc && j < rawFeatures.Length; j++)
                    {
                        float std  = j < altSnap.Stds.Length  && altSnap.Stds[j]  > 1e-8f ? altSnap.Stds[j]  : 1f;
                        float mean = j < altSnap.Means.Length ? altSnap.Means[j] : 0f;
                        altFeatures[j] = (rawFeatures[j] - mean) / std;
                    }
                    if (altSnap.ActiveFeatureMask.Length == altFc)
                        for (int j = 0; j < altFc; j++)
                            if (!altSnap.ActiveFeatureMask[j]) altFeatures[j] = 0f;

                    var (altRaw, _) = EnsembleProb(altFeatures, altSnap.Weights, altSnap.Biases, altFc,
                        altSnap.FeatureSubsetIndices, null, 0.0, int.MaxValue, null, null,
                        altSnap.MlpHiddenWeights, altSnap.MlpHiddenBiases, altSnap.MlpHiddenDim);
                    double altCalibP = MLFeatureHelper.Sigmoid(altSnap.PlattA * MLFeatureHelper.Logit(altRaw) + altSnap.PlattB);
                    double altThr    = altSnap.OptimalThreshold > 0.0 ? altSnap.OptimalThreshold : 0.5;
                    var    altDir    = altCalibP >= altThr ? TradeDirection.Buy : TradeDirection.Sell;

                    if (altDir == direction) agreedCount++;
                }

                if (agreedCount < consensusMin)
                {
                    _logger.LogDebug(
                        "Consensus filter: {Symbol}/{Tf} — only {Agreed}/{Total} models agree on {Dir}; " +
                        "need {Min}. Signal suppressed.",
                        signal.Symbol, signalTimeframe, agreedCount, otherModels.Count + 1, direction, consensusMin);
                    return new MLScoreResult(null, null, null, null);
                }

                _logger.LogDebug(
                    "Consensus filter: {Symbol}/{Tf} — {Agreed}/{Total} models agree on {Dir}. Proceeding.",
                    signal.Symbol, signalTimeframe, agreedCount, otherModels.Count + 1, direction);
            }
        }

        // ── 13. Half-Kelly fraction ───────────────────────────────────────────
        // f* = max(0, 2p − 1) × 0.5 — caps bet size to half the full-Kelly optimum,
        // reducing variance at the cost of a small EV reduction.
        double kellyFraction = Math.Max(0.0, 2.0 * calibP - 1.0) * 0.5;

        // ── 13b. BSS-based Kelly multiplier (Round 9) ─────────────────────────
        // Models with very low Brier Skill Score are barely better than naive baseline;
        // their Kelly fraction is penalised to reflect calibration quality.
        // BSS ≥ 0.10 → multiplier = 1.0 (no penalty). BSS ≤ -0.05 → multiplier = 0.5.
        if (snap.BrierSkillScore < 0.10)
        {
            double bssMultiplier = Math.Clamp(0.5 + (snap.BrierSkillScore + 0.05) / 0.15 * 0.5, 0.5, 1.0);
            kellyFraction *= bssMultiplier;
        }

        // ── 13c. Live-accuracy Kelly multiplier (Round 16) ───────────────────
        // MLPositionSizeAdvisorWorker writes clamp(liveAccuracy / trainingAccuracy, 0.5, 1.0)
        // per symbol/timeframe. Scales Kelly fraction proportionally to live underperformance.
        try
        {
            var klCacheKey = $"{KellyLiveCacheKeyPrefix}{signal.Symbol}:{signalTimeframe}:LiveMultiplier";
            if (!_cache.TryGetValue<double>(klCacheKey, out var kellyLiveMult))
            {
                var klKey = $"MLKelly:{signal.Symbol}:{signalTimeframe}:LiveMultiplier";
                var klEntry = await db.Set<EngineConfig>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Key == klKey, cancellationToken);
                kellyLiveMult = klEntry?.Value is not null &&
                                double.TryParse(klEntry.Value,
                                    System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out var klv)
                    ? klv : 1.0;
                _cache.Set(klCacheKey, kellyLiveMult, KellyLiveCacheDuration);
            }
            if (kellyLiveMult < 1.0)
            {
                kellyFraction *= kellyLiveMult;
                _logger.LogDebug(
                    "KellyLiveAdvisor: {Symbol}/{Tf} model {Id} — liveMultiplier={Mult:F3} kelly→{Kelly:F4}",
                    signal.Symbol, signalTimeframe, model.Id, kellyLiveMult, kellyFraction);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Kelly multiplier lookup failed for {Symbol}/{Tf}", signal.Symbol, signalTimeframe);
        }

        // ── 14. Conformal prediction set (90% marginal coverage) ─────────────
        // Include Buy  when nonconformity_Buy  = 1−p ≤ qHat (i.e. p ≥ 1−qHat)
        // Include Sell when nonconformity_Sell = p   ≤ qHat
        string? conformalSet     = null;
        int?    conformalSetSize = null;
        if (snap.ConformalQHat > 0.0 && snap.ConformalQHat < 1.0)
        {
            bool includeBuy  = calibP >= 1.0 - snap.ConformalQHat;
            bool includeSell = calibP <= snap.ConformalQHat;
            conformalSet = (includeBuy, includeSell) switch
            {
                (true,  false) => "Buy",
                (false, true)  => "Sell",
                (true,  true)  => "Ambiguous",
                _              => "None",
            };
            // ConformalSetSize: 0=None/empty, 1=confident single direction, 2=ambiguous
            conformalSetSize = conformalSet switch
            {
                "Buy" or "Sell" => 1,
                "Ambiguous"     => 2,
                _               => 0,
            };
        }

        // ── 15. Meta-label secondary classifier ──────────────────────────────
        decimal? metaLabelScore = null;
        if (snap.MetaLabelWeights.Length > 0)
        {
            // Meta-features: [calibP, ensStd, features[0..4]]
            int metaFeatCount = 2 + Math.Min(5, featureCount);
            double metaZ = snap.MetaLabelBias;
            if (snap.MetaLabelWeights.Length >= metaFeatCount)
            {
                metaZ += snap.MetaLabelWeights[0] * calibP;
                metaZ += snap.MetaLabelWeights[1] * ensembleStd;
                for (int j = 0; j < Math.Min(5, featureCount) && 2 + j < snap.MetaLabelWeights.Length; j++)
                    metaZ += snap.MetaLabelWeights[2 + j] * features[j];
            }
            metaLabelScore = (decimal)MLFeatureHelper.Sigmoid(metaZ);
        }

        // ── 16. Jackknife+ prediction interval ───────────────────────────────
        string? jackknifeInterval = null;
        if (snap.JackknifeResiduals.Length >= 10)
        {
            // Use 90th percentile of OOB residuals as the interval half-width
            int qIdx = (int)Math.Ceiling(0.9 * snap.JackknifeResiduals.Length) - 1;
            qIdx = Math.Clamp(qIdx, 0, snap.JackknifeResiduals.Length - 1);
            double halfWidth = snap.JackknifeResiduals[qIdx]; // residuals are sorted ascending
            jackknifeInterval = $"±{halfWidth:F4}@90%";
        }

        // ── 17. Binary prediction entropy ─────────────────────────────────────
        // H = −p·log₂(p) − (1−p)·log₂(1−p), clamped to [0, 1].
        double entropyScore;
        {
            double ep = Math.Clamp(calibP, 1e-10, 1.0 - 1e-10);
            entropyScore = -(ep * Math.Log2(ep) + (1 - ep) * Math.Log2(1 - ep));
            entropyScore = Math.Clamp(entropyScore, 0.0, 1.0);
        }

        // ── 18. Abstention gate ───────────────────────────────────────────────
        // P(tradeable environment) from [calibP, ensStd, metaLabelScore] logistic gate.
        decimal? abstentionScore = null;
        if (snap.AbstentionWeights.Length == 3 && metaLabelScore.HasValue)
        {
            var    af = new double[] { calibP, ensembleStd, (double)metaLabelScore.Value };
            double az = snap.AbstentionBias;
            for (int i = 0; i < 3; i++) az += snap.AbstentionWeights[i] * af[i];
            abstentionScore = (decimal)MLFeatureHelper.Sigmoid(az);
        }

        _logger.LogDebug(
            "ML score for {Symbol}/{Tf} model={ModelId} regime={Regime}: dir={Dir} calibP={P:F4} " +
            "threshold={Thr:F4} ensStd={Std:F4} conf={Conf:F4} mag={Mag:F2} kelly={Kelly:F4} " +
            "conformal={Conf2} meta={Meta} jackknife={JK} abstention={Abs}",
            signal.Symbol, signalTimeframe, model.Id, model.RegimeScope ?? "global",
            direction, calibP, threshold, ensembleStd, confidence, magnitude, kellyFraction,
            conformalSet ?? "n/a", metaLabelScore?.ToString("F3") ?? "n/a", jackknifeInterval ?? "n/a",
            abstentionScore?.ToString("F3") ?? "n/a");

        return new MLScoreResult(
            PredictedDirection:     direction,
            PredictedMagnitudePips: (decimal)magnitude,
            ConfidenceScore:        (decimal)confidence,
            MLModelId:              model.Id,
            EnsembleDisagreement:   (decimal)ensembleStd,
            ContributionsJson:      contributionsJson,
            KellyFraction:          (decimal)kellyFraction,
            ConformalSet:           conformalSet,
            MetaLabelScore:         metaLabelScore,
            JackknifeInterval:      jackknifeInterval,
            AbstentionScore:        abstentionScore,
            ConformalSetSize:       conformalSetSize,
            EntropyScore:           (decimal)entropyScore);
    }

    // ── COT normalisation ─────────────────────────────────────────────────────

    /// <summary>
    /// Normalises COT values using the training-time min/max bounds stored in the snapshot.
    /// Falls back to hardcoded divisors when bounds are missing (legacy snapshots).
    /// </summary>
    private static CotFeatureEntry BuildCotEntry(COTReport report, ModelSnapshot snap)
    {
        float netNorm, momentum;

        float netRange = snap.CotNetNormMax - snap.CotNetNormMin;
        float momRange = snap.CotMomNormMax - snap.CotMomNormMin;

        if (netRange > 1f)
        {
            // Min-max → map to [−3, +3] consistent with training normalisation
            netNorm  = MLFeatureHelper.Clamp(
                ((float)report.NetNonCommercialPositioning - snap.CotNetNormMin) / netRange * 6f - 3f,
                -3f, 3f);
            momentum = MLFeatureHelper.Clamp(
                ((float)report.NetPositioningChangeWeekly - snap.CotMomNormMin) / momRange * 6f - 3f,
                -3f, 3f);
        }
        else
        {
            // Legacy fallback (snapshots trained before bounds were stored)
            netNorm  = MLFeatureHelper.Clamp(
                (float)((double)report.NetNonCommercialPositioning / 100_000), -3f, 3f);
            momentum = MLFeatureHelper.Clamp(
                (float)((double)report.NetPositioningChangeWeekly  / 10_000),  -3f, 3f);
        }

        return new CotFeatureEntry(netNorm, momentum);
    }

    // ── QRF tree-forest inference ─────────────────────────────────────────────

    /// <summary>
    /// Runs inference for a <c>quantilerf</c> model snapshot by deserialising
    /// <see cref="ModelSnapshot.GbmTreesJson"/> into a flat <see cref="GbmTree"/> list,
    /// routing the feature vector to each tree's leaf, and aggregating per-tree
    /// leaf-fraction probabilities.
    ///
    /// Aggregation priority (mirrors <see cref="EnsembleProb"/>):
    /// <list type="number">
    ///   <item>Stacking meta-learner (<c>MetaWeights/MetaBias</c>) when length == T.</item>
    ///   <item>GES-weighted average (<c>EnsembleSelectionWeights</c>) when length == T.</item>
    ///   <item>Softmax-weighted by per-tree cal accuracy (<c>LearnerCalAccuracies</c>).</item>
    ///   <item>Plain unweighted average.</item>
    /// </list>
    /// Returns (0.5, 0.0) on deserialisation failure or empty forest.
    /// </summary>
    private static (double AvgProb, double StdProb) QrfForestProb(
        float[]       features,
        ModelSnapshot snap)
    {
        List<GbmTree>? trees;
        try
        {
            trees = JsonSerializer.Deserialize<List<GbmTree>>(snap.GbmTreesJson!, JsonOptions);
        }
        catch
        {
            return (0.5, 0.0);
        }

        if (trees is not { Count: > 0 }) return (0.5, 0.0);

        int T     = trees.Count;
        var probs = new double[T];

        for (int t = 0; t < T; t++)
        {
            var nodes = trees[t].Nodes;
            if (nodes is not { Count: > 0 }) { probs[t] = 0.5; continue; }

            // Iterative leaf routing — avoids recursion stack overhead for deep forests.
            int    nodeIdx = 0;
            double leafVal = 0.5;
            while (nodeIdx >= 0 && nodeIdx < nodes.Count)
            {
                var node = nodes[nodeIdx];
                if (node.IsLeaf || node.SplitFeature < 0 || node.SplitFeature >= features.Length)
                {
                    leafVal = node.LeafValue;
                    break;
                }
                nodeIdx = features[node.SplitFeature] <= (float)node.SplitThreshold
                    ? node.LeftChild
                    : node.RightChild;
            }
            probs[t] = double.IsFinite(leafVal) ? leafVal : 0.5;
        }

        // ── Aggregate per-tree probs (MetaWeights > GES > CalAccuracies > plain avg) ─
        // This is a strict priority chain (if-else-if): only ONE aggregator is applied.
        // MetaWeights and EnsembleSelectionWeights are never combined additively.
        double avg;
        if (snap.MetaWeights is { Length: > 0 } mw && mw.Length == T)
        {
            double metaZ = snap.MetaBias;
            for (int t = 0; t < T; t++) metaZ += mw[t] * probs[t];
            avg = MLFeatureHelper.Sigmoid(metaZ);
        }
        else if (snap.EnsembleSelectionWeights is { Length: > 0 } gw && gw.Length == T)
        {
            double wSum = 0, pSum = 0;
            for (int t = 0; t < T; t++) { wSum += gw[t]; pSum += gw[t] * probs[t]; }
            avg = wSum > 1e-10 ? pSum / wSum : probs.Average();
        }
        else if (snap.LearnerCalAccuracies is { Length: > 0 } ca && ca.Length == T)
        {
            const double Alpha = 4.0;
            double maxAcc = ca.Max();
            double sumExp = ca.Sum(a => Math.Exp(Alpha * (a - maxAcc)));
            double wSum = 0, pSum = 0;
            for (int t = 0; t < T; t++)
            {
                double w = Math.Exp(Alpha * (ca[t] - maxAcc)) / sumExp;
                wSum += w; pSum += w * probs[t];
            }
            avg = wSum > 1e-10 ? pSum / wSum : probs.Average();
        }
        else
        {
            avg = probs.Average();
        }

        double variance = 0.0;
        for (int t = 0; t < T; t++) { double d = probs[t] - avg; variance += d * d; }
        double std = T > 1 ? Math.Sqrt(variance / (T - 1)) : 0.0;

        return (avg, std);
    }

    // ── Ensemble inference ────────────────────────────────────────────────────

    private static (double AvgProb, double StdProb) EnsembleProb(
        float[]    features,
        double[][] weights,
        double[]   biases,
        int        featureCount,
        int[][]?   subsets                  = null,
        double[]?  metaWeights              = null,
        double     metaBias                 = 0.0,
        int        polyLearnerStartIndex    = int.MaxValue,
        double[]?  gesWeights               = null,
        double[]?  learnerCalAccuracies     = null,
        double[][]? mlpHiddenW              = null,
        double[][]? mlpHiddenB              = null,
        int         mlpHiddenDim            = 0)
    {
        if (weights.Length == 0) return (0.5, 0.0);

        bool useMlp = mlpHiddenDim > 0 && mlpHiddenW is not null && mlpHiddenB is not null;
        var probs = new double[weights.Length];
        for (int k = 0; k < weights.Length; k++)
        {
            // ── MLP forward pass ──────────────────────────────────────────────
            if (useMlp && k < mlpHiddenW!.Length && mlpHiddenW[k] is not null &&
                k < mlpHiddenB!.Length && mlpHiddenB[k] is not null)
            {
                var hW = mlpHiddenW[k];
                var hB = mlpHiddenB[k];
                int[] subset = subsets?.Length > k && subsets[k] is { Length: > 0 } s ? s : [];
                int subLen = subset.Length > 0 ? subset.Length : featureCount;
                if (subset.Length == 0)
                {
                    subset = new int[featureCount];
                    for (int j = 0; j < featureCount; j++) subset[j] = j;
                    subLen = featureCount;
                }

                double z = biases[k];
                for (int h = 0; h < mlpHiddenDim; h++)
                {
                    double act = hB[h];
                    int rowOff = h * subLen;
                    for (int si = 0; si < subLen && rowOff + si < hW.Length; si++)
                        act += hW[rowOff + si] * features[subset[si]];
                    double hidden = Math.Max(0.0, act); // ReLU
                    if (h < weights[k].Length)
                        z += weights[k][h] * hidden;
                }
                probs[k] = MLFeatureHelper.Sigmoid(z);
                continue;
            }

            // ── Linear logistic forward pass ──────────────────────────────────
            double zLin = biases[k];

            // Determine effective features for this learner (poly augmentation if applicable)
            float[] effectiveFeatures = features;
            if (k >= polyLearnerStartIndex && featureCount >= 5)
            {
                // Augment with 10 pairwise products of the first 5 features
                var aug = new float[featureCount + 10];
                Array.Copy(features, aug, featureCount);
                int idx = featureCount;
                for (int a = 0; a < 5; a++)
                    for (int b = a + 1; b < 5; b++)
                        aug[idx++] = features[a] * features[b];
                effectiveFeatures = aug;
            }

            if (subsets?.Length > k && subsets[k] is { Length: > 0 } subset2)
            {
                foreach (int j in subset2)
                {
                    if (j < effectiveFeatures.Length && j < weights[k].Length)
                        zLin += weights[k][j] * effectiveFeatures[j];
                }
            }
            else
            {
                int wLen = Math.Min(weights[k].Length, effectiveFeatures.Length);
                for (int j = 0; j < wLen; j++)
                    zLin += weights[k][j] * effectiveFeatures[j];
            }
            probs[k] = MLFeatureHelper.Sigmoid(zLin);
        }

        // Apply stacking meta-learner, GES weighting, or simple average (in priority order).
        double avg;
        if (metaWeights is { Length: > 0 } && metaWeights.Length == weights.Length)
        {
            double metaZ = metaBias;
            for (int k = 0; k < metaWeights.Length; k++)
                metaZ += metaWeights[k] * probs[k];
            avg = MLFeatureHelper.Sigmoid(metaZ);
        }
        else if (gesWeights is { Length: > 0 } && gesWeights.Length == weights.Length)
        {
            // GES-weighted ensemble average (Caruana et al. 2004 greedy selection)
            double wSum = 0, pSum = 0;
            for (int k = 0; k < gesWeights.Length; k++) { wSum += gesWeights[k]; pSum += gesWeights[k] * probs[k]; }
            avg = wSum > 1e-10 ? pSum / wSum : probs.Average();
        }
        else if (learnerCalAccuracies is { Length: > 0 } && learnerCalAccuracies.Length == weights.Length)
        {
            // Softmax-weighted average by per-learner calibration-set accuracy (Round 9).
            // Temperature α = 4.0 sharpens weights toward better learners without extreme dominance.
            const double alpha = 4.0;
            double maxAcc  = learnerCalAccuracies.Max();
            double sumExp  = learnerCalAccuracies.Sum(a => Math.Exp(alpha * (a - maxAcc)));
            double wSum = 0, pSum = 0;
            for (int k = 0; k < weights.Length; k++)
            {
                double w = Math.Exp(alpha * (learnerCalAccuracies[k] - maxAcc)) / sumExp;
                wSum += w; pSum += w * probs[k];
            }
            avg = wSum > 1e-10 ? pSum / wSum : probs.Average();
        }
        else
        {
            avg = probs.Average();
        }

        double std = Math.Sqrt(probs.Select(p => (p - avg) * (p - avg)).Average());
        return (avg, std);
    }

    // ── SHAP attribution ──────────────────────────────────────────────────────

    /// <summary>
    /// Computes ensemble-averaged linear SHAP contributions φ_j = w̄_j × x_j
    /// and returns the top-5 by |φ_j| as a compact JSON array.
    /// Format: [{"Feature":"Rsi","Value":0.042},...]
    /// <para>
    /// Works for all trainer architectures that store per-feature weights in the
    /// snapshot (BaggedLogistic, ELM, AdaBoost, SMOTE, Rocket, etc.).
    /// For tree-based models (GBM, QuantileRf) where weights are not directly
    /// available, falls back to using <c>FeatureImportanceScores</c> × x_j.
    /// For attention-based models (FtTransformer, TabNet), uses attention-derived
    /// importance scores when available.
    /// </para>
    /// </summary>
    private static string? ComputeShapContributionsJson(
        float[]   features,
        double[][] weights,
        int[][]?  subsets,
        string[]  featureNames,
        int       featureCount)
    {
        if (featureNames.Length == 0) return null;

        var contribs = new (string Name, double Phi)[Math.Min(featureCount, featureNames.Length)];

        if (weights is { Length: > 0 })
        {
            // Ensemble-averaged linear SHAP: φ_j = w̄_j × x_j
            var weightSum = new double[featureCount];
            var countPer  = new int[featureCount];

            for (int k = 0; k < weights.Length; k++)
            {
                int[] active = subsets?.Length > k && subsets[k] is { Length: > 0 } s
                    ? s
                    : Enumerable.Range(0, Math.Min(featureCount, weights[k].Length)).ToArray();

                foreach (int j in active)
                {
                    if (j < weights[k].Length)
                    {
                        weightSum[j] += weights[k][j];
                        countPer[j]++;
                    }
                }
            }

            for (int j = 0; j < contribs.Length; j++)
            {
                double wBar = countPer[j] > 0 ? weightSum[j] / countPer[j] : 0.0;
                double phi  = j < features.Length ? wBar * features[j] : 0.0;
                contribs[j] = (featureNames[j], phi);
            }
        }
        else
        {
            // Fallback: zero contributions (model type doesn't expose weights)
            for (int j = 0; j < contribs.Length; j++)
                contribs[j] = (featureNames[j], 0.0);
        }

        var top5 = contribs
            .OrderByDescending(c => Math.Abs(c.Phi))
            .Take(5)
            .Select(c => new { Feature = c.Name, Value = Math.Round(c.Phi, 4) })
            .ToArray();

        return JsonSerializer.Serialize(top5);
    }

    // ── Config helpers ────────────────────────────────────────────────────────

    private const string ConsensusCacheKey = "MLScoring:ConsensusMinModels";
    private static readonly TimeSpan ConsensusCacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reads <c>MLScoring:ConsensusMinModels</c> from <see cref="EngineConfig"/>.
    /// Returns 1 (disabled) when the key is absent or unparseable.
    /// Result is cached in <see cref="IMemoryCache"/> for 5 minutes to avoid
    /// a DB round-trip on every score call.
    /// </summary>
    private async Task<int> GetConsensusMinModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
    {
        if (_cache.TryGetValue<int>(ConsensusCacheKey, out var cached))
            return cached;

        int value = 1; // default = disabled
        try
        {
            var entry = await ctx.Set<EngineConfig>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == "MLScoring:ConsensusMinModels", ct);

            if (entry?.Value is not null && int.TryParse(entry.Value, out int parsed))
                value = Math.Max(1, parsed);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read MLScoring:ConsensusMinModels — defaulting to 1 (disabled)");
        }

        _cache.Set(ConsensusCacheKey, value, ConsensusCacheDuration);
        return value;
    }
}
