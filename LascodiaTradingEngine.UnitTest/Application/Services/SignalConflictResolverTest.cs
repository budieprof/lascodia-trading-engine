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

    // ═══════════════════════════════════════════════════════════════════════════
    //  Determinism under non-deterministic input ordering (Fix 14)
    // ═══════════════════════════════════════════════════════════════════════════
    //
    // The parallel evaluator loop collects candidate signals into a ConcurrentBag
    // whose iteration order is undefined. The resolver must therefore produce the
    // same winner regardless of insertion order — otherwise which strategy gets
    // to trade depends on thread-scheduling whims. These tests pin down that
    // invariant for the three ordering-sensitive paths: same-direction ranking,
    // exact-score ties broken by (ExpiresAt, StrategyId), and mixed-direction
    // suppression.

    [Fact]
    public void Resolve_SameDirection_ScoreWinner_IsIndependentOfInsertionOrder()
    {
        var exp = DateTime.UtcNow.AddMinutes(5);
        var forward = new List<PendingSignal>
        {
            CreateSignal("EURUSD", TradeDirection.Buy, strategyId: 1, mlConfidence: 0.50m, sharpe: 0.5m, expiresAt: exp),
            CreateSignal("EURUSD", TradeDirection.Buy, strategyId: 2, mlConfidence: 0.90m, sharpe: 2.0m, expiresAt: exp),
            CreateSignal("EURUSD", TradeDirection.Buy, strategyId: 3, mlConfidence: 0.70m, sharpe: 1.0m, expiresAt: exp),
        };
        var reverse = forward.AsEnumerable().Reverse().ToList();

        var wFwd = _resolver.Resolve(forward);
        var wRev = _resolver.Resolve(reverse);

        Assert.Single(wFwd);
        Assert.Single(wRev);
        Assert.Equal(wFwd[0].StrategyId, wRev[0].StrategyId);
        Assert.Equal(2L, wFwd[0].StrategyId); // The true best score wins, not whichever was first.
    }

    [Fact]
    public void Resolve_SameDirection_ExactScoreTie_BreaksByExpiresAtThenStrategyId()
    {
        // All three candidates share identical scoring inputs, so ComputeScore
        // collapses to a single value. The tie-break order is ExpiresAt ASC then
        // StrategyId ASC — encoded on Resolver's OrderBy chain. Strategy 7 has the
        // earliest expiry; strategies 4 and 9 tie on expiry but 4 wins on StrategyId.
        var earlier = DateTime.UtcNow.AddMinutes(3);
        var later   = DateTime.UtcNow.AddMinutes(10);
        var a = CreateSignal("EURUSD", TradeDirection.Buy, strategyId: 9, mlConfidence: 0.70m, sharpe: 1.0m, expiresAt: later);
        var b = CreateSignal("EURUSD", TradeDirection.Buy, strategyId: 7, mlConfidence: 0.70m, sharpe: 1.0m, expiresAt: earlier);
        var c = CreateSignal("EURUSD", TradeDirection.Buy, strategyId: 4, mlConfidence: 0.70m, sharpe: 1.0m, expiresAt: later);

        var order1 = new List<PendingSignal> { a, b, c };
        var order2 = new List<PendingSignal> { c, a, b };
        var order3 = new List<PendingSignal> { b, c, a };

        var w1 = _resolver.Resolve(order1);
        var w2 = _resolver.Resolve(order2);
        var w3 = _resolver.Resolve(order3);

        Assert.Single(w1);
        Assert.Single(w2);
        Assert.Single(w3);
        Assert.Equal(7L, w1[0].StrategyId); // Earliest expiry wins.
        Assert.Equal(7L, w2[0].StrategyId);
        Assert.Equal(7L, w3[0].StrategyId);
    }

    [Fact]
    public void Resolve_MixedDirectionMixedSymbol_WinnerSetIsIndependentOfInsertionOrder()
    {
        // Multi-group scenario to exercise the grouping step as well as the
        // scoring step. EURUSD has a real mixed-direction conflict (suppressed);
        // GBPUSD has a same-direction winner race; USDJPY has a single signal.
        var exp = DateTime.UtcNow.AddMinutes(5);
        var signals = new List<PendingSignal>
        {
            CreateSignal("EURUSD", TradeDirection.Buy,  strategyId: 1, expiresAt: exp),
            CreateSignal("EURUSD", TradeDirection.Sell, strategyId: 2, expiresAt: exp),
            CreateSignal("GBPUSD", TradeDirection.Buy,  strategyId: 3, mlConfidence: 0.60m, sharpe: 0.8m, expiresAt: exp),
            CreateSignal("GBPUSD", TradeDirection.Buy,  strategyId: 4, mlConfidence: 0.85m, sharpe: 1.9m, expiresAt: exp),
            CreateSignal("USDJPY", TradeDirection.Sell, strategyId: 5, expiresAt: exp),
        };
        var shuffled = new List<PendingSignal> { signals[4], signals[2], signals[0], signals[3], signals[1] };

        var a = _resolver.Resolve(signals).Select(s => s.StrategyId).OrderBy(id => id).ToArray();
        var b = _resolver.Resolve(shuffled).Select(s => s.StrategyId).OrderBy(id => id).ToArray();

        Assert.Equal(new[] { 4L, 5L }, a); // GBPUSD winner = strategy 4, USDJPY solo = strategy 5. EURUSD suppressed.
        Assert.Equal(a, b);
    }

    private static PendingSignal CreateSignal(
        string symbol,
        TradeDirection direction,
        long strategyId = 1,
        decimal mlConfidence = 0.7m,
        decimal sharpe = 1.0m,
        decimal capacity = 10.0m,
        DateTime? expiresAt = null)
        => new(
            StrategyId: strategyId,
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
            ExpiresAt: expiresAt ?? DateTime.UtcNow.AddMinutes(5));
}
