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

namespace LascodiaTradingEngine.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection ConfigureDbContexts(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddDbContext<WriteApplicationDbContext>(options =>
                options.SetPostgresDB<WriteApplicationDbContext>(configuration));

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

            services.AddDbContext<ReadApplicationDbContext>(options =>
                options.SetPostgresDB<ReadApplicationDbContext>(configuration, "ReadDbConnection"));

            services.AddScoped<IReadApplicationDbContext>(provider => provider.GetService<ReadApplicationDbContext>()!);

            // Distributed lock via PostgreSQL advisory locks — no extra infrastructure needed.
            services.AddSingleton<IDistributedLock, PostgresAdvisoryLock>();

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
