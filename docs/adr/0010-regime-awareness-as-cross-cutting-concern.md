# ADR-0010: Regime Awareness as a Cross-Cutting Concern

**Status:** Accepted  
**Date:** 2025-02-20  
**Deciders:** Olabode Olaleye

## Context

Financial markets alternate between distinct behavioral regimes — trending, ranging, high-volatility, low-volatility, breakout. A momentum strategy that excels in a trend will bleed money in a range. A mean-reversion strategy that thrives in a range will get run over in a trend. Treating all market data as coming from a single distribution is the primary source of overfitting in retail trading systems.

The question is whether regime awareness should be a feature of specific components (the strategy evaluator checks the regime before generating a signal) or a system-wide architectural principle.

## Decision

Make regime awareness a **cross-cutting concern** that permeates every subsystem:

| Subsystem | How Regime Is Used |
|---|---|
| **RegimeDetectionWorker** | Classifies each symbol/timeframe into a regime every 60 seconds using ADX, Bollinger Band width, and volatility percentile. Persists `MarketRegimeSnapshot`. |
| **StrategyWorker** | Blocks signals in unfavourable regimes. Swaps to regime-conditional parameters when available. Checks cross-timeframe regime coherence. |
| **StrategyGenerationWorker** | Maps strategy types to regimes (momentum -> Trending, mean-reversion -> Ranging). Generates counter-regime reserves. Scales template count by regime confidence and duration. |
| **OptimizationWorker** | Blends regime-aware candles (80% current regime + 20% non-regime). Stores regime-conditional parameters. Runs cross-regime evaluation. Filters warm-start by regime. Defers runs during regime transitions. |
| **MLTrainingWorker** | Trains regime-specific sub-models. Applies regime-conditional F1 gate (trending allows directional-only models). Regime affinity in architecture selection. |
| **TrainerSelector** | UCB1 scores weighted by regime affinity. Transition cooldown attenuates affinity when regime is fresh. |
| **BacktestEngine** | No direct regime awareness — regime-conditional parameters are applied upstream before the evaluator runs. |

## Alternatives Considered

**Regime as a strategy-level feature only.** Each evaluator checks the regime internally and decides whether to trade. Rejected because this duplicates regime-checking logic across every evaluator and doesn't address the optimization/ML/generation layers.

**Regime as a global trade-or-don't filter.** A single "is the market tradeable?" check before any strategy runs. Rejected because different strategy types thrive in different regimes — blanket filtering would suppress strategies that should be active.

**No explicit regime detection.** Let the ML models implicitly learn regime dynamics from features. Rejected because (a) explicit regime classification is needed for strategy type mapping and parameter conditioning regardless of what ML learns, and (b) ML models trained on multi-regime data without explicit regime features tend to learn the average behavior, which is optimal for no specific regime.

## Consequences

**Positive:**
- Every subsystem adapts to regime changes without manual intervention.
- Regime-conditional parameters allow the same strategy to trade differently in different market conditions.
- Regime-aware candle blending in optimization prevents overfitting to the current regime while still concentrating on it.
- Counter-regime reserves in strategy generation ensure the system has strategies ready when the regime changes.

**Negative:**
- Regime classification errors propagate everywhere. If the detector misclassifies a trend as ranging, strategies are swapped to wrong parameters, ML models use wrong sub-models, and optimization trains on misclassified data.
- The system has a minimum-confidence gate (0.60 default) to suppress low-confidence classifications, but this means new or ambiguous market conditions (regime transitions) receive no regime label at all — falling back to global defaults.
- Regime-conditional parameters multiply the parameter space. A strategy with 5 parameters and 4 regimes effectively has 20 parameters to optimize, increasing the risk of overfitting to regime-specific noise.
