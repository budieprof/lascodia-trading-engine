namespace LascodiaTradingEngine.Application.Common.Drift;

/// <summary>
/// Pure-CPU drift signal evaluators used by <c>MLDriftMonitorWorker</c>. Each signal is a
/// stateless function that takes already-aggregated inputs and returns a structured result.
/// Extracting them out of the worker's per-model loop makes each signal individually
/// testable and removes the ~250-line algorithm from the orchestrator.
/// </summary>
public static class DriftSignalDetectors
{
    /// <summary>
    /// Accuracy drift fires when the rolling direction accuracy falls below an absolute
    /// floor (e.g. 50%).
    /// </summary>
    public static AccuracySignal EvaluateAccuracy(int correct, int total, double threshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(total);
        double accuracy = correct / (double)total;
        return new AccuracySignal(accuracy, threshold, accuracy < threshold);
    }

    /// <summary>
    /// Calibration drift fires when the rolling Brier score exceeds the configured maximum.
    /// Brier score is the mean squared error of probability predictions; lower is better.
    /// </summary>
    public static BrierSignal EvaluateBrier(double brierScore, double threshold)
    {
        return new BrierSignal(brierScore, threshold, brierScore > threshold);
    }

    /// <summary>
    /// Ensemble disagreement drift fires when the mean inter-learner standard deviation
    /// across the window exceeds the configured threshold AND the sample size is large
    /// enough to be statistically meaningful.
    /// </summary>
    public static DisagreementSignal EvaluateDisagreement(
        double meanDisagreement, int sampleCount, int minPredictions, double threshold)
    {
        bool sufficientSample = sampleCount >= minPredictions;
        return new DisagreementSignal(
            meanDisagreement,
            sampleCount,
            threshold,
            sufficientSample && meanDisagreement > threshold);
    }

    /// <summary>
    /// Relative degradation drift fires when the rolling live accuracy falls below
    /// <c>trainingAccuracy × degradationRatio</c>. Inactive (returns <see cref="RelativeDegradationSignal.Triggered"/> = false)
    /// when the model has no recorded training accuracy.
    /// </summary>
    public static RelativeDegradationSignal EvaluateRelativeDegradation(
        double accuracy, double? trainingAccuracy, double degradationRatio)
    {
        if (!trainingAccuracy.HasValue || trainingAccuracy.Value <= 0)
            return new RelativeDegradationSignal(accuracy, 0, 0, false);

        double effectiveThreshold = trainingAccuracy.Value * degradationRatio;
        return new RelativeDegradationSignal(
            accuracy,
            trainingAccuracy.Value,
            effectiveThreshold,
            accuracy < effectiveThreshold);
    }

    /// <summary>
    /// Sharpe drift fires when the rolling live (annualized) Sharpe ratio falls below
    /// <c>trainingSharpe × degradationRatio</c>. Requires at least <paramref name="minClosedTrades"/>
    /// resolved trades to compute a stable Sharpe; returns inactive (Triggered=false) below that.
    /// </summary>
    public static SharpeSignal EvaluateSharpe(
        IReadOnlyList<double> pnlReturns,
        double? trainingSharpe,
        double degradationRatio,
        int minClosedTrades)
    {
        ArgumentNullException.ThrowIfNull(pnlReturns);

        if (!trainingSharpe.HasValue || trainingSharpe.Value <= 0)
            return new SharpeSignal(0, 0, 0, pnlReturns.Count, false);
        if (pnlReturns.Count < minClosedTrades)
            return new SharpeSignal(0, trainingSharpe.Value, 0, pnlReturns.Count, false);

        double mean = 0;
        for (int i = 0; i < pnlReturns.Count; i++) mean += pnlReturns[i];
        mean /= pnlReturns.Count;

        double sumSquaredDeviations = 0;
        for (int i = 0; i < pnlReturns.Count; i++)
        {
            double d = pnlReturns[i] - mean;
            sumSquaredDeviations += d * d;
        }
        // Population variance — matches the original implementation.
        double variance = sumSquaredDeviations / pnlReturns.Count;
        double std = Math.Sqrt(variance);
        double liveSharpe = std > 1e-10 ? mean / std * Math.Sqrt(252) : 0;
        double effectiveThreshold = trainingSharpe.Value * degradationRatio;

        return new SharpeSignal(
            liveSharpe,
            trainingSharpe.Value,
            effectiveThreshold,
            pnlReturns.Count,
            liveSharpe < effectiveThreshold);
    }
}

public readonly record struct AccuracySignal(double Accuracy, double Threshold, bool Triggered);
public readonly record struct BrierSignal(double BrierScore, double Threshold, bool Triggered);
public readonly record struct DisagreementSignal(double MeanDisagreement, int SampleCount, double Threshold, bool Triggered);
public readonly record struct RelativeDegradationSignal(double Accuracy, double TrainingAccuracy, double EffectiveThreshold, bool Triggered);
public readonly record struct SharpeSignal(double LiveSharpe, double TrainingSharpe, double EffectiveThreshold, int SampleCount, bool Triggered);
