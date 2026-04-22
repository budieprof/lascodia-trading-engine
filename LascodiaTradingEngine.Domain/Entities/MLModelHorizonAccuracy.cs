using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persists per-horizon direction accuracy for an active ML model, written by
/// <c>MLHorizonAccuracyWorker</c>.
///
/// The three forward-look horizons correspond to the <c>HorizonCorrect3</c>,
/// <c>HorizonCorrect6</c>, and <c>HorizonCorrect12</c> fields on
/// <see cref="MLModelPredictionLog"/>, representing accuracy at 3-bar,
/// 6-bar, and 12-bar look-ahead respectively.
///
/// One active row per (MLModelId, HorizonBars) - upserted on each compute cycle.
/// </summary>
public class MLModelHorizonAccuracy : Entity<long>
{
    /// <summary>Foreign key to the owning <see cref="MLModel"/>.</summary>
    public long      MLModelId          { get; set; }

    /// <summary>Currency pair symbol (e.g. "EURUSD").</summary>
    public string    Symbol             { get; set; } = string.Empty;

    /// <summary>Timeframe this model operates on.</summary>
    public Timeframe Timeframe          { get; set; }

    /// <summary>Number of bars in this forward-look horizon (3, 6, or 12).</summary>
    public int       HorizonBars        { get; set; }

    /// <summary>Total resolved predictions for this horizon bucket.</summary>
    public int       TotalPredictions   { get; set; }

    /// <summary>Number of predictions where the horizon outcome was correct.</summary>
    public int       CorrectPredictions { get; set; }

    /// <summary>CorrectPredictions / TotalPredictions (0.0-1.0).</summary>
    public double    Accuracy           { get; set; }

    /// <summary>
    /// Wilson lower confidence bound for the observed horizon accuracy (0.0-1.0).
    /// Used as the conservative monitoring value when sample counts are small.
    /// </summary>
    public double    AccuracyLowerBound { get; set; }

    /// <summary>Primary 1-bar resolved predictions available in this window.</summary>
    public int       PrimaryTotalPredictions { get; set; }

    /// <summary>Primary 1-bar correct predictions available in this window.</summary>
    public int       PrimaryCorrectPredictions { get; set; }

    /// <summary>Primary 1-bar accuracy for the same look-back window.</summary>
    public double    PrimaryAccuracy { get; set; }

    /// <summary>Gap between primary accuracy and this horizon's accuracy.</summary>
    public double    PrimaryAccuracyGap { get; set; }

    /// <summary>Whether the row has enough current samples to be trusted by consumers.</summary>
    public bool      IsReliable { get; set; } = true;

    /// <summary>Machine-readable compute status (e.g. Computed, InsufficientHorizonSamples).</summary>
    public string    Status { get; set; } = "Computed";

    /// <summary>Start of the look-back window used for this computation.</summary>
    public DateTime  WindowStart        { get; set; }

    /// <summary>UTC timestamp when this row was last computed.</summary>
    public DateTime  ComputedAt         { get; set; }

    /// <summary>Soft-delete flag.</summary>
    public bool      IsDeleted          { get; set; }

    // Navigation
    public MLModel   MLModel            { get; set; } = null!;
}
