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
    {
        if (conformalQHat <= 0.0 || conformalQHat >= 1.0)
            return (null, null);

        bool includeBuy  = calibP >= 1.0 - conformalQHat;
        bool includeSell = calibP <= conformalQHat;
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
        double[] metaLabelWeights, double metaLabelBias)
    {
        if (metaLabelWeights.Length == 0)
            return null;

        int metaFeatCount = 2 + Math.Min(5, featureCount);
        double metaZ = metaLabelBias;
        if (metaLabelWeights.Length >= metaFeatCount)
        {
            metaZ += metaLabelWeights[0] * calibP;
            metaZ += metaLabelWeights[1] * ensembleStd;
            for (int j = 0; j < Math.Min(5, featureCount) && 2 + j < metaLabelWeights.Length; j++)
                metaZ += metaLabelWeights[2 + j] * features[j];
        }
        return (decimal)MLFeatureHelper.Sigmoid(metaZ);
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
        double ep = Math.Clamp(calibP, 1e-10, 1.0 - 1e-10);
        double entropy = -(ep * Math.Log2(ep) + (1 - ep) * Math.Log2(1 - ep));
        return Math.Clamp(entropy, 0.0, 1.0);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // OOD Mahalanobis detection
    // ═══════════════════════════════════════════════════════════════════════════

    internal static (double? Score, bool IsOod) ComputeOodMahalanobis(
        float[] features, int featureCount,
        double[] featureVariances, double oodThreshold, double defaultOodThresholdSigma)
    {
        if (featureVariances.Length < featureCount)
            return (null, false);

        double mahaSq = 0.0;
        for (int j = 0; j < featureCount; j++)
        {
            double v = featureVariances[j] > 1e-8 ? featureVariances[j] : 1.0;
            mahaSq += (features[j] * features[j]) / v;
        }
        double score = Math.Sqrt(mahaSq / featureCount);

        double threshold = oodThreshold > 0.0 ? oodThreshold : defaultOodThresholdSigma;
        bool isOod = score > threshold;

        return (score, isOod);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Abstention gate
    // ═══════════════════════════════════════════════════════════════════════════

    internal static decimal? ComputeAbstentionScore(
        double calibP, double ensembleStd, decimal? metaLabelScore,
        double? oodMahalanobisScore, double entropyScore,
        double[] abstentionWeights, double abstentionBias)
    {
        if (!metaLabelScore.HasValue)
            return null;

        if (abstentionWeights.Length == 5)
        {
            var af = new double[]
            {
                calibP, ensembleStd, (double)metaLabelScore.Value,
                oodMahalanobisScore ?? 0.0,
                entropyScore,
            };
            double az = abstentionBias;
            for (int i = 0; i < 5; i++) az += abstentionWeights[i] * af[i];
            return (decimal)MLFeatureHelper.Sigmoid(az);
        }

        if (abstentionWeights.Length == 3)
        {
            var af = new double[] { calibP, ensembleStd, (double)metaLabelScore.Value };
            double az = abstentionBias;
            for (int i = 0; i < 3; i++) az += abstentionWeights[i] * af[i];
            return (decimal)MLFeatureHelper.Sigmoid(az);
        }

        return null;
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
        if (featureImportanceScores.Length >= featureCount)
        {
            for (int j = 0; j < featureCount; j++)
                riskScore += featureImportanceScores[j] * features[j];
        }
        double expRisk = Math.Exp(Math.Clamp(riskScore, -10.0, 10.0));

        double? estimatedBars = null;
        double cumHazard = 0.0;
        for (int t = 0; t < survivalHazard.Length; t++)
        {
            cumHazard += survivalHazard[t] * expRisk;
            double survivalProb = Math.Exp(-cumHazard);
            if (survivalProb <= 0.5)
            {
                estimatedBars = t + 1.0;
                break;
            }
        }
        estimatedBars ??= (double)survivalHazard.Length;

        double hazardRate = survivalHazard[0] * expRisk;
        return (estimatedBars, hazardRate);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Counterfactual explanation
    // ═══════════════════════════════════════════════════════════════════════════

    internal static string? ComputeCounterfactualJson(
        float[] features, double[][] weights, double[] biases, int[][]? subsets,
        string[] featureNames, int featureCount, double calibP, double threshold)
    {
        if (weights.Length == 0 || featureNames.Length == 0) return null;

        var avgWeights = new double[featureCount];
        var counts = new int[featureCount];
        for (int k = 0; k < weights.Length; k++)
        {
            int[] active = subsets?.Length > k && subsets[k] is { Length: > 0 } s
                ? s
                : Enumerable.Range(0, Math.Min(featureCount, weights[k].Length)).ToArray();

            foreach (int j in active)
            {
                if (j < weights[k].Length && j < featureCount)
                {
                    avgWeights[j] += weights[k][j];
                    counts[j]++;
                }
            }
        }

        for (int j = 0; j < featureCount; j++)
            if (counts[j] > 0) avgWeights[j] /= counts[j];

        double gradient = calibP * (1.0 - calibP);
        if (gradient < 1e-10) return null;

        double gap = threshold - calibP;

        var perturbations = new List<(string Name, double Delta)>();
        for (int j = 0; j < featureCount && j < featureNames.Length; j++)
        {
            double wj = avgWeights[j];
            if (Math.Abs(wj) < 1e-10) continue;

            double delta = gap / (gradient * wj);
            perturbations.Add((featureNames[j], Math.Round(delta, 3)));
        }

        var top3 = perturbations
            .OrderBy(p => Math.Abs(p.Delta))
            .Take(3)
            .ToDictionary(p => p.Name, p => p.Delta.ToString("+0.###;-0.###;0"));

        return top3.Count > 0 ? JsonSerializer.Serialize(top3) : null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SHAP attribution
    // ═══════════════════════════════════════════════════════════════════════════

    internal static string? ComputeShapContributionsJson(
        float[]   features,
        double[][] weights,
        int[][]?  subsets,
        string[]  featureNames,
        int       featureCount,
        double[]  featureImportanceScores)
    {
        if (featureNames.Length == 0) return null;

        var contribs = new (string Name, double Phi)[Math.Min(featureCount, featureNames.Length)];

        if (weights is { Length: > 0 })
        {
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
        else if (featureImportanceScores is { Length: > 0 })
        {
            for (int j = 0; j < contribs.Length; j++)
            {
                double imp = j < featureImportanceScores.Length ? featureImportanceScores[j] : 0.0;
                double phi = j < features.Length ? imp * features[j] : 0.0;
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
}
