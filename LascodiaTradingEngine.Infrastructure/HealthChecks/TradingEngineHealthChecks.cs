using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Infrastructure.HealthChecks;

/// <summary>
/// Monitors PostgreSQL connection pool utilisation by checking the number of open connections
/// against the configured <c>Maximum Pool Size</c>. Reports Degraded when usage exceeds 80%
/// and Unhealthy when the pool is exhausted, enabling early detection of connection leaks
/// or insufficient pool sizing before queries start timing out.
/// </summary>
public class ConnectionPoolHealthCheck : IHealthCheck
{
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly ILogger<ConnectionPoolHealthCheck> _logger;

    /// <summary>
    /// Pool usage fraction above which the check reports <see cref="HealthStatus.Degraded"/>.
    /// Set to 80% to provide an early warning before full exhaustion.
    /// </summary>
    private const double DegradedThreshold = 0.80;

    public ConnectionPoolHealthCheck(
        IWriteApplicationDbContext writeContext,
        ILogger<ConnectionPoolHealthCheck> logger)
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
            var db = _writeContext.GetDbContext().Database;
            var connectionString = db.GetConnectionString();

            // Extract configured Maximum Pool Size from connection string (default: 100)
            int maxPoolSize = 200;
            if (!string.IsNullOrEmpty(connectionString))
            {
                var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length == 2 &&
                        kv[0].Trim().Equals("Maximum Pool Size", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(kv[1].Trim(), out int parsed))
                            maxPoolSize = parsed;
                    }
                }
            }

            // Query pg_stat_activity for the number of active connections from this application.
            // This is a lightweight read that does not lock any resources.
            var conn = db.GetDbConnection();
            await db.OpenConnectionAsync(cancellationToken);
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()";
                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                int activeConnections = Convert.ToInt32(result);

                double usage = (double)activeConnections / maxPoolSize;
                var data = new Dictionary<string, object>
                {
                    ["active_connections"] = activeConnections,
                    ["max_pool_size"] = maxPoolSize,
                    ["usage_pct"] = $"{usage:P0}"
                };

                if (usage >= 1.0)
                {
                    _logger.LogError(
                        "Connection pool health check: pool EXHAUSTED ({Active}/{Max} connections).",
                        activeConnections, maxPoolSize);
                    return HealthCheckResult.Unhealthy(
                        $"Connection pool exhausted: {activeConnections}/{maxPoolSize} connections in use.",
                        data: data);
                }

                if (usage >= DegradedThreshold)
                {
                    _logger.LogWarning(
                        "Connection pool health check: high usage ({Active}/{Max} = {Usage:P0}).",
                        activeConnections, maxPoolSize, usage);
                    return HealthCheckResult.Degraded(
                        $"Connection pool usage high: {activeConnections}/{maxPoolSize} ({usage:P0}).",
                        data: data);
                }

                return HealthCheckResult.Healthy(
                    $"Connection pool healthy: {activeConnections}/{maxPoolSize} ({usage:P0}).",
                    data: data);
            }
            finally
            {
                await db.CloseConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection pool health check threw an exception.");
            return HealthCheckResult.Unhealthy("Connection pool health check failed.", ex);
        }
    }
}

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
/// Verifies that the event bus is registered and, for RabbitMQ, that the persistent
/// connection is alive. This catches silent connection drops that the lightweight
/// DI-resolution-only check would miss.
/// </summary>
public class RabbitMQHealthCheck : IHealthCheck
{
    private readonly IEventBus? _eventBus;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RabbitMQHealthCheck> _logger;

    public RabbitMQHealthCheck(
        IEventBus? eventBus,
        IServiceProvider serviceProvider,
        ILogger<RabbitMQHealthCheck> logger)
    {
        _eventBus = eventBus;
        _serviceProvider = serviceProvider;
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

            // Attempt to resolve the RabbitMQ persistent connection and verify connectivity.
            // This uses dynamic resolution because the connection type lives in the shared
            // library and may not be registered when using Kafka instead of RabbitMQ.
            var connectionType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
                .FirstOrDefault(t => t.Name == "IRabbitMQPersistentConnection" && t.IsInterface);

            if (connectionType is not null)
            {
                var connection = _serviceProvider.GetService(connectionType);
                if (connection is not null)
                {
                    var isConnectedProp = connectionType.GetProperty("IsConnected");
                    if (isConnectedProp is not null)
                    {
                        var isConnected = (bool)(isConnectedProp.GetValue(connection) ?? false);
                        if (!isConnected)
                        {
                            _logger.LogWarning("RabbitMQ health check: connection is not active.");
                            return Task.FromResult(
                                HealthCheckResult.Degraded(
                                    "Event bus registered but RabbitMQ connection is not active — " +
                                    "events may not be delivered until the connection recovers."));
                        }
                    }
                }
            }

            _logger.LogDebug("RabbitMQ health check passed: IEventBus instance resolved ({Type}).",
                _eventBus.GetType().Name);

            return Task.FromResult(
                HealthCheckResult.Healthy(
                    $"Event bus is available and connected (implementation: {_eventBus.GetType().Name})."));
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
/// Verifies that at least one active trading account exists in the database,
/// indicating the engine can route orders.
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
            var activeAccount = await _readContext.GetDbContext()
                .Set<TradingAccount>()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    a => a.IsActive && !a.IsDeleted,
                    cancellationToken);

            if (activeAccount is not null)
            {
                return HealthCheckResult.Healthy(
                    $"Active trading account found: {activeAccount.AccountName} (Id: {activeAccount.Id}).");
            }

            _logger.LogWarning("Broker health check: no active trading account found in the database.");
            return HealthCheckResult.Degraded("No active trading account found in the database.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Broker health check threw an exception.");
            return HealthCheckResult.Unhealthy("Trading account connectivity check failed.", ex);
        }
    }
}

/// <summary>
/// Verifies that at least one EA instance has sent a heartbeat within the last 60 seconds.
/// If all instances are stale or no instances exist, the engine has no market data source.
/// </summary>
public class EAHeartbeatHealthCheck : IHealthCheck
{
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(60);

    private readonly IReadApplicationDbContext _readContext;
    private readonly ILogger<EAHeartbeatHealthCheck> _logger;

    public EAHeartbeatHealthCheck(
        IReadApplicationDbContext readContext,
        ILogger<EAHeartbeatHealthCheck> logger)
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
            var cutoff = DateTime.UtcNow - HeartbeatTimeout;

            var activeInstances = await _readContext.GetDbContext()
                .Set<EAInstance>()
                .AsNoTracking()
                .Where(x => x.Status == Domain.Enums.EAInstanceStatus.Active && !x.IsDeleted)
                .Select(x => new { x.InstanceId, x.LastHeartbeat, x.Symbols })
                .ToListAsync(cancellationToken);

            if (activeInstances.Count == 0)
            {
                _logger.LogWarning("EA heartbeat health check: no active EA instances registered.");
                return HealthCheckResult.Degraded("No active EA instances registered — engine has no market data source.");
            }

            var staleInstances = activeInstances.Where(x => x.LastHeartbeat < cutoff).ToList();
            var healthyInstances = activeInstances.Count - staleInstances.Count;

            if (healthyInstances == 0)
            {
                _logger.LogError(
                    "EA heartbeat health check: all {Total} EA instances are stale (last heartbeat > {Timeout}s ago).",
                    activeInstances.Count, HeartbeatTimeout.TotalSeconds);
                return HealthCheckResult.Unhealthy(
                    $"All {activeInstances.Count} EA instance(s) are stale — DATA_UNAVAILABLE. " +
                    $"Stale instances: {string.Join(", ", staleInstances.Select(x => x.InstanceId))}");
            }

            if (staleInstances.Count > 0)
            {
                _logger.LogWarning(
                    "EA heartbeat health check: {Stale}/{Total} EA instances are stale.",
                    staleInstances.Count, activeInstances.Count);
                return HealthCheckResult.Degraded(
                    $"{healthyInstances}/{activeInstances.Count} EA instance(s) healthy. " +
                    $"Stale: {string.Join(", ", staleInstances.Select(x => x.InstanceId))}");
            }

            return HealthCheckResult.Healthy(
                $"All {activeInstances.Count} EA instance(s) healthy. " +
                $"Freshest heartbeat: {(DateTime.UtcNow - activeInstances.Max(x => x.LastHeartbeat)).TotalSeconds:F0}s ago.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EA heartbeat health check threw an exception.");
            return HealthCheckResult.Unhealthy("EA heartbeat health check failed.", ex);
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
                    HealthCheckResult.Degraded("Live price cache is empty — no prices available."));
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
