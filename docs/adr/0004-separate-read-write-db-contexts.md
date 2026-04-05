# ADR-0004: Separate Read/Write DB Contexts (CQRS)

**Status:** Accepted  
**Date:** 2024-11-01  
**Deciders:** Olabode Olaleye

## Context

The engine uses Entity Framework Core for data access. A single `DbContext` shared between reads and writes causes two problems:

1. **Change tracker pollution.** Query handlers that load entities for display inadvertently track them. If a command handler runs in the same scope, EF may persist unintended changes when `SaveChangesAsync` is called.

2. **Read/write scaling.** Read-heavy workloads (dashboard queries, health checks, regime lookups) contend with write-heavy workloads (candle ingestion, order creation, position updates) on the same connection pool.

## Decision

Implement two separate `DbContext` subclasses — `WriteApplicationDbContext` (for commands) and `ReadApplicationDbContext` (for queries) — both inheriting from a shared `BaseApplicationDbContext<T>` that defines the entity model. Enforce at the interface level: `IWriteApplicationDbContext` (commands only) and `IReadApplicationDbContext` (queries only). A handler must never inject both.

Both contexts share the same physical database. The separation is logical, not physical — it enforces the CQRS command/query responsibility split at the DI level.

## Alternatives Considered

**Single context with `AsNoTracking()` convention.** Query handlers would call `.AsNoTracking()` on every query. Rejected because it relies on developer discipline — a single missing `AsNoTracking()` silently re-enables tracking, and there's no compile-time enforcement.

**Physical read replica.** Route reads to a PostgreSQL streaming replica. Considered for future scaling, but rejected for the current deployment because the engine runs on a single server. The logical separation makes physical separation a configuration change rather than a code change.

**CQRS with separate read models (projections).** Maintain denormalized read models updated by event handlers. Rejected as premature — the current entity model serves both reads and writes adequately, and projections would double the schema maintenance burden.

## Consequences

**Positive:**
- Commands and queries cannot accidentally interfere with each other's change tracking.
- The DI registration enforces the pattern at compile time — injecting `IWriteApplicationDbContext` into a query handler is a visible code review violation.
- Future physical read/write splitting requires only changing the connection string for `ReadApplicationDbContext`, not modifying any handler code.

**Negative:**
- Background workers that need to read state and then write based on it (e.g., `OptimizationWorker` loads candles, then persists results) must inject both contexts via separate DI registrations. The "never inject both into a single handler" rule applies to CQRS handlers, not to background orchestrators — but this nuance must be understood.
- Two contexts double the EF migration surface area. Mitigated by sharing `BaseApplicationDbContext<T>` and using a third `ApplicationDbContext` solely for migration generation.
