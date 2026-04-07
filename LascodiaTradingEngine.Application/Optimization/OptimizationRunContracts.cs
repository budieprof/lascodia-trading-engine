using System.Text.Json;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationRunContracts
{
    public const int LegacyConfigSnapshotVersion = 1;
    public const int ConfigSnapshotVersion = 2;
    public const int RunMetadataVersion = 1;

    public sealed record ConfigSnapshotContract(
        int Version,
        OptimizationConfig Config);

    public sealed record RunMetadataSnapshot
    {
        public int Version { get; init; }
        public int DeterministicSeed { get; init; }
        public string Surrogate { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public Timeframe Timeframe { get; init; }
        public DateTime CandleFromUtc { get; init; }
        public DateTime CandleToUtc { get; init; }
        public int CandleCount { get; init; }
        public int TrainCandles { get; init; }
        public int TestCandles { get; init; }
        public string DataWindowFingerprint { get; init; } = string.Empty;
        public int EmbargoCandles { get; init; }
        public bool ResumedFromCheckpoint { get; init; }
        public string? CurrentRegime { get; init; }
        public string? OptimizationRegime { get; init; }
        public string? PersistenceRegime { get; init; }
        public bool BaselineRegimeParamsUsed { get; init; }
        public string RecoveryModeUsed { get; init; } = "fresh_start";
        public int WarmStartedObservations { get; init; }
        public int Iterations { get; init; }
        public decimal? BaselineHealthScore { get; init; }
        public decimal? BaselineComparisonScore { get; init; }
        public decimal? OosHealthScore { get; init; }
        public bool? AutoApproved { get; init; }
        public SearchExecutionSummary? SearchSummary { get; init; }
        public string? SearchAbortReason { get; init; }
    }

    public static string SerializeConfigSnapshot(OptimizationConfig config)
        => JsonSerializer.Serialize(new ConfigSnapshotContract(ConfigSnapshotVersion, config));

    public static bool TryDeserializeConfigSnapshot(OptimizationRun run, out OptimizationConfig config)
        => TryDeserializeConfigSnapshot(run.ConfigSnapshotJson, out config);

    public static bool TryDeserializeConfigSnapshot(string? snapshotJson, out OptimizationConfig config)
    {
        config = null!;
        if (string.IsNullOrWhiteSpace(snapshotJson))
            return false;

        try
        {
            using var snapshotDoc = JsonDocument.Parse(snapshotJson);
            if (!snapshotDoc.RootElement.TryGetProperty("Config", out var configElement))
                return false;

            int version = snapshotDoc.RootElement.TryGetProperty("Version", out var versionElement)
                && versionElement.TryGetInt32(out int parsedVersion)
                ? parsedVersion
                : 0;

            if (version is LegacyConfigSnapshotVersion or ConfigSnapshotVersion)
            {
                var parsedConfig = configElement.Deserialize<OptimizationConfig>();
                if (parsedConfig is not null)
                {
                    config = parsedConfig;
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    public static string SerializeRunMetadata(
        RunMetadataSnapshot snapshot,
        ILogger? logger = null)
        => OptimizationCheckpointStore.LimitJsonPayload(
            JsonSerializer.Serialize(snapshot with { Version = RunMetadataVersion }),
            OptimizationCheckpointStore.MaxMetadataChars,
            "run metadata",
            logger);

    public static string? ExtractOptimizationRegime(string? runMetadataJson)
    {
        if (string.IsNullOrWhiteSpace(runMetadataJson))
            return null;

        try
        {
            using var metaDoc = JsonDocument.Parse(runMetadataJson);
            if (metaDoc.RootElement.TryGetProperty("OptimizationRegime", out var optimizationRegimeElement))
            {
                var optimizationRegime = optimizationRegimeElement.GetString();
                if (!string.IsNullOrWhiteSpace(optimizationRegime))
                    return optimizationRegime;
            }

            if (metaDoc.RootElement.TryGetProperty("CurrentRegime", out var currentRegimeElement))
                return currentRegimeElement.GetString();
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
