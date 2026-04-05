namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Decomposed configuration records for the optimization pipeline.
/// The worker's internal <c>OptimizationConfig</c> monolith is mapped to these
/// focused sub-records for clarity when passing config to extracted stage classes.
/// </summary>

/// <summary>Configuration for the Bayesian search phase.</summary>
internal sealed record SearchConfig(
    int TpeBudget,
    int TpeInitialSamples,
    int PurgedKFolds,
    int MaxParallelBacktests,
    int ScreeningTimeoutSeconds,
    int MinCandidateTrades,
    int TopNCandidates,
    int CoarsePhaseThreshold,
    int CheckpointEveryN,
    int GpEarlyStopPatience,
    int CircuitBreakerThreshold,
    string SuccessiveHalvingRungs,
    bool AdaptiveBoundsEnabled);

/// <summary>Configuration for validation gates.</summary>
internal sealed record ValidationConfig(
    double SensitivityPerturbPct,
    double SensitivityDegradationTolerance,
    int BootstrapIterations,
    decimal MinBootstrapCILower,
    bool CostSensitivityEnabled,
    double CostStressMultiplier,
    double MaxOosDegradationPct,
    double WalkForwardMinMaxRatio,
    double CorrelationParamThreshold,
    double TemporalOverlapThreshold,
    double PortfolioCorrelationThreshold,
    double MaxCvCoefficientOfVariation,
    int PermutationIterations,
    int MinOosCandlesForValidation,
    int CpcvNFolds,
    int CpcvTestFoldCount,
    int CpcvMaxCombinations);

/// <summary>Configuration for auto-approval decisions.</summary>
internal sealed record ApprovalConfig(
    decimal AutoApprovalImprovementThreshold,
    decimal AutoApprovalMinHealthScore,
    int MaxConsecutiveFailuresBeforeEscalation);

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
