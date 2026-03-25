using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a directional trading signal produced by a <see cref="Strategy"/> evaluator.
/// A signal is a recommendation to enter the market at a given price with defined risk levels;
/// it must be approved by the risk checker before an <see cref="Order"/> is placed.
/// </summary>
/// <remarks>
/// Lifecycle: <c>Pending</c> → approved/rejected by risk checks → <c>Approved</c>/<c>Rejected</c>.
/// Unacted signals expire automatically via the <c>StrategyWorker</c> expiry sweep when
/// <see cref="ExpiresAt"/> passes without an order being placed.
/// </remarks>
public class TradeSignal : Entity<long>
{
    /// <summary>Foreign key to the <see cref="Strategy"/> that generated this signal.</summary>
    public long    StrategyId         { get; set; }

    /// <summary>The instrument this signal targets (e.g. "EURUSD").</summary>
    public string  Symbol             { get; set; } = string.Empty;

    /// <summary>Recommended trade direction: <c>Buy</c> (Long) or <c>Sell</c> (Short).</summary>
    public TradeDirection  Direction          { get; set; } = TradeDirection.Buy;

    /// <summary>
    /// The price at which the strategy wants to enter the trade.
    /// For market orders this is the live price at signal generation time;
    /// for limit orders it is the desired fill level.
    /// </summary>
    public decimal EntryPrice         { get; set; }

    /// <summary>
    /// Optional stop-loss price level. When the position price crosses this level in the
    /// adverse direction the <c>PositionWorker</c> triggers an automatic closure.
    /// </summary>
    public decimal? StopLoss          { get; set; }

    /// <summary>
    /// Optional take-profit price level. When hit, the position is closed in profit
    /// automatically by the <c>PositionWorker</c>.
    /// </summary>
    public decimal? TakeProfit        { get; set; }

    /// <summary>
    /// Optional partial take-profit price level (closer to entry than <see cref="TakeProfit"/>).
    /// When hit, a portion of the position (see <see cref="PartialClosePercent"/>) is closed
    /// and the remainder trails toward the full <see cref="TakeProfit"/>.
    /// </summary>
    public decimal? PartialTakeProfit { get; set; }

    /// <summary>
    /// Percentage of the position to close at <see cref="PartialTakeProfit"/> (0..1).
    /// Only meaningful when <see cref="PartialTakeProfit"/> is set. Defaults to null.
    /// </summary>
    public decimal? PartialClosePercent { get; set; }

    /// <summary>
    /// Lot size recommended by the strategy's position-sizing logic,
    /// constrained later by the active <see cref="RiskProfile"/>.
    /// </summary>
    public decimal SuggestedLotSize   { get; set; }

    /// <summary>
    /// Strategy confidence score in the range 0.0–1.0.
    /// Higher values indicate stronger signal conviction based on indicator confluence.
    /// </summary>
    public decimal Confidence         { get; set; }

    // ── ML scoring fields — populated by IMLSignalScorer ────────────────────

    /// <summary>
    /// Direction predicted by the active ML model (<c>Buy</c> or <c>Sell</c>).
    /// Null if no ML model was active when the signal was scored.
    /// </summary>
    public TradeDirection?  MLPredictedDirection  { get; set; }

    /// <summary>
    /// Expected price movement magnitude in pips as predicted by the ML model.
    /// Used alongside <see cref="MLConfidenceScore"/> to filter low-quality signals.
    /// </summary>
    public decimal? MLPredictedMagnitude  { get; set; }

    /// <summary>
    /// ML model's confidence in its own prediction, in the range 0.0–1.0.
    /// A low score may cause the risk checker to downsize or reject the signal.
    /// </summary>
    public decimal? MLConfidenceScore     { get; set; }

    /// <summary>Foreign key to the <see cref="MLModel"/> that scored this signal.</summary>
    public long?    MLModelId             { get; set; }

    // ── Status ──────────────────────────────────────────────────────────────

    /// <summary>Current lifecycle state of the signal (Pending, Approved, Rejected, Expired, etc.).</summary>
    public TradeSignalStatus  Status           { get; set; } = TradeSignalStatus.Pending;

    /// <summary>
    /// Human-readable reason why the signal was rejected or could not be actioned.
    /// Populated by the risk checker or the order execution pipeline.
    /// </summary>
    public string? RejectionReason  { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="Order"/> that was placed to act on this signal.
    /// Null until the signal is approved and an order is created.
    /// </summary>
    public long?   OrderId          { get; set; }

    /// <summary>UTC timestamp when this signal was produced by the strategy evaluator.</summary>
    public DateTime GeneratedAt     { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp after which this signal is considered stale and will be expired
    /// by the <c>StrategyWorker</c> expiry sweep if no order has been placed.
    /// </summary>
    public DateTime ExpiresAt       { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted        { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The strategy that generated this signal.</summary>
    public virtual Strategy Strategy { get; set; } = null!;

    /// <summary>The ML model that scored this signal (nullable).</summary>
    public virtual MLModel? MLModel { get; set; }

    /// <summary>Orders that were placed as a result of this signal (typically one).</summary>
    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    /// <summary>ML prediction logs recording what each model predicted for this signal and the actual outcome.</summary>
    public virtual ICollection<MLModelPredictionLog> PredictionLogs { get; set; } = new List<MLModelPredictionLog>();

    /// <summary>Records of account-level (Tier 2) risk check attempts against this signal.</summary>
    public virtual ICollection<SignalAccountAttempt> AccountAttempts { get; set; } = new List<SignalAccountAttempt>();
}
