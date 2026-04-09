using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records a walk-forward validation run for a <see cref="Strategy"/>, used to assess
/// whether optimised parameters generalise to unseen data and are not merely over-fitted
/// to the training period.
/// </summary>
/// <remarks>
/// Walk-forward testing works by dividing the total date range into sequential windows,
/// each split into an in-sample (training) segment and an out-of-sample (test) segment.
/// The strategy is optimised on the in-sample data and then evaluated on the out-of-sample
/// data. The process is repeated across all windows, and the results are aggregated to
/// assess consistency.
///
/// <see cref="AverageOutOfSampleScore"/> and <see cref="ScoreConsistency"/> are the key
/// output metrics: a high average score with high consistency indicates a robust strategy;
/// large variance suggests the strategy is highly sensitive to parameter choice.
/// </remarks>
public class WalkForwardRun : Entity<long>
{
    /// <summary>Foreign key to the <see cref="Strategy"/> being validated.</summary>
    public long    StrategyId                   { get; set; }

    /// <summary>The currency pair used in this walk-forward run.</summary>
    public string  Symbol                       { get; set; } = string.Empty;

    /// <summary>The chart timeframe on which candle data is replayed.</summary>
    public Timeframe  Timeframe                    { get; set; } = Timeframe.H1;

    /// <summary>UTC start of the full historical data window to be divided into sub-windows.</summary>
    public DateTime FromDate                    { get; set; }

    /// <summary>UTC end of the full historical data window.</summary>
    public DateTime ToDate                      { get; set; }

    /// <summary>
    /// Number of calendar days in each in-sample (optimisation) sub-window.
    /// Larger values provide more training data per window but reduce the number of windows.
    /// </summary>
    public int     InSampleDays                 { get; set; }

    /// <summary>
    /// Number of calendar days in each out-of-sample (test) sub-window.
    /// This is the data the strategy has never seen during optimisation.
    /// </summary>
    public int     OutOfSampleDays              { get; set; }

    /// <summary>
    /// When true, the walk-forward worker re-optimises strategy parameters on each
    /// in-sample fold before evaluating the out-of-sample segment. This validates whether
    /// the optimisation process itself consistently finds good parameters (true WFA),
    /// rather than testing a single fixed parameter set across windows.
    /// </summary>
    public bool    ReOptimizePerFold              { get; set; }

    /// <summary>Current processing state: Queued → Running → Completed / Failed.</summary>
    public RunStatus  Status                       { get; set; } = RunStatus.Queued;

    /// <summary>
    /// Starting account equity used for each sub-window backtest simulation (e.g. 10 000 USD).
    /// Each window begins fresh with this balance to produce comparable performance metrics.
    /// </summary>
    public decimal InitialBalance               { get; set; }

    /// <summary>
    /// Mean health score across all out-of-sample sub-windows.
    /// Computed as the average of the composite health score (win rate, profit factor, drawdown)
    /// achieved on each window's test segment. Null until the run completes.
    /// </summary>
    public decimal? AverageOutOfSampleScore     { get; set; }

    /// <summary>
    /// Standard deviation or consistency metric of out-of-sample scores across windows.
    /// A value close to 1.0 indicates consistent performance; a value near 0.0 indicates
    /// high variance, suggesting over-fitting. Null until the run completes.
    /// </summary>
    public decimal? ScoreConsistency            { get; set; }

    /// <summary>
    /// JSON array of per-window results, each containing in-sample and out-of-sample metrics.
    /// Null until the run completes. Parsed by the UI to render the walk-forward equity curve.
    /// </summary>
    public string? WindowResultsJson            { get; set; }

    /// <summary>Error details if the run failed. Null on successful completion.</summary>
    public string? ErrorMessage                 { get; set; }

    /// <summary>Structured failure code for retry/recovery decisions. Null on success.</summary>
    public ValidationFailureCode? FailureCode  { get; set; }

    /// <summary>Optional structured failure payload for diagnostics and dead-letter review.</summary>
    public string? FailureDetailsJson          { get; set; }

    /// <summary>Human-readable source of this queued run (Manual, BacktestFollowUp, OptimizationFollowUp, etc.).</summary>
    public ValidationQueueSource QueueSource   { get; set; } = ValidationQueueSource.Manual;

    /// <summary>UTC timestamp when this run row was originally created.</summary>
    public DateTime StartedAt                   { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when this run most recently entered the queue, including retries/recovery.</summary>
    public DateTime QueuedAt                    { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when this queued run next becomes eligible for claiming.</summary>
    public DateTime AvailableAt                 { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when a worker claimed the run.</summary>
    public DateTime? ClaimedAt                  { get; set; }

    /// <summary>Worker instance identifier that currently owns or last owned the run lease.</summary>
    public string? ClaimedByWorkerId           { get; set; }

    /// <summary>UTC timestamp when execution actually began after preflight/loading.</summary>
    public DateTime? ExecutionStartedAt         { get; set; }

    /// <summary>UTC timestamp when the current or most recent execution attempt started.</summary>
    public DateTime? LastAttemptAt             { get; set; }

    /// <summary>UTC timestamp of the worker's latest heartbeat while holding this run.</summary>
    public DateTime? LastHeartbeatAt            { get; set; }

    /// <summary>UTC lease expiration for the worker currently processing this run.</summary>
    public DateTime? ExecutionLeaseExpiresAt    { get; set; }

    /// <summary>Unique ownership token for the current execution lease.</summary>
    public Guid? ExecutionLeaseToken            { get; set; }

    /// <summary>
    /// Queue priority for validation scheduling. Higher values are claimed first.
    /// </summary>
    public int Priority                         { get; set; }

    /// <summary>UTC timestamp when walk-forward processing finished. Null while running.</summary>
    public DateTime? CompletedAt                { get; set; }

    /// <summary>
    /// Optional foreign key to the optimisation run that queued this walk-forward validation.
    /// Enables idempotent follow-up scheduling after auto-approval.
    /// </summary>
    public long?    SourceOptimizationRunId     { get; set; }

    /// <summary>
    /// Optional parameter snapshot pinned to this run at queue time.
    /// Optimization follow-up walk-forward runs use this instead of the mutable live
    /// strategy parameters so they validate the approved candidate itself.
    /// </summary>
    public string?  ParametersSnapshotJson      { get; set; }

    /// <summary>
    /// Serialized transaction-cost model resolved at queue time for deterministic replay.
    /// </summary>
    public string? BacktestOptionsSnapshotJson  { get; set; }

    /// <summary>
    /// Optional idempotency key for queued validation runs. Used to prevent duplicate
    /// queued walk-forward runs for the same candidate in recovery/replay paths.
    /// </summary>
    public string? ValidationQueueKey           { get; set; }

    /// <summary>Number of transient retry attempts already consumed for this run.</summary>
    public int RetryCount                       { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted                    { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The strategy this walk-forward run validates.</summary>
    public virtual Strategy Strategy { get; set; } = null!;
}
