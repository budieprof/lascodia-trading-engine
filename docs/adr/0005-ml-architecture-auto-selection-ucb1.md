# ADR-0005: ML Architecture Auto-Selection via UCB1 Bandit

**Status:** Accepted  
**Date:** 2025-03-15  
**Deciders:** Olabode Olaleye

## Context

The engine supports 12 ML learner architectures (BaggedLogistic, TCN, GBM, ELM, AdaBoost, ROCKET, TabNet, FT-Transformer, SMOTE, QuantileRF, SVGP, DANN). Different architectures perform better on different symbols, timeframes, and market regimes. Manually selecting the right architecture per symbol/timeframe requires ongoing operator expertise and creates a maintenance burden that scales linearly with the number of traded instruments.

## Decision

Implement a `TrainerSelector` that automatically selects the best architecture using a **UCB1 (Upper Confidence Bound) multi-armed bandit** algorithm. The selection pipeline:

1. **Regime-conditional affinity.** Each architecture has a static prior affinity per regime (e.g., TCN preferred for Trending, BaggedLogistic for Ranging), blended with empirical per-regime accuracy as history accumulates. Transition cooldown attenuates affinity when the regime was detected very recently.

2. **Historical performance (UCB1).** Query the last 30 completed training runs for the symbol/timeframe, group by architecture, compute a recency-weighted composite score (accuracy, EV, Sharpe, F1) with a UCB1 exploration bonus inversely proportional to run count. Minimum 2 runs per architecture to be considered.

3. **Cross-symbol cold start.** When no history exists for the target symbol, borrow from instruments sharing the same base or quote currency.

4. **Graduated sample-count gate.** Simple-tier architectures (BaggedLogistic) are always eligible. Standard-tier require 500+ samples. Deep-tier (TCN, FT-Transformer, TabNet) require 2,000+ samples. The gate walks the ranked list and picks the highest-scoring architecture that meets the sample requirement.

5. **Fallback.** BaggedLogistic (SimpleTier — always passes the sample gate).

## Alternatives Considered

**Fixed architecture per symbol.** Operator configures `MLTraining:DefaultArchitecture` per symbol. Rejected as the default approach because it doesn't adapt to regime changes and requires ongoing manual tuning.

**Random exploration.** Each training run randomly samples an architecture. Rejected because it wastes compute on architectures known to underperform for the symbol/regime, and doesn't exploit accumulated knowledge.

**Bayesian optimization over architectures.** Treat architecture selection as a hyperparameter optimization problem. Rejected as over-engineered — the architecture space is small (12 options) and discrete, making UCB1 more appropriate than continuous Bayesian methods.

## Consequences

**Positive:**
- The system automatically converges toward the best architecture per symbol/timeframe/regime without operator intervention.
- The exploration bonus ensures underexplored architectures are periodically tried, preventing the system from getting stuck on a locally optimal but globally suboptimal choice.
- Cross-symbol cold start means new instruments get reasonable architecture selection from day one.
- Shadow architecture runs (queued after promotion) ensure multiple architectures are continuously evaluated per symbol.

**Negative:**
- The exploration phase burns compute on architectures that may not be viable for a given symbol. Mitigated by the sample-count gate (deep architectures aren't attempted on scarce data) and the blocked architectures config.
- UCB1 assumes stationary reward distributions. Market regime changes violate this assumption. Mitigated by the regime-conditional affinity weighting and the recency-weighted composite score (30-day half-life decay).
