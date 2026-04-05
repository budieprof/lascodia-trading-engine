using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

public class GenerationCheckpointStoreTest
{
    private readonly DateTime _today = DateTime.UtcNow.Date;

    [Fact]
    public void Serialize_Restore_RoundTrip()
    {
        var state = new GenerationCheckpointStore.State
        {
            CycleDateUtc = _today,
            Fingerprint = "fp-1",
            CompletedSymbols = ["EURUSD", "GBPUSD", "AUDUSD"],
            CandidatesCreated = 7,
            ReserveCreated = 2,
            CandidatesPerCurrency = new() { ["EUR"] = 3, ["USD"] = 5 },
            RegimeCandidatesCreated = new() { ["Trending"] = 4, ["Ranging"] = 3 },
            CorrelationGroupCounts = new() { ["0"] = 2, ["1"] = 1 },
        };

        var json = GenerationCheckpointStore.Serialize(state);
        var restored = GenerationCheckpointStore.Restore(json, _today);

        Assert.NotNull(restored);
        Assert.Equal(state.CandidatesCreated, restored.CandidatesCreated);
        Assert.Equal(state.ReserveCreated, restored.ReserveCreated);
        Assert.Equal(state.Fingerprint, restored.Fingerprint);
        Assert.Equal(state.CompletedSymbols.Count, restored.CompletedSymbols.Count);
        Assert.Contains("EURUSD", restored.CompletedSymbols);
        Assert.Contains("GBPUSD", restored.CompletedSymbols);
        Assert.Equal(3, restored.CandidatesPerCurrency["EUR"]);
        Assert.Equal(4, restored.RegimeCandidatesCreated["Trending"]);
        Assert.Equal(2, restored.CorrelationGroupCounts["0"]);
    }

    [Fact]
    public void Restore_ReturnsNull_ForEmptyJson()
    {
        Assert.Null(GenerationCheckpointStore.Restore(null, _today));
        Assert.Null(GenerationCheckpointStore.Restore("", _today));
    }

    [Fact]
    public void Restore_ReturnsNull_ForCorruptJson()
    {
        var result = GenerationCheckpointStore.Restore("{invalid json!!}", _today, null, NullLogger.Instance);
        Assert.Null(result);
    }

    [Fact]
    public void Restore_ReturnsNull_ForStaleCycleDate()
    {
        var state = new GenerationCheckpointStore.State
        {
            CycleDateUtc = _today.AddDays(-1), // Yesterday
            Fingerprint = "fp-old",
            CompletedSymbols = ["EURUSD"],
            CandidatesCreated = 3,
        };

        var json = GenerationCheckpointStore.Serialize(state);
        var result = GenerationCheckpointStore.Restore(json, _today, null, NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void CompletedSymbolSet_IsCaseInsensitive()
    {
        var state = new GenerationCheckpointStore.State
        {
            CycleDateUtc = _today,
            CompletedSymbols = ["EURUSD", "gbpusd"],
        };

        var set = GenerationCheckpointStore.CompletedSymbolSet(state);

        Assert.Contains("eurusd", set);
        Assert.Contains("GBPUSD", set);
    }

    [Fact]
    public void Serialize_Restore_PreservesPendingCandidates()
    {
        var outcome = new ScreeningOutcome
        {
            Strategy = new Strategy
            {
                Name = "Auto-MovingAverageCrossover-EURUSD-H1",
                Description = "Auto-generated for Trending regime",
                StrategyType = StrategyType.MovingAverageCrossover,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = """{"FastPeriod":9,"SlowPeriod":21}""",
                CreatedAt = _today.AddHours(2),
                ScreeningMetricsJson = new ScreeningMetrics
                {
                    Regime = "Trending",
                    GenerationSource = "Primary",
                    IsWinRate = 0.61,
                    OosWinRate = 0.58,
                }.ToJson(),
            },
            TrainResult = new()
            {
                TotalTrades = 24,
                WinRate = 0.61m,
                ProfitFactor = 1.42m,
                MaxDrawdownPct = 0.11m,
                SharpeRatio = 0.83m,
                Trades =
                [
                    new() { PnL = 120m, ExitTime = _today.AddHours(3) },
                    new() { PnL = -40m, ExitTime = _today.AddHours(5) },
                ],
            },
            OosResult = new()
            {
                TotalTrades = 10,
                WinRate = 0.58m,
                ProfitFactor = 1.21m,
                MaxDrawdownPct = 0.13m,
                SharpeRatio = 0.52m,
                Trades =
                [
                    new() { PnL = 60m, ExitTime = _today.AddHours(7) },
                ],
            },
            Regime = MarketRegimeEnum.Trending,
            Metrics = new ScreeningMetrics
            {
                Regime = "Trending",
                GenerationSource = "Primary",
                IsWinRate = 0.61,
                OosWinRate = 0.58,
            },
        };

        var state = new GenerationCheckpointStore.State
        {
            CycleDateUtc = _today,
            Fingerprint = "fp-pending",
            CandidatesCreated = 1,
            PendingCandidates = [GenerationCheckpointStore.PendingCandidateState.FromOutcome(outcome)],
        };

        var json = GenerationCheckpointStore.Serialize(state);
        var restored = GenerationCheckpointStore.Restore(json, _today);

        Assert.NotNull(restored);
        Assert.Single(restored.PendingCandidates);

        var pending = restored.PendingCandidates[0].ToOutcome();
        Assert.Equal(outcome.Strategy.Name, pending.Strategy.Name);
        Assert.Equal(outcome.Strategy.ParametersJson, pending.Strategy.ParametersJson);
        Assert.Equal(outcome.TrainResult.SharpeRatio, pending.TrainResult.SharpeRatio);
        Assert.Equal(outcome.OosResult.TotalTrades, pending.OosResult.TotalTrades);
        Assert.Equal(outcome.Regime, pending.Regime);
        Assert.Collection(pending.TrainResult.Trades,
            _ => { },
            _ => { });
        Assert.Single(pending.OosResult.Trades);
    }

    [Fact]
    public void Restore_ReturnsNull_ForFingerprintMismatch()
    {
        var state = new GenerationCheckpointStore.State
        {
            CycleDateUtc = _today,
            Fingerprint = "expected-a",
            CompletedSymbols = ["EURUSD"],
            CandidatesCreated = 1,
        };

        var json = GenerationCheckpointStore.Serialize(state);
        var result = GenerationCheckpointStore.Restore(json, _today, "expected-b", NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void Empty_HasCorrectDefaults()
    {
        var empty = GenerationCheckpointStore.Empty(_today);

        Assert.Equal(_today, empty.CycleDateUtc);
        Assert.Empty(empty.CompletedSymbols);
        Assert.Equal(0, empty.CandidatesCreated);
        Assert.Equal(0, empty.ReserveCreated);
    }
}
