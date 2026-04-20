using System.Collections.Concurrent;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Feeds screening failures back into the candidate generation loop as a TPE surrogate.
///
/// <para>
/// Every rejected candidate is logged to <c>DecisionLog</c> with the in-sample health
/// metrics and its parameters. At the start of each generation cycle, this service loads
/// the last 30 days of screening observations, groups them by (strategy type × symbol ×
/// timeframe), fits a <see cref="TreeParzenEstimator"/> per group, and caches N proposals
/// keyed by (type, sym, tf). The primary screening planner reads proposals via
/// <see cref="GetProposals"/> and mixes them into the candidate grid alongside static and
/// dynamic templates.
/// </para>
///
/// <para>
/// Unlike <see cref="StrategyGenerationDynamicTemplateRefreshService"/> — which only learns
/// from promoted winners — the surrogate learns from the full observation set including
/// losers, pulling the next cycle's search toward less-bad regions even when nothing has
/// ever passed.
/// </para>
///
/// <para>
/// Strategy types whose parameters include non-numeric fields (StatisticalArbitrage,
/// CompositeML — CorrelatedSymbol/ModelPreference are strings) are not surrogate-proposed
/// and fall back to the static template list.
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IScreeningSurrogateService))]
public sealed class ScreeningSurrogateService : IScreeningSurrogateService
{
    // Hardcoded bounds per strategy type. These define the surrogate's search space.
    // Values outside these bounds are still accessible via the static template pool.
    private static readonly Dictionary<StrategyType, Dictionary<string, (double Min, double Max, bool IsInteger)>> Bounds = new()
    {
        [StrategyType.MovingAverageCrossover] = new()
        {
            ["FastPeriod"] = (3, 80, true),
            ["SlowPeriod"] = (10, 300, true),
        },
        [StrategyType.RSIReversion] = new()
        {
            ["Period"]     = (5, 40, true),
            ["Oversold"]   = (10, 45, true),
            ["Overbought"] = (55, 90, true),
        },
        [StrategyType.BreakoutScalper] = new()
        {
            ["LookbackBars"]     = (5, 150, true),
            ["ConfirmationBars"] = (1, 5,   true),
        },
        [StrategyType.BollingerBandReversion] = new()
        {
            ["Period"]           = (8, 60,  true),
            ["StdDevMultiplier"] = (1.0, 3.5, false),
        },
        [StrategyType.MACDDivergence] = new()
        {
            ["FastPeriod"]   = (3, 30, true),
            ["SlowPeriod"]   = (10, 60, true),
            ["SignalPeriod"] = (3, 20, true),
        },
        [StrategyType.SessionBreakout] = new()
        {
            ["SessionStartHour"]    = (0, 23, true),
            ["SessionEndHour"]      = (0, 23, true),
            ["BreakoutBufferPips"]  = (1, 15, true),
        },
        [StrategyType.MomentumTrend] = new()
        {
            ["MomentumPeriod"] = (5, 40,  true),
            ["TrendMaPeriod"]  = (15, 250, true),
        },
        [StrategyType.CarryTrade] = new()
        {
            ["MinCarryStrength"]   = (0.3, 3.0, false),
            ["HorizonMultiplier"]  = (0.5, 5.0, false),
        },
        [StrategyType.NewsFade] = new()
        {
            ["MinMinutesSinceEvent"]  = (0, 30, true),
            ["MaxMinutesSinceEvent"]  = (10, 120, true),
            ["MomentumAtrThreshold"]  = (0.3, 3.0, false),
        },
        [StrategyType.CalendarEffect] = new()
        {
            ["ModeId"]                = (0, 1, true),
            ["LookbackBars"]          = (2, 20, true),
            ["MomentumAtrThreshold"]  = (0.5, 3.0, false),
            ["MonthEndBusinessDays"]  = (1, 5, true),
            ["OverlapStartHourUtc"]   = (10, 15, true),
            ["OverlapEndHourUtc"]     = (14, 20, true),
        },
    };

    private const int MinObservationsForSurrogate = 5;
    private const int ProposalsPerKey = 3;
    private const int LookbackDays = 30;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<ScreeningSurrogateService> _logger;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _proposalsCache = new();
    private int _warmupRunCount;

    public ScreeningSurrogateService(ILogger<ScreeningSurrogateService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Loads recent screening observations from DecisionLog, fits a TPE surrogate per
    /// (type, symbol, timeframe) key, and caches N proposals per key. Safe to call once
    /// per cycle — subsequent calls overwrite the cache.
    /// </summary>
    public async Task WarmupAsync(DbContext readDb, CancellationToken ct)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-LookbackDays);
            var logs = await readDb.Set<DecisionLog>()
                .AsNoTracking()
                .Where(d => d.Source == "StrategyGenerationWorker"
                         && d.DecisionType == "StrategyGeneration"
                         && (d.Outcome == "ScreeningFailed" || d.Outcome == "ZeroTradesIS")
                         && d.CreatedAt >= cutoff
                         && d.ContextJson != null)
                .Select(d => d.ContextJson!)
                .ToListAsync(ct);

            var newCache = new Dictionary<string, IReadOnlyList<string>>();
            int typeGroups = 0;
            int totalProposals = 0;

            foreach (var group in ParseObservations(logs))
            {
                if (!Bounds.TryGetValue(group.StrategyType, out var bounds))
                    continue; // Unsupported type (string params)
                if (group.Observations.Count < MinObservationsForSurrogate)
                    continue;

                var proposals = FitAndPropose(group, bounds);
                if (proposals.Count == 0) continue;

                string cacheKey = BuildCacheKey(group.StrategyType, group.Symbol, group.Timeframe);
                newCache[cacheKey] = proposals;
                typeGroups++;
                totalProposals += proposals.Count;
            }

            // Atomic swap — readers see either old cache or new, never a partial state.
            _proposalsCache.Clear();
            foreach (var kv in newCache)
                _proposalsCache[kv.Key] = kv.Value;

            Interlocked.Increment(ref _warmupRunCount);
            _logger.LogInformation(
                "ScreeningSurrogateService: warmup #{Run} — fit {Groups} TPE surrogates from {Logs} observations, cached {Proposals} proposals",
                _warmupRunCount, typeGroups, logs.Count, totalProposals);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ScreeningSurrogateService: warmup failed — falling back to static templates only");
        }
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> surrogate-proposed parameter JSON strings
    /// for the given (type, symbol, timeframe). Returns an empty list when no surrogate
    /// was fit for this key — caller must fall back to static/dynamic templates.
    /// </summary>
    public IReadOnlyList<string> GetProposals(StrategyType strategyType, string symbol, Timeframe timeframe, int count)
    {
        if (count <= 0) return Array.Empty<string>();
        string cacheKey = BuildCacheKey(strategyType, symbol, timeframe);
        if (!_proposalsCache.TryGetValue(cacheKey, out var proposals) || proposals.Count == 0)
            return Array.Empty<string>();
        return proposals.Count <= count ? proposals : proposals.Take(count).ToList();
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private static string BuildCacheKey(StrategyType type, string symbol, Timeframe tf)
        => $"{type}|{symbol.ToUpperInvariant()}|{tf}";

    private sealed class ObservationGroup
    {
        public StrategyType StrategyType;
        public string Symbol = "";
        public Timeframe Timeframe;
        public List<(Dictionary<string, double> Params, double Score)> Observations = [];
    }

    private static IEnumerable<ObservationGroup> ParseObservations(IEnumerable<string> contextJsons)
    {
        var byKey = new Dictionary<(StrategyType, string, Timeframe), ObservationGroup>();

        foreach (var json in contextJsons)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("strategyType", out var stProp) ||
                    !Enum.TryParse<StrategyType>(stProp.GetString(), ignoreCase: true, out var strategyType))
                    continue;
                if (!root.TryGetProperty("symbol", out var symProp) || symProp.GetString() is not string symbol)
                    continue;
                if (!root.TryGetProperty("timeframe", out var tfProp) ||
                    !Enum.TryParse<Timeframe>(tfProp.GetString(), ignoreCase: true, out var tf))
                    continue;
                if (!root.TryGetProperty("paramsJson", out var pJson) || pJson.ValueKind != JsonValueKind.String)
                    continue;

                // Score by Sharpe (ranks correctly even when all negative; higher = better).
                // Fall back to a composite if Sharpe is missing.
                double? sharpe = root.TryGetProperty("isSharpeRatio", out var shProp) && shProp.ValueKind == JsonValueKind.Number
                    ? shProp.GetDouble()
                    : null;
                if (sharpe is null) continue;

                var paramDict = ParseParamsToNumericDict(pJson.GetString()!);
                if (paramDict is null || paramDict.Count == 0) continue;

                var key = (strategyType, symbol.ToUpperInvariant(), tf);
                if (!byKey.TryGetValue(key, out var group))
                {
                    group = new ObservationGroup
                    {
                        StrategyType = strategyType,
                        Symbol = symbol,
                        Timeframe = tf,
                    };
                    byKey[key] = group;
                }
                group.Observations.Add((paramDict, sharpe.Value));
            }
            catch
            {
                // Malformed entry — skip.
            }
        }

        return byKey.Values;
    }

    private static Dictionary<string, double>? ParseParamsToNumericDict(string paramsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            var result = new Dictionary<string, double>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    result[prop.Name] = prop.Value.GetDouble();
                else if (prop.Value.ValueKind == JsonValueKind.String)
                    return null; // Type contains non-numeric param — reject
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private List<string> FitAndPropose(
        ObservationGroup group,
        Dictionary<string, (double Min, double Max, bool IsInteger)> bounds)
    {
        // Only keep observations whose params contain ALL bound keys — skip partials.
        var validObs = group.Observations
            .Where(o => bounds.Keys.All(k => o.Params.ContainsKey(k)))
            .ToList();
        if (validObs.Count < MinObservationsForSurrogate) return [];

        // Deterministic seed per key so proposals are stable within a cycle but vary across keys.
        int seed = BuildCacheKey(group.StrategyType, group.Symbol, group.Timeframe).GetHashCode();
        var tpe = new TreeParzenEstimator(bounds, seed: seed);
        foreach (var (p, s) in validObs)
            tpe.AddObservation(p, s);

        var suggestions = tpe.SuggestCandidates(count: ProposalsPerKey, minObservationsForModel: MinObservationsForSurrogate);
        var results = new List<string>(suggestions.Count);
        foreach (var s in suggestions)
        {
            var jsonObj = new Dictionary<string, object>(s.Count);
            foreach (var kv in s)
            {
                var (min, max, isInt) = bounds[kv.Key];
                double clamped = Math.Clamp(kv.Value, min, max);
                jsonObj[kv.Key] = isInt ? (object)(int)Math.Round(clamped) : clamped;
            }
            results.Add(JsonSerializer.Serialize(jsonObj, JsonOpts));
        }
        return results;
    }
}

public interface IScreeningSurrogateService
{
    /// <summary>Fits TPE surrogates from recent DecisionLog observations and caches proposals.</summary>
    Task WarmupAsync(DbContext readDb, CancellationToken ct);

    /// <summary>Returns cached surrogate proposals for a (type, symbol, timeframe) key, or empty.</summary>
    IReadOnlyList<string> GetProposals(StrategyType strategyType, string symbol, Timeframe timeframe, int count);
}
