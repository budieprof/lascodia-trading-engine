using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that periodically hard-deletes <see cref="DeadLetterEvent"/> records
/// older than a configurable retention period. This prevents the dead-letter table from
/// growing unboundedly while preserving recent entries for investigation.
///
/// <para>
/// <b>Configuration (via EngineConfig table):</b>
/// <list type="bullet">
///   <item><description>
///     <c>DeadLetter:CleanupIntervalHours</c> — how often the cleanup runs (default 24 hours).
///   </description></item>
///   <item><description>
///     <c>DeadLetter:RetentionDays</c> — records older than this many days are deleted (default 30).
///   </description></item>
/// </list>
/// </para>
///
/// <para>
/// Uses <c>ExecuteDeleteAsync</c> (EF Core 7+ bulk operation) to delete records in a single
/// round-trip without loading entities into memory.
/// </para>
/// </summary>
public sealed class DeadLetterCleanupWorker : BackgroundService
{
    private const string CK_IntervalHours = "DeadLetter:CleanupIntervalHours";
    private const string CK_RetentionDays = "DeadLetter:RetentionDays";

    private const int DefaultIntervalHours = 24;
    private const int DefaultRetentionDays = 30;

    /// <summary>Max backoff delay on consecutive failures (5 minutes).</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeadLetterCleanupWorker> _logger;
    private int _consecutiveFailures;

    public DeadLetterCleanupWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<DeadLetterCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeadLetterCleanupWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalHours = DefaultIntervalHours;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readCtx = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                intervalHours = await GetConfigAsync<int>(readCtx, CK_IntervalHours, DefaultIntervalHours, stoppingToken);
                int retentionDays = await GetConfigAsync<int>(readCtx, CK_RetentionDays, DefaultRetentionDays, stoppingToken);

                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

                int deleted = await writeCtx.Set<DeadLetterEvent>()
                    .Where(d => d.DeadLetteredAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deleted > 0)
                    _logger.LogInformation(
                        "DeadLetterCleanupWorker: purged {Count} dead-letter record(s) older than {Days} day(s).",
                        deleted, retentionDays);
                else
                    _logger.LogDebug(
                        "DeadLetterCleanupWorker: no records older than {Days} day(s) to purge.",
                        retentionDays);

                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex,
                    "DeadLetterCleanupWorker: cleanup error (consecutive failures: {Failures})",
                    _consecutiveFailures);
            }

            // Exponential backoff on consecutive failures, capped at 5 minutes
            var normalDelay = TimeSpan.FromHours(intervalHours);
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    normalDelay.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : normalDelay;

            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("DeadLetterCleanupWorker stopping.");
    }

    // -- Config helper -------------------------------------------------------

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
