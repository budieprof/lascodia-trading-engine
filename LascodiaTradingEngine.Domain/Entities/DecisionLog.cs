using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Immutable audit trail entry that records every significant automated decision made by
/// the trading engine — signal approvals, order submissions, strategy pauses, ML model
/// promotions, broker switches, and more.
/// </summary>
/// <remarks>
/// This entity is intentionally append-only: it has no <c>IsDeleted</c> flag and no
/// update paths in the application layer. Every write is a new row created via
/// <c>LogDecisionCommand</c>. The absence of soft-delete ensures the audit log cannot be
/// tampered with after the fact.
///
/// Populated by all workers and services in the application layer whenever a consequential
/// state change occurs. The <c>EntityType</c> + <c>EntityId</c> pair forms an index key
/// that allows all decisions for a specific entity instance to be retrieved quickly.
/// </remarks>
public class DecisionLog : Entity<long>
{
    /// <summary>
    /// The domain entity type this decision relates to.
    /// Supported values: "TradeSignal", "Order", "Strategy", "MLModel", "MLTrainingRun",
    /// "OptimizationRun", "Position", "Broker".
    /// </summary>
    public string  EntityType    { get; set; } = string.Empty;

    /// <summary>
    /// The primary key of the specific entity instance this decision relates to.
    /// Must be &gt; 0 (validated by <c>LogDecisionCommandValidator</c>).
    /// </summary>
    public long    EntityId      { get; set; }

    /// <summary>
    /// A short code identifying the type of decision recorded.
    /// Examples: "SignalApproved", "SignalRejected", "OrderSubmitted", "AutoPause",
    /// "ModelPromotion", "BrokerSwitch", "StopLossClosure". Max 50 characters.
    /// </summary>
    public string  DecisionType  { get; set; } = string.Empty;

    /// <summary>
    /// The result of the decision.
    /// Examples: "Approved", "Rejected", "Blocked", "Paused", "Switched", "Closed", "Completed".
    /// Max 50 characters.
    /// </summary>
    public string  Outcome       { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable explanation of why this decision was made.
    /// Should include quantitative context such as prices, scores, or thresholds crossed.
    /// e.g. "Stop loss hit at 1.0821 (SL level: 1.0820), EURUSD Long".
    /// </summary>
    public string  Reason        { get; set; } = string.Empty;

    /// <summary>
    /// Optional JSON blob carrying additional structured context for this decision
    /// (e.g. the full signal parameters, candle values at decision time, or risk metrics).
    /// Intended for debugging and post-trade analysis; not used by the engine at runtime.
    /// </summary>
    public string? ContextJson   { get; set; }

    /// <summary>
    /// The component or worker that recorded this decision.
    /// Examples: "StrategyWorker", "PositionWorker", "RiskChecker", "MLTrainingWorker",
    /// "BrokerFailoverService". Max 50 characters.
    /// </summary>
    public string  Source        { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this audit record was created. Set once; never updated.</summary>
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;

    // Intentionally no IsDeleted — this entity is immutable and must not be soft-deleted.
}
