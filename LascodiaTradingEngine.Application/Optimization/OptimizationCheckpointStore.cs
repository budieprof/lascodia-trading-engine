using System.Text.Json;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Models;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Owns checkpoint serialization, restoration, and payload bounding for optimization runs.
/// </summary>
internal static class OptimizationCheckpointStore
{
    internal const int PayloadVersion = 3;
    internal const int MaxCheckpointChars = 1_000_000;
    internal const int MaxMetadataChars = 32_000;
    internal const int MaxApprovalReportChars = 32_000;

    internal sealed record Observation(
        int Sequence,
        string ParamsJson,
        decimal HealthScore,
        double CvCoefficientOfVariation,
        BacktestResult Result);

    internal sealed record State(
        int Version,
        int Iterations,
        int StagnantBatches,
        string? SurrogateKind,
        ulong SurrogateRandomState,
        List<Observation> Observations,
        List<string> SeenParameterJson);

    internal static State Empty => new(PayloadVersion, 0, 0, null, 0UL, [], []);

    internal static string Serialize(
        int iterations,
        int stagnantBatches,
        string surrogateKind,
        ulong surrogateRandomState,
        IEnumerable<Observation> observations,
        IEnumerable<string> seenParameterJson,
        ILogger? logger = null)
    {
        var orderedObservations = observations
            .OrderBy(o => o.Sequence)
            .Select(o => o with
            {
                ParamsJson = CanonicalParameterJson.Normalize(o.ParamsJson),
                Result = ToCheckpointResult(o.Result)
            })
            .ToList();

        var orderedSeen = seenParameterJson
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(CanonicalParameterJson.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToList();

        var state = new State(
            PayloadVersion,
            iterations,
            stagnantBatches,
            surrogateKind,
            surrogateRandomState,
            orderedObservations,
            orderedSeen);

        string json = JsonSerializer.Serialize(state);
        if (json.Length <= MaxCheckpointChars)
            return json;

        logger?.LogWarning(
            "OptimizationCheckpointStore: checkpoint payload exceeded {Limit} chars ({Actual}); trimming seen parameter cache",
            MaxCheckpointChars, json.Length);

        state = state with { SeenParameterJson = [] };
        json = JsonSerializer.Serialize(state);
        if (json.Length <= MaxCheckpointChars)
            return json;

        logger?.LogWarning(
            "OptimizationCheckpointStore: checkpoint still exceeded {Limit} chars ({Actual}); dropping oldest observation tails",
            MaxCheckpointChars, json.Length);

        int keepCount = Math.Max(25, state.Observations.Count / 2);
        state = state with
        {
            Observations = state.Observations
                .OrderByDescending(o => o.Sequence)
                .Take(keepCount)
                .OrderBy(o => o.Sequence)
                .ToList()
        };

        json = JsonSerializer.Serialize(state);
        return json.Length <= MaxCheckpointChars
            ? json
            : JsonSerializer.Serialize(new State(
                PayloadVersion,
                iterations,
                stagnantBatches,
                surrogateKind,
                surrogateRandomState,
                state.Observations
                    .OrderByDescending(o => o.Sequence)
                    .Take(Math.Min(25, state.Observations.Count))
                    .OrderBy(o => o.Sequence)
                    .ToList(),
                []));
    }

    internal static State Restore(string? checkpointJson, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(checkpointJson))
            return Empty;

        try
        {
            var state = JsonSerializer.Deserialize<State>(checkpointJson);
            if (state is not null && state.Version == PayloadVersion)
            {
                return state with
                {
                    Observations = state.Observations
                        .OrderBy(o => o.Sequence)
                        .Select(o => o with
                        {
                            ParamsJson = CanonicalParameterJson.Normalize(o.ParamsJson),
                            Result = ToCheckpointResult(o.Result)
                        })
                        .ToList(),
                    SeenParameterJson = state.SeenParameterJson
                        .Select(CanonicalParameterJson.Normalize)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "OptimizationCheckpointStore: failed to parse v{Version} checkpoint", PayloadVersion);
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<LegacyState>(checkpointJson);
            if (legacy is null || legacy.Version != 2 || legacy.Candidates.Count == 0)
                return Empty;

            return new State(
                PayloadVersion,
                legacy.Iterations,
                0,
                null,
                0UL,
                legacy.Candidates
                    .Select((candidate, index) => new Observation(
                        index + 1,
                        CanonicalParameterJson.Normalize(candidate.ParamsJson),
                        candidate.HealthScore,
                        candidate.CvCoefficientOfVariation,
                        ToCheckpointResult(candidate.Result)))
                    .ToList(),
                legacy.Candidates
                    .Select(c => CanonicalParameterJson.Normalize(c.ParamsJson))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "OptimizationCheckpointStore: failed to parse legacy checkpoint");
            return Empty;
        }
    }

    internal static string LimitJsonPayload(string? json, int maxChars, string payloadName, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length <= maxChars)
            return json ?? string.Empty;

        logger?.LogWarning(
            "OptimizationCheckpointStore: truncating oversized {Payload} payload ({Actual} chars > {Limit})",
            payloadName, json.Length, maxChars);

        return JsonSerializer.Serialize(new
        {
            truncated = true,
            payload = payloadName,
            originalLength = json.Length
        });
    }

    internal static BacktestResult ToCheckpointResult(BacktestResult result) => result with { Trades = [] };

    private sealed record LegacyState(int Version, int Iterations, List<LegacyCandidate> Candidates);

    private sealed record LegacyCandidate(
        string ParamsJson,
        decimal HealthScore,
        double CvCoefficientOfVariation,
        BacktestResult Result);
}
