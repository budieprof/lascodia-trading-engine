using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Default <see cref="IMLCalibratedEdgeEvaluator"/> implementation. Pure-functional —
/// no DI, no logging, no DB. Independently unit-testable without the worker.
/// </summary>
public sealed class MLCalibratedEdgeEvaluator : IMLCalibratedEdgeEvaluator
{
    public LiveEdgeSummary ComputeSummary(IReadOnlyList<CalibratedEdgeSample> samples)
        => ComputeSummary(samples, bootstrapResamples: 0, modelId: 0);

    public LiveEdgeSummary ComputeSummary(
        IReadOnlyList<CalibratedEdgeSample> samples,
        int bootstrapResamples,
        long modelId)
    {
        if (samples.Count == 0)
        {
            return new LiveEdgeSummary(
                ResolvedCount: 0,
                ExpectedValuePips: 0,
                WinRate: 0,
                MeanProbabilityGap: 0,
                MeanAbsMagnitudePips: 0,
                OldestOutcomeAt: default,
                NewestOutcomeAt: default,
                EvStderr: 0);
        }

        // Pre-compute the per-sample signed edge once so the bootstrap loop below can
        // reuse it without re-running threshold comparisons N×R times.
        var perSampleEdge = new double[samples.Count];
        double evSum = 0.0;
        double winSum = 0.0;
        double gapSum = 0.0;
        double magnitudeSum = 0.0;
        DateTime oldestOutcomeAt = DateTime.MaxValue;
        DateTime newestOutcomeAt = DateTime.MinValue;

        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            bool predictedBuy = sample.ServedBuyProbability >= sample.DecisionThreshold;
            bool correct = predictedBuy == sample.ActualBuy;
            double probabilityGap = Math.Abs(sample.ServedBuyProbability - sample.DecisionThreshold);
            double signedEdge = (correct ? 1.0 : -1.0) * probabilityGap * sample.AbsMagnitudePips;
            perSampleEdge[i] = signedEdge;

            evSum += signedEdge;
            gapSum += probabilityGap;
            magnitudeSum += sample.AbsMagnitudePips;
            if (correct) winSum += 1.0;

            if (sample.OutcomeAt < oldestOutcomeAt) oldestOutcomeAt = sample.OutcomeAt;
            if (sample.OutcomeAt > newestOutcomeAt) newestOutcomeAt = sample.OutcomeAt;
        }

        double divisor = samples.Count;
        double evStderr = ComputeBootstrapEvStderr(perSampleEdge, bootstrapResamples, modelId);

        return new LiveEdgeSummary(
            ResolvedCount: samples.Count,
            ExpectedValuePips: evSum / divisor,
            WinRate: winSum / divisor,
            MeanProbabilityGap: gapSum / divisor,
            MeanAbsMagnitudePips: magnitudeSum / divisor,
            OldestOutcomeAt: oldestOutcomeAt,
            NewestOutcomeAt: newestOutcomeAt,
            EvStderr: evStderr);
    }

    public MLCalibratedEdgeAlertState ResolveAlertState(
        double expectedValuePips,
        double evStderr,
        double warnExpectedValuePips,
        double regressionGuardK)
    {
        // K-sigma gate: only enter Critical when EV + K·stderr ≤ 0 — i.e. the EV is
        // negative WITH confidence. With stderr = 0 this collapses to the original
        // EV ≤ 0 check, preserving small-sample behaviour when the bootstrap is
        // disabled or has insufficient samples.
        double upperEvBound = expectedValuePips + regressionGuardK * evStderr;
        if (upperEvBound <= 0.0)
            return MLCalibratedEdgeAlertState.Critical;

        return warnExpectedValuePips > 0.0 && expectedValuePips < warnExpectedValuePips
            ? MLCalibratedEdgeAlertState.Warning
            : MLCalibratedEdgeAlertState.None;
    }

    public MLCalibratedEdgeAlertState ResolveAlertState(double expectedValuePips, double warnExpectedValuePips)
        => ResolveAlertState(expectedValuePips, evStderr: 0.0, warnExpectedValuePips, regressionGuardK: 1.0);

    /// <summary>
    /// FNV-1a-mixed deterministic bootstrap resampling on the per-sample signed edge
    /// vector. Returns the sample-stddev of resampled means as a stderr proxy. Two
    /// models with identical sample boundaries get distinct RNG sequences via modelId
    /// in the seed mix.
    /// </summary>
    private static double ComputeBootstrapEvStderr(double[] perSampleEdge, int resamples, long modelId)
    {
        if (resamples <= 0 || perSampleEdge.Length < 2) return 0.0;

        long seed;
        unchecked
        {
            seed = 1469598103934665603L;
            const long fnvPrime = 1099511628211L;
            seed = (seed ^ modelId) * fnvPrime;
            seed = (seed ^ perSampleEdge.Length) * fnvPrime;
            seed = (seed ^ BitConverter.DoubleToInt64Bits(perSampleEdge[0])) * fnvPrime;
            seed = (seed ^ BitConverter.DoubleToInt64Bits(perSampleEdge[^1])) * fnvPrime;
        }
        var rng = new Random(unchecked((int)(seed ^ (seed >> 32))));

        double sum = 0.0;
        double sumSq = 0.0;
        int n = perSampleEdge.Length;
        for (int r = 0; r < resamples; r++)
        {
            double resampleSum = 0.0;
            for (int i = 0; i < n; i++)
            {
                resampleSum += perSampleEdge[rng.Next(n)];
            }
            double mean = resampleSum / n;
            sum += mean;
            sumSq += mean * mean;
        }

        double meanOfMeans = sum / resamples;
        double variance = (sumSq / resamples) - (meanOfMeans * meanOfMeans);
        if (variance < 0 || !double.IsFinite(variance)) variance = 0;
        return Math.Sqrt(variance);
    }

    public AlertSeverity DetermineSeverity(
        MLCalibratedEdgeAlertState alertState,
        LiveEdgeSummary summary,
        double warnExpectedValuePips)
    {
        if (alertState == MLCalibratedEdgeAlertState.Critical)
            return AlertSeverity.Critical;

        if (warnExpectedValuePips > 0.0 && summary.ExpectedValuePips <= warnExpectedValuePips * 0.5)
            return AlertSeverity.High;

        return AlertSeverity.Medium;
    }

    public bool IsEdgeInformative(MLModelPredictionLog log)
    {
        return log.ServedCalibratedProbability.HasValue
            || log.CalibratedProbability.HasValue
            || log.RawProbability.HasValue
            || log.DecisionThresholdUsed.HasValue;
    }

    public bool TryCreateSample(MLModelPredictionLog log, out CalibratedEdgeSample sample)
    {
        sample = default;
        if (!log.ActualDirection.HasValue || !log.ActualMagnitudePips.HasValue)
            return false;

        double threshold = MLFeatureHelper.ResolveLoggedDecisionThreshold(log, 0.5);
        double pBuy = MLFeatureHelper.ResolveLoggedServedBuyProbability(log, threshold);

        if (!double.IsFinite(threshold) || !double.IsFinite(pBuy))
            return false;

        sample = new CalibratedEdgeSample(
            ServedBuyProbability: pBuy,
            DecisionThreshold: threshold,
            ActualBuy: log.ActualDirection.Value == TradeDirection.Buy,
            AbsMagnitudePips: Math.Abs((double)log.ActualMagnitudePips.Value),
            OutcomeAt: log.OutcomeRecordedAt ?? log.PredictedAt,
            PredictedAt: log.PredictedAt);
        return true;
    }
}
