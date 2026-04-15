using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Common.Services;

/// <summary>
/// Base class for hosted background workers that need consistent health reporting,
/// crash logging, and optional per-cycle instrumentation.
/// </summary>
/// <remarks>
/// <para>
/// Derived workers implement <see cref="ExecuteInstrumentedAsync(CancellationToken)"/>
/// instead of overriding <see cref="BackgroundService.ExecuteAsync(CancellationToken)"/>.
/// The base class seals <c>ExecuteAsync</c> so stop-state reporting and unhandled-exception
/// behavior stay uniform across all instrumented workers.
/// </para>
///
/// <para>
/// This class handles worker-level lifecycle concerns only. It does not automatically emit
/// metadata, heartbeats, backlog gauges, or cycle success for arbitrary loops unless the
/// derived worker explicitly calls the relevant <see cref="IWorkerHealthMonitor"/> methods
/// or uses <see cref="RunInstrumentedCycleAsync(Func{Task}, CancellationToken)"/>.
/// </para>
///
/// <para>
/// Use <see cref="RunInstrumentedCycleAsync(Func{Task}, CancellationToken)"/> when a worker
/// has a clear, repeatable unit of work and wants the base class to measure duration and
/// record cycle success or failure. If a worker needs custom timing, multiple sub-phases,
/// or richer status semantics, it can record those metrics manually inside
/// <see cref="ExecuteInstrumentedAsync(CancellationToken)"/>.
/// </para>
/// </remarks>
public abstract class InstrumentedBackgroundService : BackgroundService
{
    /// <summary>
    /// Shared trace source for worker-cycle activities. Downstream telemetry can subscribe to
    /// this source to correlate background-worker execution with broader engine operations.
    /// </summary>
    private static readonly System.Diagnostics.ActivitySource ActivitySource = new("LascodiaTradingEngine.Workers");

    /// <summary>
    /// Health monitor used by derived workers to publish liveness, cycle, backlog, and stop
    /// state into the consolidated worker-health view.
    /// </summary>
    protected readonly IWorkerHealthMonitor HealthMonitor;

    /// <summary>
    /// Logger instance exposed to derived workers so they can log within the same lifecycle
    /// wrapper that handles crash reporting.
    /// </summary>
    protected readonly ILogger Logger;
    private readonly string _workerName;

    /// <summary>
    /// Initializes the instrumentation wrapper shared by derived workers.
    /// </summary>
    /// <param name="healthMonitor">
    /// Worker health sink that receives success, failure, and stop notifications.
    /// </param>
    /// <param name="logger">
    /// Logger used for base-class crash logging and available to derived workers.
    /// </param>
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

    /// <summary>
    /// Executes the derived worker inside a sealed lifecycle wrapper that normalizes shutdown
    /// and crash reporting.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token controlled by the hosting runtime.</param>
    /// <remarks>
    /// <para>
    /// Normal host-driven cancellation is treated as an expected shutdown path and is not
    /// logged as a failure.
    /// </para>
    ///
    /// <para>
    /// Any other exception is logged as critical, reported to the health monitor, and then
    /// rethrown so the host can apply its configured
    /// <c>BackgroundServiceExceptionBehavior</c>.
    /// </para>
    ///
    /// <para>
    /// The worker is always marked as stopped in the <c>finally</c> block, even after a crash,
    /// so dashboards do not keep showing stale "running" state for a dead worker.
    /// </para>
    /// </remarks>
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ExecuteInstrumentedAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host-driven cancellation is the expected shutdown path for BackgroundService.
        }
        catch (Exception ex)
        {
            // Report the terminal crash before rethrowing so health endpoints reflect the
            // failure even if the host decides to stop or restart the process immediately.
            Logger.LogCritical(ex, "{Worker} crashed with unhandled exception", WorkerName);
            HealthMonitor.RecordWorkerStopped(WorkerName, ex.Message);
            throw; // Let the host handle it per BackgroundServiceExceptionBehavior
        }
        finally
        {
            // Always clear the worker's running state on exit so observability snapshots do not
            // leave behind a stale "healthy" process after shutdown or crash.
            HealthMonitor.RecordWorkerStopped(WorkerName);
        }
    }

    /// <summary>
    /// Implements the worker's actual logic inside the base class lifecycle wrapper.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token that should be honored promptly.</param>
    /// <remarks>
    /// Derived classes should place their main loop or long-running execution flow here
    /// instead of overriding <see cref="BackgroundService.ExecuteAsync(CancellationToken)"/>.
    /// This keeps stop-state reporting and crash handling centralized in the base class.
    /// </remarks>
    protected abstract Task ExecuteInstrumentedAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Runs one logical worker cycle with tracing, duration measurement, and automatic
    /// success or failure reporting.
    /// </summary>
    /// <param name="cycle">The unit of work to execute for this cycle.</param>
    /// <param name="ct">Cancellation token for the current worker loop.</param>
    /// <remarks>
    /// <para>
    /// This helper is useful for workers whose loop naturally breaks into repeatable cycles.
    /// It creates an <see cref="Activity"/> for tracing, tags it with the worker name, and
    /// records cycle duration to <see cref="IWorkerHealthMonitor"/>.
    /// </para>
    ///
    /// <para>
    /// Exceptions are never swallowed. Cancellation is rethrown unchanged, and other failures
    /// are reported before being rethrown so the caller can decide whether to back off, retry,
    /// or terminate the loop.
    /// </para>
    /// </remarks>
    protected async Task RunInstrumentedCycleAsync(Func<Task> cycle, CancellationToken ct)
    {
        // Emit an Activity per cycle so tracing backends can connect worker execution to
        // database, messaging, and downstream service spans created during the same cycle.
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
            // Preserve cooperative cancellation semantics so outer loops can stop promptly.
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Mark the trace span as failed before surfacing the exception to the caller.
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            HealthMonitor.RecordCycleFailure(WorkerName, ex.Message);
            throw;
        }
    }
}
