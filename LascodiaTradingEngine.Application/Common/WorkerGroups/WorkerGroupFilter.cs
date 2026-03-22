using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Workers;

namespace LascodiaTradingEngine.Application.Common.WorkerGroups;

/// <summary>
/// Removes <see cref="IHostedService"/> registrations for workers that don't belong to
/// any enabled group. Called after the shared library's <c>AutoRegisterBackgroundJobs</c>
/// has registered all workers unconditionally.
///
/// When <see cref="WorkerGroupConfiguration.EnableAll"/> is true (default), no workers
/// are removed — the engine runs as a monolith.
/// </summary>
public static class WorkerGroupFilter
{
    // ── Group → Worker type mappings ────────────────────────────────────────

    private static readonly Type[] CoreTradingWorkers =
    [
        typeof(StrategyWorker),
        typeof(SignalOrderBridgeWorker),
        typeof(OrderExecutionWorker),
        typeof(PositionWorker),
        typeof(TrailingStopWorker),
        typeof(AccountSyncWorker),
        typeof(OrderFilledEventHandler),
        typeof(PositionClosedEventHandler),
    ];

    private static readonly Type[] MarketDataWorkers =
    [
        typeof(MarketDataWorker),
        typeof(RegimeDetectionWorker),
        typeof(SentimentWorker),
        typeof(COTDataWorker),
        typeof(EconomicCalendarWorker),
    ];

    private static readonly Type[] RiskMonitoringWorkers =
    [
        typeof(RiskMonitorWorker),
        typeof(DrawdownMonitorWorker),
        typeof(DrawdownRecoveryWorker),
        typeof(ExecutionQualityCircuitBreakerWorker),
        typeof(StrategyHealthWorker),
        typeof(StrategyFeedbackWorker),
    ];

    private static readonly Type[] MLTrainingWorkers =
    [
        typeof(MLTrainingWorker),
        typeof(MLShadowArbiterWorker),
        typeof(MLPredictionOutcomeWorker),
        typeof(MLModelActivatedEventHandler),
        typeof(MLModelRetirementWorker),
        typeof(MLTrainingRunHealthWorker),
        typeof(MLTrainingDataFreshnessWorker),
        typeof(MLTransferLearningWorker),
        typeof(MLModelDistillationWorker),
        typeof(MLMamlMetaLearnerWorker),
        typeof(MLOnlineLearningWorker),
        typeof(MLPredictionLogPruningWorker),
    ];

    private static readonly Type[] MLMonitoringWorkers =
    [
        typeof(MLAdaptiveThresholdWorker),
        typeof(MLAdwinDriftWorker),
        typeof(MLCalibratedEdgeWorker),
        typeof(MLCausalFeatureWorker),
        typeof(MLConformalBreakerWorker),
        typeof(MLConformalCalibrationWorker),
        typeof(MLConformalRecalibrationWorker),
        typeof(MLCorrelatedSignalConflictWorker),
        typeof(MLCovariateShiftWorker),
        typeof(MLCusumDriftWorker),
        typeof(MLDataQualityWorker),
        typeof(MLDirectionStreakWorker),
        typeof(MLDriftMonitorWorker),
        typeof(MLEnsembleDiversityRecoveryWorker),
        typeof(MLErgodicityWorker),
        typeof(MLEwmaAccuracyWorker),
        typeof(MLFeatureImportanceTrendWorker),
        typeof(MLFeatureInteractionWorker),
        typeof(MLFeaturePsiWorker),
        typeof(MLFeatureRankShiftWorker),
        typeof(MLFeatureStalenessWorker),
        typeof(MLHawkesProcessWorker),
        typeof(MLHorizonAccuracyWorker),
        typeof(MLIsotonicRecalibrationWorker),
        typeof(MLKellyFractionWorker),
        typeof(MLMrmrFeatureWorker),
        typeof(MLMultiScaleDriftWorker),
        typeof(MLOnlinePlattWorker),
        typeof(MLPeltChangePointWorker),
        typeof(MLPositionSizeAdvisorWorker),
        typeof(MLPredictionPnlWorker),
        typeof(MLPredictionSharpnessWorker),
        typeof(MLPredictionSkewWorker),
        typeof(MLProductionCalibrationWorker),
        typeof(MLPsiAutoRetrainWorker),
        typeof(MLRecalibrationWorker),
        typeof(MLRegimeAccuracyWorker),
        typeof(MLRegimeTransitionGuardWorker),
        typeof(MLRewardToRiskWorker),
        typeof(MLRollingAccuracyWorker),
        typeof(MLSessionAccuracyWorker),
        typeof(MLSharpeEnsembleWorker),
        typeof(MLSignalCooldownWorker),
        typeof(MLSignalCoverageAuditWorker),
        typeof(MLSignalFunnelWorker),
        typeof(MLSignalSuppressionWorker),
        typeof(MLStackingMetaLearnerWorker),
        typeof(MLStructuralBreakWorker),
        typeof(MLTemperatureScalingWorker),
        typeof(MLThresholdCalibrationWorker),
        typeof(MLTimeOfDayAccuracyWorker),
        typeof(MLVolatilityAccuracyWorker),
    ];

    private static readonly Type[] BacktestingWorkers =
    [
        typeof(BacktestWorker),
        typeof(OptimizationWorker),
        typeof(WalkForwardWorker),
    ];

    private static readonly Type[] AlertWorkers =
    [
        typeof(AlertWorker),
    ];

    /// <summary>
    /// Removes <see cref="IHostedService"/> registrations for workers whose group is disabled.
    /// Must be called after <c>ConfigureApplicationServices</c> and <c>AddSharedApplicationDependency</c>.
    /// </summary>
    public static IServiceCollection ApplyWorkerGroupFilter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var config = new WorkerGroupConfiguration();
        configuration.GetSection("WorkerGroups").Bind(config);

        // EnableAll = true → keep everything (default behavior)
        if (config.EnableAll)
            return services;

        var disabledTypes = new HashSet<Type>();

        if (!config.CoreTrading)    AddRange(disabledTypes, CoreTradingWorkers);
        if (!config.MarketData)     AddRange(disabledTypes, MarketDataWorkers);
        if (!config.RiskMonitoring) AddRange(disabledTypes, RiskMonitoringWorkers);
        if (!config.MLTraining)     AddRange(disabledTypes, MLTrainingWorkers);
        if (!config.MLMonitoring)   AddRange(disabledTypes, MLMonitoringWorkers);
        if (!config.Backtesting)    AddRange(disabledTypes, BacktestingWorkers);
        if (!config.Alerts)         AddRange(disabledTypes, AlertWorkers);

        if (disabledTypes.Count == 0)
            return services;

        // Walk the service collection and remove IHostedService registrations
        // for workers in disabled groups.
        int removed = 0;
        for (int i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(IHostedService))
                continue;

            var implType = descriptor.ImplementationType
                        ?? descriptor.ImplementationInstance?.GetType()
                        ?? descriptor.ImplementationFactory?.Method.ReturnType;

            if (implType is not null && disabledTypes.Contains(implType))
            {
                services.RemoveAt(i);
                removed++;
            }
        }

        // Build a logger to report what was filtered
        using var tempProvider = services.BuildServiceProvider();
        var logger = tempProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("WorkerGroupFilter");

        var enabledGroups = new List<string>();
        if (config.CoreTrading)    enabledGroups.Add("CoreTrading");
        if (config.MarketData)     enabledGroups.Add("MarketData");
        if (config.RiskMonitoring) enabledGroups.Add("RiskMonitoring");
        if (config.MLTraining)     enabledGroups.Add("MLTraining");
        if (config.MLMonitoring)   enabledGroups.Add("MLMonitoring");
        if (config.Backtesting)    enabledGroups.Add("Backtesting");
        if (config.Alerts)         enabledGroups.Add("Alerts");

        logger.LogInformation(
            "WorkerGroupFilter: enabled groups=[{Groups}], removed {Removed} worker(s)",
            string.Join(", ", enabledGroups), removed);

        return services;
    }

    private static void AddRange(HashSet<Type> set, Type[] types)
    {
        foreach (var t in types) set.Add(t);
    }
}
