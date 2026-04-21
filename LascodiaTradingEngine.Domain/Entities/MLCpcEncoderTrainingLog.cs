using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Audit trail for CPC encoder training attempts. One row is written for each candidate
/// that reaches sequence construction, whether the fitted encoder is promoted, rejected,
/// skipped because another replica won promotion, or skipped by a quality gate.
/// </summary>
public class MLCpcEncoderTrainingLog : Entity<long>
{
    /// <summary>
    /// Currency pair the pretrainer evaluated, matching the candidate <see cref="MLModel.Symbol"/>.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Candle timeframe used to build CPC sequences for this attempt.
    /// </summary>
    public Timeframe Timeframe { get; set; } = Timeframe.H1;

    /// <summary>
    /// Optional regime partition used for training. <c>null</c> means the global encoder path
    /// trained on all available candles rather than a regime-specific subset.
    /// </summary>
    public MarketRegime? Regime { get; set; }

    /// <summary>
    /// Encoder architecture selected by <c>MLCpc:EncoderType</c> for this attempt.
    /// </summary>
    public CpcEncoderType EncoderType { get; set; } = CpcEncoderType.Linear;

    /// <summary>
    /// UTC timestamp when the worker made the promotion/rejection decision.
    /// </summary>
    public DateTime EvaluatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Stable high-level result bucket such as promoted, rejected, skipped, or failed.
    /// Kept as a short string so operational dashboards can group outcomes without
    /// requiring a migration for every new worker branch.
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// Stable machine-readable reason within <see cref="Outcome"/>, for example a quality
    /// gate name, no-improvement decision, insufficient data path, or replica race result.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Existing active encoder compared against this candidate, when one was present.
    /// </summary>
    public long? PriorEncoderId { get; set; }

    /// <summary>
    /// Prior encoder validation InfoNCE loss used as the promotion baseline. Lower is better.
    /// </summary>
    public double? PriorInfoNceLoss { get; set; }

    /// <summary>
    /// Newly inserted <see cref="MLCpcEncoder"/> row when this attempt was promoted.
    /// Remains <c>null</c> for rejected, skipped, and failed attempts.
    /// </summary>
    public long? PromotedEncoderId { get; set; }

    /// <summary>
    /// In-sample InfoNCE loss reported by the pretrainer for the fitted encoder.
    /// <c>null</c> when the attempt did not reach model fitting.
    /// </summary>
    public double? TrainInfoNceLoss { get; set; }

    /// <summary>
    /// Holdout InfoNCE loss used by CPC quality gates and promotion comparison.
    /// <c>null</c> when validation could not be evaluated.
    /// </summary>
    public double? ValidationInfoNceLoss { get; set; }

    /// <summary>
    /// Number of closed candles loaded before any optional regime filtering.
    /// </summary>
    public int CandlesLoaded { get; set; }

    /// <summary>
    /// Number of candles remaining after applying <see cref="Regime"/> filtering.
    /// Equal to <see cref="CandlesLoaded"/> for global encoder attempts.
    /// </summary>
    public int CandlesAfterRegimeFilter { get; set; }

    /// <summary>
    /// Number of sequence windows assigned to the training split.
    /// </summary>
    public int TrainingSequences { get; set; }

    /// <summary>
    /// Number of sequence windows assigned to the validation split.
    /// </summary>
    public int ValidationSequences { get; set; }

    /// <summary>
    /// End-to-end fitting duration in milliseconds, excluding candidate discovery.
    /// </summary>
    public long TrainingDurationMs { get; set; }

    /// <summary>
    /// Versioned JSON payload containing non-indexed context such as sequence length,
    /// stride, validation split, embedding dimension, and other runtime config values.
    /// </summary>
    public string DiagnosticsJson { get; set; } = "{}";

    /// <summary>
    /// Soft-delete flag. Filtered out by the global EF Core query filter.
    /// </summary>
    public bool IsDeleted { get; set; }
}
