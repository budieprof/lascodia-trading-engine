using System.Globalization;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static LascodiaTradingEngine.Application.StrategyGeneration.StrategyGenerationHelpers;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Loads hot-reloadable strategy-generation configuration from <see cref="EngineConfig"/>.
/// </summary>
public interface IStrategyGenerationConfigProvider
{
    Task<StrategyGenerationConfigurationSnapshot> LoadAsync(DbContext db, CancellationToken ct);
    Task<StrategyGenerationScheduleSettings> LoadScheduleAsync(DbContext db, CancellationToken ct);
    Task<StrategyGenerationFastTrackSettings> LoadFastTrackAsync(DbContext db, CancellationToken ct);
    (double? MinWinRate, double? MinProfitFactor, double? MinSharpe, double? MaxDrawdownPct) ExtractSymbolOverrides(
        IReadOnlyDictionary<string, string> allConfigs,
        string symbol);
    List<double> ParseWalkForwardSplitPcts(string raw);
}

/// <summary>
/// Lightweight scheduler settings used by the hosted worker's polling gate.
/// </summary>
public sealed record StrategyGenerationScheduleSettings(
    bool Enabled,
    int ScheduleHourUtc);

/// <summary>
/// Fast-track settings used to promote elite candidates in the validation queue.
/// </summary>
public sealed record StrategyGenerationFastTrackSettings(
    bool Enabled,
    double ThresholdMultiplier,
    double MinR2,
    double MaxMonteCarloPValue,
    int PriorityBoost);

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationConfigProvider))]
/// <summary>
/// Central configuration provider for the strategy-generation subsystem.
/// </summary>
/// <remarks>
/// This class normalizes raw engine-config values into a strongly typed
/// <see cref="GenerationConfig"/> snapshot and derives secondary helper settings such as
/// symbol overrides, scheduler configuration, and fast-track thresholds.
/// </remarks>
public sealed class StrategyGenerationConfigProvider : IStrategyGenerationConfigProvider
{
    private readonly ILogger<StrategyGenerationConfigProvider> _logger;

    public StrategyGenerationConfigProvider(ILogger<StrategyGenerationConfigProvider> logger)
    {
        _logger = logger;
    }

    public async Task<StrategyGenerationConfigurationSnapshot> LoadAsync(DbContext db, CancellationToken ct)
    {
        // Load the whole namespace once so downstream consumers can reuse the same consistent
        // snapshot rather than repeatedly re-querying individual keys.
        var allConfigs = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("StrategyGeneration:")
                     || c.Key.StartsWith("ScreeningGate:"))
            .ToDictionaryAsync(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase, ct);
        var symbolOverrides = ExtractAllSymbolOverrides(allConfigs);

        T Get<T>(string key, T defaultValue)
        {
            if (!allConfigs.TryGetValue(key, out var raw) || raw is null)
                return defaultValue;

            return TryConvertConfigValue(raw, defaultValue, out T parsed) ? parsed : defaultValue;
        }

        var config = new GenerationConfig
        {
            ScreeningMonths = Get("StrategyGeneration:ScreeningWindowMonths", 6),
            MinWinRate = Get("StrategyGeneration:MinWinRate", 0.60),
            MinProfitFactor = Get("StrategyGeneration:MinProfitFactor", 1.1),
            MinTotalTrades = Get("StrategyGeneration:MinTotalTrades", 15),
            MaxDrawdownPct = Get("StrategyGeneration:MaxDrawdownPct", 0.20),
            MinSharpe = Get("StrategyGeneration:MinSharpeRatio", 0.3),
            MaxCandidates = Get("StrategyGeneration:MaxCandidatesPerCycle", 50),
            MaxActivePerSymbol = Get("StrategyGeneration:MaxActiveStrategiesPerSymbol", 3),
            MaxActivePerTypePerSymbol = Get("StrategyGeneration:MaxActivePerTypePerSymbol", 2),
            PruneAfterFailed = Get("StrategyGeneration:PruneAfterFailedBacktests", 3),
            RegimeFreshnessHours = Get("StrategyGeneration:RegimeFreshnessHours", 48),
            RetryCooldownDays = Get("StrategyGeneration:RetryCooldownDays", 30),
            MaxPerCurrencyGroup = Get("StrategyGeneration:MaxCandidatesPerCurrencyGroup", 6),
            ScreeningSpreadPoints = Get("StrategyGeneration:ScreeningSpreadPoints", 20.0),
            ScreeningCommissionPerLot = Get("StrategyGeneration:ScreeningCommissionPerLot", 7.0),
            ScreeningSlippagePips = Get("StrategyGeneration:ScreeningSlippagePips", 1.0),
            // Default lowered from 0.60 to 0.30: the regime classifier typically returns
            // 0.34–0.55 confidence for most FX symbols in mixed / HighVolatility conditions,
            // so a 0.60 floor excludes 7 of 10 pairs and leaves the generation cycle working
            // the same 3 symbols every run. 0.30 lets the full pool flow through while still
            // filtering out regimes the detector is genuinely uncertain about. Operators can
            // retighten via the StrategyGeneration:MinRegimeConfidence hot-reload key.
            MinRegimeConfidence = Get("StrategyGeneration:MinRegimeConfidence", 0.30),
            MaxOosDegradationPct = Get("StrategyGeneration:MaxOosDegradationPct", 0.60),
            SuppressDuringDrawdownRecovery = Get("StrategyGeneration:SuppressDuringDrawdownRecovery", true),
            SeasonalBlackoutEnabled = Get("StrategyGeneration:SeasonalBlackoutEnabled", true),
            BlackoutPeriods = Get("StrategyGeneration:BlackoutPeriods", "12/20-01/05"),
            ScreeningTimeoutSeconds = Get("StrategyGeneration:ScreeningTimeoutSeconds", 30),
            // Cost-tier-aware default: generate candidates only on H4/D1 timeframes
            // where the typical move per bar comfortably exceeds retail broker spreads.
            // Past cycles on H1 showed Cost/AvgWin ratios of 3–7× — the template was
            // paying several dollars in spread for every dollar of edge. H4/D1 keeps
            // the ratio sub-1. Operators can reintroduce H1/M15 once a strategy type
            // demonstrates edge at higher TFs first, via the hot-reload config key.
            CandidateTimeframes = ParseTimeframes(Get("StrategyGeneration:CandidateTimeframes", "H4,D1")),
            MaxTemplatesPerCombo = Get("StrategyGeneration:MaxTemplatesPerCombo", 5),
            StrategicReserveQuota = Get("StrategyGeneration:StrategicReserveQuota", 3),
            MaxCandidatesPerWeek = Get("StrategyGeneration:MaxCandidatesPerWeek", 150),
            MaxSpreadToRangeRatio = Get("StrategyGeneration:MaxSpreadToRangeRatio", 0.30),
            ScreeningInitialBalance = Get("StrategyGeneration:ScreeningInitialBalance", 10_000m),
            MaxParallelBacktests = Get("StrategyGeneration:MaxParallelBacktests", 3),
            RegimeBudgetDiversityPct = Get("StrategyGeneration:RegimeBudgetDiversityPct", 0.60),
            MinEquityCurveR2 = Get("StrategyGeneration:MinEquityCurveR2", 0.70),
            MaxTradeTimeConcentration = Get("StrategyGeneration:MaxTradeTimeConcentration", 0.60),
            MaxCostToWinRatio = Get("StrategyGeneration:MaxCostToWinRatio", 0.35),
            CircuitBreakerMaxFailures = Get("StrategyGeneration:CircuitBreakerMaxFailures", 3),
            CircuitBreakerBackoffDays = Get("StrategyGeneration:CircuitBreakerBackoffDays", 2),
            MaxFaultsPerStrategyType = Get("StrategyGeneration:MaxFaultsPerStrategyType", 3),
            MaxCandleCacheSize = Get("StrategyGeneration:MaxCandleCacheSize", 500_000),
            CandleChunkSize = Get("StrategyGeneration:CandleChunkSize", 5),
            DataHealthMinCandles = Get("StrategyGeneration:DataHealthMinCandles", 100),
            MinDataHealthScore = Get("StrategyGeneration:MinDataHealthScore", 0.50),
            MaxCorrelatedCandidates = Get("StrategyGeneration:MaxCorrelatedCandidates", 4),
            AdaptiveThresholdsEnabled = Get("StrategyGeneration:AdaptiveThresholdsEnabled", true),
            AdaptiveThresholdsMinSamples = Get("StrategyGeneration:AdaptiveThresholdsMinSamples", 10),
            MinCandidatesPerArchetype = Get("StrategyGeneration:MinCandidatesPerArchetype", 2),
            EnforceArchetypeDiversity = Get("StrategyGeneration:EnforceArchetypeDiversity", true),
            ArchetypeReserveThresholdMultiplier = Get("StrategyGeneration:ArchetypeReserveThresholdMultiplier", 0.75),
            MonteCarloEnabled = Get("StrategyGeneration:MonteCarloEnabled", true),
            MonteCarloPermutations = Get("StrategyGeneration:MonteCarloPermutations", 500),
            MonteCarloMinPValue = Get("StrategyGeneration:MonteCarloMinPValue", 0.05),
            MonteCarloShuffleEnabled = Get("StrategyGeneration:MonteCarloShuffleEnabled", false),
            MonteCarloShufflePermutations = Get("StrategyGeneration:MonteCarloShufflePermutations", 0),
            MonteCarloShuffleMinPValue = Get("StrategyGeneration:MonteCarloShuffleMinPValue", 0.0),
            PortfolioBacktestEnabled = Get("StrategyGeneration:PortfolioBacktestEnabled", true),
            MaxPortfolioDrawdownPct = Get("StrategyGeneration:MaxPortfolioDrawdownPct", 0.30),
            PortfolioCorrelationWeight = Get("StrategyGeneration:PortfolioCorrelationWeight", 0.05),
            MaxPortfolioSymbolWeightPct = Get("StrategyGeneration:MaxPortfolioSymbolWeightPct", 0.35),
            MaxPortfolioCurrencyExposurePct = Get("StrategyGeneration:MaxPortfolioCurrencyExposurePct", 0.80),
            MaxCandleAgeHours = Get("StrategyGeneration:MaxCandleAgeHours", 72),
            SkipWeekends = Get("StrategyGeneration:SkipWeekends", true),
            BlackoutTimezone = Get("StrategyGeneration:BlackoutTimezone", "UTC"),
            RegimeTransitionCooldownHours = Get("StrategyGeneration:RegimeTransitionCooldownHours", 12),
            WalkForwardWindowCount = Get("StrategyGeneration:WalkForwardWindowCount", 3),
            WalkForwardMinWindowsPass = Get("StrategyGeneration:WalkForwardMinWindowsPass", 2),
            WalkForwardSplitPcts = Get("StrategyGeneration:WalkForwardSplitPcts", "0.40,0.55,0.70"),
            WalkForwardSplitPercentages = ParseWalkForwardSplitPcts(Get("StrategyGeneration:WalkForwardSplitPcts", "0.40,0.55,0.70")),
            WalkForwardEmbargoPct = Get("StrategyGeneration:WalkForwardEmbargoPct", 0.02),
            LookaheadAuditEnabled = Get("StrategyGeneration:LookaheadAuditEnabled", true),
            LookaheadAuditMaxTradeCountDelta = Get("StrategyGeneration:LookaheadAuditMaxTradeCountDelta", 0.50),
            LookaheadAuditMaxPnlDelta = Get("StrategyGeneration:LookaheadAuditMaxPnlDelta", 0.50),
            OosPfRelaxation = Get("ScreeningGate:OosPfRelaxation", 0.9),
            OosDdRelaxation = Get("ScreeningGate:OosDdRelaxation", 1.1),
            OosSharpeRelaxation = Get("ScreeningGate:OosSharpeRelaxation", 0.8),
            RegimeDegradationRelaxation = Get("ScreeningGate:RegimeDegradationRelaxation", 1.5),
            KellyFactor = Get("ScreeningGate:KellyFactor", 0.5m),
            KellyMinLot = Get("ScreeningGate:KellyMinLot", 0.01m),
            KellyMaxLot = Get("ScreeningGate:KellyMaxLot", 0.10m),
            MinDeflatedSharpe = Get("StrategyGeneration:MinDeflatedSharpe", 0.0),
            UseUcb1TemplateSelection = Get("StrategyGeneration:UseUcb1TemplateSelection", true),
            Ucb1ExplorationConstant = Get("StrategyGeneration:Ucb1ExplorationConstant", 1.41421356237),
        };

        // Validate first so operators get warnings against the raw settings before we clamp or
        // normalize them into safer runtime defaults.
        ValidateConfiguration(config);
        return new StrategyGenerationConfigurationSnapshot(NormalizeConfiguration(config), allConfigs, symbolOverrides);
    }

    public async Task<StrategyGenerationScheduleSettings> LoadScheduleAsync(DbContext db, CancellationToken ct)
    {
        var scheduleConfigs = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key == "StrategyGeneration:Enabled" || c.Key == "StrategyGeneration:ScheduleHourUtc")
            .ToDictionaryAsync(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase, ct);

        bool enabled = TryReadValue(scheduleConfigs, "StrategyGeneration:Enabled", true);
        int scheduleHour = Math.Clamp(TryReadValue(scheduleConfigs, "StrategyGeneration:ScheduleHourUtc", 2), 0, 23);
        return new StrategyGenerationScheduleSettings(enabled, scheduleHour);
    }

    public async Task<StrategyGenerationFastTrackSettings> LoadFastTrackAsync(DbContext db, CancellationToken ct)
    {
        var fastTrackConfigs = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("FastTrack:"))
            .ToDictionaryAsync(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase, ct);

        bool enabled = TryReadValue(fastTrackConfigs, "FastTrack:Enabled", false);
        double thresholdMultiplier = TryReadValue(fastTrackConfigs, "FastTrack:ThresholdMultiplier", 2.0);
        double minR2 = TryReadValue(fastTrackConfigs, "FastTrack:MinR2", 0.90);
        double maxMonteCarloPValue = TryReadValue(fastTrackConfigs, "FastTrack:MaxMonteCarloPValue", 0.01);
        int priorityBoost = Math.Max(100, TryReadValue(fastTrackConfigs, "FastTrack:PriorityBoost", 1_000));

        return new StrategyGenerationFastTrackSettings(
            enabled,
            thresholdMultiplier,
            minR2,
            maxMonteCarloPValue,
            priorityBoost);
    }

    public (double? MinWinRate, double? MinProfitFactor, double? MinSharpe, double? MaxDrawdownPct) ExtractSymbolOverrides(
        IReadOnlyDictionary<string, string> allConfigs,
        string symbol)
    {
        var prefix = $"StrategyGeneration:Overrides:{symbol}:";
        double? wr = null;
        double? pf = null;
        double? sh = null;
        double? dd = null;

        foreach (var (key, value) in allConfigs)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var suffix = key[prefix.Length..];
            if (suffix.Equals("MinWinRate", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v1))
                wr = v1;
            else if (suffix.Equals("MinProfitFactor", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v2))
                pf = v2;
            else if (suffix.Equals("MinSharpeRatio", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v3))
                sh = v3;
            else if (suffix.Equals("MaxDrawdownPct", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v4))
                dd = v4;
        }

        return (wr, pf, sh, dd);
    }

    public List<double> ParseWalkForwardSplitPcts(string raw)
    {
        // Require at least two valid splits; otherwise fall back to the engine defaults that
        // the screening pipeline and tests already expect.
        var result = new List<double>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) && val > 0 && val < 1)
                result.Add(val);
        }

        return result.Count >= 2 ? result : [0.40, 0.55, 0.70];
    }

    private void ValidateConfiguration(GenerationConfig config)
    {
        if (config.MinWinRate is <= 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationConfigProvider: MinWinRate={Value} is outside valid range (0, 1]", config.MinWinRate);
        if (config.MaxDrawdownPct is <= 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationConfigProvider: MaxDrawdownPct={Value} is outside valid range (0, 1]", config.MaxDrawdownPct);
        if (config.MinRegimeConfidence is < 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationConfigProvider: MinRegimeConfidence={Value} is outside valid range [0, 1]", config.MinRegimeConfidence);
        if (config.MaxOosDegradationPct is < 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationConfigProvider: MaxOosDegradationPct={Value} is outside valid range [0, 1]", config.MaxOosDegradationPct);
        if (config.RegimeBudgetDiversityPct is <= 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationConfigProvider: RegimeBudgetDiversityPct={Value} is outside valid range (0, 1]", config.RegimeBudgetDiversityPct);
        if (config.MinProfitFactor <= 0)
            _logger.LogWarning("StrategyGenerationConfigProvider: MinProfitFactor={Value} must be positive", config.MinProfitFactor);
        if (config.ScreeningInitialBalance <= 0)
            _logger.LogWarning("StrategyGenerationConfigProvider: ScreeningInitialBalance={Value} must be positive", config.ScreeningInitialBalance);
        if (config.MaxCandidates < config.StrategicReserveQuota)
            _logger.LogWarning(
                "StrategyGenerationConfigProvider: MaxCandidatesPerCycle ({Max}) < StrategicReserveQuota ({Reserve})",
                config.MaxCandidates,
                config.StrategicReserveQuota);
        if (config.MinDataHealthScore is < 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationConfigProvider: MinDataHealthScore={Value} is outside valid range [0, 1]", config.MinDataHealthScore);
        if (config.MaxCostToWinRatio <= 0)
            _logger.LogWarning("StrategyGenerationConfigProvider: MaxCostToWinRatio={Value} must be positive", config.MaxCostToWinRatio);
        if (config.MaxPortfolioSymbolWeightPct is <= 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationConfigProvider: MaxPortfolioSymbolWeightPct={Value} is outside valid range (0, 1]", config.MaxPortfolioSymbolWeightPct);
        if (config.MaxPortfolioCurrencyExposurePct is <= 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationConfigProvider: MaxPortfolioCurrencyExposurePct={Value} is outside valid range (0, 1]", config.MaxPortfolioCurrencyExposurePct);
        if (config.WalkForwardEmbargoPct is < 0 or > 0.25)
            _logger.LogWarning("StrategyGenerationConfigProvider: WalkForwardEmbargoPct={Value} is outside valid range [0, 0.25]", config.WalkForwardEmbargoPct);
        if (config.LookaheadAuditMaxTradeCountDelta is < 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationConfigProvider: LookaheadAuditMaxTradeCountDelta={Value} is outside valid range [0, 1]", config.LookaheadAuditMaxTradeCountDelta);
        if (config.LookaheadAuditMaxPnlDelta is < 0 or > 1.0)
            _logger.LogWarning("StrategyGenerationConfigProvider: LookaheadAuditMaxPnlDelta={Value} is outside valid range [0, 1]", config.LookaheadAuditMaxPnlDelta);
    }

    private GenerationConfig NormalizeConfiguration(GenerationConfig config) => config with
    {
        ScreeningMonths = Math.Max(1, config.ScreeningMonths),
        MinWinRate = Math.Clamp(config.MinWinRate, 0.01, 1.0),
        MinProfitFactor = Math.Max(0.01, config.MinProfitFactor),
        MinSharpe = Math.Max(0.0, config.MinSharpe),
        MinTotalTrades = Math.Max(1, config.MinTotalTrades),
        MaxDrawdownPct = Math.Clamp(config.MaxDrawdownPct, 0.01, 1.0),
        MinRegimeConfidence = Math.Clamp(config.MinRegimeConfidence, 0.0, 1.0),
        MaxOosDegradationPct = Math.Clamp(config.MaxOosDegradationPct, 0.0, 1.0),
        RegimeBudgetDiversityPct = Math.Clamp(config.RegimeBudgetDiversityPct, 0.01, 1.0),
        ScreeningSpreadPoints = Math.Max(0.0, config.ScreeningSpreadPoints),
        ScreeningCommissionPerLot = Math.Max(0.0, config.ScreeningCommissionPerLot),
        ScreeningSlippagePips = Math.Max(0.0, config.ScreeningSlippagePips),
        MaxSpreadToRangeRatio = Math.Clamp(config.MaxSpreadToRangeRatio, 0.0, 1.0),
        ScreeningInitialBalance = Math.Max(1m, config.ScreeningInitialBalance),
        MaxCandidates = Math.Max(1, config.MaxCandidates),
        MaxActivePerSymbol = Math.Max(1, config.MaxActivePerSymbol),
        MaxActivePerTypePerSymbol = Math.Max(1, config.MaxActivePerTypePerSymbol),
        MaxPerCurrencyGroup = Math.Max(1, config.MaxPerCurrencyGroup),
        MaxCorrelatedCandidates = Math.Max(1, config.MaxCorrelatedCandidates),
        PruneAfterFailed = Math.Max(1, config.PruneAfterFailed),
        RegimeFreshnessHours = Math.Max(1, config.RegimeFreshnessHours),
        RetryCooldownDays = Math.Max(1, config.RetryCooldownDays),
        RegimeTransitionCooldownHours = Math.Max(0, config.RegimeTransitionCooldownHours),
        ScreeningTimeoutSeconds = Math.Max(1, config.ScreeningTimeoutSeconds),
        CandidateTimeframes = config.CandidateTimeframes.Count > 0 ? config.CandidateTimeframes : [Timeframe.H4, Timeframe.D1],
        MaxTemplatesPerCombo = Math.Max(1, config.MaxTemplatesPerCombo),
        MaxParallelBacktests = Math.Max(1, config.MaxParallelBacktests),
        MaxCandleCacheSize = Math.Max(10_000, config.MaxCandleCacheSize),
        CandleChunkSize = Math.Max(1, config.CandleChunkSize),
        MaxCandleAgeHours = Math.Max(0, config.MaxCandleAgeHours),
        DataHealthMinCandles = Math.Max(20, config.DataHealthMinCandles),
        MinDataHealthScore = Math.Clamp(config.MinDataHealthScore, 0.0, 1.0),
        MinEquityCurveR2 = Math.Clamp(config.MinEquityCurveR2, 0.0, 1.0),
        MaxTradeTimeConcentration = Math.Clamp(config.MaxTradeTimeConcentration, 0.0, 1.0),
        MaxCostToWinRatio = Math.Clamp(config.MaxCostToWinRatio, 0.01, 2.0),
        WalkForwardWindowCount = Math.Max(2, config.WalkForwardWindowCount),
        WalkForwardMinWindowsPass = Math.Max(1, config.WalkForwardMinWindowsPass),
        WalkForwardSplitPcts = ParseWalkForwardSplitPcts(config.WalkForwardSplitPcts).Count >= 2 ? config.WalkForwardSplitPcts : "0.40,0.55,0.70",
        WalkForwardSplitPercentages = ParseWalkForwardSplitPcts(config.WalkForwardSplitPcts).Count >= 2
            ? ParseWalkForwardSplitPcts(config.WalkForwardSplitPcts)
            : [0.40, 0.55, 0.70],
        WalkForwardEmbargoPct = Math.Clamp(config.WalkForwardEmbargoPct, 0.0, 0.25),
        LookaheadAuditMaxTradeCountDelta = Math.Clamp(config.LookaheadAuditMaxTradeCountDelta, 0.0, 1.0),
        LookaheadAuditMaxPnlDelta = Math.Clamp(config.LookaheadAuditMaxPnlDelta, 0.0, 1.0),
        MonteCarloPermutations = Math.Max(1, config.MonteCarloPermutations),
        MonteCarloMinPValue = Math.Clamp(config.MonteCarloMinPValue, 0.0, 1.0),
        MonteCarloShufflePermutations = Math.Max(0, config.MonteCarloShufflePermutations),
        MonteCarloShuffleMinPValue = Math.Clamp(config.MonteCarloShuffleMinPValue, 0.0, 1.0),
        MaxPortfolioDrawdownPct = Math.Clamp(config.MaxPortfolioDrawdownPct, 0.0, 1.0),
        PortfolioCorrelationWeight = Math.Clamp(config.PortfolioCorrelationWeight, 0.0, 1.0),
        MaxPortfolioSymbolWeightPct = Math.Clamp(config.MaxPortfolioSymbolWeightPct, 0.05, 1.0),
        MaxPortfolioCurrencyExposurePct = Math.Clamp(config.MaxPortfolioCurrencyExposurePct, 0.10, 1.0),
        StrategicReserveQuota = Math.Max(0, config.StrategicReserveQuota),
        MaxCandidatesPerWeek = Math.Max(1, config.MaxCandidatesPerWeek),
        AdaptiveThresholdsMinSamples = Math.Max(1, config.AdaptiveThresholdsMinSamples),
        CircuitBreakerMaxFailures = Math.Max(1, config.CircuitBreakerMaxFailures),
        CircuitBreakerBackoffDays = Math.Max(1, config.CircuitBreakerBackoffDays),
        MaxFaultsPerStrategyType = Math.Max(1, config.MaxFaultsPerStrategyType),
        OosPfRelaxation = Math.Clamp(config.OosPfRelaxation, 0.1, 2.0),
        OosDdRelaxation = Math.Clamp(config.OosDdRelaxation, 0.1, 5.0),
        OosSharpeRelaxation = Math.Clamp(config.OosSharpeRelaxation, 0.1, 2.0),
        RegimeDegradationRelaxation = Math.Clamp(config.RegimeDegradationRelaxation, 1.0, 5.0),
        KellyFactor = Math.Clamp(config.KellyFactor, 0.1m, 1.0m),
        KellyMinLot = Math.Clamp(config.KellyMinLot, 0.001m, 1.0m),
        KellyMaxLot = Math.Clamp(config.KellyMaxLot, Math.Clamp(config.KellyMinLot, 0.001m, 1.0m), 1.0m),
    };

    private IReadOnlyDictionary<string, StrategyGenerationSymbolOverrides> ExtractAllSymbolOverrides(
        IReadOnlyDictionary<string, string> allConfigs)
    {
        var overridesBySymbol = new Dictionary<string, StrategyGenerationSymbolOverrides>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in allConfigs.Keys
                     .Where(k => k.StartsWith("StrategyGeneration:Overrides:", StringComparison.OrdinalIgnoreCase))
                     .Select(k =>
                     {
                         var parts = k.Split(':', StringSplitOptions.TrimEntries);
                         return parts.Length >= 4 ? parts[2] : null;
                     })
                     .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Cast<string>())
        {
            var extracted = ExtractSymbolOverrides(allConfigs, symbol);
            overridesBySymbol[symbol] = new StrategyGenerationSymbolOverrides(
                extracted.MinWinRate,
                extracted.MinProfitFactor,
                extracted.MinSharpe,
                extracted.MaxDrawdownPct);
        }

        return overridesBySymbol;
    }

    private static T TryReadValue<T>(
        IReadOnlyDictionary<string, string> values,
        string key,
        T defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || raw is null)
            return defaultValue;

        return TryConvertConfigValue(raw, defaultValue, out T parsed) ? parsed : defaultValue;
    }

    private static bool TryConvertConfigValue<T>(string raw, T defaultValue, out T parsed)
    {
        object? value = null;
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType == typeof(string))
        {
            value = raw;
        }
        else if (targetType == typeof(bool) && bool.TryParse(raw, out var b))
        {
            value = b;
        }
        else if (targetType == typeof(int) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            value = i;
        }
        else if (targetType == typeof(long) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            value = l;
        }
        else if (targetType == typeof(double) && double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d))
        {
            value = d;
        }
        else if (targetType == typeof(decimal) && decimal.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var m))
        {
            value = m;
        }
        else if (targetType == typeof(DateTime) && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var dt))
        {
            value = dt;
        }
        else if (targetType.IsEnum && Enum.TryParse(targetType, raw, true, out var enumValue))
        {
            value = enumValue;
        }

        if (value is T typed)
        {
            parsed = typed;
            return true;
        }

        if (value != null)
        {
            parsed = (T)value;
            return true;
        }

        parsed = defaultValue;
        return false;
    }
}
