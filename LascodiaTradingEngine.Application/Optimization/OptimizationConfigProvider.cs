using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationConfigProvider
{
    internal sealed record CacheSnapshot(
        OptimizationConfig? Config,
        DateTime LastLoadedAtUtc,
        DateTime NextRefreshDueAtUtc,
        long Generation,
        bool IsCached);

    /// <summary>
    /// Single source of truth for all recognized optimization/backtest config keys.
    /// Used by both <see cref="LoadAsync"/> (batch query) and
    /// <see cref="DetectUnknownConfigKeysAsync"/> (typo detection).
    /// </summary>
    private static readonly string[] KnownConfigKeys =
    [
        "Optimization:Preset",
        "Optimization:SchedulePollSeconds", "Optimization:CooldownDays", "Optimization:RolloutObservationDays",
        "Optimization:MaxQueuedPerCycle", "Optimization:FollowUpMonitorBatchSize",
        "Optimization:AutoScheduleEnabled", "Backtest:Gate:MinWinRate", "Backtest:Gate:MinProfitFactor",
        "Backtest:Gate:MinTotalTrades", "Optimization:AutoApprovalImprovementThreshold",
        "Optimization:AutoApprovalMinHealthScore", "Optimization:TopNCandidates",
        "Optimization:CoarsePhaseThreshold", "Optimization:ScreeningTimeoutSeconds",
        "Optimization:ScreeningSpreadPoints", "Optimization:ScreeningCommissionPerLot",
        "Optimization:ScreeningSlippagePips", "Optimization:MaxOosDegradationPct",
        "Optimization:SuppressDuringDrawdownRecovery", "Optimization:SeasonalBlackoutEnabled",
        "Optimization:BlackoutPeriods", "Optimization:MaxRunTimeoutMinutes",
        "Optimization:MaxParallelBacktests", "Optimization:MinCandidateTrades",
        "Optimization:EmbargoRatio", "Optimization:CorrelationParamThreshold",
        "Optimization:TpeBudget", "Optimization:TpeInitialSamples", "Optimization:PurgedKFolds",
        "Optimization:SensitivityPerturbPct", "Optimization:BootstrapIterations",
        "Optimization:MinBootstrapCILower", "Optimization:CostSensitivityEnabled",
        "Optimization:AdaptiveBoundsEnabled", "Optimization:TemporalOverlapThreshold",
        "Optimization:DataScarcityThreshold", "Optimization:ScreeningInitialBalance",
        "Optimization:PortfolioCorrelationThreshold", "Optimization:MaxConsecutiveFailuresBeforeEscalation",
        "Optimization:CheckpointEveryN", "Optimization:GpEarlyStopPatience",
        "Optimization:SensitivityDegradationTolerance", "Optimization:WalkForwardMinMaxRatio",
        "Optimization:CostStressMultiplier", "Optimization:MinOosCandlesForValidation",
        "Optimization:MaxCvCoefficientOfVariation", "Optimization:PermutationIterations",
        "Optimization:RegimeStabilityHours",
        "Optimization:MaxRetryAttempts", "Optimization:CandleLookbackMonths", "Optimization:CandleLookbackAutoScale",
        "Optimization:RequireEADataAvailability", "Optimization:MaxConcurrentRuns",
        "Optimization:UseSymbolSpecificSpread", "Optimization:RegimeBlendRatio",
        "Optimization:CpcvNFolds", "Optimization:CpcvTestFoldCount",
        "Optimization:CpcvMaxCombinations", "Optimization:CircuitBreakerThreshold",
        "Optimization:SuccessiveHalvingRungs", "Optimization:MaxCrossRegimeEvals",
        "Optimization:HyperbandEnabled", "Optimization:HyperbandEta",
        "Optimization:MaxRunsPerWeek", "Optimization:UseEhviAcquisition",
        "Optimization:UseParegoScalarization", "Optimization:MinEquityCurveR2",
        "Optimization:MaxTradeTimeConcentration",
    ];

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly ILogger<OptimizationConfigProvider> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _cacheLock = new();
    private OptimizationConfig? _cachedConfig;
    private DateTime _cachedAtUtc;
    private long _cacheGeneration;
    private DateTime _lastInvalidatedAtUtc;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationConfigProvider(
        ILogger<OptimizationConfigProvider> logger,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
    }

    internal async Task<OptimizationConfig> LoadAsync(DbContext db, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cachedConfig is not null
                && (UtcNow - _cachedAtUtc) < CacheTtl)
            {
                return _cachedConfig;
            }
        }

        var config = await LoadDirectAsync(db, _logger, ct);
        lock (_cacheLock)
        {
            _cachedConfig = config;
            _cachedAtUtc = UtcNow;
        }

        return config;
    }

    internal void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedConfig = null;
            _cachedAtUtc = default;
            _cacheGeneration++;
            _lastInvalidatedAtUtc = UtcNow;
        }
    }

    internal CacheSnapshot GetCacheSnapshot()
    {
        lock (_cacheLock)
        {
            bool isCached = _cachedConfig is not null && _cachedAtUtc != default;
            var lastLoadedAtUtc = isCached ? _cachedAtUtc : _lastInvalidatedAtUtc;
            return new CacheSnapshot(
                _cachedConfig,
                lastLoadedAtUtc,
                lastLoadedAtUtc == default ? default : lastLoadedAtUtc.Add(CacheTtl),
                _cacheGeneration,
                isCached);
        }
    }

    internal static async Task<OptimizationConfig> LoadDirectAsync(
        DbContext db,
        ILogger logger,
        CancellationToken ct)
    {
        var batch = await OptimizationGridBuilder.GetConfigBatchAsync(db, KnownConfigKeys, ct);
        var preset = GetPresetDefaults(OptimizationGridBuilder.GetConfigValue(batch, "Optimization:Preset", "balanced"));
        string presetName = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:Preset", "balanced");

        return new OptimizationConfig
        {
            SchedulePollSeconds = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:SchedulePollSeconds", 7200),
            CooldownDays = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CooldownDays", 14),
            RolloutObservationDays = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:RolloutObservationDays", 14),
            MaxQueuedPerCycle = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MaxQueuedPerCycle", 3),
            FollowUpMonitorBatchSize = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:FollowUpMonitorBatchSize", 10),
            AutoScheduleEnabled = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:AutoScheduleEnabled", true),
            MaxRunsPerWeek = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MaxRunsPerWeek", 20),
            MinWinRate = OptimizationGridBuilder.GetConfigValue(batch, "Backtest:Gate:MinWinRate", 0.60),
            MinProfitFactor = OptimizationGridBuilder.GetConfigValue(batch, "Backtest:Gate:MinProfitFactor", 1.0),
            MinTotalTrades = OptimizationGridBuilder.GetConfigValue(batch, "Backtest:Gate:MinTotalTrades", 10),
            AutoApprovalImprovementThreshold = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:AutoApprovalImprovementThreshold", 0.10m),
            AutoApprovalMinHealthScore = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:AutoApprovalMinHealthScore", 0.55m),
            TopNCandidates = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:TopNCandidates", preset.TopNCandidates),
            CoarsePhaseThreshold = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CoarsePhaseThreshold", 10),
            TpeBudget = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:TpeBudget", preset.TpeBudget),
            TpeInitialSamples = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:TpeInitialSamples", preset.TpeInitialSamples),
            PurgedKFolds = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:PurgedKFolds", preset.PurgedKFolds),
            AdaptiveBoundsEnabled = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:AdaptiveBoundsEnabled", true),
            GpEarlyStopPatience = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:GpEarlyStopPatience", 4),
            PresetName = presetName,
            HyperbandEnabled = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:HyperbandEnabled", true),
            HyperbandEta = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:HyperbandEta", 3),
            UseEhviAcquisition = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:UseEhviAcquisition", false),
            UseParegoScalarization = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:UseParegoScalarization", false),
            ScreeningTimeoutSeconds = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:ScreeningTimeoutSeconds", 30),
            ScreeningSpreadPoints = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:ScreeningSpreadPoints", 20.0),
            ScreeningCommissionPerLot = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:ScreeningCommissionPerLot", 7.0),
            ScreeningSlippagePips = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:ScreeningSlippagePips", 1.0),
            ScreeningInitialBalance = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:ScreeningInitialBalance", 10_000m),
            MaxParallelBacktests = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MaxParallelBacktests", preset.MaxParallelBacktests),
            MinCandidateTrades = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MinCandidateTrades", 10),
            MaxRunTimeoutMinutes = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MaxRunTimeoutMinutes", preset.MaxRunTimeoutMinutes),
            CircuitBreakerThreshold = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CircuitBreakerThreshold", 10),
            SuccessiveHalvingRungs = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:SuccessiveHalvingRungs", "0.25,0.50"),
            MaxOosDegradationPct = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MaxOosDegradationPct", 0.60),
            EmbargoRatio = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:EmbargoRatio", 0.05),
            CorrelationParamThreshold = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CorrelationParamThreshold", 0.15),
            SensitivityPerturbPct = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:SensitivityPerturbPct", 0.10),
            SensitivityDegradationTolerance = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:SensitivityDegradationTolerance", 0.20),
            BootstrapIterations = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:BootstrapIterations", preset.BootstrapIters),
            MinBootstrapCILower = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MinBootstrapCILower", 0.40m),
            CostSensitivityEnabled = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CostSensitivityEnabled", true),
            CostStressMultiplier = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CostStressMultiplier", 2.0),
            TemporalOverlapThreshold = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:TemporalOverlapThreshold", 0.70),
            PortfolioCorrelationThreshold = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:PortfolioCorrelationThreshold", 0.80),
            WalkForwardMinMaxRatio = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:WalkForwardMinMaxRatio", 0.50),
            MinOosCandlesForValidation = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MinOosCandlesForValidation", 50),
            MaxCvCoefficientOfVariation = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MaxCvCoefficientOfVariation", 0.50),
            PermutationIterations = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:PermutationIterations", preset.PermutationIters),
            MinEquityCurveR2 = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MinEquityCurveR2", 0.60),
            MaxTradeTimeConcentration = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MaxTradeTimeConcentration", 0.60),
            CpcvNFolds = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CpcvNFolds", preset.CpcvNFolds),
            CpcvTestFoldCount = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CpcvTestFoldCount", 2),
            CpcvMaxCombinations = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CpcvMaxCombinations", preset.CpcvMaxCombinations),
            DataScarcityThreshold = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:DataScarcityThreshold", 200),
            CandleLookbackMonths = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CandleLookbackMonths", 6),
            CandleLookbackAutoScale = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CandleLookbackAutoScale", true),
            UseSymbolSpecificSpread = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:UseSymbolSpecificSpread", true),
            RegimeBlendRatio = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:RegimeBlendRatio", 0.20),
            MaxCrossRegimeEvals = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MaxCrossRegimeEvals", 4),
            RegimeStabilityHours = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:RegimeStabilityHours", 6),
            SuppressDuringDrawdownRecovery = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:SuppressDuringDrawdownRecovery", true),
            SeasonalBlackoutEnabled = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:SeasonalBlackoutEnabled", true),
            BlackoutPeriods = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:BlackoutPeriods", "12/20-01/05"),
            RequireEADataAvailability = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:RequireEADataAvailability", true),
            MaxRetryAttempts = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MaxRetryAttempts", 2),
            MaxConsecutiveFailuresBeforeEscalation = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MaxConsecutiveFailuresBeforeEscalation", 3),
            CheckpointEveryN = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:CheckpointEveryN", preset.CheckpointEveryN),
            MaxConcurrentRuns = OptimizationGridBuilder.GetConfigValue(batch, "Optimization:MaxConcurrentRuns", 3),
        };
    }

    internal async Task DetectUnknownConfigKeysAsync(DbContext db, CancellationToken ct)
        => await DetectUnknownConfigKeysAsync(db, _logger, ct);

    internal static async Task DetectUnknownConfigKeysAsync(
        DbContext db,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var knownKeys = new HashSet<string>(KnownConfigKeys, StringComparer.OrdinalIgnoreCase);

            var dbKeys = await db.Set<EngineConfig>()
                .Where(c => !c.IsDeleted
                         && (c.Key.StartsWith("Optimization:") || c.Key.StartsWith("Backtest:Gate:")))
                .Select(c => c.Key)
                .ToListAsync(ct);

            var unrecognized = dbKeys.Where(k => !knownKeys.Contains(k)).ToList();
            if (unrecognized.Count > 0)
            {
                logger.LogWarning(
                    "OptimizationWorker: {Count} unrecognized config key(s) found — possible typos that will use defaults instead: {Keys}",
                    unrecognized.Count,
                    string.Join(", ", unrecognized));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "OptimizationWorker: config typo detection failed (non-fatal)");
        }
    }

    internal void LogPresetOverrides(OptimizationConfig config)
        => LogPresetOverrides(config, _logger);

    internal static void LogPresetOverrides(
        OptimizationConfig config,
        ILogger logger)
    {
        var preset = GetPresetDefaults(config.PresetName);
        var overrides = new List<string>();
        if (config.TpeBudget != preset.TpeBudget) overrides.Add($"TpeBudget={config.TpeBudget} (preset={preset.TpeBudget})");
        if (config.TpeInitialSamples != preset.TpeInitialSamples) overrides.Add($"TpeInitialSamples={config.TpeInitialSamples} (preset={preset.TpeInitialSamples})");
        if (config.PurgedKFolds != preset.PurgedKFolds) overrides.Add($"PurgedKFolds={config.PurgedKFolds} (preset={preset.PurgedKFolds})");
        if (config.BootstrapIterations != preset.BootstrapIters) overrides.Add($"BootstrapIterations={config.BootstrapIterations} (preset={preset.BootstrapIters})");
        if (config.PermutationIterations != preset.PermutationIters) overrides.Add($"PermutationIterations={config.PermutationIterations} (preset={preset.PermutationIters})");
        if (config.MaxParallelBacktests != preset.MaxParallelBacktests) overrides.Add($"MaxParallelBacktests={config.MaxParallelBacktests} (preset={preset.MaxParallelBacktests})");
        if (config.CpcvNFolds != preset.CpcvNFolds) overrides.Add($"CpcvNFolds={config.CpcvNFolds} (preset={preset.CpcvNFolds})");
        if (config.MaxRunTimeoutMinutes != preset.MaxRunTimeoutMinutes) overrides.Add($"MaxRunTimeoutMinutes={config.MaxRunTimeoutMinutes} (preset={preset.MaxRunTimeoutMinutes})");
        if (config.CpcvMaxCombinations != preset.CpcvMaxCombinations) overrides.Add($"CpcvMaxCombinations={config.CpcvMaxCombinations} (preset={preset.CpcvMaxCombinations})");
        if (config.TopNCandidates != preset.TopNCandidates) overrides.Add($"TopNCandidates={config.TopNCandidates} (preset={preset.TopNCandidates})");
        if (config.CheckpointEveryN != preset.CheckpointEveryN) overrides.Add($"CheckpointEveryN={config.CheckpointEveryN} (preset={preset.CheckpointEveryN})");

        if (overrides.Count > 0)
        {
            logger.LogInformation(
                "OptimizationWorker: using preset '{Preset}' with {Count} override(s): {Overrides}",
                config.PresetName,
                overrides.Count,
                string.Join(", ", overrides));
        }
        else
        {
            logger.LogDebug("OptimizationWorker: using preset '{Preset}' with no overrides", config.PresetName);
        }
    }

    private static (int TpeBudget, int TpeInitialSamples, int PurgedKFolds, int BootstrapIters,
        int PermutationIters, int MaxParallelBacktests, int CpcvNFolds, int MaxRunTimeoutMinutes,
        int CpcvMaxCombinations, int TopNCandidates, int CheckpointEveryN) GetPresetDefaults(string preset)
        => preset.ToLowerInvariant() switch
        {
            "conservative" => (30, 10, 3, 500, 500, 2, 4, 20, 10, 3, 5),
            "aggressive" => (100, 25, 7, 2000, 2000, 8, 8, 60, 25, 8, 15),
            _ => (50, 15, 5, 1000, 1000, 4, 6, 30, 15, 5, 10),
        };
}
