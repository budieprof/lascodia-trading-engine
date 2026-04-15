using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background worker that coordinates scheduled strategy-generation polling, manual
/// generation requests, and operator-facing health telemetry for the generation pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This worker is intentionally thin. It does not contain the generation logic itself;
/// instead, it delegates schedule/concurrency decisions to
/// <see cref="IStrategyGenerationScheduler"/> and hands the actual end-to-end generation
/// run to <see cref="IStrategyGenerationCycleRunner"/>. That separation keeps the hosted
/// service focused on lifecycle management, while the domain workflow stays testable in
/// dedicated services.
/// </para>
///
/// <para>
/// Each orchestration boundary creates a fresh DI scope. This avoids carrying scoped
/// dependencies such as EF Core contexts, locks, or config snapshots across long-running
/// cycles and allows the scheduler and cycle runner to resolve their own isolated
/// dependency graphs.
/// </para>
///
/// <para>
/// The worker also bridges generation-specific health state into the generic worker
/// monitoring system. Backlog depth is reported as the sum of pending replay artifacts and
/// unresolved strategy-generation failures, giving operators a single "outstanding work"
/// gauge for this subsystem.
/// </para>
/// </remarks>
public sealed class StrategyGenerationWorker : InstrumentedBackgroundService
{
    /// <summary>
    /// Startup grace period before the first schedule evaluation. This gives the host time
    /// to finish warming shared infrastructure and reduces noisy first-run failures during
    /// application boot.
    /// </summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cadence for periodic strategy-generation schedule checks.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStrategyGenerationHealthStore _strategyGenerationHealthStore;

    public StrategyGenerationWorker(
        ILogger<StrategyGenerationWorker> logger,
        IServiceScopeFactory scopeFactory,
        IWorkerHealthMonitor healthMonitor,
        IStrategyGenerationHealthStore strategyGenerationHealthStore)
        : base(healthMonitor, logger)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _strategyGenerationHealthStore = strategyGenerationHealthStore;
    }

    protected override string WorkerName => nameof(StrategyGenerationWorker);

    /// <summary>
    /// Runs the timed polling loop for strategy generation and records worker-level health
    /// metrics for every cycle attempt.
    /// </summary>
    /// <remarks>
    /// A poll cycle may either execute a full generation run or be skipped by the scheduler;
    /// both outcomes still count as a successful, healthy loop iteration as long as the
    /// orchestration path itself completes without error.
    /// </remarks>
    protected override async Task ExecuteInstrumentedAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyGenerationWorker starting");
        HealthMonitor.RecordWorkerMetadata(
            WorkerName,
            "Coordinates scheduled strategy generation, deferred artifact replay, and cycle health reporting.",
            PollInterval);

        // Let the rest of the host finish bootstrapping before we attempt the first poll.
        await Task.Delay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            long cycleStarted = Stopwatch.GetTimestamp();
            try
            {
                // Record liveness before asking the scheduler whether a real generation run
                // should happen so skipped cycles still show the worker as healthy and active.
                HealthMonitor.RecordWorkerHeartbeat(WorkerName);
                await ExecutePollAsync(stoppingToken);
                HealthMonitor.RecordCycleSuccess(
                    WorkerName,
                    (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                HealthMonitor.RecordCycleFailure(WorkerName, ex.Message);
                _logger.LogError(ex, "StrategyGenerationWorker: polling cycle failed");
            }

            // Refresh backlog metrics after every attempt, including failures, so dashboards
            // keep reflecting deferred artifacts and unresolved issues that still need work.
            RecordBacklogDepth();
            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("StrategyGenerationWorker stopped");
    }

    /// <summary>
    /// Resolves the strategy-generation scheduler and lets it evaluate whether the current
    /// polling window should execute a generation cycle.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token tied to worker shutdown.</param>
    internal async Task ExecutePollAsync(CancellationToken stoppingToken)
    {
        // Scope the scheduler per poll so any scoped coordination state is isolated to the
        // current evaluation and disposed before the next polling window.
        using var scope = _scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<IStrategyGenerationScheduler>();
        await scheduler.ExecutePollAsync(RunGenerationCycleCoreAsync, stoppingToken);
    }

    /// <summary>
    /// Triggers a manual generation run while still flowing through the shared scheduler.
    /// </summary>
    /// <param name="ct">Cancellation token for the manual run request.</param>
    /// <remarks>
    /// Manual invocations reuse scheduler coordination instead of calling the cycle runner
    /// directly so the same serialization, duplicate-run protection, and schedule-state
    /// bookkeeping apply to both operator-triggered and timed execution paths.
    /// </remarks>
    internal async Task RunGenerationCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<IStrategyGenerationScheduler>();
        await scheduler.ExecuteManualRunAsync(RunGenerationCycleCoreAsync, ct);
        RecordBacklogDepth();
    }

    /// <summary>
    /// Executes the actual strategy-generation cycle body passed into the scheduler.
    /// </summary>
    /// <param name="ct">Cancellation token for the generation cycle.</param>
    private async Task RunGenerationCycleCoreAsync(CancellationToken ct)
    {
        // Run the heavyweight generation pipeline in a fresh scope so it gets its own set of
        // scoped services instead of reusing the scheduler's coordination scope.
        using var scope = _scopeFactory.CreateScope();
        var cycleRunner = scope.ServiceProvider.GetRequiredService<IStrategyGenerationCycleRunner>();
        await cycleRunner.RunAsync(ct);
        RecordBacklogDepth();
    }

    /// <summary>
    /// Publishes a single backlog depth gauge for operations dashboards.
    /// </summary>
    /// <remarks>
    /// Pending artifacts represent deferred post-persist work that can still be replayed,
    /// while unresolved failures represent issues that still require intervention. Combining
    /// them yields one actionable "work outstanding" metric for the strategy-generation
    /// subsystem.
    /// </remarks>
    private void RecordBacklogDepth()
    {
        var state = _strategyGenerationHealthStore.GetState();
        int backlogDepth = Math.Max(0, state.PendingArtifacts + state.UnresolvedFailures);
        HealthMonitor.RecordBacklogDepth(WorkerName, backlogDepth);
    }
}
