# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Build & Test

```bash
dotnet build
dotnet test
dotnet test LascodiaTradingEngine.UnitTest/
dotnet test --filter "FullyQualifiedName~CreateOrderCommandTest"
dotnet run --project LascodiaTradingEngine.API/   # port 5081
dotnet ef database update --project LascodiaTradingEngine.Infrastructure/ --startup-project LascodiaTradingEngine.API/
dotnet ef migrations add <Name> --project LascodiaTradingEngine.Infrastructure/ --startup-project LascodiaTradingEngine.API/
```

> **EF Core Migration Rule:** All migrations MUST be created via `dotnet ef migrations add` — NEVER hand-edit. Manual edits break the designer snapshot and cause schema drift. To correct: `dotnet ef migrations remove`, fix the model, re-add.

---

## Architecture

Enterprise autonomous algorithmic trading engine. **Clean Architecture + CQRS** on **.NET 10**. Autonomous strategy discovery (12-gate screening pipeline — see `StrategyScreeningEngine`), plus a distinct 14-gate approval policy for optimization candidates in `OptimizationSearchEngine`. ML-driven scoring (12 learner architectures), Bayesian optimization (TPE/GP-UCB/EHVI + Hyperband), real-time data via MQL5 EA (JWT TCP bridge + REST), backtesting, 3-level walk-forward with terminal embargo, 5-layer defense-in-depth risk, regime-aware adaptation (rule+HMM), smart order routing (TWAP/VWAP), signal-level A/B (SPRT on P&L). Orchestrated via **147 background workers** and an event-driven bus. ~230k LOC, 1,645 unit tests.

**Key ADRs:** see `docs/adr/` (12 ADRs).

### Layer Dependencies

```
API → Application → Domain
API → Infrastructure → Application
Infrastructure → SharedInfrastructure (submodule)
API → SharedAPI (submodule)
```

### Projects

- **Domain** — 76 entities (all inherit `Entity<long>` from `SharedDomain`, soft-delete via `IsDeleted`), 50 enums. Groups: Core Trading, Strategies, Accounts, Market Data, Risk & Alerts, ML Models (+ Monitoring, Advanced), Backtesting, Expert Advisor, Governance, Infrastructure.
- **Application** — CQRS (MediatR), DTOs (AutoMapper), FluentValidation, interfaces (53), services (51 + 25 ML), background workers (147), integration events (20).
- **Infrastructure** — EF Core. `WriteApplicationDbContext`, `ReadApplicationDbContext`, `ApplicationDbContext` (migrations only), `EventLogDbContext` (integration events). 76+ fluent configs, all with soft-delete query filter.
- **API** — 31 controllers under `Controllers/v1/`. Most inherit `AuthControllerBase<T>`. JWT required except auth endpoints.
- **UnitTest** — xUnit + Moq + MockQueryable, 107 files across 34 directories.

### Response Codes
`"00"` success · `"-11"` validation · `"-14"` not found.

### CQRS Feature Structure

```
Orders/
  Commands/CreateOrder/
    CreateOrderCommand.cs            # IRequest<ResponseData<long>>
    CreateOrderCommandHandler.cs
    CreateOrderCommandValidator.cs
  Queries/GetOrder/
    GetOrderQuery.cs
    GetOrderQueryHandler.cs
  Queries/DTOs/OrderDto.cs
```

- Commands inject `IWriteApplicationDbContext`; queries inject `IReadApplicationDbContext` — **never both**.
- Commands return `ResponseData<long>` or `ResponseData<bool>`.
- List queries take `PagerRequest`, return `ResponseData<Pager<TDto>>`.
- After successful writes, publish integration event via `IEventBus`.

---

## Shared Library (Git Submodule — `submodules/shared/`)

| Project | Provides |
|---|---|
| `SharedDomain` | `Entity<T>` |
| `SharedApplication` | MediatR + FluentValidation DI wiring |
| `SharedLibrary` | `PagerRequest`, `Pager<T>`, `ResponseData<T>` |
| `SharedInfrastructure` | `BaseApplicationDbContext<T>` |
| `SharedAPI` | `AuthControllerBase<T>`, JWT middleware |
| `EventBus` / `EventBusRabbitMQ` / `EventBusKafka` / `IntegrationEventLogEF` | Messaging |

---

## Dependency Injection

### Auto-registered by reflection (Application assembly scan)

| Type | Lifetime |
|---|---|
| `IRequestHandler<,>` | Transient (via MediatR) |
| `AbstractValidator<T>` | Scoped |
| AutoMapper `Profile` subclasses | — |
| `IHostedService` / `BackgroundService` | Singleton |
| `IIntegrationEventHandler<T>` | Transient + subscribed to event bus |
| `ConfigurationOption<T>` subclasses | Singleton (bound by class name) |

> Workers that also handle events (`StrategyWorker`, `SignalOrderBridgeWorker`) are registered as **Singleton** and forwarded to both `IHostedService` and `IIntegrationEventHandler<T>` — same instance.

### MediatR pipeline behaviours (in order)
`UnhandledExceptionBehaviour` → `ValidationBehaviour` → `PerformanceBehaviour`.

### Explicit registrations (notable)

- **Write/Read contexts** — Scoped.
- **EventLog context** shares connection with write context.
- **`IMLModelTrainer`** — keyed by `LearnerArchitecture` enum (12 trainers); default `BaggedLogistic`.
- **`IAlertChannelSender`** — 3 implementations (webhook/email/telegram); `AlertDispatcher` is singleton, resolves senders via `IServiceScopeFactory`.
- **`ILivePriceCache`** — `InDatabaseLivePriceCache` (singleton).
- **`IEventBus`** — RabbitMQ or Kafka based on `"BrokerType"` config.
- **Named HttpClients:** default, `"AlertWebhook"`, `"AlertTelegram"`, `"ProxyClient"`.
- **Memory cache:** 1024 entry limit, 25% compaction, 5-min scan.
- **Authorization policy:** `"apiScope"` on all controllers.

---

## Expert Advisor Integration (Exclusive Broker Adapter)

Built-in broker adapters (OANDA, IB, FXCM) are **disabled**. All market data and orders flow through MQL5 EA instances on MT5. EA project: `/Users/olabodeolaleye/Developments/Software Projects/personal/Lascodia Trading Engine/lascodia-trading-engine-ea`.

```
Engine ← EA instances → MT5 → Broker
```

To disable built-in adapters: `WorkerGroups.BrokerAdapters = false`.

### Entities / Enums
- `EAInstance` (instanceId, ownedSymbols, isCoordinator, status, lastHeartbeat)
- `EACommand` (ModifySLTP, ClosePosition, CancelOrder, UpdateTrailing, RequestBackfill)
- Enums: `EACommandType`, `EAInstanceStatus` {Active, Disconnected, ShuttingDown}

### ExpertAdvisor feature — 14 commands / 2 queries under `api/v1/lascodia-trading-engine/ea`

Commands: Register, Deregister, Heartbeat, ReceiveSymbolSpecs, RefreshSymbolSpecs, ReceiveTradingSessions, ReceiveTickBatch, ReceiveCandle, ReceiveCandleBackfill, ReceivePositionSnapshot, ReceiveOrderSnapshot, ReceiveDealSnapshot, ProcessReconciliation, AcknowledgeCommand.
Queries: GetPendingCommands, GetActiveInstances.

Supporting endpoints: `GET /trade-signal/pending-execution`, `POST /order/{id}/execution-report`, `POST market-data/tick/batch`, `POST market-data/candle`, `POST market-data/candle/backfill`.

### Runtime flow

```
Startup:  register → symbol specs → sessions → position/order snapshots → candle backfill
Runtime:  tick batches → live cache + PriceUpdatedIntegrationEvent → candles → strategy eval
          engine signals → EA polls pending-execution → EA executes → reports fill
          engine queues EACommand → EA polls /ea/commands → ack
Shutdown: EA deregisters → symbols marked DATA_UNAVAILABLE
```

**Heartbeat SLA:** no heartbeat for 60s → symbols for that instance flagged `DATA_UNAVAILABLE`.

---

## Autonomous Strategy Lifecycle

```
StrategyGenerationWorker → BacktestWorker → WalkForwardWorker → StrategyHealthWorker
       ↑                                                              ↓
       └──── OptimizationWorker ←──── StrategyFeedbackWorker ←───────┘
```

### Strategy Generation
`StrategyScreeningEngine` (12-gate screening: IS/OOS, degradation, R², walk-forward, MC sign-flip, MC shuffle, marginal Sharpe, Kelly, equity curve, time concentration). Anti-bloat caps, strategic reserve for regime rotation, portfolio drawdown filter (greedy correlated removal).

### Optimization
`OptimizationSearchEngine` (TPE/GP-UCB/EHVI). Grid with adaptive bounds from approvals. Validator (purged K-fold, sensitivity, cost stress, walk-forward, temporal/portfolio correlation). 5-factor health score. 14-gate approval policy. Atomic claiming via `FOR UPDATE SKIP LOCKED`. Gradual rollout 25→50→75→100% with auto-rollback. Hyperband scheduler. Checkpointing + crash recovery.

### ML Training
- **12 architectures:** BaggedLogistic, ELM, GBM, TCN, AdaBoost, ROCKET, TabNet, FT-Transformer, SMOTE, QuantileRF, SVGP, DANN.
- `TrainerSelector` — UCB1 bandit with regime affinity + sample-count gates.
- `QualityGateEvaluator` — 9-gate (accuracy, EV, Brier, Sharpe, F1, WF-CV, ECE, BSS, OOB regression).
- `MLShadowArbiterWorker` — SPRT + z-test tournament groups before promotion.
- **82 monitoring workers** across drift (7), feature (10), calibration (9), accuracy (8), prediction (5), signal (7), lifecycle (9), advanced (21+).
- Per-trainer hardening: 4-way split, 5-signal stationarity gate (ACF/PSI/CUSUM/ADF/KPSS), GPU w/ CPU fallback, class-imbalance reject, adversarial validation, sanitized snapshots, cross-fit adaptive heads, train/inference parity audit, reliability diagrams.
- Inference: 23-step pipeline, 5-layer calibration, inference + quality circuit breakers, MC-dropout uncertainty, conformal sets, meta-label abstention, MTF blending.

### Background workers — 147 total

Categories (keep counts; see `Workers/` for exact names):

| Category | Count |
|---|---|
| Core Trading | 4 |
| Market & Data | 5 |
| Risk & Monitoring | 7 |
| Strategy Lifecycle | 5 |
| Backtesting & Optimization | 3 |
| ML Training | 3 |
| ML Drift | 7 |
| ML Feature Monitoring | 10 |
| ML Calibration | 9 |
| ML Accuracy | 8 |
| ML Prediction | 5 |
| ML Signal Management | 7 |
| ML Model Lifecycle | 9 |
| ML Advanced | 21+ |
| Infrastructure | 13+ |
| Event Handlers | 4 |

---

## CPC Encoder (V7 feature vector) — Rollout Recipe

The V7 feature vector appends a fixed-size CPC (Contrastive Predictive Coding) context embedding to the V6 raw vector (`FeatureCountV7 = FeatureCountV6 + CpcEmbeddingBlockSize = 73`). Encoders are trained and rotated by `CpcPretrainerWorker` per `(Symbol, Timeframe, Regime?)` triple. **V7 is the default feature vector** — the full V2→V7 stack is defaulted on: `MLTraining:UseExtendedFeatureVector`, `MLTraining:UseEventFeatureVector`, `MLTraining:UseTickMicrostructureFeatureVector`, `MLTraining:UseV5SyntheticMicrostructure`, `MLTraining:UseV6OrderBookFeatures`, and `MLTraining:UseV7CpcFeatureVector` all default to `true`. Each individual layer's data slots zero-fill when its upstream source is missing, so training never stalls: a missing DXY/US10Y/VIX stream degrades V3, missing ticks degrade V4/V5, missing DOM snapshots degrade V6, and a missing `MLCpcEncoder` zero-fills the CPC block. Operators can still opt a layer off by inserting an `EngineConfig` row with the corresponding key set to `false`.

### Operational rollout path

All keys live in the `EngineConfig` table and are hot-reloadable — no redeploy required to flip any of these.

**1. Let encoders train (cheap, safe).** Leave all config at defaults. `CpcPretrainerWorker` runs under `WorkerBulkhead.MLTraining` every `MLCpc:PollIntervalSeconds` (3600s) and produces one `MLCpcEncoder` per `(Symbol, Timeframe)` pair that has an active `MLModel`. You can monitor:

```sql
SELECT "Symbol", "Timeframe", "Regime", "EncoderType", "InfoNceLoss", "TrainedAt"
  FROM "MLCpcEncoder" WHERE "IsActive" AND NOT "IsDeleted"
  ORDER BY "TrainedAt" DESC;
```

**2. Enable V7 training for a single pair first (staged).** V7 is already on by default — you don't need to insert the key. You only need a row if you want to temporarily *disable* V7 for a pilot window (see step 4). Enqueue a training run for the pilot pair via `POST /api/v1/lascodia-trading-engine/ml-training/queue` (or however your pipeline currently enqueues runs) and confirm the resulting snapshot:

```sql
SELECT m."Symbol", m."Timeframe",
       (m."ModelBytes"::jsonb->>'ExpectedInputFeatures')::int   AS expected_features,
       (m."ModelBytes"::jsonb->>'FeatureSchemaVersion')::int   AS schema_version
  FROM "MLModel" m WHERE m."IsActive" AND NOT m."IsDeleted"
  ORDER BY m."Id" DESC LIMIT 5;
```

A healthy V7 model shows `expected_features=73` and `schema_version=7`.

**3. Compare V7 vs V6 on live data.** Run both models through `MLShadowArbiterWorker`'s SPRT tournament (standard ML-promotion pipeline). V7 wins only if it materially beats V6 on the out-of-sample signal PnL test. If it loses, proceed to step 4.

**4. Rollback** — insert (or update) the config row to false:

```sql
INSERT INTO "EngineConfig" ("Key", "Value", "DataType", "IsHotReloadable", "LastUpdatedAt")
  VALUES ('MLTraining:UseV7CpcFeatureVector', 'false', 2, TRUE, NOW())
  ON CONFLICT ("Key") DO UPDATE SET "Value"='false', "LastUpdatedAt"=NOW();
```

New training runs immediately fall back to V6. Existing V7 snapshots keep scoring (their `FeatureSchemaVersion=7` routes through `MLSignalScorer`'s V7 dispatch); rotate them out via the usual retirement path if you want to fully drop V7.

### Encoder architecture upgrades

`MLCpc:EncoderType` (values: `Linear` / `Tcn`) picks the architecture `CpcPretrainerWorker` produces on the next cycle. Default `Linear` — single-step `ReLU(W·x)`, lightweight, ~200-byte payload. `Tcn` uses a 2-layer dilated causal convolution with residual (receptive field ≈ 7 steps) — measurably slower to train, captures temporal context. Switch only if linear V7 underperforms V6 on soak data; existing Linear-typed `MLCpcEncoder` rows keep working because `CpcEncoderProjection` dispatches on `EncoderType`.

### Per-regime encoders

`MLCpc:TrainPerRegime=true` makes the worker enumerate each `MarketRegime` value per pair and train regime-specific encoders using `MarketRegimeSnapshot` to partition candles by the regime active at each bar. Inference (`MLSignalScorer` V7 dispatch) passes the current regime to `IActiveCpcEncoderProvider`, which prefers a regime-specific encoder and falls back to the global (null-regime) row if none exists. Default **off** — per-regime training multiplies the worker's per-cycle workload and is only worth turning on once you have soak evidence that a single global encoder per pair is too coarse.

### V7 monitoring quick checks

```sql
-- V7 adoption rate on active models
SELECT COALESCE((m."ModelBytes"::jsonb->>'FeatureSchemaVersion')::int, 0) AS schema_version,
       COUNT(*) AS active_count
  FROM "MLModel" m WHERE m."IsActive" AND NOT m."IsDeleted"
  GROUP BY schema_version ORDER BY schema_version;

-- CPC encoder health per pair
SELECT "Symbol", "Timeframe", "Regime", "EncoderType",
       AGE(NOW(), "TrainedAt") AS age, "InfoNceLoss"
  FROM "MLCpcEncoder" WHERE "IsActive" AND NOT "IsDeleted"
  ORDER BY "TrainedAt" ASC;

-- Recent CPC pretraining decisions and quality-gate outcomes
SELECT "Symbol", "Timeframe", "Regime", "EncoderType",
       "Outcome", "Reason", "PriorInfoNceLoss",
       "TrainInfoNceLoss", "ValidationInfoNceLoss",
       "TrainingSequences", "ValidationSequences", "TrainingDurationMs",
       "EvaluatedAt"
  FROM "MLCpcEncoderTrainingLog" WHERE NOT "IsDeleted"
  ORDER BY "EvaluatedAt" DESC LIMIT 50;
```

---

## Authentication & Accounts

- `Broker` entity removed — broker name/server live on `TradingAccount`. Login = AccountId + BrokerServer.
- `POST /api/v1/lascodia-trading-engine/auth/register` `[AllowAnonymous]` — self-register + auto-login, returns JWT.
- `POST /api/v1/lascodia-trading-engine/auth/login` `[AllowAnonymous]` — EA: passwordless; Web: password required.
- `PUT /api/v1/lascodia-trading-engine/trading-account/{id}/password` — change password.
- Passwords AES-256-GCM encrypted via `FieldEncryption`, stored in `TradingAccount.EncryptedPassword`. Auto-generated default on registration.
- JWT scoped to a single `TradingAccount`.

### Two-tier risk checking

- **Tier 1 (`ISignalValidator`)** — account-agnostic. Runs in `SignalOrderBridgeWorker`. Checks: expiry, lot positivity, SL/TP direction, min SL distance, R:R ratio, mandatory SL, ML agreement. Rejects/approves signal.
- **Tier 2 (`IRiskChecker`)** — account-specific. Runs in `CreateOrderFromSignalCommand` (`POST /order/from-signal`). Checks: margin, exposure, position limits, drawdown, lot constraints, spread. Tier 2 failure records `SignalAccountAttempt`, does NOT reject the signal — other accounts can still try.

Flow: `StrategyWorker` → signal → `SignalOrderBridgeWorker` (Tier 1) → EA/dashboard calls `POST /order/from-signal` → Tier 2 → order.

---

## Infrastructure Configuration (`appsettings.json`)

```json
{
  "ConnectionStrings": { "WriteDbConnection": "...", "ReadDbConnection": "..." },
  "BrokerType": "rabbitmq",
  "RabbitMQConfig": { "Host": "", "Username": "", "Password": "", "QueueName": "" },
  "EmailAlertOptions": { "Host": "", "Port": 0, "Username": "", "Password": "", "FromAddress": "" },
  "TelegramAlertOptions": { "BotToken": "", "TimeoutSeconds": 10 },
  "WebhookAlertOptions": { "TimeoutSeconds": 10, "SharedSecret": "" }
}
```

---

## Key Patterns & Rules

- **No manual gates for auto-generated strategies.** Engine is fully autonomous for strategies produced by the generation / optimization pipeline. Never introduce manual approval workflows, four-eyes gates, or synchronous human-in-the-loop on that path. Oversight is via monitoring, alerts, risk limits, circuit breakers, kill switches. The one retained human path is `ActivateStrategyCommand` + `IPromotionGateValidator` for **human-introduced** strategies (operator-created, imported from research notebooks, etc.); `StrategyPromotionWorker` enforces this boundary via `StrategyPromotion:AutoActivateEnabled` (default true) — auto-generated strategies whose name starts with `Auto-` bypass the gate; others still must pass it.
- **Soft delete.** All entities have `IsDeleted`; EF global filters exclude automatically.
- **Pagination.** List queries → `PagerRequest` in, `Pager<TDto>` out.
- **Event bus.** After successful writes, publish the relevant `IntegrationEvent`.
- **CQRS separation.** Commands = write ctx; queries = read ctx. Never mix.
- **Workers.** All extend `BackgroundService`, registered as hosted services.
- **Strategy evaluators.** Implement `IStrategyEvaluator`; resolved by strategy type enum.
- **ML workflow.** Train → 9 quality gates → shadow eval (SPRT tournament) → signal-level A/B (SPRT P&L) → promotion → activate → 82 monitoring workers.
- **Broker adapters.** Disabled when EA is active — EA is sole adapter.
- **Defense-in-depth risk.** 5 layers: SignalValidator → RiskChecker → StrategyHealthWorker → DrawdownRecoveryWorker → ExecutionQualityCircuitBreaker + EA-side circuit breakers. Tier 2 drawdown recovery enforces lot reduction on ALL order paths. `RiskCheckerPipeline` fail-closed: 5 errors → block all orders. EVT tail (GPD via MLE + Nelder-Mead). Monte Carlo VaR (Cholesky ridge). Correlated reverse stress (PCA). Gap model distinguishes weekends vs holidays. Linear-interpolated VaR. Emergency-flatten dedup persists across restarts.
- **Regime awareness.** Cross-cutting. Rule + HMM (k-means++ init). Wilder's ADX. Per-symbol/timeframe state. Confidence floor 0.15 w/ Ranging fallback. H1/H4/D1 coherence. HighVolatility is directional. Transition guard dampens ML post-change. Hot-swap regime-specific superseded models.
- **Gradual rollout.** 25→50→75→100% with weighted-linear-regression rollback detection (ADR-0007).
- **Hot-reloadable config.** 200+ params in `EngineConfig` table, batch-loaded, run-scoped snapshots for reproducibility (ADR-0012).
- **Backtesting correctness.** Gap slippage pessimistic on both SL and TP gaps. RSI swing detection backward-only. NaN/Infinity filtered. Walk-forward ≥3 windows, 10% terminal embargo, sample stddev (Bessel).
- **Smart order routing.** Auto Direct/TWAP/VWAP by size vs ADV. TWAP ±10% jitter + lot-step rounding. VWAP tick-activity-weighted. Pre- and post-trade TCA.
- **Performance attribution.** TWRR, rolling Sharpe/Sortino/Calmar (7/30-day), position-weighted benchmark, information ratio, ML alpha, timing alpha. Per-strategy via Order→Strategy FK.
- **Worker orchestration.** 147 workers, 7 groups, 4-tier startup. Connection pool 200 w/ retry. State-aware idempotent handlers. Crash alerts via dispatcher. EA all-disconnect → `DataUnavailable` mode.
- **EA integration.** JWT w/ full signature verification. NaN/Infinity tick filtering. Idempotent tick batches via `X-Request-Id`. Symbol ownership enforced, reassigned on deregister, re-claimed on reconnect. 24h command TTL.
- **Trading account-centric.** No `Broker` entity; login is AccountId + BrokerServer.

---

## Git Submodule Rules

Shared library is a **separate repo** at `submodules/shared/`.

- **NEVER edit files inside `submodules/shared/`.** Changes are overwritten on `git submodule update`.
- Edit the source repo (`lascodia-trading-engine-shared-library/`), commit + push, then bump the submodule pointer:
  ```bash
  cd submodules/shared
  git pull origin master
  cd ../..
  git add submodules/shared
  git commit -m "bump shared library submodule"
  ```
- On clone or pull, always: `git submodule update --init --recursive`.
