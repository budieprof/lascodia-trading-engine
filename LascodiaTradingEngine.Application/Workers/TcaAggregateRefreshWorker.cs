using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically refreshes the tca_daily_aggregates materialized view for dashboard performance.
/// Uses REFRESH MATERIALIZED VIEW CONCURRENTLY to avoid locking reads during refresh.
/// </summary>
public class TcaAggregateRefreshWorker : BackgroundService
{
    private readonly ILogger<TcaAggregateRefreshWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private const int DefaultPollSeconds = 3600; // Hourly
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(4);
    private int _consecutiveFailures;

    public TcaAggregateRefreshWorker(
        ILogger<TcaAggregateRefreshWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TcaAggregateRefreshWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshViewAsync(stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "TcaAggregateRefreshWorker error (failure #{Count})", _consecutiveFailures);
            }

            var baseInterval = TimeSpan.FromSeconds(DefaultPollSeconds);
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    baseInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : baseInterval;

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task RefreshViewAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();

        // CONCURRENTLY allows reads during refresh (requires unique index on the view)
        await writeCtx.GetDbContext().Database.ExecuteSqlRawAsync(
            "REFRESH MATERIALIZED VIEW CONCURRENTLY tca_daily_aggregates", ct);

        _logger.LogInformation("TcaAggregateRefreshWorker: refreshed tca_daily_aggregates view");
    }
}
