using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.TestHelpers;

/// <summary>Factory methods for creating test entities with sensible defaults.</summary>
public static class EntityFactory
{
    private static long _nextId = 1;
    private static long NextId() => Interlocked.Increment(ref _nextId);

    public static TradingAccount CreateAccount(decimal equity = 10000m, bool isActive = true)
        => new()
        {
            Id = NextId(),
            Equity = equity,
            Balance = equity,
            IsActive = isActive
        };

    public static Position CreatePosition(
        string symbol = "EURUSD",
        decimal lots = 0.1m,
        decimal entryPrice = 1.1000m,
        PositionDirection direction = PositionDirection.Long,
        PositionStatus status = PositionStatus.Open)
        => new()
        {
            Id = NextId(),
            Symbol = symbol,
            OpenLots = lots,
            AverageEntryPrice = entryPrice,
            Direction = direction,
            Status = status
        };

    public static TradeSignal CreateSignal(
        string symbol = "EURUSD",
        TradeDirection direction = TradeDirection.Buy,
        decimal entryPrice = 1.1000m,
        decimal lotSize = 0.1m,
        decimal confidence = 0.8m,
        decimal? mlConfidence = 0.75m)
        => new()
        {
            Id = NextId(),
            Symbol = symbol,
            Direction = direction,
            EntryPrice = entryPrice,
            SuggestedLotSize = lotSize,
            Confidence = confidence,
            MLConfidenceScore = mlConfidence,
            StopLoss = direction == TradeDirection.Buy ? entryPrice - 0.0050m : entryPrice + 0.0050m,
            TakeProfit = direction == TradeDirection.Buy ? entryPrice + 0.0100m : entryPrice - 0.0100m,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };

    public static Order CreateOrder(
        string symbol = "EURUSD",
        decimal quantity = 0.1m,
        decimal price = 1.1000m,
        OrderType orderType = OrderType.Buy,
        OrderStatus status = OrderStatus.Filled,
        decimal? filledPrice = null)
        => new()
        {
            Id = NextId(),
            Symbol = symbol,
            Quantity = quantity,
            Price = price,
            OrderType = orderType,
            Status = status,
            FilledPrice = filledPrice ?? price,
            FilledAt = status == OrderStatus.Filled ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow.AddSeconds(-10),
            TradingAccountId = 1,
            StrategyId = 1
        };

    public static Strategy CreateStrategy(
        string symbol = "EURUSD",
        StrategyStatus status = StrategyStatus.Active,
        StrategyLifecycleStage lifecycleStage = StrategyLifecycleStage.Active)
        => new()
        {
            Id = NextId(),
            Name = $"TestStrategy_{symbol}",
            Symbol = symbol,
            Status = status,
            LifecycleStage = lifecycleStage,
            Timeframe = Timeframe.H1,
            StrategyType = StrategyType.MovingAverageCrossover,
            CreatedAt = DateTime.UtcNow
        };

    public static MLModel CreateMLModel(
        string symbol = "EURUSD",
        bool isActive = true,
        decimal accuracy = 0.65m)
        => new()
        {
            Id = NextId(),
            Symbol = symbol,
            Timeframe = Timeframe.H1,
            ModelVersion = "v1",
            IsActive = isActive,
            Status = isActive ? MLModelStatus.Active : MLModelStatus.Superseded,
            DirectionAccuracy = accuracy,
            TrainingSamples = 1000,
            TrainedAt = DateTime.UtcNow.AddDays(-7)
        };

    public static Alert CreateAlert(
        string symbol = "EURUSD",
        AlertSeverity severity = AlertSeverity.Medium)
        => new()
        {
            Id = NextId(),
            Symbol = symbol,
            AlertType = AlertType.PriceLevel,
            Severity = severity,
            IsActive = true,
            ConditionJson = "{}"
        };

    public static Candle CreateCandle(
        string symbol = "EURUSD",
        Timeframe timeframe = Timeframe.D1,
        decimal close = 1.1000m,
        DateTime? timestamp = null)
        => new()
        {
            Id = NextId(),
            Symbol = symbol,
            Timeframe = timeframe,
            Open = close - 0.0010m,
            High = close + 0.0020m,
            Low = close - 0.0030m,
            Close = close,
            Volume = 1000,
            Timestamp = timestamp ?? DateTime.UtcNow,
            IsClosed = true
        };

    public static RiskProfile CreateRiskProfile()
        => new()
        {
            Id = NextId(),
            Name = "TestProfile",
            MaxLotSizePerTrade = 1.0m,
            MaxDailyDrawdownPct = 5.0m,
            MaxRiskPerTradePct = 2.0m,
            MaxTotalExposurePct = 30.0m,
            MaxOpenPositions = 10,
            MaxDailyTrades = 20,
            MaxSymbolExposurePct = 10.0m
        };

}
