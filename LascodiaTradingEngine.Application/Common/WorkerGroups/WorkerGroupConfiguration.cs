namespace LascodiaTradingEngine.Application.Common.WorkerGroups;

/// <summary>
/// Configuration section that controls which worker groups are active in this instance.
/// Bound from <c>WorkerGroups</c> in appsettings.json.
///
/// Deployment examples:
/// <list type="bullet">
///   <item><b>All-in-one (dev/staging):</b> <c>EnableAll = true</c> — starts everything.</item>
///   <item><b>API + Core Trading:</b> <c>CoreTrading = true, MarketData = true, RiskMonitoring = true</c></item>
///   <item><b>ML Worker Host:</b> <c>MLTraining = true, MLMonitoring = true</c></item>
///   <item><b>Backtesting Host:</b> <c>Backtesting = true</c></item>
/// </list>
/// </summary>
public sealed class WorkerGroupConfiguration
{
    /// <summary>When true, all worker groups are enabled regardless of individual flags.</summary>
    public bool EnableAll       { get; set; } = true;

    /// <summary>
    /// Core trading pipeline: StrategyWorker, SignalOrderBridgeWorker, OrderExecutionWorker,
    /// PositionWorker, TrailingStopWorker, AccountSyncWorker, OrderFilledEventHandler,
    /// PositionClosedEventHandler.
    /// </summary>
    public bool CoreTrading     { get; set; }

    /// <summary>
    /// Market data ingestion: MarketDataWorker, RegimeDetectionWorker, SentimentWorker,
    /// COTDataWorker, EconomicCalendarWorker.
    /// </summary>
    public bool MarketData      { get; set; }

    /// <summary>
    /// Risk and monitoring: RiskMonitorWorker, DrawdownMonitorWorker, DrawdownRecoveryWorker,
    /// ExecutionQualityCircuitBreakerWorker, StrategyHealthWorker, StrategyFeedbackWorker.
    /// </summary>
    public bool RiskMonitoring  { get; set; }

    /// <summary>
    /// ML training pipeline: MLTrainingWorker, MLShadowArbiterWorker, MLPredictionOutcomeWorker,
    /// MLModelActivatedEventHandler, MLModelRetirementWorker, MLTrainingRunHealthWorker,
    /// MLTrainingDataFreshnessWorker, MLTransferLearningWorker, MLModelDistillationWorker,
    /// MLMamlMetaLearnerWorker, MLOnlineLearningWorker, MLPredictionLogPruningWorker.
    /// </summary>
    public bool MLTraining      { get; set; }

    /// <summary>
    /// ML monitoring and calibration: all ML drift, accuracy, calibration, feature,
    /// and signal workers not in MLTraining.
    /// </summary>
    public bool MLMonitoring    { get; set; }

    /// <summary>
    /// Backtesting and optimization: BacktestWorker, OptimizationWorker, WalkForwardWorker.
    /// </summary>
    public bool Backtesting     { get; set; }

    /// <summary>
    /// Alert dispatch: AlertWorker.
    /// </summary>
    public bool Alerts          { get; set; }
}
