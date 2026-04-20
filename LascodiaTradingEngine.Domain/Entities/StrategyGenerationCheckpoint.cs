using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores the durable resume checkpoint for a strategy-generation worker cycle.
/// </summary>
/// <remarks>
/// <para>
/// Strategy generation can be long-running because it screens many symbols, strategy
/// templates, and reserve candidates. This entity keeps the worker's latest restartable
/// progress snapshot so a crash or deployment restart can resume from a coherent
/// boundary instead of re-screening every symbol or duplicating already accepted
/// pending candidates.
/// </para>
/// <para>
/// The current EF-backed store keeps one active row per <see cref="WorkerName"/>.
/// Each save updates that active row with fresh serialized state, and a successful
/// cycle clears the checkpoint by soft-deleting it. Restore code validates both
/// <see cref="CycleDateUtc"/> and <see cref="Fingerprint"/> before trusting the
/// payload, which prevents stale or incompatible screening state from being replayed
/// into a different generation context.
/// </para>
/// </remarks>
public class StrategyGenerationCheckpoint : Entity<long>
{
    /// <summary>
    /// Logical worker that owns this checkpoint. The active row is unique per worker
    /// so multiple generation workers can maintain independent checkpoints if needed.
    /// </summary>
    public string WorkerName { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the generation cycle that last wrote this checkpoint. Used for
    /// operator visibility, health reporting, and correlating checkpoint state with
    /// cycle-run diagnostics.
    /// </summary>
    public string CycleId { get; set; } = string.Empty;

    /// <summary>
    /// UTC calendar date of the cycle represented by <see cref="PayloadJson"/>.
    /// Restore rejects checkpoints from a different date to avoid replaying stale
    /// daily screening progress.
    /// </summary>
    public DateTime CycleDateUtc { get; set; }

    /// <summary>
    /// Compatibility fingerprint for the screening context that produced this
    /// checkpoint. Restore accepts the payload only when the current context produces
    /// the same fingerprint.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Serialized checkpoint state containing completed symbols, budget counters,
    /// pending candidates, currency/regime allocation counters, and correlation-group
    /// occupancy needed to resume screening safely.
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> when serialization had to trim non-critical detail to fit the
    /// checkpoint payload size limit. A trimmed checkpoint remains restart-safe, but
    /// may contain less diagnostic detail than the full in-memory state.
    /// </summary>
    public bool UsedRestartSafeFallback { get; set; }

    /// <summary>
    /// UTC timestamp when this checkpoint row was last saved or cleared.
    /// Health checks use this to report checkpoint age and persistence freshness.
    /// </summary>
    public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Soft-delete flag. A completed generation cycle clears its checkpoint by setting
    /// this flag so the global EF Core query filter excludes it from future restores.
    /// </summary>
    public bool IsDeleted { get; set; }
}
