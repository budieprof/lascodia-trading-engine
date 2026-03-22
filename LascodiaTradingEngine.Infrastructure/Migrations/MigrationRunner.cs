using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

namespace LascodiaTradingEngine.Infrastructure.Migrations;

/// <summary>
/// Standalone migration runner that can be invoked independently of the API startup.
///
/// Zero-downtime deployment strategy:
/// <list type="number">
///   <item>Run migrations separately (before deploying new code):
///     <code>dotnet run --project LascodiaTradingEngine.API -- --migrate-only</code></item>
///   <item>Deploy the new code — app starts without running migrations.</item>
/// </list>
///
/// This decouples schema changes from application startup, preventing the app from
/// blocking on long-running migrations and allowing rollback if a migration fails.
///
/// For backwards-compatible migrations (add column, add table), the old code continues
/// running while the migration applies. For breaking migrations, coordinate with a
/// maintenance window.
/// </summary>
public static class MigrationRunner
{
    /// <summary>
    /// Returns true if the command-line args indicate this is a migration-only invocation.
    /// </summary>
    public static bool IsMigrateOnlyMode(string[] args)
        => args.Contains("--migrate-only", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Applies all pending EF Core migrations and exits.
    /// </summary>
    public static async Task RunMigrationsAsync(IServiceProvider services, ILogger logger)
    {
        logger.LogInformation("MigrationRunner: applying pending migrations...");

        using var scope = services.CreateScope();

        // Main application DB
        var writeDb = scope.ServiceProvider.GetRequiredService<WriteApplicationDbContext>();
        var pendingMain = (await writeDb.Database.GetPendingMigrationsAsync()).ToList();
        if (pendingMain.Count > 0)
        {
            logger.LogInformation("MigrationRunner: {Count} pending migration(s) for main DB: {Names}",
                pendingMain.Count, string.Join(", ", pendingMain));
            await writeDb.Database.MigrateAsync();
            logger.LogInformation("MigrationRunner: main DB migrations applied successfully.");
        }
        else
        {
            logger.LogInformation("MigrationRunner: main DB is up to date.");
        }

        // Event log DB
        var eventLogDb = scope.ServiceProvider.GetRequiredService<EventLogDbContext>();
        var pendingEventLog = (await eventLogDb.Database.GetPendingMigrationsAsync()).ToList();
        if (pendingEventLog.Count > 0)
        {
            logger.LogInformation("MigrationRunner: {Count} pending migration(s) for EventLog DB: {Names}",
                pendingEventLog.Count, string.Join(", ", pendingEventLog));
            await eventLogDb.Database.MigrateAsync();
            logger.LogInformation("MigrationRunner: EventLog DB migrations applied successfully.");
        }
        else
        {
            logger.LogInformation("MigrationRunner: EventLog DB is up to date.");
        }

        logger.LogInformation("MigrationRunner: all migrations complete.");
    }
}
