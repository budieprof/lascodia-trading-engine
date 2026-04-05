namespace LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

/// <summary>
/// Comprehensive risk report for an ML model, aggregating training metrics, live performance,
/// calibration, robustness, drift status, shadow evaluation results, and lifecycle event counts.
/// Used for model governance, audit, and promotion decisions.
/// </summary>
public record ModelRiskReportDto(
    // Model identity
    long ModelId, string Symbol, string Timeframe, string ModelVersion, string LearnerArchitecture,
    // Training metrics
    decimal DirectionAccuracy, decimal? F1Score, decimal? BrierScore, decimal? SharpeRatio,
    int TrainingSamples, decimal? WalkForwardAvgAccuracy, decimal? WalkForwardStdDev,
    // Live performance
    decimal? LiveDirectionAccuracy, int? LiveTotalPredictions, int? LiveActiveDays,
    // Calibration
    decimal? PlattA, decimal? PlattB, decimal? PlattCalibrationDrift,
    // Robustness
    decimal? FragilityScore, string? DatasetHash,
    // Drift status
    bool IsSuppressed, bool IsFallbackChampion,
    // Shadow evaluation (latest)
    string? LatestShadowStatus, decimal? ShadowChallengerAccuracy, decimal? ShadowChampionAccuracy,
    // Lifecycle events count
    int LifecycleEventCount,
    // Generated at
    DateTime GeneratedAt);
