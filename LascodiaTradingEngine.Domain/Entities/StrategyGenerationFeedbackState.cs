using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores a durable keyed JSON payload for strategy-generation feedback state that
/// must survive worker restarts.
/// </summary>
/// <remarks>
/// <para>
/// The strategy-generation feedback layer uses this entity as a small key/value store
/// for derived state that is expensive to recompute on every cycle. The active row is
/// unique per <see cref="StateKey"/>, and saves update that row in place with a fresh
/// payload and timestamp.
/// </para>
/// <para>
/// The current primary use is the <c>feedback_summary</c> cache, which stores historical
/// survival-rate summaries used to bias future candidate generation toward strategy
/// types, regimes, timeframes, and templates that have aged well. Cache consumers should
/// validate any fingerprint or freshness metadata inside <see cref="PayloadJson"/> before
/// trusting the payload.
/// </para>
/// </remarks>
public class StrategyGenerationFeedbackState : Entity<long>
{
    /// <summary>
    /// Logical key identifying the feedback state payload, such as <c>feedback_summary</c>.
    /// Only one non-deleted row should exist for each key.
    /// </summary>
    public string StateKey { get; set; } = string.Empty;

    /// <summary>
    /// Serialized JSON payload for the keyed feedback state. The schema is owned by the
    /// component that writes the key and may include cache fingerprints, computed rates,
    /// timestamps, or other restart-safe derived state.
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when this state payload was last saved. Consumers can use this for
    /// cache freshness checks or operational diagnostics.
    /// </summary>
    public DateTime LastUpdatedAtUtc { get; set; }

    /// <summary>Soft-delete flag. Filtered out by feedback-state lookups and unique active-key checks.</summary>
    public bool IsDeleted { get; set; }
}
