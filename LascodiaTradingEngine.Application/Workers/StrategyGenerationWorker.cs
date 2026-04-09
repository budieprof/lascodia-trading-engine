using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;

namespace LascodiaTradingEngine.Application.Workers;

public sealed class StrategyGenerationWorker : InstrumentedBackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
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

    protected override async Task ExecuteInstrumentedAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyGenerationWorker starting");
        HealthMonitor.RecordWorkerMetadata(
            WorkerName,
            "Coordinates scheduled strategy generation, deferred artifact replay, and cycle health reporting.",
            PollInterval);

        await Task.Delay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            long cycleStarted = Stopwatch.GetTimestamp();
            try
            {
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

            RecordBacklogDepth();
            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("StrategyGenerationWorker stopped");
    }

    internal async Task ExecutePollAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<IStrategyGenerationScheduler>();
        await scheduler.ExecutePollAsync(RunGenerationCycleCoreAsync, stoppingToken);
    }

    internal async Task RunGenerationCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<IStrategyGenerationScheduler>();
        await scheduler.ExecuteManualRunAsync(RunGenerationCycleCoreAsync, ct);
        RecordBacklogDepth();
    }

    private async Task RunGenerationCycleCoreAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var cycleRunner = scope.ServiceProvider.GetRequiredService<IStrategyGenerationCycleRunner>();
        await cycleRunner.RunAsync(ct);
        RecordBacklogDepth();
    }

    private void RecordBacklogDepth()
    {
        var state = _strategyGenerationHealthStore.GetState();
        int backlogDepth = Math.Max(0, state.PendingArtifacts + state.UnresolvedFailures);
        HealthMonitor.RecordBacklogDepth(WorkerName, backlogDepth);
    }
}
