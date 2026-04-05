# ADR-0006: Shadow Evaluation Before Model Promotion

**Status:** Accepted  
**Date:** 2025-02-15  
**Deciders:** Olabode Olaleye

## Context

When a new ML model passes all quality gates (accuracy, EV, Brier, Sharpe, F1, walk-forward, ECE, BSS, OOB regression), it appears ready to replace the current champion. But quality gates evaluate on historical data — they cannot guarantee the model will perform well on live, unseen data. Directly promoting a gate-passing model risks replacing a profitable champion with a model that passes in-sample checks but degrades in production.

## Decision

When a new model passes quality gates AND is more profitable than the current champion (composite score: EV*5 + Sharpe*0.1 + F1*0.5), it is promoted to Active **but enters a shadow evaluation period** rather than immediately replacing the champion for signal generation.

During shadow evaluation:
- The champion continues serving live signals (`IsActive = true`).
- The challenger records shadow predictions via `MLSignalScorer`.
- `MLShadowArbiterWorker` periodically evaluates the challenger against the champion using a **Sequential Probability Ratio Test (SPRT)** and a **two-proportion z-test**.
- Only when the challenger demonstrates statistically significant superiority — or the champion is significantly worse — does the arbiter promote the challenger.

Shadow evaluations are grouped into **tournaments** (deterministic group ID from champion ID + UTC hour) so multiple challengers competing for the same slot are evaluated as a cohort.

## Alternatives Considered

**Immediate promotion on gate pass.** The model passes gates → directly replaces champion. Rejected because gate pass is a necessary but not sufficient condition. Historical metrics don't capture live execution dynamics (spread variation, signal timing, tick latency).

**A/B split traffic.** Route 50% of signals through the champion and 50% through the challenger. Rejected because it requires executing twice as many trades, doubling commission/slippage costs, and creating accounting complexity for the same symbol.

**Paper trading period.** The challenger generates hypothetical signals that aren't executed but tracked for accuracy. This is effectively what shadow evaluation does — but formalized with statistical significance tests (SPRT) rather than an arbitrary time window.

## Consequences

**Positive:**
- The champion is never replaced by a model that hasn't demonstrated live superiority.
- SPRT provides a mathematically rigorous promotion criterion — not "wait 30 days and compare averages" but "stop as soon as statistical significance is achieved, whether that's 15 trades or 200."
- Tournament grouping prevents serial promotion (Model A beats champion, then Model B beats A, then Model C beats B) by evaluating all challengers simultaneously.
- Models that pass gates but can't beat the champion in live conditions are saved as Superseded — their parameters are available for warm-starting future training runs.

**Negative:**
- Promotion is delayed by the shadow period. A genuinely superior model that would have been profitable from day one generates no live signals until the SPRT concludes.
- Shadow predictions must be stored and tracked, adding DB write volume proportional to the number of active shadow evaluations × signal frequency.
- The SPRT's required sample size depends on the true effect size, which is unknown. Small improvements take many trades to detect — the shadow evaluation may expire (configurable, default 30 days) before reaching significance.
