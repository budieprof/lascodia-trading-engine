using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that synchronizes the latest published CFTC Commitment of Traders
/// snapshots for currencies referenced by active instruments.
/// </summary>
public sealed class COTDataWorker : BackgroundService
{
    internal const string WorkerName = nameof(COTDataWorker);

    private const string DistributedLockKey = "workers:cot-data:cycle";

    private static readonly TimeSpan PollingInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<COTDataWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedLock _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;

    public COTDataWorker(
        ILogger<COTDataWorker> logger,
        IServiceScopeFactory scopeFactory,
        IDistributedLock distributedLock,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _distributedLock = distributedLock;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} starting.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Synchronizes the latest published CFTC Commitment of Traders reports for active currencies.",
            PollingInterval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var cycleStarted = Stopwatch.GetTimestamp();

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                    var result = await RunCycleAsync(stoppingToken);
                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;

                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.PendingCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "{Worker}: cycle complete; activePairs={Pairs}, currencies={Currencies}, supported={Supported}, published={Published}, created={Created}, repaired={Repaired}, unchanged={Unchanged}, unavailable={Unavailable}, fetchFailed={FetchFailed}, persistFailed={PersistFailed}.",
                            WorkerName,
                            result.ActivePairCount,
                            result.CurrencyCount,
                            result.SupportedCurrencyCount,
                            result.PublishedReportCount,
                            result.CreatedCount,
                            result.RepairedCount,
                            result.UnchangedCount,
                            result.UnavailableCount,
                            result.FetchFailedCount,
                            result.PersistFailedCount);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _metrics?.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("reason", "cot_cycle"));
                    _logger.LogError(ex, "{Worker}: unexpected error in polling loop.", WorkerName);
                }

                try
                {
                    await Task.Delay(PollingInterval, _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<COTReportSyncResult> RunCycleAsync(CancellationToken ct)
    {
        await using var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, LockTimeout, ct);

        if (cycleLock == null)
            return COTReportSyncResult.Skipped("lock_busy");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<ICOTReportSyncService>();
        return await syncService.SyncLatestPublishedReportsAsync(ct);
    }
}
