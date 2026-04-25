using System.Collections.Concurrent;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Feeds screening failures back into the candidate generation loop as a TPE surrogate.
///
/// <para>
/// Every rejected candidate is logged to <c>DecisionLog</c> with the in-sample health
/// metrics and its parameters. At the start of each generation cycle, this service loads
/// the last 30 days of screening observations, groups them by (strategy type × symbol ×
/// timeframe, regime), fits a <see cref="TreeParzenEstimator"/> per group, and caches N proposals
/// keyed by (type, sym, tf, regime). The primary screening planner reads proposals via
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
/// Mixed parameter payloads are supported: numeric fields are optimized with TPE while
/// categorical fields such as CorrelatedSymbol, Mode, and ModelPreference are replayed from
/// the best observed winners or near misses for that parameter shape.
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
            ["LookbackBars"]          = (2, 20, true),
            ["MomentumAtrThreshold"]  = (0.5, 3.0, false),
            ["MonthEndBusinessDays"]  = (1, 5, true),
            ["OverlapStartHourUtc"]   = (10, 15, true),
            ["OverlapEndHourUtc"]     = (14, 20, true),
        },
        [StrategyType.StatisticalArbitrage] = new()
        {
            ["LookbackPeriod"]          = (20, 240, true),
            ["ZScoreEntry"]             = (1.0, 4.0, false),
            ["ZScoreExit"]              = (0.1, 1.5, false),
            ["StopLossAtrMultiplier"]   = (0.8, 5.0, false),
            ["TakeProfitAtrMultiplier"] = (0.8, 6.0, false),
            ["AtrPeriod"]               = (5, 50, true),
        },
        [StrategyType.VwapReversion] = new()
        {
            ["SessionStartHour"]        = (0, 23, true),
            ["SessionEndHour"]          = (0, 23, true),
            ["EntryAtrThreshold"]       = (0.3, 4.0, false),
            ["StopLossAtrMultiplier"]   = (0.8, 5.0, false),
            ["TakeProfitAtrMultiplier"] = (0.5, 6.0, false),
            ["AtrPeriod"]               = (5, 50, true),
            ["MaxAdx"]                  = (10, 70, true),
            ["MinVolumeRatio"]          = (0.5, 3.0, false),
        },
        [StrategyType.CompositeML] = new()
        {
            ["ConfidenceThreshold"]      = (0.50, 0.90, false),
            ["StopLossAtrMultiplier"]   = (0.8, 5.0, false),
            ["TakeProfitAtrMultiplier"] = (0.8, 6.0, false),
            ["AtrPeriod"]               = (5, 50, true),
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
    /// (type, symbol, timeframe, regime) key, and caches N proposals per key. Safe to call once
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
                         && d.Outcome != "Pruned"
                         && d.CreatedAt >= cutoff
                         && d.ContextJson != null)
                .Select(d => new AuditObservation(d.ContextJson!, d.Outcome))
                .ToListAsync(ct);

            var newCache = new Dictionary<string, IReadOnlyList<string>>();
            int typeGroups = 0;
            int totalProposals = 0;

            foreach (var group in ParseObservations(logs))
            {
                Bounds.TryGetValue(group.StrategyType, out var baseBounds);
                var bounds = ResolveBoundsForContext(group, baseBounds);
                var proposals = FitAndPropose(group, bounds);
                if (proposals.Count == 0) continue;

                string cacheKey = BuildCacheKey(group.StrategyType, group.Symbol, group.Timeframe, group.Regime);
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
    /// for the given (type, symbol, timeframe, regime). Falls back to the broad
    /// type/symbol/timeframe surrogate when an exact regime-specific model is not ready.
    /// </summary>
    public IReadOnlyList<string> GetProposals(
        StrategyType strategyType,
        string symbol,
        Timeframe timeframe,
        MarketRegimeEnum? regime,
        int count)
    {
        if (count <= 0) return Array.Empty<string>();
        if (regime.HasValue)
        {
            string regimeKey = BuildCacheKey(strategyType, symbol, timeframe, regime);
            if (_proposalsCache.TryGetValue(regimeKey, out var exactProposals) && exactProposals.Count > 0)
                return exactProposals.Count <= count ? exactProposals : exactProposals.Take(count).ToList();
        }

        string cacheKey = BuildCacheKey(strategyType, symbol, timeframe, null);
        if (!_proposalsCache.TryGetValue(cacheKey, out var proposals) || proposals.Count == 0)
            return Array.Empty<string>();
        return proposals.Count <= count ? proposals : proposals.Take(count).ToList();
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private static string BuildCacheKey(StrategyType type, string symbol, Timeframe tf, MarketRegimeEnum? regime)
        => $"{type}|{symbol.ToUpperInvariant()}|{tf}|{(regime.HasValue ? regime.Value.ToString() : "AnyRegime")}";

    private sealed class ObservationGroup
    {
        public StrategyType StrategyType;
        public string Symbol = "";
        public Timeframe Timeframe;
        public MarketRegimeEnum? Regime;
        public List<Observation> Observations = [];
    }

    private sealed record AuditObservation(string ContextJson, string Outcome);

    private sealed record Observation(
        Dictionary<string, double> NumericParams,
        Dictionary<string, JsonElement> CategoricalParams,
        double Score,
        bool IsNearMiss,
        bool IsCreated);

    private static IEnumerable<ObservationGroup> ParseObservations(IEnumerable<AuditObservation> auditRows)
    {
        var byKey = new Dictionary<(StrategyType, string, Timeframe, MarketRegimeEnum?), ObservationGroup>();

        foreach (var row in auditRows)
        {
            try
            {
                using var doc = JsonDocument.Parse(row.ContextJson);
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
                MarketRegimeEnum? regime = null;
                if (root.TryGetProperty("regime", out var regimeProp)
                    && Enum.TryParse<MarketRegimeEnum>(regimeProp.GetString(), ignoreCase: true, out var parsedRegime))
                {
                    regime = parsedRegime;
                }

                // Prefer the composite quality score emitted by ScreeningAuditLogger because
                // it includes OOS robustness and drawdown, then fall back to older IS-Sharpe
                // audit rows produced before the richer failure payload existed.
                double? score = TryGetDouble(root, "qualityScore");
                if (score is null)
                {
                    double? isSharpe = TryGetDouble(root, "isSharpeRatio");
                    double? oosSharpe = TryGetDouble(root, "oosSharpeRatio");
                    if (isSharpe is null && oosSharpe is null)
                        continue;

                    score = (isSharpe ?? 0) * 0.75 + (oosSharpe ?? isSharpe ?? 0) * 1.25;
                }

                bool isNearMiss = root.TryGetProperty("isNearMiss", out var nearMissProp)
                    && nearMissProp.ValueKind is JsonValueKind.True;
                bool isCreated = string.Equals(row.Outcome, "Created", StringComparison.OrdinalIgnoreCase);
                if (isCreated)
                    score += 1.00;
                else if (isNearMiss)
                    score += 0.25;

                var paramSnapshot = ParseParams(pJson.GetString()!);
                if (paramSnapshot is null
                    || (paramSnapshot.NumericParams.Count == 0 && paramSnapshot.CategoricalParams.Count == 0))
                    continue;

                AddObservation(regime);
                if (regime.HasValue)
                    AddObservation(null);

                void AddObservation(MarketRegimeEnum? groupRegime)
                {
                    var key = (strategyType, symbol.ToUpperInvariant(), tf, groupRegime);
                    if (!byKey.TryGetValue(key, out var group))
                    {
                        group = new ObservationGroup
                        {
                            StrategyType = strategyType,
                            Symbol = symbol,
                            Timeframe = tf,
                            Regime = groupRegime,
                        };
                        byKey[key] = group;
                    }

                    group.Observations.Add(new Observation(
                        new Dictionary<string, double>(paramSnapshot.NumericParams),
                        CloneCategoricalParams(paramSnapshot.CategoricalParams),
                        score.Value,
                        isNearMiss,
                        isCreated));
                }
            }
            catch
            {
                // Malformed entry — skip.
            }
        }

        return byKey.Values;
    }

    private static double? TryGetDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var value)
            ? value
            : null;
    }

    private sealed record ParsedParameters(
        Dictionary<string, double> NumericParams,
        Dictionary<string, JsonElement> CategoricalParams);

    private static ParsedParameters? ParseParams(string paramsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var numeric = new Dictionary<string, double>();
            var categorical = new Dictionary<string, JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var number) && double.IsFinite(number))
                    numeric[prop.Name] = number;
                else if (prop.Value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                    categorical[prop.Name] = prop.Value.Clone();
            }
            return new ParsedParameters(numeric, categorical);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, JsonElement> CloneCategoricalParams(IReadOnlyDictionary<string, JsonElement> source)
        => source.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal);

    private List<string> FitAndPropose(
        ObservationGroup group,
        Dictionary<string, (double Min, double Max, bool IsInteger)> bounds)
    {
        if (group.Observations.Count == 0)
            return [];

        // Deterministic seed per key so proposals are stable within a cycle but vary across keys.
        int seed = BuildCacheKey(group.StrategyType, group.Symbol, group.Timeframe, group.Regime).GetHashCode();
        var results = new List<string>(ProposalsPerKey);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shapes = group.Observations
            .GroupBy(BuildParameterShapeKey)
            .OrderByDescending(shape => shape.Max(o => o.Score))
            .ThenByDescending(shape => shape.Count())
            .ToList();

        foreach (var shape in shapes)
        {
            if (results.Count >= ProposalsPerKey)
                break;

            var shapeObservations = shape
                .OrderByDescending(o => o.IsCreated)
                .ThenByDescending(o => o.IsNearMiss)
                .ThenByDescending(o => o.Score)
                .ToList();
            var numericKeys = shapeObservations
                .SelectMany(o => o.NumericParams.Keys)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var shapeBounds = bounds
                .Where(kv => numericKeys.Contains(kv.Key, StringComparer.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            IReadOnlyDictionary<string, JsonElement> exemplarCategorical = shapeObservations[0].CategoricalParams;
            if (shapeBounds.Count == 0)
            {
                AddProposal(MaterializeProposal(group.StrategyType, new Dictionary<string, double>(), shapeBounds, exemplarCategorical));
                continue;
            }

            var validObs = shapeObservations
                .Where(o => shapeBounds.Keys.All(k => o.NumericParams.ContainsKey(k)))
                .ToList();
            if (validObs.Count >= MinObservationsForSurrogate)
            {
                var tpe = new TreeParzenEstimator(shapeBounds, seed: seed + results.Count);
                foreach (var obs in validObs)
                    tpe.AddObservation(obs.NumericParams, obs.Score);

                var suggestions = tpe.SuggestCandidates(
                    count: ProposalsPerKey - results.Count,
                    minObservationsForModel: MinObservationsForSurrogate);
                foreach (var suggestion in suggestions)
                    AddProposal(MaterializeProposal(group.StrategyType, suggestion, shapeBounds, exemplarCategorical));
            }
            else
            {
                foreach (var proposal in ProposeFromAnchors(group, validObs, shapeBounds, seed + results.Count))
                    AddProposal(proposal);
            }
        }

        return results;

        void AddProposal(string proposal)
        {
            if (seen.Add(proposal))
                results.Add(proposal);
        }
    }

    private static Dictionary<string, (double Min, double Max, bool IsInteger)> ResolveBoundsForContext(
        ObservationGroup group,
        IReadOnlyDictionary<string, (double Min, double Max, bool IsInteger)>? baseBounds)
    {
        double timeframeScale = group.Timeframe switch
        {
            Timeframe.M1 or Timeframe.M5 => 0.55,
            Timeframe.M15 => 0.75,
            Timeframe.H1 => 1.00,
            Timeframe.H4 => 1.35,
            Timeframe.D1 => 1.75,
            _ => 1.00,
        };

        var numericKeys = group.Observations
            .SelectMany(o => o.NumericParams.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var resolved = new Dictionary<string, (double Min, double Max, bool IsInteger)>(StringComparer.Ordinal);
        foreach (var key in numericKeys)
        {
            var values = group.Observations
                .Where(o => o.NumericParams.TryGetValue(key, out var value) && double.IsFinite(value))
                .Select(o => o.NumericParams[key])
                .ToList();
            if (values.Count == 0)
                continue;

            var adjusted = baseBounds != null && baseBounds.TryGetValue(key, out var knownBound)
                ? knownBound
                : InferBoundsFromObservations(key, values);

            if (IsWindowParameter(key))
            {
                double originalMin = adjusted.Min;
                double originalMax = adjusted.Max;
                adjusted.Min = Math.Max(originalMin, Math.Round(originalMin * Math.Sqrt(timeframeScale), 2));
                double scaledMax = timeframeScale < 1.0
                    ? Math.Round(originalMax * timeframeScale, 2)
                    : originalMax;
                adjusted.Max = Math.Max(adjusted.Min + 1, scaledMax);
            }

            if (group.Regime is MarketRegimeEnum.HighVolatility or MarketRegimeEnum.Breakout
                && key.Contains("Buffer", StringComparison.OrdinalIgnoreCase))
            {
                adjusted.Min = Math.Max(adjusted.Min, adjusted.Min * 1.10);
            }

            resolved[key] = adjusted;
        }

        return resolved;
    }

    private static (double Min, double Max, bool IsInteger) InferBoundsFromObservations(string key, IReadOnlyList<double> values)
    {
        bool isInteger = values.All(v => Math.Abs(v - Math.Round(v)) < 1e-6) || IsIntegerParameterName(key);
        double min = values.Min();
        double max = values.Max();
        double span = max - min;
        if (span < 1e-6)
            span = Math.Max(isInteger ? 2.0 : 0.10, Math.Abs(max) * 0.25);

        double pad = Math.Max(span * 0.20, isInteger ? 1.0 : 0.01);
        min -= pad;
        max += pad;

        if (IsNonNegativeParameterName(key))
            min = Math.Max(0.0, min);

        if (key.Contains("Hour", StringComparison.OrdinalIgnoreCase))
        {
            min = Math.Clamp(min, 0, 23);
            max = Math.Clamp(max, Math.Max(min + 1, 1), 23);
            isInteger = true;
        }

        if (max <= min)
            max = min + (isInteger ? 1.0 : 0.01);

        return (min, max, isInteger);
    }

    private static bool IsIntegerParameterName(string key)
        => key.Contains("Period", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Lookback", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Bars", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Hour", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Days", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonNegativeParameterName(string key)
        => key.Contains("Period", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Lookback", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Bars", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Multiplier", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Ratio", StringComparison.OrdinalIgnoreCase)
        || key.Contains("ZScore", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Adx", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Minutes", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Days", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowParameter(string key)
        => key.Contains("Period", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Lookback", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Bars", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Fast", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Slow", StringComparison.OrdinalIgnoreCase)
        || key.Contains("TrendMa", StringComparison.OrdinalIgnoreCase);

    private static string BuildParameterShapeKey(Observation observation)
    {
        var numericKeys = string.Join(",", observation.NumericParams.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var categoricalPairs = observation.CategoricalParams
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value.GetRawText()}");
        return $"{numericKeys}|{string.Join(",", categoricalPairs)}";
    }

    private static List<string> ProposeFromAnchors(
        ObservationGroup group,
        IReadOnlyList<Observation> validObs,
        Dictionary<string, (double Min, double Max, bool IsInteger)> bounds,
        int seed)
    {
        var anchors = validObs
            .Where(o => o.IsCreated || o.IsNearMiss)
            .OrderByDescending(o => o.Score)
            .Take(ProposalsPerKey)
            .ToList();
        if (anchors.Count == 0)
            anchors = validObs
                .OrderByDescending(o => o.Score)
                .Take(ProposalsPerKey)
                .ToList();
        if (anchors.Count == 0)
            return [];

        var rng = new Random(seed);
        var results = new List<string>(anchors.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obs in anchors)
        {
            var suggestion = new Dictionary<string, double>(obs.NumericParams.Count);
            foreach (var (key, value) in obs.NumericParams)
            {
                if (!bounds.TryGetValue(key, out var bound))
                    continue;

                double width = Math.Max(1e-6, bound.Max - bound.Min);
                double jitter = (rng.NextDouble() * 2.0 - 1.0) * width * 0.08;
                suggestion[key] = value + jitter;
            }

            string json = MaterializeProposal(group.StrategyType, suggestion, bounds, obs.CategoricalParams);
            if (seen.Add(json))
                results.Add(json);
        }

        return results;
    }

    private static string MaterializeProposal(
        StrategyType strategyType,
        IReadOnlyDictionary<string, double> suggestion,
        Dictionary<string, (double Min, double Max, bool IsInteger)> bounds,
        IReadOnlyDictionary<string, JsonElement>? categoricalParams = null)
    {
        var repaired = ApplyParameterInvariants(strategyType, suggestion, bounds);
        var jsonObj = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        if (categoricalParams != null)
        {
            foreach (var (key, value) in categoricalParams)
                jsonObj[key] = value.Clone();
        }

        foreach (var (key, value) in repaired)
        {
            var (min, max, isInt) = bounds[key];
            double clamped = Math.Clamp(value, min, max);
            jsonObj[key] = isInt ? (object)(int)Math.Round(clamped) : Math.Round(clamped, 6);
        }

        return JsonSerializer.Serialize(jsonObj, JsonOpts);
    }

    private static Dictionary<string, double> ApplyParameterInvariants(
        StrategyType strategyType,
        IReadOnlyDictionary<string, double> suggestion,
        Dictionary<string, (double Min, double Max, bool IsInteger)> bounds)
    {
        var values = suggestion
            .Where(kv => bounds.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, bounds[kv.Key].Min, bounds[kv.Key].Max));

        void EnsureLessThan(string lowerKey, string upperKey, double gap)
        {
            if (!values.TryGetValue(lowerKey, out var lower) || !values.TryGetValue(upperKey, out var upper))
                return;

            if (lower + gap < upper)
                return;

            lower = Math.Clamp(lower, bounds[lowerKey].Min, bounds[lowerKey].Max);
            upper = Math.Clamp(Math.Max(upper, lower + gap), bounds[upperKey].Min, bounds[upperKey].Max);
            if (lower + gap >= upper)
                lower = Math.Clamp(upper - gap, bounds[lowerKey].Min, bounds[lowerKey].Max);

            values[lowerKey] = lower;
            values[upperKey] = upper;
        }

        switch (strategyType)
        {
            case StrategyType.MovingAverageCrossover:
                EnsureLessThan("FastPeriod", "SlowPeriod", 2);
                break;
            case StrategyType.MACDDivergence:
                EnsureLessThan("FastPeriod", "SlowPeriod", 2);
                break;
            case StrategyType.RSIReversion:
                EnsureLessThan("Oversold", "Overbought", 10);
                break;
            case StrategyType.MomentumTrend:
                EnsureLessThan("MomentumPeriod", "TrendMaPeriod", 5);
                break;
            case StrategyType.BreakoutScalper:
                if (values.TryGetValue("LookbackBars", out var lookback) && values.TryGetValue("ConfirmationBars", out var confirmation))
                    values["ConfirmationBars"] = Math.Min(confirmation, Math.Max(1, Math.Floor(lookback / 4.0)));
                break;
            case StrategyType.NewsFade:
                EnsureLessThan("MinMinutesSinceEvent", "MaxMinutesSinceEvent", 5);
                break;
            case StrategyType.SessionBreakout:
            case StrategyType.VwapReversion:
                if (values.TryGetValue("SessionStartHour", out var start)
                    && values.TryGetValue("SessionEndHour", out var end)
                    && Math.Round(start) == Math.Round(end))
                {
                    values["SessionEndHour"] = (Math.Round(end) + 1) % 24;
                }
                break;
            case StrategyType.StatisticalArbitrage:
                EnsureLessThan("ZScoreExit", "ZScoreEntry", 0.2);
                break;
            case StrategyType.CalendarEffect:
                EnsureLessThan("OverlapStartHourUtc", "OverlapEndHourUtc", 1);
                break;
        }

        return values;
    }
}

public interface IScreeningSurrogateService
{
    /// <summary>Fits TPE surrogates from recent DecisionLog observations and caches proposals.</summary>
    Task WarmupAsync(DbContext readDb, CancellationToken ct);

    /// <summary>Returns cached surrogate proposals for a (type, symbol, timeframe, regime) key, or empty.</summary>
    IReadOnlyList<string> GetProposals(
        StrategyType strategyType,
        string symbol,
        Timeframe timeframe,
        MarketRegimeEnum? regime,
        int count);
}
