using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records ergodicity economics metrics for a model–symbol combination (Rec #519).
/// Ergodicity economics distinguishes ensemble-average growth (non-ergodic) from
/// time-average growth (ergodic). The ergodicity gap drives the adjustment to the naive
/// Kelly fraction, producing a position size that maximises long-run compounded wealth.
/// </summary>
public class MLErgodicityLog : Entity<long>
{
    /// <summary>Foreign key to the <see cref="MLModel"/> these ergodicity metrics describe.</summary>
    public long MLModelId { get; set; }

    /// <summary>Currency pair this ergodicity analysis targets.</summary>
    public required string Symbol { get; set; }

    /// <summary>Ensemble-average growth rate (arithmetic mean of log returns across paths).</summary>
    public decimal EnsembleGrowthRate { get; set; }

    /// <summary>Time-average growth rate (geometric mean of log returns along a single path).</summary>
    public decimal TimeAverageGrowthRate { get; set; }

    /// <summary>Ergodicity gap: ensemble minus time-average growth rate. Positive indicates non-ergodic risk.</summary>
    public decimal ErgodicityGap { get; set; }

    /// <summary>Naive Kelly fraction computed from expected value without ergodicity adjustment.</summary>
    public decimal NaiveKellyFraction { get; set; }

    /// <summary>Kelly fraction adjusted downward by the ergodicity gap to maximise long-run growth.</summary>
    public decimal ErgodicityAdjustedKelly { get; set; }

    /// <summary>Variance of the time-average growth rate, indicating estimation uncertainty.</summary>
    public decimal GrowthRateVariance { get; set; }

    /// <summary>UTC timestamp when these metrics were computed.</summary>
    public DateTime ComputedAt { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
