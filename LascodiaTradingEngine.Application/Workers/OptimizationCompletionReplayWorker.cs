using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Common.Services;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Dedicated recovery worker for terminal optimization completion-event publication.
/// Keeps replay load off the main optimization execution loop.
/// </summary>
public sealed class OptimizationCompletionReplayWorker : InstrumentedBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const string WorkerName = nameof(OptimizationCompletionReplayWorker);

    private readonly ILogger<OptimizationCompletionReplayWorker> _logger;
    private readonly OptimizationCompletionPublisher _publisher;
    private readonly IWorkerHealthMonitor _healthMonitor;

    public OptimizationCompletionReplayWorker(
        ILogger<OptimizationCompletionReplayWorker> logger,
        OptimizationCompletionPublisher publisher,
        IWorkerHealthMonitor healthMonitor)
        : base(healthMonitor, logger)
    {
        _logger = logger;
        _publisher = publisher;
        _healthMonitor = healthMonitor;
    }

    protected override async Task ExecuteInstrumentedAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} starting", WorkerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStart = DateTime.UtcNow;
            try
            {
                int replayed = await _publisher.ReplayPendingAsync(batchSize: 10, stoppingToken);
                _healthMonitor.RecordBacklogDepth(WorkerName, replayed);
                _healthMonitor.RecordWorkerMetadata(WorkerName, null, PollInterval);
                _healthMonitor.RecordCycleSuccess(WorkerName, (long)(DateTime.UtcNow - cycleStart).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _healthMonitor.RecordCycleFailure(WorkerName, ex.Message);
                _logger.LogError(ex, "{Worker}: replay cycle failed", WorkerName);
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("{Worker} stopped", WorkerName);
    }
}
