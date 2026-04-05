# ADR-0007: Gradual Rollout for Optimized Parameters

**Status:** Accepted  
**Date:** 2025-03-20  
**Deciders:** Olabode Olaleye

## Context

When the OptimizationWorker finds better parameters for a strategy and they pass all 14 validation gates, the system must update the live strategy. The naive approach — immediately swapping parameters — creates a binary risk: if the new parameters perform worse in live conditions despite passing validation, the strategy degrades abruptly with no fallback.

This is the same problem that software deployments solve with canary releases and blue/green deployments.

## Decision

Implement a **gradual rollout** (25% -> 50% -> 75% -> 100%) with automatic promotion and rollback.

**Start:** Save current parameters as rollback, install new parameters, set `RolloutPct = 25`. The `StrategyWorker` uses deterministic bucketing (seed from strategy ID + tick timestamp) to route 25% of evaluations through new parameters and 75% through old.

**Promote:** `AutoScheduleUnderperformersAsync` in `OptimizationWorker.Scheduling.cs` periodically evaluates rollout strategies. If the average health score over the observation window meets the threshold, promote to the next tier (25 -> 50 -> 75 -> 100).

**Complete:** At 100%, clear rollback state. The new parameters are fully deployed.

**Rollback:** If performance degrades during any tier (average health score below minimum OR weighted linear regression slope predicts >10% decline), restore pre-optimization parameters immediately.

## Alternatives Considered

**Immediate swap.** Optimization passes gates -> replace parameters. Rejected because validation gates operate on historical data. Live conditions (spread variation, liquidity changes, regime transitions) may differ.

**Time-boxed trial.** Run new parameters for N days, revert if worse. Rejected because a fixed time window doesn't account for signal frequency — a strategy that trades once a week needs a longer window than one that trades 10 times a day.

**A/B test with separate strategy instances.** Create a clone strategy with new parameters and run both in parallel. Rejected because it doubles position count and exposure for the same symbol, complicating the risk checker's position limits.

## Consequences

**Positive:**
- Maximum 25% of signal flow is affected by a parameter change at any given time.
- Automatic rollback prevents sustained degradation — the system self-corrects without operator intervention.
- The deterministic bucketing ensures the same tick always evaluates with the same parameters, enabling consistent A/B comparison.
- Trend detection (weighted linear regression) catches gradual degradation, not just threshold breaches.

**Negative:**
- During rollout, the strategy evaluates twice on some ticks (once with old params, once with new) — a CPU overhead proportional to `RolloutPct`.
- The observation window (re-used from `CooldownDays`) may not match the optimal evaluation period for all strategy types.
- Rollback restores pre-optimization parameters even if they were also degrading — it's a return to known state, not necessarily a return to good performance.
