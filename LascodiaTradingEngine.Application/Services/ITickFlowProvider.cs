namespace LascodiaTradingEngine.Application.Services;

/// <summary>Snapshot of tick-level order flow data for ML feature construction.</summary>
public sealed record TickFlowSnapshot(
    decimal TickDelta,
    decimal CurrentSpread,
    decimal SpreadMean,
    decimal SpreadStdDev,
    decimal SpreadPercentileRank = 0m,  // [0,1] ECDF rank of CurrentSpread in recent window
    decimal SpreadRelVolatility  = 0m,  // SpreadStdDev / SpreadMean, clamped [0,3]
    decimal TickVolumeImbalance  = 0m   // (up_ticks - down_ticks) / total, clamped [-1,1]
);

/// <summary>
/// Provides tick-level order flow data (delta, spread stats) for a symbol.
/// Used by the ML feature pipeline to build genuinely orthogonal features
/// from tick data that OHLCV candle-based indicators cannot capture.
/// </summary>
public interface ITickFlowProvider
{
    Task<TickFlowSnapshot?> GetSnapshotAsync(string symbol, DateTime asOf, CancellationToken ct);
}
