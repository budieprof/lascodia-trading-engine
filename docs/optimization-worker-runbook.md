# OptimizationWorker Runbook

## Canonical Runtime Path

- `OptimizationWorker` is now the coordinator loop.
- `OptimizationRunProcessor` owns queued-run execution.
- `OptimizationWorker` launches up to `MaxConcurrentRuns` independent processing slots.
- `OptimizationApprovalCoordinator` owns approval, manual-review diagnostics, and follow-up creation.
- `OptimizationRunRecoveryCoordinator` owns stale-run recovery, retry, and dead-letter transitions.

## Timestamp Semantics

- `StartedAt`
  The original row-creation timestamp for the optimization run. It is no longer reused as a queue-ordering or claim-ordering field.
- `QueuedAt`
  The most recent time the run entered the queue, including retries and lease-recovery re-queues. This is the canonical queue-age field.
- `ClaimedAt`
  When a worker slot successfully claimed the run.
- `ExecutionStartedAt`
  When the run actually entered preflight/execution work after the claim. A claimed run may still have this unset if it never reached execution.

## Health Surfaces

- Coordinator health
  Use `OptimizationWorker` / `CoordinatorWorker` for the poll loop itself: cycle success, failure streaks, and coordinator cadence.
- Processing-slot health
  Use `OptimizationExecutionWorker` / `OptimizationWorker` for execution-slot liveness in the worker-health endpoint.
  Use `ActiveProcessingSlots`, `ConfiguredMaxConcurrentRuns`, `ProcessingSlotFailuresLastHour`, and queue-wait percentiles (`QueueWaitP50Ms`, `QueueWaitP95Ms`, `QueueWaitP99Ms`) to understand execution throughput.
  `OldestQueuedRunId`, `OldestQueuedAtUtc`, and `OldestQueuedAgeSeconds` are the starvation indicators to trust when the queue is backing up.
- Config-cache health
  `ConfigRefreshIntervalSeconds` is the config-cache TTL, not the worker poll cadence.
  `LastSuccessfulConfigRefreshAtUtc`, `IsConfigLoadDegraded`, `ConsecutiveConfigLoadFailures`, and `LastConfigLoadFailureAtUtc` are the fields to trust when config loads or DB-backed config refreshes are degrading.

## Primary Signals

- `trading.optimization.checkpoint_restored`
  Confirms a run resumed from persisted search state instead of starting fresh.
- `trading.optimization.checkpoint_save_failures`
  Indicates checkpoint persistence is failing and crash recovery quality is degrading.
- `trading.optimization.lease_reclaims`
  Indicates stale `Running` runs were reclaimed by lease recovery.
- `trading.optimization.duplicate_followups_prevented`
  Indicates duplicate backtest/walk-forward follow-up creation was prevented.
- `trading.optimization.claim_latency_ms`
  Measures latency of the atomic queued-run claim path.
- `trading.optimization.queue_wait_at_claim_ms`
  Measures how long runs waited in queue before claim.
- `trading.optimization.active_processing_slots`
  Tracks observed slot occupancy.
- `trading.optimization.processing_slot_utilization`
  Tracks slot utilization ratio against configured max concurrency.

## Stale Running Run

- Symptom: `OptimizationRun.Status = Running` with `ExecutionLeaseExpiresAt < now()`.
- Expected behavior: the worker re-queues the run automatically on startup and on polling.
- Action:
  Check worker logs for `recovered stale Running run(s)` or `lease reclaims`.
- If repeated:
  Inspect checkpoint save failures and worker shutdown/crash history.

## Checkpoint Restore Failed

- Symptom: a reclaimed run restarts without `checkpoint_restored` incrementing.
- Expected behavior: incompatible or malformed checkpoints are ignored safely.
- Action:
  Inspect `IntermediateResultsJson`, `CheckpointVersion`, and worker logs for surrogate mismatch or checkpoint parse warnings.
- Safe response:
  Re-queue the run; it will execute from a fresh deterministic seed and config snapshot.

## Duplicate Validation Follow-Ups

- Symptom: an approved optimization tries to queue duplicate `BacktestRun` or `WalkForwardRun` rows.
- Expected behavior: filtered unique indexes on `SourceOptimizationRunId` prevent duplicates.
- Action:
  Check for increments on `duplicate_followups_prevented` and confirm only one backtest and one walk-forward row exist for the `OptimizationRunId`.

## Completion Publication Recovery

- Symptom: a run is terminal but `CompletionPublicationStatus` is `Pending` or `Failed`.
- Expected behavior: recovery/replay keeps the completed run intact and retries publication side effects instead of mutating it back into a non-terminal state.
- Action:
  Inspect `CompletionPublicationStatus`, `CompletionPublicationAttempts`, `CompletionPublicationPayloadJson`, and worker logs for replay failures.

## Dead-Lettered Failed Runs

- Symptom: a failed run transitions to `Abandoned`.
- Expected behavior:
  `SearchExhausted`, `ConfigError`, `StrategyRemoved`, and retry-budget exhaustion are marked non-retryable by `OptimizationRunRecoveryCoordinator`.
- Action:
  Inspect `FailureCategory` and `ErrorMessage`.
- Note:
  Config errors are normalized to include an `Invalid configuration:` prefix before the non-retryable marker so they are easier to search operationally.

## Manual Data Review

- If repeated optimization failures coincide with data quality exceptions:
  Inspect candles, regime snapshots, and holiday coverage for the strategy symbol/timeframe.
- If repeated manual-review outcomes occur:
  Inspect `ApprovalReportJson` on the run for the specific failing gates.
- Note:
  Approval diagnostics are now written through a typed report shape. `HasSufficientOutOfSampleData=true` is the signal that approval-grade OOS validation really happened; otherwise failed-candidate scores should be interpreted as in-sample diagnostics only.

## Alerts To Configure

- Alert when `ActiveProcessingSlots` remains `0` while `QueuedRuns > 0` for multiple coordinator cycles.
- Alert when `QueueWaitP95Ms` grows materially faster than slot capacity or when `QueuedRuns` rises while `ProcessingSlotFailuresLastHour` is non-zero.
- Alert when `OldestQueuedAgeSeconds` or `OldestQueuedAtUtc` shows a single run starving far longer than the normal queue-wait distribution.
- Alert when `IsConfigLoadDegraded=true` or `ConsecutiveConfigLoadFailures` continues increasing.
- Alert when stale-queued failures repeat for the same strategy or when `AbandonedRuns` increases unexpectedly.
