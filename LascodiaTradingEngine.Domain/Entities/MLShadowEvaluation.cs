using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Manages a head-to-head evaluation of a challenger <see cref="MLModel"/> against the
/// current champion model on live trade outcomes, before a promotion decision is made.
/// </summary>
/// <remarks>
/// Shadow evaluation implements the champion-challenger pattern for safe ML model deployment:
/// instead of immediately promoting a newly trained model, the challenger scores signals
/// in parallel with the champion (without affecting order routing) and accumulates real-world
/// prediction outcomes. Once <see cref="RequiredTrades"/> outcomes have been recorded,
/// the system compares the models across three dimensions:
/// <list type="bullet">
///   <item><description><b>Direction accuracy</b> — fraction of correct Buy/Sell predictions.</description></item>
///   <item><description><b>Magnitude correlation</b> — Pearson correlation of predicted vs actual pip moves.</description></item>
///   <item><description><b>Brier score</b> — calibration of confidence scores (lower is better).</description></item>
/// </list>
/// The <see cref="PromotionDecision"/> records whether the challenger was promoted to champion,
/// retained as challenger, or retired based on the comparison.
/// </remarks>
public class MLShadowEvaluation : Entity<long>
{
    /// <summary>Foreign key to the new <see cref="MLModel"/> being evaluated as a candidate for promotion.</summary>
    public long    ChallengerModelId              { get; set; }

    /// <summary>Foreign key to the currently active <see cref="MLModel"/> being defended.</summary>
    public long    ChampionModelId                { get; set; }

    /// <summary>The currency pair on which both models are evaluated (e.g. "EURUSD").</summary>
    public string  Symbol                         { get; set; } = string.Empty;

    /// <summary>The chart timeframe on which both models operate.</summary>
    public Timeframe  Timeframe                      { get; set; } = Timeframe.H1;

    /// <summary>
    /// Current state of the shadow evaluation:
    /// <c>Running</c> — accumulating trade outcomes;
    /// <c>Completed</c> — required outcomes reached, decision recorded;
    /// <c>Abandoned</c> — cancelled before completion.
    /// </summary>
    public ShadowEvaluationStatus  Status                         { get; set; } = ShadowEvaluationStatus.Running;

    /// <summary>
    /// Minimum number of closed trade outcomes required before the evaluation can produce
    /// a statistically meaningful promotion decision.
    /// </summary>
    public int     RequiredTrades                 { get; set; }

    /// <summary>Number of closed trade outcomes collected so far.</summary>
    public int     CompletedTrades                { get; set; }

    // ── Champion model metrics ────────────────────────────────────────────────

    /// <summary>Champion model's direction prediction accuracy on the accumulated trades (0.0–1.0).</summary>
    public decimal ChampionDirectionAccuracy      { get; set; }

    /// <summary>
    /// Pearson correlation coefficient between the champion's predicted pip magnitudes
    /// and the actual pip outcomes (−1.0 to +1.0; higher is better).
    /// </summary>
    public decimal ChampionMagnitudeCorrelation   { get; set; }

    /// <summary>
    /// Champion model's Brier score — mean squared error of probability forecasts (0.0–1.0).
    /// Lower values indicate better-calibrated confidence estimates.
    /// </summary>
    public decimal ChampionBrierScore             { get; set; }

    // ── Challenger model metrics ──────────────────────────────────────────────

    /// <summary>Challenger model's direction prediction accuracy on the accumulated trades (0.0–1.0).</summary>
    public decimal ChallengerDirectionAccuracy    { get; set; }

    /// <summary>Pearson correlation of the challenger's predicted vs actual pip magnitudes.</summary>
    public decimal ChallengerMagnitudeCorrelation { get; set; }

    /// <summary>Challenger model's Brier score (lower is better).</summary>
    public decimal ChallengerBrierScore           { get; set; }

    // ── Decision ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The outcome decision after the evaluation completes:
    /// <c>PromoteChallenger</c> — challenger wins and replaces the champion;
    /// <c>RetainChampion</c> — champion outperforms; challenger is retired;
    /// <c>Inconclusive</c> — results are too close to make a statistically confident decision.
    /// Null while the evaluation is still running.
    /// </summary>
    public PromotionDecision?  PromotionDecision   { get; set; }

    /// <summary>
    /// Human-readable explanation of why the promotion decision was made,
    /// including the specific metric comparison that drove the outcome.
    /// </summary>
    public string? DecisionReason      { get; set; }

    /// <summary>UTC timestamp when this shadow evaluation was started.</summary>
    public DateTime StartedAt          { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when the evaluation completed and a decision was recorded. Null while running.</summary>
    public DateTime? CompletedAt       { get; set; }

    /// <summary>
    /// UTC deadline after which a still-Running evaluation is automatically abandoned.
    /// Prevents challenger models from being stuck in evaluation forever when live
    /// trade flow is insufficient to reach <see cref="RequiredTrades"/>.
    /// </summary>
    public DateTime? ExpiresAt         { get; set; }

    /// <summary>
    /// Minimum accuracy margin (0–1) by which the challenger must beat the champion
    /// to trigger an <c>AutoPromote</c> decision. Default 0.02 (2 percentage points).
    /// </summary>
    public decimal PromotionThreshold  { get; set; } = 0.02m;

    // ── Improvement #3: Parallel shadow tournament ────────────────────────

    /// <summary>
    /// Groups related shadow evaluations into a single tournament round.
    /// All evaluations sharing the same <c>TournamentGroupId</c> are resolved
    /// together by <c>MLShadowArbiterWorker</c> — the best challenger across
    /// the group wins, and all others are rejected in one pass.
    /// Null for legacy evaluations created before parallel tournaments were introduced.
    /// </summary>
    public Guid?   TournamentGroupId   { get; set; }

    /// <summary>
    /// Rank of this challenger within its tournament group after evaluation.
    /// 1 = tournament winner (promoted or best performer), 2+ = runners-up.
    /// Null while the evaluation is running or for non-tournament evaluations.
    /// </summary>
    public int?    TournamentRank      { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted           { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The current champion model being defended in this evaluation.</summary>
    public virtual MLModel ChampionModel { get; set; } = null!;

    /// <summary>The challenger model seeking to replace the champion.</summary>
    public virtual MLModel ChallengerModel { get; set; } = null!;

    /// <summary>Per-market-regime accuracy breakdown recorded when the evaluation completes.</summary>
    public virtual ICollection<MLShadowRegimeBreakdown> RegimeBreakdowns { get; set; } = new List<MLShadowRegimeBreakdown>();
}
