using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class SignalConflictResolverTest
{
    private readonly SignalConflictResolver _resolver;

    public SignalConflictResolverTest()
    {
        _resolver = new SignalConflictResolver(Mock.Of<ILogger<SignalConflictResolver>>());
    }

    [Fact]
    public void Resolve_SingleSignal_ReturnsIt()
    {
        var signals = new List<PendingSignal>
        {
            CreateSignal("EURUSD", TradeDirection.Buy)
        };

        var result = _resolver.Resolve(signals);

        Assert.Single(result);
        Assert.Equal("EURUSD", result[0].Symbol);
    }

    [Fact]
    public void Resolve_SameDirectionSameSymbol_KeepsHighestScoring()
    {
        var signals = new List<PendingSignal>
        {
            CreateSignal("EURUSD", TradeDirection.Buy, mlConfidence: 0.6m, sharpe: 1.0m),
            CreateSignal("EURUSD", TradeDirection.Buy, mlConfidence: 0.9m, sharpe: 2.0m),
        };

        var result = _resolver.Resolve(signals);

        Assert.Single(result);
        Assert.Equal(0.9m, result[0].MLConfidenceScore);
    }

    [Fact]
    public void Resolve_OpposingDirections_SuppressesBoth()
    {
        var signals = new List<PendingSignal>
        {
            CreateSignal("EURUSD", TradeDirection.Buy),
            CreateSignal("EURUSD", TradeDirection.Sell),
        };

        var result = _resolver.Resolve(signals);

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_DifferentSymbols_KeepsBoth()
    {
        var signals = new List<PendingSignal>
        {
            CreateSignal("EURUSD", TradeDirection.Buy),
            CreateSignal("GBPUSD", TradeDirection.Sell),
        };

        var result = _resolver.Resolve(signals);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Resolve_EmptyInput_ReturnsEmpty()
    {
        var result = _resolver.Resolve(Array.Empty<PendingSignal>());
        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_ThreeSignalsSameSymbolSameDirection_KeepsOnlyBest()
    {
        var signals = new List<PendingSignal>
        {
            CreateSignal("EURUSD", TradeDirection.Buy, mlConfidence: 0.5m, sharpe: 0.5m),
            CreateSignal("EURUSD", TradeDirection.Buy, mlConfidence: 0.7m, sharpe: 1.5m),
            CreateSignal("EURUSD", TradeDirection.Buy, mlConfidence: 0.6m, sharpe: 1.0m),
        };

        var result = _resolver.Resolve(signals);

        Assert.Single(result);
        Assert.Equal(0.7m, result[0].MLConfidenceScore);
    }

    private static PendingSignal CreateSignal(
        string symbol,
        TradeDirection direction,
        decimal mlConfidence = 0.7m,
        decimal sharpe = 1.0m,
        decimal capacity = 10.0m)
        => new(
            StrategyId: 1,
            Symbol: symbol,
            Timeframe: Timeframe.H1,
            StrategyType: StrategyType.BreakoutScalper,
            Direction: direction,
            EntryPrice: 1.1000m,
            StopLoss: 1.0950m,
            TakeProfit: 1.1100m,
            SuggestedLotSize: 0.1m,
            Confidence: 0.8m,
            MLConfidenceScore: mlConfidence,
            MLModelId: 1,
            EstimatedCapacityLots: capacity,
            StrategySharpeRatio: sharpe,
            ExpiresAt: DateTime.UtcNow.AddMinutes(5));
}
