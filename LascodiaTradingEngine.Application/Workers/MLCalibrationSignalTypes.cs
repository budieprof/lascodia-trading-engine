namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// One observed (confidence, outcome) pair used by the calibration evaluator.
/// </summary>
public readonly record struct CalibrationSample(
    double Confidence,
    bool Correct,
    DateTime OutcomeAt,
    DateTime PredictedAt);

/// <summary>
/// Per-window calibration metrics summary returned by
/// <see cref="IMLCalibrationSignalEvaluator.ComputeSummary"/>.
/// </summary>
public readonly record struct CalibrationSummary(
    int ResolvedCount,
    double CurrentEce,
    double Accuracy,
    double MeanConfidence,
    DateTime OldestOutcomeAt,
    DateTime NewestOutcomeAt,
    int[] BinCounts,
    double[] BinAccuracy,
    double[] BinMeanConfidence,
    double EceStderr);

/// <summary>
/// Three discriminated calibration signals derived from a summary:
/// <list type="bullet">
///   <item>Threshold — current ECE exceeds the absolute ceiling.</item>
///   <item>Trend — current ECE is degrading vs. recent past, gated by K-sigma stderr.</item>
///   <item>Baseline — current ECE is degrading vs. training-time baseline.</item>
/// </list>
/// </summary>
public readonly record struct CalibrationSignals(
    double? PreviousEce,
    double? BaselineEce,
    double TrendDelta,
    double BaselineDelta,
    bool ThresholdExceeded,
    bool TrendExceeded,
    bool BaselineExceeded,
    bool TrendStderrPasses);
