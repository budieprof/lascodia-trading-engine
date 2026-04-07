using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Common.Services;

/// <summary>
/// Base class for background workers that automatically reports health metrics
/// to <see cref="IWorkerHealthMonitor"/>. Tracks cycle success/failure and marks
/// the worker as stopped if ExecuteAsync exits.
/// </summary>
public abstract class InstrumentedBackgroundService : BackgroundService
{
    private static readonly System.Diagnostics.ActivitySource ActivitySource = new("LascodiaTradingEngine.Workers");

    protected readonly IWorkerHealthMonitor HealthMonitor;
    protected readonly ILogger Logger;
    private readonly string _workerName;

    protected InstrumentedBackgroundService(
        IWorkerHealthMonitor healthMonitor,
        ILogger logger)
    {
        HealthMonitor = healthMonitor;
        Logger = logger;
        _workerName = GetType().Name;
    }

    /// <summary>The worker name used for health reporting. Defaults to the class name.</summary>
    protected virtual string WorkerName => _workerName;

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ExecuteInstrumentedAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "{Worker} crashed with unhandled exception", WorkerName);
            HealthMonitor.RecordWorkerStopped(WorkerName, ex.Message);
            throw; // Let the host handle it per BackgroundServiceExceptionBehavior
        }
        finally
        {
            HealthMonitor.RecordWorkerStopped(WorkerName);
        }
    }

    /// <summary>Implement the worker's main loop here instead of ExecuteAsync.</summary>
    protected abstract Task ExecuteInstrumentedAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Runs a single cycle with automatic health metric recording.
    /// Call this from within your main loop.
    /// </summary>
    protected async Task RunInstrumentedCycleAsync(Func<Task> cycle, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity(GetType().Name);
        activity?.SetTag("worker.name", GetType().Name);

        var sw = Stopwatch.StartNew();
        try
        {
            await cycle();
            sw.Stop();
            HealthMonitor.RecordCycleSuccess(WorkerName, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            HealthMonitor.RecordCycleFailure(WorkerName, ex.Message);
            throw;
        }
    }
}
