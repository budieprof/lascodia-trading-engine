using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services;

public sealed partial class BaggedLogisticTrainer
{

    // ── Evaluation ───────────────────────────────────────────────────────────

    internal static double PredictMagnitude(float[] features, double[] magWeights, double magBias, int polyTopN = PolyTopN)
    {
        if (features.Length == 0)
            return magBias;

        double prediction = magBias;
        int baseCount = Math.Min(features.Length, magWeights.Length);
        for (int j = 0; j < baseCount; j++)
            prediction += magWeights[j] * features[j];

        if (magWeights.Length > features.Length)
        {
            int actualTop = Math.Min(polyTopN, features.Length);
            int idx = features.Length;
            for (int a = 0; a < actualTop; a++)
            {
                for (int b = a + 1; b < actualTop && idx < magWeights.Length; b++)
                    prediction += magWeights[idx++] * features[a] * features[b];
            }
        }

        return prediction;
    }

    // ── Feature subsampling ────────────────────────────────────────────────────

    /// <summary>
    /// Samples <c>⌈ratio × featureCount⌉</c> feature indices without replacement.
    /// Indices are sorted for deterministic access order.
    /// </summary>
    internal static int[] GenerateFeatureSubset(int featureCount, double ratio, int seed)
    {
        if (featureCount <= 0)
            return [];

        double safeRatio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0.0, 1.0) : 1.0;
        int subCount = Math.Clamp(Math.Max(1, (int)Math.Ceiling(safeRatio * featureCount)), 1, featureCount);
        var rng      = new Random(seed);

        // Partial Fisher-Yates: O(subCount) vs the original O(F log F) double-sort via LINQ.
        // Only the first subCount positions are shuffled; the rest are discarded.
        var indices = new int[featureCount];
        for (int i = 0; i < featureCount; i++) indices[i] = i;
        for (int i = 0; i < subCount; i++)
        {
            int j = i + rng.Next(featureCount - i);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        var result = indices[..subCount];
        Array.Sort(result); // sort for deterministic cache-friendly access order
        return result;
    }

    // ── Sharpe ratio ──────────────────────────────────────────────────────────

    /// <summary>Computes Sharpe ratio over the first <paramref name="count"/> entries of a buffer.</summary>
    internal static double ComputeSharpe(double[] buffer, int count)
    {
        if (count < 2) return 0;
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            if (!double.IsFinite(buffer[i]))
                return 0.0;
            sum += buffer[i];
        }
        double mean = sum / count;
        double varSum = 0;
        for (int i = 0; i < count; i++) { double d = buffer[i] - mean; varSum += d * d; }
        double std = Math.Sqrt(varSum / (count - 1));
        if (!double.IsFinite(std) || std < 1e-10)
            return 0.0;

        double sharpe = mean / std * Math.Sqrt(252);
        return double.IsFinite(sharpe) ? sharpe : 0.0;
    }

    // ── Temporal weighting ────────────────────────────────────────────────────

    public static double[] ComputeTemporalWeights(int count, double lambda)
    {
        if (count == 0) return [];
        if (!double.IsFinite(lambda))
        {
            var uniform = new double[count];
            Array.Fill(uniform, 1.0 / count);
            return uniform;
        }

        var w = new double[count];
        for (int i = 0; i < count; i++)
        {
            double exponent = lambda * ((double)i / Math.Max(1, count - 1) - 1.0);
            w[i] = Math.Exp(Math.Clamp(exponent, -700.0, 700.0));
        }
        double sum = w.Sum();
        if (!double.IsFinite(sum) || sum <= 0.0)
        {
            Array.Fill(w, 1.0 / count);
            return w;
        }

        for (int i = 0; i < count; i++)
            w[i] /= sum;
        return w;
    }

    internal static double[] BuildNormalisedCdf(double[] weights)
    {
        if (weights.Length == 0)
            return [];

        var sanitized = new double[weights.Length];
        double sum = 0.0;
        for (int i = 0; i < weights.Length; i++)
        {
            double weight = double.IsFinite(weights[i]) ? Math.Max(0.0, weights[i]) : 0.0;
            sanitized[i] = weight;
            sum += weight;
        }

        var cdf    = new double[weights.Length];
        if (!double.IsFinite(sum) || sum <= 0)
        {
            // Degenerate case: all weights are zero — fall back to a uniform distribution
            // so bootstrap sampling remains random rather than always returning index 0.
            for (int i = 0; i < cdf.Length; i++) cdf[i] = (i + 1.0) / cdf.Length;
            return cdf;
        }

        cdf[0] = sanitized[0] / sum;
        for (int i = 1; i < weights.Length; i++)
            cdf[i] = cdf[i - 1] + sanitized[i] / sum;

        cdf[^1] = 1.0;
        return cdf;
    }

    internal static int SampleFromCdf(double[] cdf, Random rng)
    {
        if (cdf.Length == 0)
            return 0;

        double u   = rng.NextDouble();
        int    idx = Array.BinarySearch(cdf, u);
        if (idx < 0) idx = ~idx;
        return Math.Clamp(idx, 0, cdf.Length - 1);
    }

    // ── Feature pruning ───────────────────────────────────────────────────────

    internal static void ValidateTrainingSamples(IReadOnlyList<TrainingSample> samples)
    {
        if (samples.Count == 0)
            throw new InvalidOperationException("BaggedLogisticTrainer: no training samples provided.");

        int featureCount = samples[0].Features.Length;
        if (featureCount <= 0)
            throw new InvalidOperationException("BaggedLogisticTrainer: training samples must contain at least one feature.");

        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Direction is not (1 or 0 or -1))
                throw new InvalidOperationException(
                    $"BaggedLogisticTrainer: sample {i} has invalid direction {samples[i].Direction}; expected 1 for Buy and 0/-1 for non-Buy.");

            if (!float.IsFinite(samples[i].Magnitude))
                throw new InvalidOperationException(
                    $"BaggedLogisticTrainer: sample {i} has non-finite magnitude.");

            for (int j = 0; j < samples[i].Features.Length; j++)
            {
                if (!float.IsFinite(samples[i].Features[j]))
                    throw new InvalidOperationException(
                        $"BaggedLogisticTrainer: sample {i} feature {j} is non-finite.");
            }

            if (i == 0)
                continue;

            if (samples[i].Features.Length != featureCount)
                throw new InvalidOperationException(
                    $"BaggedLogisticTrainer: inconsistent feature count — sample 0 has {featureCount} features, sample {i} has {samples[i].Features.Length}.");
        }
    }

    internal static (float[] Means, float[] Stds) ComputeStandardizationStats(List<TrainingSample> fitSamples)
    {
        if (fitSamples.Count == 0)
            throw new InvalidOperationException("Cannot fit standardisation statistics on an empty sample set.");

        var rawFeatures = new List<float[]>(fitSamples.Count);
        foreach (var s in fitSamples) rawFeatures.Add(s.Features);
        return MLFeatureHelper.ComputeStandardization(rawFeatures);
    }

    internal static int ResolveBarsPerDay(int barsPerDay) => barsPerDay > 0 ? barsPerDay : 24;

    internal static int ComputeIncrementalRecentSampleCount(int sampleCount, int recentWindowDays, int barsPerDay)
        => Math.Min(sampleCount, recentWindowDays * ResolveBarsPerDay(barsPerDay));

    internal static int ComputeDensityRatioRecentCount(int sampleCount, int recentWindowDays, int barsPerDay)
    {
        if (sampleCount <= 10)
            return Math.Max(0, sampleCount - 1);

        int recentCount = Math.Max(
            10,
            Math.Min(sampleCount / 5, recentWindowDays * ResolveBarsPerDay(barsPerDay)));
        return Math.Min(recentCount, sampleCount - 10);
    }

    internal static int ComputeLeakageGap(int embargo, int purgeHorizonBars, int lookbackWindow)
    {
        int lookbackGap = Math.Max(0, embargo) + Math.Max(0, lookbackWindow - 1);
        int horizonGap = Math.Max(0, purgeHorizonBars);
        return Math.Max(lookbackGap, horizonGap);
    }

    internal static (int TrainStdEnd, int CalStart, int CalEnd, int TestStart) ComputeFinalSplitBoundaries(
        int sampleCount,
        int embargo,
        int purgeHorizonBars,
        int lookbackWindow)
    {
        int gap = ComputeLeakageGap(embargo, purgeHorizonBars, lookbackWindow);
        int usableCount = sampleCount - 2 * gap;
        if (usableCount <= 0)
            throw new InvalidOperationException(
                $"Leakage-safe gap ({gap}) consumes the entire final split window ({sampleCount} samples).");

        int trainCount = (int)(usableCount * 0.70);
        int calCount = (int)(usableCount * 0.10);
        int trainStdEnd = Math.Max(0, trainCount);
        int calStart = Math.Min(sampleCount, trainStdEnd + gap);
        int boundedCalEnd = Math.Min(sampleCount, calStart + calCount);
        int testStart = Math.Min(sampleCount, boundedCalEnd + gap);
        return (trainStdEnd, calStart, boundedCalEnd, testStart);
    }

    internal static int ResolveTrainingRandomSeed(int trainingRandomSeed)
        => trainingRandomSeed > 0 ? trainingRandomSeed : 42;

    internal static HoldoutWindowPlan ComputeHoldoutWindowPlan(int sampleCount)
    {
        if (sampleCount < 20)
            throw new InvalidOperationException(
                $"Holdout window is too small to split into selection/calibration slices ({sampleCount} samples).");

        int selectionCount = Math.Max(10, sampleCount / 2);
        selectionCount = Math.Min(selectionCount, sampleCount - 10);
        int calibrationCount = sampleCount - selectionCount;
        return new HoldoutWindowPlan(
            SelectionStart: 0,
            SelectionCount: selectionCount,
            CalibrationStart: selectionCount,
            CalibrationCount: calibrationCount);
    }

    internal static (int TrainEnd, int HoldoutStart) ComputeWalkForwardInnerValidationBoundaries(
        int sampleCount,
        int embargo,
        int purgeHorizonBars,
        int lookbackWindow)
    {
        int gap = ComputeLeakageGap(embargo, purgeHorizonBars, lookbackWindow);
        int usableCount = sampleCount - gap;
        if (usableCount <= 20)
            throw new InvalidOperationException(
                $"Walk-forward inner validation gap ({gap}) leaves only {usableCount} usable samples.");

        int holdoutCount = Math.Max(20, usableCount / 4);
        holdoutCount = Math.Min(holdoutCount, usableCount - 1);
        int trainCount = usableCount - holdoutCount;
        int holdoutStart = Math.Min(sampleCount, trainCount + gap);
        return (trainCount, holdoutStart);
    }

    internal static (bool UseValidationHoldout, int ValSize) ComputeEnsembleValidationPlan(int sampleCount)
    {
        if (sampleCount < 30)
            return (false, 0);

        int valSize = Math.Clamp(Math.Max(5, sampleCount / 10), 1, sampleCount - 1);
        return (true, valSize);
    }

    internal static List<TrainingSample> ApplyStandardization(
        List<TrainingSample> source,
        float[]              means,
        float[]              stds)
    {
        var standardised = new List<TrainingSample>(source.Count);
        foreach (var sample in source)
        {
            standardised.Add(sample with
            {
                Features = MLFeatureHelper.Standardize(sample.Features, means, stds)
            });
        }

        return standardised;
    }

    internal static bool[] BuildFeatureMask(float[] importance, double threshold, int featureCount)
    {
        if (threshold <= 0.0 || featureCount == 0)
        {
            var allTrue = new bool[featureCount];
            Array.Fill(allTrue, true);
            return allTrue;
        }

        double minImportance = threshold / featureCount;
        var mask = new bool[featureCount];
        for (int j = 0; j < featureCount; j++)
        {
            if (j >= importance.Length || !float.IsFinite(importance[j]))
            {
                mask[j] = true;
                continue;
            }

            mask[j] = importance[j] >= minImportance;
        }

        return mask;
    }

    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        return samples.Select(s =>
        {
            var f = (float[])s.Features.Clone();
            for (int j = 0; j < f.Length && j < mask.Length; j++)
                if (!mask[j]) f[j] = 0f;
            return s with { Features = f };
        }).ToList();
    }

    internal static double[] ProjectLearnerToFeatureSpace(
        int          learnerIndex,
        double[][]   weights,
        int          featureCount,
        int[][]?     subsets = null,
        double[][]?  mlpHiddenW = null,
        int          mlpHiddenDim = 0)
    {
        var projection = new double[featureCount];
        if (learnerIndex < 0 || learnerIndex >= weights.Length || featureCount <= 0)
            return projection;

        bool useMlp = mlpHiddenDim > 0 &&
                      mlpHiddenW is not null &&
                      learnerIndex < mlpHiddenW.Length &&
                      mlpHiddenW[learnerIndex] is not null;

        if (useMlp)
        {
            var hW = mlpHiddenW![learnerIndex];
            int inputDim = hW.Length / Math.Max(1, mlpHiddenDim);
            if (inputDim <= 0)
                return projection;

            int[]? subset = subsets?.Length > learnerIndex ? subsets[learnerIndex] : null;
            for (int col = 0; col < inputDim; col++)
            {
                double contribution = 0.0;
                for (int h = 0; h < mlpHiddenDim && h < weights[learnerIndex].Length; h++)
                {
                    int hwIndex = h * inputDim + col;
                    if (hwIndex >= hW.Length) break;
                    contribution += weights[learnerIndex][h] * hW[hwIndex];
                }

                if (subset is { Length: > 0 } && col >= subset.Length)
                    continue;

                int inputIndex = subset is { Length: > 0 } ? subset[col] : col;
                AccumulateProjectedFeatureContribution(projection, inputIndex, featureCount, contribution);
            }

            return projection;
        }

        int[]? subset2 = subsets?.Length > learnerIndex ? subsets[learnerIndex] : null;
        if (subset2 is { Length: > 0 })
        {
            foreach (int inputIndex in subset2)
            {
                if (inputIndex < 0 || inputIndex >= weights[learnerIndex].Length)
                    continue;

                AccumulateProjectedFeatureContribution(
                    projection, inputIndex, featureCount, weights[learnerIndex][inputIndex]);
            }

            return projection;
        }

        for (int inputIndex = 0; inputIndex < weights[learnerIndex].Length; inputIndex++)
            AccumulateProjectedFeatureContribution(
                projection, inputIndex, featureCount, weights[learnerIndex][inputIndex]);

        return projection;
    }

    internal static double[] ComputeMeanProjectedFeatureImportance(
        double[][]   weights,
        double[]     biases,
        int          featureCount,
        int[][]?     subsets = null,
        double[][]?  mlpHiddenW = null,
        int          mlpHiddenDim = 0)
    {
        var importance = new double[featureCount];
        if (featureCount <= 0 || weights.Length == 0)
            return importance;

        var activeLearners = ComputeActiveLearnerMask(weights, biases);
        int activeCount = 0;
        for (int k = 0; k < weights.Length; k++)
        {
            if (k >= activeLearners.Length || !activeLearners[k])
                continue;

            var learnerProjection = ProjectLearnerToFeatureSpace(
                k, weights, featureCount, subsets, mlpHiddenW, mlpHiddenDim);
            for (int j = 0; j < featureCount; j++)
                importance[j] += Math.Abs(learnerProjection[j]);
            activeCount++;
        }

        if (activeCount == 0)
            return importance;

        for (int j = 0; j < featureCount; j++)
            importance[j] /= activeCount;

        return importance;
    }

    internal static bool IsPredictionCorrect(
        double probability,
        int    direction,
        double decisionThreshold = 0.5)
    {
        bool predictedUp = probability >= decisionThreshold;
        bool actualUp = direction == 1;
        return predictedUp == actualUp;
    }

    internal static string GetFeatureDisplayName(int index)
    {
        return index >= 0 && index < MLFeatureHelper.FeatureNames.Length
            ? MLFeatureHelper.FeatureNames[index]
            : $"Feature{index}";
    }

    internal static string[] BuildFeatureNames(int featureCount)
    {
        if (featureCount <= 0)
            return [];

        var names = new string[featureCount];
        for (int i = 0; i < featureCount; i++)
            names[i] = GetFeatureDisplayName(i);
        return names;
    }

    internal static string ComputeFeatureSchemaFingerprint(string[] featureNames)
    {
        if (featureNames.Length == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.Append("bagged-feature-schema|");
        builder.Append(featureNames.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append('|');
        for (int i = 0; i < featureNames.Length; i++)
        {
            builder.Append(featureNames[i]);
            builder.Append('|');
        }

        return ComputeSha256(builder.ToString());
    }

    internal static string ComputePreprocessingFingerprint(int featureCount)
    {
        if (featureCount <= 0)
            return string.Empty;

        return ComputeSha256(
            FormattableString.Invariant($"bagged-preproc|{featureCount}|standardize-v1|mask-at-inference"));
    }

    internal static string ComputeTrainerFingerprint(TrainingHyperparams hp, int featureCount)
    {
        return ComputeSha256(string.Join("|",
            "bagged-trainer",
            ModelVersion,
            featureCount.ToString(CultureInfo.InvariantCulture),
            hp.K.ToString(CultureInfo.InvariantCulture),
            hp.MlpHiddenDim.ToString(CultureInfo.InvariantCulture),
            hp.PolyLearnerFraction.ToString("G17", CultureInfo.InvariantCulture)));
    }

    internal static WarmStartCompatibilityResult AssessWarmStartCompatibility(
        ModelSnapshot snapshot,
        string expectedFeatureSchemaFingerprint,
        string expectedPreprocessingFingerprint,
        string expectedTrainerFingerprint,
        int expectedFeatureCount)
    {
        var issues = new List<string>();
        if (!string.Equals(snapshot.Type, ModelType, StringComparison.OrdinalIgnoreCase))
            issues.Add("Warm-start snapshot type does not match the bagged trainer.");
        if (snapshot.Features.Length != expectedFeatureCount)
            issues.Add("Warm-start feature count does not match the current feature count.");
        if (snapshot.Means.Length != expectedFeatureCount || snapshot.Stds.Length != expectedFeatureCount)
            issues.Add("Warm-start standardization vectors do not match the current feature count.");

        string actualFeatureSchemaFingerprint =
            !string.IsNullOrWhiteSpace(snapshot.FeatureSchemaFingerprint)
                ? snapshot.FeatureSchemaFingerprint
                : ComputeFeatureSchemaFingerprint(snapshot.Features);
        if (!string.IsNullOrWhiteSpace(expectedFeatureSchemaFingerprint) &&
            !string.IsNullOrWhiteSpace(actualFeatureSchemaFingerprint) &&
            !string.Equals(expectedFeatureSchemaFingerprint, actualFeatureSchemaFingerprint, StringComparison.Ordinal))
        {
            issues.Add("Feature schema fingerprint mismatch.");
        }

        string actualPreprocessingFingerprint =
            !string.IsNullOrWhiteSpace(snapshot.PreprocessingFingerprint)
                ? snapshot.PreprocessingFingerprint
                : ComputePreprocessingFingerprint(snapshot.Features.Length);
        if (!string.IsNullOrWhiteSpace(expectedPreprocessingFingerprint) &&
            !string.IsNullOrWhiteSpace(actualPreprocessingFingerprint) &&
            !string.Equals(expectedPreprocessingFingerprint, actualPreprocessingFingerprint, StringComparison.Ordinal))
        {
            issues.Add("Preprocessing fingerprint mismatch.");
        }

        string actualTrainerFingerprint = snapshot.TrainerFingerprint;
        if (!string.IsNullOrWhiteSpace(expectedTrainerFingerprint) &&
            !string.IsNullOrWhiteSpace(actualTrainerFingerprint) &&
            !string.Equals(expectedTrainerFingerprint, actualTrainerFingerprint, StringComparison.Ordinal))
        {
            issues.Add("Trainer fingerprint mismatch.");
        }

        return new WarmStartCompatibilityResult(issues.Count == 0, [..issues.Distinct()]);
    }

    private static string ComputeSha256(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    internal static void CopyRawFeatureWindow(
        double[] destination,
        float[]  source,
        int      destinationOffset,
        int      maxRawFeatures)
    {
        if (destinationOffset < 0 || destinationOffset >= destination.Length || maxRawFeatures <= 0)
            return;

        int copyCount = Math.Min(maxRawFeatures, Math.Min(source.Length, destination.Length - destinationOffset));
        for (int i = 0; i < copyCount; i++)
            destination[destinationOffset + i] = source[i];

        int clearFrom = destinationOffset + copyCount;
        int clearTo = Math.Min(destination.Length, destinationOffset + maxRawFeatures);
        for (int i = clearFrom; i < clearTo; i++)
            destination[i] = 0.0;
    }

    internal static void CopySelectedFeatureWindow(
        double[] destination,
        float[]  source,
        int      destinationOffset,
        int[]?   selectedFeatureIndices,
        int      maxRawFeatures)
    {
        if (destinationOffset < 0 || destinationOffset >= destination.Length || maxRawFeatures <= 0)
            return;

        int clearTo = Math.Min(destination.Length, destinationOffset + maxRawFeatures);
        for (int offset = 0; destinationOffset + offset < clearTo; offset++)
        {
            int featureIndex = selectedFeatureIndices is { Length: > 0 } && offset < selectedFeatureIndices.Length
                ? selectedFeatureIndices[offset]
                : offset;
            destination[destinationOffset + offset] =
                featureIndex >= 0 && featureIndex < source.Length
                    ? source[featureIndex]
                    : 0.0;
        }
    }

    internal static int[] ComputeTopFeatureIndices(double[] importanceScores, int count, int featureCount)
    {
        if (count <= 0 || featureCount <= 0)
            return [];

        return importanceScores
            .Take(featureCount)
            .Select((importance, index) => (Importance: double.IsFinite(importance) ? importance : 0.0, Index: index))
            .OrderByDescending(entry => entry.Importance)
            .ThenBy(entry => entry.Index)
            .Take(Math.Min(count, featureCount))
            .Select(entry => entry.Index)
            .ToArray();
    }

    internal static double ComputeAsymmetricErrorWeight(int direction, double fpCostWeight)
    {
        if (Math.Abs(fpCostWeight - 0.5) <= 1e-6)
            return 1.0;

        // Labels are encoded as +1 (Buy) and -1 (Sell). False-positive weighting
        // applies to negative-class examples, not to the impossible label value 0.
        return direction > 0
            ? 2.0 * (1.0 - fpCostWeight)
            : 2.0 * fpCostWeight;
    }

    internal static bool LearnerUsesPolynomialInputs(
        int          learnerIndex,
        int          featureCount,
        int[][]?     subsets = null,
        double[][]?  mlpHiddenW = null,
        int          mlpHiddenDim = 0,
        double[][]?  weights = null)
    {
        if (learnerIndex < 0 || featureCount <= 0)
            return false;

        if (subsets?.Length > learnerIndex && subsets[learnerIndex] is { Length: > 0 } subset)
        {
            for (int i = 0; i < subset.Length; i++)
                if (subset[i] >= featureCount)
                    return true;

            return false;
        }

        if (mlpHiddenDim > 0 &&
            mlpHiddenW is not null &&
            learnerIndex < mlpHiddenW.Length &&
            mlpHiddenW[learnerIndex] is not null)
        {
            int inputDim = mlpHiddenW[learnerIndex].Length / Math.Max(1, mlpHiddenDim);
            if (inputDim > featureCount)
                return true;
        }

        return weights is not null &&
               learnerIndex < weights.Length &&
               weights[learnerIndex].Length > featureCount;
    }

    private static void AccumulateProjectedFeatureContribution(
        double[] projection,
        int      inputIndex,
        int      featureCount,
        double   contribution)
    {
        if (!double.IsFinite(contribution) || inputIndex < 0)
            return;

        if (inputIndex < featureCount)
        {
            projection[inputIndex] += contribution;
            return;
        }

        if (!TryGetPolynomialFeaturePair(inputIndex, featureCount, PolyTopN, out int left, out int right))
            return;

        double share = contribution * 0.5;
        projection[left] += share;
        projection[right] += share;
    }

    private static bool TryGetPolynomialFeaturePair(
        int  augmentedFeatureIndex,
        int  baseFeatureCount,
        int  topN,
        out int left,
        out int right)
    {
        left = right = -1;
        if (augmentedFeatureIndex < baseFeatureCount)
            return false;

        int pairIndex = augmentedFeatureIndex - baseFeatureCount;
        int actualTop = Math.Min(topN, baseFeatureCount);
        int running = 0;
        for (int i = 0; i < actualTop; i++)
        {
            for (int j = i + 1; j < actualTop; j++)
            {
                if (running == pairIndex)
                {
                    left = i;
                    right = j;
                    return true;
                }

                running++;
            }
        }

        return false;
    }

    internal static bool TryCopyWarmStartMlpHiddenWeights(
        double[] source,
        double[] destination,
        int      hiddenDim,
        int[]?   oldSubset,
        int[]    newSubset)
    {
        if (hiddenDim <= 0 || source.Length != destination.Length || newSubset.Length == 0)
            return false;

        int rowWidth = destination.Length / hiddenDim;
        if (rowWidth * hiddenDim != destination.Length || rowWidth != newSubset.Length)
            return false;

        if (oldSubset is null || oldSubset.Length == 0)
        {
            for (int i = 0; i < newSubset.Length; i++)
            {
                if (newSubset[i] != i)
                    return false;
            }

            Array.Copy(source, destination, destination.Length);
            return true;
        }

        if (oldSubset.Length != rowWidth)
            return false;

        var oldColumnByFeature = new Dictionary<int, int>(oldSubset.Length);
        for (int col = 0; col < oldSubset.Length; col++)
            oldColumnByFeature.TryAdd(oldSubset[col], col);

        Array.Clear(destination, 0, destination.Length);
        for (int h = 0; h < hiddenDim; h++)
        {
            int srcRowOffset = h * rowWidth;
            int dstRowOffset = h * rowWidth;
            for (int newCol = 0; newCol < newSubset.Length; newCol++)
            {
                if (!oldColumnByFeature.TryGetValue(newSubset[newCol], out int oldCol))
                    continue;

                destination[dstRowOffset + newCol] = source[srcRowOffset + oldCol];
            }
        }

        return true;
    }

    internal static bool TryCopyWarmStartMlpHiddenBiases(double[] source, double[] destination)
    {
        if (source.Length != destination.Length)
            return false;

        Array.Copy(source, destination, destination.Length);
        return true;
    }

    internal static double[] ComputeMlpHiddenBackpropSignals(
        double   totalErr,
        double[] outputWeights,
        double[] hiddenActivations,
        int      hiddenDim)
    {
        var signals = new double[hiddenDim];
        int count = Math.Min(hiddenDim, Math.Min(outputWeights.Length, hiddenActivations.Length));
        for (int h = 0; h < count; h++)
        {
            if (hiddenActivations[h] <= 0.0)
                continue;

            signals[h] = totalErr * outputWeights[h];
        }

        return signals;
    }

    internal static int SanitizeLearners(
        double[][]  weights,
        double[]    biases,
        double[][]? mlpHiddenW,
        double[][]? mlpHiddenB)
    {
        int sanitizedCount = 0;

        for (int k = 0; k < weights.Length; k++)
        {
            bool needsSanitize = k >= biases.Length || !double.IsFinite(biases[k]);
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

            if (!needsSanitize && mlpHiddenW is { Length: > 0 } && k < mlpHiddenW.Length && mlpHiddenW[k] is not null)
            {
                for (int j = 0; j < mlpHiddenW[k].Length; j++)
                {
                    if (!double.IsFinite(mlpHiddenW[k][j]))
                    {
                        needsSanitize = true;
                        break;
                    }
                }
            }

            if (!needsSanitize && mlpHiddenB is { Length: > 0 } && k < mlpHiddenB.Length && mlpHiddenB[k] is not null)
            {
                for (int j = 0; j < mlpHiddenB[k].Length; j++)
                {
                    if (!double.IsFinite(mlpHiddenB[k][j]))
                    {
                        needsSanitize = true;
                        break;
                    }
                }
            }

            if (!needsSanitize)
                continue;

            Array.Clear(weights[k], 0, weights[k].Length);
            if (k < biases.Length) biases[k] = 0.0;
            if (mlpHiddenW is { Length: > 0 } && k < mlpHiddenW.Length && mlpHiddenW[k] is not null)
                Array.Clear(mlpHiddenW[k], 0, mlpHiddenW[k].Length);
            if (mlpHiddenB is { Length: > 0 } && k < mlpHiddenB.Length && mlpHiddenB[k] is not null)
                Array.Clear(mlpHiddenB[k], 0, mlpHiddenB[k].Length);

            sanitizedCount++;
        }

        return sanitizedCount;
    }

    internal static bool[] ComputeActiveLearnerMask(double[][] weights, double[] biases)
    {
        var active = new bool[weights.Length];
        for (int k = 0; k < weights.Length; k++)
        {
            bool isActive = k < biases.Length && Math.Abs(biases[k]) > 1e-12;
            if (!isActive)
            {
                for (int j = 0; j < weights[k].Length; j++)
                {
                    if (Math.Abs(weights[k][j]) > 1e-12)
                    {
                        isActive = true;
                        break;
                    }
                }
            }

            active[k] = isActive;
        }

        return active;
    }

    internal static double[] BuildLearnerAccuracyWeights(double[] learnerCalAccuracies, bool[] activeLearners)
    {
        if (learnerCalAccuracies.Length == 0 || activeLearners.Length == 0)
            return [];

        var result = new double[Math.Min(learnerCalAccuracies.Length, activeLearners.Length)];
        double sum = 0.0;
        int activeCount = 0;

        for (int k = 0; k < result.Length; k++)
        {
            if (!activeLearners[k]) continue;
            result[k] = Math.Max(0.0, learnerCalAccuracies[k]);
            sum += result[k];
            activeCount++;
        }

        if (activeCount == 0)
            return result;

        if (sum <= 1e-12)
        {
            double uniformWeight = 1.0 / activeCount;
            for (int k = 0; k < result.Length; k++)
                if (activeLearners[k]) result[k] = uniformWeight;
            return result;
        }

        for (int k = 0; k < result.Length; k++)
            result[k] /= sum;

        return result;
    }

    internal static bool[] BuildPositiveWeightMask(double[] weights)
    {
        var mask = new bool[weights.Length];
        for (int i = 0; i < weights.Length; i++)
            mask[i] = weights[i] > 1e-12;
        return mask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryGetFeatureValue(float[] features, int featureIndex, out double value)
    {
        if ((uint)featureIndex < (uint)features.Length)
        {
            value = features[featureIndex];
            return true;
        }

        value = 0.0;
        return false;
    }

    private static int GetUsableHiddenUnitCount(int requestedHiddenDim, double[] outputWeights, double[] hiddenBiases)
    {
        return Math.Min(requestedHiddenDim, Math.Min(outputWeights.Length, hiddenBiases.Length));
    }

    internal static double ComputeLearnerProbability(
        float[]      features,
        int          learnerIndex,
        double[][]   weights,
        double[]     biases,
        int          featureCount,
        int[][]?     subsets,
        int          polyLearnerStartIndex,
        double[][]?  mlpHiddenW,
        double[][]?  mlpHiddenB,
        int          mlpHiddenDim)
    {
        bool useMlp = mlpHiddenDim > 0 &&
                      mlpHiddenW is not null &&
                      mlpHiddenB is not null &&
                      learnerIndex < mlpHiddenW.Length &&
                      mlpHiddenW[learnerIndex] is not null &&
                      learnerIndex < mlpHiddenB.Length &&
                      mlpHiddenB[learnerIndex] is not null;

        if (useMlp)
        {
            var hW = mlpHiddenW![learnerIndex];
            var hB = mlpHiddenB![learnerIndex];
            int[] subset = subsets?.Length > learnerIndex && subsets[learnerIndex] is { Length: > 0 } s ? s : [];
            int subsetLen = subset.Length > 0 ? subset.Length : featureCount;
            bool mlpUsesPolyInputs = LearnerUsesPolynomialInputs(
                learnerIndex, featureCount, subsets, mlpHiddenW, mlpHiddenDim);
            float[] mlpFeatures = mlpUsesPolyInputs
                ? AugmentWithPolyFeatures(features, featureCount, PolyTopN)
                : features;

            if (subset.Length == 0)
            {
                subset = new int[mlpFeatures.Length];
                for (int j = 0; j < mlpFeatures.Length; j++) subset[j] = j;
                subsetLen = mlpFeatures.Length;
            }

            double z = learnerIndex < biases.Length ? biases[learnerIndex] : 0.0;
            int hiddenUnits = GetUsableHiddenUnitCount(mlpHiddenDim, weights[learnerIndex], hB);
            for (int h = 0; h < hiddenUnits; h++)
            {
                double act = hB[h];
                int rowOff = h * subsetLen;
                for (int si = 0; si < subsetLen && rowOff + si < hW.Length; si++)
                {
                    if (TryGetFeatureValue(mlpFeatures, subset[si], out double featureValue))
                        act += hW[rowOff + si] * featureValue;
                }
                double hidden = Math.Max(0.0, act);
                z += weights[learnerIndex][h] * hidden;
            }

            return MLFeatureHelper.Sigmoid(z);
        }

        bool isPolyLearner = learnerIndex >= polyLearnerStartIndex && featureCount >= PolyTopN;
        float[] learnerFeatures = isPolyLearner
            ? AugmentWithPolyFeatures(features, featureCount, PolyTopN)
            : features;

        double zLin = learnerIndex < biases.Length ? biases[learnerIndex] : 0.0;
        if (subsets?.Length > learnerIndex && subsets[learnerIndex] is { Length: > 0 } subset2)
        {
            foreach (int j in subset2)
                if ((uint)j < (uint)learnerFeatures.Length && (uint)j < (uint)weights[learnerIndex].Length)
                    zLin += weights[learnerIndex][j] * learnerFeatures[j];
        }
        else
        {
            int len = Math.Min(weights[learnerIndex].Length, learnerFeatures.Length);
            for (int j = 0; j < len; j++)
                zLin += weights[learnerIndex][j] * learnerFeatures[j];
        }

        return MLFeatureHelper.Sigmoid(zLin);
    }

    private static double AggregateLearnerProbs(
        double[]    probs,
        MetaLearner meta = default,
        double[]?   gesWeights = null,
        double[]?   learnerAccuracyWeights = null,
        double[]?   learnerCalAccuracies = null)
    {
        return InferenceHelpers.AggregateProbs(
            probs,
            probs.Length,
            meta.IsActive ? meta.Weights : null,
            meta.Bias,
            gesWeights,
            learnerAccuracyWeights,
            learnerCalAccuracies);
    }

    internal static double AggregateSelectedLearnerProbs(
        double[]          probs,
        IReadOnlyList<int> learnerIndices,
        MetaLearner       meta = default,
        double[]?         gesWeights = null,
        double[]?         learnerAccuracyWeights = null,
        double[]?         learnerCalAccuracies = null)
    {
        if (learnerIndices.Count == 0) return 0.5;

        if (learnerIndices.Count == probs.Length)
            return AggregateLearnerProbs(probs, meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies);

        if (meta.IsActive && meta.Weights.Length == probs.Length)
        {
            var denseProbs = new double[probs.Length];
            Array.Fill(denseProbs, 0.5);
            for (int i = 0; i < learnerIndices.Count; i++)
            {
                int learnerIndex = learnerIndices[i];
                if (learnerIndex >= 0 && learnerIndex < denseProbs.Length)
                    denseProbs[learnerIndex] = probs[learnerIndex];
            }

            return InferenceHelpers.AggregateProbs(
                denseProbs,
                denseProbs.Length,
                meta.Weights,
                meta.Bias,
                null,
                null,
                null);
        }

        var selectedProbs = new double[learnerIndices.Count];
        double[]? selectedGesWeights = gesWeights is { Length: > 0 } ? new double[learnerIndices.Count] : null;
        double[]? selectedLearnerAccuracyWeights = learnerAccuracyWeights is { Length: > 0 }
            ? new double[learnerIndices.Count]
            : null;
        double[]? selectedCalAccuracies = learnerCalAccuracies is { Length: > 0 } ? new double[learnerIndices.Count] : null;

        for (int i = 0; i < learnerIndices.Count; i++)
        {
            int learnerIndex = learnerIndices[i];
            selectedProbs[i] = probs[learnerIndex];
            if (selectedGesWeights is not null && learnerIndex < gesWeights!.Length)
                selectedGesWeights[i] = gesWeights[learnerIndex];
            if (selectedLearnerAccuracyWeights is not null && learnerIndex < learnerAccuracyWeights!.Length)
                selectedLearnerAccuracyWeights[i] = learnerAccuracyWeights[learnerIndex];
            if (selectedCalAccuracies is not null && learnerIndex < learnerCalAccuracies!.Length)
                selectedCalAccuracies[i] = learnerCalAccuracies[learnerIndex];
        }

        return InferenceHelpers.AggregateProbs(
            selectedProbs,
            selectedProbs.Length,
            null,
            meta.Bias,
            selectedGesWeights,
            selectedLearnerAccuracyWeights,
            selectedCalAccuracies);
    }

    private static (double AvgProb, double StdProb) ComputeEnsembleProbabilityAndStd(
        float[]      features,
        double[][]   weights,
        double[]     biases,
        int          featureCount,
        int[][]?     subsets,
        MetaLearner  meta = default,
        double[]?    gesWeights = null,
        double[]?    learnerAccuracyWeights = null,
        double[]?    learnerCalAccuracies = null,
        bool[]?      activeLearners = null,
        MlpState     mlp = default)
    {
        var probs = GetLearnerProbs(features, weights, biases, featureCount, subsets,
            mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
        double avg = AggregateLearnerProbs(probs, meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies);
        double meanProb = 0.5;
        int activeCount = 0;
        double sumProb = 0.0;
        for (int k = 0; k < probs.Length; k++)
        {
            if (activeLearners is not null && (k >= activeLearners.Length || !activeLearners[k]))
                continue;
            sumProb += probs[k];
            activeCount++;
        }

        if (activeCount == 0)
        {
            meanProb = probs.Length > 0 ? probs.Average() : 0.5;
            activeCount = probs.Length;
        }
        else
        {
            meanProb = sumProb / activeCount;
        }

        double variance = 0.0;
        for (int k = 0; k < probs.Length; k++)
        {
            if (activeLearners is not null && (k >= activeLearners.Length || !activeLearners[k]))
                continue;
            double delta = probs[k] - meanProb;
            variance += delta * delta;
        }

        double std = activeCount > 1 ? Math.Sqrt(variance / (activeCount - 1)) : 0.0;
        return (avg, std);
    }

    internal static double[] ComputeLearnerCalAccuracies(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             featureSubsets,
        MlpState             mlp = default)
    {
        var learnerCalAccuracies = new double[weights.Length];
        if (calSet.Count == 0)
            return learnerCalAccuracies;

        foreach (var s in calSet)
        {
            var lp = GetLearnerProbs(s.Features, weights, biases, featureCount, featureSubsets,
                mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            for (int k = 0; k < weights.Length; k++)
            {
                if (IsPredictionCorrect(lp[k], s.Direction))
                    learnerCalAccuracies[k]++;
            }
        }

        for (int k = 0; k < weights.Length; k++)
            learnerCalAccuracies[k] /= calSet.Count;

        return learnerCalAccuracies;
    }

    /// <summary>
    /// Returns the per-learner sigmoid probabilities (length = K).
    /// When <paramref name="mlpHiddenW"/> is non-null, uses MLP forward pass
    /// (hidden = ReLU(Wh × x + bh), z = Wo · hidden + bias) instead of linear logistic.
    /// </summary>
    private static double[] GetLearnerProbs(
        float[]      features,
        double[][]   weights,
        double[]     biases,
        int          featureCount,
        int[][]?     subsets,
        double[][]?  mlpHiddenW = null,
        double[][]?  mlpHiddenB = null,
        int          mlpHiddenDim = 0)
    {
        const int PolyTopN = 5;
        bool useMlp = mlpHiddenDim > 0 && mlpHiddenW is not null && mlpHiddenB is not null;
        var probs = new double[weights.Length];
        for (int k = 0; k < weights.Length; k++)
        {
            bool isPolyLearner = LearnerUsesPolynomialInputs(
                k, featureCount, subsets, mlpHiddenW, mlpHiddenDim, weights);
            float[] kFeatures  = isPolyLearner
                ? AugmentWithPolyFeatures(features, featureCount, PolyTopN)
                : features;

            // MLP forward pass when hidden weights are available for this learner
            if (useMlp && k < mlpHiddenW!.Length && mlpHiddenW[k] is not null &&
                k < mlpHiddenB!.Length && mlpHiddenB[k] is not null)
            {
                var hW = mlpHiddenW[k];
                var hB = mlpHiddenB[k];
                int[] subset = subsets?.Length > k && subsets[k] is { Length: > 0 } s ? s : [];
                int subLen = subset.Length > 0 ? subset.Length : featureCount;
                // If no subset stored, build a full-range index for contiguous access
                if (subset.Length == 0)
                {
                    subset = new int[kFeatures.Length];
                    for (int j = 0; j < kFeatures.Length; j++) subset[j] = j;
                    subLen = kFeatures.Length;
                }

                double z = k < biases.Length ? biases[k] : 0.0;
                int hiddenUnits = GetUsableHiddenUnitCount(mlpHiddenDim, weights[k], hB);
                for (int h = 0; h < hiddenUnits; h++)
                {
                    double act = hB[h];
                    int rowOff = h * subLen;
                    for (int si = 0; si < subLen && rowOff + si < hW.Length; si++)
                    {
                        if (TryGetFeatureValue(kFeatures, subset[si], out double featureValue))
                            act += hW[rowOff + si] * featureValue;
                    }
                    double hidden = Math.Max(0.0, act); // ReLU
                    z += weights[k][h] * hidden;
                }
                probs[k] = MLFeatureHelper.Sigmoid(z);
            }
            else
            {
                // Linear logistic forward pass
                int kDim = weights[k].Length;
                double z = k < biases.Length ? biases[k] : 0.0;
                if (subsets?.Length > k && subsets[k] is { Length: > 0 } subset2)
                {
                    foreach (int j in subset2)
                        if ((uint)j < (uint)weights[k].Length && (uint)j < (uint)kFeatures.Length)
                            z += weights[k][j] * kFeatures[j];
                }
                else
                {
                    int len = Math.Min(kDim, kFeatures.Length);
                    for (int j = 0; j < len; j++)
                        z += weights[k][j] * kFeatures[j];
                }
                probs[k] = MLFeatureHelper.Sigmoid(z);
            }
        }
        return probs;
    }

    /// <summary>
    /// Computes the ensemble probability.
    /// When <paramref name="meta"/> is active, applies the stacking meta-learner over per-learner
    /// probabilities instead of simple averaging.
    /// </summary>
    internal static double EnsembleProb(
        float[]      features,
        double[][]   weights,
        double[]     biases,
        int          featureCount,
        int[][]?     subsets       = null,
        MetaLearner  meta          = default,
        double[][]?  mlpHiddenW    = null,
        double[][]?  mlpHiddenB    = null,
        int          mlpHiddenDim  = 0)
    {
        var lp = GetLearnerProbs(features, weights, biases, featureCount, subsets,
            mlpHiddenW, mlpHiddenB, mlpHiddenDim);

        if (meta.IsActive)
        {
            double z = meta.Bias;
            for (int k = 0; k < meta.Weights.Length && k < lp.Length; k++)
                z += meta.Weights[k] * lp[k];
            return MLFeatureHelper.Sigmoid(z);
        }

        return lp.Average();
    }

    // ── Biased feature subset sampling (warm-start transfer) ─────────────────

    /// <summary>
    /// Samples feature indices with probability proportional to
    /// <c>importanceScores[j] + epsilon</c> where <c>epsilon = 1 / featureCount</c>.
    /// This biases feature subsets toward historically important features while keeping
    /// all features eligible. Returns a sorted array of sampled indices.
    /// </summary>
    internal static int[] GenerateBiasedFeatureSubset(
        int     featureCount,
        double  ratio,
        double[] importanceScores,
        int     seed)
    {
        if (featureCount <= 0)
            return [];

        double safeRatio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0.0, 1.0) : 1.0;
        int subCount = Math.Clamp(Math.Max(1, (int)Math.Ceiling(safeRatio * featureCount)), 1, featureCount);
        var rng      = new Random(seed);
        double epsilon = 1.0 / featureCount;

        // Build unnormalised weights: importance + epsilon
        var rawWeights = new double[featureCount];
        double sum = 0.0;
        for (int j = 0; j < featureCount; j++)
        {
            double importance = j < importanceScores.Length && double.IsFinite(importanceScores[j])
                ? Math.Max(0.0, importanceScores[j])
                : 0.0;
            double w = importance + epsilon;
            rawWeights[j] = w;
            sum += w;
        }

        // Build CDF
        var cdf = new double[featureCount];
        cdf[0] = rawWeights[0] / sum;
        for (int j = 1; j < featureCount; j++)
            cdf[j] = cdf[j - 1] + rawWeights[j] / sum;

        // Sample without replacement using reservoir / rejection
        var selected = new HashSet<int>(subCount);
        int attempts = 0;
        while (selected.Count < subCount && attempts < featureCount * 10)
        {
            attempts++;
            double u   = rng.NextDouble();
            int    idx = Array.BinarySearch(cdf, u);
            if (idx < 0) idx = ~idx;
            idx = Math.Clamp(idx, 0, featureCount - 1);
            selected.Add(idx);
        }

        // Fallback: pad with sequential indices if needed
        for (int j = 0; j < featureCount && selected.Count < subCount; j++)
            selected.Add(j);

        return [..selected.OrderBy(x => x)];
    }

    // ── Beta distribution sampler (for Mixup) ─────────────────────────────────

    /// <summary>Samples from Gamma(shape, 1) using the Marsaglia-Tsang method.</summary>
    private static double SampleGamma(Random rng, double shape)
    {
        if (shape < 1.0)
            return SampleGamma(rng, shape + 1.0) * Math.Pow(Math.Max(1e-300, rng.NextDouble()), 1.0 / shape);
        double d = shape - 1.0 / 3.0;
        double c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            double x, v;
            do { x = SampleGaussian(rng, 1.0); v = 1.0 + c * x; } while (v <= 0.0);
            v = v * v * v;
            double u = rng.NextDouble();
            if (u < 1.0 - 0.0331 * x * x * x * x) return d * v;
            if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v))) return d * v;
        }
    }

    /// <summary>
    /// Samples from Beta(alpha, alpha) and returns max(λ, 1−λ) ≥ 0.5
    /// so the first Mixup sample always dominates (standard practical Mixup convention).
    /// </summary>
    private static double SampleBeta(Random rng, double alpha)
    {
        if (alpha <= 0.0) return 0.5;
        double g1 = SampleGamma(rng, alpha);
        double g2 = SampleGamma(rng, alpha);
        double lam = g1 / (g1 + g2 + 1e-300);
        return Math.Max(lam, 1.0 - lam);
    }
}
