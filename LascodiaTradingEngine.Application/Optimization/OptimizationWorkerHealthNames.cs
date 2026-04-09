namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationWorkerHealthNames
{
    internal const string CoordinatorWorker = "OptimizationWorker";
    internal const string ExecutionWorker = "OptimizationExecutionWorker";
    internal const string CompletionReplayWorker = "OptimizationCompletionReplayWorker";

    internal static class Phases
    {
        internal const string WarmStart = "warm_start";
        internal const string StaleRunningRecovery = "stale_running_recovery";
        internal const string StaleQueuedDetection = "stale_queued_detection";
        internal const string RetryFailedRuns = "retry_failed_runs";
        internal const string LifecycleReconciliation = "lifecycle_reconciliation";
        internal const string FollowUpMonitoring = "follow_up_monitoring";
        internal const string AutoScheduling = "auto_scheduling";
        internal const string HealthRecording = "health_recording";
    }
}
