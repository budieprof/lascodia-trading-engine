# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build the entire solution
dotnet build

# Run all tests
dotnet test

# Run tests for a specific project
dotnet test LascodiaTradingEngine.UnitTest/

# Run a single test class
dotnet test --filter "FullyQualifiedName~CreateOrderCommandTest"

# Run the API (starts on port 5081)
dotnet run --project LascodiaTradingEngine.API/

# Apply EF Core migrations
dotnet ef database update --project LascodiaTradingEngine.Infrastructure/ --startup-project LascodiaTradingEngine.API/

# Add a new migration
dotnet ef migrations add <MigrationName> --project LascodiaTradingEngine.Infrastructure/ --startup-project LascodiaTradingEngine.API/
```

---

## Architecture

This is an **enterprise-grade algorithmic trading engine** built with **Clean Architecture + CQRS** targeting **.NET 10**. It supports ML-driven strategy evaluation, multi-broker failover, real-time market data, backtesting, walk-forward optimization, and comprehensive risk management — all orchestrated via background workers and an event-driven bus.

### Layer Dependency Flow

```
API → Application → Domain
API → Infrastructure → Application
Infrastructure → SharedInfrastructure (submodule)
API → SharedAPI (submodule)
```

---

## Projects

### Domain (`LascodiaTradingEngine.Domain`)

Entities inherit `Entity<long>` from `SharedDomain`. All entities use a soft-delete `IsDeleted` flag.

**Entities (26):**
| Group | Entities |
|---|---|
| Core Trading | `Order`, `Position`, `PositionScaleOrder`, `TradeSignal` |
| Strategies | `Strategy`, `StrategyAllocation`, `StrategyPerformanceSnapshot` |
| Accounts & Brokers | `TradingAccount`, `Broker`, `CurrencyPair` |
| Market Data | `Candle`, `LivePrice`, `MarketRegimeSnapshot`, `SentimentSnapshot` |
| Risk & Alerts | `Alert`, `RiskProfile`, `DrawdownSnapshot`, `ExecutionQualityLog` |
| ML Models | `MLModel`, `MLTrainingRun`, `MLModelPredictionLog`, `MLShadowEvaluation` |
| Backtesting | `BacktestRun`, `OptimizationRun`, `WalkForwardRun` |
| Expert Advisor | `EAInstance`, `EACommand` |
| Other | `EconomicEvent`, `COTReport`, `EngineConfig`, `DecisionLog` |

**Enums (35):** `OrderType`, `OrderStatus`, `TradeDirection`, `TradeSignalStatus`, `StrategyType`, `StrategyStatus`, `PositionDirection`, `PositionStatus`, `BrokerType`, `BrokerEnvironment`, `BrokerStatus`, `ExecutionType`, `Timeframe`, `TradingSession`, `TrailingStopType`, `AlertType`, `AlertChannel`, `MLModelStatus`, `ModelRole`, `ShadowEvaluationStatus`, `PromotionDecision`, `OptimizationRunStatus`, `RunStatus`, `MarketRegime`, `EconomicImpact`, `EconomicEventSource`, `SentimentSource`, `ScaleType`, `ScaleOrderStatus`, `ConfigDataType`, `RecoveryMode`, `StrategyHealthStatus`, `TriggerType`, `EACommandType`, `EAInstanceStatus`

---

### Application (`LascodiaTradingEngine.Application`)

CQRS handlers (MediatR), DTOs (AutoMapper), validators (FluentValidation), interfaces, services, and background workers.

#### CQRS Feature Structure

Every feature lives under `Application/<FeatureName>/`:

```
Orders/
  Commands/
    CreateOrder/
      CreateOrderCommand.cs           # IRequest<ResponseData<long>>
      CreateOrderCommandHandler.cs    # IRequestHandler<>
      CreateOrderCommandValidator.cs  # AbstractValidator<>
  Queries/
    GetOrder/
      GetOrderQuery.cs
      GetOrderQueryHandler.cs
    GetPagedOrders/
      GetPagedOrdersQuery.cs
      GetPagedOrdersQueryHandler.cs
  Queries/DTOs/
    OrderDto.cs                       # AutoMapper profile inline
```

- **Commands** inject `IWriteApplicationDbContext`; **Queries** inject `IReadApplicationDbContext` — never both in the same handler.
- Commands return `ResponseData<long>` (new ID) or `ResponseData<bool>`.
- List queries accept `PagerRequest` and return `ResponseData<Pager<TDto>>`.
- Publish integration events via `IEventBus` after successful writes.

#### Features (34)

| Feature | Commands | Queries |
|---|---|---|
| Alerts | Create, Update, Delete | Get, GetPaged |
| AuditTrail | Create | Get, GetPaged |
| Backtesting | RunBacktest | GetBacktestRun, GetPagedBacktestRuns |
| BrokerManagement | (operational commands) | (status queries) |
| Brokers | Create, Update, Delete, Activate, UpdateStatus | GetBroker, GetActiveBroker, GetPagedBrokers |
| CurrencyPairs | Create, Update | GetPaged |
| DrawdownRecovery | RecordDrawdownSnapshot | GetLatestDrawdownSnapshot |
| EconomicEvents | Create, Update | Get, GetPaged |
| EngineConfiguration | Update, Reset | Get, GetPaged |
| ExecutionQuality | (log commands) | (metric queries) |
| MLEvaluation | StartShadowEvaluation, RecordPredictionOutcome | GetShadowEvaluation, GetPagedEvaluations |
| MLModels | TrainModel, ActivateModel, Retrain | GetModel, GetPagedModels |
| MarketData | IngestCandle, UpdateLiveCandle | GetCandles, GetLatestCandle, GetLivePrice |
| MarketRegime | — | GetLatestRegime, GetPagedRegimeSnapshots |
| Orders | Create, Update, Delete, Cancel, Submit, Modify, UpdateOrderStatus, ClosePositionOrder | GetOrder, GetPagedOrders |
| PaperTrading | SetPaperTradingMode | GetPaperTradingStatus |
| PerformanceAttribution | — | (breakdown queries) |
| Positions | Create, Update, Close | GetPosition, GetPagedPositions |
| RateLimiting | — | GetApiQuotaStatus |
| RiskProfiles | Create, Update, Delete, AssignStrategy | GetRiskProfile, GetPagedProfiles |
| Sentiment | RecordSentiment | GetLatestSentiment |
| Strategies | Create, Update, Delete, Activate, Pause, AssignRiskProfile | GetStrategy, GetPagedStrategies |
| StrategyEnsemble | RebalanceEnsemble, UpdateStrategyAllocation | GetStrategyAllocations, GetPagedStrategyAllocations |
| StrategyFeedback | TriggerOptimization, ApproveOptimization, RejectOptimization | GetOptimizationRun, GetPagedRuns, GetStrategyPerformance |
| SystemHealth | — | (health queries) |
| TradeSignals | Create, Approve, Reject, Expire | GetTradeSignal, GetPagedTradeSignals |
| TradingAccounts | Create, Update, Delete, Activate, SyncAccountBalance | GetTradingAccount, GetActiveTradingAccount, GetPagedTradingAccounts |
| TrailingStop | UpdateTrailingStop | GetTrailingStop |
| WalkForward | RunWalkForward | GetWalkForwardRun, GetPagedRuns |
| ExpertAdvisor | RegisterEA, DeregisterEA, ProcessHeartbeat, ReceiveSymbolSpecs, RefreshSymbolSpecs, ReceiveTradingSessions, ReceiveTickBatch, ReceiveCandle, ReceiveCandleBackfill, ReceivePositionSnapshot, ReceiveOrderSnapshot, ReceiveDealSnapshot, ProcessReconciliation, AcknowledgeCommand | GetPendingCommands, GetActiveInstances |

#### Common Interfaces (`Common/Interfaces/`)

| Interface | Purpose |
|---|---|
| `IWriteApplicationDbContext` | EF write DbContext (commands) |
| `IReadApplicationDbContext` | EF read DbContext (queries) |
| `IAlertDispatcher` | Alert dispatch coordination |
| `IBrokerFailover` | Broker failover logic |
| `IBrokerOrderExecutor` | Order execution at broker |
| `IEconomicCalendarFeed` | Economic calendar data source |
| `ILivePriceCache` | Live price caching |
| `IMLModelTrainer` | ML model training |
| `IMLSignalScorer` | ML signal scoring |
| `IMarketRegimeDetector` | Market regime classification |
| `IMultiTimeframeFilter` | Multi-timeframe signal filtering |
| `INewsFilter` | News-based trading filter |
| `IPortfolioCorrelationChecker` | Correlation checks |
| `IRateLimiter` | API rate limiting |
| `IRiskChecker` | Risk validation |
| `ISessionFilter` | Trading session filtering |
| `IStrategyEvaluator` | Strategy evaluation interface |

#### Integration Events (`Common/Events/`)

- `OrderFilledIntegrationEvent`
- `PositionClosedIntegrationEvent`
- `StrategyActivatedIntegrationEvent`
- `TradeSignalCreatedIntegrationEvent`
- `MLModelActivatedIntegrationEvent`
- `BacktestCompletedIntegrationEvent`
- `EAInstanceRegisteredIntegrationEvent`

#### Services (`Services/`)

| Service Group | Implementations |
|---|---|
| Alert Channels | `AlertDispatcher`, `WebhookAlertSender`, `EmailAlertSender`, `TelegramAlertSender` |
| Broker Adapters | `OandaBrokerAdapter`, `OandaOrderExecutor`, `BrokerFailoverService` |
| Cache | `InMemoryLivePriceCache`, `InDatabaseLivePriceCache` |
| Economic Calendar | `StubEconomicCalendarFeed` |
| Filters | `NewsFilter`, `MultiTimeframeFilter`, `SessionFilter`, `PortfolioCorrelationChecker` |
| ML | `BaggedLogisticTrainer`, `MLSignalScorer`, `MarketRegimeDetector` |
| Rate Limiting | `TokenBucketRateLimiter` |
| Strategy Evaluators | `BreakoutScalperEvaluator`, `MovingAverageCrossoverEvaluator`, `RSIReversionEvaluator` |

#### Background Workers (`Workers/`)

All workers run as hosted services registered in DI.

| Category | Workers |
|---|---|
| Core Trading | `StrategyWorker`, `SignalOrderBridgeWorker`, `OrderExecutionWorker`, `PositionWorker`, `TrailingStopWorker` |
| Market & Data | `MarketDataWorker`, `RegimeDetectionWorker`, `SentimentWorker`, `COTDataWorker`, `EconomicCalendarWorker` |
| Risk & Monitoring | `RiskMonitorWorker`, `DrawdownMonitorWorker`, `DrawdownRecoveryWorker`, `ExecutionQualityCircuitBreakerWorker` |
| ML Workflow | `MLTrainingWorker`, `MLDriftMonitorWorker`, `MLCovariateShiftWorker`, `MLPredictionOutcomeWorker`, `MLShadowArbiterWorker`, `PredictionOutcomeWorker` |
| Backtesting & Optimization | `BacktestWorker`, `OptimizationWorker`, `WalkForwardWorker` |
| Other | `AccountSyncWorker`, `AlertWorker`, `StrategyFeedbackWorker`, `StrategyHealthWorker` |
| Event Handlers | `OrderFilledEventHandler`, `PositionClosedEventHandler`, `MLModelActivatedEventHandler` |

---

### Infrastructure (`LascodiaTradingEngine.Infrastructure`)

EF Core with **separate read/write contexts** and a dedicated **event log context**.

#### DbContexts (`Persistence/DbContexts/`)

| Context | Inherits | Purpose |
|---|---|---|
| `WriteApplicationDbContext` | `BaseApplicationDbContext<T>` | Command writes |
| `ReadApplicationDbContext` | `BaseApplicationDbContext<T>` | Query reads |
| `ApplicationDbContext` | — | Used for migrations only |
| `EventLogDbContext` | — | Integration event logging |

#### Entity Configurations (`Persistence/Configurations/`)

38 configuration files — one per entity, using EF Core Fluent API. All configurations apply the global `IsDeleted` soft-delete query filter.

#### Migrations (`Migrations/`)

- `InitialCreate` — Full schema bootstrap
- `MLTrainingWorkerImprovements`
- `MLModelTrainerImprovements`
- EventLog migrations live in `Migrations/EventLogDb/`

---

### API (`LascodiaTradingEngine.API`)

All controllers inherit `AuthControllerBase<T>` from the shared library. All endpoints require JWT authentication.

#### Response Codes

| Code | Meaning |
|---|---|
| `"00"` | Success |
| `"-11"` | Validation error |
| `"-14"` | Not found |

#### Controllers (`Controllers/v1/`) — 32 controllers

`OrderController`, `PositionController`, `StrategyController`, `StrategyEnsembleController`, `StrategyFeedbackController`, `TradeSignalController`, `TradingAccountController`, `BrokerController`, `BrokerManagementController`, `CurrencyPairController`, `RiskProfileController`, `AlertController`, `AuditTrailController`, `MarketDataController`, `MarketRegimeController`, `SentimentController`, `EconomicEventController`, `MLModelController`, `MLEvaluationController`, `BacktestController`, `WalkForwardController`, `DrawdownRecoveryController`, `TrailingStopController`, `ExecutionQualityController`, `PaperTradingController`, `PerformanceAttributionController`, `RateLimitingController`, `SystemHealthController`, `EngineConfigurationController`, **`ExpertAdvisorController`**

#### Infrastructure Configuration (`appsettings.json`)

```json
{
  "ConnectionStrings": {
    "WriteDbConnection": "...",
    "ReadDbConnection": "..."
  },
  "BrokerType": "rabbitmq",
  "RabbitMQConfig": { "Host": "", "Username": "", "Password": "", "QueueName": "" },
  "EmailAlertOptions": { "Host": "", "Port": 0, "Username": "", "Password": "", "FromAddress": "" },
  "TelegramAlertOptions": { "BotToken": "", "TimeoutSeconds": 10 },
  "WebhookAlertOptions": { "TimeoutSeconds": 10, "SharedSecret": "" }
}
```

---

### Unit Tests (`LascodiaTradingEngine.UnitTest`)

**Stack:** xUnit + Moq + MockQueryable

**Test classes:**
- `Application/Orders/CreateOrderCommandTest.cs`
- `Application/Strategies/CreateStrategyCommandTest.cs`
- `Application/RiskProfiles/CreateRiskProfileCommandTest.cs`
- `Application/CurrencyPairs/CreateCurrencyPairCommandTest.cs`
- `Application/MarketData/IngestCandleCommandTest.cs`
- `Application/MLModels/MLTrainingEngineTests.cs`

---

### Shared Library (Git Submodule — `submodules/shared`)

| Project | Namespace | Provides |
|---|---|---|
| `SharedDomain` | `Lascodia.Trading.Engine.SharedDomain` | `Entity<T>` base class |
| `SharedApplication` | `Lascodia.Trading.Engine.SharedApplication` | MediatR + FluentValidation wiring, DI setup |
| `SharedLibrary` | `Lascodia.Trading.Engine.SharedLibrary` | `PagerRequest`, `Pager<T>`, `ResponseData<T>`, JSON utilities |
| `SharedInfrastructure` | `Lascodia.Trading.Engine.SharedInfrastructure` | `BaseApplicationDbContext<T>` |
| `SharedAPI` | `Lascodia.Trading.Engine.SharedAPI` | `AuthControllerBase<T>`, JWT middleware |
| `EventBus` | `Lascodia.Trading.Engine.EventBus*` | Event bus abstractions |
| `EventBusRabbitMQ` | — | RabbitMQ implementation |
| `EventBusKafka` | — | Kafka implementation |
| `IntegrationEventLogEF` | — | Event log persistence with EF Core |

---

## Dependency Injection & Auto-Registration

### Auto-Registered Types (via `ConfigureAppServices` → shared library scanning the Application assembly)

These types are discovered and registered **automatically by reflection** — you do not need to add them to DI manually. Just implement the interface/base class and they will be picked up.

| Type | Registration | How auto-registered |
|---|---|---|
| `IRequestHandler<,>` (MediatR command/query handlers) | Transient | `services.AddMediatR(assembly)` |
| `AbstractValidator<T>` (FluentValidation validators) | Scoped | `services.AddValidatorsFromAssembly(assembly)` |
| AutoMapper profiles (classes inheriting `Profile`) | — | `services.AddAutoMapper(cfg => cfg.AddMaps(assembly))` |
| `IHostedService` (background workers extending `BackgroundService`) | Singleton | `AutoRegisterBackgroundJobs` — scans for all types implementing `IHostedService` |
| `IIntegrationEventHandler<T>` (event handlers) | Transient | `AutoRegisterEventHandler` — scans for all `IIntegrationEventHandler<>` implementations |
| `ConfigurationOption<T>` subclasses (options/settings) | Singleton | `AutoRegisterConfigurationOptions` — binds config section by class name and registers as singleton |

> **Important:** `IIntegrationEventHandler<T>` implementations are registered twice — once as Transient via `AutoRegisterEventHandler`, and once subscribed to the event bus via `eventBus.AutoConfigureEventHandler(assembly)` at app startup. Workers that also act as event handlers (e.g. `StrategyWorker`, `SignalOrderBridgeWorker`) are instead registered as **Singleton** explicitly in DI and forwarded to both `IHostedService` and `IIntegrationEventHandler<T>` so the same instance handles both roles.

### MediatR Pipeline Behaviours (always active, in order)

1. `UnhandledExceptionBehaviour<,>` — logs and swallows unhandled exceptions
2. `ValidationBehaviour<,>` — runs FluentValidation before the handler; throws if invalid
3. `PerformanceBehaviour<,>` — logs slow requests

### Explicitly Registered Services (Application DI)

| Interface | Implementation | Lifetime |
|---|---|---|
| `ILivePriceCache` | `InDatabaseLivePriceCache` | Singleton |
| `IBrokerDataFeed` | `OandaBrokerAdapter` | Singleton |
| `IBrokerOrderExecutor` | `OandaOrderExecutor` | Scoped |
| `IRiskChecker` | `RiskChecker` | Scoped |
| `IBacktestEngine` | `BacktestEngine` | Singleton |
| `IAlertChannelSender` | `WebhookAlertSender`, `EmailAlertSender`, `TelegramAlertSender` | Scoped (all 3 registered; `IAlertDispatcher` resolves via `IEnumerable<IAlertChannelSender>`) |
| `IAlertDispatcher` | `AlertDispatcher` | Scoped |
| `IMLModelTrainer` | `BaggedLogisticTrainer` | Scoped |
| `IMLSignalScorer` | `MLSignalScorer` | Scoped |
| `IMultiTimeframeFilter` | `MultiTimeframeFilter` | Scoped |
| `INewsFilter` | `NewsFilter` | Scoped |
| `IPortfolioCorrelationChecker` | `PortfolioCorrelationChecker` | Scoped |
| `ISessionFilter` | `SessionFilter` | Singleton |
| `IMarketRegimeDetector` | `MarketRegimeDetector` | Scoped |
| `IBrokerFailover` | `BrokerFailoverService` | Singleton |
| `IEconomicCalendarFeed` | `StubEconomicCalendarFeed` | Scoped |
| `IRateLimiter` | `TokenBucketRateLimiter` | Singleton |

### Explicitly Registered Services (Infrastructure DI)

| Interface | Implementation | Lifetime |
|---|---|---|
| `IWriteApplicationDbContext` | `WriteApplicationDbContext` | Scoped |
| `IReadApplicationDbContext` | `ReadApplicationDbContext` | Scoped |
| `IntegrationEventLogContext<EventLogDbContext>` | `EventLogDbContext` | Scoped (shares DB connection with write context) |
| `IIntegrationEventLogService` | `IntegrationEventLogService<EventLogDbContext>` | Transient |

### Explicitly Registered Services (Shared Application DI)

| Interface | Implementation | Lifetime |
|---|---|---|
| `ICurrentUserService` | `CurrentUserService` | Scoped |
| `IEventBus` | `EventBusRabbitMQ` or `EventBusKafka` (driven by `"BrokerType"` config) | Singleton |
| `IEventBusSubscriptionsManager` | `InMemoryEventBusSubscriptionsManager` | Singleton |
| `IRabbitMQPersistentConnection` | `DefaultRabbitMQPersistentConnection` | Singleton (RabbitMQ only) |
| `IIntegrationEventService` | `IntegrationEventService` | Transient |

### HTTP Clients (Named)

| Name | Purpose |
|---|---|
| *(default)* | General-purpose HTTP calls |
| `"AlertWebhook"` | Webhook alert sender — timeout from `WebhookAlertOptions` |
| `"AlertTelegram"` | Telegram alert sender — timeout from `TelegramAlertOptions` |
| `"ProxyClient"` | Proxy-aware client — reads `ProxyConfig:ProxyServer` from config |

### Other Framework Registrations (always added)

- `IMemoryCache` — size-limited (1024 entries), 25% compaction, 5-min expiry scan
- `IHealthChecks` — mapped to `/health`
- `IHttpContextAccessor`
- CORS — `AllowAnyOrigin / AllowAnyMethod / AllowAnyHeader`
- Response caching
- Authorization policy `"apiScope"` — requires authenticated user; applied to all controllers

---

## Expert Advisor Integration (EA as Exclusive Broker Adapter)

The engine's built-in broker adapters (OANDA, IB, FXCM) are **disabled**. All market data and order execution flows through MQL5 Expert Advisor instances running on MetaTrader 5. The EA project lives at `../lascodia-trading-engine-ea/`.

### Architecture Change

```
Previously:  Engine → OANDA/IB/FXCM APIs → Broker (direct connection)
Now:         Engine ← EA instances → MT5 → Broker (EA is the sole adapter)
```

- The EA streams ALL ticks, candles, symbol specs, sessions, and account state to the engine
- The engine generates trade signals; the EA polls and executes them on MT5
- If all EA instances disconnect, the engine enters `DATA_UNAVAILABLE` state
- Multiple EA instances run concurrently on different charts (one symbol per instance recommended)

### To disable built-in adapters: set `WorkerGroups.BrokerAdapters = false` in appsettings.json

### New Domain Entities

| Entity | File | Purpose |
|--------|------|---------|
| `EAInstance` | `Domain/Entities/EAInstance.cs` | Tracks registered EA instances (instanceId, ownedSymbols, isCoordinator, status, lastHeartbeat) |
| `EACommand` | `Domain/Entities/EACommand.cs` | Commands queued for EA execution (ModifySLTP, ClosePosition, CancelOrder, RequestBackfill) |

### New Domain Enums

| Enum | Values |
|------|--------|
| `EACommandType` | ModifySLTP, ClosePosition, CancelOrder, UpdateTrailing, RequestBackfill |
| `EAInstanceStatus` | Active, Disconnected, ShuttingDown |

### New Feature: ExpertAdvisor (`Application/ExpertAdvisor/`)

**Commands (14):**

| Command | Endpoint | Purpose |
|---------|----------|---------|
| `RegisterEACommand` | `POST /ea/register` | Register instance + validate symbol ownership (409 on conflict) |
| `DeregisterEACommand` | `POST /ea/deregister` | Mark instance as ShuttingDown |
| `ProcessHeartbeatCommand` | `POST /ea/heartbeat` | Update lastHeartbeat, return server time |
| `ReceiveSymbolSpecsCommand` | `POST /ea/symbol-specs` | Upsert CurrencyPair records from EA spec data |
| `RefreshSymbolSpecsCommand` | `PUT /ea/symbol-specs/refresh` | Daily swap/margin refresh |
| `ReceiveTradingSessionsCommand` | `POST /ea/trading-sessions` | Receive session schedules |
| `ReceiveTickBatchCommand` | `POST /market-data/tick/batch` | Update ILivePriceCache + publish PriceUpdatedIntegrationEvent |
| `ReceiveCandleCommand` | `POST /market-data/candle` | Upsert individual candles |
| `ReceiveCandleBackfillCommand` | `POST /market-data/candle/backfill` | Bulk-insert historical candles |
| `ReceivePositionSnapshotCommand` | `POST /ea/positions/snapshot` | Sync broker positions into engine |
| `ReceiveOrderSnapshotCommand` | `POST /ea/orders/snapshot` | Sync broker orders into engine |
| `ReceiveDealSnapshotCommand` | `POST /ea/deals/snapshot` | Update fill status from broker deals |
| `ProcessReconciliationCommand` | `POST /ea/reconciliation` | Compare engine vs broker state |
| `AcknowledgeCommandCommand` | `PUT /ea/commands/{id}/ack` | Mark EA command as processed |

**Queries (2):**

| Query | Endpoint | Purpose |
|-------|----------|---------|
| `GetPendingCommandsQuery` | `GET /ea/commands` | Unacknowledged commands for an instance |
| `GetActiveInstancesQuery` | `GET /ea/instances` | All active EA instances |

**DTOs:** `EAInstanceDto`, `EACommandDto`

### New Supporting Features

| File | Purpose |
|------|---------|
| `TradeSignals/Queries/GetPendingExecutionTradeSignals/` | `GET /trade-signal/pending-execution` -- approved signals for EA |
| `Orders/Commands/SubmitExecutionReport/` | `POST /order/{id}/execution-report` -- EA reports fill/rejection |
| `Common/Events/EAInstanceRegisteredIntegrationEvent.cs` | Published on EA registration |

### New Controller: ExpertAdvisorController

Route: `api/v1/lascodia-trading-engine/ea` with 13 endpoints (see commands/queries above).

### Modified Controllers

| Controller | Added Endpoints |
|-----------|----------------|
| `MarketDataController` | `POST tick/batch`, `POST candle`, `POST candle/backfill` |
| `TradeSignalController` | `GET pending-execution` |
| `OrderController` | `POST {id}/execution-report` |

### EA Data Flow (engine perspective)

```
STARTUP:
  EA registers → engine creates/reactivates EAInstance
  EA sends symbol specs → engine upserts CurrencyPair records
  EA sends sessions → engine stores session schedules
  EA sends position/order snapshots → engine reconciles state
  EA sends candle backfill → engine initializes indicators

RUNTIME:
  EA sends tick batches → engine updates ILivePriceCache, publishes PriceUpdatedIntegrationEvent
  EA sends closed candles → engine stores candles, triggers strategy evaluation
  Engine generates signals → EA polls GET /trade-signal/pending-execution
  EA executes signal → EA reports via POST /order/{id}/execution-report
  Engine queues commands (modify SL/TP, close) → EA polls GET /ea/commands
  EA heartbeats → engine tracks instance health

SHUTDOWN:
  EA deregisters → engine marks instance symbols as DATA_UNAVAILABLE
```

### Important: EA health monitoring

The engine should track EA heartbeats. If no heartbeat from an instance for 60 seconds, mark that instance's symbols as `DATA_UNAVAILABLE`. This prevents the engine from evaluating strategies on stale data.

---

## Key Patterns & Rules

- **Soft delete**: All entities have `IsDeleted`; EF global query filters exclude deleted rows automatically.
- **Pagination**: List queries take `PagerRequest`, return `Pager<TDto>`.
- **Event bus**: After a successful command write, publish the relevant `IntegrationEvent` via `IEventBus`.
- **CQRS separation**: Commands use `IWriteApplicationDbContext`; queries use `IReadApplicationDbContext`. Never inject both into a single handler.
- **Worker pattern**: All background workers implement `BackgroundService` and are registered as hosted services in Application DI.
- **Strategy evaluators**: Implement `IStrategyEvaluator`; registered and resolved by strategy type enum.
- **ML workflow**: Train → Shadow evaluation → Promotion decision → Activate (event triggers downstream workers).
- **Broker adapters**: Built-in adapters (`IBrokerOrderExecutor` + `IBrokerDataFeed`) are **disabled** when EA is active. The EA replaces them entirely. Set `WorkerGroups.BrokerAdapters = false`.
- **EA as broker adapter**: All market data comes from EA instances via REST API. The engine has zero direct broker connectivity when EA mode is enabled.

---

## Git Submodule Rules

The shared library lives in a **separate repository** at `submodules/shared/` (git submodule).

- **NEVER edit files directly inside `submodules/shared/`**. Changes will be overwritten on `git submodule update`.
- To modify the shared library, edit the **source repo** at its own path (e.g. `lascodia-trading-engine-shared-library/`), commit and push there, then update the submodule in the main repo:
  ```bash
  cd submodules/shared
  git pull origin master
  cd ../..
  git add submodules/shared
  git commit -m "bump shared library submodule"
  ```
- When cloning or pulling the main repo, always initialise submodules:
  ```bash
  git submodule update --init --recursive
  ```
