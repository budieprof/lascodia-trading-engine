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
    double StatisticalAlpha,
    int TimeDecayHalfLifeDays,
    int BootstrapResamples,
    double RegressionGuardK,
    long ModelId,
    DateTime NowUtc);

public readonly record struct ConformalObservation(
    bool WasCovered,
    DateTime? OutcomeRecordedAt,
    global::LascodiaTradingEngine.Domain.Enums.MarketRegime? Regime)
{
    public ConformalObservation(bool wasCovered, DateTime? outcomeRecordedAt)
        : this(wasCovered, outcomeRecordedAt, null) { }

    internal ConformalObservation(bool wasCovered)
        : this(wasCovered, null, null) { }
}

/// <summary>Per-regime coverage breakdown — diagnostic-only, doesn't affect trip semantics.</summary>
public readonly record struct RegimeCoverageBreakdown(
    int SampleCount,
    int CoveredCount,
    double EmpiricalCoverage);

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
    DateTime? LastEvaluatedOutcomeAt,
    double TimeDecayWeightedCoverage,
    double CoverageStderr,
    int BootstrapResamplesUsed,
    IReadOnlyDictionary<global::LascodiaTradingEngine.Domain.Enums.MarketRegime, RegimeCoverageBreakdown> RegimeBreakdown)
{
    internal static ConformalCoverageEvaluation Empty(int sampleCount) =>
        new(sampleCount, 0, 0.0, 0, false, false, false, MLConformalBreakerTripReason.Unknown,
            0.0, 1.0, 1.0, null, 0.0, 0.0, 0,
            new Dictionary<global::LascodiaTradingEngine.Domain.Enums.MarketRegime, RegimeCoverageBreakdown>());

    /// <summary>
    /// The regime with the lowest empirical coverage in the evaluation window. Returns
    /// <c>null</c> when per-regime decomposition is disabled or no observation carried a
    /// regime tag. Used by the worker to enrich trip alerts so operators can see which
    /// market regime is driving the coverage failure.
    /// </summary>
    public (global::LascodiaTradingEngine.Domain.Enums.MarketRegime Regime, RegimeCoverageBreakdown Breakdown)? WorstRegime()
    {
        if (RegimeBreakdown.Count == 0) return null;
        global::LascodiaTradingEngine.Domain.Enums.MarketRegime worst = default;
        RegimeCoverageBreakdown worstBreakdown = default;
        bool any = false;
        foreach (var (regime, breakdown) in RegimeBreakdown)
        {
            if (!any || breakdown.EmpiricalCoverage < worstBreakdown.EmpiricalCoverage)
            {
                worst = regime;
                worstBreakdown = breakdown;
                any = true;
            }
        }
        return any ? (worst, worstBreakdown) : null;
    }
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
    DateTime ResumeAt,
    string? WorstRegime = null,
    double? WorstRegimeCoverage = null,
    int? WorstRegimeSampleCount = null);
