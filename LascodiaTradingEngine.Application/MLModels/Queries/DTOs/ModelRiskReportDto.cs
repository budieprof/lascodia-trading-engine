namespace LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

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
