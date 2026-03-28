using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Application.Services.ML;

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
[RegisterService]
public sealed class MLSignalScorer : IMLSignalScorer
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private const string SnapshotCacheKeyPrefix  = "MLSnapshot:";
    private static readonly TimeSpan SnapshotCacheDuration = TimeSpan.FromMinutes(30);

    private const string CotCacheKeyPrefix = "MLCot:";
    private static readonly TimeSpan CotCacheDuration = TimeSpan.FromHours(1);

    private const double BssFloor = 0.10;
    private const double HalfKellyFactor = 0.5;
    private const double DefaultOodThresholdSigma = 3.0;
    private const int DefaultMcDropoutSamples = 30;

    /// <summary>
    /// Limits concurrent CPU-bound inference calls (forward passes, MC-Dropout sampling)
    /// across all scopes. Without this, a burst of scoring requests would schedule unbounded
    /// <see cref="Task.Run"/> work items that compete for threadpool threads.
    /// Capped at <see cref="Environment.ProcessorCount"/> to match available cores.
    /// </summary>
    private static readonly SemaphoreSlim _inferenceSemaphore =
        new(Environment.ProcessorCount, Environment.ProcessorCount);

    /// <summary>Per-query timeout for non-critical DB lookups (regime, config, accuracy).</summary>
    private static readonly TimeSpan DbQueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Overall latency budget for the entire <see cref="ScoreAsync"/> call.
    /// Non-critical enrichments (consensus filter, multi-TF blend, live Kelly)
    /// are skipped when the budget is exceeded. Safety gates (cooldown, dampenings)
    /// are always evaluated.
    /// </summary>
    private static readonly TimeSpan ScoringBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Guards concurrent deserialisations of the same model snapshot.
    /// Key = model ID → Lazy that produces the snapshot exactly once.
    /// Entries are removed after the snapshot is cached in IMemoryCache.
    ///
    /// This is intentionally static even though <see cref="MLSignalScorer"/> is registered
    /// as Scoped — the guard must span all concurrent scopes to prevent duplicate
    /// deserialisations of the same model. Entries are short-lived (removed immediately
    /// after the snapshot is cached or on failure) so there is no lifecycle concern.
    ///
    /// Uses <c>Lazy&lt;Task&gt;</c> so that waiting threads <c>await</c> instead of
    /// blocking synchronously — preventing threadpool starvation during model cold-starts.
    /// </summary>
    private static readonly ConcurrentDictionary<long, Lazy<Task<ModelSnapshot?>>> _snapshotInflight = new();

    /// <summary>
    /// Tracks consecutive inference failures per model ID. When a model fails
    /// <see cref="CircuitBreakerThreshold"/> times consecutively, it is skipped for
    /// <see cref="CircuitBreakerCooldown"/> to avoid wasted work.
    /// </summary>
    private static readonly ConcurrentDictionary<long, (int Count, DateTime LastFailure)> _inferenceFailures = new();
    private const int CircuitBreakerThreshold = 3;
    private static readonly TimeSpan CircuitBreakerCooldown = TimeSpan.FromMinutes(5);

    private readonly IReadApplicationDbContext          _context;
    private readonly IMemoryCache                      _cache;
    private readonly ILogger<MLSignalScorer>           _logger;
    private readonly IEnumerable<IModelInferenceEngine> _inferenceEngines;
    private readonly MLModelResolver                   _modelResolver;
    private readonly MLConfigService                   _configService;

    public MLSignalScorer(
        IReadApplicationDbContext          context,
        IMemoryCache                      cache,
        ILogger<MLSignalScorer>           logger,
        IEnumerable<IModelInferenceEngine> inferenceEngines)
    {
        _context          = context;
        _cache            = cache;
        _logger           = logger;
        _inferenceEngines = inferenceEngines;
        _modelResolver    = new MLModelResolver(context, logger);
        _configService    = new MLConfigService(cache, logger);
    }

    private IModelInferenceEngine? ResolveEngine(ModelSnapshot snap)
        => _inferenceEngines.FirstOrDefault(e => e.CanHandle(snap));

    public async Task<MLScoreResult> ScoreAsync(
        TradeSignal           signal,
        IReadOnlyList<Candle> candles,
        CancellationToken     cancellationToken)
    {
        var scoringStart = Stopwatch.GetTimestamp();

        var signalTimeframe = candles.Count > 0 ? candles[0].Timeframe : Timeframe.H1;
        var db              = _context.GetDbContext();

        // ── 1. Find active model (regime-aware, falls back to global) ────────
        var (model, currentRegime) = await _modelResolver.ResolveActiveModelAsync(
            signal, signalTimeframe, cancellationToken);
        if (model is null)
            return new MLScoreResult(null, null, null, null);

        // ── 1b. Circuit breaker — skip models with repeated inference failures ──
        if (_inferenceFailures.TryGetValue(model.Id, out var failure) &&
            failure.Count >= CircuitBreakerThreshold &&
            DateTime.UtcNow - failure.LastFailure < CircuitBreakerCooldown)
        {
            _logger.LogDebug(
                "Circuit breaker open for model {Id} ({Count} consecutive failures) — skipping until {ResetAt:HH:mm} UTC",
                model.Id, failure.Count, failure.LastFailure + CircuitBreakerCooldown);
            return new MLScoreResult(null, null, null, null);
        }

        // ── 2. Deserialise model snapshot (cached, with concurrency protection) ─
        var snap = await GetOrDeserializeSnapshotAsync(model);

        if (snap is null || !HasModelWeights(snap))
        {
            _logger.LogWarning("Model {Id} snapshot is empty or has no weights (type={Type})", model.Id, snap?.Type);
            RecordInferenceFailure(model.Id);
            return new MLScoreResult(null, null, null, null);
        }

        // ── 3–7d. Build and standardise feature vector ────────────────────────
        var featureResult = await BuildFeaturesAsync(
            signal, candles, snap, currentRegime, signalTimeframe, model.Id,
            db, cancellationToken);
        if (featureResult is null)
        {
            RecordInferenceFailure(model.Id);
            return new MLScoreResult(null, null, null, null);
        }

        var (features, rawFeatures, cotEntry, orderedCandles, window,
             featureCount, useMeans, useStds) = featureResult.Value;

        // ── 8–12. Inference, calibration, magnitude, SHAP, direction ──────────
        // Offloaded to Task.Run — inference is CPU-bound (matrix multiplications,
        // conv forward passes, MC-Dropout sampling) and must not block the async
        // threadpool thread to avoid threadpool starvation under concurrent scoring.
        // The semaphore caps concurrent inference to Environment.ProcessorCount so
        // a burst of scoring requests doesn't saturate the threadpool.
        await _inferenceSemaphore.WaitAsync(cancellationToken);
        InferencePipelineResult? inferenceResult;
        try
        {
            inferenceResult = await Task.Run(() => RunInferencePipeline(
                features, featureCount, snap, window, signal, model,
                currentRegime, signalTimeframe), cancellationToken);
        }
        finally
        {
            _inferenceSemaphore.Release();
        }
        if (inferenceResult is null)
        {
            RecordInferenceFailure(model.Id);
            return new MLScoreResult(null, null, null, null);
        }

        // Inference succeeded — clear any prior failure count.
        _inferenceFailures.TryRemove(model.Id, out _);

        var (calibP, ensembleStd, direction, threshold, confidence,
             magnitude, magnitudeUncertaintyPips, magnitudeP10Pips, magnitudeP90Pips,
             mcDropoutMean, mcDropoutVariance, contributionsJson, shapValuesJson) = inferenceResult.Value;

        // ── 12b–f. Confidence dampenings (regime, transition, cold-start, cross-TF) ─
        confidence = await _configService.ApplyAllDampeningsAsync(
            confidence, model.Id, currentRegime,
            signal.Symbol, signalTimeframe, db, cancellationToken);

        // ── 12g. Consecutive-miss cooldown gate (Round 17) ───────────────────
        if (await _configService.IsCooldownActiveAsync(
                signal.Symbol, signalTimeframe, model.Id, db, cancellationToken))
            return new MLScoreResult(null, null, null, null);

        // ── 12c. Multi-model consensus filter (Round 10) ─────────────────────
        if (Stopwatch.GetElapsedTime(scoringStart) < ScoringBudget)
        {
            int consensusMin = await _configService.GetConsensusMinModelsAsync(db, cancellationToken);
            if (consensusMin >= 2)
            {
                bool passed = await RunConsensusFilter(
                    consensusMin, model.Id, signal.Symbol, signalTimeframe, direction,
                    rawFeatures, featureCount, window, db, cancellationToken);
                if (!passed)
                    return new MLScoreResult(null, null, null, null);
            }
        }
        else
        {
            _logger.LogDebug("Scoring budget exceeded — skipping consensus filter for {Symbol}/{Tf}",
                signal.Symbol, signalTimeframe);
        }

        // ── 13. Half-Kelly fraction ───────────────────────────────────────────
        // f* = max(0, 2p − 1) × 0.5 — caps bet size to half the full-Kelly optimum,
        // reducing variance at the cost of a small EV reduction.
        double kellyFraction = Math.Max(0.0, 2.0 * calibP - 1.0) * HalfKellyFactor;

        // ── 13b. BSS-based Kelly multiplier (Round 9) ─────────────────────────
        if (snap.BrierSkillScore < BssFloor)
        {
            double bssMultiplier = Math.Clamp(0.5 + (snap.BrierSkillScore + 0.05) / 0.15 * 0.5, 0.5, 1.0);
            kellyFraction *= bssMultiplier;
        }

        // ── 13c. Live-accuracy Kelly multiplier (Round 16) ───────────────────
        if (Stopwatch.GetElapsedTime(scoringStart) >= ScoringBudget)
            _logger.LogDebug("Scoring budget exceeded — skipping live Kelly multiplier for {Symbol}/{Tf}",
                signal.Symbol, signalTimeframe);
        else
            kellyFraction = await _configService.ApplyLiveKellyMultiplierAsync(
                kellyFraction, signal.Symbol, signalTimeframe, model.Id, db, cancellationToken);

        // ── 14–23. Compute enrichments ────────────────────────────────────────
        var ctx = new ScoringContext(
            calibP, ensembleStd, threshold, features, featureCount, snap,
            signal, model, currentRegime, signalTimeframe, cotEntry,
            scoringStart, db);
        var enrichments = await ComputeEnrichmentsAsync(ctx, cancellationToken);
        var (conformalSet, conformalSetSize, metaLabelScore, jackknifeInterval,
             entropyScore, oodMahalanobisScore, isOod, abstentionScore,
             regimeRoutingDecision, minTReconciledProbability,
             estimatedTimeToTargetBars, survivalHazardRate, counterfactualJson) = enrichments;

        var scoringElapsed = Stopwatch.GetElapsedTime(scoringStart);
        if (scoringElapsed > ScoringBudget)
            _logger.LogWarning(
                "Scoring for {Symbol}/{Tf} model {Id} took {Elapsed:F1}s — exceeds {Budget}s budget",
                signal.Symbol, signalTimeframe, model.Id,
                scoringElapsed.TotalSeconds, ScoringBudget.TotalSeconds);

        _logger.LogDebug(
            "ML score for {Symbol}/{Tf} model={ModelId} regime={Regime}: dir={Dir} calibP={P:F4} " +
            "threshold={Thr:F4} ensStd={Std:F4} conf={Conf:F4} mag={Mag:F2} kelly={Kelly:F4} " +
            "conformal={Conf2} meta={Meta} jackknife={JK} abstention={Abs} ood={OOD} " +
            "mcVar={McVar} minT={MinT} survival={Surv} elapsed={Elapsed:F3}s",
            signal.Symbol, signalTimeframe, model.Id, model.RegimeScope ?? "global",
            direction, calibP, threshold, ensembleStd, confidence, magnitude, kellyFraction,
            conformalSet ?? "n/a", metaLabelScore?.ToString("F3") ?? "n/a", jackknifeInterval ?? "n/a",
            abstentionScore?.ToString("F3") ?? "n/a", isOod,
            mcDropoutVariance?.ToString("F4") ?? "n/a",
            minTReconciledProbability?.ToString("F4") ?? "n/a",
            estimatedTimeToTargetBars?.ToString("F1") ?? "n/a",
            scoringElapsed.TotalSeconds);

        return new MLScoreResult(
            PredictedDirection:           direction,
            PredictedMagnitudePips:       (decimal)magnitude,
            ConfidenceScore:              (decimal)confidence,
            MLModelId:                    model.Id,
            EnsembleDisagreement:         (decimal)ensembleStd,
            ContributionsJson:            contributionsJson,
            KellyFraction:                (decimal)kellyFraction,
            ConformalSet:                 conformalSet,
            MetaLabelScore:               metaLabelScore,
            JackknifeInterval:            jackknifeInterval,
            AbstentionScore:              abstentionScore,
            ConformalSetSize:             conformalSetSize,
            EntropyScore:                 (decimal)entropyScore,
            MagnitudeUncertaintyPips:     magnitudeUncertaintyPips,
            McDropoutVariance:            mcDropoutVariance,
            McDropoutMean:                mcDropoutMean,
            CounterfactualJson:           counterfactualJson,
            ShapValuesJson:               shapValuesJson,
            MagnitudeP10Pips:             magnitudeP10Pips,
            MagnitudeP90Pips:             magnitudeP90Pips,
            OodMahalanobisScore:          oodMahalanobisScore,
            IsOod:                        isOod,
            RegimeRoutingDecision:        regimeRoutingDecision,
            MinTReconciledProbability:    minTReconciledProbability,
            EstimatedTimeToTargetBars:    estimatedTimeToTargetBars,
            SurvivalHazardRate:           survivalHazardRate);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helper: circuit breaker failure tracking
    // ═══════════════════════════════════════════════════════════════════════════

    private static void RecordInferenceFailure(long modelId)
    {
        _inferenceFailures.AddOrUpdate(
            modelId,
            _ => (1, DateTime.UtcNow),
            (_, existing) => (existing.Count + 1, DateTime.UtcNow));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helper: snapshot deserialization with concurrency protection
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Retrieves the cached <see cref="ModelSnapshot"/> or deserialises from
    /// <see cref="MLModel.ModelBytes"/>. Uses a <see cref="ConcurrentDictionary"/>
    /// of <see cref="Lazy{Task}"/> so that concurrent callers <c>await</c> the same
    /// deserialization task instead of blocking threadpool threads.
    /// </summary>
    private static bool HasModelWeights(ModelSnapshot snap) =>
        snap.Weights.Length > 0 ||
        !string.IsNullOrEmpty(snap.ConvWeightsJson) ||
        !string.IsNullOrEmpty(snap.GbmTreesJson) ||
        !string.IsNullOrEmpty(snap.TabNetAttentionJson) ||
        !string.IsNullOrEmpty(snap.FtTransformerAdditionalLayersJson) ||
        !string.IsNullOrEmpty(snap.RotationForestJson);

    private async Task<ModelSnapshot?> GetOrDeserializeSnapshotAsync(MLModel model)
    {
        var cacheKey = $"{SnapshotCacheKeyPrefix}{model.Id}";
        if (_cache.TryGetValue<ModelSnapshot>(cacheKey, out var snap) && snap is not null)
            return snap;

        var lazy = _snapshotInflight.GetOrAdd(model.Id, _ => new Lazy<Task<ModelSnapshot?>>(() =>
            Task.Run(() =>
            {
                try
                {
                    return JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!, JsonOptions);
                }
                catch
                {
                    return null;
                }
            })));

        snap = await lazy.Value;
        _snapshotInflight.TryRemove(model.Id, out var removed);
        // Only removed if it was the same Lazy instance we awaited — prevents
        // discarding a newer Lazy added by a concurrent caller after cache expiry.
        if (removed != null && !ReferenceEquals(removed, lazy))
            _snapshotInflight.TryAdd(model.Id, removed);

        if (snap is not null)
            _cache.Set(cacheKey, snap, SnapshotCacheDuration);
        else
            _logger.LogWarning("Failed to deserialise ModelSnapshot for model {Id}", model.Id);

        return snap;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helper: DB query timeout
    // ═══════════════════════════════════════════════════════════════════════════

    private static CancellationTokenSource CreateLinkedTimeout(CancellationToken parent)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        cts.CancelAfter(DbQueryTimeout);
        return cts;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helper: standardise features
    // ═══════════════════════════════════════════════════════════════════════════

    internal static float[] StandardiseFeatures(float[] rawFeatures, float[] means, float[] stds, int featureCount)
    {
        float[] features = new float[featureCount];
        for (int j = 0; j < featureCount && j < rawFeatures.Length; j++)
        {
            if (!float.IsFinite(rawFeatures[j]))
            {
                features[j] = 0f;
                continue;
            }
            float std  = j < stds.Length  && stds[j]  > 1e-8f ? stds[j]  : 1f;
            float mean = j < means.Length ? means[j] : 0f;
            features[j] = (rawFeatures[j] - mean) / std;
        }
        return features;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helper: apply feature mask (zero pruned features)
    // ═══════════════════════════════════════════════════════════════════════════

    internal static void ApplyFeatureMask(float[] features, bool[] mask, int featureCount)
    {
        if (mask.Length != featureCount) return;
        for (int j = 0; j < featureCount; j++)
        {
            if (!mask[j])
                features[j] = 0f;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helper: fractional differencing with cached lagged feature vectors
    // ═══════════════════════════════════════════════════════════════════════════

    private float[] ApplyFractionalDifferencing(
        float[] features, ModelSnapshot snap, List<Candle> orderedCandles,
        int featureCount, float[] useMeans, float[] useStds, CotFeatureEntry cotEntry,
        string symbol, Timeframe timeframe, long modelId)
    {
        if (snap.FracDiffD <= 0.0)
            return features;

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
        int minCandles = MLFeatureHelper.LookbackWindow + W;

        if (W <= 1 || orderedCandles.Count < minCandles)
        {
            if (W > 1)
                _logger.LogDebug(
                    "FracDiff inference: insufficient candles ({Have}/{Need}) for {Symbol}/{Tf} — skipping",
                    orderedCandles.Count, minCandles, symbol, timeframe);
            return features;
        }

        var fdFeatures = new float[featureCount];

        // w_0 — current bar (already standardised in `features`)
        for (int j = 0; j < featureCount; j++)
            fdFeatures[j] = (float)(fdWeights[0] * features[j]);

        // Pre-compute lagged standardised feature vectors (avoids redundant BuildFeatureVector calls
        // when fractional differencing window overlaps between consecutive scoring calls).
        // Each lag only needs one BuildFeatureVector + standardise call.
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

            double weight = fdWeights[lag];
            for (int j = 0; j < featureCount && j < lagRaw.Length; j++)
            {
                if (!float.IsFinite(lagRaw[j])) continue;
                float std  = j < useStds.Length  && useStds[j]  > 1e-8f ? useStds[j]  : 1f;
                float mean = j < useMeans.Length ? useMeans[j] : 0f;
                fdFeatures[j] += (float)(weight * (lagRaw[j] - mean) / std);
            }
        }

        // Replace features only when all blended values are finite
        for (int j = 0; j < featureCount; j++)
        {
            if (!float.IsFinite(fdFeatures[j]))
            {
                _logger.LogDebug(
                    "FracDiff inference: non-finite value detected for {Symbol}/{Tf} model {Id} — skipping",
                    symbol, timeframe, modelId);
                return features;
            }
        }

        return fdFeatures;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helper: consensus filter (batch-loads alternate model snapshots)
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<bool> RunConsensusFilter(
        int consensusMin, long primaryModelId,
        string symbol, Timeframe timeframe, TradeDirection direction,
        float[] rawFeatures, int featureCount, List<Candle> candleWindow,
        Microsoft.EntityFrameworkCore.DbContext db, CancellationToken cancellationToken)
    {
        int agreedCount = 1; // primary model always counts

        // Batch-load alternate models — fetch cache-missed models in a single query
        // instead of one roundtrip per model.
        var altEntries = new List<(long Id, ModelSnapshot Snap)>();
        var uncachedIds = new List<long>();

        // First pass: collect IDs and resolve cache hits.
        var otherModels = await db.Set<MLModel>()
            .AsNoTracking()
            .Where(x => x.Symbol    == symbol &&
                        x.Timeframe == timeframe &&
                        x.IsActive  && !x.IsDeleted   &&
                        x.Id        != primaryModelId  &&
                        x.ModelBytes != null)
            .Select(x => new { x.Id })
            .ToListAsync(cancellationToken);

        foreach (var m in otherModels)
        {
            var snapCacheKey = $"{SnapshotCacheKeyPrefix}{m.Id}";
            if (_cache.TryGetValue<ModelSnapshot>(snapCacheKey, out var cachedSnap) && cachedSnap is not null)
                altEntries.Add((m.Id, cachedSnap));
            else
                uncachedIds.Add(m.Id);
        }

        // Second pass: batch-load all cache-missed models in a single query.
        if (uncachedIds.Count > 0)
        {
            var uncachedModels = await db.Set<MLModel>()
                .AsNoTracking()
                .Where(x => uncachedIds.Contains(x.Id))
                .ToListAsync(cancellationToken);

            foreach (var altModel in uncachedModels)
            {
                if (altModel.ModelBytes is not { Length: > 0 }) continue;
                var altSnap = await GetOrDeserializeSnapshotAsync(altModel);
                if (altSnap is not null)
                    altEntries.Add((altModel.Id, altSnap));
            }
        }

        // Score alternate models concurrently with early cancellation — once quorum
        // is reached, remaining in-flight inference tasks are cancelled to save CPU.
        using var quorumCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = quorumCts.Token;

        var scoreTasks = altEntries.Select(entry => ScoreAlternateModelAsync(
            entry.Id, entry.Snap, rawFeatures, featureCount, candleWindow, linkedToken)).ToList();

        // Process results as they complete — cancel remaining tasks once quorum is met.
        foreach (var completedTask in scoreTasks)
        {
            (TradeDirection Direction, double CalibP)? result;
            try
            {
                result = await completedTask;
            }
            catch (OperationCanceledException) when (quorumCts.IsCancellationRequested
                                                     && !cancellationToken.IsCancellationRequested)
            {
                // Task was cancelled because quorum was already reached — skip.
                continue;
            }

            if (result is null) continue;
            if (result.Value.Direction == direction)
            {
                agreedCount++;
                if (agreedCount >= consensusMin)
                {
                    await quorumCts.CancelAsync();
                    break;
                }
            }
        }

        if (agreedCount < consensusMin)
        {
            _logger.LogDebug(
                "Consensus filter: {Symbol}/{Tf} — only {Agreed}/{Total} models agree on {Dir}; " +
                "need {Min}. Signal suppressed.",
                symbol, timeframe, agreedCount, otherModels.Count + 1, direction, consensusMin);
            return false;
        }

        _logger.LogDebug(
            "Consensus filter: {Symbol}/{Tf} — {Agreed}/{Total} models agree on {Dir}. Proceeding.",
            symbol, timeframe, agreedCount, otherModels.Count + 1, direction);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helper: score a single alternate model for consensus (parallelisable)
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<(TradeDirection Direction, double CalibP)?> ScoreAlternateModelAsync(
        long altModelId, ModelSnapshot altSnap,
        float[] rawFeatures, int featureCount, List<Candle> candleWindow,
        CancellationToken cancellationToken)
    {
        int altFc = altSnap.Features.Length > 0 ? altSnap.Features.Length : rawFeatures.Length;
        float[] altFeatures = StandardiseFeatures(rawFeatures, altSnap.Means, altSnap.Stds, altFc);

        ApplyFeatureMask(altFeatures, altSnap.ActiveFeatureMask, altFc);

        var altEngine = ResolveEngine(altSnap);
        if (altEngine is null) return null;

        await _inferenceSemaphore.WaitAsync(cancellationToken);
        double altRaw;
        try
        {
            if (altEngine is EnsembleInferenceEngine)
            {
                (altRaw, _) = await Task.Run(() => EnsembleInferenceEngine.EnsembleProb(
                    altFeatures, altSnap.Weights, altSnap.Biases, altFc,
                    altSnap.FeatureSubsetIndices, null, 0.0, int.MaxValue, null, null, null,
                    altSnap.MlpHiddenWeights, altSnap.MlpHiddenBiases, altSnap.MlpHiddenDim),
                    cancellationToken);
            }
            else
            {
                var altResult = await Task.Run(() => altEngine.RunInference(
                    altFeatures, altFc, altSnap, candleWindow, altModelId, 0, 0),
                    cancellationToken);
                if (altResult is null) return null;
                altRaw = altResult.Value.Probability;
            }
        }
        finally
        {
            _inferenceSemaphore.Release();
        }

        double altCalibP = InferenceHelpers.ApplyBasicCalibration(altRaw, altSnap);
        double altThr = altSnap.OptimalThreshold > 0.0 ? altSnap.OptimalThreshold : 0.5;
        var altDir = altCalibP >= altThr ? TradeDirection.Buy : TradeDirection.Sell;
        return (altDir, altCalibP);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helper: score a single alternate timeframe for multi-TF blend (parallelisable)
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<(Timeframe Tf, double CalibP)?> ScoreTimeframeAsync(
        Timeframe tf, MLModel altModel, ModelSnapshot altSnap, List<Candle> altWindow,
        CotFeatureEntry cotEntry, CancellationToken cancellationToken)
    {
        var sliced = ScoringEnrichmentCalculator.SliceCandleWindow(altWindow);
        if (sliced is null) return null;
        var (featureWindow, altCurrent, altPrevious) = sliced.Value;

        float[] altRawFeatures = MLFeatureHelper.BuildFeatureVector(featureWindow, altCurrent, altPrevious, cotEntry);

        int altFc = altSnap.Features.Length > 0 ? altSnap.Features.Length : altRawFeatures.Length;
        var altFeatures = StandardiseFeatures(altRawFeatures, altSnap.Means, altSnap.Stds, altFc);

        ApplyFeatureMask(altFeatures, altSnap.ActiveFeatureMask, altFc);

        var altEngine = ResolveEngine(altSnap);
        if (altEngine is null) return null;

        await _inferenceSemaphore.WaitAsync(cancellationToken);
        InferenceResult? altResult;
        try
        {
            altResult = await Task.Run(() => altEngine.RunInference(
                altFeatures, altFc, altSnap, featureWindow, altModel.Id, 0, 0),
                cancellationToken);
        }
        finally
        {
            _inferenceSemaphore.Release();
        }
        if (altResult is null) return null;

        double calibP = InferenceHelpers.ApplyBasicCalibration(altResult.Value.Probability, altSnap);
        return (tf, calibP);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Weighted multi-timeframe probability (blending H1, H4, D1 models)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes a timeframe-weighted average of calibrated probabilities from
    /// H1, H4, and D1 models. Each alternate timeframe loads its own candles
    /// and builds a proper feature vector (not re-scaled from the primary TF).
    /// Higher timeframes receive more weight (D1=3, H4=2, H1=1).
    /// Returns null when fewer than 2 timeframe models are available.
    /// </summary>
    private async Task<decimal?> ComputeWeightedMultiTimeframeProbability(
        string symbol, Timeframe currentTimeframe, double currentCalibP,
        int featureCount, CotFeatureEntry cotEntry,
        Microsoft.EntityFrameworkCore.DbContext db, CancellationToken cancellationToken)
    {
        var hierarchy = new[] { Timeframe.H1, Timeframe.H4, Timeframe.D1 };
        var altTimeframes = hierarchy.Where(tf => tf != currentTimeframe).ToArray();

        // Load models and snapshots sequentially (DB context is not thread-safe),
        // then run inference concurrently.
        var tfEntries = new List<(Timeframe Tf, MLModel Model, ModelSnapshot Snap, List<Candle> Window)>();
        foreach (var tf in altTimeframes)
        {
            try
            {
                using var cts = CreateLinkedTimeout(cancellationToken);
                var altModel = await db.Set<MLModel>()
                    .AsNoTracking()
                    .Where(x => x.Symbol    == symbol &&
                                x.Timeframe == tf &&
                                x.IsActive  && !x.IsDeleted &&
                                x.ModelBytes != null)
                    .OrderByDescending(x => x.ExpectedValue ?? -1m)
                    .ThenByDescending(x => x.ActivatedAt)
                    .FirstOrDefaultAsync(cts.Token);

                if (altModel is null) continue;

                var altSnap = await GetOrDeserializeSnapshotAsync(altModel);
                if (altSnap is null || !HasModelWeights(altSnap))
                    continue;

                int requiredCandles = MLFeatureHelper.LookbackWindow + 2;
                using var candleCts = CreateLinkedTimeout(cancellationToken);
                var altCandles = await db.Set<Candle>()
                    .AsNoTracking()
                    .Where(c => c.Symbol == symbol && c.Timeframe == tf && !c.IsDeleted)
                    .OrderByDescending(c => c.Timestamp)
                    .Take(requiredCandles)
                    .OrderBy(c => c.Timestamp)
                    .ToListAsync(candleCts.Token);

                if (altCandles.Count < MLFeatureHelper.LookbackWindow + 1)
                    continue;

                tfEntries.Add((tf, altModel, altSnap, altCandles));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Data load timed out for {Symbol}/{Tf} — skipping TF in multi-TF blend",
                    symbol, tf);
            }
        }

        // Run inference for all alternate timeframes concurrently.
        var inferenceTasks = tfEntries.Select(entry => ScoreTimeframeAsync(
            entry.Tf, entry.Model, entry.Snap, entry.Window, cotEntry, cancellationToken)).ToList();

        var inferenceResults = await Task.WhenAll(inferenceTasks);

        var probsByTf = new Dictionary<Timeframe, double> { [currentTimeframe] = currentCalibP };
        foreach (var result in inferenceResults)
        {
            if (result is not null)
                probsByTf[result.Value.Tf] = result.Value.CalibP;
        }

        if (probsByTf.Count < 2)
            return null;

        // Weighted average — higher timeframes receive more weight.
        // Weights are configurable via EngineConfig (MLScoring:MultiTfWeight:{Tf}).
        double wSum = 0, pSum = 0;
        foreach (var (tf, prob) in probsByTf)
        {
            double weight = await _configService.GetMultiTfWeightAsync(tf, db, cancellationToken);
            wSum += weight;
            pSum += weight * prob;
        }

        return (decimal)(pSum / wSum);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COT normalisation
    // ═══════════════════════════════════════════════════════════════════════════

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
            netNorm  = MLFeatureHelper.Clamp(
                ((float)report.NetNonCommercialPositioning - snap.CotNetNormMin) / netRange * 6f - 3f,
                -3f, 3f);
            momentum = MLFeatureHelper.Clamp(
                ((float)report.NetPositioningChangeWeekly - snap.CotMomNormMin) / momRange * 6f - 3f,
                -3f, 3f);
        }
        else
        {
            netNorm  = MLFeatureHelper.Clamp(
                (float)((double)report.NetNonCommercialPositioning / 100_000), -3f, 3f);
            momentum = MLFeatureHelper.Clamp(
                (float)((double)report.NetPositioningChangeWeekly  / 10_000),  -3f, 3f);
        }

        return new CotFeatureEntry(netNorm, momentum);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Feature pipeline (extracted from ScoreAsync steps 3–7d)
    // ═══════════════════════════════════════════════════════════════════════════

    private readonly record struct FeaturePipelineResult(
        float[] Features,
        float[] RawFeatures,
        CotFeatureEntry CotEntry,
        List<Candle> OrderedCandles,
        List<Candle> Window,
        int FeatureCount,
        float[] UseMeans,
        float[] UseStds);

    /// <summary>
    /// Validates the candle window, loads COT data, builds and standardises the
    /// feature vector, applies fractional differencing, and masks pruned features.
    /// Returns null when candles are insufficient for inference.
    /// </summary>
    private async Task<FeaturePipelineResult?> BuildFeaturesAsync(
        TradeSignal signal, IReadOnlyList<Candle> candles,
        ModelSnapshot snap, string? currentRegime,
        Timeframe signalTimeframe, long modelId,
        Microsoft.EntityFrameworkCore.DbContext db, CancellationToken cancellationToken)
    {
        if (candles.Count < MLFeatureHelper.LookbackWindow + 1)
        {
            _logger.LogDebug(
                "Insufficient candles ({Count}) for inference — need {Min}",
                candles.Count, MLFeatureHelper.LookbackWindow + 1);
            return null;
        }

        var baseCurrency = signal.Symbol.Length >= 3 ? signal.Symbol[..3] : signal.Symbol;
        var signalTs     = signal.GeneratedAt;

        var cotCacheKey = $"{CotCacheKeyPrefix}{baseCurrency}";
        if (!_cache.TryGetValue<COTReport>(cotCacheKey, out var cotReport))
        {
            try
            {
                using var cotCts = CreateLinkedTimeout(cancellationToken);
                cotReport = await db.Set<COTReport>()
                    .AsNoTracking()
                    .Where(c => c.Currency == baseCurrency && c.ReportDate <= signalTs)
                    .OrderByDescending(c => c.ReportDate)
                    .FirstOrDefaultAsync(cotCts.Token);

                _cache.Set(cotCacheKey, cotReport, CotCacheDuration);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("COT lookup timed out for {Currency} — using zero entry", baseCurrency);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "COT lookup failed for {Currency} — using zero entry", baseCurrency);
            }
        }

        CotFeatureEntry cotEntry = cotReport is null
            ? CotFeatureEntry.Zero
            : BuildCotEntry(cotReport, snap);

        var orderedCandles = candles.OrderBy(c => c.Timestamp).ToList();
        var sliced = ScoringEnrichmentCalculator.SliceCandleWindow(orderedCandles);
        if (sliced is null)
        {
            _logger.LogDebug(
                "Insufficient candles ({Count}) for window slicing — need {Min}",
                orderedCandles.Count, MLFeatureHelper.LookbackWindow + 1);
            return null;
        }
        var (window, current, previous) = sliced.Value;

        float[] rawFeatures = MLFeatureHelper.BuildFeatureVector(window, current, previous, cotEntry);

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

        float[] features = StandardiseFeatures(rawFeatures, useMeans, useStds, featureCount);

        features = ApplyFractionalDifferencing(
            features, snap, orderedCandles, featureCount, useMeans, useStds, cotEntry,
            signal.Symbol, signalTimeframe, modelId);

        ApplyFeatureMask(features, snap.ActiveFeatureMask, featureCount);

        return new FeaturePipelineResult(
            features, rawFeatures, cotEntry, orderedCandles, window,
            featureCount, useMeans, useStds);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scoring enrichments (extracted from ScoreAsync steps 14–23)
    // ═══════════════════════════════════════════════════════════════════════════

    private readonly record struct ScoringEnrichments(
        string? ConformalSet,
        int? ConformalSetSize,
        decimal? MetaLabelScore,
        string? JackknifeInterval,
        double EntropyScore,
        double? OodMahalanobisScore,
        bool IsOod,
        decimal? AbstentionScore,
        string? RegimeRoutingDecision,
        decimal? MinTReconciledProbability,
        double? EstimatedTimeToTargetBars,
        double? SurvivalHazardRate,
        string? CounterfactualJson);

    /// <summary>
    /// Computes all non-critical scoring enrichments: conformal prediction set,
    /// meta-label, jackknife interval, entropy, OOD detection, abstention gate,
    /// regime routing, multi-TF blend, survival analysis, and counterfactual explanation.
    /// </summary>
    private async Task<ScoringEnrichments> ComputeEnrichmentsAsync(
        ScoringContext ctx, CancellationToken cancellationToken)
    {
        var snap = ctx.Snap;
        var signal = ctx.Signal;
        var model = ctx.Model;
        double calibP = ctx.CalibP;
        double ensembleStd = ctx.EnsembleStd;
        double threshold = ctx.Threshold;
        float[] features = ctx.Features;
        int featureCount = ctx.FeatureCount;
        string? currentRegime = ctx.CurrentRegime;
        var signalTimeframe = ctx.SignalTimeframe;
        var cotEntry = ctx.CotEntry;
        long scoringStart = ctx.ScoringStart;
        var db = ctx.Db;

        // Pure computations — delegated to ScoringEnrichmentCalculator for testability
        var (conformalSet, conformalSetSize) =
            ScoringEnrichmentCalculator.ComputeConformalSet(calibP, snap.ConformalQHat);

        var metaLabelScore = ScoringEnrichmentCalculator.ComputeMetaLabelScore(
            calibP, ensembleStd, features, featureCount,
            snap.MetaLabelWeights, snap.MetaLabelBias);

        var jackknifeInterval = ScoringEnrichmentCalculator.ComputeJackknifeInterval(snap.JackknifeResiduals);

        double entropyScore = ScoringEnrichmentCalculator.ComputeEntropy(calibP);

        var (oodMahalanobisScore, isOod) = ScoringEnrichmentCalculator.ComputeOodMahalanobis(
            features, featureCount,
            snap.FeatureVariances ?? Array.Empty<double>(),
            snap.OodThreshold, DefaultOodThresholdSigma);

        if (isOod)
            _logger.LogDebug(
                "OOD detected for {Symbol}/{Tf} model {Id}: Mahalanobis={Maha:F2} > threshold={Thr:F1}",
                signal.Symbol, signalTimeframe, model.Id, oodMahalanobisScore!.Value,
                snap.OodThreshold > 0.0 ? snap.OodThreshold : DefaultOodThresholdSigma);

        var abstentionScore = ScoringEnrichmentCalculator.ComputeAbstentionScore(
            calibP, ensembleStd, metaLabelScore, oodMahalanobisScore, entropyScore,
            snap.AbstentionWeights, snap.AbstentionBias);

        var regimeRoutingDecision = ScoringEnrichmentCalculator.ComputeRegimeRoutingDecision(
            currentRegime, model.RegimeScope);

        // Weighted multi-timeframe probability (requires DB + inference — not pure)
        decimal? minTReconciledProbability = null;
        if (Stopwatch.GetElapsedTime(scoringStart) >= ScoringBudget)
        {
            _logger.LogDebug("Scoring budget exceeded — skipping multi-TF blend for {Symbol}/{Tf}",
                signal.Symbol, signalTimeframe);
        }
        else try
        {
            minTReconciledProbability = await ComputeWeightedMultiTimeframeProbability(
                signal.Symbol, signalTimeframe, calibP, featureCount, cotEntry,
                db, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Multi-TF blend timed out for {Symbol}/{Tf}", signal.Symbol, signalTimeframe);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Multi-TF blend failed for {Symbol}/{Tf}", signal.Symbol, signalTimeframe);
        }

        var (estimatedTimeToTargetBars, survivalHazardRate) = ScoringEnrichmentCalculator.ComputeSurvivalAnalysis(
            features, featureCount,
            snap.SurvivalHazard ?? Array.Empty<double>(),
            snap.FeatureImportanceScores ?? Array.Empty<double>());

        // Counterfactual explanation
        string? counterfactualJson = null;
        if (snap.Features.Length > 0 && snap.Weights is { Length: > 0 })
        {
            try
            {
                counterfactualJson = ScoringEnrichmentCalculator.ComputeCounterfactualJson(
                    features, snap.Weights, snap.Biases, snap.FeatureSubsetIndices,
                    snap.Features, featureCount, calibP, threshold);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Counterfactual computation failed for {Symbol}/{Tf} model {Id}",
                    signal.Symbol, signalTimeframe, model.Id);
            }
        }

        return new ScoringEnrichments(
            conformalSet, conformalSetSize, metaLabelScore, jackknifeInterval,
            entropyScore, oodMahalanobisScore, isOod, abstentionScore,
            regimeRoutingDecision, minTReconciledProbability,
            estimatedTimeToTargetBars, survivalHazardRate, counterfactualJson);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scoring context (bundles shared state for enrichments)
    // ═══════════════════════════════════════════════════════════════════════════

    private readonly record struct ScoringContext(
        double CalibP,
        double EnsembleStd,
        double Threshold,
        float[] Features,
        int FeatureCount,
        ModelSnapshot Snap,
        TradeSignal Signal,
        MLModel Model,
        string? CurrentRegime,
        Timeframe SignalTimeframe,
        CotFeatureEntry CotEntry,
        long ScoringStart,
        Microsoft.EntityFrameworkCore.DbContext Db);

    // ═══════════════════════════════════════════════════════════════════════════
    // Inference pipeline (extracted from ScoreAsync steps 8–12)
    // ═══════════════════════════════════════════════════════════════════════════

    private readonly record struct InferencePipelineResult(
        double CalibP,
        double EnsembleStd,
        TradeDirection Direction,
        double Threshold,
        double Confidence,
        double Magnitude,
        decimal? MagnitudeUncertaintyPips,
        decimal? MagnitudeP10Pips,
        decimal? MagnitudeP90Pips,
        decimal? McDropoutMean,
        decimal? McDropoutVariance,
        string? ContributionsJson,
        string? ShapValuesJson);

    /// <summary>
    /// Runs model inference (TCN/QRF/ensemble), MC-Dropout, full calibration pipeline,
    /// magnitude prediction, SHAP attribution, and initial direction/confidence derivation.
    /// Returns null when the TCN snapshot is invalid.
    /// </summary>
    private InferencePipelineResult? RunInferencePipeline(
        float[] features, int featureCount, ModelSnapshot snap,
        List<Candle> window, TradeSignal signal, MLModel model,
        string? currentRegime, Timeframe signalTimeframe)
    {
        // ── 8. Model inference (delegated to IModelInferenceEngine) ──────────
        var engine = ResolveEngine(snap);
        if (engine is null)
        {
            _logger.LogWarning("No inference engine found for model {Id} (type={Type})", model.Id, snap.Type);
            return null;
        }

        int mcSamples = snap.McDropoutVarianceThreshold > 0.0 ? DefaultMcDropoutSamples : 0;
        int mcSeed = HashCode.Combine(signal.Id, signal.Symbol, signal.GeneratedAt);

        var engineResult = engine.RunInference(
            features, featureCount, snap, window, model.Id, mcSamples, mcSeed);
        if (engineResult is null)
        {
            _logger.LogWarning("Inference engine returned null for model {Id}", model.Id);
            return null;
        }

        double rawProb              = engineResult.Value.Probability;
        double ensembleStd          = engineResult.Value.EnsembleStd;
        decimal? mcDropoutMean      = engineResult.Value.McDropoutMean;
        decimal? mcDropoutVariance  = engineResult.Value.McDropoutVariance;

        // ── 9. Platt / temperature scaling (with class-conditional fallback) ──
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

        // ── 9b. Isotonic calibration ─────────────────────────────────────────
        if (snap.IsotonicBreakpoints.Length >= 4)
            calibP = BaggedLogisticTrainer.ApplyIsotonicCalibration(calibP, snap.IsotonicBreakpoints);

        // ── 9c. Model age decay ──────────────────────────────────────────────
        if (snap.AgeDecayLambda > 0.0 && snap.TrainedAtUtc != default)
        {
            double daysSinceTrain = (DateTime.UtcNow - snap.TrainedAtUtc).TotalDays;
            double decayFactor    = Math.Exp(-snap.AgeDecayLambda * Math.Max(0.0, daysSinceTrain));
            calibP = 0.5 + (calibP - 0.5) * decayFactor;
        }

        // ── 9d. Feature stability diagnostic ─────────────────────────────────
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
        double magnitude = 0;
        int mlpH = snap.QrfMlpHiddenDim;
        if (mlpH > 0 && snap.QrfMlpW1.Length == featureCount * mlpH)
        {
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
        else if (TryPredictElmMagnitude(features, featureCount, snap, out var elmMagnitude))
        {
            magnitude = Math.Abs(elmMagnitude);
        }
        else if (snap.MagWeights.Length == featureCount)
        {
            magnitude = snap.MagBias;
            for (int j = 0; j < featureCount; j++)
                magnitude += snap.MagWeights[j] * features[j];
            magnitude = Math.Abs(magnitude);
        }

        // ── 10b. Heteroscedastic magnitude uncertainty ───────────────────────
        decimal? magnitudeUncertaintyPips = null;
        decimal? magnitudeP10Pips = null;
        decimal? magnitudeP90Pips = null;
        if (snap.MagQ90Weights.Length == featureCount)
        {
            double magQ90 = snap.MagQ90Bias;
            for (int j = 0; j < featureCount; j++)
                magQ90 += snap.MagQ90Weights[j] * features[j];
            magQ90 = Math.Abs(magQ90);

            magnitudeUncertaintyPips = (decimal)Math.Max(0.0, magQ90 - magnitude);
            magnitudeP90Pips = (decimal)magQ90;
            magnitudeP10Pips = (decimal)Math.Max(0.0, 2.0 * magnitude - magQ90);
        }
        if (snap.QuantileWeights is { Length: > 0 } qw && snap.QuantileBiases is { Length: > 0 } qb)
        {
            int nQuantiles = qw.Length;
            var quantileOutputs = new double[nQuantiles];
            for (int q = 0; q < nQuantiles; q++)
            {
                double qVal = q < qb.Length ? qb[q] : 0.0;
                int wLen = qw[q]?.Length ?? 0;
                for (int j = 0; j < featureCount && j < wLen; j++)
                    qVal += qw[q][j] * features[j];
                quantileOutputs[q] = Math.Abs(qVal);
            }
            magnitudeP10Pips = (decimal)quantileOutputs[0];
            magnitudeP90Pips = (decimal)quantileOutputs[^1];
            magnitudeUncertaintyPips = (decimal)Math.Max(0.0, quantileOutputs[^1] - quantileOutputs[0]) / 2m;
        }

        // ── 11. SHAP attribution ─────────────────────────────────────────────
        string? contributionsJson = null;
        try
        {
            contributionsJson = ScoringEnrichmentCalculator.ComputeShapContributionsJson(
                features, snap.Weights, snap.FeatureSubsetIndices, snap.Features, featureCount,
                snap.FeatureImportanceScores);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SHAP contributions computation failed for {Symbol}/{Tf} model {Id}",
                signal.Symbol, signalTimeframe, model.Id);
        }

        // ── 11b. Approximate SHAP values (importance × activation, not true permutation SHAP) ─
        string? shapValuesJson = null;
        if (snap.FeatureImportanceScores is { Length: > 0 } fiScores && fiScores.Length >= featureCount)
        {
            try
            {
                var approxShapValues = new double[featureCount];
                for (int j = 0; j < featureCount; j++)
                    approxShapValues[j] = Math.Round(fiScores[j] * features[j], 6);
                shapValuesJson = JsonSerializer.Serialize(approxShapValues);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Approximate SHAP computation failed for {Symbol}/{Tf} model {Id}",
                    signal.Symbol, signalTimeframe, model.Id);
            }
        }

        // ── 12. Derive outputs ───────────────────────────────────────────────
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

        return new InferencePipelineResult(
            calibP, ensembleStd, direction, threshold, confidence,
            magnitude, magnitudeUncertaintyPips, magnitudeP10Pips, magnitudeP90Pips,
            mcDropoutMean, mcDropoutVariance, contributionsJson, shapValuesJson);
    }

    private static bool TryPredictElmMagnitude(
        float[] features, int featureCount, ModelSnapshot snap, out double magnitude)
    {
        magnitude = 0.0;
        if (snap.ElmInputWeights is not { Length: > 0 } inputWeights ||
            snap.ElmInputBiases is not { Length: > 0 } inputBiases ||
            snap.ElmHiddenDim <= 0)
        {
            return false;
        }

        if (snap.MagAugWeightsFolds is { Length: > 0 } foldWeights &&
            snap.MagAugBiasFolds is { Length: > 0 } foldBiases)
        {
            int used = 0;
            double sum = 0.0;
            int foldCount = Math.Min(foldWeights.Length, foldBiases.Length);
            for (int i = 0; i < foldCount; i++)
            {
                if (foldWeights[i] is not { Length: > 0 }) continue;
                sum += PredictElmMagnitudeAug(
                    features, featureCount, foldWeights[i], foldBiases[i], snap,
                    inputWeights, inputBiases);
                used++;
            }

            if (used > 0)
            {
                magnitude = sum / used;
                return true;
            }
        }

        if (snap.MagAugWeights is { Length: > 0 })
        {
            magnitude = PredictElmMagnitudeAug(
                features, featureCount, snap.MagAugWeights, snap.MagAugBias, snap,
                inputWeights, inputBiases);
            return true;
        }

        return false;
    }

    private static double PredictElmMagnitudeAug(
        float[] features, int featureCount, double[] augWeights, double augBias,
        ModelSnapshot snap, double[][] inputWeights, double[][] inputBiases)
    {
        double pred = augBias;
        for (int j = 0; j < Math.Min(featureCount, Math.Min(features.Length, augWeights.Length)); j++)
            pred += augWeights[j] * features[j];

        int hiddenSize = snap.ElmHiddenDim;
        int[] defaultSubset = Enumerable.Range(0, featureCount).ToArray();
        for (int h = 0; h < hiddenSize; h++)
        {
            int augIdx = featureCount + h;
            if (augIdx >= augWeights.Length) break;

            double hSum = 0.0;
            int hCount = 0;
            for (int k = 0; k < inputWeights.Length && k < inputBiases.Length; k++)
            {
                var learnerBiases = inputBiases[k];
                if (learnerBiases is null || h >= learnerBiases.Length) continue;

                var learnerWeights = inputWeights[k];
                int[] subset = snap.FeatureSubsetIndices is { Length: > 0 } subsets && k < subsets.Length
                    ? subsets[k]
                    : defaultSubset;
                int subLen = subset.Length;
                double z = learnerBiases[h];
                int rowOff = h * subLen;
                for (int si = 0; si < subLen && rowOff + si < learnerWeights.Length; si++)
                {
                    int fi = subset[si];
                    if (fi < features.Length)
                        z += learnerWeights[rowOff + si] * features[fi];
                }

                ElmActivation activation = snap.LearnerActivations is { Length: > 0 } activations
                    ? (ElmActivation)activations[Math.Min(k, activations.Length - 1)]
                    : ElmActivation.Sigmoid;
                hSum += ElmMathHelper.Activate(z, activation);
                hCount++;
            }

            pred += augWeights[augIdx] * (hCount > 0 ? hSum / hCount : 0.0);
        }

        return pred;
    }
}
