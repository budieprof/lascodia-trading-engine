using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records a single parameter-optimisation job for a <see cref="Strategy"/>.
/// The <c>OptimizationWorker</c> performs a grid search over the strategy's parameter space,
/// backtests each candidate combination, and saves the best-performing parameter set.
/// </summary>
/// <remarks>
/// Optimisation runs are triggered either on a schedule (<see cref="TriggerType.Scheduled"/>)
/// or automatically by the <c>StrategyHealthWorker</c> when a strategy's health score falls
/// below the critical threshold. After a run completes, the best parameters are held in
/// <see cref="BestParametersJson"/> pending human review (<see cref="ApprovedAt"/>).
/// </remarks>
public class OptimizationRun : Entity<long>
{
    /// <summary>Foreign key to the <see cref="Strategy"/> being optimised.</summary>
    public long    StrategyId              { get; set; }

    /// <summary>
    /// Whether this run was started automatically (health degradation, schedule) or
    /// initiated manually via the API.
    /// </summary>
    public TriggerType  TriggerType            { get; set; } = TriggerType.Scheduled;

    /// <summary>Current processing state: Queued → Running → Completed / Failed.</summary>
    public OptimizationRunStatus  Status                 { get; set; } = OptimizationRunStatus.Queued;

    /// <summary>
    /// Total number of parameter combinations evaluated during the grid search.
    /// Populated after the run completes.
    /// </summary>
    public int     Iterations             { get; set; }

    /// <summary>
    /// JSON object containing the parameter values that produced the highest health score
    /// across all tested combinations (e.g. <c>{"FastPeriod":9,"SlowPeriod":21}</c>).
    /// Null until the run completes. Copied to <see cref="Strategy.ParametersJson"/> on approval.
    /// </summary>
    public string? BestParametersJson     { get; set; }

    /// <summary>
    /// Composite health score (0.0–1.0) achieved by the best parameter set.
    /// Combines win rate (40%), profit factor (30%), and inverse max-drawdown (30%).
    /// Null until the run completes.
    /// </summary>
    public decimal? BestHealthScore       { get; set; }

    /// <summary>Sharpe ratio of the best parameter set. Used for EHVI warm-start.</summary>
    public decimal? BestSharpeRatio       { get; set; }

    /// <summary>Max drawdown % of the best parameter set. Used for EHVI warm-start.</summary>
    public decimal? BestMaxDrawdownPct    { get; set; }

    /// <summary>Win rate of the best parameter set. Used for EHVI warm-start.</summary>
    public decimal? BestWinRate           { get; set; }

    /// <summary>
    /// Snapshot of the strategy's parameter JSON at the time the run was queued.
    /// Used to compare baseline performance against the optimised result.
    /// </summary>
    public string? BaselineParametersJson { get; set; }

    /// <summary>
    /// Health score of the strategy using its original (baseline) parameters.
    /// A higher <see cref="BestHealthScore"/> relative to this value confirms improvement.
    /// </summary>
    public decimal? BaselineHealthScore   { get; set; }

    /// <summary>
    /// Immutable JSON snapshot of the optimization config used for this run. Captured at
    /// run start so hot-reloaded EngineConfig changes do not alter an in-flight run.
    /// </summary>
    public string? ConfigSnapshotJson     { get; set; }

    /// <summary>
    /// Structured lineage metadata for the run: deterministic seed, candle window,
    /// regime context, surrogate kind, and other diagnostics needed for reproducibility.
    /// </summary>
    public string? RunMetadataJson        { get; set; }

    /// <summary>
    /// JSON snapshot of the top-N evaluated candidates persisted periodically during the
    /// TPE/GP search. Enables crash recovery — if the worker restarts mid-run, it can
    /// resume from these intermediate results rather than re-evaluating from scratch.
    /// </summary>
    public string? IntermediateResultsJson { get; set; }

    /// <summary>
    /// Version of the serialized checkpoint payload stored in <see cref="IntermediateResultsJson"/>.
    /// Used to safely reject incompatible checkpoint formats after code upgrades.
    /// </summary>
    public int     CheckpointVersion      { get; set; }

    /// <summary>
    /// Structured approval or manual-review report for the final selected candidate.
    /// Stores pass/fail gates and key metrics in machine-readable form.
    /// </summary>
    public string? ApprovalReportJson     { get; set; }

    /// <summary>
    /// Deterministic seed used for all stochastic parts of the optimization run. Persisted
    /// so resumed or audited runs are reproducible.
    /// </summary>
    public int     DeterministicSeed      { get; set; }

    /// <summary>
    /// Error details if the run failed. Null on successful completion.
    /// </summary>
    public string? ErrorMessage           { get; set; }

    /// <summary>
    /// Categorises the failure reason for targeted retry strategies. Timeout failures
    /// get extended timeouts on retry; data quality waits longer for new data;
    /// config errors are never retried.
    /// </summary>
    public OptimizationFailureCategory? FailureCategory { get; set; }

    /// <summary>
    /// Number of times this run has been retried after transient failures (DB timeouts, OOM).
    /// Incremented each time the worker re-queues a Failed run. Once this reaches the
    /// configured max (<c>Optimization:MaxRetryAttempts</c>), the run stays Failed permanently.
    /// </summary>
    public int     RetryCount             { get; set; }

    /// <summary>
    /// UTC timestamp of the worker's latest heartbeat while holding this run.
    /// Used to distinguish genuinely active runs from stale ones.
    /// </summary>
    public DateTime? LastHeartbeatAt      { get; set; }

    /// <summary>
    /// UTC lease expiration for the worker currently processing this run. If this passes
    /// without a heartbeat update, the run may be safely reclaimed.
    /// </summary>
    public DateTime? ExecutionLeaseExpiresAt { get; set; }

    /// <summary>
    /// Unique ownership token for the current execution lease. Changes every time a worker
    /// claims or reclaims the run so stale workers cannot continue heartbeating or persist
    /// results after losing ownership.
    /// </summary>
    public Guid? ExecutionLeaseToken { get; set; }

    /// <summary>
    /// UTC timestamp before which this run should not be claimed by the worker. Set when
    /// a run is deferred (seasonal blackout, drawdown recovery, regime transition, EA
    /// unavailability, data quality). Prevents the worker from re-evaluating deferred runs
    /// every polling cycle, reducing unnecessary DB churn.
    /// </summary>
    /// <summary>Optimistic concurrency token — auto-incremented by the database on every update.</summary>
    public uint RowVersion { get; set; }

    public DateTime? DeferredUntilUtc     { get; set; }

    /// <summary>UTC timestamp when the run first entered the queue.</summary>
    public DateTime QueuedAt              { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when a worker claimed the run.</summary>
    public DateTime? ClaimedAt            { get; set; }

    /// <summary>UTC timestamp when the execution pipeline started doing real work.</summary>
    public DateTime? ExecutionStartedAt   { get; set; }

    /// <summary>Persisted fine-grained execution stage for progress tracking.</summary>
    public OptimizationExecutionStage ExecutionStage { get; set; } = OptimizationExecutionStage.Queued;

    /// <summary>Human-readable description of the current execution stage.</summary>
    public string? ExecutionStageMessage  { get; set; }

    /// <summary>UTC timestamp when <see cref="ExecutionStage"/> was last updated.</summary>
    public DateTime? ExecutionStageUpdatedAt { get; set; }

    /// <summary>Last operational issue code recorded for the run.</summary>
    public string? LastOperationalIssueCode { get; set; }

    /// <summary>Last operational issue message recorded for the run.</summary>
    public string? LastOperationalIssueMessage { get; set; }

    /// <summary>UTC timestamp when the last operational issue was recorded.</summary>
    public DateTime? LastOperationalIssueAt { get; set; }

    /// <summary>UTC timestamp when this run was queued / the record was created.</summary>
    public DateTime StartedAt             { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when grid search finished (succeeded or failed). Null while running.</summary>
    public DateTime? CompletedAt          { get; set; }

    /// <summary>
    /// UTC timestamp when a human operator approved the optimised parameters and
    /// applied them to the strategy. Null until explicitly approved.
    /// </summary>
    public DateTime? ApprovedAt           { get; set; }

    /// <summary>
    /// UTC timestamp when the worker successfully ensured post-approval validation
    /// follow-up runs (backtest + walk-forward) for this optimization.
    /// </summary>
    public DateTime? ValidationFollowUpsCreatedAt { get; set; }

    /// <summary>
    /// Aggregate outcome of the post-approval validation follow-ups (backtest + walk-forward).
    /// Null until follow-ups are created; updated by the workers when they complete.
    /// </summary>
    public ValidationFollowUpStatus? ValidationFollowUpStatus { get; set; }

    /// <summary>UTC timestamp when the follow-up monitor should next revisit this run.</summary>
    public DateTime? NextFollowUpCheckAt { get; set; }

    /// <summary>UTC timestamp of the latest follow-up monitor check.</summary>
    public DateTime? FollowUpLastCheckedAt { get; set; }

    /// <summary>How many times missing or broken follow-ups have been repaired.</summary>
    public int FollowUpRepairAttempts { get; set; }

    /// <summary>Machine-readable code for the latest follow-up state update.</summary>
    public string? FollowUpLastStatusCode { get; set; }

    /// <summary>Human-readable message for the latest follow-up state update.</summary>
    public string? FollowUpLastStatusMessage { get; set; }

    /// <summary>UTC timestamp when the follow-up status message was last updated.</summary>
    public DateTime? FollowUpStatusUpdatedAt { get; set; }

    /// <summary>Serialized integration event payload awaiting completion publication.</summary>
    public string? CompletionPublicationPayloadJson { get; set; }

    /// <summary>Current state of completion publication side effects.</summary>
    public OptimizationCompletionPublicationStatus? CompletionPublicationStatus { get; set; }

    /// <summary>Total publication attempts made for the completion payload.</summary>
    public int CompletionPublicationAttempts { get; set; }

    /// <summary>UTC timestamp of the latest completion publication attempt.</summary>
    public DateTime? CompletionPublicationLastAttemptAt { get; set; }

    /// <summary>UTC timestamp when completion publication finally succeeded.</summary>
    public DateTime? CompletionPublicationCompletedAt { get; set; }

    /// <summary>Last error recorded while publishing completion side effects.</summary>
    public string? CompletionPublicationErrorMessage { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted              { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The strategy this optimisation run was executed for.</summary>
    public virtual Strategy Strategy { get; set; } = null!;
}
