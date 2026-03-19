using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records PELT (Pruned Exact Linear Time) change point detection results for a model,
/// identifying structural breaks in the return or feature time series.
/// </summary>
public class MLPeltChangePointLog : Entity<long>
{
    /// <summary>FK to the <see cref="MLModel"/> this change point detection belongs to.</summary>
    public long MLModelId { get; set; }

    /// <summary>The currency pair symbol (e.g. "EURUSD").</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The chart timeframe (e.g. "H1", "M15").</summary>
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>Total number of change points detected in the time series.</summary>
    public int ChangePointCount { get; set; }

    /// <summary>JSON array of time indices at which change points were detected.</summary>
    public string ChangePointIndicesJson { get; set; } = string.Empty;

    /// <summary>PELT penalty parameter used (typically BIC-based).</summary>
    public double Penalty { get; set; }

    /// <summary>Minimised total segmentation cost after change point detection.</summary>
    public double TotalCost { get; set; }

    /// <summary>UTC timestamp when this change point detection was computed.</summary>
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
