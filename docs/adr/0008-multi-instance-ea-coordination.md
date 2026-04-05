# ADR-0008: Multi-Instance EA Coordination via GVar CAS

**Status:** Accepted  
**Date:** 2025-01-20  
**Deciders:** Olabode Olaleye

## Context

A single EA instance on a single MT5 chart can only receive ticks for one symbol at native speed. To trade multiple symbols with full tick coverage, multiple EA instances must run simultaneously — one per chart. But MT5 provides no built-in inter-EA communication mechanism. Multiple instances sharing the same account must coordinate:

1. **Symbol ownership.** Two instances must not both claim the same symbol (duplicate signals, duplicate orders).
2. **Coordinator duties.** Account sync, deal history, symbol spec collection should happen once, not once per instance.
3. **Safety enforcement.** Global limits (max total positions, daily loss, total lots) must be enforced across all instances atomically.

MT5's only shared-state mechanism is **terminal global variables** — key-value pairs (`string -> double`) accessible to all EAs in the same terminal.

## Decision

Implement a coordination layer using **Compare-And-Swap (CAS) operations** over MT5 GlobalVariables.

**Coordinator Election:** One instance is elected coordinator via a GVar-based CAS. The coordinator refreshes a heartbeat GVar every tick. If other instances detect the coordinator heartbeat is stale (>30s), they attempt to promote themselves via CAS. An **epoch fence** (45s) prevents split-brain during promotion — newly elected coordinators wait before assuming duties, allowing the old coordinator's in-flight operations to drain.

**Symbol Ownership:** Each symbol is claimed by writing `ChartID()` to `LASC_INST_{SYMBOL}`. CAS ensures atomic acquisition. Ownership is validated at init and periodically during runtime.

**Global Safety:** Cross-instance limits use GVars as atomic counters:
- `LASC_TOTAL_POSITIONS` — scanned from all open positions (2-second cache TTL)
- `LASC_TOTAL_LOTS` — sum of all open lot sizes
- `LASC_DAILY_PNL` — accumulated daily PnL
- `LASC_ORDERS_THIS_MINUTE` — rate limiter counter
- `LASC_SAFETY_STOP` — global halt flag

**CAS Exhaustion:** If CAS operations fail repeatedly (>N consecutive failures), the contention is too high for reliable safety state. The EA escalates to SAFETY_STOP because the global limits can no longer be trusted.

## Alternatives Considered

**Shared file on disk.** Each instance writes its state to a JSON file, others read it. Rejected because file I/O has unpredictable latency, file locking is unreliable across processes, and MT5's file system is sandboxed.

**Named pipes / shared memory.** MT5 doesn't support named pipes natively. DLL imports could provide shared memory, but this adds a C++ dependency that must be compiled per platform and breaks when MT5 updates its DLL loading policy.

**Database coordination (via engine).** Each instance reports its state to the engine, which arbitrates coordination decisions. Rejected because it adds a network round-trip to every safety check and makes the EA dependent on engine availability for basic safety enforcement. The EA must be safe even when the engine is down.

**DLL-based coordinator.** A native DLL running in-process that provides mutex-protected shared memory. Partially adopted — the `DllCoordinator` provides an alternative coordination backend when available, with automatic fallback to GVar-based CAS. This gives better atomicity guarantees when the DLL bridge is running.

## Consequences

**Positive:**
- Zero external dependencies — coordination works with standard MT5 features.
- Safety enforcement is local to the terminal — works even when the engine is down.
- Epoch fencing prevents the split-brain that naive leader election would cause.
- CAS exhaustion detection prevents operating with unreliable safety state.

**Negative:**
- MT5 GlobalVariables store `double`, not arbitrary data. Complex state must be encoded as doubles, limiting precision and expressiveness.
- CAS on GlobalVariables is not truly atomic — `GlobalVariableGet` + `GlobalVariableSetOnCondition` has a small race window. In practice, MT5's single-threaded per-EA model makes this safe for the EA's own operations, but two EAs executing OnTimer simultaneously can race.
- The 30-second staleness threshold means coordinator failover takes up to 30 seconds. During this window, coordinator duties (account sync, deal history) are paused.
