using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class ElmModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Bagged ELM ensemble fitting
    // ═══════════════════════════════════════════════════════════════════════════

    private (double[][] Weights, double[] Biases,
             double[][] InputWeights, double[][] InputBiases,
             int[][]? FeatureSubsets, int[] LearnerHiddenSizes,
             ElmActivation[] LearnerActivations,
             double[][] InverseGramsFlat, int[] InverseGramDims) FitBaggedElm(
        List<TrainingSample> train,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  hiddenSize,
        int                  K,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        CancellationToken    ct,
        bool[]?              activeFeatureMask = null,
        int                  maxInnerParallelism = 0)
    {
        var weights      = new double[K][];
        var biases       = new double[K];
        var inputWeights = new double[K][];
        var inputBiases  = new double[K][];
        var cgDidNotConverge = new bool[K];
        var learnerHiddenSizes = new int[K];
        var learnerActivations = new ElmActivation[K];
        var inverseGramsFlat = new double[K][];
        var inverseGramDims  = new int[K];

        bool useSubsampling = hp.FeatureSampleRatio > 0.0 && hp.FeatureSampleRatio < 1.0;
        var featureSubsets   = useSubsampling ? new int[K][] : null;

        int outerSeed = hp.ElmOuterSeed;

        var temporalWeights = ElmBootstrapHelper.ComputeTemporalWeights(train.Count, hp.TemporalDecayLambda);

        if (densityWeights is { Length: > 0 } && densityWeights.Length == temporalWeights.Length)
        {
            var blended = new double[temporalWeights.Length];
            double sum = 0.0;
            for (int i = 0; i < temporalWeights.Length; i++)
            {
                blended[i] = temporalWeights[i] * densityWeights[i];
                sum += blended[i];
            }
            if (sum > 1e-15)
                for (int i = 0; i < blended.Length; i++) blended[i] /= sum;
            temporalWeights = blended;
        }

        // ── Effective sample size (ESS) monitoring ──
        // ESS = (Σwi)² / Σ(wi²) — measures how many samples effectively contribute
        // after importance weighting. Low ESS / N ratio means a few samples dominate.
        {
            double wSum = 0.0, wSumSq = 0.0;
            for (int i = 0; i < temporalWeights.Length; i++)
            {
                wSum   += temporalWeights[i];
                wSumSq += temporalWeights[i] * temporalWeights[i];
            }
            double ess = wSumSq > 1e-30 ? (wSum * wSum) / wSumSq : train.Count;
            double essRatio = train.Count > 0 ? ess / train.Count : 1.0;
            if (essRatio < 0.5)
                _logger.LogWarning(
                    "ELM bootstrap ESS={Ess:F0}/{N} ({Ratio:P0}). " +
                    "Importance weights are highly concentrated — effective training diversity is reduced. " +
                    "Consider reducing TemporalDecayLambda or DensityRatioWindowDays.",
                    ess, train.Count, essRatio);
            else
                _logger.LogDebug("ELM bootstrap ESS={Ess:F0}/{N} ({Ratio:P0})", ess, train.Count, essRatio);
        }

        // ── Class imbalance handling: class weights (preferred) or SMOTE (legacy) ──
        bool useClassWeights = hp.ElmUseClassWeights;
        double classWeightBuy = 1.0, classWeightSell = 1.0;
        bool smoteEnabled = false;
        List<TrainingSample>? smoteMinoritySamples = null;
        int smoteSyntheticNeeded = 0;
        int smoteKNeighbors = 5;

        {
            int buyCount = 0, sellCount = 0;
            foreach (var s in train)
            {
                if (s.Direction > 0) buyCount++;
                else sellCount++;
            }

            if (useClassWeights && buyCount > 0 && sellCount > 0)
            {
                // Inverse-frequency class weights: minority class gets higher weight
                double total = buyCount + sellCount;
                classWeightBuy = total / (2.0 * buyCount);
                classWeightSell = total / (2.0 * sellCount);
                _logger.LogDebug(
                    "ELM class weights: buy={Buy:F3} sell={Sell:F3} (buy={BuyN} sell={SellN})",
                    classWeightBuy, classWeightSell, buyCount, sellCount);
            }
            else if (hp.ElmUseSmote && !useClassWeights)
            {
                // Legacy SMOTE path — only used when ElmUseClassWeights is explicitly disabled
                int majCount = Math.Max(buyCount, sellCount);
                int minCount = Math.Min(buyCount, sellCount);
                double minRatio = majCount > 0 ? (double)minCount / majCount : 1.0;

                smoteKNeighbors = hp.SmoteKNeighbors is > 0 ? hp.SmoteKNeighbors.Value : 5;
                int minSmoteFloor = smoteKNeighbors + 1;
                double extremeImbalanceFloor = 0.05;
                if (minCount >= minSmoteFloor && minRatio >= extremeImbalanceFloor && minRatio < hp.ElmSmoteMinorityRatioThreshold)
                {
                    smoteEnabled = true;
                    var buyList = new List<TrainingSample>();
                    var sellList = new List<TrainingSample>();
                    foreach (var s in train)
                    {
                        if (s.Direction > 0) buyList.Add(s);
                        else sellList.Add(s);
                    }
                    smoteMinoritySamples = buyList.Count < sellList.Count ? buyList : sellList;
                    smoteSyntheticNeeded = majCount - minCount;
                }
                else if (minCount < minSmoteFloor || minRatio < extremeImbalanceFloor)
                {
                    _logger.LogWarning(
                        "ELM SMOTE skipped: minority count {MinCount} or ratio {Ratio:P1} too low for reliable interpolation (need ≥{Floor} samples and ≥{FloorRatio:P0} ratio).",
                        minCount, minRatio, minSmoteFloor, extremeImbalanceFloor);
                }
            }
        }

        double ridgeLambda = Math.Max(1e-6, hp.L2Lambda > 0 ? hp.L2Lambda : 1e-3);
        double maxWeightMag = hp.MaxWeightMagnitude > 0 ? hp.MaxWeightMagnitude : 10.0;
        double dropRate = Math.Clamp(hp.ElmDropoutRate, 0.0, 0.5);
        double dropScale = dropRate > 0.0 ? 1.0 / (1.0 - dropRate) : 1.0;
        double smoteSampleWeight = Math.Clamp(hp.ElmSmoteSampleWeight, 0.01, 1.0);
        ElmActivation activation = hp.ElmActivation;

        bool useBiasedFeatureSampling =
            warmStart is not null &&
            warmStart.FeatureImportanceScores.Length == featureCount &&
            hp.FeatureSampleRatio > 0.0;

        int effectiveParallelism = maxInnerParallelism > 0
            ? maxInnerParallelism
            : Math.Max(1, Environment.ProcessorCount);
        Parallel.For(0, K, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = effectiveParallelism
        }, k =>
        {
            ct.ThrowIfCancellationRequested();

            // ── Per-learner hidden size variation ──────────────────────────
            int learnerHidden = hiddenSize;
            if (hp.ElmHiddenSizeVariation > 0.0)
            {
                var hiddenRng = new Random(ElmMathHelper.HashSeed(outerSeed, k, 99));
                double variation = hp.ElmHiddenSizeVariation;
                double factor = 1.0 + (hiddenRng.NextDouble() * 2.0 - 1.0) * variation;
                learnerHidden = Math.Max(8, (int)(hiddenSize * factor));
            }
            // Safe in Parallel.For: each iteration writes to its own index k — no contention.
            learnerHiddenSizes[k] = learnerHidden;

            // ── Per-learner activation (mixed activation ensemble) ─────────
            ElmActivation learnerAct;
            if (hp.ElmMixActivations)
            {
                var availableActivations = new[] { ElmActivation.Sigmoid, ElmActivation.Tanh, ElmActivation.Relu };
                learnerAct = availableActivations[k % availableActivations.Length];
            }
            else
            {
                learnerAct = activation;
            }
            learnerActivations[k] = learnerAct;

            int learnerSeed = ElmMathHelper.HashSeed(outerSeed, k, 42);
            int featureSeed = ElmMathHelper.HashSeed(outerSeed, k, 13);
            var rng = new Random(learnerSeed);

            // ── Feature subset ──────────────────────────────────────────────
            int[] eligibleIndices = activeFeatureMask is not null
                ? Enumerable.Range(0, featureCount).Where(i => activeFeatureMask[i]).ToArray()
                : Enumerable.Range(0, featureCount).ToArray();

            int[] subset;
            if (useSubsampling)
            {
                if (activeFeatureMask is not null)
                {
                    subset = useBiasedFeatureSampling
                        ? ElmBootstrapHelper.GenerateBiasedFeatureSubsetFromPool(eligibleIndices, hp.FeatureSampleRatio,
                            warmStart!.FeatureImportanceScores, seed: featureSeed)
                        : ElmBootstrapHelper.GenerateFeatureSubsetFromPool(eligibleIndices, hp.FeatureSampleRatio, seed: featureSeed);
                }
                else
                {
                    subset = useBiasedFeatureSampling
                        ? ElmBootstrapHelper.GenerateBiasedFeatureSubset(featureCount, hp.FeatureSampleRatio,
                            warmStart!.FeatureImportanceScores, seed: featureSeed)
                        : ElmBootstrapHelper.GenerateFeatureSubset(featureCount, hp.FeatureSampleRatio, seed: featureSeed);
                }
            }
            else
            {
                subset = eligibleIndices;
            }
            if (featureSubsets is not null) featureSubsets[k] = subset;

            int subsetLen = subset.Length;

            // ── Xavier-init random input weights ───
            double scale = Math.Sqrt(2.0 / (subsetLen + learnerHidden));
            double[] wIn = new double[learnerHidden * subsetLen];
            double[] bIn = new double[learnerHidden];

            if (warmStart is not null &&
                WarmStartMatchesLearnerSubset(warmStart, k, subset, featureCount, learnerHidden * subsetLen))
            {
                var warmInputWeights = warmStart.ElmInputWeights![k];
                var warmInputBiases = warmStart.ElmInputBiases is not null && k < warmStart.ElmInputBiases.Length
                    ? warmStart.ElmInputBiases[k]
                    : null;
                bool warmStartFinite = HasFiniteValues(warmInputWeights) &&
                    (warmInputBiases is null || HasFiniteValues(warmInputBiases));
                if (warmStartFinite)
                {
                    Array.Copy(warmInputWeights, wIn, wIn.Length);
                    if (warmInputBiases is not null)
                        Array.Copy(warmInputBiases, bIn, Math.Min(warmInputBiases.Length, bIn.Length));
                }
                else
                {
                    _logger.LogWarning(
                        "ELM learner {K}: warm-start input weights/biases contained non-finite values. Falling back to random init.",
                        k);
                    for (int i = 0; i < wIn.Length; i++) wIn[i] = ElmMathHelper.SampleGaussian(rng) * scale;
                    for (int h = 0; h < learnerHidden; h++) bIn[h] = ElmMathHelper.SampleGaussian(rng) * scale;
                }
            }
            else
            {
                if (warmStart?.ElmInputWeights is not null && k < warmStart.ElmInputWeights.Length)
                    _logger.LogWarning(
                        "ELM learner {K}: warm-start mismatch for subset/dimension (expected length {Expected}, got {Actual}). Falling back to random init.",
                        k, learnerHidden * subsetLen, warmStart.ElmInputWeights[k]?.Length ?? 0);

                for (int i = 0; i < wIn.Length; i++) wIn[i] = ElmMathHelper.SampleGaussian(rng) * scale;
                for (int h = 0; h < learnerHidden; h++) bIn[h] = ElmMathHelper.SampleGaussian(rng) * scale;
            }

            inputWeights[k] = wIn;
            inputBiases[k]  = bIn;

            // ── Stratified biased bootstrap ──
            int bootstrapSeed = ElmMathHelper.HashSeed(outerSeed, k, 7);
            var bootstrap = ElmBootstrapHelper.StratifiedBiasedBootstrap(train, temporalWeights, train.Count, seed: bootstrapSeed);

            // ── Per-learner SMOTE with sample weighting ──
            // Track which samples are synthetic to apply differential weighting
            bool[]? isSynthetic = null;
            if (smoteEnabled && smoteMinoritySamples is not null)
            {
                int smoteSeed = ElmMathHelper.HashSeed(outerSeed, k, 9999);
                var syntheticPairs = ElmBootstrapHelper.GenerateSmoteSamples(smoteMinoritySamples, smoteSyntheticNeeded, smoteKNeighbors, smoteSeed);
                int originalCount = bootstrap.Count;
                isSynthetic = new bool[originalCount + syntheticPairs.Count];
                foreach (var (sample, _) in syntheticPairs)
                    bootstrap.Add(sample);
                for (int i = originalCount; i < bootstrap.Count; i++) isSynthetic[i] = true;
            }
            int N = bootstrap.Count;

            // ── Compute H^TH and H^TY ──
            double posLabel = 1.0 - labelSmoothing;
            double negLabel = labelSmoothing;

            int solveSize = learnerHidden + 1;
            double[,] HtH = new double[solveSize, solveSize];
            double[] HtY  = new double[solveSize];

            var dropMask = new bool[learnerHidden];
            double[] hRow = new double[solveSize];

            for (int t = 0; t < N; t++)
            {
                // Per-sample dropout mask
                if (dropRate > 0.0)
                    for (int h = 0; h < learnerHidden; h++)
                        dropMask[h] = rng.NextDouble() >= dropRate;

                var features = bootstrap[t].Features;
                for (int h = 0; h < learnerHidden; h++)
                {
                    if (dropRate > 0.0 && !dropMask[h]) { hRow[h] = 0.0; continue; }

                    double z = bIn[h];
                    int rowOff = h * subsetLen;
                    // SIMD-accelerated dot product
                    z += ElmMathHelper.DotProductSimd(wIn, rowOff, features, subset, subsetLen);
                    double act = ElmMathHelper.Activate(z, learnerAct);
                    hRow[h] = (double.IsFinite(act) ? act : 0.5) * (dropRate > 0.0 ? dropScale : 1.0);
                }
                hRow[learnerHidden] = 1.0;
                double yt = bootstrap[t].Direction > 0 ? posLabel : negLabel;

                // Apply sample weighting: class weights (inverse-frequency) and/or SMOTE down-weighting
                double sampleW = 1.0;
                if (useClassWeights)
                    sampleW *= bootstrap[t].Direction > 0 ? classWeightBuy : classWeightSell;
                if (isSynthetic is not null && t < isSynthetic.Length && isSynthetic[t])
                    sampleW *= smoteSampleWeight;

                for (int i = 0; i < solveSize; i++)
                {
                    HtY[i] += hRow[i] * yt * sampleW;
                    for (int j = i; j < solveSize; j++)
                        HtH[i, j] += hRow[i] * hRow[j] * sampleW;
                }
            }

            // ── Degenerate activation detection ──
            {
                int saturatedUnits = 0;
                for (int h = 0; h < learnerHidden; h++)
                {
                    double diagH = HtH[h, h];
                    double saturationThreshold = 1e-6 * N;
                    if (diagH < saturationThreshold || diagH > N - saturationThreshold)
                        saturatedUnits++;
                }
                if (saturatedUnits > learnerHidden / 2)
                    _logger.LogWarning(
                        "ELM learner {K}: {Sat}/{H} hidden units are saturated. " +
                        "Consider reducing input weight scale, increasing ridge lambda, or trying a different activation.",
                        k, saturatedUnits, learnerHidden);
            }

            // Symmetric fill + ridge
            for (int i = 0; i < solveSize; i++)
            {
                if (i < learnerHidden) HtH[i, i] += ridgeLambda;
                for (int j = i + 1; j < solveSize; j++)
                    HtH[j, i] = HtH[i, j];
            }

            var inverseGram = new double[solveSize, solveSize];
            if (ElmMathHelper.TryInvertSpd(HtH, inverseGram, solveSize))
            {
                var flat = new double[solveSize * solveSize];
                for (int i = 0; i < solveSize; i++)
                    for (int j = 0; j < solveSize; j++)
                        flat[i * solveSize + j] = inverseGram[i, j];
                inverseGramsFlat[k] = flat;
                inverseGramDims[k]  = solveSize;
            }
            else
            {
                _logger.LogWarning(
                    "ELM learner {K}: failed to invert hidden Gram matrix; online updates disabled for this learner",
                    k);
            }

            // ── Cholesky solve ──
            double[] wSolve = new double[solveSize];
            bool choleskyOk = ElmMathHelper.CholeskySolve(HtH, HtY, wSolve, solveSize);

            if (!choleskyOk)
                cgDidNotConverge[k] = true;

            double[] wOut = new double[learnerHidden];
            Array.Copy(wSolve, wOut, learnerHidden);
            double outBias = wSolve[learnerHidden];

            bool solveIsFinite = double.IsFinite(outBias);
            if (solveIsFinite)
                for (int i = 0; i < learnerHidden; i++)
                    if (!double.IsFinite(wSolve[i])) { solveIsFinite = false; break; }

            if (!solveIsFinite)
            {
                Array.Clear(wOut, 0, learnerHidden);
                outBias = 0.0;
            }
            else
            {
                for (int i = 0; i < learnerHidden; i++)
                    wOut[i] = Math.Clamp(wOut[i], -maxWeightMag, maxWeightMag);
            }

            weights[k] = wOut;
            biases[k]  = Math.Clamp(outBias, -maxWeightMag, maxWeightMag);
        });

        int cgFailCount = cgDidNotConverge.Count(f => f);
        if (cgFailCount > 0)
            _logger.LogWarning(
                "ELM Cholesky solver failed for {N}/{K} learners — consider increasing ridge lambda.",
                cgFailCount, K);

        return (weights, biases, inputWeights, inputBiases, featureSubsets, learnerHiddenSizes, learnerActivations, inverseGramsFlat, inverseGramDims);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ELM inference helpers (with SIMD + configurable activation)
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ElmLearnerProb(
        float[] features, double[] wOut, double bias,
        double[] wIn, double[] bIn,
        int featureCount, int hiddenSize, int[]? subset,
        ElmActivation activation)
    {
        int subsetLen = subset?.Length ?? featureCount;
        double score = bias;
        for (int h = 0; h < hiddenSize; h++)
        {
            double z = h < bIn.Length ? bIn[h] : 0.0;
            int rowOff = h * subsetLen;
            if (subset is not null)
                z += ElmMathHelper.DotProductSimd(wIn, rowOff, features, subset, subsetLen);
            else
            {
                int len = Math.Min(subsetLen, features.Length);
                z += ElmMathHelper.DotProductSimdContiguous(wIn, rowOff, features, len);
            }
            double hAct = ElmMathHelper.Activate(z, activation);
            if (h < wOut.Length) score += wOut[h] * hAct;
        }
        return MLFeatureHelper.Sigmoid(score);
    }

    private static double EnsembleRawProb(
        float[] features, double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        double[]? learnerWeights,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        double[]? stackingWeights = null, double stackingBias = 0.0)
    {
        int K = Math.Min(
            weights.Length,
            Math.Min(biases.Length, Math.Min(inputWeights.Length, inputBiases.Length)));
        if (K <= 0)
            return 0.5;

        // Stacking meta-learner: logistic regression over per-learner probabilities.
        // When active, this replaces both uniform and accuracy-weighted averaging,
        // learning optimal per-learner combination weights.
        if (stackingWeights is not null && stackingWeights.Length == K)
        {
            double z = stackingBias;
            for (int k = 0; k < K; k++)
            {
                if (weights[k] is not { Length: > 0 } ||
                    inputWeights[k] is not { Length: > 0 } ||
                    inputBiases[k] is null)
                {
                    continue;
                }

                double pk = ClampProbabilityOrNeutral(ElmLearnerProb(
                    features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                    featureCount,
                    ResolveLearnerHiddenSize(learnerHiddenSizes, k, hiddenSize, inputBiases[k]),
                    ResolveLearnerSubset(featureSubsets, k),
                    ResolveLearnerActivation(learnerActivations, k)));
                double stackingWeight = double.IsFinite(stackingWeights[k]) ? stackingWeights[k] : 0.0;
                z += stackingWeight * pk;
            }
            return ClampProbabilityOrNeutral(MLFeatureHelper.Sigmoid(z));
        }

        if (learnerWeights is not null && learnerWeights.Length == K)
        {
            double sum = 0;
            double sumWeights = 0.0;
            for (int k = 0; k < K; k++)
            {
                double learnerWeight = double.IsFinite(learnerWeights[k]) && learnerWeights[k] > 0.0
                    ? learnerWeights[k]
                    : 0.0;
                if (learnerWeight <= 0.0)
                    continue;
                if (weights[k] is not { Length: > 0 } ||
                    inputWeights[k] is not { Length: > 0 } ||
                    inputBiases[k] is null)
                {
                    continue;
                }

                sum += learnerWeight * ClampProbabilityOrNeutral(ElmLearnerProb(
                    features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                    featureCount,
                    ResolveLearnerHiddenSize(learnerHiddenSizes, k, hiddenSize, inputBiases[k]),
                    ResolveLearnerSubset(featureSubsets, k),
                    ResolveLearnerActivation(learnerActivations, k)));
                sumWeights += learnerWeight;
            }
            if (sumWeights > 1e-15)
                return ClampProbabilityOrNeutral(sum / sumWeights);
        }

        double uniSum = 0;
        int uniCount = 0;
        for (int k = 0; k < K; k++)
        {
            if (weights[k] is not { Length: > 0 } ||
                inputWeights[k] is not { Length: > 0 } ||
                inputBiases[k] is null)
            {
                continue;
            }

            uniSum += ClampProbabilityOrNeutral(ElmLearnerProb(
                features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                featureCount,
                ResolveLearnerHiddenSize(learnerHiddenSizes, k, hiddenSize, inputBiases[k]),
                ResolveLearnerSubset(featureSubsets, k),
                ResolveLearnerActivation(learnerActivations, k)));
            uniCount++;
        }
        return uniCount > 0 ? uniSum / uniCount : 0.5;
    }

    private static double EnsembleCalibProb(
        float[] features, double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double plattA, double plattB,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        double[]? learnerWeights,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        double[]? stackingWeights = null, double stackingBias = 0.0)
    {
        if (weights.Length <= 0)
            return 0.5;

        double raw = EnsembleRawProb(features, weights, biases, inputWeights, inputBiases,
            featureCount, hiddenSize, featureSubsets, learnerWeights, learnerHiddenSizes, learnerActivations,
            stackingWeights, stackingBias);
        double logit = MLFeatureHelper.Logit(ClampProbabilityForLogit(raw));
        return MLFeatureHelper.Sigmoid(plattA * logit + plattB);
    }

    private static double ApplyProductionCalibration(
        double rawProb,
        double plattA,
        double plattB,
        double temperatureScale,
        double plattABuy,
        double plattBBuy,
        double plattASell,
        double plattBSell)
    {
        double rawLogit = MLFeatureHelper.Logit(ClampProbabilityForLogit(rawProb));
        double globalCalibP = ElmCalibrationHelper.ApplyGlobalCalibration(rawProb, plattA, plattB, temperatureScale);

        if (globalCalibP >= 0.5 && plattABuy != 0.0)
            return MLFeatureHelper.Sigmoid(plattABuy * rawLogit + plattBBuy);
        if (globalCalibP < 0.5 && plattASell != 0.0)
            return MLFeatureHelper.Sigmoid(plattASell * rawLogit + plattBSell);

        return globalCalibP;
    }
}
