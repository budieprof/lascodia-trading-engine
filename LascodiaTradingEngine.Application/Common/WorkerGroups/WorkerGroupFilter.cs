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
        typeof(PositionWorker),
        typeof(TrailingStopWorker),
        typeof(OrderFilledEventHandler),
        typeof(PositionClosedEventHandler),
        typeof(PartialFillResubmissionWorker),
        typeof(EAHealthMonitorWorker),
        typeof(ReconciliationWorker),
        typeof(TcpBridgeWorker),
    ];

    private static readonly Type[] MarketDataWorkers =
    [
        typeof(RegimeDetectionWorker),
        typeof(SentimentWorker),
        typeof(NewsSentimentWorker),
        typeof(COTDataWorker),
        typeof(EconomicCalendarWorker),
    ];

    private static readonly Type[] RiskMonitoringWorkers =
    [
        typeof(RiskMonitorWorker),
        typeof(DrawdownMonitorWorker),
        typeof(DrawdownRecoveryWorker),
        typeof(ExecutionQualityCircuitBreakerWorker),
        typeof(BrokerPnLReconciliationWorker),
        typeof(StrategyHealthWorker),
        typeof(StrategyFeedbackWorker),
        typeof(CorrelationMatrixWorker),
        typeof(SlippageDriftWorker),
    ];

    private static readonly Type[] MLTrainingWorkers =
    [
        typeof(MLTrainingWorker),
        typeof(MLShadowArbiterWorker),
        typeof(MLPredictionOutcomeWorker),
        typeof(MLMultiHorizonOutcomeWorker),
        typeof(MLModelActivatedEventHandler),
        typeof(MLModelRetirementWorker),
        typeof(MLTrainingRunHealthWorker),
        typeof(MLTrainingDataFreshnessWorker),
        typeof(MLTransferLearningWorker),
        typeof(MLModelDistillationWorker),
        typeof(MLAverageWeightInitWorker),
        typeof(MLOnlineLearningWorker),
        typeof(MLPredictionLogPruningWorker),
        typeof(MLDeadLetterWorker),
        typeof(CpcPretrainerWorker),
    ];

    private static readonly Type[] MLMonitoringWorkers =
    [
        typeof(MLAdaptiveThresholdWorker),
        typeof(MLAdwinDriftWorker),
        typeof(MLCalibratedEdgeWorker),
        typeof(MLCausalFeatureWorker),
        typeof(MLConformalBreakerWorker),
        typeof(MLConformalCoverageBackfillWorker),
        typeof(MLConformalCalibrationWorker),
        typeof(MLConformalRecalibrationWorker),
        typeof(MLCorrelatedSignalConflictWorker),
        typeof(MLCovariateShiftWorker),
        typeof(MLCusumDriftWorker),
        typeof(MLDataQualityWorker),
        typeof(MLDegradationModeWorker),
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
