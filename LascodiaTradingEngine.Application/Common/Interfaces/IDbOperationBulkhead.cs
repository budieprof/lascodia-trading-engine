namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// In-process bulkhead that caps concurrent DB-bound operations per logical
/// worker group. Prevents a runaway worker (ML training, backtesting) from
/// exhausting the Npgsql connection pool and starving signal generation.
///
/// <para>
/// Design: one semaphore per named group. Callers acquire a handle before
/// executing their DB work and release it on dispose. Groups and limits are
/// read from <c>EngineConfig</c> at startup; unknown groups default to a
/// configurable fallback capacity so additions to the caller code don't
/// need DI updates.
/// </para>
///
/// <para>
/// Recommended groups:
/// <list type="bullet">
///   <item><c>signal-path</c> — StrategyWorker, SignalOrderBridgeWorker,
///         CreateOrderFromSignal. Defaults to 60 slots.</item>
///   <item><c>ml-training</c> — MLTrainingWorker and friends. Default 60.</item>
///   <item><c>backtesting</c> — BacktestWorker, WalkForwardWorker,
///         OptimizationWorker. Default 40.</item>
///   <item><c>other</c> — unclassified callers. Default 40.</item>
/// </list>
/// </para>
/// </summary>
public interface IDbOperationBulkhead
{
    /// <summary>
    /// Acquires a slot in <paramref name="group"/>. Blocks if the group is
    /// saturated. Returns a handle that MUST be disposed to release the slot.
    /// </summary>
    ValueTask<IDisposable> AcquireAsync(string group, CancellationToken ct = default);

    /// <summary>
    /// Returns the current number of available slots in <paramref name="group"/>
    /// — primarily for test assertions and dashboards.
    /// </summary>
    int AvailableSlots(string group);
}
