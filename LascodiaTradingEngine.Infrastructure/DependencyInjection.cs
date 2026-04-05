using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Lascodia.Trading.Engine.SharedApplication;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Lascodia.Trading.Engine.IntegrationEventLogEF.Services;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using LascodiaTradingEngine.Infrastructure.Services;
using LascodiaTradingEngine.Infrastructure.HealthChecks;
using LascodiaTradingEngine.Infrastructure.Persistence.Interceptors;

namespace LascodiaTradingEngine.Infrastructure;

/// <summary>
/// Infrastructure-layer dependency injection registrations for EF Core DbContexts,
/// integration event log, distributed locking, and health checks.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the write and read <see cref="DbContext"/> instances, the integration event log context,
    /// the PostgreSQL advisory lock service, and all custom health checks against the provided configuration.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configuration">Application configuration containing connection strings.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection ConfigureDbContexts(this IServiceCollection services, IConfiguration configuration)
        {
            // Slow query interceptor: logs queries > 500ms as Warning, > 5000ms as Error
            services.AddSingleton<SlowQueryInterceptor>();

            services.AddDbContext<WriteApplicationDbContext>((sp, options) =>
            {
                options.SetPostgresDB<WriteApplicationDbContext>(configuration);
                options.AddInterceptors(sp.GetRequiredService<SlowQueryInterceptor>());
            });

            services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetService<WriteApplicationDbContext>()!);

            services.AddTransient<IIntegrationEventLogService, IntegrationEventLogService<EventLogDbContext>>();

            services.AddScoped<IntegrationEventLogContext<EventLogDbContext>>(s =>
            {
                var writeDb = s.GetRequiredService<IWriteApplicationDbContext>();
                return new EventLogDbContext(new DbContextOptionsBuilder<EventLogDbContext>()
                .UseNpgsql(writeDb.GetDbContext().Database.GetDbConnection(), options => options.EnableRetryOnFailure())
                .Options);
            });
            services.AddDbContext<EventLogDbContext>(options =>
                options.SetPostgresDB<EventLogDbContext>(configuration));

            services.AddDbContext<ReadApplicationDbContext>((sp, options) =>
            {
                options.SetPostgresDB<ReadApplicationDbContext>(configuration, "ReadDbConnection");
                options.AddInterceptors(sp.GetRequiredService<SlowQueryInterceptor>());
            });

            services.AddScoped<IReadApplicationDbContext>(provider => provider.GetService<ReadApplicationDbContext>()!);

            // Distributed lock via PostgreSQL advisory locks — no extra infrastructure needed.
            services.AddSingleton<IDistributedLock, PostgresAdvisoryLock>();

            // Event log reader for the IntegrationEventRetryWorker outbox poller
            services.AddScoped<IEventLogReader, EventLogReader>();

            // Processed event tracker for cross-instance integration event deduplication
            services.AddScoped<IProcessedEventTracker, ProcessedEventTracker>();

            // Deep health checks — registered as named checks for /health endpoint.
            services.AddHealthChecks()
                .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
                .AddCheck<RabbitMQHealthCheck>("event_bus", tags: ["ready"])
                .AddCheck<BrokerHealthCheck>("broker", tags: ["ready"])
                .AddCheck<EAHeartbeatHealthCheck>("ea_heartbeat", tags: ["ready", "live"])
                .AddCheck<PriceCacheFreshnessCheck>("price_cache", tags: ["live"]);

            return services;
        }


}
