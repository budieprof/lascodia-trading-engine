# Product Requirements Document
# Lascodia Automated Forex Trading Engine

**Version:** 1.0
**Date:** 2026-03-16
**Status:** Draft

---

## Table of Contents

1. [Overview](#1-overview)
2. [Goals & Non-Goals](#2-goals--non-goals)
3. [System Architecture](#3-system-architecture)
4. [Domain Model](#4-domain-model)
5. [Feature Modules](#5-feature-modules)
   - 5.1 [Market Data](#51-market-data)
   - 5.2 [Strategy Engine](#52-strategy-engine)
   - 5.3 [Signal Management](#53-signal-management)
   - 5.4 [Order Execution](#54-order-execution)
   - 5.5 [Position & Portfolio Management](#55-position--portfolio-management)
   - 5.6 [Risk Management](#56-risk-management)
   - 5.7 [Backtesting](#57-backtesting)
   - 5.8 [Notifications & Alerts](#58-notifications--alerts)
   - 5.9 [ML Signal Scoring](#59-ml-signal-scoring)
   - 5.10 [Strategy Feedback & Optimization](#510-strategy-feedback--optimization)
   - 5.11 [ML Model Evaluation & Continuous Improvement](#511-ml-model-evaluation--continuous-improvement)
   - 5.12 [Multi-Timeframe Confluence](#512-multi-timeframe-confluence)
   - 5.13 [News & Economic Calendar Filter](#513-news--economic-calendar-filter)
   - 5.14 [Portfolio Correlation Risk](#514-portfolio-correlation-risk)
   - 5.15 [Session-Aware Execution](#515-session-aware-execution)
   - 5.16 [Walk-Forward Optimization](#516-walk-forward-optimization)
   - 5.17 [Market Regime Detection](#517-market-regime-detection)
   - 5.18 [Execution Quality Analysis](#518-execution-quality-analysis)
   - 5.19 [Strategy Ensemble & Capital Allocation](#519-strategy-ensemble--capital-allocation)
   - 5.20 [Paper Trading / Simulation Mode](#520-paper-trading--simulation-mode)
   - 5.21 [Audit Trail & Decision Log](#521-audit-trail--decision-log)
   - 5.22 [Drawdown Recovery Mode](#522-drawdown-recovery-mode)
   - 5.23 [Sentiment Data Integration](#523-sentiment-data-integration)
   - 5.24 [System Health Monitoring](#524-system-health-monitoring)
   - 5.25 [Broker Failover & Redundancy](#525-broker-failover--redundancy)
   - 5.26 [Trailing Stop & Position Scaling](#526-trailing-stop--position-scaling)
   - 5.27 [Performance Attribution](#527-performance-attribution)
   - 5.28 [Configuration Hot Reload](#528-configuration-hot-reload)
   - 5.29 [Rate Limiting & API Quota Manager](#529-rate-limiting--api-quota-manager)
6. [API Design](#6-api-design)
7. [External Integrations](#7-external-integrations)
8. [Data Persistence](#8-data-persistence)
9. [Event Architecture](#9-event-architecture)
10. [Implementation Phases](#10-implementation-phases)
11. [Testing Strategy](#11-testing-strategy)
12. [Non-Functional Requirements](#12-non-functional-requirements)

---

## 1. Overview

Lascodia Trading Engine is an **automated forex trading system** built on .NET 10 using Clean Architecture and CQRS. It connects to broker APIs (e.g., MetaTrader 5, OANDA, Interactive Brokers) to receive live market data, evaluate configurable trading strategies, and autonomously execute forex orders with real-time risk controls.

All trading activity is auditable via integration events published to RabbitMQ.

### Core Value Proposition

- Fully automated trade lifecycle: signal → order → fill → position management
- Multi-strategy, multi-symbol support
- Real-time risk enforcement (per-trade, per-session, per-account)
- Strategy backtesting against historical price data before live deployment
- ML-powered post-processing layer that re-scores rule-based signals with predicted direction, magnitude, and confidence
- Continuous strategy feedback loop — live trade outcomes drive health scoring, auto-pause, and Bayesian parameter optimization
- ML model champion-challenger framework — challenger models run in shadow mode against live trades before promotion
- Multi-timeframe confluence filtering on all strategy evaluators
- News/economic calendar integration — execution paused around high-impact events
- Portfolio correlation risk — prevents concentrated exposure across correlated pairs
- Market regime detection — strategies only execute in their optimal regime
- Walk-forward optimization — rolling out-of-sample validation replaces naive backtesting
- Strategy ensemble with dynamic capital allocation weighted by rolling Sharpe ratio
- Paper trading mode — full live pipeline with simulated fills, zero real capital at risk
- Immutable audit trail — every engine decision recorded with full context and reason
- Graduated drawdown recovery mode — lot size reduction before hard pause
- Sentiment data integration — COT positioning and news NLP as macro filters and ML features
- Trailing stop and position scaling — moves stops as price moves in favour; pyramids into winners; scales out at intermediate targets
- Performance attribution — P&L broken down by session, regime, ML confidence tier, news proximity, and MTF confluence
- Configuration hot reload — key operational parameters updated via API without service restart
- Centralised rate limiter — token bucket per broker endpoint prevents API quota breaches
- Detailed system health API — per-subsystem status for operational monitoring
- Broker failover — automatic switch to secondary data/execution feed on primary failure
- Observable system — every state change emits an integration event

---

## 2. Goals & Non-Goals

### Goals

- [x] Ingest live forex price data (tick and OHLCV candles) from broker feeds
- [x] Evaluate pluggable trading strategies on incoming market data
- [x] Generate, queue, and execute trade signals as broker orders
- [x] Manage full order lifecycle (open → fill → partial fill → cancel → close)
- [x] Track open positions and P&L in real time
- [x] Enforce risk rules before and after every order (lot size, drawdown, exposure)
- [x] Provide REST APIs for configuration, monitoring, and manual overrides
- [x] Support backtesting strategies against historical candle data
- [x] Emit integration events for all state transitions (audit, downstream services)
- [x] ML post-processing layer: re-score rule-based signals with predicted direction, movement magnitude, and confidence using ML.NET
- [x] Scheduled and manual model retraining on historical candle + signal outcome data

### Non-Goals

- Crypto or equity instruments (forex pairs only in v1)
- UI / dashboard (API-first; frontend is a separate project)
- Copy trading or social features
- HFT (high-frequency trading) — target latency is seconds, not microseconds
- Regulatory compliance reporting (MiFID II, etc.) — out of scope for v1

---

## 3. System Architecture

### 3.1 Layer Overview

The codebase follows Clean Architecture. New modules are added as features inside the existing project structure, maintaining the same layer boundaries.

```
┌─────────────────────────────────────────────────────────────┐
│                        API Layer                            │
│  OrderController, StrategyController, PositionController    │
│  MarketDataController, BacktestController, AlertController  │
└──────────────────────────┬──────────────────────────────────┘
                           │ MediatR (ISender)
┌──────────────────────────▼──────────────────────────────────┐
│                    Application Layer                        │
│  Commands & Queries (CQRS), Validators, DTOs, AutoMapper    │
│  Strategy Evaluator Interface, Risk Checker Interface       │
└──────────┬─────────────────────────────────────┬────────────┘
           │ EF Core (Write/Read DbContext)       │ Interfaces
┌──────────▼────────────┐             ┌───────────▼────────────┐
│  Infrastructure Layer │             │  Background Services   │
│  SQL Server (CQRS)    │             │  MarketDataWorker       │
│  EF Configurations    │             │  StrategyWorker         │
│  Broker Adapters      │             │  OrderExecutionWorker   │
│  Event Bus (RabbitMQ) │             │  RiskMonitorWorker      │
└───────────────────────┘             └────────────────────────┘
```

### 3.2 Background Worker Architecture

Automated trading requires always-on background processing. Three long-running `IHostedService` workers run alongside the API:

| Worker | Responsibility |
|---|---|
| `MarketDataWorker` | Connects to broker WebSocket/API, ingests ticks and candles, persists and publishes `PriceUpdatedEvent` |
| `StrategyWorker` | Subscribes to `PriceUpdatedEvent`, evaluates active strategies, generates `TradeSignal` records |
| `OrderExecutionWorker` | Subscribes to `TradeSignalCreatedEvent`, validates risk rules, submits orders to broker, updates `Order` and `Position` |

Workers communicate exclusively via the RabbitMQ event bus, keeping them fully decoupled.

### 3.3 Dependency Flow (New Modules)

```
Domain:         MarketData, Strategy, TradeSignal, Position, RiskProfile, Backtest
Application:    Features/{Entity}/Commands | Queries | Dtos
Infrastructure: BrokerAdapters/, Persistence/Configurations/
API:            Controllers/v1/{Entity}Controller.cs
Background:     Workers/ (registered as IHostedService via AddSharedApplicationDependency)
```

---

## 4. Domain Model

All entities inherit `Entity<long>` from SharedDomain.

### 4.1 Entities

#### `Order` (existing — extend)

```csharp
public class Order : Entity<long>
{
    public long? TradeSignalId { get; set; }     // FK → TradeSignal (nullable for manual orders)
    public string Symbol { get; set; }            // e.g., "EURUSD", "GBPJPY"
    public string OrderType { get; set; }         // "Buy" | "Sell"
    public string ExecutionType { get; set; }     // "Market" | "Limit" | "Stop" | "StopLimit"
    public decimal Quantity { get; set; }         // Lot size
    public decimal Price { get; set; }            // Requested price (0 for Market orders)
    public decimal? StopLoss { get; set; }        // SL price level
    public decimal? TakeProfit { get; set; }      // TP price level
    public decimal? FilledPrice { get; set; }     // Actual fill price
    public decimal? FilledQuantity { get; set; }  // Actual fill quantity
    public string Status { get; set; }            // Pending | Submitted | PartialFill | Filled | Cancelled | Rejected | Expired
    public string? BrokerOrderId { get; set; }    // ID returned by broker
    public string? RejectionReason { get; set; }
    public string? Notes { get; set; }
    public bool IsPaper { get; set; }            // True = paper trading order, no real money

    // Trailing stop configuration
    public bool TrailingStopEnabled { get; set; }
    public string? TrailingStopType { get; set; }  // "FixedPips" | "ATR" | "Percentage"
    public decimal? TrailingStopValue { get; set; } // Pips / ATR multiplier / percentage
    public decimal? HighestFavourablePrice { get; set; } // Tracked by TrailingStopWorker

    public DateTime CreatedAt { get; set; }
    public DateTime? FilledAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `CurrencyPair`

```csharp
public class CurrencyPair : Entity<long>
{
    public string Symbol { get; set; }           // "EURUSD"
    public string BaseCurrency { get; set; }     // "EUR"
    public string QuoteCurrency { get; set; }    // "USD"
    public int DecimalPlaces { get; set; }       // 5 for most forex pairs
    public decimal ContractSize { get; set; }    // 100,000 for standard lot
    public decimal MinLotSize { get; set; }
    public decimal MaxLotSize { get; set; }
    public decimal LotStep { get; set; }         // 0.01 for micro-lot
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `Candle`

```csharp
public class Candle : Entity<long>
{
    public string Symbol { get; set; }
    public string Timeframe { get; set; }        // "M1" | "M5" | "M15" | "H1" | "H4" | "D1"
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public DateTime Timestamp { get; set; }      // UTC candle open time
    public bool IsClosed { get; set; }           // false = live/forming candle
}
```

#### `Strategy`

```csharp
public class Strategy : Entity<long>
{
    public string Name { get; set; }
    public string Description { get; set; }
    public string StrategyType { get; set; }     // "MovingAverageCrossover" | "RSIReversion" | "BreakoutScalper" | "Custom"
    public string Symbol { get; set; }           // Target symbol
    public string Timeframe { get; set; }        // Target timeframe
    public string ParametersJson { get; set; }   // Strategy-specific parameters as JSON
    public string Status { get; set; }           // "Active" | "Paused" | "Backtesting" | "Stopped"
    public long? RiskProfileId { get; set; }     // FK → RiskProfile
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

Strategy parameter examples per type:
- `MovingAverageCrossover`: `{ "FastPeriod": 9, "SlowPeriod": 21, "MaPeriod": 50 }`
- `RSIReversion`: `{ "Period": 14, "Oversold": 30, "Overbought": 70 }`
- `BreakoutScalper`: `{ "LookbackBars": 20, "BreakoutMultiplier": 1.5 }`

#### `TradeSignal`

```csharp
public class TradeSignal : Entity<long>
{
    public long StrategyId { get; set; }         // FK → Strategy
    public string Symbol { get; set; }
    public string Direction { get; set; }        // "Buy" | "Sell"
    public decimal EntryPrice { get; set; }      // Suggested entry
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public decimal SuggestedLotSize { get; set; }
    public decimal Confidence { get; set; }      // 0.0 – 1.0 (rule-based evaluator score)

    // ML scoring fields — populated by IMLSignalScorer after rule-based evaluation
    public string? MLPredictedDirection { get; set; }    // "Buy" | "Sell" | null if model not available
    public decimal? MLPredictedMagnitude { get; set; }   // Expected price movement in pips
    public decimal? MLConfidenceScore { get; set; }      // 0.0 – 1.0 model probability
    public long? MLModelId { get; set; }                 // FK → MLModel used for scoring

    public string Status { get; set; }           // "Pending" | "Approved" | "Executed" | "Rejected" | "Expired"
    public string? RejectionReason { get; set; }
    public long? OrderId { get; set; }           // FK → Order once executed
    public DateTime GeneratedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `Position`

```csharp
public class Position : Entity<long>
{
    public string Symbol { get; set; }
    public string Direction { get; set; }          // "Long" | "Short"
    public decimal OpenLots { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal? CurrentPrice { get; set; }     // Updated by MarketDataWorker
    public decimal UnrealizedPnL { get; set; }     // Calculated field
    public decimal RealizedPnL { get; set; }       // Accumulated from closed partial fills
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public string Status { get; set; }             // "Open" | "Closed" | "Closing"
    public bool IsPaper { get; set; }             // True = paper trading position
    public decimal? TrailingStopLevel { get; set; } // Current trailing stop price, updated by TrailingStopWorker
    public string? BrokerPositionId { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `RiskProfile`

```csharp
public class RiskProfile : Entity<long>
{
    public string Name { get; set; }
    public decimal MaxLotSizePerTrade { get; set; }
    public decimal MaxDailyDrawdownPct { get; set; }  // e.g., 2.0 = 2%
    public decimal MaxTotalDrawdownPct { get; set; }  // e.g., 10.0 = 10%
    public int MaxOpenPositions { get; set; }
    public int MaxDailyTrades { get; set; }
    public decimal MaxRiskPerTradePct { get; set; }   // % of account balance
    public decimal MaxSymbolExposurePct { get; set; } // % of account in one symbol
    public bool IsDefault { get; set; }

    // Drawdown recovery mode
    public decimal DrawdownRecoveryThresholdPct { get; set; }  // e.g., 1.5 = enter recovery at 1.5% drawdown
    public decimal RecoveryLotSizeMultiplier { get; set; }     // e.g., 0.5 = half lot size in recovery mode
    public decimal RecoveryExitThresholdPct { get; set; }      // Drawdown % at which normal sizing resumes

    public bool IsDeleted { get; set; }
}
```

#### `BacktestRun`

```csharp
public class BacktestRun : Entity<long>
{
    public long StrategyId { get; set; }
    public string Symbol { get; set; }
    public string Timeframe { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public decimal InitialBalance { get; set; }
    public string Status { get; set; }             // "Queued" | "Running" | "Completed" | "Failed"
    public string? ResultJson { get; set; }        // Serialized BacktestResult
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `Alert`

```csharp
public class Alert : Entity<long>
{
    public string AlertType { get; set; }          // "PriceLevel" | "DrawdownBreached" | "SignalGenerated" | "OrderFilled" | "PositionClosed"
    public string Symbol { get; set; }
    public string Channel { get; set; }            // "Email" | "Webhook" | "Telegram"
    public string Destination { get; set; }        // Email address, webhook URL, chat ID
    public string ConditionJson { get; set; }      // Alert trigger conditions as JSON
    public bool IsActive { get; set; }
    public DateTime? LastTriggeredAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `MLModel`

```csharp
public class MLModel : Entity<long>
{
    public string Symbol { get; set; }             // Symbol this model was trained on
    public string Timeframe { get; set; }          // Timeframe this model was trained on
    public string ModelVersion { get; set; }       // e.g., "1.0.0", "2.1.0"
    public string FilePath { get; set; }           // Absolute path to .mlnet model file on local filesystem
    public string Status { get; set; }             // "Training" | "Active" | "Superseded" | "Failed"
    public bool IsActive { get; set; }             // Only one model per (Symbol, Timeframe) is Active at a time
    public decimal? DirectionAccuracy { get; set; } // Validation set accuracy for direction classification
    public decimal? MagnitudeRMSE { get; set; }    // Validation set RMSE for magnitude regression
    public int TrainingSamples { get; set; }
    public DateTime TrainedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `MLTrainingRun`

```csharp
public class MLTrainingRun : Entity<long>
{
    public string Symbol { get; set; }
    public string Timeframe { get; set; }
    public string TriggerType { get; set; }        // "Scheduled" | "Manual"
    public string Status { get; set; }             // "Queued" | "Running" | "Completed" | "Failed"
    public DateTime FromDate { get; set; }         // Training data window start
    public DateTime ToDate { get; set; }           // Training data window end
    public int TotalSamples { get; set; }
    public decimal? DirectionAccuracy { get; set; }
    public decimal? MagnitudeRMSE { get; set; }
    public long? MLModelId { get; set; }           // FK → MLModel produced (null until completed)
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `StrategyPerformanceSnapshot`

```csharp
public class StrategyPerformanceSnapshot : Entity<long>
{
    public long StrategyId { get; set; }           // FK → Strategy
    public int WindowTrades { get; set; }          // Number of closed trades in evaluation window
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public decimal WinRate { get; set; }           // WinningTrades / WindowTrades
    public decimal ProfitFactor { get; set; }      // GrossProfit / GrossLoss
    public decimal SharpeRatio { get; set; }
    public decimal MaxDrawdownPct { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal HealthScore { get; set; }       // Weighted composite (0.0 – 1.0)
    public string HealthStatus { get; set; }       // "Healthy" | "Degrading" | "Critical"
    public DateTime EvaluatedAt { get; set; }      // Timestamp of this snapshot
    public bool IsDeleted { get; set; }
}
```

#### `OptimizationRun`

```csharp
public class OptimizationRun : Entity<long>
{
    public long StrategyId { get; set; }           // FK → Strategy being optimized
    public string TriggerType { get; set; }        // "Scheduled" | "Manual" | "AutoDegrading"
    public string Status { get; set; }             // "Queued" | "Running" | "Completed" | "Failed" | "Approved" | "Rejected"
    public int Iterations { get; set; }            // Bayesian optimization iterations run
    public string? BestParametersJson { get; set; } // Best parameter set found
    public decimal? BestHealthScore { get; set; }  // Score of best candidate on backtest
    public string? BaselineParametersJson { get; set; } // Parameters before optimization
    public decimal? BaselineHealthScore { get; set; }   // Score of current params for comparison
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `MLModelPredictionLog`

```csharp
public class MLModelPredictionLog : Entity<long>
{
    public long TradeSignalId { get; set; }         // FK → TradeSignal
    public long MLModelId { get; set; }             // FK → MLModel that made this prediction
    public string ModelRole { get; set; }           // "Champion" | "Challenger"
    public string Symbol { get; set; }
    public string Timeframe { get; set; }
    public string PredictedDirection { get; set; }  // "Buy" | "Sell"
    public decimal PredictedMagnitudePips { get; set; }
    public decimal ConfidenceScore { get; set; }    // 0.0 – 1.0 predicted probability
    public string? ActualDirection { get; set; }    // Populated when position closes
    public decimal? ActualMagnitudePips { get; set; }
    public bool? WasProfitable { get; set; }        // Populated when position closes
    public bool? DirectionCorrect { get; set; }     // PredictedDirection == ActualDirection
    public DateTime PredictedAt { get; set; }
    public DateTime? OutcomeRecordedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `MLShadowEvaluation`

```csharp
public class MLShadowEvaluation : Entity<long>
{
    public long ChallengerModelId { get; set; }     // FK → MLModel (challenger)
    public long ChampionModelId { get; set; }       // FK → MLModel (champion)
    public string Symbol { get; set; }
    public string Timeframe { get; set; }
    public string Status { get; set; }              // "Running" | "Completed" | "Promoted" | "Rejected"
    public int RequiredTrades { get; set; }         // N trades needed before evaluation
    public int CompletedTrades { get; set; }        // Trades with resolved outcomes so far

    // Champion metrics over the shadow window
    public decimal ChampionDirectionAccuracy { get; set; }
    public decimal ChampionMagnitudeCorrelation { get; set; }
    public decimal ChampionBrierScore { get; set; }

    // Challenger metrics over the shadow window
    public decimal ChallengerDirectionAccuracy { get; set; }
    public decimal ChallengerMagnitudeCorrelation { get; set; }
    public decimal ChallengerBrierScore { get; set; }

    public string? PromotionDecision { get; set; }  // "AutoPromoted" | "FlaggedForReview" | "Rejected"
    public string? DecisionReason { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `EconomicEvent`

```csharp
public class EconomicEvent : Entity<long>
{
    public string Title { get; set; }              // e.g., "US Non-Farm Payrolls"
    public string Currency { get; set; }           // Affected currency, e.g., "USD"
    public string Impact { get; set; }             // "Low" | "Medium" | "High"
    public DateTime ScheduledAt { get; set; }      // UTC event time
    public string? Forecast { get; set; }
    public string? Previous { get; set; }
    public string? Actual { get; set; }            // Populated after event releases
    public string Source { get; set; }             // "ForexFactory" | "Investing" | "Manual"
    public bool IsDeleted { get; set; }
}
```

#### `MarketRegimeSnapshot`

```csharp
public class MarketRegimeSnapshot : Entity<long>
{
    public string Symbol { get; set; }
    public string Timeframe { get; set; }
    public string Regime { get; set; }             // "Trending" | "Ranging" | "HighVolatility" | "LowVolatility"
    public decimal Confidence { get; set; }        // 0.0 – 1.0
    public decimal ADX { get; set; }               // Trend strength indicator
    public decimal ATR { get; set; }               // Volatility indicator
    public decimal BollingerBandWidth { get; set; } // Range proxy
    public DateTime DetectedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `ExecutionQualityLog`

```csharp
public class ExecutionQualityLog : Entity<long>
{
    public long OrderId { get; set; }              // FK → Order
    public long? StrategyId { get; set; }          // FK → Strategy (null for manual orders)
    public string Symbol { get; set; }
    public string Session { get; set; }            // "London" | "NewYork" | "Asian" | "LondonNYOverlap"
    public decimal RequestedPrice { get; set; }
    public decimal FilledPrice { get; set; }
    public decimal SlippagePips { get; set; }      // abs(FilledPrice - RequestedPrice) / PipSize
    public long SubmitToFillMs { get; set; }       // Latency in milliseconds
    public bool WasPartialFill { get; set; }
    public decimal FillRate { get; set; }          // FilledQuantity / RequestedQuantity
    public DateTime RecordedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `StrategyAllocation`

```csharp
public class StrategyAllocation : Entity<long>
{
    public long StrategyId { get; set; }           // FK → Strategy
    public decimal Weight { get; set; }            // 0.0 – 1.0, normalised across all active strategies
    public decimal RollingSharpRatio { get; set; } // Used to compute weight
    public DateTime LastRebalancedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `WalkForwardRun`

```csharp
public class WalkForwardRun : Entity<long>
{
    public long StrategyId { get; set; }
    public string Symbol { get; set; }
    public string Timeframe { get; set; }
    public DateTime FromDate { get; set; }         // Full data window start
    public DateTime ToDate { get; set; }           // Full data window end
    public int InSampleDays { get; set; }          // Optimization window size
    public int OutOfSampleDays { get; set; }       // Validation window size
    public string Status { get; set; }             // "Queued" | "Running" | "Completed" | "Failed"
    public decimal InitialBalance { get; set; }
    public decimal? AverageOutOfSampleScore { get; set; }  // Mean HealthScore across all OOS windows
    public decimal? ScoreConsistency { get; set; }          // Std dev of OOS scores (lower = more consistent)
    public string? WindowResultsJson { get; set; }          // Per-window results array
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `DecisionLog`

```csharp
public class DecisionLog : Entity<long>
{
    public string EntityType { get; set; }         // "TradeSignal" | "Order" | "Strategy" | "MLModel"
    public long EntityId { get; set; }             // FK to relevant entity
    public string DecisionType { get; set; }       // "SignalApproved" | "SignalRejected" | "OrderSubmitted"
                                                   // "OrderBlocked" | "StrategyPaused" | "ModelPromoted" | etc.
    public string Outcome { get; set; }            // "Approved" | "Rejected" | "Blocked" | "Paused"
    public string Reason { get; set; }             // Human-readable reason
    public string? ContextJson { get; set; }       // Full context snapshot (risk check results, regime,
                                                   // news window, MTF confluence, ML scores, etc.)
    public string Source { get; set; }             // "OrderExecutionWorker" | "RiskChecker" | "NewsFilter" | etc.
    public DateTime CreatedAt { get; set; }
    // DecisionLog is immutable — no IsDeleted, no update commands
}
```

#### `SentimentSnapshot`

```csharp
public class SentimentSnapshot : Entity<long>
{
    public string Currency { get; set; }           // e.g., "USD", "EUR"
    public string Source { get; set; }             // "COT" | "NewsSentiment"
    public decimal SentimentScore { get; set; }    // -1.0 (very bearish) to +1.0 (very bullish)
    public decimal Confidence { get; set; }        // 0.0 – 1.0
    public string? RawDataJson { get; set; }       // Source-specific raw values
    public DateTime CapturedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `COTReport`

```csharp
public class COTReport : Entity<long>
{
    public string Currency { get; set; }
    public DateTime ReportDate { get; set; }       // CFTC report release date (weekly, Fridays)
    public long CommercialLong { get; set; }
    public long CommercialShort { get; set; }
    public long NonCommercialLong { get; set; }    // "Smart money" / speculators
    public long NonCommercialShort { get; set; }
    public long RetailLong { get; set; }
    public long RetailShort { get; set; }
    public decimal NetNonCommercialPositioning { get; set; } // NonCommercialLong - NonCommercialShort
    public decimal NetPositioningChangeWeekly { get; set; }  // Change vs prior week
    public bool IsDeleted { get; set; }
}
```

#### `PositionScaleOrder`

```csharp
public class PositionScaleOrder : Entity<long>
{
    public long PositionId { get; set; }           // FK → parent Position
    public long OrderId { get; set; }              // FK → Order created for this scale
    public string ScaleType { get; set; }          // "ScaleIn" | "ScaleOut"
    public int ScaleStep { get; set; }             // 1st, 2nd, 3rd scale level
    public decimal TriggerPips { get; set; }       // Pips in favour before triggering
    public decimal LotSize { get; set; }           // Lots to add/remove at this level
    public decimal? TakeProfitPrice { get; set; }  // For ScaleOut: partial TP price
    public string Status { get; set; }             // "Pending" | "Triggered" | "Filled" | "Cancelled"
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

#### `EngineConfig`

```csharp
public class EngineConfig : Entity<long>
{
    public string Key { get; set; }                // Unique config key, e.g. "RiskMonitor:IntervalSeconds"
    public string Value { get; set; }              // Serialised value
    public string? Description { get; set; }
    public string DataType { get; set; }           // "String" | "Int" | "Decimal" | "Bool" | "Json"
    public bool IsHotReloadable { get; set; }      // False = requires restart, safety guard
    public DateTime LastUpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
```

### 4.2 Status Flows

```
// Order status flow
Pending → Submitted → Filled
                    ↘ PartialFill → Filled / Cancelled
         ↘ Rejected
         ↘ Cancelled
         ↘ Expired

// Signal status flow
Pending → Approved → Executed
        ↘ Rejected
        ↘ Expired (no order created before ExpiresAt)

// Strategy status
Active → Paused → Active
       ↘ Stopped
```

---

## 5. Feature Modules

### 5.1 Market Data

**Purpose:** Ingest, persist, and broadcast live price data (ticks and OHLCV candles) from broker data feeds.

#### Commands

| Command | Description |
|---|---|
| `CreateCurrencyPairCommand` | Register a new forex symbol |
| `UpdateCurrencyPairCommand` | Update symbol metadata (lot limits, decimal places) |
| `DeleteCurrencyPairCommand` | Soft-delete a symbol |
| `IngestCandleCommand` | Persist a candle (called by `MarketDataWorker`) |
| `UpdateLiveCandleCommand` | Update the in-progress (unclosed) candle |

#### Queries

| Query | Description |
|---|---|
| `GetCurrencyPairQuery` | Fetch a single symbol configuration |
| `GetPagedCurrencyPairsQuery` | List symbols with pagination |
| `GetCandlesQuery` | Fetch OHLCV candles for a symbol/timeframe with date range |
| `GetLatestCandleQuery` | Fetch most recent closed candle |
| `GetLivePriceQuery` | Fetch current bid/ask (cached in memory) |

#### Background Worker: `MarketDataWorker`

```
1. On startup: connect to broker WebSocket (configurable per broker adapter)
2. Subscribe to tick stream for all active CurrencyPairs
3. On each tick:
   a. Update in-memory price cache (ILivePriceCache)
   b. Aggregate into OHLCV candle for each active timeframe
   c. On candle close: persist via IngestCandleCommand, publish PriceUpdatedIntegrationEvent
4. On reconnect: replay missing candles from broker REST API
```

#### Interfaces

```csharp
public interface IBrokerDataFeed
{
    Task ConnectAsync(CancellationToken ct);
    IAsyncEnumerable<Tick> SubscribeToTicks(IEnumerable<string> symbols, CancellationToken ct);
    Task<IEnumerable<Candle>> GetHistoricalCandlesAsync(string symbol, string timeframe, DateTime from, DateTime to);
}

public interface ILivePriceCache
{
    void Update(string symbol, decimal bid, decimal ask, DateTime timestamp);
    (decimal Bid, decimal Ask, DateTime Timestamp)? Get(string symbol);
}
```

---

### 5.2 Strategy Engine

**Purpose:** Define, configure, and evaluate trading strategies against market data to produce signals.

#### Commands

| Command | Description |
|---|---|
| `CreateStrategyCommand` | Create a new strategy |
| `DeleteStrategyCommand` | Soft-delete |
| `ActivateStrategyCommand` | Set status to "Active" |
| `PauseStrategyCommand` | Set status to "Paused" |
| `AssignRiskProfileCommand` | Link a strategy to a RiskProfile |

#### Queries

| Query | Description |
|---|---|
| `GetStrategyQuery` | Fetch a single strategy with full config |
| `GetPagedStrategiesQuery` | List strategies with filters (status, symbol) |

#### Strategy Evaluator Interface

Each strategy type is implemented as a class in the Application layer:

```csharp
public interface IStrategyEvaluator
{
    string StrategyType { get; }

    // Returns a signal if conditions are met, or null if no trade setup exists
    Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken ct);
}
```

Built-in evaluators (v1):

| Evaluator | Logic |
|---|---|
| `MovingAverageCrossoverEvaluator` | Fast MA crosses above/below slow MA; optional trend filter using long MA |
| `RSIReversionEvaluator` | RSI exits oversold (<30) → Buy; exits overbought (>70) → Sell |
| `BreakoutScalperEvaluator` | Price breaks above/below N-bar high/low with ATR multiplier confirmation |

Evaluators are registered with DI and resolved by `StrategyType` key at runtime.

#### Background Worker: `StrategyWorker`

```
1. Subscribe to PriceUpdatedIntegrationEvent from event bus
2. On event received:
   a. Load all Active strategies for the event's Symbol + Timeframe
   b. For each strategy:
      i.   Load last N candles from read DB (N = max lookback across all indicators)
      ii.  Resolve the matching IStrategyEvaluator
      iii. Call EvaluateAsync()
      iv.  If signal returned:
             - Call IMLSignalScorer.ScoreAsync(signal, candles)
             - Attach MLPredictedDirection, MLPredictedMagnitude, MLConfidenceScore, MLModelId to signal
               (if no active model exists for the symbol/timeframe, ML fields remain null — signal proceeds)
             - Persist via CreateTradeSignalCommand
             - Publish TradeSignalCreatedIntegrationEvent
3. Expired signals (past ExpiresAt, still Pending): update status to Expired via background sweep
```

---

### 5.3 Signal Management

**Purpose:** Track, review, and manage generated trade signals. Supports both auto-execution and manual approval modes.

#### Commands

| Command | Description |
|---|---|
| `CreateTradeSignalCommand` | Persist a signal (called by StrategyWorker) |
| `ApproveTradeSignalCommand` | Manually approve a Pending signal for execution |
| `RejectTradeSignalCommand` | Manually reject a signal (records reason) |
| `ExpireTradeSignalCommand` | Mark signal as Expired (called by sweep job) |

#### Queries

| Query | Description |
|---|---|
| `GetTradeSignalQuery` | Fetch a single signal |
| `GetPagedTradeSignalsQuery` | List signals with filters (status, symbol, strategy, date range) |

#### Execution Mode

Per-strategy configuration (stored in `ParametersJson`):
- `"ExecutionMode": "Auto"` — `OrderExecutionWorker` auto-approves and executes signals immediately
- `"ExecutionMode": "Manual"` — signal stays Pending until a human calls `ApproveTradeSignalCommand` via the API

---

### 5.4 Order Execution

**Purpose:** Submit approved signals as orders to the broker, track order lifecycle, and handle fills.

#### Commands

| Command | Description |
|---|---|
| `SubmitOrderCommand` | Send a Pending order to the broker API |
| `UpdateOrderStatusCommand` | Update order status from broker callback/polling |
| `CancelOrderCommand` | Cancel a Submitted/PartialFill order |
| `ClosePositionOrderCommand` | Create a counter-order to close an open position |
| `ModifyOrderCommand` | Modify SL/TP of an existing order at broker |

#### Queries

| Query | Description |
|---|---|
| `GetOrderQuery` | Fetch single order (existing) |
| `GetPagedOrdersQuery` | List orders (existing — extend filters for status, signal, fill dates) |

#### Broker Execution Interface

```csharp
public interface IBrokerOrderExecutor
{
    Task<BrokerOrderResult> SubmitOrderAsync(Order order, CancellationToken ct);
    Task<BrokerOrderResult> CancelOrderAsync(string brokerOrderId, CancellationToken ct);
    Task<BrokerOrderResult> ModifyOrderAsync(string brokerOrderId, decimal? stopLoss, decimal? takeProfit, CancellationToken ct);
    Task<BrokerOrderStatus> GetOrderStatusAsync(string brokerOrderId, CancellationToken ct);
}

public record BrokerOrderResult(
    bool Success,
    string? BrokerOrderId,
    decimal? FilledPrice,
    decimal? FilledQuantity,
    string? ErrorMessage);
```

#### Background Worker: `OrderExecutionWorker`

```
1. Subscribe to TradeSignalCreatedIntegrationEvent
2. On event:
   a. Load signal from DB
   b. If strategy ExecutionMode = "Manual" → skip (wait for ApproveTradeSignalCommand)
   c. Run risk checks (see Risk Management section)
   d. If risk checks pass:
      i.  Set signal status = "Approved"
      ii. Create Order record (status = "Pending")
      iii.Call IBrokerOrderExecutor.SubmitOrderAsync()
      iv. Update Order with BrokerOrderId, status = "Submitted"
      v.  Publish OrderSubmittedIntegrationEvent
   e. If risk checks fail:
      i.  Set signal status = "Rejected" with reason
      ii. Publish SignalRejectedIntegrationEvent
3. Order status reconciliation (every 5 seconds):
   a. Poll broker for status of all "Submitted" / "PartialFill" orders
   b. On fill: update Order, update/create Position, publish OrderFilledIntegrationEvent
   c. On rejection: update Order status = "Rejected", publish OrderRejectedIntegrationEvent
```

---

### 5.5 Position & Portfolio Management

**Purpose:** Maintain real-time position state, compute unrealized P&L, and track account equity.

#### Commands

| Command | Description |
|---|---|
| `OpenPositionCommand` | Create a Position when an order is filled (called internally by OrderExecutionWorker) |
| `UpdatePositionCommand` | Update CurrentPrice, UnrealizedPnL (called by MarketDataWorker on tick) |
| `ClosePositionCommand` | Record position closure after counter-order fills |
| `PartialClosePositionCommand` | Reduce open lots after partial close fill |

#### Queries

| Query | Description |
|---|---|
| `GetPositionQuery` | Fetch a single position |
| `GetPagedPositionsQuery` | List positions with filters (status, symbol) |
| `GetPortfolioSummaryQuery` | Aggregate: total open positions, total unrealized P&L, open lots by symbol |
| `GetDailyPnLQuery` | Sum of RealizedPnL for closed positions within a date range |

#### P&L Calculation

```
UnrealizedPnL (Long)  = (CurrentBid - AverageEntryPrice) × OpenLots × ContractSize
UnrealizedPnL (Short) = (AverageEntryPrice - CurrentAsk) × OpenLots × ContractSize
```

Calculation is performed in the `UpdatePositionCommand` handler whenever a new tick arrives for the position's symbol.

---

### 5.6 Risk Management

**Purpose:** Enforce risk rules before every order submission and continuously monitor open positions.

#### Commands

| Command | Description |
|---|---|
| `CreateRiskProfileCommand` | Create a risk profile |
| `UpdateRiskProfileCommand` | Update risk limits |
| `DeleteRiskProfileCommand` | Soft-delete |

#### Queries

| Query | Description |
|---|---|
| `GetRiskProfileQuery` | Fetch a single profile |
| `GetPagedRiskProfilesQuery` | List all risk profiles |
| `GetRiskSummaryQuery` | Current exposure: daily P&L, drawdown %, open positions count |

#### Risk Checker Interface

```csharp
public interface IRiskChecker
{
    Task<RiskCheckResult> CheckPreTradeAsync(
        TradeSignal signal,
        RiskProfile profile,
        AccountState account,
        CancellationToken ct);
}

public record RiskCheckResult(bool Passed, IReadOnlyList<string> Violations);

public record AccountState(
    decimal Balance,
    decimal Equity,
    int OpenPositionCount,
    int DailyTradeCount,
    decimal DailyRealizedPnL,
    decimal PeakEquity);
```

#### Pre-Trade Risk Checks (evaluated in order)

| Rule | Condition to Block |
|---|---|
| Max lot size | `signal.SuggestedLotSize > profile.MaxLotSizePerTrade` |
| Max open positions | `account.OpenPositionCount >= profile.MaxOpenPositions` |
| Max daily trades | `account.DailyTradeCount >= profile.MaxDailyTrades` |
| Daily drawdown | `account.DailyRealizedPnL / account.Balance < -profile.MaxDailyDrawdownPct / 100` |
| Total drawdown | `(account.PeakEquity - account.Equity) / account.PeakEquity > profile.MaxTotalDrawdownPct / 100` |
| Risk per trade | `signal.StopLoss != null AND pip_risk × lot_size > profile.MaxRiskPerTradePct / 100 × account.Balance` |
| Symbol exposure | `open_lots_for_symbol + signal.SuggestedLotSize > profile.MaxSymbolExposurePct / 100 × account.Balance / price` |

#### Background Worker: `RiskMonitorWorker`

```
1. Every minute: evaluate drawdown against all open positions
2. If total drawdown exceeds MaxTotalDrawdownPct:
   a. Pause all Active strategies (PauseStrategyCommand)
   b. Optionally close all open positions (ClosePositionOrderCommand per position)
   c. Publish DrawdownBreachedIntegrationEvent → triggers Alert
3. If daily drawdown limit is hit:
   a. Pause strategies for remainder of trading day
   b. Publish DailyDrawdownBreachedIntegrationEvent
```

---

### 5.7 Backtesting

**Purpose:** Simulate a strategy against historical candle data to evaluate performance before live deployment.

#### Commands

| Command | Description |
|---|---|
| `QueueBacktestCommand` | Create a `BacktestRun` record with status "Queued" |
| `CancelBacktestCommand` | Cancel a Queued/Running backtest |

#### Queries

| Query | Description |
|---|---|
| `GetBacktestRunQuery` | Fetch a single run with status and results |
| `GetPagedBacktestRunsQuery` | List runs with filters (strategy, symbol, status) |

#### Backtest Engine

The backtest engine lives in the Application layer as `IBacktestEngine`:

```csharp
public interface IBacktestEngine
{
    Task<BacktestResult> RunAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        decimal initialBalance,
        RiskProfile riskProfile,
        CancellationToken ct);
}

public record BacktestResult(
    decimal FinalBalance,
    decimal TotalPnL,
    decimal MaxDrawdownPct,
    int TotalTrades,
    int WinningTrades,
    int LosingTrades,
    decimal WinRate,
    decimal ProfitFactor,        // GrossProfit / GrossLoss
    decimal SharpeRatio,
    List<BacktestTrade> Trades);
```

#### Backtest Worker

```
1. BacktestRun created with status "Queued" → publishes BacktestQueuedIntegrationEvent
2. Worker picks up event:
   a. Load historical candles from DB (or broker historical API if not cached)
   b. Set BacktestRun status = "Running"
   c. Call IBacktestEngine.RunAsync()
   d. Serialize result to BacktestRun.ResultJson
   e. Set status = "Completed" (or "Failed" on exception)
   f. Publish BacktestCompletedIntegrationEvent
3. Engine simulates bar-by-bar:
   - For each candle: call IStrategyEvaluator.EvaluateAsync() with candles up to current bar
   - Apply simulated risk checks
   - Simulate order fills at next candle open (slippage configurable in ParametersJson)
   - Track simulated positions and equity curve
```

---

### 5.8 Notifications & Alerts

**Purpose:** Notify traders via external channels when key events occur.

#### Commands

| Command | Description |
|---|---|
| `CreateAlertCommand` | Create a new alert rule |
| `UpdateAlertCommand` | Modify alert conditions or channel |
| `DeleteAlertCommand` | Soft-delete |

#### Queries

| Query | Description |
|---|---|
| `GetAlertQuery` | Fetch a single alert |
| `GetPagedAlertsQuery` | List alerts with filters |

#### Alert Dispatcher Interface

```csharp
public interface IAlertDispatcher
{
    Task SendAsync(Alert alert, string message, CancellationToken ct);
}
```

Channel implementations (Infrastructure):
- `EmailAlertDispatcher` — sends via SMTP
- `WebhookAlertDispatcher` — HTTP POST to configured URL
- `TelegramAlertDispatcher` — sends via Telegram Bot API

#### Event-Driven Alert Triggers

Alert handler subscribes to integration events and checks active Alert records:

| Integration Event | Potential Alert Type |
|---|---|
| `OrderFilledIntegrationEvent` | `OrderFilled` |
| `PositionClosedIntegrationEvent` | `PositionClosed` |
| `TradeSignalCreatedIntegrationEvent` | `SignalGenerated` |
| `DrawdownBreachedIntegrationEvent` | `DrawdownBreached` |
| `PriceUpdatedIntegrationEvent` | `PriceLevel` (if bid/ask crosses target) |

---

### 5.9 ML Signal Scoring

**Purpose:** After a rule-based evaluator generates a `TradeSignal`, an ML model re-scores it with three additional predictions: trade direction, expected price movement magnitude (pips), and a confidence probability. This gives the `OrderExecutionWorker` richer signal quality data to act on — for example, only auto-executing signals where `MLConfidenceScore >= 0.7` and `MLPredictedDirection` agrees with the rule-based direction.

#### How It Fits in the Pipeline

```
Rule-based evaluator → TradeSignal (Confidence, Direction, SL, TP)
                                    ↓
                          IMLSignalScorer.ScoreAsync()
                                    ↓
                     Enriched TradeSignal (+MLPredictedDirection,
                                           +MLPredictedMagnitude,
                                           +MLConfidenceScore,
                                           +MLModelId)
                                    ↓
                        Persist + publish to OrderExecutionWorker
```

The ML layer is **additive** — it never blocks a signal. If no active model exists for a symbol/timeframe, the signal proceeds with null ML fields and the rule-based `Confidence` is used as fallback.

#### ML Scorer Interface

```csharp
public interface IMLSignalScorer
{
    // Returns null fields if no active model exists for the symbol/timeframe
    Task<MLScoreResult> ScoreAsync(
        TradeSignal signal,
        IReadOnlyList<Candle> candles,
        CancellationToken ct);
}

public record MLScoreResult(
    string? PredictedDirection,   // "Buy" | "Sell"
    decimal? PredictedMagnitude,  // Expected pip movement
    decimal? ConfidenceScore,     // 0.0 – 1.0 model probability
    long? MLModelId);
```

#### Feature Engineering

Features are extracted from the last N candles before inference. The feature vector includes:

| Feature | Description |
|---|---|
| `Close_t`, `Close_t-1` ... `Close_t-N` | Recent close prices (normalised) |
| `High - Low` | Candle range (volatility proxy) |
| `SMA_9`, `SMA_21`, `SMA_50` | Simple moving averages |
| `EMA_9`, `EMA_21` | Exponential moving averages |
| `RSI_14` | Relative Strength Index |
| `ATR_14` | Average True Range |
| `MACD`, `MACD_Signal` | MACD line and signal line |
| `BollingerUpper`, `BollingerLower` | Bollinger Band levels |
| `Volume` | Candle volume (if available from broker) |
| `HourOfDay`, `DayOfWeek` | Session timing features |

#### ML.NET Models

Two models are trained per (Symbol, Timeframe) pair:

| Model | Task | Algorithm | Output |
|---|---|---|---|
| Direction classifier | Binary classification | FastTree / LightGBM | `Buy` or `Sell` probability |
| Magnitude regressor | Regression | FastForest regression | Expected pip movement (float) |

The direction classifier's positive class probability becomes `MLConfidenceScore`. Both models are bundled into a single `.mlnet` zip file saved at `{ModelStoragePath}/{Symbol}/{Timeframe}/model_v{version}.mlnet`.

#### Commands

| Command | Description |
|---|---|
| `TriggerMLTrainingCommand` | Queue a manual retraining run for a symbol/timeframe |
| `ActivateMLModelCommand` | Promote a trained model to Active (swaps out the previous one) |
| `DeactivateMLModelCommand` | Deactivate a model without replacing it (fall back to rule-based only) |

#### Queries

| Query | Description |
|---|---|
| `GetMLModelQuery` | Fetch a single model record with metrics |
| `GetPagedMLModelsQuery` | List models with filters (symbol, timeframe, status) |
| `GetMLTrainingRunQuery` | Fetch a training run with status and outcome metrics |
| `GetPagedMLTrainingRunsQuery` | List training runs |

#### Background Worker: `MLRetrainingWorker`

```
Scheduled trigger (configurable cron, e.g., every Sunday 00:00 UTC):
  For each (Symbol, Timeframe) that has an active strategy:
    1. Create MLTrainingRun (TriggerType = "Scheduled", status = "Queued")
    2. Publish MLTrainingQueuedIntegrationEvent

Manual trigger (TriggerMLTrainingCommand via API):
  1. Create MLTrainingRun (TriggerType = "Manual", status = "Queued")
  2. Publish MLTrainingQueuedIntegrationEvent

Worker picks up event:
  1. Set MLTrainingRun status = "Running"
  2. Load historical candles for the training window from read DB
  3. Build labeled dataset:
       - Features: extracted indicator vector per candle (see Feature Engineering)
       - Direction label: actual price direction N bars after signal candle ("Buy" if close_t+N > close_t)
       - Magnitude label: abs(close_t+N - close_t) in pips
  4. Train direction classifier (ML.NET FastTree / LightGBM binary classification)
  5. Train magnitude regressor (ML.NET FastForest regression)
  6. Evaluate on held-out validation set (20% split); record DirectionAccuracy and MagnitudeRMSE
  7. Save bundled .mlnet file to local filesystem
  8. Create MLModel record (status = "Training" → "Active" if metrics pass threshold)
  9. If metrics pass (DirectionAccuracy >= 55%, MagnitudeRMSE within tolerance):
       - Auto-promote: set new model IsActive = true, previous model IsActive = false (status = "Superseded")
       - Hot-reload IMLSignalScorer with new model (no restart required)
  10. Set MLTrainingRun status = "Completed"
  11. Publish MLTrainingCompletedIntegrationEvent
```

#### Model Hot-Reload

`IMLSignalScorer` is implemented as a singleton that holds the loaded `PredictionEngine<>` in memory. When a new model is activated, it reloads from the new file path without restarting the host:

```csharp
public interface IMLModelLoader
{
    void Reload(string symbol, string timeframe, string filePath);
    bool HasModel(string symbol, string timeframe);
}
```

#### Usage by OrderExecutionWorker

`OrderExecutionWorker` can optionally gate auto-execution using ML scores. This is configured per-strategy in `ParametersJson`:

```json
{
  "ExecutionMode": "Auto",
  "MLGatingEnabled": true,
  "MLMinConfidence": 0.65,
  "MLDirectionMustMatch": true
}
```

If `MLGatingEnabled = true`:
- Signal is rejected (not submitted) if `MLConfidenceScore < MLMinConfidence`
- Signal is rejected if `MLDirectionMustMatch = true` and `MLPredictedDirection != signal.Direction`

If `MLGatingEnabled = false` or ML fields are null, the rule-based signal is executed as normal.

---

### 5.10 Strategy Feedback & Optimization

**Purpose:** Close the loop between live trade outcomes and strategy quality. After every trade closes, the strategy's health is re-scored. Underperforming strategies are automatically flagged or paused. Weekly, a Bayesian optimizer proposes improved parameter sets by running candidates through the backtest engine. Live trade outcome labels are also fed back into the ML retraining pipeline as high-quality labelled data.

#### How It Fits in the Pipeline

```
PositionClosedIntegrationEvent
        ↓
StrategyEvaluationWorker
        ↓
  Recompute HealthScore for owning strategy (rolling N-trade window)
        ↓
  ┌─────────────────────────────────────────────────────┐
  │ HealthStatus = "Healthy"   → persist snapshot only  │
  │ HealthStatus = "Degrading" → persist + emit alert   │
  │ HealthStatus = "Critical"  → auto-pause strategy    │
  │                              + queue OptimizationRun│
  │                              + emit alert           │
  └─────────────────────────────────────────────────────┘
        ↓ (also)
  Append OutcomeLabelledCandle to ML training dataset
  (feeds into next MLRetrainingWorker run)
```

Weekly batch (cron) also queues `OptimizationRun` for all Active strategies regardless of health, giving healthy strategies a chance to improve too.

#### Health Score Formula

```
HealthScore = (WinRate       × 0.25)
            + (ProfitFactor  × 0.30)   // capped at 3.0 before weighting
            + (SharpeRatio   × 0.25)   // capped at 3.0 before weighting
            - (MaxDrawdownPct × 0.20)  // penalty: higher drawdown → lower score

HealthStatus thresholds (configurable in appsettings):
  >= 0.60  → "Healthy"
  0.40–0.59 → "Degrading"   (flag + alert)
  < 0.40   → "Critical"     (auto-pause + queue optimization)
```

Metric weights and thresholds are configurable per strategy via `ParametersJson`.

#### Bayesian Optimization

The optimizer treats the backtest engine as a **black-box objective function** — it proposes a parameter set, runs a backtest, receives the `HealthScore`, and uses the result to guide the next proposal.

```csharp
public interface IStrategyOptimizer
{
    // Runs up to maxIterations Bayesian optimization steps.
    // Returns the best parameter set found and its backtest HealthScore.
    Task<OptimizationResult> OptimizeAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        RiskProfile riskProfile,
        int maxIterations,
        CancellationToken ct);
}

public record OptimizationResult(
    string BestParametersJson,
    decimal BestHealthScore,
    decimal BaselineHealthScore,
    int IterationsRun,
    List<OptimizationIteration> Iterations);

public record OptimizationIteration(
    string ParametersJson,
    decimal HealthScore,
    int IterationNumber);
```

**Parameter search space** is declared per `StrategyType` as a bounded range for each parameter. For example, `MovingAverageCrossover`:

```json
{
  "FastPeriod":  { "min": 3,  "max": 20,  "step": 1 },
  "SlowPeriod":  { "min": 15, "max": 60,  "step": 1 },
  "MaPeriod":    { "min": 40, "max": 200, "step": 5 }
}
```

The optimizer uses a **Gaussian Process surrogate model** (via a lightweight .NET implementation) to predict which parameter regions are likely to score well, balancing exploration vs exploitation with an Upper Confidence Bound (UCB) acquisition function.

#### ML Outcome Feedback

After each trade closes, `StrategyEvaluationWorker` appends a labelled record to an outcome table used by `MLRetrainingWorker`:

```csharp
public class OutcomeLabelledSample : Entity<long>
{
    public long TradeSignalId { get; set; }    // FK → TradeSignal
    public long OrderId { get; set; }          // FK → Order
    public string Symbol { get; set; }
    public string Timeframe { get; set; }
    public string FeaturesJson { get; set; }   // Feature vector at signal time (serialised SignalFeatureVector)
    public string ActualDirection { get; set; } // "Buy" | "Sell" — actual direction taken
    public bool WasProfitable { get; set; }    // true if RealizedPnL > 0
    public decimal ActualMagnitudePips { get; set; } // Actual pip movement captured
    public decimal RealizedPnL { get; set; }
    public DateTime CapturedAt { get; set; }
}
```

`MLRetrainingWorker` blends these live outcome samples into the training dataset, weighting each sample **3×** relative to synthetic candle-derived labels. As live sample volume grows, the model progressively relies more on real outcomes.

#### Tiered Auto-Response

| Tier | Condition | Automated Action | Human Action Required |
|---|---|---|---|
| 1 — Degrading | `HealthScore` drops below 0.60 | Emit `StrategyHealthDegradedIntegrationEvent` → alert | Review via dashboard |
| 2 — Critical | `HealthScore` drops below 0.40 | Auto-pause strategy + queue `OptimizationRun` + emit alert | Approve or reject proposed parameters via API |
| 3 — Optimized | `OptimizationRun` completes with `BestHealthScore > BaselineHealthScore + 0.05` | Persist proposed params, set `OptimizationRun.Status = "PendingApproval"` + emit alert | Call `ApproveOptimizedParametersCommand` to apply |

Approved parameters are applied by updating `Strategy.ParametersJson` and re-activating the strategy.

#### Commands

| Command | Description |
|---|---|
| `TriggerOptimizationRunCommand` | Manually queue an optimization run for a strategy |
| `ApproveOptimizedParametersCommand` | Apply the best parameters from a completed run to the live strategy |
| `RejectOptimizedParametersCommand` | Discard the proposed parameters (strategy stays paused or on current params) |

#### Queries

| Query | Description |
|---|---|
| `GetStrategyPerformanceQuery` | Fetch the latest snapshot for a strategy |
| `GetPagedStrategyPerformancesQuery` | List snapshots with filters (strategyId, healthStatus, date range) |
| `GetOptimizationRunQuery` | Fetch a single optimization run with all iterations |
| `GetPagedOptimizationRunsQuery` | List optimization runs with filters (strategyId, status, triggerType) |

#### Background Worker: `StrategyEvaluationWorker`

```
Event-driven (subscribes to PositionClosedIntegrationEvent):
  1. Identify the Strategy that originated the signal → order → position
  2. Load last N closed trades for that strategy (N = configurable window, default 20)
  3. Compute WinRate, ProfitFactor, SharpeRatio, MaxDrawdownPct, TotalPnL
  4. Compute HealthScore and determine HealthStatus
  5. Persist new StrategyPerformanceSnapshot
  6. Apply tiered response (see table above)
  7. Append OutcomeLabelledSample for ML feedback

Scheduled batch (weekly cron, same schedule as MLRetrainingWorker):
  For each Active strategy:
    1. Queue OptimizationRun (TriggerType = "Scheduled")
    2. Publish OptimizationRunQueuedIntegrationEvent

OptimizationRun worker (picks up OptimizationRunQueuedIntegrationEvent):
  1. Set status = "Running"
  2. Load historical candles for the strategy's symbol/timeframe
  3. Record baseline: run backtest with current ParametersJson → BaselineHealthScore
  4. Run IStrategyOptimizer.OptimizeAsync() (default 50 iterations)
  5. If BestHealthScore > BaselineHealthScore + 0.05:
       - Save best params to OptimizationRun.BestParametersJson
       - Set status = "Completed" (PendingApproval)
       - Publish OptimizationRunCompletedIntegrationEvent → alert
  6. Else: set status = "Completed" (no improvement found, no action)
```

---

### 5.11 ML Model Evaluation & Continuous Improvement

**Purpose:** Monitor deployed ML model prediction quality against live trade outcomes, detect model drift before it damages signal quality, evaluate newly trained challenger models in shadow mode against the active champion, and promote challengers only when they demonstrably outperform on real market conditions. Hyperparameters for both ML.NET models are tuned automatically at training time via cross-validated grid search.

#### Full Lifecycle Overview

```
New MLModel trained (MLRetrainingWorker)
        ↓
Shadow evaluation starts (MLShadowEvaluationWorker)
  Champion + Challenger both score every signal
  Only champion scores used for execution gating
  Prediction logs recorded for both
        ↓
PositionClosedIntegrationEvent → outcomes recorded on prediction logs
        ↓
After N trades with resolved outcomes:
  Compare on 3 criteria (direction accuracy, magnitude correlation, Brier score)
        ↓
  All 3 pass (challenger better by margin) → Auto-promote challenger → champion
  Some pass                                → Flag for human review (PendingReview)
  None pass                                → Reject challenger, keep champion
        ↓
Drift monitoring runs continuously on champion:
  Rolling accuracy window drops below threshold → trigger emergency retraining
```

#### Champion-Challenger Shadow Mode

Every time `MLRetrainingWorker` produces a new model it becomes a **Challenger**. A `MLShadowEvaluation` record is created and `MLShadowEvaluationWorker` begins logging predictions from both models on every signal for that (Symbol, Timeframe).

```csharp
public interface IMLShadowEvaluator
{
    // Scores a signal using both champion and challenger models.
    // Champion score is returned for live use; challenger score is logged only.
    Task<ShadowScoreResult> ScoreAsync(
        TradeSignal signal,
        IReadOnlyList<Candle> candles,
        CancellationToken ct);
}

public record ShadowScoreResult(
    MLScoreResult ChampionScore,   // Used for execution gating
    MLScoreResult? ChallengerScore // Logged to MLModelPredictionLog, not used for gating
);
```

#### Promotion Criteria

Evaluated after `RequiredTrades` prediction logs have resolved outcomes. All three conditions must pass for auto-promotion:

| Metric | Condition | Notes |
|---|---|---|
| Direction accuracy | Challenger > Champion + 3% | e.g., 61% vs 58% |
| Magnitude correlation | Challenger Pearson correlation > Champion | Actual vs predicted pip movement |
| Confidence calibration | Challenger Brier score < Champion Brier score | Lower = better calibrated probabilities |

**Outcome:**

| Criteria met | Action |
|---|---|
| All 3 | Auto-promote: challenger becomes champion, previous champion status = "Superseded" |
| 1 or 2 | Set `MLShadowEvaluation.Status = "FlaggedForReview"`, emit alert — human decides via `PromoteMLModelCommand` |
| None | Reject challenger, champion unchanged, emit informational event |

#### Drift Detection

`MLDriftMonitorWorker` runs continuously on the active champion model using a rolling window of the last M prediction logs with resolved outcomes:

```csharp
public interface IMLDriftDetector
{
    DriftCheckResult Check(
        IReadOnlyList<MLModelPredictionLog> recentPredictions,
        MLDriftConfig config);
}

public record DriftCheckResult(
    bool DriftDetected,
    decimal CurrentAccuracy,
    decimal BaselineAccuracy,     // Accuracy recorded at model activation time
    decimal AccuracyDrop,
    string? Reason);
```

**Drift triggers** (configurable in `MLConfig`):

| Condition | Action |
|---|---|
| Rolling direction accuracy drops > 5% below activation baseline | Trigger emergency retraining (`TriggerType = "DriftDetected"`) + emit `MLDriftDetectedIntegrationEvent` |
| Rolling Brier score increases > 0.10 above activation baseline | Same as above |
| Consecutive wrong direction predictions >= 10 | Same as above |

Emergency retraining bypasses the weekly schedule — it runs immediately and follows the same shadow evaluation flow before promotion.

#### Hyperparameter Tuning

At training time, before fitting the final models, a **coarse grid search with cross-validation** is run over the most impactful hyperparameters for each ML.NET algorithm:

```
Direction classifier (FastTree / LightGBM):
  NumberOfLeaves:       [20, 50, 100]
  NumberOfTrees:        [100, 200, 500]
  LearningRate:         [0.05, 0.1, 0.2]
  MinDataInLeaf:        [10, 20]

Magnitude regressor (FastForest):
  NumberOfTrees:        [100, 200, 500]
  NumberOfLeaves:       [20, 50]
  MinDataInLeaf:        [10, 20]
```

3-fold cross-validation on the training set selects the best combination before the final model is trained on the full dataset. The winning hyperparameters are stored in `MLModel.HyperparametersJson` for reproducibility.

```csharp
public interface IMLHyperparameterTuner
{
    Task<HyperparameterSearchResult> TuneAsync(
        IDataView trainingData,
        string taskType,        // "Classification" | "Regression"
        CancellationToken ct);
}

public record HyperparameterSearchResult(
    string BestHyperparametersJson,
    decimal BestCrossValidationScore,
    List<HyperparameterTrialResult> AllTrials);
```

#### Updated `MLModel` fields

Two new fields added to `MLModel`:

```csharp
public string? HyperparametersJson { get; set; }   // Best hyperparameters from grid search
public decimal? ActivationAccuracy { get; set; }   // Direction accuracy at time of activation (drift baseline)
```

#### Commands

| Command | Description |
|---|---|
| `PromoteMLModelCommand` | Manually promote a challenger to champion (used when `FlaggedForReview`) |
| `RejectMLChallengerCommand` | Manually reject a challenger and close the shadow evaluation |

#### Queries

| Query | Description |
|---|---|
| `GetMLShadowEvaluationQuery` | Fetch a shadow evaluation with current metrics for both models |
| `GetPagedMLShadowEvaluationsQuery` | List evaluations with filters (symbol, timeframe, status) |
| `GetMLModelPredictionLogsQuery` | Fetch prediction accuracy logs for a model over a date range |
| `GetMLDriftSummaryQuery` | Current rolling accuracy, baseline, and drift status per active model |

#### Background Workers

**`MLShadowEvaluationWorker`**
```
Subscribes to TradeSignalCreatedIntegrationEvent:
  1. If active shadow evaluation exists for signal's (Symbol, Timeframe):
     a. Score signal with champion via IMLSignalScorer (existing)
     b. Score signal with challenger model
     c. Persist MLModelPredictionLog for both (Role = "Champion" / "Challenger")

Subscribes to PositionClosedIntegrationEvent:
  1. Find MLModelPredictionLog records linked to the closed signal
  2. Populate ActualDirection, ActualMagnitudePips, WasProfitable, DirectionCorrect
  3. Increment MLShadowEvaluation.CompletedTrades
  4. If CompletedTrades >= RequiredTrades:
     a. Compute champion and challenger metrics
     b. Apply promotion criteria
     c. Update MLShadowEvaluation.Status and PromotionDecision
     d. If auto-promoted: call ActivateMLModelCommand, hot-reload IMLModelLoader
     e. Publish MLShadowEvaluationCompletedIntegrationEvent
```

**`MLDriftMonitorWorker`**
```
Runs every hour:
  For each active (Symbol, Timeframe) model:
    1. Load last M MLModelPredictionLog records with resolved outcomes (Role = "Champion")
    2. Call IMLDriftDetector.Check()
    3. If DriftDetected:
       a. Publish MLDriftDetectedIntegrationEvent → alert
       b. Trigger emergency MLTrainingRun (TriggerType = "DriftDetected")
       c. New model enters shadow evaluation immediately on completion
```

---

### 5.12 Multi-Timeframe Confluence

**Purpose:** Filter rule-based signals using higher-timeframe context, reducing false entries by only taking trades where the macro trend agrees with the signal direction.

#### How It Works

Each strategy declares a `HigherTimeframe` in `ParametersJson`. When `StrategyWorker` evaluates a signal, it loads candles for both the strategy's primary timeframe and the higher timeframe, computes a trend direction on the higher timeframe, and only passes the signal forward if both agree.

```json
{
  "StrategyType": "MovingAverageCrossover",
  "FastPeriod": 9,
  "SlowPeriod": 21,
  "HigherTimeframe": "H4",
  "HigherTimeframeTrendIndicator": "EMA200"
}
```

#### Interface

```csharp
public interface IMultiTimeframeFilter
{
    // Returns true if higher timeframe trend agrees with signal direction.
    Task<MTFFilterResult> EvaluateAsync(
        string higherTimeframe,
        string trendIndicator,
        string signalDirection,
        IReadOnlyList<Candle> higherTimeframeCandles,
        CancellationToken ct);
}

public record MTFFilterResult(
    bool Passes,
    string HigherTimeframeTrend,   // "Bullish" | "Bearish" | "Neutral"
    decimal IndicatorValue);
```

`TradeSignal` gains two new fields: `MTFConfluence` (bool) and `HigherTimeframeTrend` (string).

#### Integration with StrategyWorker

```
StrategyWorker on PriceUpdatedIntegrationEvent:
  1. Evaluate primary timeframe (existing)
  2. If signal returned AND strategy has HigherTimeframe configured:
     a. Load higher timeframe candles from read DB
     b. Call IMultiTimeframeFilter.EvaluateAsync()
     c. Attach MTFConfluence and HigherTimeframeTrend to signal
     d. If MTFConfluence = false AND strategy.MTFFilterStrict = true → discard signal
        If MTFConfluence = false AND strategy.MTFFilterStrict = false → persist signal with MTFConfluence = false (execution worker can decide)
```

No new commands or queries — configuration is entirely via strategy `ParametersJson`.

---

### 5.13 News & Economic Calendar Filter

**Purpose:** Protect capital by pausing signal execution around high-impact economic events. Extreme volatility during news releases frequently blows through stops and produces unrepresentative fills.

#### Commands

| Command | Description |
|---|---|
| `SyncEconomicCalendarCommand` | Pull upcoming events from external calendar source (called by sync worker) |
| `CreateEconomicEventCommand` | Manually add a one-off event |
| `UpdateEconomicEventCommand` | Update actual value after event releases |
| `DeleteEconomicEventCommand` | Soft-delete |

#### Queries

| Query | Description |
|---|---|
| `GetUpcomingEconomicEventsQuery` | Events in the next N hours, optionally filtered by currency and impact |
| `GetPagedEconomicEventsQuery` | Full list with filters |
| `IsNewsWindowActiveQuery` | Returns true if any high-impact event affecting a symbol is within the blackout window |

#### News Filter Interface

```csharp
public interface INewsFilter
{
    // Returns true if execution should be blocked for this symbol right now.
    Task<NewsFilterResult> CheckAsync(string symbol, DateTime utcNow, CancellationToken ct);
}

public record NewsFilterResult(
    bool Blocked,
    string? EventTitle,
    string? Impact,
    DateTime? EventTime,
    int MinutesUntilOrSince);
```

#### Integration with OrderExecutionWorker

Before submitting any order to the broker:
```
1. Call INewsFilter.CheckAsync(order.Symbol, utcNow)
2. If blocked:
   a. Hold order (requeue for retry after blackout window clears)
   b. Or reject signal if signal ExpiresAt < end of blackout window
   c. Log NewsFilterBlockedEvent
```

Each strategy can configure news sensitivity in `ParametersJson`:
```json
{
  "NewsFilter": {
    "BlockHighImpact": true,
    "BlockMediumImpact": false,
    "MinutesBefore": 15,
    "MinutesAfter": 30
  }
}
```

#### Background Worker: `EconomicCalendarSyncWorker`

```
Daily at 00:00 UTC:
  1. Fetch next 7 days of events from configured source (ForexFactory RSS / Investing.com API)
  2. Upsert into EconomicEvents table via SyncEconomicCalendarCommand
  3. Publish EconomicCalendarSyncedIntegrationEvent
```

---

### 5.14 Portfolio Correlation Risk

**Purpose:** Prevent the engine from accidentally concentrating exposure in a single currency by taking multiple positions in highly correlated pairs simultaneously.

#### The Problem

EURUSD and GBPUSD are typically correlated at 0.80+. Holding both Long simultaneously is nearly equivalent to double USD short exposure — yet the per-trade risk checks in Section 5.6 would pass both individually.

#### Interface

```csharp
public interface ICorrelationRiskChecker
{
    Task<CorrelationRiskResult> CheckAsync(
        TradeSignal incomingSignal,
        IReadOnlyList<Position> openPositions,
        IReadOnlyList<Candle> recentCandles,   // Used to compute rolling correlation
        RiskProfile profile,
        CancellationToken ct);
}

public record CorrelationRiskResult(
    bool Passed,
    IReadOnlyList<CorrelationViolation> Violations);

public record CorrelationViolation(
    string ExistingSymbol,
    string IncomingSymbol,
    decimal Correlation,
    decimal ProjectedExposurePct);
```

#### Correlation Matrix

Computed on-demand using rolling 20-period returns from recent candles for each open position's symbol vs the incoming signal's symbol:

```
Pearson correlation on log returns:
  r = cov(returns_A, returns_B) / (σ_A × σ_B)
```

Pre-trade check added to `IRiskChecker` pipeline:

| Rule | Block condition |
|---|---|
| High correlation | `r > 0.75` AND combined same-direction exposure > `profile.MaxCorrelatedExposurePct` |
| Currency concentration | Net exposure to any single currency (base or quote) > `profile.MaxSingleCurrencyExposurePct` |

#### New `RiskProfile` fields

```csharp
public decimal MaxCorrelatedExposurePct { get; set; }    // Max combined exposure in correlated pairs
public decimal MaxSingleCurrencyExposurePct { get; set; } // Max net exposure to one currency
```

#### Queries

| Query | Description |
|---|---|
| `GetPortfolioCorrelationQuery` | Current correlation matrix across all open position symbols |
| `GetCurrencyExposureQuery` | Net long/short exposure per currency across all open positions |

---

### 5.15 Session-Aware Execution

**Purpose:** Restrict strategy evaluation and order execution to the forex trading sessions where each strategy historically performs best, avoiding low-liquidity periods that produce erratic fills and false breakouts.

#### Sessions

| Session | UTC Hours | Characteristics |
|---|---|---|
| Asian | 00:00 – 09:00 | Low volatility, JPY/AUD pairs most active |
| London | 08:00 – 17:00 | High liquidity, EUR/GBP pairs most active |
| New York | 13:00 – 22:00 | High volatility, USD pairs most active |
| London-NY Overlap | 13:00 – 17:00 | Highest liquidity of the day |

#### Configuration

Per-strategy in `ParametersJson`:
```json
{
  "AllowedSessions": ["London", "LondonNYOverlap"],
  "BlockFridayAfter": "20:00",
  "BlockMondayBefore": "08:00"
}
```

#### Session Filter Interface

```csharp
public interface ISessionFilter
{
    SessionFilterResult Check(string[] allowedSessions, DateTime utcNow);
}

public record SessionFilterResult(
    bool Allowed,
    string CurrentSession,    // Which session is currently active (or "OffMarket")
    string? BlockReason);
```

`StrategyWorker` calls `ISessionFilter` before calling `IStrategyEvaluator` — if the session is not allowed, evaluation is skipped entirely for that strategy on that candle. No new entities required; configuration is entirely via `ParametersJson`.

---

### 5.16 Walk-Forward Optimization

**Purpose:** Replace the single in-sample backtest in the optimization loop with a rolling walk-forward validation, producing parameter sets that genuinely generalize rather than overfit to the training window.

#### How Walk-Forward Works

The full historical data range is divided into rolling windows:

```
|--- In-Sample (optimize) ---|--- Out-of-Sample (validate) ---|
                              |--- In-Sample ---|--- Out-of-Sample ---|
                                                |--- In-Sample ---|--- Out-of-Sample ---|
```

For each window:
1. Run Bayesian optimization on the in-sample period → best parameters
2. Run a plain backtest on the out-of-sample period using those parameters → OOS HealthScore
3. Advance window by `StepDays`

Final result is the **average OOS HealthScore** and its **standard deviation** (consistency). A strategy that scores 0.65 average with low std dev is far more trustworthy than one scoring 0.75 with high variance.

#### Commands

| Command | Description |
|---|---|
| `QueueWalkForwardRunCommand` | Queue a WFO job for a strategy |
| `CancelWalkForwardRunCommand` | Cancel a queued/running job |

#### Queries

| Query | Description |
|---|---|
| `GetWalkForwardRunQuery` | Fetch run with per-window results |
| `GetPagedWalkForwardRunsQuery` | List runs with filters |

#### Walk-Forward Engine Interface

```csharp
public interface IWalkForwardEngine
{
    Task<WalkForwardResult> RunAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        decimal initialBalance,
        RiskProfile riskProfile,
        WalkForwardConfig config,
        CancellationToken ct);
}

public record WalkForwardConfig(
    int InSampleDays,
    int OutOfSampleDays,
    int StepDays,
    int OptimizationIterations);

public record WalkForwardResult(
    decimal AverageOutOfSampleScore,
    decimal ScoreConsistency,           // Standard deviation across OOS windows
    string BestParametersJson,          // Params from the most recent in-sample window
    List<WalkForwardWindowResult> Windows);

public record WalkForwardWindowResult(
    DateTime InSampleFrom, DateTime InSampleTo,
    DateTime OutOfSampleFrom, DateTime OutOfSampleTo,
    string OptimizedParametersJson,
    decimal InSampleScore,
    decimal OutOfSampleScore);
```

#### Integration with Optimization

`OptimizationRunWorker` is updated to prefer `IWalkForwardEngine` over a single `IBacktestEngine` call when `WalkForwardEnabled = true` in strategy `ParametersJson`. The `BestParametersJson` from the most recent WFO window becomes the proposed parameters in `OptimizationRun`.

---

### 5.17 Market Regime Detection

**Purpose:** Classify the current market condition for each symbol/timeframe and suppress strategies in regimes where they historically underperform.

#### Regimes

| Regime | Characteristics | Best Strategy Types |
|---|---|---|
| Trending | Strong directional move, high ADX | Moving average crossover, breakout |
| Ranging | Price oscillating within a band, low ADX | RSI reversion, mean reversion |
| HighVolatility | Wide candles, ATR spike above average | Reduced lot size; wider stops |
| LowVolatility | Tight candles, ATR well below average | Breakout strategies on standby |

#### Interface

```csharp
public interface IRegimeClassifier
{
    RegimeClassification Classify(
        IReadOnlyList<Candle> candles,
        RegimeConfig config);
}

public record RegimeClassification(
    string Regime,
    decimal Confidence,
    decimal ADX,
    decimal ATR,
    decimal BollingerBandWidth);
```

Regime is determined by combining two indicators:
- **ADX >= 25** → Trending; **ADX < 20** → Ranging
- **ATR > 1.5× 20-period average ATR** → HighVolatility; **ATR < 0.5× average** → LowVolatility
- HighVolatility takes precedence over Trending/Ranging when ATR threshold is breached

#### Configuration

Per-strategy in `ParametersJson`:
```json
{
  "OptimalRegimes": ["Trending", "HighVolatility"],
  "RegimeFilterStrict": true
}
```

If `RegimeFilterStrict = true` and current regime is not in `OptimalRegimes`, `StrategyWorker` skips evaluation entirely. If `false`, signal is generated with `RegimeAtSignalTime` attached but not blocked.

`TradeSignal` gains a new field: `RegimeAtSignalTime` (string).

#### Background Worker: `MarketRegimeWorker`

```
Subscribes to PriceUpdatedIntegrationEvent (candle close only):
  1. Call IRegimeClassifier.Classify() using last N candles
  2. If regime changed from previous snapshot: persist new MarketRegimeSnapshot
  3. Publish MarketRegimeChangedIntegrationEvent → StrategyWorker reloads cached regime
```

#### Commands & Queries

| Type | Name | Description |
|---|---|---|
| Query | `GetCurrentRegimeQuery` | Current regime for a symbol/timeframe |
| Query | `GetPagedRegimeHistoryQuery` | Historical regime snapshots with filters |

---

### 5.18 Execution Quality Analysis

**Purpose:** Track slippage, fill latency, and fill rates per order to distinguish between signal quality problems and execution quality problems in performance attribution.

#### `Order` Extension

One new field added to `Order`:
```csharp
public decimal? SlippagePips { get; set; }   // Populated on fill: abs(FilledPrice - Price) / PipSize
```

#### How It Is Recorded

`OrderExecutionWorker` populates `ExecutionQualityLog` and `Order.SlippagePips` immediately when a fill is received from the broker:

```
OrderFilledIntegrationEvent received:
  1. Compute SlippagePips = abs(FilledPrice - RequestedPrice) / CurrencyPair.PipSize
  2. Compute SubmitToFillMs = FilledAt - SubmittedAt
  3. Determine active Session at fill time
  4. Persist ExecutionQualityLog
  5. Update Order.SlippagePips
```

#### Queries

| Query | Description |
|---|---|
| `GetExecutionQualityQuery` | Slippage and fill stats for a single order |
| `GetExecutionQualitySummaryQuery` | Aggregate stats: avg slippage, avg latency, fill rate — filterable by symbol, session, strategy, date range |

#### How It Feeds Back

The `IBacktestEngine` can load the average slippage per symbol/session from `ExecutionQualityLog` and apply it as a realistic slippage model in backtests, making simulated results more accurate than assuming zero slippage. Configurable via `ParametersJson`:
```json
{ "SlippageModel": "Historical" }
```

---

### 5.19 Strategy Ensemble & Capital Allocation

**Purpose:** Dynamically allocate capital across active strategies based on their rolling risk-adjusted performance, and optionally require multi-strategy confluence before executing a signal.

#### Capital Allocation

Each strategy has a `StrategyAllocation` record with a `Weight` (0.0–1.0, normalised across all active strategies). Weights are rebalanced weekly by `AllocationRebalanceWorker` using rolling Sharpe ratios:

```
Weight_i = max(SharpeRatio_i, 0) / Σ max(SharpeRatio_j, 0)
```

Strategies with negative Sharpe ratio receive zero weight — their signals are generated but lot size is set to zero (no execution). This is softer than auto-pause: the strategy stays active and monitored but contributes nothing to execution until its Sharpe recovers.

Lot size calculation in `OrderExecutionWorker`:
```
AdjustedLotSize = BaseLotSize × StrategyAllocation.Weight × AllocationMultiplier
```

`AllocationMultiplier` is configurable per risk profile to scale the entire portfolio up or down.

#### Interface

```csharp
public interface IEnsembleAllocator
{
    Task<AllocationResult> RebalanceAsync(
        IReadOnlyList<Strategy> activeStrategies,
        IReadOnlyList<StrategyPerformanceSnapshot> snapshots,
        CancellationToken ct);
}

public record AllocationResult(
    IReadOnlyList<StrategyWeightAssignment> Weights,
    DateTime RebalancedAt);

public record StrategyWeightAssignment(long StrategyId, decimal Weight, decimal RollingSharpRatio);
```

#### Multi-Strategy Confluence Mode

Strategies targeting the same symbol can be grouped. When `EnsembleMode` is enabled in `appsettings.json`, a signal is only executed if the required number of active strategies for that symbol agree on direction within the signal expiry window:

```json
"EnsembleConfig": {
  "Mode": "Confluence",
  "MinConfluentStrategies": 2,
  "ConfluentWindowSeconds": 30
}
```

`OrderExecutionWorker` holds a pending signal in a short-lived buffer and checks if a second signal for the same symbol/direction arrives within the window before submitting.

#### Commands

| Command | Description |
|---|---|
| `TriggerAllocationRebalanceCommand` | Manually trigger a rebalance |
| `SetStrategyAllocationCommand` | Override a strategy's weight manually |

#### Queries

| Query | Description |
|---|---|
| `GetStrategyAllocationsQuery` | All current weights with Sharpe ratios |
| `GetEnsembleStatusQuery` | Current pending signals in confluence buffer, active strategy count per symbol |

#### Background Worker: `AllocationRebalanceWorker`

```
Weekly cron (same schedule as optimization):
  1. Load all Active strategies and their latest StrategyPerformanceSnapshot
  2. Call IEnsembleAllocator.RebalanceAsync()
  3. Upsert StrategyAllocation records
  4. Publish AllocationRebalancedIntegrationEvent → alert with weight changes
```

---

### 5.20 Paper Trading / Simulation Mode

**Purpose:** Run the full live engine pipeline — real market data, real strategy evaluation, real risk checks, real ML scoring — but route orders to a simulated order book instead of the broker. Validates system behaviour with zero real capital at risk before going live.

#### How It Works

Paper mode is configured per strategy via `ParametersJson`:
```json
{ "TradingMode": "Paper" }
```

`OrderExecutionWorker` resolves the executor based on `TradingMode`:
- `"Live"` → real `IBrokerOrderExecutor`
- `"Paper"` → `IPaperBrokerOrderExecutor`

`IsPaper = true` is stamped on all `Order` and `Position` records created from paper strategies. Paper and live records coexist in the same tables, always filterable by `IsPaper`.

#### Paper Broker Interface

```csharp
public interface IPaperBrokerOrderExecutor : IBrokerOrderExecutor
{
    // Simulates immediate fill at current mid-price + configurable slippage
}
```

Implementation behaviour:
- **Market orders**: filled immediately at current bid/ask from `ILivePriceCache` + configured slippage
- **Limit/Stop orders**: held in memory, filled when price crosses the level on next tick
- **Fill latency**: simulated delay (configurable, default 50ms) to mimic real execution

#### What Paper Mode Validates

- Worker pipeline timing and event flow end-to-end on live data
- Risk checks fire correctly under real market conditions
- ML scoring produces sensible predictions on live candles
- Strategy evaluators generate signals at expected frequencies
- All integration events emitted and consumed correctly

#### Paper vs Live Segregation in Queries

All existing queries (`GetPagedOrdersQuery`, `GetPagedPositionsQuery`, `GetPortfolioSummaryQuery`) accept an optional `IsPaper` filter. Paper P&L is tracked in `Position.RealizedPnL` like live, but aggregated separately in `GetPortfolioSummaryQuery`.

No new commands or queries required — `IsPaper` filter added to existing list queries.

---

### 5.21 Audit Trail & Decision Log

**Purpose:** Record every significant decision the engine makes as an immutable, queryable log entry with full context. This is the single source of truth for understanding *why* the engine behaved a certain way at any point in time.

#### What Gets Logged

Every decision point across all workers and handlers writes a `DecisionLog` entry:

| Source | Decision Type | Example Reason |
|---|---|---|
| `OrderExecutionWorker` | `SignalApproved` | "All risk checks passed; ML confidence 0.78 above threshold 0.65" |
| `OrderExecutionWorker` | `SignalRejected` | "Risk: MaxDailyTrades limit reached (20/20)" |
| `OrderExecutionWorker` | `OrderBlocked` | "NewsFilter: US NFP release in 12 minutes" |
| `OrderExecutionWorker` | `OrderBlocked` | "CorrelationRisk: GBPUSD Long would create 85% USD exposure (limit 60%)" |
| `StrategyWorker` | `SignalDiscarded` | "MTFConfluence: H4 trend bearish, signal direction Buy (strict mode)" |
| `StrategyWorker` | `SignalDiscarded` | "RegimeFilter: current regime Ranging, strategy requires Trending" |
| `StrategyWorker` | `SignalDiscarded` | "SessionFilter: current session Asian, strategy allows London only" |
| `RiskMonitorWorker` | `StrategyPaused` | "Total drawdown 10.2% exceeded MaxTotalDrawdownPct 10.0%" |
| `RiskMonitorWorker` | `RecoveryModeActivated` | "Drawdown 1.6% exceeded RecoveryThreshold 1.5%; lot size reduced to 50%" |
| `StrategyEvaluationWorker` | `OptimizationQueued` | "HealthScore 0.38 below Critical threshold 0.40" |
| `MLShadowEvaluationWorker` | `ChallengerPromoted` | "Direction accuracy +4.1%, magnitude correlation improved, Brier score improved" |
| `MLDriftMonitorWorker` | `EmergencyRetrainTriggered` | "Rolling accuracy dropped 6.2% below activation baseline (threshold 5%)" |

#### Interface

```csharp
public interface IDecisionLogger
{
    Task LogAsync(
        string entityType,
        long entityId,
        string decisionType,
        string outcome,
        string reason,
        object? context,       // Serialised to ContextJson
        string source,
        CancellationToken ct);
}
```

`IDecisionLogger` is injected into every worker and handler that makes a consequential decision. It writes asynchronously and never throws — a failed audit write must never block trading execution.

#### Queries

| Query | Description |
|---|---|
| `GetDecisionLogsQuery` | Paginated log entries filterable by entityType, entityId, decisionType, outcome, source, date range |
| `GetSignalDecisionSummaryQuery` | For a given TradeSignalId: full chain of decisions from generation through execution or rejection |

#### API

```
POST   /audit/decisions/list               → GetDecisionLogsQuery
GET    /audit/decisions/signal/{signalId}  → GetSignalDecisionSummaryQuery
```

---

### 5.22 Drawdown Recovery Mode

**Purpose:** Introduce a graduated response between normal operation and hard pause. When drawdown enters the "warning zone", lot sizes are automatically reduced to protect remaining capital — allowing the engine to continue trading at reduced risk rather than stopping entirely.

#### Recovery Mode Flow

```
Normal operation (drawdown < RecoveryThreshold)
        ↓ drawdown crosses RecoveryThresholdPct
Recovery Mode (lot size × RecoveryLotSizeMultiplier)
  + emit DrawdownRecoveryActivatedIntegrationEvent
  + persist DecisionLog
        ↓ drawdown continues to MaxDailyDrawdownPct
Auto-pause (existing Section 5.6 behaviour)
        ↓ drawdown recovers above RecoveryExitThresholdPct
Normal operation resumes
  + emit DrawdownRecoveryDeactivatedIntegrationEvent
```

#### Implementation

`RiskMonitorWorker` is extended to track a `RecoveryModeActive` flag per strategy (in-memory, rehydrated from `StrategyPerformanceSnapshot` on startup). When active, it publishes `RecoveryModeActivatedIntegrationEvent`. `OrderExecutionWorker` subscribes and applies the multiplier to all subsequent lot size calculations for that strategy until recovery exits.

`RiskProfile` gains three new fields (already added above):
- `DrawdownRecoveryThresholdPct` — drawdown % that activates recovery mode
- `RecoveryLotSizeMultiplier` — lot size fraction during recovery (e.g., 0.5)
- `RecoveryExitThresholdPct` — drawdown % at which normal sizing resumes

No new entities required — state tracked in `RiskMonitorWorker` memory and `DecisionLog`.

#### New Integration Events

| Event | Publisher |
|---|---|
| `DrawdownRecoveryActivatedIntegrationEvent` | `RiskMonitorWorker` |
| `DrawdownRecoveryDeactivatedIntegrationEvent` | `RiskMonitorWorker` |

---

### 5.23 Sentiment Data Integration

**Purpose:** Augment the engine's market view with macro sentiment signals — institutional positioning from CFTC COT reports and NLP sentiment from financial news headlines. These become additional inputs to the ML feature vector and can act as standalone macro filters.

#### Data Sources

| Source | Frequency | What It Provides |
|---|---|---|
| CFTC Commitment of Traders (COT) | Weekly (released Fridays) | Institutional long/short positioning per currency futures |
| Financial news NLP | Configurable (hourly) | Sentiment score per currency from headline analysis |

#### Interfaces

```csharp
public interface ISentimentDataFeed
{
    Task<IEnumerable<SentimentSnapshot>> FetchLatestAsync(
        IEnumerable<string> currencies,
        CancellationToken ct);
}

public interface ICOTDataFeed
{
    Task<IEnumerable<COTReport>> FetchLatestAsync(
        IEnumerable<string> currencies,
        CancellationToken ct);
}
```

#### Sentiment Score Calculation

COT `SentimentScore` is derived from net non-commercial positioning normalised over a 52-week range:
```
SentimentScore = (NetNonCommercialPositioning - 52wkMin) /
                 (52wkMax - 52wkMin) × 2 - 1      // normalised to [-1, +1]
```

News NLP score is computed using a pre-trained financial sentiment model (e.g., FinBERT or a lightweight ML.NET text classification model) applied to headline text, averaged across N recent headlines per currency.

#### Integration Points

**As ML Feature**
`FeatureExtractor` (Section 5.9) is extended to include sentiment features in the `SignalFeatureVector`:
```csharp
public float COTSentimentScore { get; set; }      // Latest COT score for signal's base currency
public float NewsSentimentScore { get; set; }     // Latest news NLP score
public float COTWeeklyChange { get; set; }        // Positioning change vs prior week
```

**As Macro Filter (optional, per strategy)**
```json
{
  "SentimentFilter": {
    "Enabled": true,
    "BlockBuyIfCOTBearish": true,       // Block Buy signals if COT score < -0.3
    "BlockSellIfCOTBullish": true,      // Block Sell signals if COT score > 0.3
    "COTThreshold": 0.3
  }
}
```

#### Background Worker: `SentimentWorker`

```
Weekly (Saturday 06:00 UTC — after CFTC Friday release):
  1. Call ICOTDataFeed.FetchLatestAsync()
  2. Upsert COTReport records
  3. Compute and persist SentimentSnapshot (source = "COT") per currency

Hourly:
  1. Call ISentimentDataFeed.FetchLatestAsync() (news NLP)
  2. Persist SentimentSnapshot (source = "NewsSentiment") per currency
  3. Publish SentimentUpdatedIntegrationEvent → StrategyWorker caches latest scores
```

#### Commands & Queries

| Type | Name | Description |
|---|---|---|
| Query | `GetLatestSentimentQuery` | Latest sentiment snapshot per currency |
| Query | `GetPagedCOTReportsQuery` | Historical COT reports with filters |
| Query | `GetSentimentHistoryQuery` | Sentiment score over time for a currency |

---

### 5.24 System Health Monitoring

**Purpose:** Provide a single, detailed health endpoint that reports the real-time status of every engine subsystem. Enables rapid detection of silent failures — a worker crash, stale market data, disconnected broker feed, or unloaded ML model.

#### Health Check Interface

```csharp
public interface ISystemHealthAggregator
{
    Task<SystemHealthReport> GetReportAsync(CancellationToken ct);
}

public record SystemHealthReport(
    string OverallStatus,              // "Healthy" | "Degraded" | "Unhealthy"
    DateTime GeneratedAt,
    IReadOnlyList<SubsystemHealth> Subsystems);

public record SubsystemHealth(
    string Name,
    string Status,                     // "Healthy" | "Degraded" | "Unhealthy" | "Unknown"
    string? Detail,
    DateTime? LastActivityAt,
    IReadOnlyDictionary<string, object> Metrics);
```

#### Subsystems Checked

| Subsystem | Healthy Condition | Key Metrics |
|---|---|---|
| `BrokerDataFeed` | Connected; last tick < 60s ago | LastTickAt, Symbol, ReconnectCount |
| `MarketDataWorker` | Running; last candle persisted < 5m ago | LastCandleAt, CandlesPerMinute |
| `StrategyWorker` | Running; last evaluation < 5m ago | LastEvaluatedAt, SignalsGeneratedToday |
| `OrderExecutionWorker` | Running; no stuck Submitted orders > 60s | PendingOrders, LastFilledAt |
| `RiskMonitorWorker` | Running; last check < 2m ago | LastCheckAt, ActiveRecoveryStrategies |
| `MLSignalScorer` | Models loaded for all active (Symbol, Timeframe) pairs | LoadedModels, MissingModels |
| `MLDriftMonitorWorker` | Running; no drift detected | LastCheckAt, DriftDetectedSymbols |
| `RabbitMQ` | Connected; queue depth < threshold | QueueDepth, ConnectionStatus |
| `WriteDb` | Reachable; query latency < 500ms | LatencyMs |
| `ReadDb` | Reachable; query latency < 500ms | LatencyMs |
| `BrokerFailover` | Primary active; secondary on standby | ActiveBroker, FailoverCount |

#### API

```
GET    /health/detailed     → Full SystemHealthReport (authenticated)
GET    /health              → Existing ASP.NET basic health check (unauthenticated, for load balancer)
```

`/health/detailed` is authenticated (requires JWT). `/health` remains unauthenticated for infrastructure probes.

No new DB entities — health state is computed in real time from in-memory worker state, last event timestamps, and lightweight DB pings.

---

### 5.25 Broker Failover & Redundancy

**Purpose:** Eliminate the broker connection as a single point of failure. If the primary broker's data feed or order execution becomes unavailable, the engine automatically switches to a configured secondary provider without human intervention.

#### Configuration

```json
"BrokerConfig": {
  "Primary": {
    "Provider": "OANDA",
    "ApiKey": "...",
    "AccountId": "...",
    "Environment": "practice"
  },
  "Secondary": {
    "Provider": "InteractiveBrokers",
    "ApiKey": "...",
    "AccountId": "...",
    "Environment": "paper"
  },
  "FailoverConfig": {
    "MaxReconnectAttempts": 5,
    "ReconnectBackoffSeconds": [1, 2, 4, 8, 16],
    "FailoverTriggerAfterAttempts": 3,
    "AutoFailbackEnabled": true,
    "FailbackCheckIntervalMinutes": 5
  }
}
```

#### Failover Manager Interface

```csharp
public interface IBrokerFailoverManager
{
    IBrokerDataFeed ActiveDataFeed { get; }
    IBrokerOrderExecutor ActiveOrderExecutor { get; }
    string ActiveProvider { get; }                // "Primary" | "Secondary"
    bool IsFailoverActive { get; }

    Task TriggerFailoverAsync(string reason, CancellationToken ct);
    Task AttemptFailbackAsync(CancellationToken ct);
}
```

#### Failover Flow

```
MarketDataWorker detects connection loss:
  1. Attempt reconnect (exponential backoff per ReconnectBackoffSeconds)
  2. After FailoverTriggerAfterAttempts failed reconnects:
     a. IBrokerFailoverManager.TriggerFailoverAsync()
     b. ActiveDataFeed + ActiveOrderExecutor swap to secondary
     c. Publish BrokerFailoverActivatedIntegrationEvent → alert
     d. Log DecisionLog entry (source = "BrokerFailoverManager")
  3. MarketDataWorker reconnects using new ActiveDataFeed
  4. OrderExecutionWorker switches to new ActiveOrderExecutor for new orders
     (in-flight orders on primary are reconciled via status polling on secondary)

Failback (if AutoFailbackEnabled):
  Every FailbackCheckIntervalMinutes: probe primary connection
  If primary responds:
    1. IBrokerFailoverManager.AttemptFailbackAsync()
    2. Swap back to primary feeds
    3. Publish BrokerFailbackCompletedIntegrationEvent → alert
```

#### Open Position Handling During Failover

Open positions remain on the primary broker account even after failover. `OrderExecutionWorker` continues to poll primary for position status (via a degraded REST-only connection if WebSocket is down). New orders go through the secondary. This is recorded in `DecisionLog` with `DecisionType = "FailoverOrderRouting"`.

#### New Integration Events

| Event | Publisher |
|---|---|
| `BrokerFailoverActivatedIntegrationEvent` | `IBrokerFailoverManager` |
| `BrokerFailbackCompletedIntegrationEvent` | `IBrokerFailoverManager` |

---

### 5.26 Trailing Stop & Position Scaling

**Purpose:** Dynamically manage open positions after entry — move stops to lock in profits as price moves in favour, add to winning positions (scale-in), and take partial profits at intermediate targets (scale-out).

#### Trailing Stop

Three trail types, configured per strategy in `ParametersJson`:

| Type | Behaviour | Example Config |
|---|---|---|
| `FixedPips` | Trail stop N pips behind highest favourable price | `"TrailingStop": { "Type": "FixedPips", "Value": 20 }` |
| `ATR` | Trail stop N × ATR behind highest favourable price | `"TrailingStop": { "Type": "ATR", "Value": 1.5 }` |
| `Percentage` | Trail stop N% behind highest favourable price | `"TrailingStop": { "Type": "Percentage", "Value": 0.5 }` |

`Order` gains four new fields (already added): `TrailingStopEnabled`, `TrailingStopType`, `TrailingStopValue`, `HighestFavourablePrice`. `Position` gains `TrailingStopLevel` (current computed stop price).

#### Trailing Stop Interface

```csharp
public interface ITrailingStopManager
{
    // Computes the new stop level given current price and trail config.
    // Returns null if the stop should not move (price hasn't advanced enough).
    decimal? ComputeNewStop(
        Position position,
        Order originalOrder,
        decimal currentPrice,
        IReadOnlyList<Candle> recentCandles);  // Used for ATR calculation
}
```

#### Position Scaling

Scale levels are declared in strategy `ParametersJson`:

```json
{
  "ScaleIn": [
    { "Step": 1, "TriggerPips": 20, "LotSize": 0.5 },
    { "Step": 2, "TriggerPips": 40, "LotSize": 0.5 }
  ],
  "ScaleOut": [
    { "Step": 1, "TriggerPips": 30, "LotSize": 0.5 },
    { "Step": 2, "TriggerPips": 60, "LotSize": 0.5 }
  ]
}
```

When a position opens, `OpenPositionCommand` creates `PositionScaleOrder` records for each declared step. `TrailingStopWorker` monitors these and triggers the associated order when the price condition is met.

#### Background Worker: `TrailingStopWorker`

```
Subscribes to PriceUpdatedIntegrationEvent (tick-level, not just candle close):
  For each open position with TrailingStopEnabled = true:
    1. Call ITrailingStopManager.ComputeNewStop()
    2. If new stop > current stop (Long) or new stop < current stop (Short):
       a. Update Position.TrailingStopLevel
       b. Update Order.HighestFavourablePrice
       c. Call ModifyOrderCommand (sends new SL to broker)
       d. Log DecisionLog entry

  For each pending PositionScaleOrder:
    1. Check if current price meets TriggerPips condition
    2. If triggered:
       a. Create new Order (ScaleIn → Buy/Sell; ScaleOut → counter-direction partial close)
       b. Submit via SubmitOrderCommand
       c. Update PositionScaleOrder.Status = "Triggered"
       d. Log DecisionLog entry
```

#### Commands

| Command | Description |
|---|---|
| `AddTrailingStopCommand` | Enable trailing stop on an existing open order |
| `ModifyTrailingStopCommand` | Change trail type or value on an active trailing stop |
| `AddScaleOrderCommand` | Manually add a scale-in or scale-out level to an open position |
| `CancelScaleOrderCommand` | Cancel a pending scale order |

#### Queries

| Query | Description |
|---|---|
| `GetPositionScaleOrdersQuery` | All scale orders for a position with status |

---

### 5.27 Performance Attribution

**Purpose:** Answer the question "under what conditions does each strategy actually perform?" by breaking down realised P&L across every contextual dimension already being collected by the engine. No new entities — pure aggregation queries over existing data.

#### Attribution Dimensions

| Dimension | Source Data | Example Insight |
|---|---|---|
| Session | `ExecutionQualityLog.Session` | "EURUSD strategy earns 80% of P&L during London-NY overlap" |
| Market Regime | `TradeSignal.RegimeAtSignalTime` | "Breakout strategy loses in Ranging regime; +ve P&L only in Trending" |
| ML Confidence Tier | `TradeSignal.MLConfidenceScore` | "Trades with score > 0.75 win 68%; trades < 0.55 win only 41%" |
| News Proximity | `EconomicEvent` joined to trade time | "Trades within 30min of High-impact events have 2× drawdown" |
| MTF Confluence | `TradeSignal.MTFConfluence` | "Confluent signals: 62% win rate; non-confluent: 44%" |
| Symbol | `Position.Symbol` | "GBPJPY generates highest P&L but also highest drawdown" |
| Day of Week | `Position.OpenedAt` | "Monday trades underperform vs Tuesday–Thursday" |

#### Queries

| Query | Description |
|---|---|
| `GetAttributionBySessionQuery` | Win rate, avg P&L, trade count grouped by session |
| `GetAttributionByRegimeQuery` | Same grouped by regime at signal time |
| `GetAttributionByMLConfidenceQuery` | Bucketed by confidence tier (< 0.55, 0.55–0.70, 0.70–0.85, > 0.85) |
| `GetAttributionByNewsProximityQuery` | Segmented by minutes-to-nearest-high-impact-event |
| `GetAttributionByMTFConfluenceQuery` | Confluent vs non-confluent signal comparison |
| `GetAttributionBySymbolQuery` | P&L, win rate, avg MAE/MFE per symbol |
| `GetAttributionByDayOfWeekQuery` | Performance by day of week |
| `GetFullAttributionSummaryQuery` | All dimensions in a single composite response |

All queries accept `strategyId` and `dateRange` filters.

#### API

```
POST   /attribution/session            → GetAttributionBySessionQuery
POST   /attribution/regime             → GetAttributionByRegimeQuery
POST   /attribution/ml-confidence      → GetAttributionByMLConfidenceQuery
POST   /attribution/news-proximity     → GetAttributionByNewsProximityQuery
POST   /attribution/mtf-confluence     → GetAttributionByMTFConfluenceQuery
POST   /attribution/symbol             → GetAttributionBySymbolQuery
POST   /attribution/day-of-week        → GetAttributionByDayOfWeekQuery
POST   /attribution/summary            → GetFullAttributionSummaryQuery
```

---

### 5.28 Configuration Hot Reload

**Purpose:** Allow key operational parameters to be updated via API without restarting the engine host. For a live trading system, restarts create gaps in market data ingestion and leave open positions temporarily unmonitored.

#### What Is Hot-Reloadable

Not everything should be hot-reloadable — changing database connection strings or broker credentials mid-flight would be dangerous. Only operational parameters that are safe to change while running are marked `IsHotReloadable = true`:

| Parameter Key | Type | Effect of Change |
|---|---|---|
| `RiskMonitor:IntervalSeconds` | Int | RiskMonitorWorker picks up on next iteration |
| `Trading:SignalExpiryMinutes` | Int | Applied to all new signals immediately |
| `Trading:OrderStatusPollIntervalSeconds` | Int | Applied on next poll cycle |
| `Ensemble:MinConfluentStrategies` | Int | Applied to next signal received |
| `Ensemble:ConfluentWindowSeconds` | Int | Applied to next signal buffer |
| `ML:RequiredShadowTrades` | Int | Applied to new shadow evaluations |
| `ML:DriftAccuracyDropThreshold` | Decimal | Applied on next drift check |
| `NewsFilter:DefaultMinutesBefore` | Int | Applied to next order submission check |
| `NewsFilter:DefaultMinutesAfter` | Int | Applied to next order submission check |

#### Interface

```csharp
public interface IEngineConfigManager
{
    T Get<T>(string key, T defaultValue);
    Task UpdateAsync(string key, string value, CancellationToken ct);
    void RegisterReloadCallback(string key, Action<string> onChanged);
}
```

Workers and handlers call `IEngineConfigManager.Get<T>()` instead of `IConfiguration` for hot-reloadable parameters. When `UpdateEngineConfigCommand` is called, `IEngineConfigManager` updates the `EngineConfig` record in DB, broadcasts `EngineConfigUpdatedIntegrationEvent`, and invokes all registered reload callbacks immediately.

#### Commands

| Command | Description |
|---|---|
| `UpdateEngineConfigCommand` | Update a hot-reloadable config value |
| `ResetEngineConfigCommand` | Reset a config value back to its `appsettings.json` default |

#### Queries

| Query | Description |
|---|---|
| `GetEngineConfigsQuery` | List all config entries with current values, types, and hot-reload status |
| `GetEngineConfigQuery` | Fetch a single config entry by key |

#### API

```
GET    /config                     → GetEngineConfigsQuery
GET    /config/{key}               → GetEngineConfigQuery
PUT    /config/{key}               → UpdateEngineConfigCommand
DELETE /config/{key}               → ResetEngineConfigCommand
```

#### Integration Event

| Event | Publisher | Subscribers |
|---|---|---|
| `EngineConfigUpdatedIntegrationEvent` | `UpdateEngineConfigCommand` handler | All workers (via registered callbacks) |

---

### 5.29 Rate Limiting & API Quota Manager

**Purpose:** Prevent broker API bans by enforcing per-endpoint rate limits centrally. A token bucket per endpoint ensures the engine never exceeds configured quotas, even during reconnect storms, bulk historical data fetches, or high signal-volume periods.

#### Interface

```csharp
public interface IBrokerRateLimiter
{
    // Acquires a token for the given endpoint, waiting if necessary.
    Task AcquireAsync(string broker, string endpoint, CancellationToken ct);

    // Returns current quota status for monitoring.
    RateLimiterStatus GetStatus(string broker, string endpoint);
}

public record RateLimiterStatus(
    string Broker,
    string Endpoint,
    int TokensAvailable,
    int BucketCapacity,
    double RefillRatePerSecond,
    int TotalThrottledRequests);
```

#### Token Bucket Implementation

Each `(broker, endpoint)` pair has an independent token bucket. Tokens refill continuously at the configured rate. If no token is available, the call waits (up to a configurable timeout) rather than throwing:

```json
"RateLimiterConfig": {
  "OANDA": {
    "TickStream":         { "Capacity": 1,   "RefillPerSecond": 1   },
    "HistoricalCandles":  { "Capacity": 5,   "RefillPerSecond": 2   },
    "SubmitOrder":        { "Capacity": 10,  "RefillPerSecond": 5   },
    "OrderStatus":        { "Capacity": 20,  "RefillPerSecond": 10  },
    "ModifyOrder":        { "Capacity": 10,  "RefillPerSecond": 5   },
    "AccountInfo":        { "Capacity": 5,   "RefillPerSecond": 1   }
  },
  "InteractiveBrokers": {
    "SubmitOrder":        { "Capacity": 50,  "RefillPerSecond": 25  },
    "OrderStatus":        { "Capacity": 100, "RefillPerSecond": 50  }
  }
}
```

#### Integration

All broker adapter methods wrap their HTTP/WebSocket calls with `IBrokerRateLimiter.AcquireAsync()`:

```csharp
// In OandaBrokerAdapter:
await _rateLimiter.AcquireAsync("OANDA", "SubmitOrder", ct);
return await _httpClient.PostAsync(...);
```

`IBrokerRateLimiter` is a singleton registered in DI — all adapters and workers share the same limiter instance, ensuring the global quota is respected regardless of how many concurrent calls are in flight.

#### Monitoring

`/health/detailed` (Section 5.24) is extended to include rate limiter status for each endpoint, showing tokens available, throttled request count, and current refill rate. Useful for detecting if the engine is consistently hitting limits and needs quota adjustment.

No new DB entities — all state is in-memory.

---

## 6. API Design

All endpoints follow the existing pattern:
- Base path: `/api/v1/lascodia-trading-engine/`
- All require JWT authentication (`RequireAuthorization("apiScope")`)
- All responses wrapped in `ResponseData<T>`
- Response codes: `"00"` success, `"-11"` validation error, `"-14"` not found

### 6.1 Currency Pairs

```
POST   /currency-pair              → CreateCurrencyPairCommand
PUT    /currency-pair/{id}         → UpdateCurrencyPairCommand
DELETE /currency-pair/{id}         → DeleteCurrencyPairCommand
GET    /currency-pair/{id}         → GetCurrencyPairQuery
POST   /currency-pair/list         → GetPagedCurrencyPairsQuery
```

### 6.2 Market Data

```
GET    /market-data/live/{symbol}           → GetLivePriceQuery
POST   /market-data/candles/{symbol}        → GetCandlesQuery (body: timeframe, from, to)
GET    /market-data/candles/{symbol}/latest → GetLatestCandleQuery?timeframe=H1
```

### 6.3 Strategies

```
POST   /strategy                   → CreateStrategyCommand
DELETE /strategy/{id}              → DeleteStrategyCommand
GET    /strategy/{id}              → GetStrategyQuery
POST   /strategy/list              → GetPagedStrategiesQuery
PUT    /strategy/{id}/activate     → ActivateStrategyCommand
PUT    /strategy/{id}/pause        → PauseStrategyCommand
PUT    /strategy/{id}/risk-profile → AssignRiskProfileCommand
```

### 6.4 Trade Signals

```
GET    /signal/{id}                → GetTradeSignalQuery
POST   /signal/list                → GetPagedTradeSignalsQuery
PUT    /signal/{id}/approve        → ApproveTradeSignalCommand
PUT    /signal/{id}/reject         → RejectTradeSignalCommand
```

### 6.5 Orders

```
POST   /order                      → CreateOrderCommand (manual orders)
PUT    /order/{id}                 → UpdateOrderCommand
DELETE /order/{id}                 → DeleteOrderCommand (soft)
GET    /order/{id}                 → GetOrderQuery
POST   /order/list                 → GetPagedOrdersQuery
PUT    /order/{id}/cancel          → CancelOrderCommand
PUT    /order/{id}/modify          → ModifyOrderCommand (SL/TP)
```

### 6.6 Positions

```
GET    /position/{id}              → GetPositionQuery
POST   /position/list              → GetPagedPositionsQuery
GET    /position/summary           → GetPortfolioSummaryQuery
PUT    /position/{id}/close        → ClosePositionOrderCommand
PUT    /position/{id}/partial-close → PartialClosePositionCommand
```

### 6.7 Risk Profiles

```
POST   /risk-profile               → CreateRiskProfileCommand
PUT    /risk-profile/{id}          → UpdateRiskProfileCommand
DELETE /risk-profile/{id}          → DeleteRiskProfileCommand
GET    /risk-profile/{id}          → GetRiskProfileQuery
POST   /risk-profile/list          → GetPagedRiskProfilesQuery
GET    /risk-profile/summary       → GetRiskSummaryQuery
```

### 6.8 Backtesting

```
POST   /backtest                   → QueueBacktestCommand
DELETE /backtest/{id}              → CancelBacktestCommand
GET    /backtest/{id}              → GetBacktestRunQuery
POST   /backtest/list              → GetPagedBacktestRunsQuery
```

### 6.9 ML Signal Scoring

```
POST   /ml/train                      → TriggerMLTrainingCommand
PUT    /ml/model/{id}/activate        → ActivateMLModelCommand
PUT    /ml/model/{id}/deactivate      → DeactivateMLModelCommand
GET    /ml/model/{id}                 → GetMLModelQuery
POST   /ml/model/list                 → GetPagedMLModelsQuery
GET    /ml/training-run/{id}          → GetMLTrainingRunQuery
POST   /ml/training-run/list          → GetPagedMLTrainingRunsQuery
```

### 6.10 Strategy Feedback & Optimization

```
GET    /strategy-optimization/performance/{strategyId}       → GetStrategyPerformanceQuery
POST   /strategy-optimization/performance/list               → GetPagedStrategyPerformancesQuery
POST   /strategy-optimization/run                            → TriggerOptimizationRunCommand
GET    /strategy-optimization/run/{id}                       → GetOptimizationRunQuery
POST   /strategy-optimization/run/list                       → GetPagedOptimizationRunsQuery
PUT    /strategy-optimization/run/{id}/approve               → ApproveOptimizedParametersCommand
PUT    /strategy-optimization/run/{id}/reject                → RejectOptimizedParametersCommand
```

### 6.11 ML Model Evaluation

```
PUT    /ml/model/{id}/promote              → PromoteMLModelCommand
PUT    /ml/shadow-evaluation/{id}/reject   → RejectMLChallengerCommand
GET    /ml/shadow-evaluation/{id}          → GetMLShadowEvaluationQuery
POST   /ml/shadow-evaluation/list          → GetPagedMLShadowEvaluationsQuery
POST   /ml/model/{id}/prediction-logs      → GetMLModelPredictionLogsQuery
GET    /ml/drift-summary                   → GetMLDriftSummaryQuery
```

### 6.12 Economic Calendar

```
POST   /economic-calendar/sync          → SyncEconomicCalendarCommand
POST   /economic-calendar               → CreateEconomicEventCommand
PUT    /economic-calendar/{id}          → UpdateEconomicEventCommand
DELETE /economic-calendar/{id}          → DeleteEconomicEventCommand
POST   /economic-calendar/upcoming      → GetUpcomingEconomicEventsQuery
POST   /economic-calendar/list          → GetPagedEconomicEventsQuery
GET    /economic-calendar/news-active/{symbol} → IsNewsWindowActiveQuery
```

### 6.13 Market Regime

```
GET    /market-regime/{symbol}          → GetCurrentRegimeQuery?timeframe=H1
POST   /market-regime/history           → GetPagedRegimeHistoryQuery
```

### 6.14 Portfolio Risk

```
GET    /portfolio/correlation           → GetPortfolioCorrelationQuery
GET    /portfolio/currency-exposure     → GetCurrencyExposureQuery
```

### 6.15 Walk-Forward Optimization

```
POST   /walk-forward                    → QueueWalkForwardRunCommand
DELETE /walk-forward/{id}               → CancelWalkForwardRunCommand
GET    /walk-forward/{id}               → GetWalkForwardRunQuery
POST   /walk-forward/list               → GetPagedWalkForwardRunsQuery
```

### 6.16 Execution Quality

```
GET    /execution-quality/{orderId}     → GetExecutionQualityQuery
POST   /execution-quality/summary       → GetExecutionQualitySummaryQuery
```

### 6.17 Strategy Ensemble

```
POST   /ensemble/rebalance              → TriggerAllocationRebalanceCommand
PUT    /ensemble/allocation/{strategyId} → SetStrategyAllocationCommand
GET    /ensemble/allocations            → GetStrategyAllocationsQuery
GET    /ensemble/status                 → GetEnsembleStatusQuery
```

### 6.18 Audit Trail

```
POST   /audit/decisions/list                  → GetDecisionLogsQuery
GET    /audit/decisions/signal/{signalId}     → GetSignalDecisionSummaryQuery
```

### 6.19 Sentiment

```
GET    /sentiment/latest/{currency}           → GetLatestSentimentQuery
POST   /sentiment/cot/list                    → GetPagedCOTReportsQuery
POST   /sentiment/history                     → GetSentimentHistoryQuery
```

### 6.20 System Health

```
GET    /health/detailed                       → SystemHealthReport (authenticated)
GET    /health                                → Basic ASP.NET health check (unauthenticated)
```

### 6.21 Trailing Stop & Position Scaling

```
PUT    /position/{id}/trailing-stop         → AddTrailingStopCommand
PATCH  /position/{id}/trailing-stop         → ModifyTrailingStopCommand
POST   /position/{id}/scale-order           → AddScaleOrderCommand
DELETE /position/{id}/scale-order/{scaleId} → CancelScaleOrderCommand
GET    /position/{id}/scale-orders          → GetPositionScaleOrdersQuery
```

### 6.22 Performance Attribution

```
POST   /attribution/session            → GetAttributionBySessionQuery
POST   /attribution/regime             → GetAttributionByRegimeQuery
POST   /attribution/ml-confidence      → GetAttributionByMLConfidenceQuery
POST   /attribution/news-proximity     → GetAttributionByNewsProximityQuery
POST   /attribution/mtf-confluence     → GetAttributionByMTFConfluenceQuery
POST   /attribution/symbol             → GetAttributionBySymbolQuery
POST   /attribution/day-of-week        → GetAttributionByDayOfWeekQuery
POST   /attribution/summary            → GetFullAttributionSummaryQuery
```

### 6.23 Configuration

```
GET    /config                         → GetEngineConfigsQuery
GET    /config/{key}                   → GetEngineConfigQuery
PUT    /config/{key}                   → UpdateEngineConfigCommand
DELETE /config/{key}                   → ResetEngineConfigCommand
```

### 6.24 Alerts

```
POST   /alert                      → CreateAlertCommand
PUT    /alert/{id}                 → UpdateAlertCommand
DELETE /alert/{id}                 → DeleteAlertCommand
GET    /alert/{id}                 → GetAlertQuery
POST   /alert/list                 → GetPagedAlertsQuery
```

---

## 7. External Integrations

### 7.1 Broker Adapters

Each broker is implemented as a concrete class behind `IBrokerDataFeed` and `IBrokerOrderExecutor`. The active broker is selected via `appsettings.json`:

```json
"BrokerConfig": {
  "Provider": "OANDA",
  "ApiKey": "...",
  "AccountId": "...",
  "Environment": "practice"
}
```

**v1 Broker Targets:**

| Broker | Protocol | Notes |
|---|---|---|
| OANDA | REST + WebSocket | v20 API; practice + live accounts |
| MetaTrader 5 | ZeroMQ bridge or REST | Requires MT5 EA/script on broker side |
| Interactive Brokers | TWS API (IBApi) | Via IB Gateway |

**Adapter contract:**

```csharp
// Infrastructure/BrokerAdapters/
OandaBrokerAdapter : IBrokerDataFeed, IBrokerOrderExecutor
MetaTrader5BrokerAdapter : IBrokerDataFeed, IBrokerOrderExecutor
InteractiveBrokersBrokerAdapter : IBrokerDataFeed, IBrokerOrderExecutor
```

DI registration selects the adapter based on `BrokerConfig:Provider`.

### 7.2 Notification Channels

```json
"NotificationConfig": {
  "Smtp": {
    "Host": "smtp.gmail.com",
    "Port": 587,
    "Username": "...",
    "Password": "..."
  },
  "Telegram": {
    "BotToken": "...",
    "DefaultChatId": "..."
  }
}
```

---

## 8. Data Persistence

### 8.1 Database Schema (new tables)

All new entities follow the existing `OrderConfiguration` pattern — `IEntityTypeConfiguration<T>` classes in `Infrastructure/Persistence/Configurations/`.

| Table | Key Indexes |
|---|---|
| `CurrencyPairs` | (Symbol) UNIQUE |
| `Candles` | (Symbol, Timeframe, Timestamp) UNIQUE |
| `Strategies` | (Status) |
| `TradeSignals` | (StrategyId, Status, GeneratedAt) |
| `Positions` | (Symbol, Status) |
| `RiskProfiles` | (IsDefault) |
| `BacktestRuns` | (StrategyId, Status) |
| `Alerts` | (AlertType, IsActive) |
| `MLModels` | (Symbol, Timeframe, IsActive); unique index on (Symbol, Timeframe) WHERE IsActive = true |
| `MLTrainingRuns` | (Symbol, Timeframe, Status, StartedAt) |
| `StrategyPerformanceSnapshots` | (StrategyId, EvaluatedAt); index on (HealthStatus) |
| `OptimizationRuns` | (StrategyId, Status, TriggerType) |
| `OutcomeLabelledSamples` | (Symbol, Timeframe, CapturedAt); index on (TradeSignalId) |
| `MLModelPredictionLogs` | (MLModelId, ModelRole, PredictedAt); index on (TradeSignalId) |
| `MLShadowEvaluations` | (ChallengerModelId, Status); index on (Symbol, Timeframe, Status) |
| `EconomicEvents` | (Currency, Impact, ScheduledAt); index on (ScheduledAt) |
| `MarketRegimeSnapshots` | (Symbol, Timeframe, DetectedAt); index on (Symbol, Timeframe) |
| `ExecutionQualityLogs` | (OrderId); index on (Symbol, Session, RecordedAt) |
| `StrategyAllocations` | (StrategyId) UNIQUE |
| `WalkForwardRuns` | (StrategyId, Status) |
| `DecisionLogs` | (EntityType, EntityId, CreatedAt); index on (DecisionType, Outcome, CreatedAt) — append-only, no soft delete |
| `SentimentSnapshots` | (Currency, Source, CapturedAt) |
| `COTReports` | (Currency, ReportDate) UNIQUE |
| `PositionScaleOrders` | (PositionId, ScaleType, ScaleStep); index on (Status) |
| `EngineConfigs` | (Key) UNIQUE |

### 8.2 Read vs Write Separation

| Context | Used By |
|---|---|
| `WriteApplicationDbContext` | All Command handlers; Workers |
| `ReadApplicationDbContext` | All Query handlers |

The `Candle` table will grow large. A future optimization is a dedicated time-series DB (e.g., TimescaleDB extension or InfluxDB) for candle data, behind the same `IBrokerDataFeed` interface.

### 8.3 Migrations

Each batch of new entities requires a migration:
```bash
dotnet ef migrations add AddMarketDataEntities \
  --project LascodiaTradingEngine.Infrastructure/ \
  --startup-project LascodiaTradingEngine.API/
```

---

## 9. Event Architecture

### 9.1 Integration Events

All events extend `IntegrationEvent` from `EventBus`. Events are published via `IIntegrationEventService.SaveAndPublish()` which ensures outbox pattern reliability.

| Event | Publisher | Subscribers |
|---|---|---|
| `PriceUpdatedIntegrationEvent` | `MarketDataWorker` | `StrategyWorker`, `RiskMonitorWorker`, Alert handler |
| `TradeSignalCreatedIntegrationEvent` | `StrategyWorker` | `OrderExecutionWorker`, Alert handler |
| `SignalRejectedIntegrationEvent` | `OrderExecutionWorker` | Alert handler, audit log |
| `OrderSubmittedIntegrationEvent` | `OrderExecutionWorker` | Audit log |
| `OrderFilledIntegrationEvent` | `OrderExecutionWorker` | `PositionManager`, Alert handler |
| `OrderRejectedIntegrationEvent` | `OrderExecutionWorker` | Alert handler |
| `PositionOpenedIntegrationEvent` | Position handler | Alert handler |
| `PositionClosedIntegrationEvent` | `OrderExecutionWorker` | Alert handler, P&L aggregator |
| `DrawdownBreachedIntegrationEvent` | `RiskMonitorWorker` | `StrategyWorker` (pause), Alert handler |
| `BacktestCompletedIntegrationEvent` | Backtest worker | Alert handler |
| `MLTrainingQueuedIntegrationEvent` | `TriggerMLTrainingCommand` handler | `MLRetrainingWorker` |
| `MLTrainingCompletedIntegrationEvent` | `MLRetrainingWorker` | Alert handler, `IMLModelLoader` (hot-reload) |
| `MLModelActivatedIntegrationEvent` | `ActivateMLModelCommand` handler | `IMLModelLoader` (hot-reload) |
| `StrategyHealthDegradedIntegrationEvent` | `StrategyEvaluationWorker` | Alert handler |
| `StrategyAutoPausedIntegrationEvent` | `StrategyEvaluationWorker` | Alert handler |
| `OptimizationRunQueuedIntegrationEvent` | `StrategyEvaluationWorker` / scheduler | Optimization worker |
| `OptimizationRunCompletedIntegrationEvent` | Optimization worker | Alert handler |
| `MLShadowEvaluationCompletedIntegrationEvent` | `MLShadowEvaluationWorker` | Alert handler, `IMLModelLoader` (if promoted) |
| `MLDriftDetectedIntegrationEvent` | `MLDriftMonitorWorker` | Alert handler, `MLRetrainingWorker` (emergency retrain) |
| `MLChallengerPromotedIntegrationEvent` | `MLShadowEvaluationWorker` / `PromoteMLModelCommand` | Alert handler |
| `MLChallengerRejectedIntegrationEvent` | `MLShadowEvaluationWorker` / `RejectMLChallengerCommand` | Alert handler |
| `EconomicCalendarSyncedIntegrationEvent` | `EconomicCalendarSyncWorker` | Alert handler |
| `NewsWindowBlockedIntegrationEvent` | `OrderExecutionWorker` | Alert handler, audit log |
| `MarketRegimeChangedIntegrationEvent` | `MarketRegimeWorker` | `StrategyWorker` (reload cached regime), Alert handler |
| `AllocationRebalancedIntegrationEvent` | `AllocationRebalanceWorker` | Alert handler |
| `WalkForwardRunCompletedIntegrationEvent` | Walk-forward worker | Alert handler |
| `DrawdownRecoveryActivatedIntegrationEvent` | `RiskMonitorWorker` | Alert handler, `OrderExecutionWorker` (apply lot multiplier) |
| `DrawdownRecoveryDeactivatedIntegrationEvent` | `RiskMonitorWorker` | Alert handler, `OrderExecutionWorker` (restore lot size) |
| `SentimentUpdatedIntegrationEvent` | `SentimentWorker` | `StrategyWorker` (refresh cached sentiment) |
| `BrokerFailoverActivatedIntegrationEvent` | `IBrokerFailoverManager` | Alert handler, `DecisionLogger` |
| `BrokerFailbackCompletedIntegrationEvent` | `IBrokerFailoverManager` | Alert handler |
| `TrailingStopMovedIntegrationEvent` | `TrailingStopWorker` | Audit log, Alert handler |
| `ScaleOrderTriggeredIntegrationEvent` | `TrailingStopWorker` | Audit log, Alert handler |
| `EngineConfigUpdatedIntegrationEvent` | `UpdateEngineConfigCommand` handler | All workers (reload callbacks) |

### 9.2 Event Payloads (examples)

```csharp
public record PriceUpdatedIntegrationEvent(
    string Symbol,
    string Timeframe,
    decimal Bid,
    decimal Ask,
    DateTime Timestamp,
    bool CandleClosed) : IntegrationEvent;

public record OrderFilledIntegrationEvent(
    long OrderId,
    string Symbol,
    string Direction,
    decimal FilledPrice,
    decimal FilledLots,
    decimal? StopLoss,
    decimal? TakeProfit,
    DateTime FilledAt) : IntegrationEvent;

public record DrawdownBreachedIntegrationEvent(
    string BreachType,          // "Daily" | "Total"
    decimal CurrentDrawdownPct,
    decimal LimitPct,
    decimal CurrentEquity) : IntegrationEvent;
```

---

## 10. Implementation Phases

### Phase 1 — Foundation & Market Data (Weeks 1–2)

**Goal:** Establish the data pipeline. By end of this phase, the system can connect to a broker and store live candles.

- [ ] Domain: Add `CurrencyPair`, `Candle` entities
- [ ] Application: CRUD commands/queries for `CurrencyPair`; `IngestCandleCommand`, `GetCandlesQuery`
- [ ] Infrastructure: EF configurations; migrations
- [ ] Infrastructure: `IBrokerDataFeed` interface + OANDA adapter (WebSocket tick feed + REST historical candles)
- [ ] Infrastructure: `ILivePriceCache` (in-memory, `IMemoryCache` backed)
- [ ] Background: `MarketDataWorker` — connect, aggregate candles, publish `PriceUpdatedIntegrationEvent`
- [ ] API: `CurrencyPairController`, `MarketDataController`
- [ ] Tests: Unit tests for candle aggregation logic; integration test for OANDA adapter against sandbox

---

### Phase 2 — Strategy Engine & Signals (Weeks 3–4)

**Goal:** Strategies can be configured and evaluated against live candle data, generating signals.

- [ ] Domain: Add `Strategy`, `TradeSignal` entities
- [ ] Application: CRUD for `Strategy`; signal commands/queries; `IStrategyEvaluator` interface
- [ ] Application: `MovingAverageCrossoverEvaluator`, `RSIReversionEvaluator`, `BreakoutScalperEvaluator`
- [ ] Application: Technical indicator helpers (SMA, EMA, RSI, ATR)
- [ ] Background: `StrategyWorker` — evaluate strategies on `PriceUpdatedIntegrationEvent`
- [ ] API: `StrategyController`, `SignalController`
- [ ] Tests: Unit tests for each evaluator with fixture candle data; validator tests for strategy commands

---

### Phase 3 — Order Execution & Positions (Weeks 5–7)

**Goal:** Signals flow through to real broker orders; positions are tracked in real time.

- [ ] Domain: Extend `Order` entity (new fields); add `Position` entity
- [ ] Application: `SubmitOrderCommand`, `UpdateOrderStatusCommand`, `CancelOrderCommand`, `ModifyOrderCommand`; position commands/queries; `IBrokerOrderExecutor` interface; `GetPortfolioSummaryQuery`
- [ ] Infrastructure: OANDA order execution adapter; order status polling
- [ ] Background: `OrderExecutionWorker` — execute signals, reconcile order status, update positions
- [ ] API: Extend `OrderController`; add `PositionController`
- [ ] Tests: Handler tests for order submission; mock broker executor; position P&L calculation tests

---

### Phase 4 — Risk Management (Week 8)

**Goal:** All orders pass risk checks; drawdown monitoring is active.

- [ ] Domain: Add `RiskProfile` entity
- [ ] Application: CRUD for `RiskProfile`; `IRiskChecker` interface + implementation; `GetRiskSummaryQuery`
- [ ] Background: `RiskMonitorWorker` — monitor drawdown, pause strategies on breach
- [ ] Integration: Wire `IRiskChecker` into `OrderExecutionWorker` pre-trade flow
- [ ] API: `RiskProfileController`
- [ ] Tests: Risk checker unit tests for each rule; integration test for drawdown breach → strategy pause flow

---

### Phase 5 — Backtesting (Weeks 9–10)

**Goal:** Strategies can be validated offline against historical data before going live.

- [ ] Domain: Add `BacktestRun` entity
- [ ] Application: `QueueBacktestCommand`, `GetBacktestRunQuery`, `GetPagedBacktestRunsQuery`; `IBacktestEngine` interface + implementation
- [ ] Infrastructure: Historical candle seeder (bulk-load from broker REST API)
- [ ] Background: Backtest execution worker (queue-based, can run multiple concurrently)
- [ ] API: `BacktestController`
- [ ] Tests: Backtest engine tests with synthetic candle sequences; verify win rate, drawdown calculations

---

### Phase 6 — Notifications & Alerts (Week 11)

**Goal:** Traders receive real-time notifications on key events.

- [ ] Domain: Add `Alert` entity
- [ ] Application: CRUD for `Alert`; `IAlertDispatcher` interface
- [ ] Infrastructure: `EmailAlertDispatcher`, `WebhookAlertDispatcher`, `TelegramAlertDispatcher`
- [ ] Integration: Alert event handler subscribes to all key integration events
- [ ] API: `AlertController`
- [ ] Tests: Alert dispatcher tests (mock HTTP clients); alert trigger tests per event type

---

### Phase 7 — ML Signal Scoring (Weeks 12–14)

**Goal:** Rule-based signals are enriched with ML-predicted direction, magnitude, and confidence before execution.

- [ ] Domain: Add `MLModel`, `MLTrainingRun` entities; add ML score fields to `TradeSignal`
- [ ] Application: `TriggerMLTrainingCommand`, `ActivateMLModelCommand`, `DeactivateMLModelCommand`; ML queries; `IMLSignalScorer` and `IMLModelLoader` interfaces; feature engineering pipeline
- [ ] Application: ML.NET training pipeline (FastTree direction classifier + FastForest magnitude regressor)
- [ ] Infrastructure: `MLSignalScorer` implementation (loads `.mlnet` file, runs `PredictionEngine<>`); `MLModelLoader` with hot-reload support; `MLRetrainingWorker` with scheduled cron + manual trigger
- [ ] Infrastructure: EF configurations for `MLModel`, `MLTrainingRun`; migrations
- [ ] Integration: Wire `IMLSignalScorer.ScoreAsync()` into `StrategyWorker` after evaluator returns signal
- [ ] Integration: ML gating logic in `OrderExecutionWorker` (respect `MLGatingEnabled`, `MLMinConfidence`, `MLDirectionMustMatch` from strategy `ParametersJson`)
- [ ] API: `MLController`
- [ ] Configuration: `ModelStoragePath` in `appsettings.json`; retraining cron schedule
- [ ] Tests: Feature extraction unit tests with fixture candles; scorer tests with pre-trained stub model; retraining worker tests asserting model file written and `MLModel` record created

---

### Phase 8 — Strategy Feedback & Optimization (Weeks 15–17)

**Goal:** Live trade outcomes continuously score strategy health; underperforming strategies are auto-paused and Bayesian optimization proposes improved parameters.

- [ ] Domain: Add `StrategyPerformanceSnapshot`, `OptimizationRun`, `OutcomeLabelledSample` entities
- [ ] Application: `IHealthScoreCalculator` interface + implementation (weighted composite formula, configurable weights); `IStrategyOptimizer` interface + Bayesian optimization implementation (Gaussian Process surrogate + UCB acquisition); parameter search space registry per `StrategyType`
- [ ] Application: `TriggerOptimizationRunCommand`, `ApproveOptimizedParametersCommand`, `RejectOptimizedParametersCommand`; performance and optimization run queries
- [ ] Application: `OutcomeLabelledSample` persistence logic; ML feedback blending weight applied in `MLRetrainingWorker`
- [ ] Infrastructure: EF configurations for new entities; migrations; `StrategyEvaluationWorker` (event-driven + weekly cron); optimization run worker
- [ ] Integration: Tiered auto-response wired into `StrategyEvaluationWorker` (flag → pause → queue optimization)
- [ ] Integration: `ApproveOptimizedParametersCommand` applies `BestParametersJson` to `Strategy.ParametersJson` and re-activates
- [ ] API: `StrategyOptimizationController`
- [ ] Configuration: `HealthScoreThresholds`, `OptimizationIterations`, `EvaluationWindowTrades` in `appsettings.json`
- [ ] Tests: `IHealthScoreCalculator` tests with known P&L sequences; optimizer tests asserting it improves over baseline on synthetic candle data; tiered response tests (critical score → strategy paused + optimization queued)

---

### Phase 9 — ML Model Evaluation & Continuous Improvement (Weeks 18–20)

**Goal:** Newly trained models enter shadow mode before promotion; drift is detected automatically; hyperparameters are tuned at training time.

- [ ] Domain: Add `MLModelPredictionLog`, `MLShadowEvaluation` entities; add `HyperparametersJson`, `ActivationAccuracy` fields to `MLModel`
- [ ] Application: `PromoteMLModelCommand`, `RejectMLChallengerCommand`; shadow evaluation and prediction log queries; `IMLShadowEvaluator`, `IMLDriftDetector`, `IMLHyperparameterTuner` interfaces
- [ ] Application: Promotion criteria evaluator (direction accuracy delta, magnitude correlation, Brier score comparison)
- [ ] Application: Drift detection logic (rolling accuracy window, consecutive error streak, Brier score delta)
- [ ] Application: Hyperparameter grid search with 3-fold cross-validation wired into `MLTrainingPipeline`
- [ ] Infrastructure: `MLShadowEvaluationWorker` — dual-model scoring on signals, outcome resolution on position close, promotion decision; EF configurations and migrations
- [ ] Infrastructure: `MLDriftMonitorWorker` — hourly rolling accuracy check, emergency retraining trigger
- [ ] Integration: `MLRetrainingWorker` updated to create `MLShadowEvaluation` on completion instead of auto-activating; `ActivateMLModelCommand` sets `ActivationAccuracy` as drift baseline
- [ ] API: Extend `MLController` with shadow evaluation and drift endpoints
- [ ] Configuration: `RequiredShadowTrades`, `DriftAccuracyDropThreshold`, `DriftBrierScoreDelta`, `DriftConsecutiveErrorLimit`, `HyperparameterGridConfig` in `MLConfig`
- [ ] Tests: Shadow evaluator tests — assert champion used for gating, challenger only logged; promotion criteria tests for all three pass/partial/fail cases; drift detector tests with controlled accuracy sequences; hyperparameter tuner tests asserting best config selected

---

### Phase 10 — Signal Intelligence: MTF, News Filter & Regime Detection (Weeks 21–23)

**Goal:** Signals are filtered by higher-timeframe trend, blocked around news events, and suppressed when market regime is unsuitable.

- [ ] Domain: Add `EconomicEvent`, `MarketRegimeSnapshot` entities; add `MTFConfluence`, `HigherTimeframeTrend`, `RegimeAtSignalTime` fields to `TradeSignal`
- [ ] Application: `IMultiTimeframeFilter`, `ISessionFilter`, `INewsFilter`, `IRegimeClassifier` interfaces and implementations; `SyncEconomicCalendarCommand`, economic calendar CRUD and queries; regime queries
- [ ] Infrastructure: `EconomicCalendarSyncWorker` (daily sync from ForexFactory/Investing.com); `MarketRegimeWorker` (regime detection on candle close); EF configurations and migrations
- [ ] Integration: Wire `IMultiTimeframeFilter` and `ISessionFilter` into `StrategyWorker` pre-evaluation; wire `INewsFilter` and `IRegimeClassifier` check into `OrderExecutionWorker` pre-submission
- [ ] API: `EconomicCalendarController`, `MarketRegimeController`
- [ ] Configuration: `NewsFilter`, `AllowedSessions`, `OptimalRegimes`, `HigherTimeframe` all in strategy `ParametersJson`; `RegimeConfig` (ADX/ATR thresholds) in `appsettings.json`
- [ ] Tests: MTF filter tests (bullish/bearish/neutral higher TF); news filter tests (inside/outside blackout window); regime classifier tests with known ADX/ATR fixture candles; session filter tests across UTC boundaries

---

### Phase 11 — Portfolio Risk, Execution Quality & Walk-Forward (Weeks 24–26)

**Goal:** Portfolio-level correlation risk enforced; execution quality tracked; walk-forward optimization available as a more robust alternative to single backtests.

- [ ] Domain: Add `ExecutionQualityLog`, `WalkForwardRun` entities; add `SlippagePips` to `Order`; add `MaxCorrelatedExposurePct`, `MaxSingleCurrencyExposurePct` to `RiskProfile`
- [ ] Application: `ICorrelationRiskChecker` interface + Pearson correlation implementation; `GetPortfolioCorrelationQuery`, `GetCurrencyExposureQuery`; `IWalkForwardEngine` interface + implementation; walk-forward commands and queries; `GetExecutionQualitySummaryQuery`
- [ ] Infrastructure: `OrderExecutionWorker` updated to populate `ExecutionQualityLog` and `Order.SlippagePips` on fill; `IBacktestEngine` updated to apply historical slippage model when `SlippageModel = "Historical"`; EF configurations and migrations
- [ ] Integration: `ICorrelationRiskChecker` wired into `IRiskChecker` pipeline as an additional pre-trade check; `IWalkForwardEngine` wired into `OptimizationRunWorker` when `WalkForwardEnabled = true`
- [ ] API: `PortfolioRiskController`, `ExecutionQualityController`, `WalkForwardController`
- [ ] Tests: Correlation risk checker tests (correlated pairs blocked, uncorrelated allowed); execution quality log tests asserting correct slippage calculation; walk-forward engine tests asserting OOS windows non-overlapping and average score computed correctly

---

### Phase 12 — Ensemble & Capital Allocation (Weeks 27–28)

**Goal:** Capital is dynamically allocated across strategies by rolling Sharpe ratio; optional confluence mode requires multiple strategies to agree before execution.

- [ ] Domain: Add `StrategyAllocation` entity
- [ ] Application: `IEnsembleAllocator` interface + Sharpe-weighted implementation; `TriggerAllocationRebalanceCommand`, `SetStrategyAllocationCommand`; ensemble queries; confluence buffer logic in `OrderExecutionWorker`
- [ ] Infrastructure: `AllocationRebalanceWorker` (weekly cron + manual trigger); EF configuration and migration for `StrategyAllocation`
- [ ] Integration: `OrderExecutionWorker` loads `StrategyAllocation.Weight` and applies it to lot size before submission; confluence buffer holds signals for `ConfluentWindowSeconds` before checking agreement count
- [ ] API: `EnsembleController`
- [ ] Configuration: `EnsembleConfig` (Mode, MinConfluentStrategies, ConfluentWindowSeconds), `AllocationMultiplier` in `appsettings.json`
- [ ] Tests: Allocator tests — negative Sharpe → zero weight; rebalance tests asserting weights sum to 1.0; confluence buffer tests — single signal not executed, two agreeing signals executed, conflicting signals discarded

---

### Phase 13 — Operational Resilience: Paper Trading, Audit Trail, Drawdown Recovery & Failover (Weeks 29–31)

**Goal:** Engine is safe to run with real capital — paper mode validates before go-live, all decisions are auditable, capital is protected by graduated recovery, and broker failure is handled automatically.

- [ ] Domain: Add `DecisionLog` entity; add `IsPaper` to `Order` and `Position`; add recovery fields to `RiskProfile`
- [ ] Application: `IDecisionLogger` interface + async fire-and-forget implementation; `GetDecisionLogsQuery`, `GetSignalDecisionSummaryQuery`; `IsPaper` filter on all existing list queries
- [ ] Application: `IPaperBrokerOrderExecutor` — fills at live mid-price + simulated slippage; pending limit/stop order tracker
- [ ] Infrastructure: `IBrokerFailoverManager` implementation; failover/failback logic in `MarketDataWorker` and `OrderExecutionWorker`; EF configuration for `DecisionLog` (no soft delete, append-only)
- [ ] Integration: `IDecisionLogger` injected into every worker and handler that makes a consequential decision; `RiskMonitorWorker` extended with recovery mode state machine; `OrderExecutionWorker` applies `RecoveryLotSizeMultiplier` when recovery active
- [ ] API: `AuditController`; `/health/detailed` endpoint via `ISystemHealthAggregator`
- [ ] Configuration: `FailoverConfig` in `BrokerConfig`; `DrawdownRecovery` fields in `RiskProfile` seed data
- [ ] Tests: Paper executor tests — market order fills at mid-price; limit order triggers on price crossing; decision logger tests asserting all rejection paths write a log entry; recovery mode state machine tests (activate → reduced lots → deactivate → normal lots); failover tests asserting secondary adapter activated after N failed reconnects

---

### Phase 14 — Sentiment Data Integration (Weeks 32–33)

**Goal:** COT institutional positioning and news NLP sentiment enrich ML features and optionally act as macro filters.

- [ ] Domain: Add `SentimentSnapshot`, `COTReport` entities
- [ ] Application: `ISentimentDataFeed`, `ICOTDataFeed` interfaces; sentiment queries; `FeatureExtractor` extended with COT and news NLP fields; optional `SentimentFilter` wired into `StrategyWorker`
- [ ] Infrastructure: CFTC COT data adapter (parses weekly CSV from CFTC website); news NLP adapter (FinBERT or lightweight ML.NET text classifier); `SentimentWorker` (weekly COT sync + hourly news); EF configurations and migrations
- [ ] Integration: `SentimentUpdatedIntegrationEvent` consumed by `StrategyWorker` to refresh cached sentiment scores; sentiment features included in `SignalFeatureVector` for next ML retraining run
- [ ] API: `SentimentController`
- [ ] Tests: COT score normalisation tests; sentiment filter tests (buy blocked when COT bearish beyond threshold); feature extractor tests asserting sentiment fields populated

---

### Phase 15 — Trailing Stop, Attribution, Hot Reload & Rate Limiting (Weeks 34–36)

**Goal:** Position management is dynamic and profit-protecting; performance insights are queryable across all contextual dimensions; config changes don't require restarts; broker API quotas are never breached.

- [ ] Domain: Add `PositionScaleOrder`, `EngineConfig` entities; add trailing stop fields to `Order` and `Position`
- [ ] Application: `ITrailingStopManager` interface + FixedPips/ATR/Percentage implementations; `AddTrailingStopCommand`, `ModifyTrailingStopCommand`, `AddScaleOrderCommand`, `CancelScaleOrderCommand`, `GetPositionScaleOrdersQuery`; all attribution queries; `IEngineConfigManager` interface + implementation with reload callbacks; config commands and queries; `IBrokerRateLimiter` interface + token bucket implementation
- [ ] Infrastructure: `TrailingStopWorker` — subscribes to `PriceUpdatedIntegrationEvent`, computes new stops, triggers scale orders; EF configurations and migrations for `PositionScaleOrder` and `EngineConfig`; `RateLimiterConfig` binding and `IBrokerRateLimiter` singleton registration; all broker adapters updated to call `AcquireAsync()` before each API call
- [ ] Integration: `OpenPositionCommand` handler creates `PositionScaleOrder` records from strategy `ParametersJson`; `IEngineConfigManager` injected into all workers replacing `IConfiguration` for hot-reloadable keys; `/health/detailed` extended with rate limiter status per endpoint
- [ ] API: Extend `PositionController` with trailing stop and scale order endpoints; `AttributionController`; `ConfigController`
- [ ] Configuration: `RateLimiterConfig` per broker/endpoint in `appsettings.json`; `EngineConfig` seed data for all hot-reloadable keys with defaults
- [ ] Tests: `ITrailingStopManager` tests for each trail type with fixture price sequences; scale order trigger tests (price reaches level → order created); attribution query tests with known P&L fixture data asserting correct grouping; `IEngineConfigManager` tests asserting reload callbacks fire on update; token bucket tests asserting throttling under burst load

---

### Phase 16 — Hardening & Additional Brokers (Weeks 37–38)

**Goal:** Production readiness, additional broker support.

- [ ] MT5 broker adapter (via ZeroMQ bridge)
- [ ] Interactive Brokers adapter (TWS API)
- [ ] Reconnect / failover logic in `MarketDataWorker` and `OrderExecutionWorker`
- [ ] Rate limit handling in broker adapters (retry with exponential backoff)
- [ ] Performance: candle table partitioning or TimescaleDB migration for high-volume symbols
- [ ] Health check endpoints per subsystem (market data feed, order execution, risk monitor)
- [ ] Structured logging with correlation IDs across worker pipelines

---

## 11. Testing Strategy

### Unit Tests (per feature — in `LascodiaTradingEngine.UnitTest`)

Follow the existing `CreateOrderCommandTest` pattern:

- **Handler tests**: Mock `IWriteApplicationDbContext` or `IReadApplicationDbContext` using `MockQueryable.Moq`; assert correct entity state changes and response codes
- **Validator tests**: Assert valid input passes; assert each invalid condition triggers the correct error message
- **Evaluator tests**: Supply deterministic candle fixture lists; assert signal direction, entry, SL, TP
- **Risk checker tests**: Parameterized tests covering each rule boundary (at limit = pass; over limit = fail)
- **Backtest engine tests**: Synthetic candle sequences with known outcomes; verify P&L, win rate, max drawdown

### Integration Tests (new project: `LascodiaTradingEngine.IntegrationTest`)

- **Broker adapter tests**: Connect to broker sandbox/paper accounts; verify tick ingestion and order submission round-trips
- **Worker pipeline tests**: Publish `PriceUpdatedIntegrationEvent` → assert `TradeSignal` created by `StrategyWorker` (in-memory event bus)
- **Risk enforcement tests**: Full flow — signal → risk check failure → no order created
- **End-to-end backtest**: Queue a backtest run → assert result computed and persisted

### Test Conventions

- xUnit test classes named `<Subject>Test` (e.g., `MovingAverageCrossoverEvaluatorTest`)
- Use `MockQueryable.Moq` for `IQueryable<T>` mocking
- Use `Moq` for all interface mocks
- Arrange / Act / Assert sections separated by blank lines
- No `Thread.Sleep` — use `CancellationToken` with `Task.CompletedTask` in async worker tests

---

## 12. Non-Functional Requirements

| Requirement | Target |
|---|---|
| Market data latency | < 500ms from tick received to candle updated in DB |
| Signal generation latency | < 2s from candle close to signal published |
| Order submission latency | < 1s from signal approved to order submitted to broker |
| API response time (p99) | < 300ms for all read queries |
| Uptime | 99.5% during forex market hours (Sun 17:00 – Fri 17:00 ET) |
| Database scaling | Candle table partitioned by (Symbol, Timeframe, Year); target 100M+ rows |
| Security | JWT authentication on all endpoints; broker API keys stored in environment variables / secrets manager; no secrets in appsettings |
| Observability | Structured JSON logs with correlation IDs; health check endpoint per subsystem; PerformanceBehaviour logs slow queries (> 3s) |
| Resilience | Worker reconnect on broker disconnect (exponential backoff, max 5 retries); order status polling as fallback if webhook missed |

---

## Appendix A — appsettings.json Extensions

```json
{
  "ConnectionStrings": {
    "WriteDbConnection": "Server=.;Database=LascodiaTradingEngineDb;...",
    "ReadDbConnection": "Server=.;Database=LascodiaTradingEngineDb;..."
  },
  "BrokerType": "rabbitmq",
  "RabbitMQConfig": {
    "Host": "localhost",
    "Username": "guest",
    "Password": "guest",
    "QueueName": "lascodia-trading-engine-queue"
  },
  "BrokerConfig": {
    "Primary": {
      "Provider": "OANDA",
      "ApiKey": "",
      "AccountId": "",
      "Environment": "practice"
    },
    "Secondary": {
      "Provider": "InteractiveBrokers",
      "ApiKey": "",
      "AccountId": "",
      "Environment": "paper"
    },
    "FailoverConfig": {
      "MaxReconnectAttempts": 5,
      "ReconnectBackoffSeconds": [1, 2, 4, 8, 16],
      "FailoverTriggerAfterAttempts": 3,
      "AutoFailbackEnabled": true,
      "FailbackCheckIntervalMinutes": 5
    }
  },
  "NotificationConfig": {
    "Smtp": {
      "Host": "smtp.gmail.com",
      "Port": 587,
      "Username": "",
      "Password": ""
    },
    "Telegram": {
      "BotToken": "",
      "DefaultChatId": ""
    }
  },
  "TradingConfig": {
    "CandleAggregationTimeframes": ["M1", "M5", "M15", "H1", "H4", "D1"],
    "SignalExpiryMinutes": 5,
    "OrderStatusPollIntervalSeconds": 5,
    "RiskMonitorIntervalSeconds": 60
  },
  "MLConfig": {
    "ModelStoragePath": "/var/lascodia/ml-models",
    "RetrainingCronSchedule": "0 0 * * 0",
    "TrainingWindowDays": 180,
    "ValidationSplitPct": 20,
    "MinDirectionAccuracyToActivate": 55.0,
    "FeatureLookbackBars": 50,
    "LiveOutcomeSampleWeight": 3,
    "RequiredShadowTrades": 50,
    "DriftMonitorIntervalMinutes": 60,
    "DriftRollingWindowTrades": 30,
    "DriftAccuracyDropThreshold": 0.05,
    "DriftBrierScoreDeltaThreshold": 0.10,
    "DriftConsecutiveErrorLimit": 10,
    "PromotionMinDirectionAccuracyDelta": 0.03,
    "HyperparameterGridConfig": {
      "FastTree": {
        "NumberOfLeaves":  [20, 50, 100],
        "NumberOfTrees":   [100, 200, 500],
        "LearningRate":    [0.05, 0.1, 0.2],
        "MinDataInLeaf":   [10, 20]
      },
      "FastForest": {
        "NumberOfTrees":   [100, 200, 500],
        "NumberOfLeaves":  [20, 50],
        "MinDataInLeaf":   [10, 20]
      }
    }
  },
  "RegimeConfig": {
    "ADXTrendingThreshold": 25,
    "ADXRangingThreshold": 20,
    "ATRHighVolatilityMultiplier": 1.5,
    "ATRLowVolatilityMultiplier": 0.5,
    "ATRAveragePeriod": 20,
    "LookbackBars": 14
  },
  "EnsembleConfig": {
    "Mode": "Confluence",
    "MinConfluentStrategies": 2,
    "ConfluentWindowSeconds": 30,
    "AllocationMultiplier": 1.0,
    "RebalanceCronSchedule": "0 0 * * 0"
  },
  "EconomicCalendarConfig": {
    "Source": "ForexFactory",
    "SyncCronSchedule": "0 0 * * *",
    "DefaultMinutesBefore": 15,
    "DefaultMinutesAfter": 30
  },
  "SentimentConfig": {
    "COTSyncCronSchedule": "0 6 * * 6",
    "NewsSentimentIntervalMinutes": 60,
    "COTLookbackWeeks": 52,
    "NewsSentimentSource": "FinancialNewsAPI",
    "NewsSentimentApiKey": ""
  },
  "PaperTradingConfig": {
    "DefaultSlippagePips": 0.5,
    "SimulatedFillLatencyMs": 50
  },
  "RateLimiterConfig": {
    "OANDA": {
      "TickStream":        { "Capacity": 1,  "RefillPerSecond": 1  },
      "HistoricalCandles": { "Capacity": 5,  "RefillPerSecond": 2  },
      "SubmitOrder":       { "Capacity": 10, "RefillPerSecond": 5  },
      "OrderStatus":       { "Capacity": 20, "RefillPerSecond": 10 },
      "ModifyOrder":       { "Capacity": 10, "RefillPerSecond": 5  },
      "AccountInfo":       { "Capacity": 5,  "RefillPerSecond": 1  }
    },
    "InteractiveBrokers": {
      "SubmitOrder":       { "Capacity": 50,  "RefillPerSecond": 25 },
      "OrderStatus":       { "Capacity": 100, "RefillPerSecond": 50 }
    }
  },
  "WalkForwardConfig": {
    "DefaultInSampleDays": 90,
    "DefaultOutOfSampleDays": 30,
    "DefaultStepDays": 30,
    "DefaultOptimizationIterations": 30
  },
  "StrategyOptimizationConfig": {
    "EvaluationWindowTrades": 20,
    "OptimizationCronSchedule": "0 0 * * 0",
    "OptimizationIterations": 50,
    "HealthScoreThresholds": {
      "Degrading": 0.60,
      "Critical": 0.40
    },
    "HealthScoreWeights": {
      "WinRate": 0.25,
      "ProfitFactor": 0.30,
      "SharpeRatio": 0.25,
      "MaxDrawdownPenalty": 0.20
    },
    "MinImprovementToPropose": 0.05
  }
}
```

---

## Appendix B — CQRS Feature Folder Structure (full)

```
Application/
  Features/
    CurrencyPairs/
      Commands/
        CreateCurrencyPair/
        UpdateCurrencyPair/
        DeleteCurrencyPair/
      Queries/
        GetCurrencyPair/
        GetPagedCurrencyPairs/
      Dtos/
        CurrencyPairDto.cs
    MarketData/
      Commands/
        IngestCandle/
        UpdateLiveCandle/
      Queries/
        GetCandles/
        GetLatestCandle/
        GetLivePrice/
      Dtos/
        CandleDto.cs
        LivePriceDto.cs
    Strategies/
      Commands/
        CreateStrategy/
        UpdateStrategy/
        DeleteStrategy/
        ActivateStrategy/
        PauseStrategy/
        AssignRiskProfile/
      Queries/
        GetStrategy/
        GetPagedStrategies/
      Dtos/
        StrategyDto.cs
      Evaluators/
        IStrategyEvaluator.cs
        MovingAverageCrossoverEvaluator.cs
        RSIReversionEvaluator.cs
        BreakoutScalperEvaluator.cs
      Indicators/
        MovingAverage.cs
        RSI.cs
        ATR.cs
    TradeSignals/
      Commands/
        CreateTradeSignal/
        ApproveTradeSignal/
        RejectTradeSignal/
        ExpireTradeSignal/
      Queries/
        GetTradeSignal/
        GetPagedTradeSignals/
      Dtos/
        TradeSignalDto.cs
    Orders/                          (existing — extend)
      Commands/
        CreateOrder/
        UpdateOrder/
        DeleteOrder/
        SubmitOrder/
        UpdateOrderStatus/
        CancelOrder/
        ModifyOrder/
        ClosePositionOrder/
      Queries/
        GetOrder/
        GetPagedOrders/
      Dtos/
        OrderDto.cs
    Positions/
      Commands/
        OpenPosition/
        UpdatePosition/
        ClosePosition/
        PartialClosePosition/
      Queries/
        GetPosition/
        GetPagedPositions/
        GetPortfolioSummary/
        GetDailyPnL/
      Dtos/
        PositionDto.cs
        PortfolioSummaryDto.cs
    RiskProfiles/
      Commands/
        CreateRiskProfile/
        UpdateRiskProfile/
        DeleteRiskProfile/
      Queries/
        GetRiskProfile/
        GetPagedRiskProfiles/
        GetRiskSummary/
      Dtos/
        RiskProfileDto.cs
        RiskSummaryDto.cs
      Checkers/
        IRiskChecker.cs
        PreTradeRiskChecker.cs
    Backtests/
      Commands/
        QueueBacktest/
        CancelBacktest/
      Queries/
        GetBacktestRun/
        GetPagedBacktestRuns/
      Dtos/
        BacktestRunDto.cs
        BacktestResultDto.cs
      Engine/
        IBacktestEngine.cs
        BacktestEngine.cs
    Alerts/
      Commands/
        CreateAlert/
        UpdateAlert/
        DeleteAlert/
      Queries/
        GetAlert/
        GetPagedAlerts/
      Dtos/
        AlertDto.cs
      Handlers/
        PriceAlertEventHandler.cs
        OrderFilledAlertEventHandler.cs
        DrawdownAlertEventHandler.cs
    MLSignalScoring/
      Commands/
        TriggerMLTraining/
        ActivateMLModel/
        DeactivateMLModel/
      Queries/
        GetMLModel/
        GetPagedMLModels/
        GetMLTrainingRun/
        GetPagedMLTrainingRuns/
      Dtos/
        MLModelDto.cs
        MLTrainingRunDto.cs
      Interfaces/
        IMLSignalScorer.cs
        IMLModelLoader.cs
      Pipeline/
        FeatureExtractor.cs          # Extracts indicator feature vector from candles
        DirectionClassifierTrainer.cs
        MagnitudeRegressorTrainer.cs
        MLTrainingPipeline.cs        # Orchestrates both trainers
      Models/
        SignalFeatureVector.cs               # ML.NET input schema
        DirectionPrediction.cs              # ML.NET output schema (direction)
        MagnitudePrediction.cs              # ML.NET output schema (magnitude)
      Evaluation/
        IMLShadowEvaluator.cs
        IMLDriftDetector.cs
        IMLHyperparameterTuner.cs
        PromotionCriteriaEvaluator.cs       # Direction accuracy, magnitude correlation, Brier score
        DriftDetector.cs                    # Rolling accuracy window + consecutive error streak
        HyperparameterTuner.cs              # 3-fold CV grid search over FastTree/FastForest params
      Commands/
        PromoteMLModel/
        RejectMLChallenger/
      Queries/
        GetMLShadowEvaluation/
        GetPagedMLShadowEvaluations/
        GetMLModelPredictionLogs/
        GetMLDriftSummary/
      Dtos/
        MLShadowEvaluationDto.cs
        MLModelPredictionLogDto.cs
        MLDriftSummaryDto.cs
    MarketData/
      ...
    EconomicCalendar/
      Commands/
        SyncEconomicCalendar/
        CreateEconomicEvent/
        UpdateEconomicEvent/
        DeleteEconomicEvent/
      Queries/
        GetUpcomingEconomicEvents/
        GetPagedEconomicEvents/
        IsNewsWindowActive/
      Dtos/
        EconomicEventDto.cs
      Filters/
        INewsFilter.cs
        NewsFilter.cs
    MarketRegime/
      Queries/
        GetCurrentRegime/
        GetPagedRegimeHistory/
      Dtos/
        MarketRegimeSnapshotDto.cs
      Classifiers/
        IRegimeClassifier.cs
        RegimeClassifier.cs              # ADX + ATR combined classification
    MultiTimeframe/
      Filters/
        IMultiTimeframeFilter.cs
        MultiTimeframeFilter.cs
    SessionFilter/
      ISessionFilter.cs
      SessionFilter.cs
      ForexSessionSchedule.cs            # UTC session windows
    PortfolioRisk/
      Queries/
        GetPortfolioCorrelation/
        GetCurrencyExposure/
      Dtos/
        PortfolioCorrelationDto.cs
        CurrencyExposureDto.cs
      Checkers/
        ICorrelationRiskChecker.cs
        CorrelationRiskChecker.cs
        PearsonCorrelationCalculator.cs
    ExecutionQuality/
      Queries/
        GetExecutionQuality/
        GetExecutionQualitySummary/
      Dtos/
        ExecutionQualityLogDto.cs
        ExecutionQualitySummaryDto.cs
    WalkForward/
      Commands/
        QueueWalkForwardRun/
        CancelWalkForwardRun/
      Queries/
        GetWalkForwardRun/
        GetPagedWalkForwardRuns/
      Dtos/
        WalkForwardRunDto.cs
      Engine/
        IWalkForwardEngine.cs
        WalkForwardEngine.cs
    Ensemble/
      Commands/
        TriggerAllocationRebalance/
        SetStrategyAllocation/
      Queries/
        GetStrategyAllocations/
        GetEnsembleStatus/
      Dtos/
        StrategyAllocationDto.cs
        EnsembleStatusDto.cs
      Allocator/
        IEnsembleAllocator.cs
        SharpeWeightedAllocator.cs
      Confluence/
        ConfluentSignalBuffer.cs         # Short-lived in-memory buffer for confluence matching
    TrailingStop/
      ITrailingStopManager.cs
      FixedPipsTrailingStopManager.cs
      ATRTrailingStopManager.cs
      PercentageTrailingStopManager.cs
      Commands/
        AddTrailingStop/
        ModifyTrailingStop/
        AddScaleOrder/
        CancelScaleOrder/
      Queries/
        GetPositionScaleOrders/
      Dtos/
        PositionScaleOrderDto.cs
    Attribution/
      Queries/
        GetAttributionBySession/
        GetAttributionByRegime/
        GetAttributionByMLConfidence/
        GetAttributionByNewsProximity/
        GetAttributionByMTFConfluence/
        GetAttributionBySymbol/
        GetAttributionByDayOfWeek/
        GetFullAttributionSummary/
      Dtos/
        AttributionResultDto.cs
    Configuration/
      IEngineConfigManager.cs
      EngineConfigManager.cs
      Commands/
        UpdateEngineConfig/
        ResetEngineConfig/
      Queries/
        GetEngineConfigs/
        GetEngineConfig/
      Dtos/
        EngineConfigDto.cs
    PaperTrading/
      IPaperBrokerOrderExecutor.cs
      PaperOrderBook.cs                  # In-memory pending limit/stop orders
    AuditTrail/
      IDecisionLogger.cs
      DecisionLogger.cs                  # Async fire-and-forget IDecisionLogger implementation
      Queries/
        GetDecisionLogs/
        GetSignalDecisionSummary/
      Dtos/
        DecisionLogDto.cs
    Sentiment/
      ISentimentDataFeed.cs
      ICOTDataFeed.cs
      Queries/
        GetLatestSentiment/
        GetPagedCOTReports/
        GetSentimentHistory/
      Dtos/
        SentimentSnapshotDto.cs
        COTReportDto.cs
      Filters/
        ISentimentFilter.cs
        SentimentFilter.cs
    Health/
      ISystemHealthAggregator.cs
      SystemHealthAggregator.cs
      SubsystemCheckers/
        BrokerFeedHealthChecker.cs
        WorkerHealthChecker.cs
        MLModelHealthChecker.cs
        MessageBusHealthChecker.cs
        DatabaseHealthChecker.cs
    StrategyFeedback/
      Commands/
        TriggerOptimizationRun/
        ApproveOptimizedParameters/
        RejectOptimizedParameters/
      Queries/
        GetStrategyPerformance/
        GetPagedStrategyPerformances/
        GetOptimizationRun/
        GetPagedOptimizationRuns/
      Dtos/
        StrategyPerformanceSnapshotDto.cs
        OptimizationRunDto.cs
      Interfaces/
        IHealthScoreCalculator.cs
        IStrategyOptimizer.cs
      Scoring/
        HealthScoreCalculator.cs     # Weighted composite formula
        HealthStatusClassifier.cs    # Maps score → "Healthy" | "Degrading" | "Critical"
      Optimization/
        BayesianStrategyOptimizer.cs # Gaussian Process + UCB acquisition
        ParameterSearchSpace.cs      # Bounded parameter ranges per StrategyType
        OptimizationIteration.cs
      Feedback/
        OutcomeLabelledSampleBuilder.cs  # Builds OutcomeLabelledSample from closed position

Infrastructure/
  BrokerAdapters/
    IBrokerDataFeed.cs
    IBrokerOrderExecutor.cs
    OANDA/
      OandaBrokerAdapter.cs
      OandaWebSocketClient.cs
      OandaRestClient.cs
    MetaTrader5/
      MetaTrader5BrokerAdapter.cs
    InteractiveBrokers/
      IBBrokerAdapter.cs
  Notifications/
    IAlertDispatcher.cs
    EmailAlertDispatcher.cs
    WebhookAlertDispatcher.cs
    TelegramAlertDispatcher.cs
  Cache/
    ILivePriceCache.cs
    InMemoryLivePriceCache.cs
  MLScoring/
    MLSignalScorer.cs              # IMLSignalScorer implementation (ML.NET PredictionEngine)
    MLModelLoader.cs               # IMLModelLoader implementation (hot-reload on model activation)
  PaperTrading/
    PaperBrokerOrderExecutor.cs    # IPaperBrokerOrderExecutor — fills at live mid-price
    PaperOrderBook.cs              # Pending limit/stop orders in memory
  Failover/
    IBrokerFailoverManager.cs
    BrokerFailoverManager.cs       # Monitors primary, switches to secondary, handles failback
  RateLimiting/
    IBrokerRateLimiter.cs
    TokenBucketRateLimiter.cs      # Token bucket per (broker, endpoint)
    RateLimiterConfig.cs           # Binds RateLimiterConfig from appsettings
  Sentiment/
    COTDataFeedAdapter.cs          # Parses CFTC weekly CSV
    NewsSentimentAdapter.cs        # NLP headline scoring (FinBERT or ML.NET text classifier)
  Workers/
    MarketDataWorker.cs
    StrategyWorker.cs
    OrderExecutionWorker.cs
    RiskMonitorWorker.cs
    MLRetrainingWorker.cs
    MLShadowEvaluationWorker.cs
    MLDriftMonitorWorker.cs
    StrategyEvaluationWorker.cs
    OptimizationRunWorker.cs
    EconomicCalendarSyncWorker.cs
    MarketRegimeWorker.cs
    AllocationRebalanceWorker.cs
    SentimentWorker.cs
    TrailingStopWorker.cs

API/
  Controllers/v1/
    OrderController.cs             (existing — extend)
    CurrencyPairController.cs
    MarketDataController.cs
    StrategyController.cs
    SignalController.cs
    PositionController.cs
    RiskProfileController.cs
    BacktestController.cs
    AlertController.cs
    MLController.cs
    StrategyOptimizationController.cs
    EconomicCalendarController.cs
    MarketRegimeController.cs
    PortfolioRiskController.cs
    ExecutionQualityController.cs
    WalkForwardController.cs
    EnsembleController.cs
    AuditController.cs
    SentimentController.cs
    AttributionController.cs
    ConfigController.cs
```
