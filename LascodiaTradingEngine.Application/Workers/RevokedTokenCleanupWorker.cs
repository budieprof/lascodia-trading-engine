using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Daily sweep that deletes <see cref="RevokedToken"/> rows whose underlying JWT has already
/// expired. Without this the table grows unbounded — every logout adds a row that is only
/// useful until the JWT's natural <c>exp</c> passes.
/// </summary>
/// <remarks>
/// Runs once at startup (after a short jitter) and then on a fixed 24-hour cadence. Failures
/// are logged and the loop continues — losing a sweep only delays cleanup, never blocks logins.
/// </remarks>
public sealed class RevokedTokenCleanupWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan StartupJitter = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory                 _scopeFactory;
    private readonly ILogger<RevokedTokenCleanupWorker>   _logger;

    public RevokedTokenCleanupWorker(
        IServiceScopeFactory                scopeFactory,
        ILogger<RevokedTokenCleanupWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RevokedTokenCleanupWorker started.");

        try
        {
            await Task.Delay(StartupJitter, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
                var cutoff = DateTime.UtcNow;

                var deleted = await db.Set<RevokedToken>()
                    .Where(x => x.ExpiresAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deleted > 0)
                    _logger.LogInformation("Pruned {Count} expired revoked-token rows.", deleted);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RevokedTokenCleanupWorker sweep failed; will retry next cycle.");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("RevokedTokenCleanupWorker stopped.");
    }
}
