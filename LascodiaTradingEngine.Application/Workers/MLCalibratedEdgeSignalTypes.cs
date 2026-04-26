namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// One observed (served-probability, decision-threshold, actual outcome) triple,
/// distilled from an <see cref="LascodiaTradingEngine.Domain.Entities.MLModelPredictionLog"/>
/// into the form <see cref="IMLCalibratedEdgeEvaluator"/> consumes.
/// </summary>
public readonly record struct CalibratedEdgeSample(
    double ServedBuyProbability,
    double DecisionThreshold,
    bool ActualBuy,
    double AbsMagnitudePips,
    DateTime OutcomeAt,
    DateTime PredictedAt);

/// <summary>
/// Per-window live-edge metrics summary returned by
/// <see cref="IMLCalibratedEdgeEvaluator.ComputeSummary"/>. <c>EvStderr</c> is the
/// bootstrap-derived standard error of <c>ExpectedValuePips</c> — the K-sigma gate
/// in <see cref="IMLCalibratedEdgeEvaluator.ResolveAlertState"/> uses it to keep a
/// single noisy sample window from tripping Critical.
/// </summary>
public readonly record struct LiveEdgeSummary(
    int ResolvedCount,
    double ExpectedValuePips,
    double WinRate,
    double MeanProbabilityGap,
    double MeanAbsMagnitudePips,
    DateTime OldestOutcomeAt,
    DateTime NewestOutcomeAt,
    double EvStderr)
{
    public LiveEdgeSummary(
        int ResolvedCount,
        double ExpectedValuePips,
        double WinRate,
        double MeanProbabilityGap,
        double MeanAbsMagnitudePips,
        DateTime OldestOutcomeAt,
        DateTime NewestOutcomeAt)
        : this(
            ResolvedCount,
            ExpectedValuePips,
            WinRate,
            MeanProbabilityGap,
            MeanAbsMagnitudePips,
            OldestOutcomeAt,
            NewestOutcomeAt,
            EvStderr: 0.0)
    {
    }
}
