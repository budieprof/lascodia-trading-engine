using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

// Partial: sample creation, regime assignment, baseline-snapshot deserialization.
// Pure ECE / signal / severity math has been extracted to MLCalibrationSignalEvaluator
// (collaborator class, independently unit-testable). This file now holds only the
// worker-internal data-extraction helpers that bridge logs/snapshots into the shape
// the evaluator expects. See file-layout note in MLCalibrationMonitorWorker.cs.
public sealed partial class MLCalibrationMonitorWorker
{
    private static Dictionary<MarketRegimeEnum, List<CalibrationSample>> AssignRegimes(
        List<CalibrationSample> chronological, List<RegimeSlice> ascendingTimeline)
    {
        var groups = new Dictionary<MarketRegimeEnum, List<CalibrationSample>>();
        if (ascendingTimeline.Count == 0) return groups;

        // Binary search per sample to find the regime that was active at PredictedAt.
        var detectedAtArray = ascendingTimeline.Select(slice => slice.DetectedAt).ToArray();

        foreach (var sample in chronological)
        {
            int idx = Array.BinarySearch(detectedAtArray, sample.PredictedAt);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0) continue;

            var regime = ascendingTimeline[idx].Regime;
            if (!groups.TryGetValue(regime, out var bucket))
            {
                bucket = [];
                groups[regime] = bucket;
            }
            bucket.Add(sample);
        }

        return groups;
    }

    private static bool TryCreateCalibrationSample(
        MLModelPredictionLog log,
        out CalibrationSample sample)
    {
        sample = default;

        bool? correct = log.DirectionCorrect;
        if (!correct.HasValue && log.ActualDirection.HasValue)
            correct = log.ActualDirection.Value == log.PredictedDirection;

        if (!correct.HasValue)
            return false;

        double confidence;
        if (HasExplicitProbability(log))
        {
            double threshold = MLFeatureHelper.ResolveLoggedDecisionThreshold(log, 0.5);
            double pBuy = MLFeatureHelper.ResolveLoggedServedBuyProbability(log, threshold);
            confidence = log.PredictedDirection == TradeDirection.Buy
                ? pBuy
                : 1.0 - pBuy;
        }
        else
        {
            // Legacy logs: ConfidenceScore is the predicted-class confidence by convention
            // (matches how scorers populate the field). Direct assignment is correct.
            confidence = (double)log.ConfidenceScore;
        }

        if (!double.IsFinite(confidence))
            return false;

        sample = new CalibrationSample(
            Confidence: Math.Clamp(confidence, 0.0, 1.0),
            Correct: correct.Value,
            OutcomeAt: log.OutcomeRecordedAt ?? log.PredictedAt,
            PredictedAt: log.PredictedAt);
        return true;
    }

    private static bool HasExplicitProbability(MLModelPredictionLog log)
        => log.ServedCalibratedProbability.HasValue
        || log.CalibratedProbability.HasValue
        || log.RawProbability.HasValue;

    private static double? TryResolveBaselineEce(byte[]? modelBytes, MarketRegimeEnum? regime = null)
    {
        if (modelBytes is not { Length: > 0 })
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(modelBytes);
            if (snapshot is null) return null;

            // Per-regime baseline takes precedence when training populated it; otherwise the
            // global ECE is the honest fallback. Operators see the same baseline for the
            // global row and any regimes the training pipeline didn't measure.
            if (regime is not null && snapshot.RegimeEce is { Count: > 0 } regimeMap &&
                regimeMap.TryGetValue(regime.Value.ToString(), out double regimeEce) &&
                double.IsFinite(regimeEce) && regimeEce >= 0.0)
            {
                return regimeEce;
            }

            if (!double.IsFinite(snapshot.Ece) || snapshot.Ece < 0.0)
                return null;

            return snapshot.Ece;
        }
        catch
        {
            return null;
        }
    }
}
