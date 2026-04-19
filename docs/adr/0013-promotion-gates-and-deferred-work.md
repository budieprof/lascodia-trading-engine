# 0013 — Promotion Gates and Deferred Quality Work

## Status

Accepted for Phase-2 (items #1, #5, #6, #8). Phase-3 (#3, #7, #9) deferred with designs captured here.

## Context

The strategy pipeline generates candidate strategies faster than the traditional
gate ("backtest Sharpe > X") can separate edge from overfitting. Live P&L from
Q1 2026 showed multiple cases where a strategy promoted on a 1.8 backtest Sharpe
produced <0 live Sharpe within 4 weeks. Root cause: every promotion criterion
was advisory, none were mandatory.

## Decision

### Phase 2 (landed)

**#1 Promotion gate stack** — `PromotionGateValidator` enforces 6 criteria at
`ActivateStrategyCommand` time. Any failure blocks promotion.

| Gate | Threshold | Source |
|---|---|---|
| Deflated Sharpe (DSR) | ≥ 1.0 | `BacktestRun.SharpeRatio` deflated by # trials in same symbol/timeframe |
| PBO proxy | ≤ 0.30 | Fraction of peers with ≥ this Sharpe (weak proxy; full PBO needs CPCV — see #3) |
| TCA-adjusted EV/trade | > 0 | `BacktestRun.TotalReturn / TotalTrades − avg(TCA)` |
| Paper-trade duration | ≥ 60 days, ≥ 100 trades | `Strategy.LifecycleStageEnteredAt`, `BacktestRun.TotalTrades` |
| Backtest coverage (regime proxy) | ≥ 180 days | `BacktestRun.ToDate − FromDate` |
| Max pairwise correlation | ≤ 0.70 | Pearson on `StrategyPerformanceSnapshot.SharpeRatio` series vs each active strategy |

All thresholds tunable via `EngineConfig` keys `Promotion:*`. Individual gates
disablable via `Promotion:DisableGates=csv`.

**#8 TCA cost model feedback** — `ITcaCostModelProvider` exposes per-symbol
realised cost profile (spread + commission + market impact) derived from
30-day rolling `TransactionCostAnalysis` samples. Callers (backtest engine,
promotion validator, shadow scorer) consume it to compute P&L against costs
that match live fills. Returns a conservative default profile for symbols
without enough TCA history (fail-safe: makes untested strategies look
pessimistic rather than free).

### Phase 3 (deferred, designed)

**#3 Combinatorial Purged Cross-Validation** — `ICpcvValidator` scaffolded.
Full implementation replaces the single-path walk-forward with C(N, K) paths
(suggested N=12, K=2 → 66 paths). Provides real DSR and PBO computable from
a full Sharpe distribution rather than the current point estimates. Expected
to be a ~1000-line subsystem over 2+ weeks:

1. New `CpcvRun` entity parallel to `WalkForwardRun` with Sharpe-distribution JSON column
2. Generation-cycle runner invokes CPCV only on candidates that cleared cheap gates
3. `PromotionGateValidator` reads CpcvRun for DSR/PBO instead of peer-proxy
4. Compute-cost mitigation: per-group training must be incremental / warm-startable

**#7 Look-ahead audit harness** — scaffolded in Phase 4 (not yet in this commit).
Will consist of:

1. Property-based test that replays candle sequences with last-bar values
   redacted, verifies features at time T do not change.
2. Automatic check at feature-save time: fingerprint the feature function's
   output given a candle window vs. the same window with `[T].close` mutated.
   Feature that changes is look-ahead-contaminated and must be rejected.
3. Run in CI on every PR that touches `MLFeatureHelper`.

**#9 Bayesian edge prior** — not a code change, a framing change. Every metric
that today reads "strategy is profitable if X > threshold" should be reframed
as "what's the posterior probability of live-Sharpe > 0 given the observed
backtest Sharpe and N trials?" Concrete work:

1. Build `EdgePosterior` service with a weak prior (mean = 0, σ = 0.1 Sharpe)
   and update with backtest evidence weighted by trial count.
2. Replace each gate threshold with a posterior-probability threshold
   (e.g. P(live Sharpe > 0) > 0.8 rather than backtest Sharpe > 1.5).
3. Requires instrumenting strategy generation to count N trials properly.

## Consequences

**Positive**:
- Fewer strategies promoted → higher average quality of active fleet
- Promotion failures are now diagnosable (gate-level reasons)
- TCA-adjusted backtesting eliminates the largest systematic backtest-to-live gap

**Negative**:
- Initial gate calibration is conservative (DSR ≥ 1.0 may block some genuinely good strategies)
- PBO-proxy is weaker than true CPCV-based PBO until #3 lands
- Correlation gate requires enough `StrategyPerformanceSnapshot` history; bootstraps are harder

## Migration path to full Phase 3

The scaffolded interfaces (`ICpcvValidator`, TCA provider) are designed so
adopting the full implementation is additive — `PromotionGateValidator` already
computes TCA-adjusted EV the right way; it will just read a richer distribution
when `CpcvValidator` is implemented.
