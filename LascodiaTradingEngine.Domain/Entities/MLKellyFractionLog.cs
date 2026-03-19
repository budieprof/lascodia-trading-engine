using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records the Kelly Criterion position-sizing fraction for a model,
/// computing the optimal fraction f* = (p*b - q) / b and its practical half-Kelly variant.
/// </summary>
public class MLKellyFractionLog : Entity<long>
{
    /// <summary>FK to the <see cref="MLModel"/> this Kelly calculation belongs to.</summary>
    public long MLModelId { get; set; }

    /// <summary>The currency pair symbol (e.g. "EURUSD").</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The chart timeframe (e.g. "H1", "M15").</summary>
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>Optimal full Kelly fraction f* = (p*b - q) / b.</summary>
    public double KellyFraction { get; set; }

    /// <summary>Practical half-Kelly recommendation: 0.5 * KellyFraction.</summary>
    public double HalfKelly { get; set; }

    /// <summary>Win rate p — fraction of trades that are profitable.</summary>
    public double WinRate { get; set; }

    /// <summary>Win/loss ratio b = mean_win / mean_loss.</summary>
    public double WinLossRatio { get; set; }

    /// <summary>Whether KellyFraction is negative, indicating the model has negative expected value.</summary>
    public bool NegativeEV { get; set; }

    /// <summary>UTC timestamp when this Kelly Criterion calculation was computed.</summary>
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
