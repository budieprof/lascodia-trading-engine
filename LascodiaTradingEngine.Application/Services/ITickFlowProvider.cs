namespace LascodiaTradingEngine.Application.Services;

/// <summary>Snapshot of tick-level order flow data for ML feature construction.</summary>
public sealed record TickFlowSnapshot(
    decimal TickDelta,
    decimal CurrentSpread,
    decimal SpreadMean,
    decimal SpreadStdDev,
    decimal SpreadPercentileRank = 0m,  // [0,1] ECDF rank of CurrentSpread in recent window
    decimal SpreadRelVolatility  = 0m,  // SpreadStdDev / SpreadMean, clamped [0,3]
    decimal TickVolumeImbalance  = 0m,  // Lee-Ready signed-trade flow / total volume, [-1,1]
    // ── V5 microstructure (synthetic DOM proxies, no broker dependency) ──
    /// <summary>Average effective spread = mean(|trade_price − midquote| × 2 / midquote)
    /// across the window. Captures the spread the market actually realises round-trip,
    /// which differs from the quoted spread when prices move during execution.</summary>
    decimal EffectiveSpread      = 0m,
    /// <summary>Amihud illiquidity = mean(|return| / volume). Direct proxy for market impact
    /// per unit of executed volume; rises in thin / fast markets, falls in deep / slow ones.</summary>
    decimal AmihudIlliquidity    = 0m,
    /// <summary>Roll's spread estimator = 2×√(−Cov(Δp_t, Δp_{t−1})) when the autocovariance is
    /// negative (the bid-ask bounce signature). Returns 0 when prices are trending (positive
    /// autocorr) — the estimator is undefined in that regime. López de Prado §16.</summary>
    decimal RollSpreadEstimate   = 0m,
    /// <summary>Variance ratio Var(k-period returns) / (k × Var(1-period returns)). Equals 1.0
    /// for a random walk; deviation from 1.0 indicates serial correlation (predictable price
    /// path). Computed at k=2 against the tick-return series.</summary>
    decimal VarianceRatio        = 1m
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
