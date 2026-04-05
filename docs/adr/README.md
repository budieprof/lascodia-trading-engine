# Architectural Decision Records (ADRs)

This directory contains the key architectural decisions made during the design and implementation of the Lascodia Trading Engine. Each ADR documents **why** a decision was made — not what the code does (that's in CLAUDE.md and the code itself), but why it does it that way instead of the alternatives.

## Index

| # | Decision | Status |
|---|---|---|
| [0001](0001-ea-as-exclusive-broker-adapter.md) | EA as exclusive broker adapter (replacing direct broker APIs) | Accepted |
| [0002](0002-two-tier-risk-checking.md) | Two-tier risk checking (signal-level vs account-level) | Accepted |
| [0003](0003-event-driven-core-with-bounded-backpressure.md) | Event-driven core loop with bounded channel backpressure | Accepted |
| [0004](0004-separate-read-write-db-contexts.md) | Separate read/write DB contexts (CQRS) | Accepted |
| [0005](0005-ml-architecture-auto-selection-ucb1.md) | ML architecture auto-selection via UCB1 bandit | Accepted |
| [0006](0006-shadow-evaluation-before-model-promotion.md) | Shadow evaluation before model promotion (SPRT) | Accepted |
| [0007](0007-gradual-rollout-for-optimized-parameters.md) | Gradual rollout for optimized parameters (25->50->75->100%) | Accepted |
| [0008](0008-multi-instance-ea-coordination.md) | Multi-instance EA coordination via GVar CAS | Accepted |
| [0009](0009-closed-loop-strategy-lifecycle.md) | Closed-loop strategy lifecycle (generation through optimization) | Accepted |
| [0010](0010-regime-awareness-as-cross-cutting-concern.md) | Regime awareness as a cross-cutting concern | Accepted |
| [0011](0011-defense-in-depth-risk-model.md) | Defense-in-depth risk model (5 independent layers + EA-side) | Accepted |
| [0012](0012-hot-reloadable-configuration-via-db.md) | Hot-reloadable configuration via database (EngineConfig) | Accepted |

## Format

Each ADR follows this structure:

- **Context** — What problem were we solving? What constraints existed?
- **Decision** — What did we decide and how does it work?
- **Alternatives Considered** — What else did we evaluate, and why was it rejected?
- **Consequences** — What are the positive and negative implications?

## When to Write a New ADR

Write an ADR when:
- A design choice affects multiple subsystems (cross-cutting)
- The decision is non-obvious and someone re-reading the code in 6 months would ask "why?"
- There were viable alternatives that were deliberately rejected
- The decision creates tradeoffs that operators need to understand

Do not write an ADR for:
- Implementation details that are self-evident from the code
- Bug fixes or refactoring that don't change the architecture
- Library/framework choices that are standard for the stack
