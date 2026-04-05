# ADR-0011: Defense-in-Depth Risk Model (5 Independent Layers)

**Status:** Accepted  
**Date:** 2025-01-25  
**Deciders:** Olabode Olaleye

## Context

A single risk check before order execution is a single point of failure. If the risk checker has a bug, is misconfigured, or is bypassed by an edge case, there is no fallback. In trading, a single unchecked order can cause catastrophic loss — margin call, account blowout, or regulatory violation.

## Decision

Implement risk enforcement at **5 independent layers**, each with its own trigger, scope, and failure mode:

**Layer 1 — Signal Validation (`ISignalValidator`, Tier 1):**
Checks the signal's intrinsic quality. Runs in `SignalOrderBridgeWorker` immediately on signal creation. No account state referenced. Catches malformed signals, expired signals, missing stop-loss, bad R:R ratio.

**Layer 2 — Account Risk (`IRiskChecker`, Tier 2):**
Checks account-level constraints. Runs when an account attempts to create an order from an approved signal. 16+ checks: equity guard, lot sizing (min/max/step), margin availability, symbol/total exposure, position limits (global, per-symbol, daily), daily drawdown, consecutive loss streak, correlation limits, spread validation, cross-currency conversion.

**Layer 3 — Strategy Health (`StrategyHealthWorker` + `StrategyFeedbackWorker`):**
Real-time (60s) and hourly monitoring of per-strategy performance. Auto-pauses strategies with Critical health status. Queues optimization for degraded strategies. A paused strategy generates no signals — this layer prevents bad strategies from reaching Layer 1.

**Layer 4 — Account Drawdown (`DrawdownMonitorWorker` + `DrawdownRecoveryWorker`):**
Account-level equity monitoring every 60 seconds. Mode transitions: Normal -> Reduced (logging) -> Halted (all strategies paused). Recovery auto-resumes only the strategies that were auto-paused. This layer halts the entire engine when account equity degrades.

**Layer 5 — Execution Quality (`ExecutionQualityCircuitBreakerWorker`):**
Monitors fill quality per strategy (slippage, latency). Auto-pauses strategies with persistent execution degradation. Hysteresis margin prevents flapping. This layer catches broker-side issues that no backtest can model.

**EA-Side Safety (Layers 6-7, separate process):**
Per-symbol `CircuitBreaker` and cross-instance `GlobalCircuitBreaker` enforce limits independently of the engine. These operate even when the engine is down.

## Alternatives Considered

**Single comprehensive risk checker.** One component checks everything. Rejected because a single bug or misconfiguration leaves zero fallback.

**Pre-trade risk only (no runtime monitoring).** Check risk before order, don't monitor after. Rejected because market conditions change — a position that was within limits at entry can breach limits as prices move.

**Centralized risk service.** A separate microservice that all components call before any action. Rejected because it creates a single point of failure and adds latency to every operation.

## Consequences

**Positive:**
- No single layer's failure can result in unchecked trading. Even if Layer 2 (RiskChecker) has a bug, Layer 4 (DrawdownRecovery) will halt trading when equity drops.
- Each layer operates independently with its own polling interval, data sources, and enforcement mechanism. They share no state.
- Layers 6-7 (EA-side) provide safety even when the engine process is down — the EA enforces position limits, daily loss, and order rate locally.

**Negative:**
- Seven layers of risk checking is operationally complex. When trading is unexpectedly blocked, diagnosing *which* layer is responsible requires checking all seven.
- Layers can conflict subtly. Layer 3 may pause a strategy for health reasons while Layer 5 would have resumed it (execution quality recovered). The resolution is always conservative — any layer that says "stop" wins.
- The independence of layers means they can't optimize jointly. Layer 2 might approve an order that Layer 4 will halt 60 seconds later. This is by design (defense in depth means redundant checks), but it can confuse operators.
