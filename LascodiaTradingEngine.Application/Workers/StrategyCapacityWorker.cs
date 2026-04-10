using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically recalculates capital capacity for all active strategies.
/// Updates Strategy.EstimatedCapacityLots and persists StrategyCapacity snapshots.
/// </summary>
public class StrategyCapacityWorker : BackgroundService
{
    private readonly ILogger<StrategyCapacityWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private const int DefaultPollSeconds = 3600; // Hourly
    private const string CK_PollSecs = "StrategyCapacity:PollIntervalSeconds";
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);
    private int _consecutiveFailures;

    public StrategyCapacityWorker(
        ILogger<StrategyCapacityWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyCapacityWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSeconds;
            try
            {
                await UpdateCapacitiesAsync(stoppingToken);
                _consecutiveFailures = 0;

                // Hot-reload the poll interval from EngineConfig on every cycle
                using var configScope = _scopeFactory.CreateScope();
                var configCtx = configScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                pollSecs = await GetConfigAsync<int>(configCtx, CK_PollSecs, DefaultPollSeconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "StrategyCapacityWorker error (failure #{Count})", _consecutiveFailures);
            }

            var baseInterval = TimeSpan.FromSeconds(pollSecs);
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    baseInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : baseInterval;

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task UpdateCapacitiesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var writeCtx  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var estimator = scope.ServiceProvider.GetRequiredService<IStrategyCapacityEstimator>();

        var strategies = await writeCtx.GetDbContext()
            .Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .ToListAsync(ct);

        foreach (var strategy in strategies)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var capacity = await estimator.EstimateAsync(strategy, ct);
                await writeCtx.GetDbContext().Set<StrategyCapacity>().AddAsync(capacity, ct);

                // Update the strategy's cached capacity — entity is already tracked
                // by the write context, so mutations are consistent.
                strategy.EstimatedCapacityLots = capacity.CapacityCeilingLots;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "StrategyCapacityWorker: failed to estimate capacity for strategy {Id}",
                    strategy.Id);
            }
        }

        await writeCtx.GetDbContext().SaveChangesAsync(ct);
        _logger.LogInformation("StrategyCapacityWorker: updated capacities for {Count} strategies",
            strategies.Count);
    }

    private static async Task<T> GetConfigAsync<T>(
        IReadApplicationDbContext readContext, string key, T defaultValue, CancellationToken ct)
    {
        var entry = await readContext.GetDbContext()
            .Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
