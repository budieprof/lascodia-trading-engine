using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Composite ML evaluator that loads the active trained model for a strategy's
/// symbol/timeframe, runs inference via <see cref="IModelInferenceEngine"/>,
/// applies Platt-calibrated probabilities, and produces ATR-based trade signals
/// when confidence exceeds a configurable threshold.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyEvaluator))]
public class CompositeMLEvaluator : IStrategyEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private const string ModelCacheKeyPrefix = "CompositeML:Model:";
    private const string SnapshotCacheKeyPrefix = "CompositeML:Snap:";
    private static readonly TimeSpan ModelCacheDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SnapshotCacheDuration = TimeSpan.FromMinutes(30);

    private const int DefaultAtrPeriod = 14;
    private const double DefaultConfidenceThreshold = 0.65;
    private const double DefaultSlAtrMultiplier = 2.0;
    private const double DefaultTpAtrMultiplier = 3.0;
    private const int FeatureWindowSize = MLFeatureHelper.LookbackWindow; // 30
    private const int MinCandles = FeatureWindowSize + 20; // 50
    private const int McDropoutSamples = 0; // disabled for evaluator — speed matters
    private const double SignalExpiryMinutes = 60;

    private readonly IEnumerable<IModelInferenceEngine> _inferenceEngines;
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CompositeMLEvaluator> _logger;

    public CompositeMLEvaluator(
        IEnumerable<IModelInferenceEngine> inferenceEngines,
        IMemoryCache cache,
        IServiceScopeFactory scopeFactory,
        ILogger<CompositeMLEvaluator> logger)
    {
        _inferenceEngines = inferenceEngines;
        _cache            = cache;
        _scopeFactory     = scopeFactory;
        _logger           = logger;
    }

    public StrategyType StrategyType => StrategyType.CompositeML;

    public int MinRequiredCandles(Strategy strategy) => MinCandles;

    public async Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        // ── 1. Parse parameters ────────────────────────────────────────────────
        var parameters = ParseParameters(strategy.ParametersJson);

        if (candles.Count < MinCandles)
        {
            _logger.LogDebug(
                "CompositeML insufficient candles for {Symbol}/{Timeframe} strategy {StrategyId}: {Actual}/{Required}",
                strategy.Symbol, strategy.Timeframe, strategy.Id, candles.Count, MinCandles);
            return null;
        }

        int lastIdx = candles.Count - 1;

        // ── 2. Build feature vector ────────────────────────────────────────────
        var windowCandles = new List<Candle>(FeatureWindowSize);
        int windowStart = lastIdx - FeatureWindowSize;
        for (int i = windowStart; i < lastIdx; i++)
            windowCandles.Add(candles[i]);

        var currentCandle  = candles[lastIdx];
        var previousCandle = candles[lastIdx - 1];

        float[] features;
        try
        {
            features = MLFeatureHelper.BuildFeatureVector(
                windowCandles, currentCandle, previousCandle, CotFeatureEntry.Zero);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Feature extraction failed for {Symbol} strategy {StrategyId}",
                strategy.Symbol, strategy.Id);
            return null;
        }

        // ── 2b. Extend to 47-feature vector when cross-pair mode is enabled ───
        if (parameters.CrossPairEnabled)
        {
            try
            {
                using var featureScope = _scopeFactory.CreateScope();
                var crossPairProvider = featureScope.ServiceProvider.GetService<ICrossPairCandleProvider>();
                var newsProvider = featureScope.ServiceProvider.GetService<INewsProximityProvider>();
                var sentimentProvider = featureScope.ServiceProvider.GetService<ISentimentProvider>();

                float[]? crossPairFeatures = null;
                if (crossPairProvider != null)
                {
                    var crossCandles = await crossPairProvider.GetCrossPairCandlesAsync(
                        strategy.Symbol, strategy.Timeframe,
                        currentCandle.Timestamp, MLFeatureHelper.LookbackWindow, cancellationToken);

                    if (crossCandles.Count > 0)
                    {
                        // AppendCrossPairFeatures returns base + 12 appended; extract the 12
                        var appended = MLFeatureHelper.AppendCrossPairFeatures(
                            features, strategy.Symbol, windowCandles, crossCandles);
                        crossPairFeatures = appended[features.Length..];
                    }
                }

                double newsMinutes = double.MaxValue;
                if (newsProvider != null)
                    newsMinutes = await newsProvider.GetMinutesUntilNextEventAsync(
                        strategy.Symbol, cancellationToken);

                decimal baseSent = 0, quoteSent = 0;
                if (sentimentProvider != null)
                    (baseSent, quoteSent) = await sentimentProvider.GetSentimentAsync(
                        strategy.Symbol, cancellationToken);

                // Tick flow features
                TickFlowSnapshot? tickFlow = null;
                var tickFlowProvider = featureScope.ServiceProvider.GetService<ITickFlowProvider>();
                if (tickFlowProvider != null)
                    tickFlow = await tickFlowProvider.GetSnapshotAsync(strategy.Symbol, currentCandle.Timestamp, cancellationToken);

                // Economic surprise — only for 6+ char FX pair symbols
                float economicSurprise = 0f;
                if (strategy.Symbol.Length >= 6)
                try
                {
                    var baseCcy = strategy.Symbol[..3];
                    var quoteCcy = strategy.Symbol[3..6];
                    var scopedReadDb = featureScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                    var recentEvent = await scopedReadDb.GetDbContext().Set<EconomicEvent>()
                        .Where(e => !e.IsDeleted && e.Impact == EconomicImpact.High
                                 && e.Actual != null
                                 && e.ScheduledAt >= DateTime.UtcNow.AddHours(-24)
                                 && (e.Currency == baseCcy || e.Currency == quoteCcy))
                        .OrderByDescending(e => e.ScheduledAt)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (recentEvent != null)
                        economicSurprise = MLFeatureHelper.ComputeEconomicSurprise(
                            recentEvent.Actual, recentEvent.Forecast, recentEvent.Previous);
                }
                catch { /* Non-critical */ }

                // Compute price return for divergence feature
                decimal priceReturn = candles.Count >= 2
                    ? candles[^1].Close - candles[^2].Close
                    : 0m;

                // Proxy features: vol expectation, order flow, calendar density
                IReadOnlyList<(decimal Bid, decimal Ask, DateTime Timestamp)>? recentTicksForProxy = null;
                if (tickFlowProvider != null)
                {
                    try
                    {
                        var scopedTickDb = featureScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                        var rawTicks = await scopedTickDb.GetDbContext().Set<TickRecord>()
                            .Where(t => t.Symbol == strategy.Symbol && !t.IsDeleted
                                     && t.TickTimestamp >= DateTime.UtcNow.AddMinutes(-5))
                            .OrderByDescending(t => t.TickTimestamp)
                            .Take(100)
                            .Select(t => new { t.Bid, t.Ask, t.TickTimestamp })
                            .ToListAsync(cancellationToken);
                        recentTicksForProxy = rawTicks
                            .Select(t => (t.Bid, t.Ask, t.TickTimestamp))
                            .ToList();
                    }
                    catch { /* Non-critical */ }
                }

                // Count upcoming events for calendar density
                int upcomingEventCount = 0;
                try
                {
                    if (strategy.Symbol.Length >= 6)
                    {
                        var baseCcy = strategy.Symbol[..3];
                        var quoteCcy = strategy.Symbol[3..6];
                        var scopedEventDb = featureScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                        upcomingEventCount = await scopedEventDb.GetDbContext().Set<EconomicEvent>()
                            .Where(e => !e.IsDeleted
                                     && (e.Impact == EconomicImpact.High || e.Impact == EconomicImpact.Medium)
                                     && (e.Currency == baseCcy || e.Currency == quoteCcy)
                                     && e.ScheduledAt >= DateTime.UtcNow
                                     && e.ScheduledAt <= DateTime.UtcNow.AddHours(24))
                            .CountAsync(cancellationToken);
                    }
                }
                catch { /* Non-critical */ }

                var proxyData = MLFeatureHelper.ComputeProxyFeatures(
                    candles, candles.Count - 1, tickFlow, recentTicksForProxy, upcomingEventCount);

                features = MLFeatureHelper.BuildExtendedFeatureVector(
                    features, crossPairFeatures, newsMinutes, baseSent, quoteSent,
                    tickFlow, priceReturn, economicSurprise, proxyData);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "CompositeMLEvaluator: extended feature construction failed for {Symbol} — using base features",
                    strategy.Symbol);
            }
        }

        // ── 3. Load active MLModel ─────────────────────────────────────────────
        var model = await GetActiveModelAsync(strategy.Symbol, strategy.Timeframe, cancellationToken);
        if (model is null)
        {
            _logger.LogInformation(
                "No active MLModel for {Symbol}/{Timeframe} — CompositeML skipping (strategy {StrategyId})",
                strategy.Symbol, strategy.Timeframe, strategy.Id);
            return null;
        }

        if (model.ModelBytes is null || model.ModelBytes.Length == 0)
        {
            _logger.LogWarning(
                "Active MLModel {ModelId} has no ModelBytes — CompositeML skipping",
                model.Id);
            return null;
        }

        // ── 4. Deserialize ModelSnapshot ───────────────────────────────────────
        var snapshot = GetOrDeserializeSnapshot(model);
        if (snapshot is null)
        {
            _logger.LogWarning(
                "Failed to deserialise ModelSnapshot for model {ModelId}", model.Id);
            return null;
        }

        // ── 4b. V2 feature-vector dispatch ─────────────────────────────────────
        // If the model was trained with the 37-feature V2 vector, extend the 33-feature
        // base vector by computing the 4 cross-pair macro features from a live basket
        // of H1 closes. Falls back to V1 silently if the basket can't be loaded, so
        // V2-tagged models keep scoring even on partial data (the 4 appended slots
        // go to 0.0, which is the neutral value the trainer also sees for NaN).
        int expectedFeatures = snapshot.ExpectedInputFeatures > 0
            ? snapshot.ExpectedInputFeatures
            : MLFeatureHelper.FeatureCount;

        if (expectedFeatures == MLFeatureHelper.FeatureCountV2 &&
            features.Length == MLFeatureHelper.FeatureCount)
        {
            try
            {
                using var basketScope = _scopeFactory.CreateScope();
                var readDb = basketScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var basketSymbols = new[] { "EURUSD", "GBPUSD", "USDJPY", "USDCHF", "USDCAD", "AUDUSD", "NZDUSD" };
                var asOf = currentCandle.Timestamp;
                var basketStart = asOf.AddDays(-7);

                var raw = await readDb.GetDbContext().Set<Candle>()
                    .AsNoTracking()
                    .Where(c => basketSymbols.Contains(c.Symbol)
                             && c.Timeframe == Timeframe.H1
                             && c.IsClosed
                             && !c.IsDeleted
                             && c.Timestamp >= basketStart
                             && c.Timestamp <= asOf)
                    .OrderBy(c => c.Timestamp)
                    .Select(c => new { c.Symbol, c.Timestamp, c.Close })
                    .ToListAsync(cancellationToken);

                var sliced = raw
                    .GroupBy(c => c.Symbol)
                    .ToDictionary(
                        g => g.Key,
                        g => g.OrderBy(x => x.Timestamp).Select(x => (double)x.Close).ToArray(),
                        StringComparer.OrdinalIgnoreCase);

                features = MLFeatureHelper.BuildFeatureVectorV2(
                    windowCandles, currentCandle, previousCandle,
                    sliced, strategy.Symbol, CotFeatureEntry.Zero);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "V2 feature extension failed for {Symbol} model {ModelId} — zero-padding",
                    strategy.Symbol, model.Id);
                var padded = new float[MLFeatureHelper.FeatureCountV2];
                Array.Copy(features, padded, features.Length);
                features = padded;
            }
        }

        // ── 4c. V3 dispatch: cross-asset + event features appended to V2 ───────
        if (expectedFeatures == MLFeatureHelper.FeatureCountV3 &&
            features.Length == MLFeatureHelper.FeatureCountV2)
        {
            var v3 = new float[MLFeatureHelper.FeatureCountV3];
            Array.Copy(features, v3, features.Length);
            try
            {
                var asOfDay = new DateTime(currentCandle.Timestamp.Year, currentCandle.Timestamp.Month, currentCandle.Timestamp.Day, 0, 0, 0, DateTimeKind.Utc);
                using var v3Scope = _scopeFactory.CreateScope();
                var crossProvider = v3Scope.ServiceProvider.GetRequiredService<
                    global::LascodiaTradingEngine.Application.Services.ML.CrossAssetFeatureProvider>();
                var eventProvider = v3Scope.ServiceProvider.GetRequiredService<
                    global::LascodiaTradingEngine.Application.Services.ML.EconomicEventFeatureProvider>();

                var crossAsset = await crossProvider.GetAsync(asOfDay, cancellationToken);
                var eventLookup = await eventProvider.LoadForSymbolAsync(
                    strategy.Symbol, asOfDay.AddDays(-1), asOfDay.AddDays(1), cancellationToken);
                var eventSnap = eventLookup.SnapshotAt(currentCandle.Timestamp);

                static float S(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : Math.Clamp(v, -5f, 5f);
                v3[MLFeatureHelper.FeatureCountV2 + 0] = S(crossAsset.DxyReturn5d);
                v3[MLFeatureHelper.FeatureCountV2 + 1] = S(crossAsset.Us10YYieldChange5d);
                v3[MLFeatureHelper.FeatureCountV2 + 2] = S(crossAsset.VixLevelNormalized);
                v3[MLFeatureHelper.FeatureCountV2 + 3] = S(eventSnap.HoursToNextHighNormalized);
                v3[MLFeatureHelper.FeatureCountV2 + 4] = S(eventSnap.HoursSinceLastHighNormalized);
                v3[MLFeatureHelper.FeatureCountV2 + 5] = S(eventSnap.HighMedPending6hNormalized);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "V3 cross-asset/event load failed for {Symbol} model {ModelId} — zero-padding",
                    strategy.Symbol, model.Id);
            }
            features = v3;
        }

        // ── 5. Resolve inference engine ────────────────────────────────────────
        var engine = ResolveInferenceEngine(snapshot);
        if (engine is null)
        {
            _logger.LogWarning(
                "No IModelInferenceEngine can handle model {ModelId} (type={Type})",
                model.Id, snapshot.Type);
            return null;
        }

        // ── 6. Run inference ───────────────────────────────────────────────────
        InferenceResult? result;
        try
        {
            result = engine.RunInference(
                features,
                expectedFeatures,
                snapshot,
                windowCandles,
                model.Id,
                McDropoutSamples,
                mcDropoutSeed: 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Inference failed for model {ModelId} on {Symbol}", model.Id, strategy.Symbol);
            return null;
        }

        if (result is null)
        {
            _logger.LogWarning(
                "Inference returned null for model {ModelId} ({Symbol}/{Timeframe}, engine={Engine}) — skipping",
                model.Id, strategy.Symbol, strategy.Timeframe, engine.GetType().Name);
            return null;
        }

        double rawProbability = result.Value.Probability;
        if (double.IsNaN(rawProbability) || double.IsInfinity(rawProbability))
        {
            _logger.LogWarning(
                "Inference returned {Value} for model {ModelId} — skipping",
                rawProbability, model.Id);
            return null;
        }

        // ── 7. Apply Platt calibration ─────────────────────────────────────────
        double calibratedP = ApplyPlattCalibration(rawProbability, snapshot);
        if (double.IsNaN(calibratedP) || double.IsInfinity(calibratedP))
        {
            _logger.LogWarning(
                "Platt calibration produced {Value} for model {ModelId} — skipping",
                calibratedP, model.Id);
            return null;
        }

        // ── 8. Determine direction and confidence ──────────────────────────────
        // Use the snapshot's stored decision threshold (which may diverge from 0.5
        // when training calibration tunes it to maximise F1/Youden-J on imbalanced
        // classes) so the evaluator's direction matches the downstream
        // MLSignalScorer's direction. Otherwise the two compute opposing sides on
        // the same model+tick and Tier 1 validation rejects the signal for "ML
        // disagreement" — the exact bug observed in signals 1-3 (evaluator=Sell,
        // scorer=Buy, threshold was calibrated below 0.4372).
        double decisionThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snapshot);
        var direction = calibratedP >= decisionThreshold ? TradeDirection.Buy : TradeDirection.Sell;
        double confidence = Math.Abs(calibratedP - decisionThreshold) * 2.0; // 0..1

        if (confidence < parameters.ConfidenceThreshold)
        {
            _logger.LogDebug(
                "CompositeML confidence {Confidence:F4} < threshold {Threshold:F4} for {Symbol}",
                confidence, parameters.ConfidenceThreshold, strategy.Symbol);
            return null;
        }

        // ── 9. Compute ATR for SL/TP ──────────────────────────────────────────
        decimal atr = IndicatorCalculator.WilderAtr(candles, lastIdx, parameters.AtrPeriod);
        if (atr <= 0m)
        {
            _logger.LogWarning(
                "ATR is zero for {Symbol} — degenerate price data, skipping", strategy.Symbol);
            return null;
        }

        // ── 9b. Macro / positioning context modulation ────────────────────────
        // Blend regime confidence, COT positioning deltas, recent economic surprise,
        // and tick-pressure gradient into a signed alignment score for the proposed
        // direction. The score multiplicatively adjusts the ML-derived confidence by
        // up to ±30 %. We re-check the threshold afterwards so a directionally aligned
        // macro context can rescue a marginal signal and an opposing one can reject
        // an otherwise-qualifying one.
        double macroAlignment = 0.0;
        try
        {
            using var macroScope = _scopeFactory.CreateScope();
            var macroProvider = macroScope.ServiceProvider.GetService<Services.ML.IMacroFeatureProvider>();
            if (macroProvider is not null)
            {
                var macroContext = await macroProvider.GetAsync(strategy.Symbol, DateTime.UtcNow, cancellationToken);
                macroAlignment = macroContext.AlignmentFor(direction == TradeDirection.Buy);
                double adjustedConfidence = confidence * (1.0 + 0.30 * macroAlignment);
                adjustedConfidence = Math.Clamp(adjustedConfidence, 0.0, 1.0);
                if (adjustedConfidence < parameters.ConfidenceThreshold)
                {
                    _logger.LogDebug(
                        "CompositeML {Symbol}: macro alignment {Align:+0.00;-0.00} pulled confidence {Raw:F3}\u2192{Adj:F3} below threshold {Thr:F3}",
                        strategy.Symbol, macroAlignment, confidence, adjustedConfidence, parameters.ConfidenceThreshold);
                    return null;
                }
                confidence = adjustedConfidence;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "CompositeML {Symbol}: macro context unavailable, proceeding with raw confidence",
                strategy.Symbol);
        }

        // ── 10. Build signal ───────────────────────────────────────────────────
        decimal entryPrice = direction == TradeDirection.Buy ? currentPrice.Ask : currentPrice.Bid;
        decimal stopDistance   = atr * (decimal)parameters.StopLossAtrMultiplier;
        decimal profitDistance = atr * (decimal)parameters.TakeProfitAtrMultiplier;

        decimal stopLoss, takeProfit;
        if (direction == TradeDirection.Buy)
        {
            stopLoss   = entryPrice - stopDistance;
            takeProfit = entryPrice + profitDistance;
        }
        else
        {
            stopLoss   = entryPrice + stopDistance;
            takeProfit = entryPrice - profitDistance;
        }

        var now = DateTime.UtcNow;
        var signal = new TradeSignal
        {
            StrategyId            = strategy.Id,
            Symbol                = strategy.Symbol,
            Direction             = direction,
            EntryPrice            = entryPrice,
            StopLoss              = stopLoss,
            TakeProfit            = takeProfit,
            SuggestedLotSize      = 0.01m, // minimum; risk checker will resize
            Confidence            = (decimal)confidence,
            MLPredictedDirection  = direction,
            MLConfidenceScore     = (decimal)calibratedP,
            MLModelId             = model.Id,
            Status                = TradeSignalStatus.Pending,
            GeneratedAt           = now,
            ExpiresAt             = now.AddMinutes(SignalExpiryMinutes)
        };

        _logger.LogInformation(
            "CompositeML signal: {Direction} {Symbol} @ {Entry:F5}, SL={SL:F5}, TP={TP:F5}, " +
            "confidence={Confidence:F4}, calibP={CalibP:F4}, model={ModelId}",
            direction, strategy.Symbol, entryPrice, stopLoss, takeProfit,
            confidence, calibratedP, model.Id);

        return signal;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Private helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<MLModel?> GetActiveModelAsync(
        string symbol, Timeframe timeframe, CancellationToken ct)
    {
        var cacheKey = $"{ModelCacheKeyPrefix}{symbol}:{timeframe}";
        if (_cache.TryGetValue<MLModel>(cacheKey, out var cached) && cached is not null)
            return cached;

        using var scope = _scopeFactory.CreateScope();
        var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var model = await readDb.GetDbContext().Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.Symbol    == symbol &&
                        m.Timeframe == timeframe &&
                        m.IsActive  && !m.IsDeleted &&
                        m.ModelBytes != null)
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync(ct);

        if (model is not null)
            _cache.Set(cacheKey, model, ModelCacheDuration);

        return model;
    }

    private ModelSnapshot? GetOrDeserializeSnapshot(MLModel model)
    {
        var cacheKey = $"{SnapshotCacheKeyPrefix}{model.Id}";
        if (_cache.TryGetValue<ModelSnapshot>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!, JsonOptions);
            if (snapshot is not null)
                _cache.Set(cacheKey, snapshot, SnapshotCacheDuration);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ModelSnapshot deserialisation failed for model {Id}", model.Id);
            return null;
        }
    }

    private IModelInferenceEngine? ResolveInferenceEngine(ModelSnapshot snapshot)
    {
        foreach (var engine in _inferenceEngines)
        {
            if (engine.CanHandle(snapshot))
                return engine;
        }
        return null;
    }

    private static double ApplyPlattCalibration(double rawP, ModelSnapshot snapshot)
    {
        double a = snapshot.PlattA;
        double b = snapshot.PlattB;

        // Identity calibration — no Platt parameters were fitted
        if (Math.Abs(a - 1.0) < 1e-9 && Math.Abs(b) < 1e-9)
            return rawP;

        // Clamp to avoid log(0) or log(∞)
        double p = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7);
        double logit = Math.Log(p / (1.0 - p));
        double calibrated = Sigmoid(a * logit + b);
        return calibrated;
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

    private static CompositeMLParameters ParseParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new CompositeMLParameters();

        try
        {
            return JsonSerializer.Deserialize<CompositeMLParameters>(json, JsonOptions)
                   ?? new CompositeMLParameters();
        }
        catch
        {
            return new CompositeMLParameters();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Parameter model
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed class CompositeMLParameters
    {
        public double ConfidenceThreshold    { get; set; } = DefaultConfidenceThreshold;
        public string ModelPreference        { get; set; } = "Ensemble";
        public double StopLossAtrMultiplier  { get; set; } = DefaultSlAtrMultiplier;
        public double TakeProfitAtrMultiplier { get; set; } = DefaultTpAtrMultiplier;
        public int    AtrPeriod              { get; set; } = DefaultAtrPeriod;
        public bool   CrossPairEnabled       { get; set; }
    }
}
