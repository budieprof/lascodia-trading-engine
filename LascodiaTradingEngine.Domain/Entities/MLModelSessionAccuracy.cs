using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persists per-trading-session direction accuracy for an <see cref="MLModel"/>.
///
/// <b>Motivation:</b> A model's aggregate accuracy can hide session-specific weaknesses.
/// Forex markets behave very differently across London (trending), New York (high volatility),
/// and Asian (ranging) sessions. A model trained on mixed-session data may be accurate
/// overall but significantly underperform in a specific session. This entity allows
/// <c>MLSignalScorer</c> to apply a per-session confidence scale (or suppress signals
/// entirely) when session accuracy falls below the minimum threshold.
///
/// Written by <see cref="MLSessionAccuracyWorker"/>; one row per (model, session), upserted
/// on each computation cycle.
/// </summary>
public class MLModelSessionAccuracy : Entity<long>
{
    /// <summary>Foreign key to the <see cref="MLModel"/> these stats describe.</summary>
    public long          MLModelId          { get; set; }

    /// <summary>The currency pair this model targets.</summary>
    public string        Symbol             { get; set; } = string.Empty;

    /// <summary>The chart timeframe this model targets.</summary>
    public Timeframe     Timeframe          { get; set; } = Timeframe.H1;

    /// <summary>The trading session this row describes.</summary>
    public TradingSession Session           { get; set; }

    /// <summary>Total resolved predictions evaluated within this session.</summary>
    public int           TotalPredictions   { get; set; }

    /// <summary>Number of those predictions where <c>DirectionCorrect == true</c>.</summary>
    public int           CorrectPredictions { get; set; }

    /// <summary>Direction accuracy within this session (0.0–1.0).</summary>
    public double        Accuracy           { get; set; }

    /// <summary>UTC start of the evaluation window used for this computation.</summary>
    public DateTime      WindowStart        { get; set; }

    /// <summary>UTC timestamp when this row was last computed.</summary>
    public DateTime      ComputedAt         { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool          IsDeleted          { get; set; }

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>The model these session stats belong to.</summary>
    public virtual MLModel MLModel { get; set; } = null!;
}
