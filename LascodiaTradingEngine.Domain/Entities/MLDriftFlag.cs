using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Typed runtime drift-flag state per (Symbol, Timeframe, DetectorType). Replaces the
/// earlier pattern of stuffing drift-flag expiry timestamps into <c>EngineConfig</c>
/// rows, which mixed operator config with worker state and made config exports noisy.
/// </summary>
/// <remarks>
/// One row per active or recently-active drift signal. The flag is "active" when
/// <see cref="ExpiresAtUtc"/> is in the future. Drift workers refresh
/// <see cref="ExpiresAtUtc"/> on each detection and clear it (set to a past timestamp)
/// when their next evaluation finds no degradation.
/// </remarks>
public class MLDriftFlag : Entity<long>
{
    /// <summary>The traded instrument the drift flag applies to (e.g. "EURUSD").</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The candle timeframe the drift flag applies to.</summary>
    public Timeframe Timeframe { get; set; } = Timeframe.H1;

    /// <summary>
    /// The detector that raised this flag (e.g. "AdwinDrift", "CusumDrift",
    /// "MultiScaleDrift"). Allows multiple drift workers to coexist on the same
    /// (Symbol, Timeframe) without collision.
    /// </summary>
    public string DetectorType { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp at which this flag becomes inactive. The flag is considered
    /// active when <c>ExpiresAtUtc &gt; UtcNow</c>. Refreshed on each fresh detection
    /// to keep the flag alive.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>UTC timestamp when this flag was first raised.</summary>
    public DateTime FirstDetectedAtUtc { get; set; }

    /// <summary>UTC timestamp when this flag was most recently refreshed.</summary>
    public DateTime LastRefreshedAtUtc { get; set; }

    /// <summary>
    /// Number of consecutive cycles this flag has been refreshed without a clear.
    /// Useful for triage (a flag that has been "active" for 20 consecutive cycles
    /// indicates a different failure mode than one that just fired).
    /// </summary>
    public int ConsecutiveDetections { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
