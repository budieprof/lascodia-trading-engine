using LascodiaTradingEngine.Application.StrategyGeneration;
using Microsoft.Extensions.Logging.Abstractions;

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
        var result = GenerationCheckpointStore.Restore("{invalid json!!}", _today, NullLogger.Instance);
        Assert.Null(result);
    }

    [Fact]
    public void Restore_ReturnsNull_ForStaleCycleDate()
    {
        var state = new GenerationCheckpointStore.State
        {
            CycleDateUtc = _today.AddDays(-1), // Yesterday
            CompletedSymbols = ["EURUSD"],
            CandidatesCreated = 3,
        };

        var json = GenerationCheckpointStore.Serialize(state);
        var result = GenerationCheckpointStore.Restore(json, _today, NullLogger.Instance);

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
    public void Empty_HasCorrectDefaults()
    {
        var empty = GenerationCheckpointStore.Empty(_today);

        Assert.Equal(_today, empty.CycleDateUtc);
        Assert.Empty(empty.CompletedSymbols);
        Assert.Equal(0, empty.CandidatesCreated);
        Assert.Equal(0, empty.ReserveCreated);
    }
}
