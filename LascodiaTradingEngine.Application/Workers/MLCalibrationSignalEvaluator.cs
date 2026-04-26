using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Default <see cref="IMLCalibrationSignalEvaluator"/> implementation. Pure-functional —
/// no DI, no logging, no DB access. Independently unit-testable without the worker.
/// </summary>
public sealed class MLCalibrationSignalEvaluator : IMLCalibrationSignalEvaluator
{
    private const int NumBins = 10;
    private const double SevereThresholdMultiplier = 2.0;

    public CalibrationSummary ComputeSummary(
        IReadOnlyList<CalibrationSample> samples,
        int bootstrapResamples,
        DateTime nowUtc,
        double timeDecayHalfLifeDays,
        int minSamplesForTimeDecay,
        double? cachedStderr,
        long modelId)
    {
        // Time decay is auto-disabled below MinSamplesForTimeDecay so the tilt cannot
        // dominate floating-point noise on small samples.
        double effectiveHalfLife = samples.Count >= minSamplesForTimeDecay
            ? timeDecayHalfLifeDays
            : 0.0;

        var binCounts = new double[NumBins];
        var binCorrect = new double[NumBins];
        var binConfidenceSum = new double[NumBins];

        double correctSum = 0.0;
        double confidenceSum = 0.0;
        double totalWeight = 0.0;
        DateTime oldestOutcomeAt = DateTime.MaxValue;
        DateTime newestOutcomeAt = DateTime.MinValue;

        foreach (var sample in samples)
        {
            double weight = ComputeTimeDecayWeight(sample.OutcomeAt, nowUtc, effectiveHalfLife);
            if (!double.IsFinite(weight) || weight <= 0) continue;

            int bin = Math.Clamp((int)(sample.Confidence * NumBins), 0, NumBins - 1);
            binCounts[bin] += weight;
            binConfidenceSum[bin] += sample.Confidence * weight;
            confidenceSum += sample.Confidence * weight;
            totalWeight += weight;

            if (sample.Correct)
            {
                binCorrect[bin] += weight;
                correctSum += weight;
            }

            if (sample.OutcomeAt < oldestOutcomeAt) oldestOutcomeAt = sample.OutcomeAt;
            if (sample.OutcomeAt > newestOutcomeAt) newestOutcomeAt = sample.OutcomeAt;
        }

        double ece = ComputeEceFromBins(binCounts, binCorrect, binConfidenceSum, totalWeight);

        var binAccuracy = new double[NumBins];
        var binMeanConfidence = new double[NumBins];
        var binCountsForAudit = new int[NumBins];
        for (int i = 0; i < NumBins; i++)
        {
            binCountsForAudit[i] = (int)Math.Round(binCounts[i]);
            if (binCounts[i] <= 0) continue;
            binAccuracy[i] = binCorrect[i] / binCounts[i];
            binMeanConfidence[i] = binConfidenceSum[i] / binCounts[i];
        }

        // Bootstrap caching: caller supplies the cached value when fresh; we recompute
        // only when the cache is stale or missing.
        double eceStderr = cachedStderr
            ?? ComputeBootstrapEceStderr(samples, bootstrapResamples, nowUtc, effectiveHalfLife, modelId);

        return new CalibrationSummary(
            ResolvedCount: samples.Count,
            CurrentEce: ece,
            Accuracy: totalWeight > 0 ? correctSum / totalWeight : 0,
            MeanConfidence: totalWeight > 0 ? confidenceSum / totalWeight : 0,
            OldestOutcomeAt: oldestOutcomeAt,
            NewestOutcomeAt: newestOutcomeAt,
            BinCounts: binCountsForAudit,
            BinAccuracy: binAccuracy,
            BinMeanConfidence: binMeanConfidence,
            EceStderr: eceStderr);
    }

    public CalibrationSignals BuildSignals(
        double currentEce,
        double eceStderr,
        double? previousEce,
        double? baselineEce,
        double maxEce,
        double degradationDelta,
        double regressionGuardK)
    {
        double trendDelta = previousEce.HasValue ? currentEce - previousEce.Value : 0.0;
        double baselineDelta = baselineEce.HasValue ? currentEce - baselineEce.Value : 0.0;
        bool thresholdExceeded = maxEce > 0.0 && currentEce > maxEce;

        // Trend signal must clear BOTH the absolute degradation delta AND the K-sigma stderr
        // bar. With non-zero stderr this rejects single-cycle drift inside the noise band.
        // With zero stderr the K-sigma bar collapses to the absolute delta.
        bool trendDeltaExceeded = degradationDelta > 0.0 && previousEce.HasValue && trendDelta > degradationDelta;
        bool trendStderrPasses = trendDelta > regressionGuardK * eceStderr;
        bool trendExceeded = trendDeltaExceeded && trendStderrPasses;

        bool baselineExceeded = degradationDelta > 0.0 && baselineEce.HasValue && baselineDelta > degradationDelta;

        return new CalibrationSignals(
            previousEce,
            baselineEce,
            trendDelta,
            baselineDelta,
            thresholdExceeded,
            trendExceeded,
            baselineExceeded,
            trendStderrPasses);
    }

    public MLCalibrationMonitorAlertState ResolveAlertState(
        double currentEce,
        CalibrationSignals signals,
        double maxEce,
        double degradationDelta)
    {
        if ((maxEce > 0.0 && signals.ThresholdExceeded && currentEce > maxEce * SevereThresholdMultiplier) ||
            (degradationDelta > 0.0 && signals.TrendExceeded && signals.TrendDelta > degradationDelta * SevereThresholdMultiplier) ||
            (degradationDelta > 0.0 && signals.BaselineExceeded && signals.BaselineDelta > degradationDelta * SevereThresholdMultiplier))
        {
            return MLCalibrationMonitorAlertState.Critical;
        }

        return signals.ThresholdExceeded || signals.TrendExceeded || signals.BaselineExceeded
            ? MLCalibrationMonitorAlertState.Warning
            : MLCalibrationMonitorAlertState.None;
    }

    public AlertSeverity DetermineSeverity(
        MLCalibrationMonitorAlertState alertState,
        CalibrationSummary summary,
        CalibrationSignals signals,
        double maxEce,
        double degradationDelta)
    {
        if (alertState == MLCalibrationMonitorAlertState.Critical)
            return AlertSeverity.Critical;

        if ((maxEce > 0.0 && summary.CurrentEce >= maxEce * 1.25) ||
            (degradationDelta > 0.0 && Math.Max(signals.TrendDelta, signals.BaselineDelta) >= degradationDelta * 1.5))
        {
            return AlertSeverity.High;
        }

        return AlertSeverity.Medium;
    }

    private static double ComputeTimeDecayWeight(DateTime outcomeAt, DateTime nowUtc, double halfLifeDays)
    {
        if (halfLifeDays <= 0) return 1.0;
        double ageDays = Math.Max(0, (nowUtc - outcomeAt).TotalDays);
        return Math.Pow(0.5, ageDays / halfLifeDays);
    }

    private static double ComputeEceFromBins(
        double[] binCounts, double[] binCorrect, double[] binConfidenceSum, double total)
    {
        if (total <= 0) return 0.0;
        double ece = 0.0;
        for (int i = 0; i < binCounts.Length; i++)
        {
            if (binCounts[i] <= 0) continue;
            double accuracy = binCorrect[i] / binCounts[i];
            double meanConfidence = binConfidenceSum[i] / binCounts[i];
            ece += (binCounts[i] / total) * Math.Abs(accuracy - meanConfidence);
        }
        return ece;
    }

    private static double ComputeBootstrapEceStderr(
        IReadOnlyList<CalibrationSample> samples,
        int resamples,
        DateTime nowUtc,
        double effectiveHalfLifeDays,
        long modelId)
    {
        if (resamples <= 0 || samples.Count < 2) return 0.0;

        // Deterministic, fixed-mix seed: FNV-1a 64 over (modelId, count, firstTick, lastTick)
        // folded to int. Identical inputs reproduce the same stderr across runs and replicas.
        // Including modelId in the mix prevents collisions between two models that happen to
        // share sample-boundary timestamps.
        long seed;
        unchecked
        {
            seed = 1469598103934665603L;
            const long fnvPrime = 1099511628211L;
            seed = (seed ^ modelId) * fnvPrime;
            seed = (seed ^ samples.Count) * fnvPrime;
            seed = (seed ^ samples[0].OutcomeAt.Ticks) * fnvPrime;
            seed = (seed ^ samples[^1].OutcomeAt.Ticks) * fnvPrime;
        }
        var rng = new Random(unchecked((int)(seed ^ (seed >> 32))));

        var binCounts = new double[NumBins];
        var binCorrect = new double[NumBins];
        var binConfidenceSum = new double[NumBins];

        double sum = 0.0;
        double sumSq = 0.0;

        int n = samples.Count;
        for (int r = 0; r < resamples; r++)
        {
            Array.Clear(binCounts);
            Array.Clear(binCorrect);
            Array.Clear(binConfidenceSum);
            double total = 0.0;

            for (int i = 0; i < n; i++)
            {
                var sample = samples[rng.Next(n)];
                double weight = ComputeTimeDecayWeight(sample.OutcomeAt, nowUtc, effectiveHalfLifeDays);
                if (!double.IsFinite(weight) || weight <= 0) continue;

                int bin = Math.Clamp((int)(sample.Confidence * NumBins), 0, NumBins - 1);
                binCounts[bin] += weight;
                binConfidenceSum[bin] += sample.Confidence * weight;
                total += weight;
                if (sample.Correct) binCorrect[bin] += weight;
            }

            double ece = ComputeEceFromBins(binCounts, binCorrect, binConfidenceSum, total);
            sum += ece;
            sumSq += ece * ece;
        }

        double mean = sum / resamples;
        double variance = (sumSq / resamples) - (mean * mean);
        if (variance < 0 || !double.IsFinite(variance)) variance = 0;
        return Math.Sqrt(variance);
    }
}
