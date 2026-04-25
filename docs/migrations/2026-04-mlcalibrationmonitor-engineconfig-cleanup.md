# MLCalibrationMonitorWorker — EngineConfig Cleanup

**Date:** 2026-04
**Worker:** `MLCalibrationMonitorWorker`
**Audience:** anyone reading `EngineConfig` keys produced by this worker (dashboards, alert rules, downstream services).

## TL;DR

`MLCalibrationMonitorWorker` previously wrote 12+ `EngineConfig` keys per model per cycle. Time-series fields have been moved to the `MLCalibrationLog` audit table (the single source of truth for historical calibration data). `EngineConfig` now retains only the four current-state keys hot-reload consumers actually read.

If you query any of the deleted keys today, migrate to `MLCalibrationLog`.

## Deleted keys → `MLCalibrationLog` mapping

All deleted keys were of the form `MLCalibration:Model:{ModelId}:<suffix>`. Read the latest row from `MLCalibrationLog` for the model (filter `MLModelId = {id}`, order by `EvaluatedAt DESC`, `LIMIT 1`).

| Deleted EngineConfig suffix                          | Replacement column on `MLCalibrationLog` |
| ---------------------------------------------------- | ---------------------------------------- |
| `:Accuracy`                                          | `Accuracy`                               |
| `:MeanConfidence`                                    | `MeanConfidence`                         |
| `:ResolvedCount`                                     | `ResolvedSampleCount`                    |
| `:TrendDelta`                                        | `TrendDelta`                             |
| `:PreviousEce`                                       | `PreviousEce`                            |
| `:BaselineEce`                                       | `BaselineEce`                            |
| `:BaselineDelta`                                     | `BaselineDelta`                          |
| `MLCalibration:{Symbol}:{Timeframe}:CurrentEce` alias | filter `MLCalibrationLog` by `Symbol`+`Timeframe`, take latest |

The legacy `MLCalibration:{Symbol}:{Timeframe}:*` alias path was never consumed externally as far as we can tell. If you do consume it, switch to `MLCalibrationLog` filtered by `Symbol`+`Timeframe`.

### Example query (replacement for `:CurrentEce` per-model lookup)

```sql
SELECT "CurrentEce", "Accuracy", "MeanConfidence", "ResolvedSampleCount",
       "PreviousEce", "TrendDelta", "BaselineEce", "BaselineDelta",
       "EvaluatedAt"
  FROM "MLCalibrationLog"
 WHERE "MLModelId" = :modelId AND NOT "IsDeleted"
 ORDER BY "EvaluatedAt" DESC
 LIMIT 1;
```

## Retained EngineConfig keys

These four are still written every cycle and are safe to read from hot-reload consumers (dashboards, alert rules):

| Key suffix                  | Type    | Meaning                                                                |
| --------------------------- | ------- | ---------------------------------------------------------------------- |
| `:CurrentEce`               | Decimal | Current live Expected Calibration Error.                               |
| `:CalibrationDegrading`     | Bool    | True when any of {threshold, trend, baseline} alert conditions breach. |
| `:LastEvaluatedAt`          | String  | UTC timestamp of the latest cycle that touched this model.             |
| `:EceStderr`                | Decimal | Bootstrap stderr of `:CurrentEce`.                                     |

## Internal-state keys (not for downstream readers)

The bootstrap-cache scaffolding keys are also written by the worker but are intended for the worker's own restart-recovery logic. They are not part of the public surface — schema may change without notice:

- `:EceStderrComputedAt`
- `:EceStderrModelRowVersion`
- `:Regime:{RegimeName}:EceStderr`
- `:Regime:{RegimeName}:EceStderrComputedAt`
- `:Regime:{RegimeName}:EceStderrModelRowVersion`

If you find yourself wanting to read these, file a ticket — surface a stable view in `MLCalibrationLog` instead.

## Why the change

Two reasons:

1. **`EngineConfig` is not a time-series store.** Every cycle overwrote the previous value, so the "history" was a single point. `MLCalibrationLog` was added explicitly to hold the time-series, with proper indexes on `(MLModelId, EvaluatedAt)`.
2. **Write-amplification.** Twelve upserts per model per cycle, multiplied across hundreds of active models, dominated the worker's DB time and produced row-level lock contention with hot-reload readers. The retained four keys are batched into one round-trip per model alongside the bootstrap-cache specs.

## Rollback

There's no automated rollback — if a downstream consumer breaks, fix the consumer to query `MLCalibrationLog`. Rolling the worker back to the prior version would re-introduce the write-amplification problem.
