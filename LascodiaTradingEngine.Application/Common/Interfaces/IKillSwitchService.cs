namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Runtime kill switches consulted at every decision point that emits a signal
/// or creates an order. Two scopes:
/// <list type="bullet">
///   <item><b>Global:</b> disables all signal generation and order creation
///         engine-wide. Operators flip this when something catastrophic is
///         detected (data feed outage, systemic risk breach, catastrophic
///         PnL). Sub-second propagation required.</item>
///   <item><b>Per-strategy:</b> disables a single strategy without touching
///         the rest of the fleet. Used when a specific strategy misbehaves
///         (runaway signal burst, blown P&amp;L, suspected curve-fit drift)
///         without a full-engine pause.</item>
/// </list>
///
/// <para>
/// State is persisted in <c>EngineConfig</c> under the keys
/// <c>KillSwitch:Global</c> and <c>KillSwitch:Strategy:{id}</c> so it survives
/// process restarts. Reads are cached in memory with event-driven invalidation
/// on upsert.
/// </para>
/// </summary>
public interface IKillSwitchService
{
    /// <summary>
    /// Returns <c>true</c> when the global kill switch is active. Callers must
    /// treat this as a hard stop: emit no signals, create no orders. Sync-only
    /// so it can be checked inside hot-path parallel loops without awaiting.
    /// </summary>
    ValueTask<bool> IsGlobalKilledAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> when the specific strategy is disabled. Global kill
    /// does NOT imply per-strategy kill — callers should check both.
    /// </summary>
    ValueTask<bool> IsStrategyKilledAsync(long strategyId, CancellationToken ct = default);

    /// <summary>
    /// Writes the global kill-switch state. Persists to <c>EngineConfig</c>
    /// under <c>KillSwitch:Global</c> and records a decision-log entry.
    /// </summary>
    Task SetGlobalAsync(bool enabled, string reason, CancellationToken ct = default);

    /// <summary>
    /// Writes a per-strategy kill-switch state. Persists to <c>EngineConfig</c>
    /// under <c>KillSwitch:Strategy:{id}</c>.
    /// </summary>
    Task SetStrategyAsync(long strategyId, bool enabled, string reason, CancellationToken ct = default);
}
