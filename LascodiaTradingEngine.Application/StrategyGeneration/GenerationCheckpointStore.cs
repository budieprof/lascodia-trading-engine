using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Owns checkpoint serialization and restoration for strategy generation cycles.
/// Allows the worker to resume from a partial cycle after a crash, skipping
/// already-screened symbols and restoring budget counters.
/// </summary>
internal static class GenerationCheckpointStore
{
    internal const int PayloadVersion = 1;
    internal const int MaxCheckpointChars = 500_000;
    internal const string ConfigKey = "StrategyGeneration:CycleCheckpoint";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal sealed record State
    {
        public int Version { get; init; } = PayloadVersion;
        public DateTime CycleDateUtc { get; init; }
        public List<string> CompletedSymbols { get; init; } = [];
        public int CandidatesCreated { get; init; }
        public int ReserveCreated { get; init; }
        // String-keyed dicts for JSON serialization (enums/tuples don't round-trip in System.Text.Json)
        public Dictionary<string, int> CandidatesPerCurrency { get; init; } = new();
        public Dictionary<string, int> RegimeCandidatesCreated { get; init; } = new();
        public Dictionary<string, int> CorrelationGroupCounts { get; init; } = new();
    }

    internal static State Empty(DateTime cycleDate) => new() { CycleDateUtc = cycleDate };

    internal static string Serialize(State state, ILogger? logger = null)
    {
        string json = JsonSerializer.Serialize(state, SerializerOptions);

        if (json.Length <= MaxCheckpointChars)
            return json;

        logger?.LogWarning(
            "GenerationCheckpointStore: checkpoint payload exceeded {Limit} chars ({Actual}); trimming CompletedSymbols",
            MaxCheckpointChars, json.Length);

        state = state with
        {
            CompletedSymbols = [$"[{state.CompletedSymbols.Count} symbols completed — trimmed to fit checkpoint limit]"]
        };

        json = JsonSerializer.Serialize(state, SerializerOptions);
        return json;
    }

    internal static State? Restore(string? json, DateTime todayUtc, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var state = JsonSerializer.Deserialize<State>(json, SerializerOptions);

            if (state is null)
                return null;

            if (state.Version != PayloadVersion)
            {
                logger?.LogWarning(
                    "GenerationCheckpointStore: ignoring checkpoint with version {Found}, expected {Expected}",
                    state.Version, PayloadVersion);
                return null;
            }

            if (state.CycleDateUtc.Date != todayUtc.Date)
            {
                logger?.LogDebug(
                    "GenerationCheckpointStore: stale checkpoint from {CheckpointDate}, today is {Today}",
                    state.CycleDateUtc.Date, todayUtc.Date);
                return null;
            }

            return state;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "GenerationCheckpointStore: failed to deserialize checkpoint");
            return null;
        }
    }

    internal static HashSet<string> CompletedSymbolSet(State state)
        => new(state.CompletedSymbols, StringComparer.OrdinalIgnoreCase);
}
