using System.Text.Json;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Owns checkpoint serialization and restoration for strategy generation cycles.
/// Allows the worker to resume from a partial cycle after a crash, skipping
/// already-screened symbols and restoring budget counters.
/// </summary>
public static class GenerationCheckpointStore
{
    internal const int PayloadVersion = 2;
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
        public string Fingerprint { get; init; } = string.Empty;
        public List<string> CompletedSymbols { get; init; } = [];
        public int CandidatesCreated { get; init; }
        public int ReserveCreated { get; init; }
        public int CandidatesScreened { get; init; }
        public int SymbolsProcessed { get; init; }
        public int SymbolsSkipped { get; init; }
        public List<PendingCandidateState> PendingCandidates { get; init; } = [];
        // String-keyed dicts for JSON serialization (enums/tuples don't round-trip in System.Text.Json)
        public Dictionary<string, int> CandidatesPerCurrency { get; init; } = new();
        public Dictionary<string, int> RegimeCandidatesCreated { get; init; } = new();
        public Dictionary<string, int> CorrelationGroupCounts { get; init; } = new();
    }

    public sealed record PendingTradeState
    {
        public decimal PnL { get; init; }
        public DateTime ExitTime { get; init; }
    }

    public sealed record PendingBacktestResultState
    {
        public int TotalTrades { get; init; }
        public decimal WinRate { get; init; }
        public decimal ProfitFactor { get; init; }
        public decimal MaxDrawdownPct { get; init; }
        public decimal SharpeRatio { get; init; }
        public List<PendingTradeState> Trades { get; init; } = [];

        public static PendingBacktestResultState FromResult(BacktestResult result) => new()
        {
            TotalTrades = result.TotalTrades,
            WinRate = result.WinRate,
            ProfitFactor = result.ProfitFactor,
            MaxDrawdownPct = result.MaxDrawdownPct,
            SharpeRatio = result.SharpeRatio,
            Trades = result.Trades
                .Select(t => new PendingTradeState { PnL = t.PnL, ExitTime = t.ExitTime })
                .ToList(),
        };

        public BacktestResult ToBacktestResult() => new()
        {
            TotalTrades = TotalTrades,
            WinRate = WinRate,
            ProfitFactor = ProfitFactor,
            MaxDrawdownPct = MaxDrawdownPct,
            SharpeRatio = SharpeRatio,
            Trades = Trades
                .Select(t => new BacktestTrade { PnL = t.PnL, ExitTime = t.ExitTime })
                .ToList(),
        };
    }

    public sealed record PendingCandidateState
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public StrategyType StrategyType { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public Timeframe Timeframe { get; init; }
        public string ParametersJson { get; init; } = "{}";
        public DateTime CreatedAt { get; init; }
        public string? ScreeningMetricsJson { get; init; }
        public string Regime { get; init; } = string.Empty;
        public PendingBacktestResultState TrainResult { get; init; } = new();
        public PendingBacktestResultState OosResult { get; init; } = new();

        public static PendingCandidateState FromOutcome(ScreeningOutcome outcome) => new()
        {
            Name = outcome.Strategy.Name,
            Description = outcome.Strategy.Description,
            StrategyType = outcome.Strategy.StrategyType,
            Symbol = outcome.Strategy.Symbol,
            Timeframe = outcome.Strategy.Timeframe,
            ParametersJson = outcome.Strategy.ParametersJson,
            CreatedAt = outcome.Strategy.CreatedAt,
            ScreeningMetricsJson = outcome.Strategy.ScreeningMetricsJson,
            Regime = outcome.Regime.ToString(),
            TrainResult = PendingBacktestResultState.FromResult(outcome.TrainResult),
            OosResult = PendingBacktestResultState.FromResult(outcome.OosResult),
        };

        public ScreeningOutcome ToOutcome()
        {
            var metrics = ScreeningMetrics.FromJson(ScreeningMetricsJson);
            var regimeText = !string.IsNullOrWhiteSpace(Regime) ? Regime : metrics?.Regime;
            var regime = Enum.TryParse<MarketRegimeEnum>(regimeText, out var parsedRegime)
                ? parsedRegime
                : default;

            return new ScreeningOutcome
            {
                Strategy = new Strategy
                {
                    Name = Name,
                    Description = Description,
                    StrategyType = StrategyType,
                    Symbol = Symbol,
                    Timeframe = Timeframe,
                    ParametersJson = ParametersJson,
                    CreatedAt = CreatedAt,
                    Status = StrategyStatus.Paused,
                    LifecycleStage = StrategyLifecycleStage.Draft,
                    ScreeningMetricsJson = ScreeningMetricsJson,
                },
                TrainResult = TrainResult.ToBacktestResult(),
                OosResult = OosResult.ToBacktestResult(),
                Regime = regime,
                ObservedRegime = Enum.TryParse<MarketRegimeEnum>(metrics?.ObservedRegime, out var observedRegime)
                    ? observedRegime
                    : regime,
                GenerationSource = metrics?.GenerationSource ?? "Primary",
                Metrics = metrics ?? new ScreeningMetrics { Regime = Regime },
            };
        }
    }

    internal sealed record SerializationResult(string Json, bool UsedRestartSafeFallback);

    internal static State Empty(DateTime cycleDate) => new() { CycleDateUtc = cycleDate };

    internal static string Serialize(State state, ILogger? logger = null)
    {
        return SerializeWithStatus(state, logger).Json;
    }

    internal static SerializationResult SerializeWithStatus(State state, ILogger? logger = null)
    {
        bool usedRestartSafeFallback = false;
        string json = JsonSerializer.Serialize(state, SerializerOptions);

        if (json.Length <= MaxCheckpointChars)
            return new SerializationResult(json, usedRestartSafeFallback);

        logger?.LogWarning(
            "GenerationCheckpointStore: checkpoint payload exceeded {Limit} chars ({Actual}); trimming CompletedSymbols",
            MaxCheckpointChars, json.Length);
        usedRestartSafeFallback = true;

        state = state with
        {
            CompletedSymbols = [$"[{state.CompletedSymbols.Count} symbols completed - trimmed to fit checkpoint limit]"]
        };

        json = JsonSerializer.Serialize(state, SerializerOptions);
        if (json.Length <= MaxCheckpointChars)
            return new SerializationResult(json, usedRestartSafeFallback);

        logger?.LogWarning(
            "GenerationCheckpointStore: checkpoint payload still exceeded {Limit} chars after trimming CompletedSymbols; trimming pending trade detail",
            MaxCheckpointChars);

        state = state with
        {
            PendingCandidates = state.PendingCandidates.Select(c => c with
            {
                TrainResult = c.TrainResult with { Trades = [] },
                OosResult = c.OosResult with { Trades = [] },
            }).ToList(),
        };

        json = JsonSerializer.Serialize(state, SerializerOptions);
        return new SerializationResult(json, usedRestartSafeFallback);
    }

    internal static State? Restore(string? json, DateTime todayUtc, string? expectedFingerprint = null, ILogger? logger = null)
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

            if (!string.IsNullOrWhiteSpace(expectedFingerprint)
                && !string.Equals(state.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                logger?.LogWarning(
                    "GenerationCheckpointStore: ignoring checkpoint with mismatched fingerprint");
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
