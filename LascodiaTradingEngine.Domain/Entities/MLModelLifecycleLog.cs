using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Immutable audit log of ML model lifecycle transitions: promotion, rollback, suppression,
/// retirement. Stores the explicit predecessor chain for multi-hop rollback support and
/// provides a complete history for model risk management (MRM) reporting per SR 11-7.
/// </summary>
public class MLModelLifecycleLog : Entity<long>
{
    /// <summary>FK to the model this event applies to.</summary>
    public long MLModelId { get; set; }

    /// <summary>Lifecycle event type.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Model status before the event. Null for the initial lifecycle event (e.g. first training).</summary>
    public MLModelStatus? PreviousStatus { get; set; }

    /// <summary>Model status after the event.</summary>
    public MLModelStatus NewStatus { get; set; }

    /// <summary>FK to the predecessor champion (for promotion/rollback chain).</summary>
    public long? PreviousChampionModelId { get; set; }

    /// <summary>FK to the shadow evaluation that drove the promotion decision (if applicable).</summary>
    public long? ShadowEvaluationId { get; set; }

    /// <summary>Human-readable reason for the transition.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Account ID that triggered the event (null for automated transitions).</summary>
    public long? TriggeredByAccountId { get; set; }

    /// <summary>Direction accuracy at time of transition (snapshot for audit).</summary>
    public decimal? DirectionAccuracyAtTransition { get; set; }

    /// <summary>Live accuracy at time of transition.</summary>
    public decimal? LiveAccuracyAtTransition { get; set; }

    /// <summary>Brier score at time of transition.</summary>
    public decimal? BrierScoreAtTransition { get; set; }

    /// <summary>When the lifecycle event occurred.</summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public virtual MLModel MLModel { get; set; } = null!;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
