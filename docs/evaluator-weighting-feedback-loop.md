# Evaluator Weighting Feedback Loop

## How the System Naturally Shifts Toward Better Strategy Types

The StrategyGenerationWorker has a built-in feedback loop that tracks which strategy types survive screening and live trading. Over time, this loop automatically increases generation of high-performing types and reduces generation of underperformers.

## Mechanism

1. **Performance Feedback (`LoadPerformanceFeedbackAsync`):** Every cycle, the worker computes a recency-weighted survival rate per `(StrategyType, MarketRegime)` pair. Strategies that reach `BacktestQualified` or higher are "survivors"; soft-deleted strategies are "failures."

2. **Type Reordering (`ApplyPerformanceFeedback`):** Strategy types for each regime are reordered by their survival rate. Higher survival → screened first → more likely to consume the per-cycle candidate budget.

3. **Template-Level Tracking (`TemplateSurvivalRates`):** Individual parameter templates (e.g., `{FastPeriod:9, SlowPeriod:21}`) are tracked separately. Templates with proven survival are tried first via `OrderTemplatesForRegime`.

4. **Data-Driven Regime Mapping (`RefreshFromFeedback`):** Strategy types that achieve ≥65% survival in a regime they aren't statically mapped to are promoted into that regime's candidate pool.

## Expected Behavior with CompositeML

When `CompositeML` is enabled:
- It starts with the same generation priority as other types (no boost, no penalty)
- If CompositeML strategies consistently survive backtesting and live trading better than rule-based types, its survival rate will rise
- The feedback loop will then:
  - Reorder it higher in the generation priority list
  - Promote it into regimes where it wasn't statically mapped (if survival ≥ 65%)
  - Its templates with proven parameters will be tried first
- Conversely, if CompositeML underperforms, it will be naturally deprioritized

## Timeline

- **Weeks 1-4:** CompositeML generates candidates alongside other types. No weighting advantage yet.
- **Weeks 4-8:** First survival data accumulates. Feedback starts showing differential survival rates.
- **Weeks 8+:** If CompositeML has genuine edge, it will dominate generation. If not, it will be deprioritized.

## Manual Override

To force-boost or suppress a type, use per-regime EngineConfig overrides:
- Boost: Set `StrategyGeneration:Overrides:CompositeML:MinSurvivalOverride` (not yet implemented — would bypass natural feedback)
- Suppress: Remove `StrategyType.CompositeML` from `RegimeStrategyMapper.StaticMap` entries

The recommended approach is to let the feedback loop operate autonomously.

## Configuration Keys

| Key | Default | Effect |
|-----|---------|--------|
| `CompositeML:Enabled` | false | Master toggle for CompositeML generation |
| `MLInference:EnableExtendedFeatures` | false | Enable 47-feature vector (cross-pair + news + sentiment) |
| `MLTraining:EnableExtendedFeatures` | false | Enable 47-feature training (when ready) |
