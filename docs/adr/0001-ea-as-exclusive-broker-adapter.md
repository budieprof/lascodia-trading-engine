# ADR-0001: EA as Exclusive Broker Adapter

**Status:** Accepted  
**Date:** 2025-01-15  
**Deciders:** Olabode Olaleye

## Context

The engine originally connected to brokers (OANDA, IB, FXCM) directly via REST/FIX APIs. This created three problems:

1. **Broker API fragmentation.** Each broker has a different API, authentication model, order format, and error code set. Every new broker required a full adapter implementation, and broker API changes broke production without warning.

2. **No access to MetaTrader 5 infrastructure.** MT5 is the dominant retail/prop-firm platform, but MT5 brokers don't expose public REST APIs. The only way to trade on MT5 is through an Expert Advisor running inside the terminal.

3. **Data quality.** Direct API feeds varied in tick granularity, candle completeness, and symbol spec format. Normalizing across brokers was a constant maintenance burden.

## Decision

Disable all built-in broker adapters. Make the MQL5 Expert Advisor the **sole** interface between the engine and the broker. The EA is responsible for:

- Streaming all market data (ticks, candles, symbol specs, sessions) to the engine via REST
- Polling the engine for trade signals and executing them on MT5
- Reporting execution results (fills, rejections, partial fills) back to the engine
- Reconciling positions, orders, and deals between engine and broker state

The engine has zero direct broker connectivity when EA mode is enabled.

## Alternatives Considered

**Keep direct broker APIs alongside the EA.** Rejected because maintaining two execution paths doubles the testing surface and creates split-brain risk — the engine could have a position open via OANDA that the EA doesn't know about.

**Use MT5's built-in WebRequest for engine communication.** Rejected because WebRequest blocks the calling thread, is limited to GET/POST, has no connection pooling, and can't be called from OnTick (only from indicators and scripts). The EA needs a proper HTTP client with timeouts, retries, and async support.

**Use a bridge DLL for all communication.** Partially adopted — the EA supports both HTTP polling and DLL push transport, with automatic fallback from DLL to HTTP when the bridge is unresponsive. This gives latency benefits when the DLL bridge is available without sacrificing reliability.

## Consequences

**Positive:**
- Single execution path eliminates split-brain risk.
- The engine's trading logic is 100% broker-agnostic — switching brokers only requires reconfiguring the EA.
- MT5's execution infrastructure (order routing, partial fills, margin calculation) is battle-tested across thousands of brokers.
- Symbol specs, session schedules, and tick data come directly from the broker via MT5's native APIs — no normalization layer needed.

**Negative:**
- The engine is completely dependent on EA availability. If all EA instances disconnect, the engine enters `DATA_UNAVAILABLE` and cannot evaluate strategies or execute trades.
- Latency is higher than direct API (engine → HTTP → EA → MT5 → broker, vs engine → broker). Mitigated by the DLL transport option.
- The EA codebase is 27,000 lines of MQL5 — a language with limited tooling, no package manager, no unit test framework, and single-threaded execution. Bugs are harder to find and fix than in .NET.

**Mitigations:**
- Engine tracks EA heartbeats. If no heartbeat for 60 seconds, the symbol is marked `DATA_UNAVAILABLE` and strategy evaluation is suppressed.
- Multiple EA instances can run concurrently with coordinator election and symbol ownership, providing redundancy.
- The EA persists safety-critical state to GlobalVariables, surviving restart without data loss.
