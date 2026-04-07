using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Coordinates worker startup ordering by waiting for infrastructure-tier workers to
/// report healthy before allowing data-tier and core-tier workers to begin processing.
/// Registered as a hosted service that runs before other workers via early registration.
///
/// Tier ordering:
///   0. Infrastructure: WorkerHealthWorker, EAHealthMonitorWorker, DataRetentionWorker
///   1. Data: CandleAggregationWorker, RegimeDetectionWorker, CorrelationMatrixWorker
///   2. Core: StrategyWorker, SignalOrderBridgeWorker, PositionWorker, TrailingStopWorker
///   3. ML: MLTrainingWorker, MLDriftMonitorWorker, MLShadowArbiterWorker
///   4. Optimization: OptimizationWorker, StrategyGenerationWorker, BacktestWorker
///
/// Workers check <see cref="IsReady"/> before starting their main loops.
/// The orchestrator publishes readiness once Tier 0 workers report at least one
/// healthy cycle via IWorkerHealthMonitor, or after a configurable timeout (default 60s).
/// </summary>
public class WorkerStartupOrchestrator : BackgroundService
{
    private readonly ILogger<WorkerStartupOrchestrator> _logger;
    private readonly IWorkerHealthMonitor _healthMonitor;
    private readonly TaskCompletionSource _readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const int StartupTimeoutSeconds = 60;

    /// <summary>
    /// True once infrastructure-tier workers have reported healthy (or timeout elapsed).
    /// Non-infrastructure workers should await <see cref="WaitForReadyAsync"/> before processing.
    /// </summary>
    public bool IsReady => _readySignal.Task.IsCompleted;

    public Task WaitForReadyAsync(CancellationToken ct) => _readySignal.Task.WaitAsync(ct);

    private static readonly HashSet<string> InfrastructureWorkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "WorkerHealthWorker", "EAHealthMonitorWorker", "DataRetentionWorker"
    };

    public WorkerStartupOrchestrator(
        ILogger<WorkerStartupOrchestrator> logger,
        IWorkerHealthMonitor healthMonitor)
    {
        _logger        = logger;
        _healthMonitor = healthMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WorkerStartupOrchestrator: waiting for infrastructure workers (timeout={Timeout}s)", StartupTimeoutSeconds);

        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);

        while (!stoppingToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            var snapshots = _healthMonitor.GetCurrentSnapshots();
            var healthyInfra = snapshots
                .Where(s => InfrastructureWorkers.Contains(s.WorkerName) && s.LastSuccessAt.HasValue)
                .Select(s => s.WorkerName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (healthyInfra.Count >= InfrastructureWorkers.Count)
            {
                _logger.LogInformation(
                    "WorkerStartupOrchestrator: all infrastructure workers healthy — signalling ready");
                _readySignal.TrySetResult();
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }

        _logger.LogWarning(
            "WorkerStartupOrchestrator: timeout elapsed ({Timeout}s) — proceeding with available workers",
            StartupTimeoutSeconds);
        _readySignal.TrySetResult();
    }
}
