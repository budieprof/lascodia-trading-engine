namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>All hot-reloadable configuration for a single optimisation cycle.</summary>
internal sealed record OptimizationConfig
{
    // Scheduling
    public required int SchedulePollSeconds { get; init; }
    public required int CooldownDays { get; init; }
    public int RolloutObservationDays { get; init; } = 14;
    public required int MaxQueuedPerCycle { get; init; }
    public int FollowUpMonitorBatchSize { get; init; } = 10;
    public required bool AutoScheduleEnabled { get; init; }
    public required int MaxRunsPerWeek { get; init; }

    // Performance gates
    public required double MinWinRate { get; init; }
    public required double MinProfitFactor { get; init; }
    public required int MinTotalTrades { get; init; }

    // Approval thresholds
    public required decimal AutoApprovalImprovementThreshold { get; init; }
    public required decimal AutoApprovalMinHealthScore { get; init; }

    // Search
    public required int TopNCandidates { get; init; }
    public required int CoarsePhaseThreshold { get; init; }
    public required int TpeBudget { get; init; }
    public required int TpeInitialSamples { get; init; }
    public required int PurgedKFolds { get; init; }
    public required bool AdaptiveBoundsEnabled { get; init; }
    public required int GpEarlyStopPatience { get; init; }
    public required string PresetName { get; init; }
    public required bool HyperbandEnabled { get; init; }
    public required int HyperbandEta { get; init; }
    public required bool UseEhviAcquisition { get; init; }
    public required bool UseParegoScalarization { get; init; }

    // Screening / backtesting
    public required int ScreeningTimeoutSeconds { get; init; }
    public required double ScreeningSpreadPoints { get; init; }
    public required double ScreeningCommissionPerLot { get; init; }
    public required double ScreeningSlippagePips { get; init; }
    public required decimal ScreeningInitialBalance { get; init; }
    public required int MaxParallelBacktests { get; init; }
    public required int MinCandidateTrades { get; init; }
    public required int MaxRunTimeoutMinutes { get; init; }
    public required int CircuitBreakerThreshold { get; init; }
    public required string SuccessiveHalvingRungs { get; init; }

    // Validation gates
    public required double MaxOosDegradationPct { get; init; }
    public required double EmbargoRatio { get; init; }
    public required double CorrelationParamThreshold { get; init; }
    public required double SensitivityPerturbPct { get; init; }
    public required double SensitivityDegradationTolerance { get; init; }
    public required int BootstrapIterations { get; init; }
    public required decimal MinBootstrapCILower { get; init; }
    public required bool CostSensitivityEnabled { get; init; }
    public required double CostStressMultiplier { get; init; }
    public required double TemporalOverlapThreshold { get; init; }
    public required double PortfolioCorrelationThreshold { get; init; }
    public required double WalkForwardMinMaxRatio { get; init; }
    public required int MinOosCandlesForValidation { get; init; }
    public required double MaxCvCoefficientOfVariation { get; init; }
    public required int PermutationIterations { get; init; }
    public required double MinEquityCurveR2 { get; init; }
    public required double MaxTradeTimeConcentration { get; init; }

    // CPCV
    public required int CpcvNFolds { get; init; }
    public required int CpcvTestFoldCount { get; init; }
    public required int CpcvMaxCombinations { get; init; }

    // Data loading
    public required int DataScarcityThreshold { get; init; }
    public required int CandleLookbackMonths { get; init; }
    public required bool CandleLookbackAutoScale { get; init; }
    public required bool UseSymbolSpecificSpread { get; init; }
    public required double RegimeBlendRatio { get; init; }
    public required int MaxCrossRegimeEvals { get; init; }
    public required int RegimeStabilityHours { get; init; }

    // Suppression / deferral
    public required bool SuppressDuringDrawdownRecovery { get; init; }
    public required bool SeasonalBlackoutEnabled { get; init; }
    public required string BlackoutPeriods { get; init; }
    public required bool RequireEADataAvailability { get; init; }

    // Retry / escalation
    public required int MaxRetryAttempts { get; init; }
    public required int MaxConsecutiveFailuresBeforeEscalation { get; init; }
    public required int CheckpointEveryN { get; init; }
    public required int MaxConcurrentRuns { get; init; }
}

/// <summary>
/// Decomposed configuration records for the optimization pipeline.
/// The <see cref="OptimizationConfig"/> monolith is mapped to these
/// focused sub-records for clarity when passing config to extracted stage classes.
/// </summary>

/// <summary>Configuration for the Bayesian search phase.</summary>
internal sealed record SearchConfig
{
    public required int TpeBudget { get; init; }
    public required int TpeInitialSamples { get; init; }
    public required int PurgedKFolds { get; init; }
    public required int MaxParallelBacktests { get; init; }
    public required int ScreeningTimeoutSeconds { get; init; }
    public required decimal ScreeningInitialBalance { get; init; }
    public required int MinCandidateTrades { get; init; }
    public required int TopNCandidates { get; init; }
    public required int CoarsePhaseThreshold { get; init; }
    public required int CheckpointEveryN { get; init; }
    public required int GpEarlyStopPatience { get; init; }
    public required int CircuitBreakerThreshold { get; init; }
    public required string SuccessiveHalvingRungs { get; init; }
    public required bool AdaptiveBoundsEnabled { get; init; }
    public required bool HyperbandEnabled { get; init; }
    public required int HyperbandEta { get; init; }
    public required bool UseEhviAcquisition { get; init; }
    public required bool UseParegoScalarization { get; init; }
}

/// <summary>Configuration for validation gates.</summary>
internal sealed record ValidationConfig
{
    public required double SensitivityPerturbPct { get; init; }
    public required double SensitivityDegradationTolerance { get; init; }
    public required int BootstrapIterations { get; init; }
    public required decimal MinBootstrapCILower { get; init; }
    public required bool CostSensitivityEnabled { get; init; }
    public required double CostStressMultiplier { get; init; }
    public required double MaxOosDegradationPct { get; init; }
    public required double WalkForwardMinMaxRatio { get; init; }
    public required double CorrelationParamThreshold { get; init; }
    public required double TemporalOverlapThreshold { get; init; }
    public required double PortfolioCorrelationThreshold { get; init; }
    public required double MaxCvCoefficientOfVariation { get; init; }
    public required int PermutationIterations { get; init; }
    public required int MinOosCandlesForValidation { get; init; }
    public required int CpcvNFolds { get; init; }
    public required int CpcvTestFoldCount { get; init; }
    public required int CpcvMaxCombinations { get; init; }
    public required int ScreeningTimeoutSeconds { get; init; }
    public required int MaxRunTimeoutMinutes { get; init; }
    public required int MaxParallelBacktests { get; init; }
    public required decimal ScreeningInitialBalance { get; init; }
    public required decimal AutoApprovalMinHealthScore { get; init; }
    public required decimal AutoApprovalImprovementThreshold { get; init; }
    public required double EmbargoRatio { get; init; }
    public required int MinCandidateTrades { get; init; }
    public required double MinEquityCurveR2 { get; init; }
    public required double MaxTradeTimeConcentration { get; init; }
}

/// <summary>Configuration for auto-approval decisions.</summary>
internal sealed record ApprovalConfig
{
    public required decimal AutoApprovalImprovementThreshold { get; init; }
    public required decimal AutoApprovalMinHealthScore { get; init; }
    public required int MaxConsecutiveFailuresBeforeEscalation { get; init; }
    public required int CooldownDays { get; init; }
    public required int ScreeningTimeoutSeconds { get; init; }
    public required int MaxRunTimeoutMinutes { get; init; }
    public required int MaxParallelBacktests { get; init; }
    public required int MaxCrossRegimeEvals { get; init; }
}

/// <summary>Configuration for auto-scheduling.</summary>
internal sealed record SchedulingConfig(
    int SchedulePollSeconds,
    int CooldownDays,
    int MaxQueuedPerCycle,
    bool AutoScheduleEnabled,
    double MinWinRate,
    double MinProfitFactor,
    int MinTotalTrades);

/// <summary>Configuration for transaction costs in backtests.</summary>
internal sealed record CostConfig(
    double ScreeningSpreadPoints,
    double ScreeningCommissionPerLot,
    double ScreeningSlippagePips,
    decimal ScreeningInitialBalance,
    bool UseSymbolSpecificSpread);

/// <summary>Configuration for environment guards (blackout, drawdown, EA).</summary>
internal sealed record EnvironmentConfig(
    bool SuppressDuringDrawdownRecovery,
    bool SeasonalBlackoutEnabled,
    string BlackoutPeriods,
    bool RequireEADataAvailability,
    int MaxRunTimeoutMinutes,
    int MaxConcurrentRuns,
    int MaxRetryAttempts,
    int CandleLookbackMonths,
    double EmbargoRatio,
    int DataScarcityThreshold,
    double RegimeBlendRatio,
    int MaxCrossRegimeEvals);

internal static class OptimizationConfigMappingExtensions
{
    public static OptimizationConfig ToDataLoadingConfig(this OptimizationConfig config) => config;

    public static SearchConfig ToSearchConfig(this OptimizationConfig config) => new()
    {
        TpeBudget = config.TpeBudget,
        TpeInitialSamples = config.TpeInitialSamples,
        PurgedKFolds = config.PurgedKFolds,
        MaxParallelBacktests = config.MaxParallelBacktests,
        ScreeningTimeoutSeconds = config.ScreeningTimeoutSeconds,
        ScreeningInitialBalance = config.ScreeningInitialBalance,
        MinCandidateTrades = config.MinCandidateTrades,
        TopNCandidates = config.TopNCandidates,
        CoarsePhaseThreshold = config.CoarsePhaseThreshold,
        CheckpointEveryN = config.CheckpointEveryN,
        GpEarlyStopPatience = config.GpEarlyStopPatience,
        CircuitBreakerThreshold = config.CircuitBreakerThreshold,
        SuccessiveHalvingRungs = config.SuccessiveHalvingRungs,
        AdaptiveBoundsEnabled = config.AdaptiveBoundsEnabled,
        HyperbandEnabled = config.HyperbandEnabled,
        HyperbandEta = config.HyperbandEta,
        UseEhviAcquisition = config.UseEhviAcquisition,
        UseParegoScalarization = config.UseParegoScalarization,
    };

    public static ValidationConfig ToValidationConfig(this OptimizationConfig config) => new()
    {
        SensitivityPerturbPct = config.SensitivityPerturbPct,
        SensitivityDegradationTolerance = config.SensitivityDegradationTolerance,
        BootstrapIterations = config.BootstrapIterations,
        MinBootstrapCILower = config.MinBootstrapCILower,
        CostSensitivityEnabled = config.CostSensitivityEnabled,
        CostStressMultiplier = config.CostStressMultiplier,
        MaxOosDegradationPct = config.MaxOosDegradationPct,
        WalkForwardMinMaxRatio = config.WalkForwardMinMaxRatio,
        CorrelationParamThreshold = config.CorrelationParamThreshold,
        TemporalOverlapThreshold = config.TemporalOverlapThreshold,
        PortfolioCorrelationThreshold = config.PortfolioCorrelationThreshold,
        MaxCvCoefficientOfVariation = config.MaxCvCoefficientOfVariation,
        PermutationIterations = config.PermutationIterations,
        MinOosCandlesForValidation = config.MinOosCandlesForValidation,
        CpcvNFolds = config.CpcvNFolds,
        CpcvTestFoldCount = config.CpcvTestFoldCount,
        CpcvMaxCombinations = config.CpcvMaxCombinations,
        ScreeningTimeoutSeconds = config.ScreeningTimeoutSeconds,
        MaxRunTimeoutMinutes = config.MaxRunTimeoutMinutes,
        MaxParallelBacktests = config.MaxParallelBacktests,
        ScreeningInitialBalance = config.ScreeningInitialBalance,
        AutoApprovalMinHealthScore = config.AutoApprovalMinHealthScore,
        AutoApprovalImprovementThreshold = config.AutoApprovalImprovementThreshold,
        EmbargoRatio = config.EmbargoRatio,
        MinCandidateTrades = config.MinCandidateTrades,
        MinEquityCurveR2 = config.MinEquityCurveR2,
        MaxTradeTimeConcentration = config.MaxTradeTimeConcentration,
    };

    public static ApprovalConfig ToApprovalConfig(this OptimizationConfig config) => new()
    {
        AutoApprovalImprovementThreshold = config.AutoApprovalImprovementThreshold,
        AutoApprovalMinHealthScore = config.AutoApprovalMinHealthScore,
        MaxConsecutiveFailuresBeforeEscalation = config.MaxConsecutiveFailuresBeforeEscalation,
        CooldownDays = config.CooldownDays,
        ScreeningTimeoutSeconds = config.ScreeningTimeoutSeconds,
        MaxRunTimeoutMinutes = config.MaxRunTimeoutMinutes,
        MaxParallelBacktests = config.MaxParallelBacktests,
        MaxCrossRegimeEvals = config.MaxCrossRegimeEvals,
    };
}
