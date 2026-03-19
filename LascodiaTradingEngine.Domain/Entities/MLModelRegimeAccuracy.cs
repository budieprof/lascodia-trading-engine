using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Per-regime live accuracy snapshot for a deployed <see cref="MLModel"/>.
///
/// One row per <c>(MLModelId, Regime)</c> combination. The row is upserted by
/// <c>MLRegimeAccuracyWorker</c> on every evaluation cycle so it always reflects
/// the most recent rolling window. Consuming workers and operators can compare
/// per-regime accuracy to spot regimes where the model systematically underperforms
/// even when the global accuracy is acceptable.
///
/// <c>MLSignalScorer</c> reads these rows (cached) to gate or scale confidence
/// for the current detected regime.
/// </summary>
public class MLModelRegimeAccuracy : Entity<long>
{
    /// <summary>Foreign key to the <see cref="MLModel"/> this row describes.</summary>
    public long         MLModelId          { get; set; }

    /// <summary>The currency pair the model covers (e.g. "EURUSD").</summary>
    public string       Symbol             { get; set; } = string.Empty;

    /// <summary>The chart timeframe the model covers.</summary>
    public Timeframe    Timeframe          { get; set; } = Timeframe.H1;

    /// <summary>The market regime bucket this row tracks.</summary>
    public MarketRegime Regime             { get; set; }

    /// <summary>Number of resolved prediction logs in the evaluation window for this regime.</summary>
    public int          TotalPredictions   { get; set; }

    /// <summary>Number of those predictions where <c>DirectionCorrect == true</c>.</summary>
    public int          CorrectPredictions { get; set; }

    /// <summary>
    /// Rolling direction accuracy for this regime, 0.0–1.0.
    /// Equals <see cref="CorrectPredictions"/> / <see cref="TotalPredictions"/>.
    /// </summary>
    public double       Accuracy           { get; set; }

    /// <summary>UTC start of the rolling window used to compute these stats.</summary>
    public DateTime     WindowStart        { get; set; }

    /// <summary>UTC timestamp when this row was last computed.</summary>
    public DateTime     ComputedAt         { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool         IsDeleted          { get; set; }

    // ── Navigation ──────────────────────────────────────────────────────────

    public virtual MLModel MLModel { get; set; } = null!;
}
