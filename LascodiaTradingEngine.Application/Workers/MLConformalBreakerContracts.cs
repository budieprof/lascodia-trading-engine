using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Workers;

public interface IMLConformalCoverageEvaluator
{
    ConformalCoverageEvaluation Evaluate(
        IReadOnlyCollection<ConformalObservation> observations,
        ConformalCoverageEvaluationOptions options);
}

public interface IMLConformalPredictionLogReader
{
    Task<IReadOnlyDictionary<long, List<MLModelPredictionLog>>> LoadRecentResolvedLogsByModelAsync(
        DbContext db,
        IReadOnlyCollection<long> modelIds,
        int maxLogs,
        CancellationToken ct);
}

public interface IMLConformalCalibrationReader
{
    Task<IReadOnlyDictionary<long, MLConformalCalibration>> LoadLatestUsableByModelAsync(
        DbContext db,
        IReadOnlyCollection<MLModel> models,
        ConformalCalibrationSelectionOptions options,
        CancellationToken ct);
}

public interface IMLConformalBreakerStateStore
{
    Task<BreakerStateResult> ApplyAsync(
        DbContext db,
        IReadOnlyCollection<BreakerTripCandidate> tripCandidates,
        IReadOnlyCollection<BreakerRecoveryCandidate> recoveryCandidates,
        IReadOnlyCollection<BreakerRefreshCandidate> refreshCandidates,
        CancellationToken ct);
}

public readonly record struct ConformalCalibrationSelectionOptions(
    int MinSamples,
    DateTime NowUtc,
    int MaxCalibrationAgeDays,
    bool RequireCalibrationAfterModelActivation);

public readonly record struct ConformalCoverageEvaluationOptions(
    double TargetCoverage,
    double CoverageTolerance,
    int MinLogs,
    int TriggerRunLength,
    bool UseWilsonCoverageFloor,
    double WilsonConfidenceLevel,
    double StatisticalAlpha);

public readonly record struct ConformalObservation(bool WasCovered, DateTime? OutcomeRecordedAt)
{
    internal ConformalObservation(bool wasCovered)
        : this(wasCovered, null) { }
}

public readonly record struct ConformalCoverageEvaluation(
    int SampleCount,
    int CoveredCount,
    double EmpiricalCoverage,
    int ConsecutivePoorCoverageBars,
    bool ShouldTrip,
    bool HasEnoughSamples,
    bool TrippedByCoverageFloor,
    MLConformalBreakerTripReason TripReason,
    double CoverageLowerBound,
    double CoverageUpperBound,
    double CoveragePValue,
    DateTime? LastEvaluatedOutcomeAt)
{
    internal static ConformalCoverageEvaluation Empty(int sampleCount) =>
        new(sampleCount, 0, 0.0, 0, false, false, false, MLConformalBreakerTripReason.Unknown, 0.0, 1.0, 1.0, null);
}

public readonly record struct BreakerTripCandidate(
    long MLModelId,
    string Symbol,
    Timeframe Timeframe,
    ConformalCoverageEvaluation Evaluation,
    double CoverageThreshold,
    double TargetCoverage,
    int SuspensionBars);

public readonly record struct BreakerRecoveryCandidate(
    long BreakerId,
    long MLModelId,
    string Symbol,
    Timeframe Timeframe,
    ConformalCoverageEvaluation Evaluation);

public readonly record struct BreakerRefreshCandidate(
    long BreakerId,
    long MLModelId,
    string Symbol,
    Timeframe Timeframe,
    ConformalCoverageEvaluation Evaluation,
    double CoverageThreshold,
    double TargetCoverage);

public readonly record struct BreakerStateResult(
    int ExpiredCount,
    int RecoveredCount,
    int RefreshedCount,
    int TrippedCount,
    int DuplicateActiveBreakersDeactivated,
    int AlertsCreated,
    int ActiveBreakers,
    IReadOnlyList<BreakerAlertDispatch> Alerts);

public enum BreakerAlertDispatchKind
{
    Trip,
    Resolve
}

public readonly record struct BreakerAlertDispatch(
    Alert Alert,
    string Message,
    BreakerAlertDispatchKind Kind);

public sealed record MLConformalBreakerAlertPayload(
    long ModelId,
    string Timeframe,
    string Reason,
    double EmpiricalCoverage,
    double TargetCoverage,
    double CoverageLowerBound,
    double CoverageUpperBound,
    double CoveragePValue,
    DateTime? LastEvaluatedOutcomeAt,
    DateTime ResumeAt);
