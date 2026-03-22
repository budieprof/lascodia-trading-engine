using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Infrastructure.HealthChecks;

/// <summary>
/// Verifies PostgreSQL connectivity by attempting a lightweight connection check
/// against the write database context.
/// </summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(
        IWriteApplicationDbContext writeContext,
        ILogger<DatabaseHealthCheck> logger)
    {
        _writeContext = writeContext;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _writeContext.GetDbContext().Database.CanConnectAsync(cancellationToken);

            if (canConnect)
            {
                return HealthCheckResult.Healthy("PostgreSQL database is reachable.");
            }

            _logger.LogWarning("Database health check failed: CanConnectAsync returned false.");
            return HealthCheckResult.Unhealthy("PostgreSQL database is not reachable.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check threw an exception.");
            return HealthCheckResult.Unhealthy("PostgreSQL database connectivity check failed.", ex);
        }
    }
}

/// <summary>
/// Verifies that the RabbitMQ event bus service is registered and resolvable in DI.
/// This is a lightweight check — it does not attempt to open a connection or publish a message.
/// </summary>
public class RabbitMQHealthCheck : IHealthCheck
{
    private readonly IEventBus? _eventBus;
    private readonly ILogger<RabbitMQHealthCheck> _logger;

    public RabbitMQHealthCheck(
        IEventBus? eventBus,
        ILogger<RabbitMQHealthCheck> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_eventBus is null)
            {
                _logger.LogWarning("RabbitMQ health check failed: IEventBus is not registered.");
                return Task.FromResult(
                    HealthCheckResult.Unhealthy("Event bus (RabbitMQ) is not registered in the service container."));
            }

            _logger.LogDebug("RabbitMQ health check passed: IEventBus instance resolved ({Type}).",
                _eventBus.GetType().Name);

            return Task.FromResult(
                HealthCheckResult.Healthy(
                    $"Event bus is available (implementation: {_eventBus.GetType().Name})."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RabbitMQ health check threw an exception.");
            return Task.FromResult(
                HealthCheckResult.Unhealthy("Event bus health check failed.", ex));
        }
    }
}

/// <summary>
/// Verifies that at least one broker with <see cref="BrokerStatus.Connected"/> status
/// exists in the database, indicating the engine can route orders.
/// </summary>
public class BrokerHealthCheck : IHealthCheck
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly ILogger<BrokerHealthCheck> _logger;

    public BrokerHealthCheck(
        IReadApplicationDbContext readContext,
        ILogger<BrokerHealthCheck> logger)
    {
        _readContext = readContext;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var activeBroker = await _readContext.GetDbContext()
                .Set<Broker>()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    b => b.Status == BrokerStatus.Connected && !b.IsDeleted,
                    cancellationToken);

            if (activeBroker is not null)
            {
                return HealthCheckResult.Healthy(
                    $"Active broker found: {activeBroker.Name} (Id: {activeBroker.Id}).");
            }

            _logger.LogWarning("Broker health check: no connected broker found in the database.");
            return HealthCheckResult.Unhealthy("No broker with Connected status found in the database.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Broker health check threw an exception.");
            return HealthCheckResult.Unhealthy("Broker connectivity check failed.", ex);
        }
    }
}

/// <summary>
/// Checks whether the live price cache contains any prices and whether the most recent
/// price update is younger than 5 minutes. A stale cache is reported as Degraded rather
/// than Unhealthy because it may recover on its own once market data feeds reconnect.
/// </summary>
public class PriceCacheFreshnessCheck : IHealthCheck
{
    private static readonly TimeSpan StalenessThreshold = TimeSpan.FromMinutes(5);

    private readonly ILivePriceCache _livePriceCache;
    private readonly ILogger<PriceCacheFreshnessCheck> _logger;

    public PriceCacheFreshnessCheck(
        ILivePriceCache livePriceCache,
        ILogger<PriceCacheFreshnessCheck> logger)
    {
        _livePriceCache = livePriceCache;
        _logger = logger;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var allPrices = _livePriceCache.GetAll();

            if (allPrices is null || allPrices.Count == 0)
            {
                _logger.LogWarning("Price cache freshness check: cache is empty.");
                return Task.FromResult(
                    HealthCheckResult.Unhealthy("Live price cache is empty — no prices available."));
            }

            var freshestTimestamp = allPrices.Values.Max(p => p.Timestamp);
            var age = DateTime.UtcNow - freshestTimestamp;

            if (age <= StalenessThreshold)
            {
                return Task.FromResult(
                    HealthCheckResult.Healthy(
                        $"Live price cache contains {allPrices.Count} symbol(s). " +
                        $"Freshest update: {age.TotalSeconds:F0}s ago."));
            }

            _logger.LogWarning(
                "Price cache freshness check: freshest price is {Age} old, exceeding {Threshold} threshold.",
                age, StalenessThreshold);

            return Task.FromResult(
                HealthCheckResult.Degraded(
                    $"Live price cache is stale. {allPrices.Count} symbol(s) cached, " +
                    $"but freshest update is {age.TotalMinutes:F1} minutes old (threshold: {StalenessThreshold.TotalMinutes} minutes)."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Price cache freshness check threw an exception.");
            return Task.FromResult(
                HealthCheckResult.Unhealthy("Live price cache freshness check failed.", ex));
        }
    }
}
