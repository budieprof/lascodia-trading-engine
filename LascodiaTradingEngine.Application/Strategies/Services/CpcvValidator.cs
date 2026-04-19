using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Services;

/// <summary>
/// Full Combinatorial Purged Cross-Validation (candle-level replay).
///
/// <para>
/// Replaces the earlier trade-resampling MVP. For each C(N, K) fold we re-run
/// the backtest engine on the fold's candle window — this is the correct
/// "retrain-per-fold" semantics for rule-based / parameter-driven strategies
/// (the strategy parameters are the "model"; re-evaluating them on a fresh
/// held-out candle window is equivalent to ML retraining). Sharpe is computed
/// from the per-fold trades rather than from a resampled slice of the global
/// trade history, so each fold's out-of-sample Sharpe is genuinely independent
/// and usable for DSR / PBO calculations.
/// </para>
///
/// <para><b>Procedure (López de Prado 2018, Ch.12)</b>:</para>
/// <list type="number">
/// <item><description>Pull closed candles for the strategy's Symbol/Timeframe over [fromDate, toDate].</description></item>
/// <item><description>Partition chronologically into N contiguous groups.</description></item>
/// <item><description>Enumerate C(N, K) test-group combinations. For each combination:
///   take the K test groups' candles plus a warmup prefix (bars before the earliest
///   test candle so indicators are primed); run the backtest engine on that slice
///   (with TCA profile if available); compute Sharpe from the resulting per-trade
///   PnL. An explicit embargo of <see cref="EmbargoBars"/> bars before each test
///   group is excluded from the warmup prefix to stop label leakage.</description></item>
/// <item><description>Aggregate the fold Sharpes into the distribution used for DSR / PBO /
///   percentile summaries returned to <c>PromotionGateValidator</c>.</description></item>
/// </list>
///
/// <para>
/// <b>Cost</b>: C(N=12, K=2) = 66 backtests per strategy. Promotion gate only calls
/// this on candidates that already passed the cheap pre-filters, so amortised cost
/// is acceptable. Embargo defaults to 5 bars (≈ one session on H1), enough to break
/// typical triple-barrier label horizons; widen via EngineConfig if labels span longer.
/// </para>
/// </summary>
public sealed class CpcvValidator : ICpcvValidator
{
    private readonly IReadApplicationDbContext _readCtx;
    private readonly IBacktestEngine _backtestEngine;
    private readonly IBacktestOptionsSnapshotBuilder _optionsSnapshotBuilder;
    private readonly ITcaCostModelProvider? _tcaProvider;
    private readonly IEnumerable<IMLModelTrainer>? _mlTrainers;
    private readonly IEnumerable<IModelInferenceEngine>? _inferenceEngines;
    private readonly ILogger<CpcvValidator>? _logger;

    private const int DefaultNGroups       = 12;
    private const int DefaultKTestGroups   = 2;
    private const int EmbargoBars          = 5;
    private const int WarmupBars           = 200;
    private const int MinCandlesForCpcv    = 500;
    private const int MinFoldTrades        = 3;
    private const decimal InitialBalance   = 10_000m;

    public CpcvValidator(
        IReadApplicationDbContext readCtx,
        IBacktestEngine backtestEngine,
        IBacktestOptionsSnapshotBuilder optionsSnapshotBuilder,
        ITcaCostModelProvider? tcaProvider = null,
        IEnumerable<IMLModelTrainer>? mlTrainers = null,
        IEnumerable<IModelInferenceEngine>? inferenceEngines = null,
        ILogger<CpcvValidator>? logger = null)
    {
        _readCtx                 = readCtx;
        _backtestEngine          = backtestEngine;
        _optionsSnapshotBuilder  = optionsSnapshotBuilder;
        _tcaProvider             = tcaProvider;
        _mlTrainers              = mlTrainers;
        _inferenceEngines        = inferenceEngines;
        _logger                  = logger;
    }

    public async Task<CpcvResult> ValidateAsync(
        long strategyId, DateTime fromDate, DateTime toDate, CancellationToken ct)
    {
        var db = _readCtx.GetDbContext();

        var strategy = await db.Set<Strategy>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == strategyId && !s.IsDeleted, ct);

        if (strategy is null)
            return EmptyResult(strategyId, fromDate, toDate);

        // Pull closed candles for the strategy's Symbol/Timeframe in the window.
        // Add a WarmupBars prefix by starting the DB read slightly earlier so the first
        // fold that includes group 0 still has history for indicator warmup.
        var candles = await db.Set<Candle>().AsNoTracking()
            .Where(c => c.Symbol == strategy.Symbol
                     && c.Timeframe == strategy.Timeframe
                     && c.IsClosed
                     && !c.IsDeleted
                     && c.Timestamp >= fromDate
                     && c.Timestamp <= toDate)
            .OrderBy(c => c.Timestamp)
            .ToListAsync(ct);

        if (candles.Count < MinCandlesForCpcv)
        {
            _logger?.LogInformation(
                "CPCV: strategy {Id} has only {N} candles in window (need ≥ {Min}) — skipping",
                strategyId, candles.Count, MinCandlesForCpcv);
            return EmptyResult(strategyId, fromDate, toDate);
        }

        int n = DefaultNGroups;
        int k = DefaultKTestGroups;
        if (candles.Count < n * 200) n = Math.Max(4, candles.Count / 200);
        if (k >= n) k = Math.Max(1, n / 3);

        // Compute group boundaries (inclusive start, exclusive end).
        int[] boundaries = ComputeGroupBoundaries(candles.Count, n);

        // Build a single BacktestOptions snapshot once (costs per-symbol spread / commission
        // etc.) and hydrate the TCA profile so per-fold Sharpes are live-fill-comparable.
        var optionsSnapshot = await _optionsSnapshotBuilder.BuildAsync(db, strategy.Symbol, ct);
        var baseOptions = optionsSnapshot.ToOptions();
        if (_tcaProvider is not null)
            baseOptions.TcaProfile = await _tcaProvider.GetAsync(strategy.Symbol, ct);

        // ── ML-strategy dispatch ────────────────────────────────────────────
        // For ML-based strategies (CompositeML), true retrain-per-fold means fitting
        // a fresh model on the fold's train slice and scoring the test slice with that
        // model — this is what López de Prado §12 advocates for ML Sharpe validation.
        // For rule-based strategies, the existing per-fold backtest replay path is
        // already the correct semantics (parameters ARE the model; re-running the
        // engine on held-out candles measures parameter generalisation).
        bool isMlStrategy =
            strategy.StrategyType == StrategyType.CompositeML
            && _mlTrainers is not null
            && _inferenceEngines is not null;
        if (isMlStrategy)
        {
            try
            {
                return await RunMlRetrainPerFoldAsync(
                    strategy, candles, boundaries, n, k, fromDate, toDate, ct);
            }
            catch (NotSupportedException) { /* fall through to backtest-replay path */ }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "CPCV: ML retrain-per-fold failed for strategy {Id} — falling back to backtest-replay",
                    strategy.Id);
            }
        }

        var distribution = new List<double>();
        foreach (var testGroupIdx in Combinations(n, k))
        {
            ct.ThrowIfCancellationRequested();

            // Contiguous test slice from min(testGroupIdx) → max(testGroupIdx)+1 so we
            // don't simulate gaps inside the evaluation window. Non-contiguous K picks
            // are handled by taking their bounding box (López de Prado §12.4.1).
            int firstGroup = testGroupIdx.Min();
            int lastGroup  = testGroupIdx.Max();
            int testStart  = boundaries[firstGroup];
            int testEnd    = boundaries[lastGroup + 1]; // exclusive

            // Warmup prefix: WarmupBars bars before testStart, but clipped by the embargo
            // so the last EmbargoBars bars preceding testStart are dropped (prevent label
            // leakage from triple-barrier horizons that straddle the boundary).
            int embargoStart = Math.Max(0, testStart - EmbargoBars);
            int warmupStart  = Math.Max(0, embargoStart - WarmupBars);
            if (embargoStart - warmupStart < 50) continue; // too little warmup → unreliable

            var foldCandles = new List<Candle>(testEnd - warmupStart);
            for (int i = warmupStart; i < embargoStart; i++) foldCandles.Add(candles[i]);
            for (int i = testStart; i < testEnd;     i++) foldCandles.Add(candles[i]);

            DateTime testFirstTs = candles[testStart].Timestamp;

            BacktestResult result;
            try
            {
                result = await _backtestEngine.RunAsync(
                    strategy, foldCandles, InitialBalance, ct, baseOptions);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "CPCV: backtest failed for strategy {Id} fold [{First},{Last}] — skipping fold",
                    strategyId, firstGroup, lastGroup);
                continue;
            }

            // Keep only trades entered in the held-out test window; warmup trades are
            // ignored so the Sharpe reflects genuine OOS performance.
            var oosPnls = result.Trades
                .Where(t => t.EntryTime >= testFirstTs)
                .Select(t => (double)t.PnL)
                .ToArray();

            if (oosPnls.Length < MinFoldTrades) continue;
            distribution.Add(ComputeSharpe(oosPnls));
        }

        if (distribution.Count == 0)
            return EmptyResult(strategyId, fromDate, toDate, n, k);

        distribution.Sort();
        double median = Percentile(distribution, 0.50);
        double p25    = Percentile(distribution, 0.25);
        double p75    = Percentile(distribution, 0.75);

        // Total trade count proxy for DSR trades-denominator — sum over folds would
        // double-count. Use the median fold size × number of folds as a conservative proxy.
        int tradesProxy = Math.Max(distribution.Count * MinFoldTrades, distribution.Count);
        double dsr = PromotionGateValidator.ComputeDeflatedSharpe(
            rawSharpe: median, trials: distribution.Count, trades: tradesProxy);

        int belowMedian = distribution.Count(s => s < median);
        double pbo = distribution.Count > 0 ? (double)belowMedian / distribution.Count : 1.0;

        return new CpcvResult(
            StrategyId: strategyId,
            FromDate: fromDate, ToDate: toDate,
            NGroups: n, KTestGroups: k,
            SharpeDistribution: distribution,
            MedianSharpe: median, P25Sharpe: p25, P75Sharpe: p75,
            DeflatedSharpe: dsr, ProbabilityOfOverfitting: pbo);
    }

    /// <summary>
    /// True retrain-per-fold for ML strategies: for each C(N, K) fold we train a fresh
    /// model on the train slice (with embargo purge around each test group), run inference
    /// on the test slice, and compute Sharpe from the sign(probability − 0.5) × realised
    /// triple-barrier PnL stream. The base vector is V1 (33 features) for portability —
    /// the aim is to measure model-level signal quality, not match the live pipeline's
    /// exact feature vector. Upstream promotion-gate metrics that need V3/V4 parity can
    /// consume the existing backtest-replay path.
    /// </summary>
    private async Task<CpcvResult> RunMlRetrainPerFoldAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        int[] boundaries,
        int n,
        int k,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken ct)
    {
        var trainer = _mlTrainers!.FirstOrDefault();
        if (trainer is null)
            throw new NotSupportedException("No IMLModelTrainer registered; cannot retrain per fold.");

        // Build all triple-barrier training samples over the full window once. Slicing
        // the sample list by index gives us train/test splits aligned to the candle
        // group boundaries without re-generating features per fold.
        var allCandles = candles.ToList();
        var samples = MLFeatureHelper.BuildTrainingSamplesWithTripleBarrier(
            allCandles,
            cotLookup: null,
            profitAtrMult: 1.5f,
            stopAtrMult:   1.5f,
            horizonBars:   24,
            costBufferPriceUnits: 0f);

        // Samples start at index `LookbackWindow` (V1 default 120) — map sample-space
        // indices back to candle-space indices so test-group boundaries align.
        int sampleOffset = MLFeatureHelper.LookbackWindow;
        if (samples.Count < 200)
        {
            _logger?.LogInformation(
                "CPCV ML-retrain: strategy {Id} has only {N} triple-barrier samples — falling back to backtest-replay",
                strategy.Id, samples.Count);
            throw new NotSupportedException("Insufficient ML training samples for retrain-per-fold.");
        }

        var hp = BuildRetrainHyperparams();

        var distribution = new List<double>();
        foreach (var testGroupIdx in Combinations(n, k))
        {
            ct.ThrowIfCancellationRequested();
            int firstGroup = testGroupIdx.Min();
            int lastGroup  = testGroupIdx.Max();

            // Test slice (in candle-space) → filter to samples whose candle-index falls in it.
            int testStartCandle = boundaries[firstGroup];
            int testEndCandle   = boundaries[lastGroup + 1]; // exclusive

            // Embargo applies around test in BOTH directions for train/test isolation.
            int embargoBeforeCandle = Math.Max(0, testStartCandle - EmbargoBars);
            int embargoAfterCandle  = Math.Min(allCandles.Count, testEndCandle + EmbargoBars);

            var trainSamples = new List<TrainingSample>(samples.Count);
            var testSamples  = new List<TrainingSample>();
            for (int s = 0; s < samples.Count; s++)
            {
                int candleIdx = s + sampleOffset;
                bool inTest   = candleIdx >= testStartCandle && candleIdx < testEndCandle;
                bool inEmbargo = !inTest
                             && candleIdx >= embargoBeforeCandle
                             && candleIdx <  embargoAfterCandle;
                if (inTest) testSamples.Add(samples[s]);
                else if (!inEmbargo) trainSamples.Add(samples[s]);
            }

            if (trainSamples.Count < 200 || testSamples.Count < MinFoldTrades) continue;

            TrainingResult? trainResult;
            try
            {
                trainResult = await trainer.TrainAsync(trainSamples, hp, warmStart: null, parentModelId: null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "CPCV ML-retrain fold [{First},{Last}] trainer failed — skipping fold", firstGroup, lastGroup);
                continue;
            }

            if (trainResult is null || trainResult.ModelBytes is null || trainResult.ModelBytes.Length == 0)
                continue;

            ModelSnapshot? snap;
            try
            {
                snap = System.Text.Json.JsonSerializer.Deserialize<ModelSnapshot>(trainResult.ModelBytes);
            }
            catch { continue; }
            if (snap is null) continue;

            var engine = _inferenceEngines!.FirstOrDefault(e => e.CanHandle(snap));
            if (engine is null) continue;

            // Score each test sample; emit pnl = sign(prediction - 0.5) × magnitude
            // (from the triple-barrier label). Signed PnL → Sharpe captures whether
            // the model's directional conviction aligned with realised label PnL.
            var foldPnls = new List<double>(testSamples.Count);
            foreach (var sample in testSamples)
            {
                var result = engine.RunInference(
                    sample.Features, sample.Features.Length,
                    snap, candleWindow: new List<Candle>(),
                    modelId: strategy.Id, mcDropoutSamples: 0, mcDropoutSeed: 0);
                if (result is null) continue;
                double direction = result.Value.Probability > 0.5 ? 1.0 : -1.0;
                // label=1 (profit) → magnitude > 0; label=0 (stop) → magnitude < 0 (encoded in builder)
                double signedPnl = direction * sample.Magnitude;
                foldPnls.Add(signedPnl);
            }

            if (foldPnls.Count < MinFoldTrades) continue;
            distribution.Add(ComputeSharpe(foldPnls.ToArray()));
        }

        if (distribution.Count == 0)
            return EmptyResult(strategy.Id, fromDate, toDate, n, k);

        distribution.Sort();
        double median = Percentile(distribution, 0.50);
        double p25    = Percentile(distribution, 0.25);
        double p75    = Percentile(distribution, 0.75);
        int tradesProxy = Math.Max(distribution.Count * MinFoldTrades, distribution.Count);
        double dsr = PromotionGateValidator.ComputeDeflatedSharpe(
            rawSharpe: median, trials: distribution.Count, trades: tradesProxy);
        int belowMedian = distribution.Count(s => s < median);
        double pbo = distribution.Count > 0 ? (double)belowMedian / distribution.Count : 1.0;

        _logger?.LogInformation(
            "CPCV ML-retrain: strategy {Id} completed with {Folds} folds, median Sharpe {Median:F2}",
            strategy.Id, distribution.Count, median);

        return new CpcvResult(
            StrategyId: strategy.Id,
            FromDate: fromDate, ToDate: toDate,
            NGroups: n, KTestGroups: k,
            SharpeDistribution: distribution,
            MedianSharpe: median, P25Sharpe: p25, P75Sharpe: p75,
            DeflatedSharpe: dsr, ProbabilityOfOverfitting: pbo);
    }

    private static TrainingHyperparams BuildRetrainHyperparams()
    {
        // Minimal hyperparams for the retrain path — trainers generally fall back to their
        // own internal defaults when fields are at neutral values. Keep symmetric triple-
        // barrier (ratio 1.0) so the SymmetricTripleBarrier guard in MLTrainingWorker is
        // never tripped if a CPCV fold is misused as a training run by mistake.
        return new TrainingHyperparams(
            K: 3, LearningRate: 0.01, L2Lambda: 0.001, MaxEpochs: 20,
            EarlyStoppingPatience: 3, MinAccuracyToPromote: 0.50, MinExpectedValue: -1.0,
            MaxBrierScore: 1.0, MinSharpeRatio: -10.0, MinSamples: 100,
            ShadowRequiredTrades: 0, ShadowExpiryDays: 0, WalkForwardFolds: 2,
            EmbargoBarCount: 5, TrainingTimeoutMinutes: 5, TemporalDecayLambda: 1.0,
            DriftWindowDays: 14, DriftMinPredictions: 30, DriftAccuracyThreshold: 0.45,
            MaxWalkForwardStdDev: 1.0, LabelSmoothing: 0.0, MinFeatureImportance: 0.0,
            EnableRegimeSpecificModels: false, FeatureSampleRatio: 1.0, MaxEce: 1.0,
            UseTripleBarrier: true, TripleBarrierProfitAtrMult: 1.5,
            TripleBarrierStopAtrMult: 1.5, TripleBarrierHorizonBars: 24,
            NoiseSigma: 0, FpCostWeight: 1.0, NclLambda: 0, FracDiffD: 0,
            MaxFoldDrawdown: 1.0, MinFoldCurveSharpe: -999, PolyLearnerFraction: 1.0,
            PurgeHorizonBars: 0, NoiseCorrectionThreshold: 0.4, MaxLearnerCorrelation: 1.0,
            SwaStartEpoch: 0, SwaFrequency: 1, MixupAlpha: 0.0,
            EnableGreedyEnsembleSelection: false, MaxGradNorm: 0.0,
            AtrLabelSensitivity: 0.0, ShadowMinZScore: 0.0,
            L1Lambda: 0.0, MagnitudeQuantileTau: 0.0, MagLossWeight: 0.0,
            DensityRatioWindowDays: 0, BarsPerDay: 24,
            DurbinWatsonThreshold: 0.0, AdaptiveLrDecayFactor: 0.0,
            OobPruningEnabled: false, MutualInfoRedundancyThreshold: 0.0,
            MinSharpeTrendSlope: -99.0, FitTemperatureScale: false,
            MinBrierSkillScore: -1.0, RecalibrationDecayLambda: 0.0,
            MaxEnsembleDiversity: 1.0, UseSymmetricCE: false,
            SymmetricCeAlpha: 0.0, DiversityLambda: 0.0,
            UseAdaptiveLabelSmoothing: false, AgeDecayLambda: 0.0,
            UseCovariateShiftWeights: false, MaxBadFoldFraction: 1.0,
            MinQualityRetentionRatio: 0.0, MultiTaskMagnitudeWeight: 0.0,
            CurriculumEasyFraction: 0.3, SelfDistillTemp: 3.0,
            FgsmEpsilon: 0.0, MinF1Score: 0.0, UseClassWeights: false);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CpcvResult EmptyResult(long strategyId, DateTime from, DateTime to,
                                          int n = 0, int k = 0) => new(
        StrategyId: strategyId,
        FromDate: from, ToDate: to,
        NGroups: n, KTestGroups: k,
        SharpeDistribution: Array.Empty<double>(),
        MedianSharpe: 0, P25Sharpe: 0, P75Sharpe: 0,
        DeflatedSharpe: 0, ProbabilityOfOverfitting: 1.0);

    private static int[] ComputeGroupBoundaries(int totalCount, int n)
    {
        var b = new int[n + 1];
        int perGroup = totalCount / n;
        int remainder = totalCount % n;
        int idx = 0;
        for (int g = 0; g < n; g++)
        {
            b[g] = idx;
            idx += perGroup + (g < remainder ? 1 : 0);
        }
        b[n] = totalCount;
        return b;
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var indices = new int[k];
        for (int i = 0; i < k; i++) indices[i] = i;
        while (true)
        {
            yield return (int[])indices.Clone();
            int i2 = k - 1;
            while (i2 >= 0 && indices[i2] == n - k + i2) i2--;
            if (i2 < 0) yield break;
            indices[i2]++;
            for (int j = i2 + 1; j < k; j++) indices[j] = indices[j - 1] + 1;
        }
    }

    private static double ComputeSharpe(double[] pnls)
    {
        if (pnls.Length < 2) return 0.0;
        double mean = pnls.Average();
        double variance = pnls.Sum(p => (p - mean) * (p - mean)) / (pnls.Length - 1);
        double stdev = Math.Sqrt(variance);
        return stdev > 1e-12 ? mean / stdev * Math.Sqrt(pnls.Length) : 0.0;
    }

    private static double Percentile(IReadOnlyList<double> sortedAsc, double pct)
    {
        if (sortedAsc.Count == 0) return 0.0;
        double pos = pct * (sortedAsc.Count - 1);
        int low = (int)Math.Floor(pos);
        int high = (int)Math.Ceiling(pos);
        if (low == high) return sortedAsc[low];
        double frac = pos - low;
        return sortedAsc[low] * (1 - frac) + sortedAsc[high] * frac;
    }
}
