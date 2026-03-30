using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Blends <see cref="MLScoreResult"/> outputs from multiple committee models
/// into a single unified result using accuracy-weighted probability averaging.
/// </summary>
/// <remarks>
/// <para>Improvement #1: Ensemble scoring committee.</para>
/// <para>
/// Each committee member's calibrated probability is weighted by its training-time
/// direction accuracy (higher accuracy = higher weight). The blended probability
/// drives the final direction and confidence. Magnitude predictions are weighted
/// identically. Committee disagreement (std of individual probabilities) is computed
/// as an additional uncertainty signal.
/// </para>
/// </remarks>
public static class EnsembleCommitteeBlender
{
    /// <summary>
    /// Blends N score results into a single unified result.
    /// </summary>
    /// <param name="members">Individual model score results paired with their training accuracy.</param>
    /// <returns>A blended <see cref="MLScoreResult"/> with committee metadata.</returns>
    public static MLScoreResult Blend(IReadOnlyList<(MLScoreResult Result, decimal TrainingAccuracy)> members)
    {
        if (members.Count == 0)
            return new MLScoreResult(null, null, null, null);

        if (members.Count == 1)
        {
            var single = members[0].Result;
            var modelIds = single.MLModelId.HasValue
                ? JsonSerializer.Serialize(new[] { single.MLModelId.Value })
                : null;
            return single with
            {
                CommitteeModelIdsJson = modelIds,
                CommitteeDisagreement = 0m,
            };
        }

        // Compute accuracy-based weights (softmax-style normalization)
        double totalWeight = 0;
        var weights = new double[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            // Use accuracy as raw weight; clamp to [0.5, 1.0] to prevent
            // near-zero weights for models slightly below random
            double w = Math.Max(0.5, (double)members[i].TrainingAccuracy);
            weights[i] = w;
            totalWeight += w;
        }

        // Normalize weights
        if (totalWeight > 0)
        {
            for (int i = 0; i < weights.Length; i++)
                weights[i] /= totalWeight;
        }

        // Weighted average of calibrated probabilities
        double blendedProb = 0;
        double blendedMagnitude = 0;
        int magnitudeCount = 0;
        var probs = new List<double>(members.Count);
        var modelIds2 = new List<long>(members.Count);
        MLScoreResult? bestResult = null;
        double bestWeight = -1;

        for (int i = 0; i < members.Count; i++)
        {
            var (result, _) = members[i];
            double w = weights[i];

            // Use CalibratedProbability if available, else derive from confidence
            double prob = result.CalibratedProbability.HasValue
                ? (double)result.CalibratedProbability.Value
                : result.ConfidenceScore.HasValue
                    ? (double)result.ConfidenceScore.Value / 100.0
                    : 0.5;

            blendedProb += w * prob;
            probs.Add(prob);

            if (result.PredictedMagnitudePips.HasValue)
            {
                blendedMagnitude += w * (double)result.PredictedMagnitudePips.Value;
                magnitudeCount++;
            }

            if (result.MLModelId.HasValue)
                modelIds2.Add(result.MLModelId.Value);

            if (w > bestWeight)
            {
                bestWeight = w;
                bestResult = result;
            }
        }

        // Compute committee disagreement: std of individual probabilities
        double mean = probs.Average();
        double variance = probs.Sum(p => (p - mean) * (p - mean)) / probs.Count;
        decimal disagreement = (decimal)Math.Sqrt(variance);

        // Check if any member had a real probability (not the 0.5 fallback).
        // If all members have null calibrated probability and null confidence,
        // the blended probability is meaningless — return null direction.
        bool anyRealProbability = members.Any(m =>
            m.Result.CalibratedProbability.HasValue || m.Result.ConfidenceScore.HasValue);

        // Derive direction and confidence from blended probability
        TradeDirection? direction = anyRealProbability
            ? (blendedProb >= 0.5 ? TradeDirection.Buy : TradeDirection.Sell)
            : null;
        decimal confidence = anyRealProbability
            ? (decimal)(Math.Abs(blendedProb - 0.5) * 2.0 * 100.0)
            : 0m;
        decimal? magnitude = magnitudeCount > 0 ? (decimal)blendedMagnitude : null;

        // Use the best-weighted model's enrichments as the base, override core fields
        var baseResult = bestResult ?? members[0].Result;
        string committeeJson = JsonSerializer.Serialize(modelIds2);

        // Kelly fraction from blended probability
        decimal kellyP = (decimal)blendedProb;
        decimal kelly = Math.Max(0, 2 * kellyP - 1) * 0.5m;

        return baseResult with
        {
            PredictedDirection        = direction,
            PredictedMagnitudePips    = magnitude,
            ConfidenceScore           = confidence,
            MLModelId                 = baseResult.MLModelId,
            CalibratedProbability     = (decimal)blendedProb,
            ServedCalibratedProbability = (decimal)blendedProb,
            KellyFraction             = kelly,
            CommitteeModelIdsJson     = committeeJson,
            CommitteeDisagreement     = disagreement,
            // Aggregate ensemble disagreement as max across committee members
            EnsembleDisagreement      = members
                .Where(m => m.Result.EnsembleDisagreement.HasValue)
                .Select(m => m.Result.EnsembleDisagreement!.Value)
                .DefaultIfEmpty(disagreement)
                .Max(),
        };
    }
}
