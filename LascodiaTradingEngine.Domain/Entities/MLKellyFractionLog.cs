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

    /// <summary>Total resolved prediction rows considered before usability filtering.</summary>
    public int TotalResolvedSamples { get; set; }

    /// <summary>Prediction outcomes that had a usable economic return proxy.</summary>
    public int UsableSamples { get; set; }

    /// <summary>Usable outcomes classified as profitable/winning.</summary>
    public int WinCount { get; set; }

    /// <summary>Usable outcomes classified as losing.</summary>
    public int LossCount { get; set; }

    /// <summary>Usable outcomes classified from closed-position P&amp;L rather than prediction-log fallback fields.</summary>
    public int PnlBasedSamples { get; set; }

    /// <summary>
    /// Whether the row is based on enough usable observations to drive suppression or sizing decisions.
    /// Unreliable rows are audit-only and must not be treated as evidence that a model recovered.
    /// </summary>
    public bool IsReliable { get; set; } = true;

    /// <summary>Human-readable computation status, for example "Computed" or "InsufficientUsableSamples".</summary>
    public string Status { get; set; } = "Computed";

    /// <summary>UTC timestamp when this Kelly Criterion calculation was computed.</summary>
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
