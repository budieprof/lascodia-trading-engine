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
        List<string> SeenParameterJson,
        string? DataWindowFingerprint = null,
        DateTime? CandleWindowStartUtc = null,
        DateTime? CandleWindowEndUtc = null,
        int? CandleCount = null,
        int? TrainCandleCount = null,
        int? TestCandleCount = null,
        string? OptimizationRegimeText = null);

    internal static State Empty => new(PayloadVersion, 0, 0, null, 0UL, [], []);

    internal static string Serialize(
        int iterations,
        int stagnantBatches,
        string surrogateKind,
        ulong surrogateRandomState,
        IEnumerable<Observation> observations,
        IEnumerable<string> seenParameterJson,
        ILogger? logger = null)
        => Serialize(
            iterations,
            stagnantBatches,
            surrogateKind,
            surrogateRandomState,
            observations,
            seenParameterJson,
            dataWindowFingerprint: null,
            candleWindowStartUtc: null,
            candleWindowEndUtc: null,
            candleCount: null,
            trainCandleCount: null,
            testCandleCount: null,
            optimizationRegimeText: null,
            logger);

    internal static string Serialize(
        int iterations,
        int stagnantBatches,
        string surrogateKind,
        ulong surrogateRandomState,
        IEnumerable<Observation> observations,
        IEnumerable<string> seenParameterJson,
        string? dataWindowFingerprint,
        DateTime? candleWindowStartUtc,
        DateTime? candleWindowEndUtc,
        int? candleCount,
        int? trainCandleCount,
        int? testCandleCount,
        string? optimizationRegimeText,
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
            orderedSeen,
            dataWindowFingerprint,
            candleWindowStartUtc,
            candleWindowEndUtc,
            candleCount,
            trainCandleCount,
            testCandleCount,
            optimizationRegimeText);

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
        var trimmedObservations = state.Observations
            .OrderByDescending(o => o.Sequence)
            .Take(keepCount)
            .OrderBy(o => o.Sequence)
            .Select((o, idx) => o with { Sequence = idx + 1 }) // Re-index to close gaps
            .ToList();
        state = state with { Observations = trimmedObservations };

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
                    .Select((o, idx) => o with { Sequence = idx + 1 }) // Re-index to close gaps
                    .ToList(),
                [],
                dataWindowFingerprint,
                candleWindowStartUtc,
                candleWindowEndUtc,
                candleCount,
                trainCandleCount,
                testCandleCount,
                optimizationRegimeText));
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

        // Preserve as much of the original content as possible by truncating instead
        // of replacing entirely. Keep the first portion up to the limit so that key
        // decision fields (scores, gate results) are preserved for auditability.
        string truncatedContent = json[..Math.Min(maxChars - 200, json.Length)];
        return JsonSerializer.Serialize(new
        {
            truncated = true,
            payload = payloadName,
            originalLength = json.Length,
            partialContent = truncatedContent
        });
    }

    internal static BacktestResult ToCheckpointResult(BacktestResult result) => result with { Trades = [] };

    internal static bool TryValidateCompatibility(
        State checkpoint,
        string currentDataWindowFingerprint,
        DateTime candleWindowStartUtc,
        DateTime candleWindowEndUtc,
        int candleCount,
        int trainCandleCount,
        int testCandleCount,
        string? optimizationRegimeText,
        out string? mismatchReason,
        string? currentSurrogateKind = null)
    {
        mismatchReason = null;

        if (checkpoint == Empty || checkpoint.Observations.Count == 0)
            return true;

        if (!string.IsNullOrWhiteSpace(currentSurrogateKind)
            && !string.IsNullOrWhiteSpace(checkpoint.SurrogateKind)
            && !string.Equals(checkpoint.SurrogateKind, currentSurrogateKind, StringComparison.OrdinalIgnoreCase))
        {
            mismatchReason = $"surrogate kind changed from {checkpoint.SurrogateKind} to {currentSurrogateKind}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(checkpoint.DataWindowFingerprint)
            && !string.Equals(checkpoint.DataWindowFingerprint, currentDataWindowFingerprint, StringComparison.Ordinal))
        {
            mismatchReason = "data window fingerprint changed";
            return false;
        }

        if (checkpoint.CandleWindowStartUtc.HasValue && checkpoint.CandleWindowStartUtc.Value != candleWindowStartUtc)
        {
            mismatchReason = "candle window start changed";
            return false;
        }

        if (checkpoint.CandleWindowEndUtc.HasValue && checkpoint.CandleWindowEndUtc.Value != candleWindowEndUtc)
        {
            mismatchReason = "candle window end changed";
            return false;
        }

        if (checkpoint.CandleCount.HasValue && checkpoint.CandleCount.Value != candleCount)
        {
            mismatchReason = "candle count changed";
            return false;
        }

        if (checkpoint.TrainCandleCount.HasValue && checkpoint.TrainCandleCount.Value != trainCandleCount)
        {
            mismatchReason = "train candle count changed";
            return false;
        }

        if (checkpoint.TestCandleCount.HasValue && checkpoint.TestCandleCount.Value != testCandleCount)
        {
            mismatchReason = "test candle count changed";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(checkpoint.OptimizationRegimeText)
            && !string.Equals(checkpoint.OptimizationRegimeText, optimizationRegimeText, StringComparison.OrdinalIgnoreCase))
        {
            mismatchReason = "optimization regime changed";
            return false;
        }

        return true;
    }

    private sealed record LegacyState(int Version, int Iterations, List<LegacyCandidate> Candidates);

    private sealed record LegacyCandidate(
        string ParamsJson,
        decimal HealthScore,
        double CvCoefficientOfVariation,
        BacktestResult Result);
}
