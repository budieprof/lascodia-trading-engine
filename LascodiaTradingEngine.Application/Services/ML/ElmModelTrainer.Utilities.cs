using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class ElmModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Warm-start pruning remap
    // ═══════════════════════════════════════════════════════════════════════════

    private static ModelSnapshot? RemapWarmStartForPruning(
        ModelSnapshot? warmStart, bool[] activeMask, int featureCount, int hiddenSize)
    {
        if (warmStart?.ElmInputWeights is null) return null;

        int wsK = warmStart.ElmInputWeights.Length;
        var remappedInputWeights = new double[wsK][];
        var remappedInputBiases  = new double[wsK][];
        var remappedSubsets      = new int[wsK][];

        for (int ki = 0; ki < wsK; ki++)
        {
            var oldWIn = warmStart.ElmInputWeights[ki] ?? Array.Empty<double>();
            var oldBIn = warmStart.ElmInputBiases is not null && ki < warmStart.ElmInputBiases.Length
                ? warmStart.ElmInputBiases[ki] : new double[hiddenSize];
            oldBIn ??= Array.Empty<double>();

            int[] oldSub = warmStart.FeatureSubsetIndices is not null && ki < warmStart.FeatureSubsetIndices.Length
                ? warmStart.FeatureSubsetIndices[ki] is { Length: > 0 } warmSubset
                    ? warmSubset
                    : Enumerable.Range(0, featureCount).ToArray()
                : Enumerable.Range(0, featureCount).ToArray();
            int oldSubLen = oldSub.Length;
            int oldHidden = oldBIn.Length > 0
                ? oldBIn.Length
                : oldSubLen > 0
                    ? Math.Max(0, oldWIn.Length / oldSubLen)
                    : hiddenSize;

            var newSubList = new List<int>();
            var oldSubPositions = new List<int>();
            for (int si = 0; si < oldSubLen; si++)
            {
                int oldFi = oldSub[si];
                if (oldFi >= 0 && oldFi < featureCount && oldFi < activeMask.Length && activeMask[oldFi])
                {
                    newSubList.Add(oldFi);
                    oldSubPositions.Add(si);
                }
            }

            int newSubLen = newSubList.Count;
            if (newSubLen == 0)
            {
                remappedInputWeights[ki] = [];
                remappedInputBiases[ki]  = [];
                remappedSubsets[ki]      = [];
                continue;
            }

            var newWIn = new double[oldHidden * newSubLen];
            for (int h = 0; h < oldHidden; h++)
            {
                int oldRowOff = h * oldSubLen;
                int newRowOff = h * newSubLen;
                for (int nsi = 0; nsi < newSubLen; nsi++)
                {
                    int oldPos = oldSubPositions[nsi];
                    if (oldRowOff + oldPos < oldWIn.Length)
                        newWIn[newRowOff + nsi] = oldWIn[oldRowOff + oldPos];
                }
            }

            remappedInputWeights[ki] = newWIn;
            remappedInputBiases[ki]  = (double[])oldBIn.Clone();
            remappedSubsets[ki]      = newSubList.ToArray();
        }

        return new ModelSnapshot
        {
            ElmInputWeights      = remappedInputWeights,
            ElmInputBiases       = remappedInputBiases,
            FeatureSubsetIndices = remappedSubsets,
            FeatureImportanceScores = warmStart.FeatureImportanceScores,
            GenerationNumber       = warmStart.GenerationNumber,
        };
    }

    // ── Online incremental update (Sherman-Morrison) ────────────────────────

    /// <summary>
    /// Incrementally updates an already-trained ELM model with a single new
    /// training sample using the Sherman-Morrison rank-1 formula.
    /// Cost: O(H²) per sample per learner — sub-millisecond for typical H=64.
    /// <para>
    /// The inverse Gram matrix is stored on <see cref="ModelSnapshot.ElmInverseGram"/>
    /// and updated in-place. If not available (older model), the update is skipped.
    /// </para>
    /// </summary>
    /// <param name="snapshot">
    /// The current model snapshot. <c>ElmInverseGram</c>, <c>Weights</c>, <c>Biases</c>
    /// are updated in-place.
    /// </param>
    /// <param name="sample">The new labelled training sample.</param>
    /// <returns><c>true</c> if the update was applied; <c>false</c> if skipped.</returns>
    public bool UpdateOnline(ModelSnapshot snapshot, TrainingSample sample)
    {
        if (snapshot.ElmInverseGram is null || snapshot.ElmInverseGram.Length == 0)
        {
            _logger.LogDebug("ELM online update skipped — no inverse Gram matrix in snapshot");
            return false;
        }

        if (sample.Direction is not (1 or 0 or -1))
        {
            _logger.LogDebug(
                "ELM online update skipped — sample direction {Direction} is invalid",
                sample.Direction);
            return false;
        }

        if (sample.Features.Length == 0 || !HasFiniteValues(sample.Features))
        {
            _logger.LogDebug("ELM online update skipped — sample features are empty or non-finite");
            return false;
        }

        if (snapshot.FracDiffD > 0.0)
        {
            _logger.LogDebug(
                "ELM online update skipped — FracDiffD={D:F2} requires historical context not available to single-sample updates",
                snapshot.FracDiffD);
            return false;
        }

        if (snapshot.Weights is null || snapshot.Biases is null || snapshot.ElmInputWeights is null)
            return false;

        int K = Math.Min(snapshot.Weights.Length, snapshot.Biases.Length);
        double smoothing = double.IsFinite(snapshot.AdaptiveLabelSmoothing)
            ? Math.Clamp(snapshot.AdaptiveLabelSmoothing, 0.0, 0.49)
            : 0.0;
        double target = sample.Direction > 0 ? 1.0 - smoothing : smoothing;

        float[] maskedFeatures;
        try
        {
            bool maskDisablesEveryFeature =
                snapshot.ActiveFeatureMask is { Length: > 0 } activeMask &&
                !activeMask.Any(static value => value);
            var prepared = ElmFeaturePipelineHelper.PrepareSnapshotFeatures(
                sample.Features,
                snapshot,
                applyMask: !maskDisablesEveryFeature);
            maskedFeatures = maskDisablesEveryFeature
                ? new float[prepared.FeatureCount]
                : prepared.Features;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "ELM online update skipped — snapshot preprocessing contract is invalid");
            return false;
        }

        if (!HasFiniteValues(maskedFeatures))
        {
            _logger.LogDebug("ELM online update skipped — replayed features became non-finite");
            return false;
        }

        double forgettingFactor = snapshot.OnlineForgettingFactor > 0.0 && snapshot.OnlineForgettingFactor < 1.0
            ? snapshot.OnlineForgettingFactor : 0.0;

        int updatedLearners = 0;
        for (int k = 0; k < K; k++)
        {
            if (k >= snapshot.ElmInverseGram.Length || snapshot.ElmInverseGram[k] is null)
                continue;
            if (k >= snapshot.ElmInputWeights.Length || snapshot.Weights[k] is null || snapshot.Weights[k].Length == 0)
                continue;
            if (snapshot.ElmInputBiases is null || k >= snapshot.ElmInputBiases.Length)
                continue;
            if (snapshot.ElmInputBiases[k] is null)
                continue;

            var inputW = snapshot.ElmInputWeights[k];
            var inputB = snapshot.ElmInputBiases[k];
            int H      = snapshot.Weights[k].Length;
            int gramDim = snapshot.ElmInverseGramDim is not null && k < snapshot.ElmInverseGramDim.Length
                ? snapshot.ElmInverseGramDim[k] : H;
            if (gramDim <= 0 || snapshot.ElmInverseGram[k].Length != gramDim * gramDim || (gramDim != H && gramDim != H + 1))
                continue;

            // Resolve feature subset for this learner
            float[] features = maskedFeatures;
            if (snapshot.FeatureSubsetIndices is not null && k < snapshot.FeatureSubsetIndices.Length)
            {
                var subset = snapshot.FeatureSubsetIndices[k];
                if (subset is { Length: > 0 })
                {
                    features = new float[subset.Length];
                    for (int i = 0; i < subset.Length; i++)
                        features[i] = subset[i] >= 0 && subset[i] < maskedFeatures.Length
                            ? maskedFeatures[subset[i]]
                            : 0f;
                }
            }

            // Compute hidden activation: h = activation(W_in × features + b_in)
            int inputDim = features.Length;
            var hidden   = new double[H];
            ElmActivation learnerAct = ResolveLearnerActivation(snapshot.LearnerActivations, k);
            for (int h = 0; h < H; h++)
            {
                double z = h < inputB.Length ? inputB[h] : 0.0;
                int rowOff = h * inputDim;
                for (int f = 0; f < inputDim && rowOff + f < inputW.Length; f++)
                    z += inputW[rowOff + f] * features[f];
                hidden[h] = ElmMathHelper.Activate(z, learnerAct);
            }

            if (forgettingFactor > 0.0)
            {
                double scale = 1.0 / (1.0 - forgettingFactor);
                for (int gi = 0; gi < snapshot.ElmInverseGram[k].Length; gi++)
                    snapshot.ElmInverseGram[k][gi] *= scale;
            }

            if (gramDim == H + 1)
            {
                var augmentedFeatures = new double[gramDim];
                var coefficients = new double[gramDim];
                Array.Copy(hidden, augmentedFeatures, H);
                augmentedFeatures[H] = 1.0;
                Array.Copy(snapshot.Weights[k], coefficients, H);
                coefficients[H] = snapshot.Biases[k];

                if (!ElmMathHelper.ShermanMorrisonUpdate(
                        snapshot.ElmInverseGram[k],
                        gramDim,
                        coefficients,
                        augmentedFeatures,
                        target))
                {
                    continue;
                }

                Array.Copy(coefficients, snapshot.Weights[k], H);
                snapshot.Biases[k] = coefficients[H];
                updatedLearners++;
                continue;
            }

            var legacyCoefficients = new double[H];
            Array.Copy(snapshot.Weights[k], legacyCoefficients, H);
            double legacyPrediction = snapshot.Biases[k];
            for (int i = 0; i < H; i++)
                legacyPrediction += legacyCoefficients[i] * hidden[i];
            if (!ElmMathHelper.ShermanMorrisonUpdate(
                    snapshot.ElmInverseGram[k],
                    gramDim,
                    legacyCoefficients,
                    hidden,
                    target))
            {
                continue;
            }

            Array.Copy(legacyCoefficients, snapshot.Weights[k], H);
            double legacyBiasLr = snapshot.TrainSamples > 0 ? 1.0 / snapshot.TrainSamples : 0.001;
            snapshot.Biases[k] += legacyBiasLr * (target - legacyPrediction);
            updatedLearners++;
        }

        if (updatedLearners == 0)
        {
            _logger.LogDebug("ELM online update skipped — no learner had a usable inverse Gram matrix");
            return false;
        }

        snapshot.TrainSamples++;

        _logger.LogDebug("ELM online update applied: {K} learners updated with 1 sample", updatedLearners);
        return true;
    }

    /// <summary>
    /// Lightweight online recalibration: refits Platt scaling + isotonic breakpoints
    /// on a recent sample buffer without retraining the base ensemble.
    /// Call periodically when <c>TrainSamples - TrainSamplesAtLastCalibration</c> exceeds
    /// a threshold (e.g., 50-100 samples).
    /// </summary>
    public bool RecalibrateOnline(ModelSnapshot snapshot, List<TrainingSample> recentSamples)
    {
        if (recentSamples.Count < 20)
        {
            _logger.LogDebug("ELM recalibration skipped — need at least 20 samples, got {N}", recentSamples.Count);
            return false;
        }

        if (snapshot.Weights is null || snapshot.Biases is null ||
            snapshot.ElmInputWeights is null || snapshot.ElmInputBiases is null ||
            snapshot.Means is null || snapshot.Stds is null)
        {
            _logger.LogDebug("ELM recalibration skipped — snapshot missing required fields");
            return false;
        }

        List<TrainingSample> stdSamples;
        try
        {
            stdSamples = ElmFeaturePipelineHelper.ReplaySnapshotPreprocessing(recentSamples, snapshot);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "ELM recalibration skipped — snapshot preprocessing contract is invalid");
            return false;
        }

        if (stdSamples.Count == 0 || stdSamples[0].Features.Length == 0)
        {
            _logger.LogDebug("ELM recalibration skipped — replayed feature buffer is empty");
            return false;
        }

        if (stdSamples.Any(s => s.Features.Length != stdSamples[0].Features.Length || !HasFiniteValues(s.Features)))
        {
            _logger.LogDebug("ELM recalibration skipped — replayed feature buffer contains ragged or non-finite rows");
            return false;
        }

        int featureCount = stdSamples[0].Features.Length;
        int hiddenSize = snapshot.ElmHiddenDim > 0 ? snapshot.ElmHiddenDim : 128;
        var learnerHiddenSizes = snapshot.Weights.Select(w => w?.Length ?? 0).ToArray();
        var learnerActivations = snapshot.LearnerActivations is { Length: > 0 }
            ? snapshot.LearnerActivations.Select(a => (ElmActivation)a).ToArray()
            : Enumerable.Repeat(ElmActivation.Sigmoid, snapshot.Weights.Length).ToArray();

        // Refit Platt scaling
        var learnerAccWeights = snapshot.LearnerAccuracyWeights is { Length: > 0 }
            ? snapshot.LearnerAccuracyWeights : null;
        var stackingWeights = snapshot.MetaWeights is { Length: > 0 } ? snapshot.MetaWeights : null;
        double stackingBias = snapshot.MetaBias;

        Func<float[], double[][], double[], double[][], double[][], int, int, int[][]?, double[]?, double>
            rawProb = (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                f, w, b, iw, ib, fc, hs, fs, lw ?? learnerAccWeights,
                learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias);

        var (newPlattA, newPlattB) = ElmCalibrationHelper.FitPlattScalingCV(
            stdSamples, snapshot.Weights, snapshot.Biases,
            snapshot.ElmInputWeights, snapshot.ElmInputBiases ?? Array.Empty<double[]>(),
            featureCount, hiddenSize, snapshot.FeatureSubsetIndices, rawProb);

        // Refit temperature scaling with new Platt params
        double newTemp = snapshot.TemperatureScale;
        if (snapshot.TemperatureScale != 0.0 && stdSamples.Count >= 10)
        {
            newTemp = ElmCalibrationHelper.FitTemperatureScaling(
                stdSamples, snapshot.Weights, snapshot.Biases,
                snapshot.ElmInputWeights, snapshot.ElmInputBiases ?? Array.Empty<double[]>(),
                featureCount, hiddenSize, snapshot.FeatureSubsetIndices, rawProb);
        }

        // Refit class-conditional Platt with new Platt + temperature
        var (newABuy, newBBuy, newASell, newBSell) = ElmCalibrationHelper.FitClassConditionalPlatt(
            stdSamples, snapshot.Weights, snapshot.Biases,
            snapshot.ElmInputWeights, snapshot.ElmInputBiases ?? Array.Empty<double[]>(),
            featureCount, hiddenSize, snapshot.FeatureSubsetIndices,
            newPlattA, newPlattB, newTemp, rawProb);

        // Refit isotonic with all updated calibration layers
        double PreIsoProb(float[] features) => ApplyProductionCalibration(
            EnsembleRawProb(features, snapshot.Weights, snapshot.Biases,
                snapshot.ElmInputWeights, snapshot.ElmInputBiases ?? Array.Empty<double[]>(),
                featureCount, hiddenSize, snapshot.FeatureSubsetIndices, learnerAccWeights,
                learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias),
            newPlattA, newPlattB, newTemp, newABuy, newBBuy, newASell, newBSell);

        var newIsotonicBp = ElmCalibrationHelper.FitIsotonicCalibration(
            stdSamples, snapshot.Weights, snapshot.Biases,
            snapshot.ElmInputWeights, snapshot.ElmInputBiases ?? Array.Empty<double[]>(),
            newPlattA, newPlattB, featureCount, hiddenSize, snapshot.FeatureSubsetIndices,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PreIsoProb(f));

        snapshot.PlattA = newPlattA;
        snapshot.PlattB = newPlattB;
        snapshot.TemperatureScale = newTemp;
        snapshot.PlattABuy = newABuy; snapshot.PlattBBuy = newBBuy;
        snapshot.PlattASell = newASell; snapshot.PlattBSell = newBSell;
        snapshot.IsotonicBreakpoints = newIsotonicBp;
        snapshot.TrainSamplesAtLastCalibration = snapshot.TrainSamples;

        _logger.LogInformation(
            "ELM online recalibration applied: PlattA={A:F4} PlattB={B:F4} temp={T:F4} isotonicBp={N}",
            newPlattA, newPlattB, newTemp, newIsotonicBp.Length / 2);
        return true;
    }

    private int SanitizeLearnerOutputs(double[][] weights, double[] biases, string label)
    {
        int learnerCount = Math.Min(weights.Length, biases.Length);
        int sanitizedCount = 0;

        for (int k = 0; k < learnerCount; k++)
        {
            weights[k] ??= [];

            bool needsSanitize = !double.IsFinite(biases[k]);
            if (!needsSanitize)
            {
                for (int j = 0; j < weights[k].Length; j++)
                {
                    if (!double.IsFinite(weights[k][j]))
                    {
                        needsSanitize = true;
                        break;
                    }
                }
            }

            if (!needsSanitize)
                continue;

            if (weights[k].Length > 0)
                Array.Clear(weights[k], 0, weights[k].Length);
            biases[k] = 0.0;
            sanitizedCount++;
            _logger.LogWarning("{Label}: sanitized learner {K}: non-finite weights replaced with zeros.", label, k);
        }

        if (sanitizedCount > 0)
            _logger.LogWarning(
                "{Label} post-training sanitization: {N}/{K} learners had non-finite weights.",
                label, sanitizedCount, learnerCount);

        return sanitizedCount;
    }

    private static int ValidateTrainingSamples(IReadOnlyList<TrainingSample> samples)
    {
        if (samples.Count == 0)
            throw new InvalidOperationException("ElmModelTrainer: no training samples provided.");

        int featureCount = samples[0].Features.Length;
        if (featureCount <= 0)
            throw new InvalidOperationException("ElmModelTrainer: training samples must contain at least one feature.");

        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Direction is not (1 or 0 or -1))
                throw new InvalidOperationException(
                    $"ElmModelTrainer: sample {i} has invalid direction {samples[i].Direction}; expected 1 for Buy and 0/-1 for non-Buy.");

            if (!float.IsFinite(samples[i].Magnitude))
                throw new InvalidOperationException($"ElmModelTrainer: sample {i} has non-finite magnitude.");

            if (samples[i].Features.Length != featureCount)
                throw new InvalidOperationException(
                    $"ElmModelTrainer: inconsistent feature count — sample 0 has {featureCount} features, sample {i} has {samples[i].Features.Length}.");

            for (int j = 0; j < samples[i].Features.Length; j++)
            {
                if (!float.IsFinite(samples[i].Features[j]))
                    throw new InvalidOperationException(
                        $"ElmModelTrainer: sample {i} feature {j} is non-finite.");
            }
        }

        return featureCount;
    }

    private static string[] BuildSnapshotFeatureNames(int featureCount)
    {
        if (featureCount <= 0)
            return [];

        var names = new string[featureCount];
        for (int i = 0; i < featureCount; i++)
            names[i] = i < MLFeatureHelper.FeatureNames.Length
                ? MLFeatureHelper.FeatureNames[i]
                : $"F{i}";
        return names;
    }

    private static double ClampProbabilityForLogit(double probability)
    {
        if (!double.IsFinite(probability))
            return 0.5;

        return Math.Clamp(probability, 1e-7, 1.0 - 1e-7);
    }

    private static double ClampProbabilityOrNeutral(double probability)
    {
        if (!double.IsFinite(probability))
            return 0.5;

        return Math.Clamp(probability, 0.0, 1.0);
    }

    private static double ClampNonNegativeFinite(double value)
    {
        if (!double.IsFinite(value) || value < 0.0)
            return 0.0;

        return value;
    }

    private static double SanitizeFiniteOrDefault(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;

    private static double[] BuildMetaLabelFeatureVector(
        double calibP,
        double ensStd,
        float[] features,
        int featureCount,
        int[]? topFeatureIndices = null)
    {
        int[] effectiveTopFeatures = topFeatureIndices is { Length: > 0 }
            ? topFeatureIndices.Take(Math.Min(5, topFeatureIndices.Length)).ToArray()
            : Enumerable.Range(0, Math.Min(5, Math.Max(0, featureCount))).ToArray();
        var metaFeatures = new double[2 + effectiveTopFeatures.Length];
        metaFeatures[0] = ClampProbabilityOrNeutral(calibP);
        metaFeatures[1] = ClampNonNegativeFinite(ensStd);

        for (int j = 0; j < effectiveTopFeatures.Length; j++)
        {
            int featureIndex = effectiveTopFeatures[j];
            if (featureIndex < 0 || featureIndex >= featureCount || featureIndex >= features.Length)
                continue;

            metaFeatures[j + 2] = SanitizeFiniteOrDefault(features[featureIndex], 0.0);
        }

        return metaFeatures;
    }

    private static double ComputeMetaLabelScore(
        double calibP,
        double ensStd,
        float[] features,
        int featureCount,
        double[] metaLabelWeights,
        double metaLabelBias)
    {
        if (metaLabelWeights.Length == 0)
            return 0.5;

        double[] metaFeatures = BuildMetaLabelFeatureVector(calibP, ensStd, features, featureCount);
        double metaZ = SanitizeFiniteOrDefault(metaLabelBias, 0.0);
        for (int j = 0; j < Math.Min(metaLabelWeights.Length, metaFeatures.Length); j++)
            metaZ += SanitizeFiniteOrDefault(metaLabelWeights[j], 0.0) * metaFeatures[j];

        return ClampProbabilityOrNeutral(MLFeatureHelper.Sigmoid(metaZ));
    }

    private static double ComputeMetaLabelScoreWithTopFeatures(
        double calibP,
        double ensStd,
        float[] features,
        int featureCount,
        double[] metaLabelWeights,
        double metaLabelBias,
        int[]? topFeatureIndices,
        double[]? metaLabelHiddenWeights = null,
        double[]? metaLabelHiddenBiases = null,
        int metaLabelHiddenDim = 0)
    {
        if (metaLabelWeights.Length == 0)
            return 0.5;

        decimal? score = ScoringEnrichmentCalculator.ComputeMetaLabelScore(
            calibP,
            ensStd,
            features,
            featureCount,
            metaLabelWeights,
            metaLabelBias,
            topFeatureIndices,
            metaLabelHiddenWeights,
            metaLabelHiddenBiases,
            metaLabelHiddenDim);
        return score.HasValue ? (double)score.Value : 0.5;
    }

    private static ElmActivation ResolveLearnerActivation(ElmActivation[] learnerActivations, int learnerIndex)
    {
        if (learnerActivations.Length == 0)
            return ElmActivation.Sigmoid;

        return learnerIndex < learnerActivations.Length
            ? learnerActivations[learnerIndex]
            : learnerActivations[0];
    }

    private static ElmActivation ResolveLearnerActivation(int[]? learnerActivations, int learnerIndex)
    {
        if (learnerActivations is null || learnerActivations.Length == 0)
            return ElmActivation.Sigmoid;

        int activation = learnerIndex < learnerActivations.Length
            ? learnerActivations[learnerIndex]
            : learnerActivations[0];
        return (ElmActivation)activation;
    }

    private static int[]? ResolveLearnerSubset(int[][]? featureSubsets, int learnerIndex)
    {
        if (featureSubsets is null || learnerIndex < 0 || learnerIndex >= featureSubsets.Length)
            return null;

        return featureSubsets[learnerIndex];
    }

    private static int ResolveLearnerHiddenSize(
        int[] learnerHiddenSizes,
        int learnerIndex,
        int defaultHiddenSize,
        double[] learnerBiases)
    {
        int hiddenSize = learnerIndex >= 0 && learnerIndex < learnerHiddenSizes.Length
            ? learnerHiddenSizes[learnerIndex]
            : defaultHiddenSize;

        if (hiddenSize < 0)
            hiddenSize = 0;

        return Math.Min(hiddenSize, learnerBiases.Length);
    }

    private static bool WarmStartMatchesLearnerSubset(
        ModelSnapshot warmStart,
        int learnerIndex,
        int[] currentSubset,
        int featureCount,
        int expectedInputWeightLength)
    {
        if (warmStart.ElmInputWeights is not { Length: > 0 } inputWeights ||
            learnerIndex >= inputWeights.Length ||
            inputWeights[learnerIndex] is not { Length: > 0 } learnerWeights ||
            learnerWeights.Length != expectedInputWeightLength)
        {
            return false;
        }

        if (warmStart.ElmInputBiases is not { Length: > 0 } inputBiases ||
            learnerIndex >= inputBiases.Length ||
            inputBiases[learnerIndex] is not { Length: > 0 })
        {
            return false;
        }

        if (warmStart.FeatureSubsetIndices is { Length: > 0 } subsets &&
            learnerIndex < subsets.Length &&
            subsets[learnerIndex] is { Length: > 0 } warmSubset)
        {
            return warmSubset.Length == currentSubset.Length &&
                   warmSubset.SequenceEqual(currentSubset);
        }

        if (currentSubset.Length != featureCount)
            return false;

        for (int i = 0; i < currentSubset.Length; i++)
            if (currentSubset[i] != i) return false;

        return true;
    }

    private static bool HasFiniteValues(double[] values)
    {
        for (int i = 0; i < values.Length; i++)
            if (!double.IsFinite(values[i]))
                return false;

        return true;
    }

    private static bool HasFiniteValues(float[] values)
    {
        for (int i = 0; i < values.Length; i++)
            if (!float.IsFinite(values[i]))
                return false;

        return true;
    }

    // ── Reliability diagram (10 equal-width bins) ─────────────────────────────

    private static (double[] BinConf, double[] BinAcc, int[] BinCounts) ComputeReliabilityDiagram(
        List<TrainingSample>  testSet,
        Func<float[], double> calibProb,
        int                   bins = 10)
    {
        var binConf   = new double[bins];
        var binAcc    = new double[bins];
        var binCounts = new int[bins];

        if (testSet.Count < bins) return (binConf, binAcc, binCounts);

        foreach (var s in testSet)
        {
            double p = calibProb(s.Features);
            int b = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[b] += p;
            if (s.Direction > 0) binAcc[b]++;
            binCounts[b]++;
        }

        for (int b = 0; b < bins; b++)
        {
            if (binCounts[b] > 0)
            {
                binConf[b] /= binCounts[b];
                binAcc[b]  /= binCounts[b];
            }
        }
        return (binConf, binAcc, binCounts);
    }

    // ── Murphy decomposition (Brier = calibration + refinement) ───────────────

    private static (double CalibrationLoss, double RefinementLoss) ComputeMurphyDecomposition(
        List<TrainingSample>  testSet,
        Func<float[], double> calibProb,
        int                   bins = 10)
    {
        if (testSet.Count < bins) return (0.0, 0.0);

        var binSumP = new double[bins];
        var binSumY = new double[bins];
        var binCnt  = new int[bins];

        foreach (var s in testSet)
        {
            double p = calibProb(s.Features);
            int b = Math.Clamp((int)(p * bins), 0, bins - 1);
            binSumP[b] += p;
            binSumY[b] += s.Direction > 0 ? 1.0 : 0.0;
            binCnt[b]++;
        }

        int    n   = testSet.Count;
        double cal = 0.0, ref_ = 0.0;
        for (int b = 0; b < bins; b++)
        {
            if (binCnt[b] == 0) continue;
            double meanP = binSumP[b] / binCnt[b];
            double meanY = binSumY[b] / binCnt[b];
            double w     = (double)binCnt[b] / n;
            cal  += w * (meanY - meanP) * (meanY - meanP);
            ref_ += w * meanY * (1.0 - meanY);
        }
        return (cal, ref_);
    }

    // ── Calibration residual stats ────────────────────────────────────────────

    private static (double Mean, double Std, double Threshold) ComputeCalibrationResidualStats(
        List<TrainingSample>  calSet,
        Func<float[], double> calibProb)
    {
        if (calSet.Count < 10) return (0.0, 0.0, 1.0);

        var residuals = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = calibProb(calSet[i].Features);
            double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals[i] = Math.Abs(p - y);
        }

        double mean = 0.0;
        foreach (double r in residuals) mean += r;
        mean /= residuals.Length;

        double variance = 0.0;
        foreach (double r in residuals) { double d = r - mean; variance += d * d; }
        double std = residuals.Length > 1 ? Math.Sqrt(variance / (residuals.Length - 1)) : 0.0;

        return (mean, std, mean + 2.0 * std);
    }

    // ── Prediction stability score ────────────────────────────────────────────

    private static double ComputePredictionStabilityScore(
        List<TrainingSample>  testSet,
        Func<float[], double> calibProb)
    {
        if (testSet.Count == 0) return 0.0;

        double sum = 0.0;
        foreach (var s in testSet)
            sum += Math.Abs(calibProb(s.Features) - 0.5);
        return sum / testSet.Count;
    }

    // ── Feature variances ─────────────────────────────────────────────────────

    private static double[] ComputeFeatureVariances(List<TrainingSample> trainSet, int F)
    {
        if (trainSet.Count < 2) return new double[F];

        var variances = new double[F];
        int n = trainSet.Count;

        for (int j = 0; j < F; j++)
        {
            double sum = 0.0, sumSq = 0.0;
            for (int i = 0; i < n; i++)
            {
                double v = trainSet[i].Features[j];
                sum   += v;
                sumSq += v * v;
            }
            double mean = sum / n;
            variances[j] = sumSq / n - mean * mean;
        }
        return variances;
    }

    // ── Cross-fit adaptive heads ──────────────────────────────────────────────

    private (double[] MetaLabelWeights, double MetaLabelBias,
             double[] AbstentionWeights, double AbstentionBias, double AbstentionThreshold,
             bool Used, int FoldCount)
        CrossFitElmAdaptiveHeads(
            List<TrainingSample>  calSet,
            double[][]            weights,
            double[]              biases,
            double[][]            inputWeights,
            double[][]            inputBiases,
            int                   featureCount,
            int                   hiddenSize,
            int[][]?              featureSubsets,
            int[]                 learnerHiddenSizes,
            ElmActivation[]       learnerActivations,
            double                optimalThreshold,
            int[]                 metaLabelTopFeatureIndices,
            Func<float[], double> calibProb,
            double[]?             stackingWeights,
            double                stackingBias,
            double                subModelLr,
            int                   subModelMaxEpochs,
            int                   subModelPatience,
            int                   embargo,
            int                   minSamples = 20,
            CancellationToken     ct = default)
    {
        const int K = 3;
        int n = calSet.Count;

        if (n < minSamples * K)
            return ([], 0.0, [], 0.0, 0.5, false, 0);

        int foldSize = n / K;

        // Determine dimensions from a trial fit
        var trialMl = FitMetaLabelModel(
            calSet[..Math.Min(minSamples, n)],
            weights, biases, inputWeights, inputBiases,
            featureCount, hiddenSize, featureSubsets, learnerHiddenSizes, learnerActivations,
            optimalThreshold, metaLabelTopFeatureIndices, calibProb,
            stackingWeights, stackingBias,
            subModelLr, subModelMaxEpochs, subModelPatience, embargo, ct);
        int mlDim = trialMl.Item1.Length;
        const int absDim = 3;

        var mlWeightsAccum = new double[mlDim];
        double mlBiasAccum = 0.0;
        var absWeightsAccum = new double[absDim];
        double absBiasAccum = 0.0;
        int validFolds = 0;

        for (int fold = 0; fold < K && !ct.IsCancellationRequested; fold++)
        {
            int testStartFold = fold * foldSize;
            int testEndFold   = fold == K - 1 ? n : (fold + 1) * foldSize;

            var foldTrain = new List<TrainingSample>(n - (testEndFold - testStartFold));
            for (int i = 0; i < testStartFold; i++) foldTrain.Add(calSet[i]);
            for (int i = testEndFold; i < n; i++) foldTrain.Add(calSet[i]);

            if (foldTrain.Count < minSamples) continue;

            var (mw, mb) = FitMetaLabelModel(
                foldTrain, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, learnerHiddenSizes, learnerActivations,
                optimalThreshold, metaLabelTopFeatureIndices, calibProb,
                stackingWeights, stackingBias,
                subModelLr, subModelMaxEpochs, subModelPatience, embargo, ct);

            var (aw, ab, at) = FitAbstentionModel(
                foldTrain, weights, biases, inputWeights, inputBiases,
                0, 0, mw, mb,  // plattA/B not needed for abstention features
                featureCount, hiddenSize, featureSubsets, learnerHiddenSizes, learnerActivations,
                optimalThreshold, metaLabelTopFeatureIndices, calibProb,
                stackingWeights, stackingBias,
                subModelLr, subModelMaxEpochs, subModelPatience, embargo, ct);

            for (int j = 0; j < mlDim && j < mw.Length; j++) mlWeightsAccum[j] += mw[j];
            mlBiasAccum += mb;
            for (int j = 0; j < absDim && j < aw.Length; j++) absWeightsAccum[j] += aw[j];
            absBiasAccum += ab;
            validFolds++;
        }

        if (validFolds == 0)
            return ([], 0.0, [], 0.0, 0.5, false, 0);

        for (int j = 0; j < mlDim; j++) mlWeightsAccum[j] /= validFolds;
        mlBiasAccum /= validFolds;
        for (int j = 0; j < absDim; j++) absWeightsAccum[j] /= validFolds;
        absBiasAccum /= validFolds;

        return (mlWeightsAccum, mlBiasAccum,
                absWeightsAccum, absBiasAccum, 0.5,
                true, validFolds);
    }

    // ── Adversarial validation (CPU fallback) ─────────────────────────────────

    private static double ComputeAdversarialAuc(
        List<TrainingSample> trainSet,
        List<TrainingSample> testSet,
        int                  F)
    {
        int n1 = testSet.Count;
        int n0 = Math.Min(trainSet.Count, n1 * 5);
        int n  = n0 + n1;
        if (n < 20) return 0.5;

        var trainSlice = trainSet.Count > n0 ? trainSet[^n0..] : trainSet;

        // Simple logistic regression via SGD
        var w = new double[F];
        double b = 0;
        const double lr = 0.005;
        const double l2 = 0.01;

        for (int epoch = 0; epoch < 60; epoch++)
        {
            double dB = 0;
            var dW = new double[F];

            for (int i = 0; i < n; i++)
            {
                float[] features = i < n0 ? trainSlice[i].Features : testSet[i - n0].Features;
                double label = i < n0 ? 0.0 : 1.0;

                double z = b;
                for (int j = 0; j < F && j < features.Length; j++) z += w[j] * features[j];
                double p = 1.0 / (1.0 + Math.Exp(-z));
                double err = p - label;
                dB += err;
                for (int j = 0; j < F && j < features.Length; j++) dW[j] += err * features[j];
            }

            b -= lr * dB / n;
            for (int j = 0; j < F; j++) { w[j] -= lr * (dW[j] / n + l2 * w[j]); }
        }

        // Score all samples and compute Wilcoxon AUC
        var scores = new (double Score, int Label)[n];
        for (int i = 0; i < n; i++)
        {
            float[] features = i < n0 ? trainSlice[i].Features : testSet[i - n0].Features;
            double z = b;
            for (int j = 0; j < F && j < features.Length; j++) z += w[j] * features[j];
            scores[i] = (1.0 / (1.0 + Math.Exp(-z)), i < n0 ? 0 : 1);
        }
        Array.Sort(scores, (a, c) => c.Score.CompareTo(a.Score));

        long tp = 0, aucNum = 0;
        foreach (var (_, lbl) in scores)
        {
            if (lbl == 1) tp++;
            else aucNum += tp;
        }
        return (n1 > 0 && n0 > 0) ? (double)aucNum / ((long)n1 * n0) : 0.5;
    }
}
