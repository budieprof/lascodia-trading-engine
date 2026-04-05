# ADR-0012: Hot-Reloadable Configuration via Database (EngineConfig)

**Status:** Accepted  
**Date:** 2024-12-15  
**Deciders:** Olabode Olaleye

## Context

The engine has 200+ configurable parameters across workers (optimization: 55+ keys, ML training: 60+ keys, strategy generation: 45+ keys, plus per-worker thresholds). These parameters need to be tunable at runtime without redeploying the application. In a trading system, a deployment means downtime — and downtime during market hours means missed trades, unprotected positions, and potential reconciliation issues.

## Decision

Store all configuration in an `EngineConfig` database table (key-value pairs with metadata: description, data type, hot-reloadable flag, last-updated timestamp). Workers load their configuration at the start of each cycle via batch queries (single `WHERE Key.StartsWith("Prefix:")` query, not N individual lookups).

Key design rules:
- **Batch loading.** Every worker loads all its config keys in a single DB round-trip. The OptimizationWorker loads ~55 keys in one query; the MLTrainingWorker loads ~60 keys in one query.
- **Run-scoped snapshots.** Long-running operations (optimization runs, ML training runs) capture a config snapshot at the start and use it for the entire run. This prevents mid-run configuration changes from causing inconsistent behavior.
- **Per-symbol overrides.** Workers support per-symbol config overrides via `Prefix:Overrides:{SYMBOL}:{Key}` keys, extracted from the same batch query.
- **Preset system.** The OptimizationWorker supports presets (conservative/balanced/aggressive) that provide coherent defaults for performance-sensitive keys. Individual key overrides take precedence.
- **Config validation.** Workers validate loaded config before acting on it (range checks, dependency enforcement). Invalid configs are logged as warnings, not errors — the system continues with safe defaults.

## Alternatives Considered

**`appsettings.json` with `IOptionsMonitor<T>`.** Standard .NET configuration. Rejected because (a) changes require file system access to the deployment server, (b) no audit trail of who changed what, (c) no per-symbol overrides without nested JSON objects, (d) no run-scoped snapshots.

**Feature flag service (LaunchDarkly, Unleash).** External service for dynamic configuration. Rejected because it adds an external dependency that must be available 24/7, introduces network latency on every config read, and doesn't support the structured metadata (description, data type, hot-reloadable flag) needed for self-documenting configuration.

**Redis with pub/sub for change notifications.** Store config in Redis, subscribe to changes. Rejected because it adds an infrastructure dependency (Redis must be running and reachable), and the polling cadence of workers (30s-60s) means change notification latency doesn't matter — the worker picks up changes on its next cycle regardless.

## Consequences

**Positive:**
- Configuration changes are instant and don't require deployment.
- `EngineConfig` rows have `LastUpdatedAt` timestamps, providing an implicit audit trail.
- Run-scoped snapshots ensure reproducibility — the `ConfigSnapshotJson` on an `OptimizationRun` records exactly what configuration produced each result.
- Config diff auditing: the OptimizationWorker compares the current snapshot against the previous run's snapshot and logs any changes.
- Unknown config key detection: the OptimizationWorker warns about keys that match the prefix but aren't in the known key set, catching typos.

**Negative:**
- DB-stored configuration is invisible to standard .NET config tooling (`IConfiguration`, `IOptions<T>`). Operators must query the `EngineConfig` table directly or use the API endpoint.
- No schema validation at the storage layer — a string "abc" stored for a double key will silently fall back to the default value. Mitigated by per-worker validation on load.
- 200+ config keys is a large surface area. Without a UI or CLI tool for browsing/editing, operators must know the exact key names. The CLAUDE.md documents all keys, but this is passive documentation — it doesn't prevent misconfiguration.
