namespace LascodiaTradingEngine.Application.Common.Services;

/// <summary>
/// Provides phased startup delays for background workers to prevent connection pool
/// exhaustion and DB thundering herd at application boot. Workers are grouped by
/// criticality: core trading workers start immediately, while monitoring workers
/// are delayed by 15-20 seconds.
/// </summary>
public static class WorkerStartupSequencer
{
    private static readonly Dictionary<string, int> PhaseDelaySeconds = new()
    {
        ["CoreTrading"]    = 0,   // StrategyWorker, SignalOrderBridge, PositionWorker
        ["MarketData"]     = 2,   // RegimeDetection, CandleAggregation, Correlation, Spread
        ["Risk"]           = 5,   // RiskMonitor, Drawdown, Portfolio, StressTest, DailyPnl
        ["Lifecycle"]      = 8,   // StrategyHealth, StrategyGeneration, StrategyFeedback, Optimization
        ["MLTraining"]     = 12,  // MLTrainingWorker, MLShadowArbiter, MLModelWarmup
        ["MLMonitoring"]   = 15,  // 82 ML drift/calibration/accuracy workers
        ["Infrastructure"] = 20,  // DataRetention, DeadLetterCleanup, WorkerHealth, Reconciliation
    };

    /// <summary>
    /// Returns the startup delay for a worker based on its name. Workers in the same
    /// phase are staggered by a small jitter (0-500ms) to avoid synchronized DB hits.
    /// </summary>
    public static TimeSpan GetDelay(string workerName)
    {
        var phase = ClassifyWorker(workerName);
        int baseDelay = PhaseDelaySeconds.GetValueOrDefault(phase, 20);

        // Add small jitter (0-500ms) based on worker name hash to desynchronize within phase
        int jitterMs = Math.Abs(workerName.GetHashCode()) % 500;
        return TimeSpan.FromSeconds(baseDelay) + TimeSpan.FromMilliseconds(jitterMs);
    }

    private static string ClassifyWorker(string name)
    {
        return name switch
        {
            "StrategyWorker" or "SignalOrderBridgeWorker" or "PositionWorker"
                or "TrailingStopWorker" or "TcpBridgeWorker"
                => "CoreTrading",

            _ when name.Contains("Regime") || name.Contains("CandleAggregation")
                || name.Contains("Correlation") || name.Contains("Spread")
                || name.Contains("EconomicCalendar") || name.Contains("EAHealth")
                || name.Contains("EACommandPush")
                => "MarketData",

            _ when name.Contains("Risk") || name.Contains("Drawdown")
                || name.Contains("Portfolio") || name.Contains("StressTest")
                || name.Contains("DailyPnl") || name.Contains("ExecutionQuality")
                => "Risk",

            _ when name.Contains("StrategyGeneration") || name.Contains("StrategyHealth")
                || name.Contains("StrategyFeedback") || name.Contains("StrategyCapacity")
                || name.Contains("StrategyPromotion") || name.Contains("Optimization")
                || name.Contains("Backtest") || name.Contains("WalkForward")
                => "Lifecycle",

            _ when name.StartsWith("MLTraining") || name.Contains("ShadowArbiter")
                || name.Contains("MLModelWarmup") || name.Contains("MLModelRetirement")
                => "MLTraining",

            _ when name.StartsWith("ML")
                => "MLMonitoring",

            _ => "Infrastructure",
        };
    }
}
