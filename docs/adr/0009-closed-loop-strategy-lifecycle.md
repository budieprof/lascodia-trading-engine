# ADR-0009: Closed-Loop Strategy Lifecycle (Generation -> Optimization)

**Status:** Accepted  
**Date:** 2025-04-01  
**Deciders:** Olabode Olaleye

## Context

A trading system that only executes pre-configured strategies is static — it can't adapt to new market conditions, discover new opportunities, or retire strategies that no longer work. The alternative — manual strategy development by a quant researcher — doesn't scale beyond a handful of instruments and requires continuous human attention.

## Decision

Implement a fully autonomous strategy lifecycle with 6 stages, each driven by an independent background worker:

1. **StrategyGenerationWorker** — Discovers new strategy candidates by combinatorially screening regime-mapped strategy types, parameter templates, symbols, and timeframes. Candidates must pass 12 gates (IS/OOS backtests, degradation, R², walk-forward, Monte Carlo, marginal Sharpe, Kelly sizing). Survivors are persisted as Draft strategies.

2. **BacktestWorker** — Runs full historical backtests on Draft strategies. Strategies that meet quality thresholds (win rate, profit factor, Sharpe) are promoted and a WalkForwardRun is queued.

3. **WalkForwardWorker** — Runs anchored walk-forward analysis with re-optimization on each fold. Measures out-of-sample consistency. Passes/fails feed back to strategy lifecycle stage.

4. **StrategyHealthWorker** (real-time) + **StrategyFeedbackWorker** (hourly) — Monitor live performance. StrategyHealthWorker evaluates the last 50 signals every 60 seconds. StrategyFeedbackWorker computes rolling 30-day performance. Both auto-pause degraded strategies and queue optimization runs for critically underperforming ones.

5. **OptimizationWorker** — Refines parameters using Bayesian optimization (TPE/GP-UCB/EHVI) with 14 validation gates. Auto-approves improvements that pass all gates; deploys via gradual rollout.

6. **Pruning** — StrategyGenerationWorker deletes Draft strategies with N+ consecutive failed backtests. Failure memory prevents re-generating the same failed combination for a configurable cooldown period.

The loop closes: degradation detected (stage 4) -> optimization queued (stage 5) -> if optimization fails -> strategy paused -> generation discovers replacement (stage 1).

## Alternatives Considered

**Manual strategy management.** Operator creates strategies, configures parameters, monitors performance, triggers optimization. Rejected as the primary approach because it doesn't scale beyond ~10 instruments and requires 24/7 attention.

**Periodic batch optimization only.** Run optimization weekly on all strategies, no generation or pruning. Rejected because it doesn't discover new opportunities and doesn't remove strategies that are fundamentally broken (no amount of parameter tuning fixes a strategy type that's wrong for the current regime).

**Genetic programming for strategy discovery.** Evolve strategy logic (not just parameters) using GP. Considered for future work but rejected for now because GP-evolved strategies are opaque (can't be explained to an operator), prone to overfitting to the training period, and expensive to evaluate.

## Consequences

**Positive:**
- The system autonomously discovers, validates, deploys, monitors, optimizes, and retires strategies without human intervention.
- Failure memory and pruning prevent the system from repeatedly generating strategies that have already been shown to fail.
- Strategic reserve candidates (counter-regime types) ensure the system has strategies ready when the regime changes.
- The feedback from live performance (stage 4) directly drives optimization (stage 5) and generation (stage 1), creating a learning loop.

**Negative:**
- The system can only discover strategies within the bounds of the implemented `IStrategyEvaluator` types. If all evaluator types produce no edge in a given market, the generation worker will screen thousands of candidates, find none, and produce nothing.
- The autonomous lifecycle requires careful threshold calibration. Too permissive = bloat (too many low-quality strategies). Too strict = nothing passes screening. The adaptive threshold mechanism mitigates this but doesn't eliminate it.
- Six interacting workers with shared database state create complex interaction patterns. A bug in StrategyHealthWorker that incorrectly marks strategies as Critical will cascade: those strategies stop generating signals, their health snapshots show zero activity, and the feedback worker may queue unnecessary optimization.
