# ADR-0003: Event-Driven Core Loop with Bounded Channel Backpressure

**Status:** Accepted  
**Date:** 2025-03-01  
**Deciders:** Olabode Olaleye

## Context

The StrategyWorker must evaluate strategies on every price tick. Ticks arrive from the EA at variable rates — from quiet periods (1 tick/second) to news spikes (100+ ticks/second). The original polling design queried for new prices every N seconds, which was either too slow (missed opportunities) or too fast (wasted CPU cycles on empty polls).

The event-driven alternative — subscribing to `PriceUpdatedIntegrationEvent` via the event bus and evaluating immediately — has a different problem: when ticks arrive faster than evaluation completes, the handler queue grows unboundedly. Eventually this causes memory pressure, GC pauses, and strategy evaluations running on prices that are minutes stale.

## Decision

Use a **bounded channel** (`Channel.CreateBounded<T>` with capacity 256 and `DropOldest` policy) as a backpressure buffer between the event bus and the evaluation pipeline.

The event bus handler (`Handle()`) writes each price event into the channel. The main loop (`ExecuteAsync`) reads from the channel and runs the full evaluation pipeline. When ticks arrive faster than evaluation completes, the oldest queued ticks are dropped — the latest price is always more relevant than a stale one.

This makes tick loss **explicit and bounded** rather than implicit and unbounded.

## Alternatives Considered

**Unbounded queue with rate limiting.** Accept all events, skip evaluation if the last evaluation for the same symbol was < N ms ago. Rejected because the queue still grows unboundedly during sustained bursts, and the skip logic introduces subtle timing bugs.

**Per-symbol latest-only buffer.** Store only the most recent event per symbol, overwriting on each new tick. Rejected because this loses the temporal ordering needed for audit logging and because it requires a ConcurrentDictionary lookup on every tick.

**Debounce timer per symbol.** Buffer ticks and evaluate only after N ms of silence. Rejected because it introduces latency during normal trading and doesn't handle the case where ticks never "pause" during volatile periods.

## Consequences

**Positive:**
- Memory usage is bounded (256 events max, regardless of tick rate).
- The evaluation pipeline always processes the freshest available price.
- Tick drops are tracked via `TicksDroppedBackpressure` metric — operators can tune the channel capacity or evaluation parallelism if drops are excessive.
- The stale tick rejection gate (MaxTickAgeSeconds) provides a second defense — even if a tick survives the channel, it's dropped if too old.

**Negative:**
- Ticks ARE dropped during sustained bursts. This is by design — evaluating on a 30-second-old price is worse than skipping it — but operators unfamiliar with the design may interpret dropped ticks as a bug.
- The channel capacity (256) is a tunable that affects the tradeoff between responsiveness and drop rate. Too small = frequent drops during normal volatility. Too large = stale ticks queuing up.
