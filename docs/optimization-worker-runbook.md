# OptimizationWorker Runbook

## Canonical Runtime Path

- `OptimizationWorker` is now the host/orchestrator only.
- `OptimizationRunProcessor` owns queued-run execution.
- `OptimizationApprovalCoordinator` owns approval, manual-review diagnostics, and follow-up creation.
- `OptimizationRunRecoveryCoordinator` owns stale-run recovery, retry, and dead-letter transitions.

## Primary Signals

- `trading.optimization.checkpoint_restored`
  Confirms a run resumed from persisted search state instead of starting fresh.
- `trading.optimization.checkpoint_save_failures`
  Indicates checkpoint persistence is failing and crash recovery quality is degrading.
- `trading.optimization.lease_reclaims`
  Indicates stale `Running` runs were reclaimed by lease recovery.
- `trading.optimization.duplicate_followups_prevented`
  Indicates duplicate backtest/walk-forward follow-up creation was prevented.

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
