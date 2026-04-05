namespace LascodiaTradingEngine.Application.Services;

/// <summary>Snapshot of tick-level order flow data for ML feature construction.</summary>
public sealed record TickFlowSnapshot(
    decimal TickDelta,
    decimal CurrentSpread,
    decimal SpreadMean,
    decimal SpreadStdDev);

/// <summary>
/// Provides tick-level order flow data (delta, spread stats) for a symbol.
/// Used by the ML feature pipeline to build genuinely orthogonal features
/// from tick data that OHLCV candle-based indicators cannot capture.
/// </summary>
public interface ITickFlowProvider
{
    Task<TickFlowSnapshot?> GetSnapshotAsync(string symbol, DateTime asOf, CancellationToken ct);
}
