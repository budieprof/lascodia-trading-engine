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

> **IMPORTANT: EF Core Migration Rule**
> All migration files MUST be created using `dotnet ef migrations add` — NEVER create or edit migration files manually. The EF Core tooling generates the migration designer snapshot and SQL from the current model state. Manual edits break the migration chain and cause schema drift. If a migration needs correction, use `dotnet ef migrations remove` to delete it and re-run `dotnet ef migrations add` with the corrected model.

---

## Architecture

This is an **enterprise-grade autonomous algorithmic trading engine** built with **Clean Architecture + CQRS** targeting **.NET 10**. It supports autonomous strategy discovery and screening (14-gate pipeline), ML-driven signal scoring (12 learner architectures, all A+ hardened), Bayesian parameter optimization (TPE/GP-UCB/EHVI + Hyperband), real-time market data via MQL5 EA (JWT-authenticated TCP bridge + REST), backtesting (corrected gap slippage, no look-ahead bias), walk-forward analysis (3-level anchored with terminal embargo), 5-layer defense-in-depth risk management (EVT tail modeling, correlated PCA stress testing), regime-aware adaptation (hybrid rule+HMM with k-means++ initialization), smart order routing (TWAP/VWAP with auto-selection), and signal-level A/B testing (SPRT on P&L) — all orchestrated via 147 background workers and an event-driven bus.

### Engine Hardening Summary (April 2026 Review)

A comprehensive multi-pass code review was conducted across 13 subsystems (~230,000 lines). **196 issues were found and fixed**, including 12 critical bugs. All 1,645 unit tests pass. Key improvements:

| Subsystem | Issues Fixed | Key Changes |
|---|---|---|
| ML Trainers (12 arch.) | 22 | 4-way split, 5-signal drift gate, GPU acceleration, full sanitization, train/inference audit |
| ML Pipeline | 18 | Promotion race fix, bulkhead sizing, inter-worker coordination, online learning |
| Strategy Lifecycle | 16 | Configurable thresholds, per-strategy SaveChangesAsync, drift gate with REJECT |
| Optimization Pipeline | 14 | Approval idempotency, per-phase timeouts, deferred run TTL, query batching |
| Signal Generation | 24 | SL==Entry guard, isotonic monotonicity, Hawkes timezone fix, MTF ordering |
| Risk Management | 19+4 features | EVT (GPD tail fit), correlated stress (PCA), RiskChecker circuit breaker, Tier 2 drawdown enforcement |
| Worker Orchestration | 14 | Connection pool 200, stale event re-publish, state-aware idempotent handlers, crash alerts |
| EA Integration | 13 | JWT signature verification (critical), NaN tick validation, symbol reassignment on deregister |
| Backtesting | 7 | Gap slippage inversion fix, RSI swing look-ahead fix, NaN guards, terminal embargo |
| Walk-Forward | 7 | Minimum 3 windows, sample stddev, IS+OOS validation, window sizing rounding |
| Market Regime | 12+1 | Wilder's ADX, k-means++ HMM init, per-symbol state isolation, confidence floor |
| Smart Order Routing | 10 | Thread-safe RNG, lot step rounding, auto-algorithm selection, pre-trade cost estimation |
| Performance Attribution | 10+3 | TWRR, Sharpe/Sortino/Calmar, position-weighted benchmark, ML alpha/timing alpha decomposition |

**New features implemented:** EVT tail modeling (GPD), correlated stress testing (PCA), RiskChecker circuit breaker, signal-level A/B testing (SPRT on P&L), versioned feature store, effective online learning, k-means++ HMM initialization.

**Key architectural decisions are documented in [docs/adr/](docs/adr/README.md)** (12 ADRs covering EA integration, risk model, ML selection, optimization rollout, etc.).

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

**Entities (76):**
| Group | Entities |
|---|---|
| Core Trading | `Order`, `Position`, `PositionScaleOrder`, `TradeSignal`, `SignalAccountAttempt`, `SignalAllocation` |
| Strategies | `Strategy`, `StrategyAllocation`, `StrategyPerformanceSnapshot`, `StrategyRegimeParams`, `StrategyVariant`, `StrategyCapacity` |
| Accounts | `TradingAccount`, `CurrencyPair`, `AccountPerformanceAttribution` |
| Market Data | `Candle`, `LivePrice`, `MarketRegimeSnapshot`, `SentimentSnapshot`, `TickRecord`, `OrderBookSnapshot`, `SpreadProfile`, `MarketDataAnomaly` |
| Risk & Alerts | `Alert`, `RiskProfile`, `DrawdownSnapshot`, `ExecutionQualityLog`, `TransactionCostAnalysis`, `StressTestScenario`, `StressTestResult` |
| ML Models | `MLModel`, `MLTrainingRun`, `MLModelPredictionLog`, `MLShadowEvaluation`, `MLModelLifecycleLog` |
| ML Monitoring | `MLModelRegimeAccuracy`, `MLModelSessionAccuracy`, `MLModelVolatilityAccuracy`, `MLModelHourlyAccuracy`, `MLModelEwmaAccuracy`, `MLModelHorizonAccuracy`, `MLShadowRegimeBreakdown` |
| ML Advanced | `MLAdwinDriftLog`, `MLCausalFeatureAudit`, `MLConformalCalibration`, `MLConformalBreakerLog`, `MLCorrelatedFailureLog`, `MLErgodicityLog`, `MLFeatureConsensusSnapshot`, `MLFeatureInteractionAudit`, `MLFeatureStalenessLog`, `MLHawkesKernelParams`, `MLKellyFractionLog`, `MLMrmrFeatureRanking`, `MLPeltChangePointLog`, `MLStackingMetaModel`, `MLTemperatureScalingLog`, `MLVaeEncoder`, `MLCpcEncoder` |
| Backtesting | `BacktestRun`, `OptimizationRun`, `WalkForwardRun` |
| Expert Advisor | `EAInstance`, `EACommand` |
| Governance | `ProcessedIdempotencyKey`, `EngineConfigAuditLog` |
| Infrastructure | `EconomicEvent`, `COTReport`, `EngineConfig`, `DecisionLog`, `DeadLetterEvent`, `FeatureVector`, `TradeRationale`, `WorkerHealthSnapshot` |

**Enums (50):** `OrderType`, `OrderStatus`, `TradeDirection`, `TradeSignalStatus`, `StrategyType`, `StrategyStatus`, `PositionDirection`, `PositionStatus`, `ExecutionType`, `Timeframe`, `TradingSession`, `TrailingStopType`, `AlertType`, `AlertChannel`, `AlertSeverity`, `MLModelStatus`, `ModelRole`, `ShadowEvaluationStatus`, `PromotionDecision`, `OptimizationRunStatus`, `OptimizationFailureCategory`, `ValidationFollowUpStatus`, `RunStatus`, `MarketRegime`, `EconomicImpact`, `EconomicEventSource`, `SentimentSource`, `ScaleType`, `ScaleOrderStatus`, `ConfigDataType`, `RecoveryMode`, `StrategyHealthStatus`, `StrategyLifecycleStage`, `TriggerType`, `EACommandType`, `EAInstanceStatus`, `LearnerArchitecture`, `ElmActivation`, `TcnActivation`, `AccountType`, `MarginMode`, `TimeInForce`, `ExecutionAlgorithmType`, `ApprovalOperationType`, `ApprovalStatus`, `StressScenarioType`, `MarketDataAnomalyType`, `DegradationMode`, `TradeExitReason`

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

#### Features (40+)

| Feature | Commands | Queries |
|---|---|---|
| Alerts | Create, Update, Delete | Get, GetPaged |
| AuditTrail | Create | Get, GetPaged |
| Backtesting | RunBacktest | GetBacktestRun, GetPagedBacktestRuns |
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
| TradingAccounts | Create, Update, Delete, Activate, SyncAccountBalance, ChangePassword, RegisterTrader, LoginTradingAccount | GetTradingAccount, GetActiveTradingAccount, GetPagedTradingAccounts |
| TrailingStop | UpdateTrailingStop | GetTrailingStop |
| WalkForward | RunWalkForward | GetWalkForwardRun, GetPagedRuns |
| ExpertAdvisor | RegisterEA, DeregisterEA, ProcessHeartbeat, ReceiveSymbolSpecs, RefreshSymbolSpecs, ReceiveTradingSessions, ReceiveTickBatch, ReceiveCandle, ReceiveCandleBackfill, ReceivePositionSnapshot, ReceiveOrderSnapshot, ReceiveDealSnapshot, ProcessReconciliation, AcknowledgeCommand | GetPendingCommands, GetActiveInstances |

#### Common Interfaces (`Common/Interfaces/`) — 53 interfaces

| Interface | Purpose |
|---|---|
| `IWriteApplicationDbContext` | EF write DbContext (commands) |
| `IReadApplicationDbContext` | EF read DbContext (queries) |
| `IAlertDispatcher` | Alert dispatch coordination |
| `ILivePriceCache` | Live price caching |
| `IMLModelTrainer` | ML model training (keyed by `LearnerArchitecture`) |
| `IMLSignalScorer` | ML signal scoring |
| `IBatchMLSignalScorer` | Batch ML scoring for multiple signals |
| `ITrainerSelector` | UCB1 bandit architecture auto-selection |
| `IMarketRegimeDetector` | Market regime classification |
| `IMultiTimeframeFilter` | Multi-timeframe signal filtering |
| `INewsFilter` | News-based trading filter |
| `IPortfolioCorrelationChecker` | Portfolio correlation checks |
| `IPortfolioRiskCalculator` | Portfolio VaR and risk calculations |
| `IRateLimiter` | API rate limiting |
| `IRiskChecker` | Tier 2 account-level risk validation |
| `ISignalValidator` | Tier 1 signal-level validation |
| `ISessionFilter` | Trading session filtering |
| `IStrategyEvaluator` | Strategy evaluation interface |
| `IFeatureStore` | ML feature storage and retrieval |
| `ISignalConflictResolver` | Cross-strategy signal dedup and conflict resolution |
| `IDistributedLock` | Distributed locking (promotions, evaluations) |
| `IEconomicCalendarFeed` | Economic calendar data source |
| `IHawkesSignalFilter` | Hawkes process signal clustering detection |
| `ISignalAllocationEngine` | Signal allocation across accounts |
| `ISmartOrderRouter` | Smart order routing (TWAP, VWAP) |
| `IStrategyCapacityEstimator` | Strategy capacity estimation |
| `IStressTestEngine` | Stress test scenario execution |
| `ITransactionCostAnalyzer` | Transaction cost analysis |
| `IDeadLetterSink` | Dead letter event storage |
| `IGapRiskModel` | Weekend/holiday gap risk model |
| `ICorrelationRiskAnalyzer` | Cross-asset correlation analysis |
| `IWorkerHealthMonitor` | Background worker health tracking |
| + 20 more (ML pre-trainers, explainers, providers) |

#### Integration Events (`Common/Events/`) — 20 events

- `PriceUpdatedIntegrationEvent` — tick data received from EA (drives StrategyWorker)
- `TradeSignalCreatedIntegrationEvent` — new signal generated (drives SignalOrderBridgeWorker)
- `OrderFilledIntegrationEvent` — order filled by broker
- `OrderCreatedIntegrationEvent` — order created in engine
- `PositionOpenedIntegrationEvent` — new position opened
- `PositionClosedIntegrationEvent` — position closed (drives PredictionOutcomeWorker)
- `StrategyActivatedIntegrationEvent` — strategy activated
- `StrategyCandidateCreatedIntegrationEvent` — auto-generated strategy candidate
- `StrategyAutoPromotedIntegrationEvent` — elite candidate fast-tracked
- `StrategyGenerationCycleCompletedIntegrationEvent` — generation cycle summary
- `MLModelActivatedIntegrationEvent` — ML model promoted to active
- `BacktestCompletedIntegrationEvent` — backtest finished (seeds WalkForwardRun)
- `OptimizationCompletedIntegrationEvent` — optimization run completed
- `OptimizationApprovedIntegrationEvent` — optimization auto-approved
- `EAInstanceRegisteredIntegrationEvent` — EA instance registered
- `EmergencyFlattenIntegrationEvent` — emergency position flatten
- `VaRBreachIntegrationEvent` — portfolio VaR limit breached
- `StressTestCompletedIntegrationEvent` — stress test scenario completed
- `MarketDataAnomalyIntegrationEvent` — data quality anomaly detected

#### Services (`Services/`) — 51 top-level + 25 ML services

| Service Group | Implementations |
|---|---|
| Alert Channels | `AlertDispatcher`, `WebhookAlertSender`, `EmailAlertSender`, `TelegramAlertSender` |
| Signal Processing | `SignalConflictResolver`, `SignalAllocationEngine`, `RegimeCoherenceChecker` |
| Risk & Portfolio | `PortfolioRiskCalculator`, `CorrelationRiskAnalyzer`, `GapRiskModel`, `StressTestEngine`, `TransactionCostAnalyzer`, `PortfolioOptimizer` |
| Execution | `SmartOrderRouter`, `TwapExecutionAlgorithm`, `VwapExecutionAlgorithm`, `PartialFillRedistributor` |
| Market Data | `MarketRegimeDetector`, `RegimeClassificationVerifier`, `MarketDataAnomalyDetector`, `SpreadProfileProvider`, `NewsProximityProvider` |
| ML Scoring | `MLSignalScorer`, `BatchMLSignalScorer`, `LatencyAwareMLSignalScorer`, `MLModelResolver`, `MLConfigService`, `ParallelShadowScorer` |
| ML Training (12 architectures) | `BaggedLogisticTrainer`, `ElmModelTrainer`, `GbmModelTrainer`, `TcnModelTrainer`, `AdaBoostModelTrainer`, `RocketModelTrainer`, `TabNetModelTrainer`, `FtTransformerModelTrainer`, `SmoteModelTrainer`, `QuantileRfModelTrainer`, `SvgpModelTrainer`, `DannModelTrainer` |
| ML Support | `TrainerSelector` (UCB1 bandit), `EnsembleCommitteeBlender`, `OnnxInferenceEngine`, `OodDetector`, `HawkesSignalFilter`, `CounterfactualExplainer` |
| ML Pre-trainers | `SelfSupervisedPretrainer`, `VaePretrainer`, `CpcPretrainer` |
| Feature Store | `DatabaseFeatureStore` |
| Monitoring | `LivePerformanceBenchmark`, `PortfolioEquityCurveProvider`, `WorkerHealthMonitor`, `EngineMonitoringService`, `StrategyCapacityEstimator` |
| Infrastructure | `DeadLetterSink`, `IdempotencyGuard`, `DataRetentionManager`, `DegradationModeManager` |
| Strategy Evaluators | `BreakoutScalperEvaluator`, `MovingAverageCrossoverEvaluator`, `RSIReversionEvaluator` |

#### Background Workers (`Workers/`) — 147 workers

All workers run as hosted services registered in DI.

| Category | Workers | Count |
|---|---|---|
| Core Trading | `StrategyWorker` (event-driven, bounded channel), `SignalOrderBridgeWorker` (event-driven), `PositionWorker`, `TrailingStopWorker` | 4 |
| Market & Data | `RegimeDetectionWorker`, `EconomicCalendarWorker`, `CandleAggregationWorker`, `CorrelationMatrixWorker`, `SpreadProfileWorker` | 5 |
| Risk & Monitoring | `RiskMonitorWorker`, `DrawdownMonitorWorker`, `DrawdownRecoveryWorker`, `ExecutionQualityCircuitBreakerWorker`, `PortfolioRiskWorker`, `StressTestWorker`, `DailyPnlMonitorWorker` | 7 |
| Strategy Lifecycle | `StrategyGenerationWorker`, `StrategyHealthWorker`, `StrategyFeedbackWorker`, `StrategyCapacityWorker`, `StrategyPromotionWorker` | 5 |
| Backtesting & Optimization | `BacktestWorker`, `OptimizationWorker` (4 partial files), `WalkForwardWorker` | 3 |
| ML Training | `MLTrainingWorker`, `MLTrainingRunHealthWorker`, `MLTrainingDataFreshnessWorker` | 3 |
| ML Drift Detection (7) | `MLDriftMonitorWorker`, `MLDriftAgreementWorker`, `MLAdwinDriftWorker`, `MLCusumDriftWorker`, `MLMultiScaleDriftWorker`, `MLStructuralBreakWorker`, `MLPeltChangePointWorker` | 7 |
| ML Feature Monitoring (10) | `MLCovariateShiftWorker`, `MLFeatureDataFreshnessWorker`, `MLFeatureStalenessWorker`, `MLFeatureConsensusWorker`, `MLFeatureInteractionWorker`, `MLFeatureImportanceTrendWorker`, `MLFeaturePsiWorker`, `MLFeatureRankShiftWorker`, `MLMrmrFeatureWorker`, `MLCausalFeatureWorker` | 10 |
| ML Calibration (9) | `MLCalibrationMonitorWorker`, `MLThresholdCalibrationWorker`, `MLTemperatureScalingWorker`, `MLOnlinePlattWorker`, `MLIsotonicRecalibrationWorker`, `MLProductionCalibrationWorker`, `MLRecalibrationWorker`, `MLConformalCalibrationWorker`, `MLConformalRecalibrationWorker` | 9 |
| ML Accuracy (8) | `MLRegimeAccuracyWorker`, `MLSessionAccuracyWorker`, `MLVolatilityAccuracyWorker`, `MLTimeOfDayAccuracyWorker`, `MLEwmaAccuracyWorker`, `MLHorizonAccuracyWorker`, `MLRollingAccuracyWorker`, `MLHourlyAccuracyWorker` | 8 |
| ML Prediction (5) | `MLPredictionOutcomeWorker`, `MLPredictionPnlWorker`, `MLPredictionSharpnessWorker`, `MLPredictionSkewWorker`, `MLPredictionLogPruningWorker` | 5 |
| ML Signal Management (7) | `MLSignalSuppressionWorker`, `MLSignalCooldownWorker`, `MLSignalFunnelWorker`, `MLSignalCoverageAuditWorker`, `MLSuppressionRollbackWorker`, `MLCorrelatedSignalConflictWorker`, `MLCorrelatedFailureWorker` | 7 |
| ML Model Lifecycle (9) | `MLShadowArbiterWorker`, `MLModelWarmupWorker`, `MLModelRetirementWorker`, `MLModelDistillationWorker`, `MLModelSoupWorker`, `MLInferenceWarmupWorker`, `MLArchitectureRotationWorker`, `MLTransferLearningWorker`, `MLModelActivatedEventHandler` | 9 |
| ML Advanced (15+) | `MLStackingMetaLearnerWorker`, `MLSharpeEnsembleWorker`, `MLEnsembleDiversityRecoveryWorker`, `MLAdaptiveThresholdWorker`, `MLCalibratedEdgeWorker`, `MLKellyFractionWorker`, `MLPositionSizeAdvisorWorker`, `MLRewardToRiskWorker`, `MLResourceGuardWorker`, `MLDegradationModeWorker`, `MLDeadLetterWorker`, `MLOnlineLearningWorker`, `MLMetricsExportWorker`, `MLRegimeHotSwapWorker`, `MLRegimeTransitionGuardWorker`, `MLDataQualityWorker`, `MLDirectionStreakWorker`, `MLErgodicityWorker`, `MLHawkesProcessWorker`, `MLPsiAutoRetrainWorker`, `MLConformalBreakerWorker` | 21+ |
| Infrastructure | `DataRetentionWorker`, `WorkerHealthWorker`, `DeadLetterCleanupWorker`, `StaleOrderRecoveryWorker`, `EAHealthMonitorWorker`, `ReconciliationWorker`, `IntegrationEventRetryWorker`, `TransactionCostWorker`, `PerformanceAttributionWorker`, `EACommandPushWorker`, `TcpBridgeWorker`, `PartialFillResubmissionWorker`, `FeatureStoreBackfillWorker` | 13+ |
| Event Handlers | `OrderFilledEventHandler`, `PositionClosedEventHandler`, `MLModelActivatedEventHandler`, `VaRRecalculationEventHandler` | 4 |

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

76+ configuration files — one per entity, using EF Core Fluent API. All configurations apply the global `IsDeleted` soft-delete query filter.

#### Migrations (`Migrations/`)

- `InitialCreate` — Full schema bootstrap
- `MLTrainingWorkerImprovements`
- `MLModelTrainerImprovements`
- EventLog migrations live in `Migrations/EventLogDb/`

---

### API (`LascodiaTradingEngine.API`)

Most controllers inherit `AuthControllerBase<T>` from the shared library. All endpoints require JWT authentication except the auth endpoints.

#### Response Codes

| Code | Meaning |
|---|---|
| `"00"` | Success |
| `"-11"` | Validation error |
| `"-14"` | Not found |

#### Controllers (`Controllers/v1/`) — 31 controllers

`OrderController`, `PositionController`, `StrategyController`, `StrategyEnsembleController`, `StrategyFeedbackController`, `TradeSignalController`, `TradingAccountController`, **`TradingAccountAuthController`** (`[AllowAnonymous]`), **`AuthTokenController`**, `CurrencyPairController`, `RiskProfileController`, `AlertController`, `AuditTrailController`, `MarketDataController`, `MarketRegimeController`, `SentimentController`, `EconomicEventController`, `MLModelController`, `MLEvaluationController`, `BacktestController`, `WalkForwardController`, `DrawdownRecoveryController`, `TrailingStopController`, `ExecutionQualityController`, `PaperTradingController`, `PerformanceAttributionController`, `RateLimitingController`, `SystemHealthController`, `EngineConfigurationController`, `DeadLetterController`, **`ExpertAdvisorController`**

#### Authentication Endpoints (TradingAccountAuthController)

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `POST /api/v1/lascodia-trading-engine/auth/register` | RegisterTraderCommand | `[AllowAnonymous]` | Self-registration + auto-login (returns JWT) |
| `POST /api/v1/lascodia-trading-engine/auth/login` | LoginTradingAccountCommand | `[AllowAnonymous]` | Login (EA: passwordless, Web: password required) |

#### TradingAccount Password Management

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `PUT /api/v1/lascodia-trading-engine/trading-account/{id}/password` | ChangePasswordCommand | Change account password |

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
**Total:** 107 test files across 34 test directories

**Key test areas:**
- `Application/Workers/OptimizationWorkerTest.cs` — 122 tests covering optimization pipeline
- `Application/Workers/StrategyGenerationWorkerTest.cs` + `StrategyGenerationTests.cs` — generation/screening tests
- `Application/Workers/MLTrainingWorkerTest.cs` — ML training pipeline tests
- `Application/Workers/MLDriftMonitorWorkerTest.cs`, `MLAdwinDriftWorkerTest.cs`, `MLCusumDriftWorkerTest.cs` — drift detection tests
- `Application/Workers/MLShadowArbiterWorkerTest.cs` — shadow evaluation tests
- `Application/Services/BaggedLogisticTrainerTests.cs`, `ElmTrainerHelpersTests.cs` — ML trainer tests
- `Application/MLModels/QualityGateEvaluatorTest.cs` — quality gate pure-function tests
- `Application/Backtesting/BacktestEngineTest.cs` — backtest engine tests
- `Application/Optimization/OptimizationImprovementsTest.cs` — optimization config validation
- `Application/Orders/`, `Application/Strategies/`, `Application/RiskProfiles/`, `Application/CurrencyPairs/`, `Application/MarketData/` — CQRS handler tests

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
| `IRiskChecker` | `RiskChecker` | Scoped |
| `IBacktestEngine` | `BacktestEngine` | Singleton |
| `IAlertChannelSender` | `WebhookAlertSender`, `EmailAlertSender`, `TelegramAlertSender` | Scoped (all 3 registered) |
| `IAlertDispatcher` | `AlertDispatcher` | Singleton (holds dedup state; resolves scoped senders via `IServiceScopeFactory`) |
| `IMLModelTrainer` | `BaggedLogisticTrainer` (default, keyed `BaggedLogistic`) | Scoped |
| `IMLModelTrainer` (keyed) | `ElmModelTrainer`, `GbmModelTrainer`, `TcnModelTrainer`, `AdaBoostModelTrainer`, `RocketModelTrainer`, `TabNetModelTrainer`, `FtTransformerModelTrainer`, `SmoteModelTrainer`, `QuantileRfModelTrainer`, `SvgpModelTrainer`, `DannModelTrainer` | Scoped (keyed by `LearnerArchitecture` enum) |
| `IMLSignalScorer` | `MLSignalScorer` | Scoped |
| `ITrainerSelector` | `TrainerSelector` | Scoped |
| `IMultiTimeframeFilter` | `MultiTimeframeFilter` | Scoped |
| `INewsFilter` | `NewsFilter` | Scoped |
| `IPortfolioCorrelationChecker` | `PortfolioCorrelationChecker` | Scoped |
| `IPortfolioRiskCalculator` | `PortfolioRiskCalculator` | Scoped |
| `ISessionFilter` | `SessionFilter` | Singleton |
| `IMarketRegimeDetector` | `MarketRegimeDetector` | Scoped |
| `ISignalConflictResolver` | `SignalConflictResolver` | Singleton |
| `IDistributedLock` | (implementation varies) | Singleton |
| `IEconomicCalendarFeed` | `FairEconomyCalendarFeed` | Scoped |
| `IRateLimiter` | `TokenBucketRateLimiter` | Singleton |
| `IHawkesSignalFilter` | `HawkesSignalFilter` | Scoped |
| `IStressTestEngine` | `StressTestEngine` | Scoped |
| `ITransactionCostAnalyzer` | `TransactionCostAnalyzer` | Scoped |

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

The engine's built-in broker adapters (OANDA, IB, FXCM) are **disabled**. All market data and order execution flows through MQL5 Expert Advisor instances running on MetaTrader 5. The EA project lives at `/Users/olabodeolaleye/Developments/Software Projects/personal/Lascodia Trading Engine/lascodia-trading-engine-ea`.

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

## Autonomous Strategy Lifecycle

The engine implements a fully autonomous closed-loop strategy lifecycle:

```
StrategyGenerationWorker → BacktestWorker → WalkForwardWorker → StrategyHealthWorker
       ↑                                                              ↓
       └──── OptimizationWorker ←──── StrategyFeedbackWorker ←───────┘
```

### Strategy Generation (`StrategyGeneration/`)

`StrategyGenerationWorker` autonomously discovers new strategy candidates daily. Key components:
- `StrategyScreeningEngine` — 12-gate screening pipeline (IS/OOS backtests, degradation, R², walk-forward, Monte Carlo sign-flip, Monte Carlo shuffle, marginal Sharpe, Kelly sizing, equity curve, time concentration)
- `StrategyGenerationHelpers` — asset classification, ATR computation, spread filtering, regime-threshold scaling
- `IRegimeStrategyMapper` — maps market regimes to suitable strategy types
- `IStrategyParameterTemplateProvider` — provides parameter templates with dynamic refresh from promoted strategies
- `FeedbackDecayMonitor` — monitors and adjusts half-life for recency-weighted survival rates
- Anti-bloat: MaxCandidatesPerCycle, MaxActivePerSymbol, correlation group caps, regime budget diversity, weekly velocity cap
- Strategic reserve: counter-regime candidates for regime rotation readiness
- Portfolio drawdown filter: greedy removal of correlated candidates post-screening

### Optimization Pipeline (`Optimization/`)

`OptimizationWorker` refines strategy parameters using Bayesian optimization. Key components:
- `OptimizationSearchEngine` — TPE / GP-UCB / EHVI surrogate selection based on dimensionality
- `OptimizationGridBuilder` — parameter grid generation with adaptive bounds from historical approvals
- `OptimizationValidator` — purged K-fold, sensitivity, cost stress, walk-forward, temporal/portfolio correlation
- `OptimizationHealthScorer` — 5-factor health score (WR, PF, DD, Sharpe, trade count)
- `OptimizationApprovalPolicy` — composite + multi-objective + safety gate evaluation (14 total gates)
- `OptimizationRunStateMachine` — validated state transitions (Queued→Running→Completed→Approved/Rejected/Abandoned)
- `OptimizationRunClaimer` — atomic claiming via `FOR UPDATE SKIP LOCKED`
- `GradualRolloutManager` — 25%→50%→75%→100% traffic rollout with automatic rollback
- `HyperbandScheduler` — multi-bracket successive halving for efficient candidate screening
- Checkpointing, crash recovery, self-tuning retry, chronic failure escalation

### ML Training Pipeline

`MLTrainingWorker` trains and promotes ML models. Key components:
- **12 learner architectures:** BaggedLogistic, ELM, GBM, TCN, AdaBoost, ROCKET, TabNet, FT-Transformer, SMOTE, QuantileRF, SVGP, DANN
- **`TrainerSelector`** — UCB1 bandit auto-selection with regime affinity, cross-symbol cold start, graduated sample-count gates
- **`QualityGateEvaluator`** — 9-gate quality check (accuracy, EV, Brier, Sharpe, F1, walk-forward CV, ECE, BSS, OOB regression)
- **`MLShadowArbiterWorker`** — SPRT + z-test shadow evaluation before promotion (tournament groups)
- **82 ML monitoring workers** covering: drift detection (7), feature monitoring (10), calibration (9), accuracy tracking (8), prediction analysis (5), signal management (7), model lifecycle (9), advanced (21+)
- Self-tuning retry: analyzes failure patterns, blends hyperparams toward profitable models
- Profitability-based promotion gate: composite score must exceed champion
- Regime-specific sub-models: per-regime parameter conditioning

---

## Key Patterns & Rules

- **No manual gates**: The engine is fully autonomous. Never introduce manual approval workflows, four-eyes gates, human-in-the-loop confirmation steps, or any mechanism that blocks the autonomous pipeline waiting for human action. Human oversight is achieved through monitoring, alerts, risk limits, circuit breakers, and kill switches — not synchronous approval gates.
- **Soft delete**: All entities have `IsDeleted`; EF global query filters exclude deleted rows automatically.
- **Pagination**: List queries take `PagerRequest`, return `Pager<TDto>`.
- **Event bus**: After a successful command write, publish the relevant `IntegrationEvent` via `IEventBus`.
- **CQRS separation**: Commands use `IWriteApplicationDbContext`; queries use `IReadApplicationDbContext`. Never inject both into a single handler.
- **Worker pattern**: All background workers implement `BackgroundService` and are registered as hosted services in Application DI.
- **Strategy evaluators**: Implement `IStrategyEvaluator`; registered and resolved by strategy type enum.
- **ML workflow**: Train → Quality gates (9 checks) → Shadow evaluation (SPRT + tournament groups) → Signal-level A/B testing (SPRT on P&L) → Promotion decision → Activate (event triggers downstream workers). 82 monitoring workers continuously track drift, calibration, accuracy, and feature health. Online learning with per-update validation and adaptive LR decay.
- **ML architecture selection**: `TrainerSelector` uses UCB1 bandit algorithm with regime affinity to auto-select from 12 architectures. Sample-count gates prevent complex architectures on small datasets. All 12 trainers hardened with: 4-way data split (train/selection/calibration/test), 5-signal stationarity gate (ACF/PSI/CUSUM/ADF/KPSS with REJECT), GPU acceleration (CUDA + CPU fallback), class-imbalance rejection, adversarial validation, full snapshot sanitization, cross-fit adaptive heads, structured warm-start artifacts, train/inference parity audit, reliability diagram, Murphy decomposition, calibration residual stats.
- **ML inference**: 23-step scoring pipeline with 5-layer calibration (temperature → global Platt → conditional Platt → isotonic → age decay), circuit breakers (inference failure + prediction quality), latency-aware SLA tracking, MC-dropout uncertainty, conformal prediction sets, meta-label abstention, multi-timeframe blending.
- **Broker adapters**: Built-in adapters (`IBrokerOrderExecutor` + `IBrokerDataFeed`) are **disabled** when EA is active. The EA replaces them entirely. Set `WorkerGroups.BrokerAdapters = false`.
- **Autonomous strategy lifecycle**: StrategyGenerationWorker discovers → BacktestWorker validates → WalkForwardWorker confirms → StrategyHealthWorker monitors → StrategyFeedbackWorker detects degradation → OptimizationWorker refines → cycle repeats.
- **Defense-in-depth risk**: 5 independent layers (SignalValidator → RiskChecker → StrategyHealthWorker → DrawdownRecoveryWorker → ExecutionQualityCircuitBreaker) + EA-side safety (per-symbol + global circuit breakers). Tier 2 enforces drawdown recovery lot reduction for ALL order creation paths (not just autonomous). RiskCheckerPipeline has fail-closed circuit breaker (5 errors → block all orders). Portfolio risk: EVT tail modeling (GPD fit via MLE + Nelder-Mead), Monte Carlo VaR with Cholesky ridge regularization, correlated reverse stress testing (PCA shock direction). Gap risk model distinguishes weekends from holidays. VaR uses linear interpolation. Emergency flatten dedup persists across restarts.
- **Regime awareness**: Cross-cutting concern permeating all subsystems — strategy evaluation, generation, optimization, ML training, and signal filtering all adapt to detected market regime. Hybrid rule-based + Hidden Markov Model classification with k-means++ initialization. Proper Wilder's ADX smoothing, aligned Bollinger Band Width formula. Per-symbol/timeframe state isolation. Confidence floor (0.15) with Ranging fallback. Cross-timeframe coherence (H1/H4/D1) with HighVolatility categorized as directional. Transition guard dampens ML confidence post-regime-change. Hot-swap activates regime-specific superseded models.
- **Gradual rollout**: Optimized parameters deploy at configurable tiers (default 25%→50%→75%→100%) with statistical rollback detection (weighted linear regression on snapshots) and automatic reversion on degradation (see ADR-0007).
- **Hot-reloadable config**: 200+ parameters stored in `EngineConfig` table, loaded via batch queries, with run-scoped snapshots for reproducibility (see ADR-0012). All screening thresholds (OOS relaxation, Kelly factor, walk-forward min candles), health score bands, margin levels, and execution quality thresholds are configurable.
- **Backtesting correctness**: Gap slippage applies pessimistic fills on both SL and TP gap scenarios. RSI swing detection is backward-only (no look-ahead bias). NaN/Infinity candles filtered. Walk-forward requires minimum 3 windows with 10% terminal embargo and sample stddev (Bessel's correction).
- **Smart order routing**: Auto-selects Direct/TWAP/VWAP based on order size vs average daily volume. TWAP with ±10% jitter and broker lot-step rounding. VWAP with tick-activity-weighted profiles (forex OTC proxy). Pre-trade and post-trade transaction cost analysis.
- **Performance attribution**: Time-weighted returns (TWRR), rolling Sharpe/Sortino/Calmar (7/30-day), position-weighted benchmark, information ratio, ML alpha (ML-scored vs rule-based P&L), timing alpha (signal vs actual entry), per-strategy attribution via Order→Strategy FK.
- **Worker orchestration**: 147 workers in 7 groups (CoreTrading, MarketData, RiskMonitoring, MLTraining, MLMonitoring, Backtesting, Alerts). 4-tier startup ordering. Connection pool sized to 200 with retry-on-failure. State-aware idempotent event handlers. Worker crash alerts via AlertDispatcher. EA all-disconnect triggers DataUnavailable degradation mode.
- **EA integration**: JWT-authenticated TCP bridge with full signature verification. NaN/Infinity tick filtering. Idempotent tick batches via X-Request-Id. Symbol ownership enforced on registration with reassignment on deregister and re-claim on reconnect. Hard-reject unauthorized symbol snapshots. 24-hour command TTL on all delivery paths.
- **EA as broker adapter**: All market data comes from EA instances via REST API. The engine has zero direct broker connectivity when EA mode is enabled.
- **Trading account-centric**: The `Broker` entity has been removed. All broker info (name, server) lives on `TradingAccount`. Login is via AccountId + BrokerServer.
- **Trader authentication**: `POST /auth/register` (self-registration + auto-login) and `POST /auth/login` (EA: passwordless, Web: password required). JWT tokens are scoped to a single TradingAccount.
- **Password encryption**: Passwords are AES-256-GCM encrypted via `FieldEncryption` (stored in `TradingAccount.EncryptedPassword`). Default passwords are auto-generated on registration. Traders must set their own via `PUT /trading-account/{id}/password` for web dashboard access.
- **Two-tier risk checking**: Trade signals are validated in two stages:
  - **Tier 1 (`ISignalValidator`)**: Account-agnostic signal validation (expiry, lot size positivity, SL/TP direction, min SL distance, R:R ratio, mandatory SL, ML agreement). Runs in `SignalOrderBridgeWorker` — rejects or approves the signal.
  - **Tier 2 (`IRiskChecker`)**: Account-specific risk checks (margin, exposure, position limits, drawdown, lot constraints, spread). Runs when creating an order from an approved signal via `POST /order/from-signal` (`CreateOrderFromSignalCommand`).
  - Approved signals remain consumable until they expire. Tier 2 failure does NOT reject the signal — it records a `SignalAccountAttempt` and returns failure, so other accounts can still try.
- **Signal-to-order flow**: `StrategyWorker` generates signal → `SignalOrderBridgeWorker` runs Tier 1 → approves/rejects → EA/dashboard calls `POST /order/from-signal` with signalId + accountId → Tier 2 runs → order created if passed.

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
