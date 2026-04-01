using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically enforces data retention policies: purges aged records from hot storage,
/// cleans up expired idempotency keys, and trims worker health snapshots.
/// </summary>
public class DataRetentionWorker : BackgroundService
{
    private readonly ILogger<DataRetentionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DataRetentionOptions _options;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private int _consecutiveFailures;

    public DataRetentionWorker(
        ILogger<DataRetentionWorker> logger,
        IServiceScopeFactory scopeFactory,
        DataRetentionOptions options)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _options      = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DataRetentionWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var retentionManager = scope.ServiceProvider.GetRequiredService<IDataRetentionManager>();

                var results = await retentionManager.EnforceRetentionAsync(stoppingToken);

                var totalPurged = results.Sum(r => r.RowsPurged);
                if (totalPurged > 0)
                    _logger.LogInformation("DataRetentionWorker: purged {Total} records across {Types} entity types",
                        totalPurged, results.Count(r => r.RowsPurged > 0));

                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "DataRetentionWorker error (failure #{Count})", _consecutiveFailures);
            }

            var baseInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    baseInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : baseInterval;

            await Task.Delay(delay, stoppingToken);
        }
    }
}
