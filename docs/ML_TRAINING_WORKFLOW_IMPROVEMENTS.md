# ML Training Workflow Improvements — Implementation Blueprint

> **Date:** 2026-03-30
> **Scope:** 12 production-grade improvements to the ML trainer selection, training, evaluation, scoring, and monitoring pipeline.
> **Status:** Implemented & reviewed (3 review rounds, all bugs fixed).

---

## Table of Contents

1. [Improvement Overview](#improvement-overview)
2. [Architecture Diagram](#architecture-diagram)
3. [Improvement #1 — Ensemble Scoring Committee](#improvement-1--ensemble-scoring-committee)
4. [Improvement #2 — Drift-Aware Trainer Selection](#improvement-2--drift-aware-trainer-selection)
5. [Improvement #3 — Parallel Shadow Tournament](#improvement-3--parallel-shadow-tournament)
6. [Improvement #4 — Graduated Sample Gates](#improvement-4--graduated-sample-gates)
7. [Improvement #5 — Regime-Transition Hot-Swap](#improvement-5--regime-transition-hot-swap)
8. [Improvement #6 — Cross-Architecture Feature Importance Consensus](#improvement-6--cross-architecture-feature-importance-consensus)
9. [Improvement #7 — Steeper Temporal Decay on UCB1 Scores](#improvement-7--steeper-temporal-decay-on-ucb1-scores)
10. [Improvement #8 — Abstention-Aware Trainer Ranking](#improvement-8--abstention-aware-trainer-ranking)
11. [Improvement #9 — Training Budget Allocation (Two-Lane Priority Queue)](#improvement-9--training-budget-allocation-two-lane-priority-queue)
12. [Improvement #10 — Champion Tenure Tracking](#improvement-10--champion-tenure-tracking)
13. [Improvement #11 — Correlated Failure Detection Across Symbols](#improvement-11--correlated-failure-detection-across-symbols)
14. [Improvement #12 — Shadow Evaluation Outcomes → Regime Affinity](#improvement-12--shadow-evaluation-outcomes--regime-affinity)
15. [Configuration Reference](#configuration-reference)
16. [Schema Changes](#schema-changes)
17. [File Inventory](#file-inventory)
18. [Backward Compatibility](#backward-compatibility)

---

## Improvement Overview

| # | Improvement | Category | Gated By | Default |
|---|---|---|---|---|
| 1 | Ensemble scoring committee | Inference | `MLScoring:EnableCommittee` | `false` |
| 2 | Drift-aware trainer selection | Selection | Always active | — |
| 3 | Parallel shadow tournament | Evaluation | Always active | — |
| 4 | Graduated sample gates | Selection | `MLTraining:UseGraduatedSampleGate` | `false` |
| 5 | Regime-transition hot-swap | Promotion | `MLRegime:EnableHotSwap` | `false` |
| 6 | Feature importance consensus | Monitoring | Always active | — |
| 7 | Steeper temporal decay | Selection | `MLTraining:SteepDecayMultiplier` | `1.0` (no-op) |
| 8 | Abstention-aware ranking | Selection | `MLTraining:WeightAbstention` | `0.0` (disabled) |
| 9 | Two-lane priority queue | Training | Always active | — |
| 10 | Champion tenure tracking | Monitoring | `MLTraining:ProactiveChallengeEnabled` | `false` |
| 11 | Correlated failure detection | Monitoring | Always active | — |
| 12 | Shadow regime affinity feedback | Selection | `MLTraining:ShadowRegimeAffinityWeight` | `0.30` |

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                     ML TRAINING WORKFLOW (POST-IMPROVEMENT)                  │
└─────────────────────────────────────────────────────────────────────────────┘

SELECTION (TrainerSelector)
  ├─ #7  Configurable temporal decay (RecencyHalfLifeDays, SteepDecayMultiplier)
  ├─ #4  Graduated sample gates (continuous discount vs hard tiers)
  ├─ #8  Abstention-aware composite score (WeightAbstention)
  ├─ #2  Drift-aware architecture boost (ComputeDriftAwareBoostsAsync)
  └─ #12 Shadow regime affinity blended into regime affinity map
       ↓
TRAINING (MLTrainingWorker)
  ├─ #9  Priority-ordered claiming (Priority field, systemic pause check)
  └─ #3  Tournament group ID assigned to shadow evaluations
       ↓
SHADOW EVALUATION (MLShadowArbiterWorker)
  ├─ #3  Parallel tournament resolution (TournamentGroupId, rank all challengers)
  └─ #12 Cache invalidation on promotion (TrainerSelector affinity refresh)
       ↓
SCORING (MLSignalScorer)
  └─ #1  Ensemble committee blending (multiple models from different families)
       ↓
MONITORING
  ├─ #10 Champion tenure tracking (MLDriftMonitorWorker.CheckChampionTenureAsync)
  ├─ #2  Drift metadata tagging (DriftTriggerType, DriftMetadataJson)
  ├─ #5  Regime hot-swap (MLRegimeHotSwapWorker — reactivate superseded models)
  ├─ #6  Feature consensus (MLFeatureConsensusWorker — cross-architecture agreement)
  └─ #11 Correlated failure (MLCorrelatedFailureWorker — systemic pause)
```

---

## Improvement #1 — Ensemble Scoring Committee

### Problem
Only one active model serves signals per symbol/timeframe. All trainer diversity is wasted at inference time.

### Solution
Maintain a scoring committee of up to 3 models from different `ModelFamily` groups. Blend their calibrated probabilities using accuracy-weighted averaging.

### Files

| File | Change |
|---|---|
| `Application/Services/ML/EnsembleCommitteeBlender.cs` | **New.** Static `Blend()` method: accuracy-weighted probability averaging, committee disagreement (std of probabilities), Kelly fraction from blended probability. Handles null calibrated probabilities gracefully (returns null direction). |
| `Application/Services/MLModelResolver.cs` | **Added** `ResolveCommitteeModelsAsync()`: loads up to N active models from different `ModelFamily` groups. Uses `FamilyOf()` mapping (mirrors `TrainerSelector.ModelFamily`). |
| `Application/Common/Interfaces/IMLSignalScorer.cs` | **Added** `CommitteeModelIdsJson` and `CommitteeDisagreement` fields to `MLScoreResult` record. |
| `Domain/Entities/MLModelPredictionLog.cs` | **Added** `CommitteeModelIdsJson` (string?) and `CommitteeDisagreement` (decimal?). |

### Key Design Decisions
- Committee members are drawn from **different model families** (e.g., TreeBoosting, BaggedEnsemble, Transformer) to maximise prediction diversity.
- When all committee members have null calibrated probabilities, `direction = null` and `confidence = 0` — prevents acting on meaningless data.
- Gated behind `MLScoring:EnableCommittee` (default `false`).

### Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `MLScoring:EnableCommittee` | bool | `false` | Enable/disable committee scoring |
| `MLScoring:MaxCommitteeSize` | int | `3` | Maximum committee members |

---

## Improvement #2 — Drift-Aware Trainer Selection

### Problem
When drift triggers retraining, the new run goes through the same selector pipeline. The *type* of drift isn't considered — covariate shift (same architecture, fresh data) vs accuracy drift (try a different architecture) are treated identically.

### Solution
Tag each drift-triggered `MLTrainingRun` with the specific drift criterion and metrics. `TrainerSelector` queries these tags to boost architectures that historically recovered well from similar drift events.

### Files

| File | Change |
|---|---|
| `Domain/Entities/MLTrainingRun.cs` | **Added** `DriftTriggerType` (string?) and `DriftMetadataJson` (string?). |
| `Application/Workers/MLDriftMonitorWorker.cs` | **Modified.** Populates `DriftTriggerType` (one of `AccuracyDrift`, `CalibrationDrift`, `DisagreementDrift`, `SharpeDrift`, `RelativeDegradation`, `MultiSignal`) and `DriftMetadataJson` with the specific metrics that fired. Uses signal-count logic: `activeSignals == 1` → single type, else `MultiSignal`. |
| `Application/Workers/MLCovariateShiftWorker.cs` | **Modified.** Sets `DriftTriggerType = "CovariateShift"` and `DriftMetadataJson` with `maxPsi`, `psiFeature`, `msz`. |
| `Application/Services/ML/TrainerSelector.cs` | **Added** `ComputeDriftAwareBoostsAsync()`: loads completed drift-triggered runs, computes per-architecture average post-drift accuracy, boosts architectures above the drift-recovery average by up to 15%. |

### Flow
```
Drift detected → DriftTriggerType + DriftMetadataJson set on MLTrainingRun
                 → TrainerSelector.ComputeDriftAwareBoostsAsync() queries these tags
                 → Architectures that recovered well from similar drift get UCB1 boost
```

---

## Improvement #3 — Parallel Shadow Tournament

### Problem
Shadow evaluation is sequential — champion vs one challenger at a time. On low-volume pairs, this can take weeks.

### Solution
Group related shadow evaluations into tournaments via a shared `TournamentGroupId`. When all members complete, `MLShadowArbiterWorker` ranks them and promotes the best.

### Files

| File | Change |
|---|---|
| `Domain/Entities/MLShadowEvaluation.cs` | **Added** `TournamentGroupId` (Guid?) and `TournamentRank` (int?). |
| `Application/Workers/MLTrainingWorker.cs` | **Modified.** Assigns `DeterministicTournamentGroup(championId)` to shadow evaluations. Uses champion ID + hour-truncated UTC timestamp for deterministic grouping — all challengers for the same champion within the same hour share one tournament. |
| `Application/Workers/MLShadowArbiterWorker.cs` | **Modified.** Tournament resolution block: checks if siblings are still running/processing → defers if so. When all complete, ranks only individually `AutoPromoted` challengers by `ChallengerDirectionAccuracy`. Reloads current shadow from write context to get fresh accuracy (avoids stale `AsNoTracking` data). Retires non-winners. Falls back to `FlaggedForReview` if no promotable candidates survive. |

### Key Design Decisions
- `DeterministicTournamentGroup` uses `championModelId + hourKey.Ticks` → same GUID for all shadows created for the same champion in the same hour.
- Only individually `AutoPromoted` challengers can win the tournament — prevents bypassing per-regime accuracy guards.
- Current shadow is reloaded from `writeCtx` before ranking to get freshly persisted `ChallengerDirectionAccuracy`.
- `TournamentRank` persisted via `ExecuteUpdateAsync` (handles both tracked and untracked entities).

---

## Improvement #4 — Graduated Sample Gates

### Problem
The three-tier gate (Simple: always, Standard: ≥500, Deep: ≥2000) is a cliff — at 499 samples, FT-Transformer is ineligible; at 500, fully eligible.

### Solution
Replace hard pass/fail with a continuous confidence discount: `min(1.0, sqrt(sampleCount / required))`. Hard floor at 20% of required (below which the architecture is still rejected outright).

### Files

| File | Change |
|---|---|
| `Application/Services/ML/TrainerSelector.cs` | **Added** `PickBestWithGraduatedGate()` and `ComputeSampleDiscount()`. The graduated gate multiplies each candidate's UCB1 score by the sample discount, then picks the highest adjusted score. Hard gate path preserved as default; graduated gate activated via config. |

### Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `MLTraining:UseGraduatedSampleGate` | bool | `false` | Enable graduated gates (accepts `"true"`, `"1"`) |
| `MLTraining:SampleGateHardFloorFraction` | double | `0.20` | Below this fraction of required samples, architecture is rejected |

---

## Improvement #5 — Regime-Transition Hot-Swap

### Problem
When the market regime changes, the active model (optimised for the old regime) continues serving signals. Retraining takes hours.

### Solution
New `MLRegimeHotSwapWorker` detects regime transitions and reactivates previously superseded models that performed well in the new regime.

### Files

| File | Change |
|---|---|
| `Application/Workers/MLRegimeHotSwapWorker.cs` | **New.** Polls every 60s. For each symbol/timeframe: loads two most recent `MarketRegimeSnapshot`s, detects transitions, queries `MLShadowRegimeBreakdown` for superseded models' per-regime accuracy, reactivates the best candidate as `IsFallbackChampion` if it beats the champion by the configured margin. Sets `Status = Active` + `IsFallbackChampion = true` + `IsActive = true` with `Status == Superseded` guard against double-activation. Queues a training run for a proper replacement. |

### Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `MLRegime:EnableHotSwap` | bool | `false` | Feature gate |
| `MLRegime:PollIntervalSeconds` | int | `60` | Polling interval |
| `MLRegime:HotSwapAccuracyMargin` | double | `0.05` | Minimum accuracy advantage to trigger swap |

---

## Improvement #6 — Cross-Architecture Feature Importance Consensus

### Problem
Each trainer independently computes feature importance, but this information isn't shared.

### Solution
New `MLFeatureConsensusWorker` periodically computes per-feature consensus across all active models.

### Files

| File | Change |
|---|---|
| `Application/Workers/MLFeatureConsensusWorker.cs` | **New.** Polls hourly. Loads active models' `FeatureImportance` from `ModelSnapshot`, computes per-feature mean/std importance and agreement score (1 − std/mean), pairwise Kendall's tau rank correlation. Persists `MLFeatureConsensusSnapshot`. |
| `Domain/Entities/MLFeatureConsensusSnapshot.cs` | **New entity.** `Symbol`, `Timeframe`, `FeatureConsensusJson`, `ContributingModelCount`, `MeanKendallTau`, `DetectedAt`. |
| `Infrastructure/Persistence/Configurations/MLFeatureConsensusSnapshotConfiguration.cs` | **New.** Composite index on `(Symbol, Timeframe, DetectedAt)`. |

### Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `MLFeatureConsensus:PollIntervalSeconds` | int | `3600` | Polling interval |
| `MLFeatureConsensus:MinModelsForConsensus` | int | `3` | Minimum models required |

---

## Improvement #7 — Steeper Temporal Decay on UCB1 Scores

### Problem
`RecencyHalfLifeDays = 30` allows old performance to inflate UCB1 scores.

### Solution
Make decay parameters configurable. Add a two-phase decay: normal half-life for the first `ArchStalenessDays`, then `halfLife / SteepDecayMultiplier` afterwards.

### Files

| File | Change |
|---|---|
| `Application/Services/ML/TrainerSelector.cs` | **Modified** `ScoreArchitectureGroup()`: uses configurable `cfgRecencyHalfLife` and applies steepened half-life after `ArchStalenessDays`. **Modified** `RecencyWeight()`: accepts optional `halfLifeDays` parameter. |

### Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `MLTraining:RecencyHalfLifeDays` | double | `30.0` | Base recency half-life |
| `MLTraining:SteepDecayMultiplier` | double | `1.0` | Phase-2 decay acceleration (>1.0 = faster decay) |

---

## Improvement #8 — Abstention-Aware Trainer Ranking

### Problem
UCB1 composite ignores abstention rate. A model that abstains on 60% of signals looks identical to one that trades everything.

### Solution
Add `AbstentionPrecision` to the composite score with configurable weight.

### Files

| File | Change |
|---|---|
| `Domain/Entities/MLTrainingRun.cs` | **Added** `AbstentionRate` (decimal?) and `AbstentionPrecision` (decimal?). |
| `Application/Services/ML/TrainerSelector.cs` | **Modified** `ComputeCompositeScore()`: adds `abstentionPrecision` and `weightAbstention` optional params. **Modified** `ArchRunProjection`: added `AbstentionPrecision` field. **Modified** `LoadRecentRunsAsync` and cross-symbol projection: project `AbstentionPrecision`. |

### Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `MLTraining:WeightAbstention` | double | `0.0` | Composite score weight for abstention precision (0 = disabled) |

---

## Improvement #9 — Training Budget Allocation (Two-Lane Priority Queue)

### Problem
All training runs consume equal queue priority. Expensive deep-tier runs block cheap simple-tier runs.

### Solution
Add `Priority` field to `MLTrainingRun`. Modify `ClaimNextRunAsync` to order by `Priority` first, then `StartedAt`. During systemic pause (#11), only fast-lane runs (Priority ≤ 1) are processed.

### Files

| File | Change |
|---|---|
| `Domain/Entities/MLTrainingRun.cs` | **Added** `Priority` (int, default 5). |
| `Application/Workers/MLTrainingWorker.cs` | **Modified** `ClaimNextRunAsync()`: orders by `Priority` then `StartedAt`. Reads `MLTraining:SystemicPauseActive` from `EngineConfig` — when paused, only claims runs with `Priority <= 1` or `IsEmergencyRetrain`. |
| `Application/Workers/MLDriftMonitorWorker.cs` | Sets `Priority = 1` on drift-triggered runs, `Priority = 2` on tenure-challenge runs. |
| `Application/Workers/MLCovariateShiftWorker.cs` | Sets `Priority = 1` on covariate-shift runs. |

### Priority Levels

| Priority | Meaning | Source |
|---|---|---|
| 0 | Emergency (structural break) | `MLStructuralBreakWorker` |
| 1 | Drift-triggered | `MLDriftMonitorWorker`, `MLCovariateShiftWorker` |
| 2 | Tenure challenge | `MLDriftMonitorWorker.CheckChampionTenureAsync` |
| 3 | Manual (operator-initiated) | API |
| 5 | Scheduled (routine) | Default |

---

## Improvement #10 — Champion Tenure Tracking

### Problem
A long-serving champion that maintains 52% accuracy (just above drift threshold) is never retrained — it's "good enough" but potentially far from optimal.

### Solution
Track tenure via `ActivatedAt` and `LastChallengedAt`. Proactively queue a training run when tenure exceeds the configured maximum.

### Files

| File | Change |
|---|---|
| `Domain/Entities/MLModel.cs` | **Added** `LastChallengedAt` (DateTime?). |
| `Application/Workers/MLDriftMonitorWorker.cs` | **Added** `CheckChampionTenureAsync()`: checks `ActivatedAt` tenure against `MaxChampionTenureDays`, respects `MinDaysBetweenChallenges` cooldown, queues `TriggerType.Scheduled` run with `Priority = 2`. Updates `LastChallengedAt` via `ExecuteUpdateAsync`. Called per-model after drift check. |

### Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `MLTraining:ProactiveChallengeEnabled` | bool | `false` | Feature gate |
| `MLTraining:MaxChampionTenureDays` | int | `30` | Max days before proactive challenge |
| `MLTraining:MinDaysBetweenChallenges` | int | `7` | Cooldown between challenges |

---

## Improvement #11 — Correlated Failure Detection Across Symbols

### Problem
Drift detection is per-model. Simultaneous degradation across many symbols indicates a systemic event, not independent failures.

### Solution
New `MLCorrelatedFailureWorker` detects when a significant fraction of active models degrade simultaneously and activates a system-wide training pause.

### Files

| File | Change |
|---|---|
| `Application/Workers/MLCorrelatedFailureWorker.cs` | **New.** Polls every 600s. Batch-queries prediction accuracy via `GroupBy(MLModelId)`. Computes failure ratio. If ≥ alarm threshold: sets `MLTraining:SystemicPauseActive = true` in `EngineConfig`, creates `MLCorrelatedFailureLog`, raises `AlertType.SystemicMLDegradation`. If ≤ recovery threshold: lifts pause. |
| `Domain/Entities/MLCorrelatedFailureLog.cs` | **New entity.** `DetectedAt`, `FailingModelCount`, `TotalModelCount`, `FailureRatio`, `SymbolsAffectedJson`, `PauseActivated`. |
| `Domain/Enums/AlertType.cs` | **Added** `SystemicMLDegradation = 7`. |
| `Infrastructure/Persistence/Configurations/MLCorrelatedFailureLogConfiguration.cs` | **New.** Index on `DetectedAt`. |
| `Application/Workers/MLTrainingWorker.cs` | **Modified** `ClaimNextRunAsync()`: reads `MLTraining:SystemicPauseActive` and filters to fast-lane runs only when paused. |

### Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `MLCorrelated:PollIntervalSeconds` | int | `600` | Polling interval |
| `MLCorrelated:AlarmRatio` | double | `0.40` | Fraction of failing models to trigger alarm |
| `MLCorrelated:RecoveryRatio` | double | `0.20` | Fraction below which to lift pause |
| `MLTraining:SystemicPauseActive` | bool | `false` | Runtime flag (set by worker, read by training) |

---

## Improvement #12 — Shadow Evaluation Outcomes → Regime Affinity

### Problem
Shadow evaluation per-regime accuracy is the strongest signal for "which architecture works in which regime" but is discarded after the promotion decision.

### Solution
Blend `MLShadowRegimeBreakdown` data into `TrainerSelector`'s regime affinity map alongside training-run empirical data.

### Files

| File | Change |
|---|---|
| `Application/Services/ML/TrainerSelector.cs` | **Modified** `BuildRawAffinityMapAsync()`: queries `MLShadowRegimeBreakdown` rows for the target regime, computes per-architecture shadow affinity, blends with the base affinity using configurable weight. All callers now pass the config-derived `shadowAffinityWt` for cache consistency. |
| `Application/Workers/MLShadowArbiterWorker.cs` | **Modified.** After promotion, invalidates `TrainerSelector` cache via `ITrainerSelector.InvalidateCache()` with proper scope disposal. |

### Configuration

| Key | Type | Default | Description |
|---|---|---|---|
| `MLTraining:ShadowRegimeAffinityWeight` | double | `0.30` | Weight given to shadow-derived vs training-run-derived regime affinity |

---

## Configuration Reference

### All New EngineConfig Keys

| Key | Type | Default | Improvement | Description |
|---|---|---|---|---|
| `MLScoring:EnableCommittee` | bool | `false` | #1 | Enable multi-model committee scoring |
| `MLScoring:MaxCommitteeSize` | int | `3` | #1 | Maximum committee members |
| `MLTraining:UseGraduatedSampleGate` | bool | `false` | #4 | Enable continuous sample discount |
| `MLTraining:SampleGateHardFloorFraction` | double | `0.20` | #4 | Hard rejection floor as fraction of required |
| `MLRegime:EnableHotSwap` | bool | `false` | #5 | Enable regime hot-swap |
| `MLRegime:PollIntervalSeconds` | int | `60` | #5 | Hot-swap polling interval |
| `MLRegime:HotSwapAccuracyMargin` | double | `0.05` | #5 | Minimum accuracy margin for swap |
| `MLFeatureConsensus:PollIntervalSeconds` | int | `3600` | #6 | Consensus polling interval |
| `MLFeatureConsensus:MinModelsForConsensus` | int | `3` | #6 | Minimum models for consensus |
| `MLTraining:RecencyHalfLifeDays` | double | `30.0` | #7 | Recency weight half-life |
| `MLTraining:SteepDecayMultiplier` | double | `1.0` | #7 | Phase-2 decay acceleration |
| `MLTraining:WeightAbstention` | double | `0.0` | #8 | Abstention precision weight |
| `MLTraining:ProactiveChallengeEnabled` | bool | `false` | #10 | Enable tenure tracking |
| `MLTraining:MaxChampionTenureDays` | int | `30` | #10 | Max champion tenure |
| `MLTraining:MinDaysBetweenChallenges` | int | `7` | #10 | Cooldown between challenges |
| `MLCorrelated:PollIntervalSeconds` | int | `600` | #11 | Correlated failure polling |
| `MLCorrelated:AlarmRatio` | double | `0.40` | #11 | Alarm threshold |
| `MLCorrelated:RecoveryRatio` | double | `0.20` | #11 | Recovery threshold |
| `MLTraining:SystemicPauseActive` | bool | `false` | #11 | Systemic pause flag (runtime) |
| `MLTraining:ShadowRegimeAffinityWeight` | double | `0.30` | #12 | Shadow regime affinity weight |

---

## Schema Changes

### EF Migration: `MLTrainingWorkflowImprovements`

#### Modified Tables

**MLTrainingRun:**
| Column | Type | Nullable | Default | Improvement |
|---|---|---|---|---|
| `DriftTriggerType` | `text` | Yes | `null` | #2 |
| `DriftMetadataJson` | `text` | Yes | `null` | #2 |
| `AbstentionRate` | `decimal(8,6)` | Yes | `null` | #8 |
| `AbstentionPrecision` | `decimal(8,6)` | Yes | `null` | #8 |
| `Priority` | `int` | No | `5` | #9 |

**MLModel:**
| Column | Type | Nullable | Default | Improvement |
|---|---|---|---|---|
| `LastChallengedAt` | `timestamp` | Yes | `null` | #10 |

**MLShadowEvaluation:**
| Column | Type | Nullable | Default | Improvement |
|---|---|---|---|---|
| `TournamentGroupId` | `uuid` | Yes | `null` | #3 |
| `TournamentRank` | `int` | Yes | `null` | #3 |

**MLModelPredictionLog:**
| Column | Type | Nullable | Default | Improvement |
|---|---|---|---|---|
| `CommitteeModelIdsJson` | `text` | Yes | `null` | #1 |
| `CommitteeDisagreement` | `decimal(8,6)` | Yes | `null` | #1 |

#### New Tables

**MLFeatureConsensusSnapshot:**
| Column | Type | Nullable |
|---|---|---|
| `Id` | `bigint` (PK, auto) | No |
| `Symbol` | `varchar(20)` | No |
| `Timeframe` | `varchar(10)` | No |
| `FeatureConsensusJson` | `text` | No |
| `ContributingModelCount` | `int` | No |
| `MeanKendallTau` | `double` | No |
| `DetectedAt` | `timestamp` | No |
| `IsDeleted` | `bool` | No |

Index: `(Symbol, Timeframe, DetectedAt)`

**MLCorrelatedFailureLog:**
| Column | Type | Nullable |
|---|---|---|
| `Id` | `bigint` (PK, auto) | No |
| `DetectedAt` | `timestamp` | No |
| `FailingModelCount` | `int` | No |
| `TotalModelCount` | `int` | No |
| `FailureRatio` | `double` | No |
| `SymbolsAffectedJson` | `text` | No |
| `PauseActivated` | `bool` | No |
| `IsDeleted` | `bool` | No |

Index: `DetectedAt`

#### New Enum Value

**AlertType:** `SystemicMLDegradation = 7`

---

## File Inventory

### New Files (8)

| File | Lines | Purpose |
|---|---|---|
| `Application/Services/ML/EnsembleCommitteeBlender.cs` | ~153 | Committee probability blending |
| `Application/Workers/MLRegimeHotSwapWorker.cs` | ~369 | Regime-transition model reactivation |
| `Application/Workers/MLFeatureConsensusWorker.cs` | ~426 | Cross-architecture feature consensus |
| `Application/Workers/MLCorrelatedFailureWorker.cs` | ~361 | Systemic ML degradation detection |
| `Domain/Entities/MLFeatureConsensusSnapshot.cs` | ~50 | Feature consensus entity |
| `Domain/Entities/MLCorrelatedFailureLog.cs` | ~46 | Correlated failure event entity |
| `Infrastructure/Persistence/Configurations/MLFeatureConsensusSnapshotConfiguration.cs` | ~24 | EF config |
| `Infrastructure/Persistence/Configurations/MLCorrelatedFailureLogConfiguration.cs` | ~21 | EF config |

### Modified Files (10)

| File | Improvements | Key Changes |
|---|---|---|
| `Application/Services/ML/TrainerSelector.cs` | #2, #4, #7, #8, #12 | 6 new config keys, `ComputeDriftAwareBoostsAsync`, `PickBestWithGraduatedGate`, `ComputeSampleDiscount`, `AccuracyToAffinity`, configurable decay, shadow affinity blend |
| `Application/Workers/MLTrainingWorker.cs` | #3, #9, #11 | `DeterministicTournamentGroup`, priority-ordered claiming, systemic pause check |
| `Application/Workers/MLShadowArbiterWorker.cs` | #3, #12 | Tournament resolution with regime-guard, cache invalidation |
| `Application/Workers/MLDriftMonitorWorker.cs` | #2, #9, #10 | Drift metadata tagging, priority, `CheckChampionTenureAsync` |
| `Application/Workers/MLCovariateShiftWorker.cs` | #2, #9 | Covariate shift metadata, priority |
| `Application/Services/MLModelResolver.cs` | #1 | `ResolveCommitteeModelsAsync`, `FamilyOf` |
| `Domain/Entities/MLTrainingRun.cs` | #2, #8, #9 | `DriftTriggerType`, `DriftMetadataJson`, `AbstentionRate`, `AbstentionPrecision`, `Priority` |
| `Domain/Entities/MLModel.cs` | #10 | `LastChallengedAt` |
| `Domain/Entities/MLShadowEvaluation.cs` | #3 | `TournamentGroupId`, `TournamentRank` |
| `Domain/Entities/MLModelPredictionLog.cs` | #1 | `CommitteeModelIdsJson`, `CommitteeDisagreement` |
| `Domain/Enums/AlertType.cs` | #11 | `SystemicMLDegradation = 7` |
| `Application/Common/Interfaces/IMLSignalScorer.cs` | #1 | `CommitteeModelIdsJson`, `CommitteeDisagreement` on `MLScoreResult` |

---

## Backward Compatibility

All changes are **fully backward compatible**:

1. **New entity properties** are nullable (except `Priority` which defaults to `5`). Existing rows are unaffected.
2. **New workers** are auto-registered via assembly scanning as `IHostedService`. They start automatically but gate their logic behind config flags.
3. **Feature gates** default to `false`/`0.0` — all improvements are opt-in except the always-active ones (#2 drift tagging, #3 tournament grouping, #9 priority ordering, #11 correlated failure, #12 shadow affinity).
4. **Always-active improvements** are no-ops when conditions aren't met:
   - #2: `DriftTriggerType = null` on non-drift runs (selector ignores nulls).
   - #3: `TournamentGroupId` is null for pre-existing shadow evals (arbiter processes them individually).
   - #9: `Priority = 5` for existing runs (same FIFO order within the default tier).
   - #11: Systemic pause is only activated when failure ratio exceeds 40% of models.
   - #12: Shadow affinity blends with weight `0.30` × alpha (0 when no shadow data exists) — neutral when empty.
5. **No breaking API changes**. `MLScoreResult` uses default parameter values.
6. **EF migration** is additive only (new columns, new tables). No column renames or drops.
