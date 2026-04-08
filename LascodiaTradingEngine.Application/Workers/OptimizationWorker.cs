using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Thin hosted-service orchestrator for optimization work.
/// The execution pipeline lives in the extracted optimization services.
/// </summary>
public class OptimizationWorker : BackgroundService
{
    private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultShutdownDrainTimeout = TimeSpan.FromSeconds(30);
    private const int ProcessingFailureWindowMinutes = 60;

    private readonly ILogger<OptimizationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IOptimizationWorkerHealthStore _optimizationHealthStore;
    private readonly OptimizationConfigProvider _configProvider;
    private readonly IOptimizationWorkerLoopCoordinator _loopCoordinator;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _shutdownDrainTimeout;
    private readonly object _processingTasksGate = new();
    private readonly object _runtimeStateGate = new();
    private readonly HashSet<Task> _processingTasks = [];
    private readonly Queue<DateTime> _processingSlotFailureTimestamps = [];
    private int _configuredMaxConcurrentRuns = 1;
    private DateTime? _lastProcessingSlotFailureAtUtc;
    private string? _lastProcessingSlotFailureMessage;
    private DateTime? _lastSuccessfulConfigRefreshAtUtc;
    private bool _isConfigLoadDegraded;
    private int _consecutiveConfigLoadFailures;
    private DateTime? _lastConfigLoadFailureAtUtc;
    private string? _lastConfigLoadFailureMessage;

    private DateTime _nextScheduleScanUtc = DateTime.MinValue;

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationWorker(
        ILogger<OptimizationWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics,
        IWorkerHealthMonitor? healthMonitor,
        IOptimizationWorkerHealthStore optimizationHealthStore,
        OptimizationConfigProvider configProvider,
        IServiceProvider serviceProvider,
        TimeProvider timeProvider)
        : this(
            logger,
            scopeFactory,
            metrics,
            healthMonitor,
            optimizationHealthStore,
            configProvider,
            serviceProvider.GetRequiredService<IOptimizationWorkerLoopCoordinator>(),
            timeProvider,
            DefaultPollingInterval,
            DefaultShutdownDrainTimeout)
    {
    }

    internal OptimizationWorker(
        ILogger<OptimizationWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics,
        IWorkerHealthMonitor? healthMonitor,
        IOptimizationWorkerHealthStore optimizationHealthStore,
        OptimizationConfigProvider configProvider,
        IOptimizationWorkerLoopCoordinator loopCoordinator,
        TimeProvider timeProvider,
        TimeSpan pollingInterval,
        TimeSpan shutdownDrainTimeout)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _healthMonitor = healthMonitor;
        _optimizationHealthStore = optimizationHealthStore;
        _configProvider = configProvider;
        _loopCoordinator = loopCoordinator;
        _timeProvider = timeProvider;
        _pollingInterval = pollingInterval;
        _shutdownDrainTimeout = shutdownDrainTimeout;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OptimizationWorker starting");
        _healthMonitor?.RecordWorkerMetadata(
            OptimizationWorkerHealthNames.CoordinatorWorker,
            "Coordinates recovery, follow-up monitoring, scheduling, and processing-slot supervision.",
            _pollingInterval);
        _healthMonitor?.RecordWorkerMetadata(
            OptimizationWorkerHealthNames.ExecutionWorker,
            "Tracks optimization execution-slot liveness, queue-wait pressure, and concurrent capacity.",
            _pollingInterval);
        _healthMonitor?.RecordWorkerHeartbeat(OptimizationWorkerHealthNames.ExecutionWorker);

        try
        {
            await _loopCoordinator.WarmStartAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OptimizationWorker: crash recovery check failed (non-fatal)");
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var cycleStopwatch = Stopwatch.StartNew();
                try
                {
                    _healthMonitor?.RecordWorkerMetadata(
                        OptimizationWorkerHealthNames.CoordinatorWorker,
                        "Coordinates recovery, follow-up monitoring, scheduling, and processing-slot supervision.",
                        _pollingInterval);
                    await ExecuteCoordinatorCycleAsync(stoppingToken);
                    _healthMonitor?.RecordCycleSuccess(OptimizationWorkerHealthNames.CoordinatorWorker, cycleStopwatch.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OptimizationWorker: unexpected error in polling loop");
                    _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", OptimizationWorkerHealthNames.CoordinatorWorker));
                    _healthMonitor?.RecordCycleFailure(OptimizationWorkerHealthNames.CoordinatorWorker, ex.Message);

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                try
                {
                    await Task.Delay(_pollingInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            await DrainProcessingTasksAsync();
            _healthMonitor?.RecordWorkerStopped(OptimizationWorkerHealthNames.ExecutionWorker);
            _healthMonitor?.RecordWorkerStopped(OptimizationWorkerHealthNames.CoordinatorWorker);
            _logger.LogInformation("OptimizationWorker stopped");
        }
    }

    internal async Task<bool> ProcessNextQueuedRunAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var runProcessor = scope.ServiceProvider.GetRequiredService<IOptimizationRunProcessor>();
        return await runProcessor.ProcessNextQueuedRunAsync(ct);
    }

    private async Task<OptimizationConfig> GetOrLoadConfigAsync(DbContext db, CancellationToken ct)
        => await _configProvider.LoadAsync(db, ct);

    private async Task ExecuteCoordinatorCycleAsync(CancellationToken ct)
    {
        var cycleConfig = await LoadCycleConfigSnapshotAsync(ct);
        bool shouldRunScheduling = UtcNow >= _nextScheduleScanUtc;
        if (shouldRunScheduling)
            _nextScheduleScanUtc = UtcNow.AddSeconds(cycleConfig.Config.SchedulePollSeconds);

        await _loopCoordinator.ExecuteCycleAsync(
            new OptimizationWorkerCycleContext(
                cycleConfig.Config,
                cycleConfig.LastConfigRefreshUtc,
                cycleConfig.NextConfigRefreshUtc,
                shouldRunScheduling && cycleConfig.Config.AutoScheduleEnabled),
            ct);

        await EnsureProcessingCapacityAsync(cycleConfig.Config.MaxConcurrentRuns, ct);
    }

    private async Task<CycleConfigSnapshot> LoadCycleConfigSnapshotAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        try
        {
            var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var db = readCtx.GetDbContext();
            var config = await GetOrLoadConfigAsync(db, ct);
            var cacheSnapshot = _configProvider.GetCacheSnapshot();
            lock (_runtimeStateGate)
            {
                _configuredMaxConcurrentRuns = Math.Max(1, config.MaxConcurrentRuns);
                _lastSuccessfulConfigRefreshAtUtc = cacheSnapshot.LastLoadedAtUtc == default
                    ? UtcNow
                    : cacheSnapshot.LastLoadedAtUtc;
                _isConfigLoadDegraded = false;
                _consecutiveConfigLoadFailures = 0;
                _lastConfigLoadFailureAtUtc = null;
                _lastConfigLoadFailureMessage = null;
            }

            UpdateRuntimeHealthState();
            return new CycleConfigSnapshot(
                config,
                cacheSnapshot.LastLoadedAtUtc,
                cacheSnapshot.NextRefreshDueAtUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_runtimeStateGate)
            {
                _isConfigLoadDegraded = true;
                _consecutiveConfigLoadFailures++;
                _lastConfigLoadFailureAtUtc = UtcNow;
                _lastConfigLoadFailureMessage = TruncateErrorMessage(ex.Message);
            }

            UpdateRuntimeHealthState();
            throw;
        }
    }

    private Task EnsureProcessingCapacityAsync(int configuredMaxConcurrentRuns, CancellationToken ct)
    {
        int maxConcurrentRuns = Math.Max(1, configuredMaxConcurrentRuns);
        lock (_runtimeStateGate)
            _configuredMaxConcurrentRuns = maxConcurrentRuns;

        PruneCompletedProcessingTasks();

        int activeProcessingTasks = GetActiveProcessingTaskCount();
        int slotsToLaunch = Math.Max(0, maxConcurrentRuns - activeProcessingTasks);
        for (int i = 0; i < slotsToLaunch; i++)
            StartProcessingSlot(ct);

        int activeAfterLaunch = activeProcessingTasks + slotsToLaunch;
        _metrics.OptimizationActiveProcessingSlots.Record(activeAfterLaunch);
        _metrics.OptimizationProcessingSlotUtilization.Record((double)activeAfterLaunch / maxConcurrentRuns);
        UpdateRuntimeHealthState();
        return Task.CompletedTask;
    }

    private void StartProcessingSlot(CancellationToken ct)
    {
        var task = RunProcessingSlotAsync(ct);
        lock (_processingTasksGate)
            _processingTasks.Add(task);

        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_processingTasksGate)
                    _processingTasks.Remove(completedTask);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunProcessingSlotAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextQueuedRunAsync(ct))
                    break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OptimizationWorker: processing slot crashed unexpectedly");
                _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", OptimizationWorkerHealthNames.ExecutionWorker));
                RecordProcessingSlotFailure(ex);
                break;
            }
        }

        UpdateRuntimeHealthState();
    }

    private void PruneCompletedProcessingTasks()
    {
        lock (_processingTasksGate)
            _processingTasks.RemoveWhere(task => task.IsCompleted);
    }

    private async Task DrainProcessingTasksAsync()
    {
        Task[] processingTasks;
        lock (_processingTasksGate)
            processingTasks = _processingTasks.ToArray();

        if (processingTasks.Length == 0)
            return;

        try
        {
            var allProcessingTasks = Task.WhenAll(processingTasks);
            var completedTask = await Task.WhenAny(
                allProcessingTasks,
                Task.Delay(_shutdownDrainTimeout));

            if (completedTask != allProcessingTasks)
            {
                _logger.LogWarning(
                    "OptimizationWorker: shutdown drain timed out after {TimeoutSeconds}s with {Remaining} processing slot(s) still active",
                    _shutdownDrainTimeout.TotalSeconds,
                    processingTasks.Count(task => !task.IsCompleted));
                return;
            }

            await allProcessingTasks;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OptimizationWorker: processing slot shutdown observed a non-fatal exception");
        }
    }

    private void RecordProcessingSlotFailure(Exception ex)
    {
        lock (_runtimeStateGate)
        {
            _processingSlotFailureTimestamps.Enqueue(UtcNow);
            PruneProcessingSlotFailuresLocked();
            _lastProcessingSlotFailureAtUtc = UtcNow;
            _lastProcessingSlotFailureMessage = TruncateErrorMessage(ex.Message);
        }

        UpdateRuntimeHealthState();
    }

    private void UpdateRuntimeHealthState()
    {
        int activeProcessingSlots = GetActiveProcessingTaskCount();
        int processingSlotFailuresLastHour;
        int configuredMaxConcurrentRuns;
        DateTime? lastProcessingSlotFailureAtUtc;
        string? lastProcessingSlotFailureMessage;
        DateTime? lastSuccessfulConfigRefreshAtUtc;
        bool isConfigLoadDegraded;
        int consecutiveConfigLoadFailures;
        DateTime? lastConfigLoadFailureAtUtc;
        string? lastConfigLoadFailureMessage;

        lock (_runtimeStateGate)
        {
            PruneProcessingSlotFailuresLocked();
            processingSlotFailuresLastHour = _processingSlotFailureTimestamps.Count;
            configuredMaxConcurrentRuns = _configuredMaxConcurrentRuns;
            lastProcessingSlotFailureAtUtc = _lastProcessingSlotFailureAtUtc;
            lastProcessingSlotFailureMessage = _lastProcessingSlotFailureMessage;
            lastSuccessfulConfigRefreshAtUtc = _lastSuccessfulConfigRefreshAtUtc;
            isConfigLoadDegraded = _isConfigLoadDegraded;
            consecutiveConfigLoadFailures = _consecutiveConfigLoadFailures;
            lastConfigLoadFailureAtUtc = _lastConfigLoadFailureAtUtc;
            lastConfigLoadFailureMessage = _lastConfigLoadFailureMessage;
        }

        var queueWaitPercentiles = _optimizationHealthStore.GetQueueWaitPercentiles();
        _healthMonitor?.RecordWorkerHeartbeat(OptimizationWorkerHealthNames.ExecutionWorker);
        _optimizationHealthStore.UpdateMainWorkerState(current => current with
        {
            ActiveProcessingSlots = activeProcessingSlots,
            ConfiguredMaxConcurrentRuns = configuredMaxConcurrentRuns,
            ProcessingSlotFailuresLastHour = processingSlotFailuresLastHour,
            LastProcessingSlotFailureAtUtc = lastProcessingSlotFailureAtUtc,
            LastProcessingSlotFailureMessage = lastProcessingSlotFailureMessage,
            QueueWaitP50Ms = queueWaitPercentiles.P50Ms,
            QueueWaitP95Ms = queueWaitPercentiles.P95Ms,
            QueueWaitP99Ms = queueWaitPercentiles.P99Ms,
            LastSuccessfulConfigRefreshAtUtc = lastSuccessfulConfigRefreshAtUtc,
            IsConfigLoadDegraded = isConfigLoadDegraded,
            ConsecutiveConfigLoadFailures = consecutiveConfigLoadFailures,
            LastConfigLoadFailureAtUtc = lastConfigLoadFailureAtUtc,
            LastConfigLoadFailureMessage = lastConfigLoadFailureMessage,
        });
    }

    private int GetActiveProcessingTaskCount()
    {
        lock (_processingTasksGate)
            return _processingTasks.Count;
    }

    private void PruneProcessingSlotFailuresLocked()
    {
        var cutoff = UtcNow.AddMinutes(-ProcessingFailureWindowMinutes);
        while (_processingSlotFailureTimestamps.Count > 0
            && _processingSlotFailureTimestamps.Peek() < cutoff)
        {
            _processingSlotFailureTimestamps.Dequeue();
        }
    }

    private static string? TruncateErrorMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Length <= 500 ? value : value[..500];
    }

    private readonly record struct CycleConfigSnapshot(
        OptimizationConfig Config,
        DateTime LastConfigRefreshUtc,
        DateTime NextConfigRefreshUtc);
}
