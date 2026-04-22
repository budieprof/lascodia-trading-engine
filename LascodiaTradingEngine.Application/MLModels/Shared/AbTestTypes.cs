using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Shared;

/// <summary>
/// Snapshot of a running A/B test between a champion and challenger ML model,
/// including all resolved trade outcomes attributed to each arm.
/// </summary>
public record AbTestState(
    long TestId,
    long ChampionModelId,
    long ChallengerModelId,
    string Symbol,
    Timeframe Timeframe,
    DateTime StartedAtUtc,
    List<AbTestOutcome> ChampionOutcomes,
    List<AbTestOutcome> ChallengerOutcomes);

/// <summary>
/// A single resolved trade outcome attributed to one arm of an A/B test.
/// </summary>
public record AbTestOutcome(
    double Pnl,
    double Magnitude,
    int DurationMinutes,
    DateTime ResolvedAtUtc,
    long? StrategyId = null,
    int? SessionHourUtc = null);

/// <summary>
/// The decision produced by evaluating an A/B test via SPRT on cumulative P&amp;L.
/// </summary>
public enum AbTestDecision
{
    /// <summary>Insufficient evidence to decide — test continues.</summary>
    Inconclusive,

    /// <summary>Challenger model produces statistically significantly better P&amp;L.</summary>
    PromoteChallenger,

    /// <summary>Champion model is equal or better — reject the challenger.</summary>
    KeepChampion,

    /// <summary>The test was ended because one arm became unavailable or invalid.</summary>
    Invalidated
}

/// <summary>
/// Statistical controls used when evaluating a signal-level A/B test.
/// </summary>
public record AbTestEvaluationOptions(
    double Alpha = 0.05,
    double Beta = 0.20,
    double DeltaWinSizeMultiplier = 0.5,
    double? MinimumEffectPnl = null,
    double WinsorizationQuantile = 0.05,
    double MaxCovariateImbalance = 0.35,
    double ImbalanceEvidenceMultiplier = 1.5);

/// <summary>
/// Detailed result of an A/B test evaluation, including per-arm metrics and the SPRT decision.
/// </summary>
public class AbTestResult
{
    public AbTestDecision Decision { get; set; }
    public double ChampionAvgPnl { get; set; }
    public double ChallengerAvgPnl { get; set; }
    public double ChampionSharpe { get; set; }
    public double ChallengerSharpe { get; set; }
    public double SprtLogLikelihoodRatio { get; set; }
    public double CovariateImbalanceScore { get; set; }
    public int ChampionTradeCount { get; set; }
    public int ChallengerTradeCount { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Lightweight in-memory representation of an active A/B test for fast lookup
/// during signal scoring. Keyed by (Symbol, Timeframe).
/// </summary>
public record ActiveAbTestEntry(
    long TestId,
    long ChampionModelId,
    long ChallengerModelId);
