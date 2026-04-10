using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Pure-function calculator for ML scoring enrichments. Each method is stateless and
/// independently unit-testable — no DB, cache, or logger dependencies.
/// </summary>
internal static class ScoringEnrichmentCalculator
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Candle window slicing (shared by BuildFeaturesAsync, ScoreTimeframeAsync,
    // and ComputeWeightedMultiTimeframeProbability)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts the lookback window, current bar, and previous bar from an
    /// ordered candle list. Returns null when candles are insufficient.
    /// </summary>
    internal static (List<Candle> Window, Candle Current, Candle Previous)? SliceCandleWindow(
        List<Candle> orderedCandles)
    {
        if (orderedCandles.Count < MLFeatureHelper.LookbackWindow + 1)
            return null;

        var current  = orderedCandles[^1];
        var previous = orderedCandles[^2];
        var window   = orderedCandles
            .TakeLast(MLFeatureHelper.LookbackWindow + 1)
            .Take(MLFeatureHelper.LookbackWindow)
            .ToList();

        return (window, current, previous);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Conformal prediction set
    // ═══════════════════════════════════════════════════════════════════════════

    internal static (string? Set, int? Size) ComputeConformalSet(double calibP, double conformalQHat)
        => ComputeConformalSet(calibP, conformalQHat, conformalQHat, conformalQHat);

    internal static (string? Set, int? Size) ComputeConformalSet(
        double calibP,
        double conformalQHat,
        double conformalQHatBuy,
        double conformalQHatSell)
    {
        static bool IsValidQHat(double qHat) =>
            double.IsFinite(qHat) && qHat > 0.0 && qHat < 1.0;

        if (!IsValidQHat(conformalQHat))
            return (null, null);

        double buyQHat = IsValidQHat(conformalQHatBuy) ? conformalQHatBuy : conformalQHat;
        double sellQHat = IsValidQHat(conformalQHatSell) ? conformalQHatSell : conformalQHat;
        double probability = ClampProbabilityOrNeutral(calibP);
        bool includeBuy  = probability >= 1.0 - buyQHat;
        bool includeSell = probability <= sellQHat;
        string set = (includeBuy, includeSell) switch
        {
            (true,  false) => "Buy",
            (false, true)  => "Sell",
            (true,  true)  => "Ambiguous",
            _              => "None",
        };
        int size = set switch
        {
            "Buy" or "Sell" => 1,
            "Ambiguous"     => 2,
            _               => 0,
        };
        return (set, size);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Meta-label secondary classifier
    // ═══════════════════════════════════════════════════════════════════════════

    internal static decimal? ComputeMetaLabelScore(
        double calibP, double ensembleStd, float[] features, int featureCount,
        double[] metaLabelWeights, double metaLabelBias, int[]? topFeatureIndices = null,
        double[]? metaLabelHiddenWeights = null, double[]? metaLabelHiddenBiases = null, int metaLabelHiddenDim = 0)
    {
        if (metaLabelWeights.Length == 0)
            return null;

        int featureTerms = Math.Min(Math.Min(5, featureCount), features.Length);
        var metaFeatures = new double[2 + featureTerms];
        metaFeatures[0] = ClampProbabilityOrNeutral(calibP);
        metaFeatures[1] = ClampNonNegativeFinite(ensembleStd);
        for (int j = 0; j < featureTerms && 2 + j < metaLabelWeights.Length; j++)
        {
            int featureIndex = topFeatureIndices is { Length: > 0 } && j < topFeatureIndices.Length
                ? topFeatureIndices[j]
                : j;
            if (featureIndex < 0 || featureIndex >= featureCount || featureIndex >= features.Length)
                continue;

            metaFeatures[2 + j] = SanitizeFiniteOrDefault(features[featureIndex], 0.0);
        }

        if (metaLabelHiddenDim > 0 &&
            metaLabelHiddenWeights is { Length: > 0 } hiddenWeights &&
            metaLabelHiddenBiases is { Length: > 0 } hiddenBiases &&
            hiddenWeights.Length == metaLabelHiddenDim * metaFeatures.Length &&
            hiddenBiases.Length == metaLabelHiddenDim &&
            metaLabelWeights.Length >= metaLabelHiddenDim)
        {
            var hidden = new double[metaLabelHiddenDim];
            for (int h = 0; h < metaLabelHiddenDim; h++)
            {
                double z = SanitizeFiniteOrDefault(hiddenBiases[h], 0.0);
                int rowOffset = h * metaFeatures.Length;
                for (int j = 0; j < metaFeatures.Length; j++)
                    z += SanitizeFiniteOrDefault(hiddenWeights[rowOffset + j], 0.0) * metaFeatures[j];
                hidden[h] = Math.Max(0.0, z);
            }

            double mlpZ = SanitizeFiniteOrDefault(metaLabelBias, 0.0);
            for (int h = 0; h < metaLabelHiddenDim; h++)
                mlpZ += SanitizeFiniteOrDefault(metaLabelWeights[h], 0.0) * hidden[h];
            return (decimal)ClampProbabilityOrNeutral(MLFeatureHelper.Sigmoid(mlpZ));
        }

        double metaZ = SanitizeFiniteOrDefault(metaLabelBias, 0.0);
        for (int j = 0; j < metaFeatures.Length && j < metaLabelWeights.Length; j++)
            metaZ += SanitizeFiniteOrDefault(metaLabelWeights[j], 0.0) * metaFeatures[j];
        return (decimal)ClampProbabilityOrNeutral(MLFeatureHelper.Sigmoid(metaZ));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Jackknife+ prediction interval
    // ═══════════════════════════════════════════════════════════════════════════

    internal static string? ComputeJackknifeInterval(double[] jackknifeResiduals)
    {
        if (jackknifeResiduals.Length < 10)
            return null;

        int qIdx = (int)Math.Ceiling(0.9 * jackknifeResiduals.Length) - 1;
        qIdx = Math.Clamp(qIdx, 0, jackknifeResiduals.Length - 1);
        double halfWidth = jackknifeResiduals[qIdx];
        return $"±{halfWidth:F4}@90%";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Binary prediction entropy
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double ComputeEntropy(double calibP)
    {
        double ep = Math.Clamp(ClampProbabilityOrNeutral(calibP), 1e-10, 1.0 - 1e-10);
        double entropy = -(ep * Math.Log2(ep) + (1 - ep) * Math.Log2(1 - ep));
        return double.IsFinite(entropy) ? Math.Clamp(entropy, 0.0, 1.0) : 0.0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // OOD Mahalanobis detection
    // ═══════════════════════════════════════════════════════════════════════════

    internal static (double? Score, bool IsOod) ComputeOodMahalanobis(
        float[] features, int featureCount,
        double[] featureVariances, double oodThreshold, double defaultOodThresholdSigma)
    {
        int effectiveFeatureCount = Math.Min(featureCount, Math.Min(features.Length, featureVariances.Length));
        if (effectiveFeatureCount <= 0)
            return (null, false);

        double mahaSq = 0.0;
        for (int j = 0; j < effectiveFeatureCount; j++)
        {
            double v = featureVariances[j];
            if (!double.IsFinite(v) || v <= 1e-8)
                v = 1.0;

            double feature = SanitizeFiniteOrDefault(features[j], 0.0);
            mahaSq += (feature * feature) / v;
        }
        double score = double.IsFinite(mahaSq) ? Math.Sqrt(mahaSq / effectiveFeatureCount) : 0.0;

        double threshold = double.IsFinite(oodThreshold) && oodThreshold > 0.0
            ? oodThreshold
            : SanitizeFiniteOrDefault(defaultOodThresholdSigma, 3.0);
        bool isOod = score > threshold;

        return (score, isOod);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Abstention gate
    // ═══════════════════════════════════════════════════════════════════════════

    internal static decimal? ComputeAbstentionScore(
        double calibP, double ensembleStd, decimal? metaLabelScore,
        double? oodMahalanobisScore, double entropyScore,
        double decisionThreshold,
        double[] abstentionWeights, double abstentionBias)
    {
        if (abstentionWeights.Length == 1)
        {
            double margin = Math.Abs(ClampProbabilityOrNeutral(calibP) - ClampProbabilityOrNeutral(decisionThreshold));
            double az = SanitizeFiniteOrDefault(abstentionBias, 0.0) +
                        SanitizeFiniteOrDefault(abstentionWeights[0], 0.0) * margin;
            return (decimal)ClampProbabilityOrNeutral(MLFeatureHelper.Sigmoid(az));
        }

        if (!metaLabelScore.HasValue)
            return null;

        if (abstentionWeights.Length >= 5)
        {
            var af = new double[]
            {
                ClampProbabilityOrNeutral(calibP),
                ClampNonNegativeFinite(ensembleStd),
                ClampProbabilityOrNeutral((double)metaLabelScore.Value),
                ClampNonNegativeFinite(oodMahalanobisScore ?? 0.0),
                ClampProbabilityOrNeutral(entropyScore),
            };
            double az = SanitizeFiniteOrDefault(abstentionBias, 0.0);
            for (int i = 0; i < 5; i++) az += SanitizeFiniteOrDefault(abstentionWeights[i], 0.0) * af[i];
            return (decimal)ClampProbabilityOrNeutral(MLFeatureHelper.Sigmoid(az));
        }

        if (abstentionWeights.Length >= 3)
        {
            var af = new double[]
            {
                ClampProbabilityOrNeutral(calibP),
                ClampNonNegativeFinite(ensembleStd),
                ClampProbabilityOrNeutral((double)metaLabelScore.Value),
            };
            double az = SanitizeFiniteOrDefault(abstentionBias, 0.0);
            for (int i = 0; i < 3; i++) az += SanitizeFiniteOrDefault(abstentionWeights[i], 0.0) * af[i];
            return (decimal)ClampProbabilityOrNeutral(MLFeatureHelper.Sigmoid(az));
        }

        return null;
    }

    internal static (double AppliedAbstentionThreshold, bool Suppressed) ComputeSelectiveSuppression(
        bool     predictedUp,
        decimal? metaLabelScore,
        int      metaLabelWeightCount,
        double   metaLabelThreshold,
        decimal? abstentionScore,
        int      abstentionWeightCount,
        double   abstentionThreshold,
        double   abstentionThresholdBuy = 0.0,
        double   abstentionThresholdSell = 0.0)
    {
        double appliedAbstentionThreshold = predictedUp
            ? (abstentionThresholdBuy > 0.0 ? abstentionThresholdBuy : abstentionThreshold)
            : (abstentionThresholdSell > 0.0 ? abstentionThresholdSell : abstentionThreshold);

        bool suppressed =
            metaLabelScore.HasValue &&
            metaLabelWeightCount > 0 &&
            metaLabelScore.Value < (decimal)metaLabelThreshold;

        suppressed = suppressed || (
            abstentionScore.HasValue &&
            abstentionWeightCount > 0 &&
            abstentionScore.Value < (decimal)appliedAbstentionThreshold);

        return (appliedAbstentionThreshold, suppressed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Regime routing decision
    // ═══════════════════════════════════════════════════════════════════════════

    internal static string? ComputeRegimeRoutingDecision(string? currentRegime, string? modelRegimeScope)
    {
        if (currentRegime is not null && modelRegimeScope is not null)
            return $"Regime:{modelRegimeScope}";
        if (currentRegime is not null)
            return "Global";
        return "Fallback";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Survival analysis
    // ═══════════════════════════════════════════════════════════════════════════

    internal static (double? EstimatedBars, double? HazardRate) ComputeSurvivalAnalysis(
        float[] features, int featureCount,
        double[] survivalHazard, double[] featureImportanceScores)
    {
        if (survivalHazard.Length == 0)
            return (null, null);

        double riskScore = 0.0;
        int usableFeatures = Math.Min(featureCount, Math.Min(features.Length, featureImportanceScores.Length));
        if (usableFeatures > 0)
        {
            for (int j = 0; j < usableFeatures; j++)
            {
                riskScore += SanitizeFiniteOrDefault(featureImportanceScores[j], 0.0) *
                             SanitizeFiniteOrDefault(features[j], 0.0);
            }
        }
        double expRisk = Math.Exp(Math.Clamp(riskScore, -10.0, 10.0));

        double? estimatedBars = null;
        double cumHazard = 0.0;
        for (int t = 0; t < survivalHazard.Length; t++)
        {
            double hazard = ClampNonNegativeFinite(survivalHazard[t]);
            cumHazard += hazard * expRisk;
            double survivalProb = Math.Exp(-cumHazard);
            if (survivalProb <= 0.5)
            {
                estimatedBars = t + 1.0;
                break;
            }
        }
        estimatedBars ??= (double)survivalHazard.Length;

        double hazardRate = ClampNonNegativeFinite(survivalHazard[0]) * expRisk;
        return (estimatedBars, hazardRate);
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

    private static double SanitizeFiniteOrDefault(double value, double fallback)
        => double.IsFinite(value) ? value : fallback;

    private static double SanitizeFiniteOrDefault(float value, double fallback)
        => float.IsFinite(value) ? value : fallback;

    // ═══════════════════════════════════════════════════════════════════════════
    // Counterfactual explanation
    // ═══════════════════════════════════════════════════════════════════════════

    internal static string? ComputeCounterfactualJson(
        float[]     features,
        double[][]  weights,
        int[][]?    subsets,
        string[]    featureNames,
        int         featureCount,
        double      calibP,
        double      threshold,
        double[][]? mlpHiddenWeights = null,
        int         mlpHiddenDim = 0)
    {
        if (weights.Length == 0 || featureNames.Length == 0) return null;

        var avgWeights = new double[featureCount];
        for (int k = 0; k < weights.Length; k++)
        {
            var projection = BaggedLogisticTrainer.ProjectLearnerToFeatureSpace(
                k, weights, featureCount, subsets, mlpHiddenWeights, mlpHiddenDim);
            for (int j = 0; j < featureCount; j++)
                avgWeights[j] += SanitizeFiniteOrDefault(projection[j], 0.0);
        }

        for (int j = 0; j < featureCount; j++)
            avgWeights[j] /= Math.Max(1, weights.Length);

        double probability = ClampProbabilityOrNeutral(calibP);
        double gradient = probability * (1.0 - probability);
        if (gradient < 1e-10) return null;

        double targetThreshold = ClampProbabilityOrNeutral(threshold);
        double gap = targetThreshold - probability;

        var perturbations = new List<(string Name, double Delta)>();
        for (int j = 0; j < featureCount && j < featureNames.Length; j++)
        {
            double wj = avgWeights[j];
            if (!double.IsFinite(wj) || Math.Abs(wj) < 1e-10) continue;

            double delta = gap / (gradient * wj);
            if (!double.IsFinite(delta))
                continue;
            perturbations.Add((featureNames[j], Math.Round(delta, 3)));
        }

        var top3 = perturbations
            .OrderBy(p => Math.Abs(p.Delta))
            .Take(3)
            .ToDictionary(p => p.Name, p => p.Delta.ToString("+0.###;-0.###;0"));

        return top3.Count > 0 ? JsonSerializer.Serialize(top3) : null;
    }

    internal static string? ComputeCounterfactualJson(
        float[]     features,
        double[][]  weights,
        double[]    biases,
        int[][]?    subsets,
        string[]    featureNames,
        int         featureCount,
        double      calibP,
        double      threshold)
    {
        return ComputeCounterfactualJson(
            features,
            weights,
            subsets,
            featureNames,
            featureCount,
            calibP,
            threshold);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SHAP attribution
    // ═══════════════════════════════════════════════════════════════════════════

    internal static string? ComputeShapContributionsJson(
        float[]     features,
        double[][]  weights,
        int[][]?    subsets,
        string[]    featureNames,
        int         featureCount,
        double[]    featureImportanceScores,
        double[][]? mlpHiddenWeights = null,
        int         mlpHiddenDim = 0)
    {
        if (featureNames.Length == 0) return null;

        var contribs = new (string Name, double Phi)[Math.Min(featureCount, featureNames.Length)];

        if (weights is { Length: > 0 })
        {
            var weightSum = new double[featureCount];

            for (int k = 0; k < weights.Length; k++)
            {
                var projection = BaggedLogisticTrainer.ProjectLearnerToFeatureSpace(
                    k, weights, featureCount, subsets, mlpHiddenWeights, mlpHiddenDim);
                for (int j = 0; j < featureCount; j++)
                    weightSum[j] += SanitizeFiniteOrDefault(projection[j], 0.0);
            }

            for (int j = 0; j < contribs.Length; j++)
            {
                double wBar = weightSum[j] / Math.Max(1, weights.Length);
                double featureValue = j < features.Length ? SanitizeFiniteOrDefault(features[j], 0.0) : 0.0;
                double phi  = wBar * featureValue;
                contribs[j] = (featureNames[j], phi);
            }
        }
        else if (featureImportanceScores is { Length: > 0 })
        {
            for (int j = 0; j < contribs.Length; j++)
            {
                double imp = j < featureImportanceScores.Length
                    ? SanitizeFiniteOrDefault(featureImportanceScores[j], 0.0)
                    : 0.0;
                double featureValue = j < features.Length ? SanitizeFiniteOrDefault(features[j], 0.0) : 0.0;
                double phi = imp * featureValue;
                contribs[j] = (featureNames[j], phi);
            }
        }
        else
        {
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

    internal static string? ComputeNamedContributionsJson(
        IReadOnlyList<double> values,
        IReadOnlyList<string> names,
        IReadOnlyList<double> importanceScores,
        int topK = 5)
    {
        if (names.Count == 0 || values.Count == 0 || importanceScores.Count == 0)
            return null;

        int count = Math.Min(values.Count, Math.Min(names.Count, importanceScores.Count));
        if (count == 0)
            return null;

        var top = Enumerable.Range(0, count)
            .Select(i => new
            {
                Feature = names[i],
                Value = Math.Round(values[i] * importanceScores[i], 4),
            })
            .OrderByDescending(x => Math.Abs(x.Value))
            .Take(Math.Max(1, topK))
            .ToArray();

        return JsonSerializer.Serialize(top);
    }

    internal static string? ComputeShapContributionsJson(
        float[]     features,
        double[][]  weights,
        double[]    biases,
        int[][]?    subsets,
        string[]    featureNames,
        int         featureCount,
        double[]    featureImportanceScores)
    {
        return ComputeShapContributionsJson(
            features,
            weights,
            subsets,
            featureNames,
            featureCount,
            featureImportanceScores);
    }
}
