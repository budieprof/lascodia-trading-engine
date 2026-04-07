using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Scans for permanently failed <see cref="MLTrainingRun"/> records that have exhausted
/// their retry budget and been sitting in <see cref="RunStatus.Failed"/> state beyond a
/// configurable retention period. For each such run, if no successful training run exists
/// for the same symbol/timeframe since the failure, the worker resets the run to
/// <see cref="RunStatus.Queued"/> with a fresh attempt counter — giving the ML pipeline
/// another chance after transient infrastructure issues (e.g. OOM, disk full, DB timeout)
/// have presumably been resolved.
///
/// <para>
/// Dead-letter retries are capped at a configurable maximum (default 3) per symbol/timeframe
/// combination, tracked via <see cref="EngineConfig"/> keys of the form
/// <c>MLDeadLetter:{Symbol}:{Timeframe}:RetryCount</c>. Once the cap is exceeded, the worker
/// creates an <see cref="AlertType.MLModelDegraded"/> alert to notify operators that manual
/// intervention is required.
/// </para>
///
/// <para>Configuration keys (read from <see cref="EngineConfig"/>):</para>
/// <list type="bullet">
///   <item><c>MLDeadLetter:PollIntervalSeconds</c> — default 604800 (7 days)</item>
///   <item><c>MLDeadLetter:RetryAfterDays</c>      — minimum age of failure before retry, default 7</item>
///   <item><c>MLDeadLetter:MaxRetries</c>           — max dead-letter retries per symbol/tf, default 3</item>
///   <item><c>MLDeadLetter:AlertDestination</c>     — alert destination, default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLDeadLetterWorker : BackgroundService
{
    private const string CK_PollSecs   = "MLDeadLetter:PollIntervalSeconds";
    private const string CK_RetryDays  = "MLDeadLetter:RetryAfterDays";
    private const string CK_MaxRetries = "MLDeadLetter:MaxRetries";
    private const string CK_AlertDest  = "MLDeadLetter:AlertDestination";

    private readonly IServiceScopeFactory          _scopeFactory;
    private readonly ILogger<MLDeadLetterWorker>   _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new async DI scope per poll cycle so scoped EF Core
    /// contexts are correctly disposed after each dead-letter scan pass.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLDeadLetterWorker(
        IServiceScopeFactory        scopeFactory,
        ILogger<MLDeadLetterWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Polls every <c>MLDeadLetter:PollIntervalSeconds</c>
    /// seconds (default 604800 = 7 days), reading the interval from <see cref="EngineConfig"/>
    /// on each cycle so it can be hot-reloaded without a restart.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLDeadLetterWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 604800; // default 7 days

            try
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(stoppingToken);
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                    var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var readCtx = readDb.GetDbContext();
                    var writeCtx = writeDb.GetDbContext();

                    pollSecs = await GetConfigAsync<int>(readCtx, CK_PollSecs, 604800, stoppingToken);

                    await ScanDeadLetterRunsAsync(readCtx, writeCtx, stoppingToken);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLDeadLetterWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLDeadLetterWorker stopping.");
    }

    // ── Dead-letter scan core ────────────────────────────────────────────────

    /// <summary>
    /// Finds all failed training runs whose <see cref="MLTrainingRun.CompletedAt"/> is older
    /// than the configured retention period. For each run, checks whether the same
    /// symbol/timeframe has had a successful run since the failure. If not, and the
    /// dead-letter retry cap has not been reached, resets the run to Queued.
    /// </summary>
    private async Task ScanDeadLetterRunsAsync(
        DbContext         readCtx,
        DbContext         writeCtx,
        CancellationToken ct)
    {
        int    retryAfterDays = await GetConfigAsync<int>   (readCtx, CK_RetryDays,  7,       ct);
        int    maxRetries     = await GetConfigAsync<int>   (readCtx, CK_MaxRetries, 3,       ct);
        string alertDest      = await GetConfigAsync<string>(readCtx, CK_AlertDest,  "ml-ops", ct);

        var cutoff = DateTime.UtcNow.AddDays(-retryAfterDays);

        // Find all permanently failed runs older than the retention period.
        var deadLetterRuns = await readCtx.Set<MLTrainingRun>()
            .Where(r => r.Status      == RunStatus.Failed &&
                        r.CompletedAt != null             &&
                        r.CompletedAt < cutoff            &&
                        !r.IsDeleted)
            .AsNoTracking()
            .Select(r => new
            {
                r.Id,
                r.Symbol,
                r.Timeframe,
                r.CompletedAt,
                r.ErrorMessage,
            })
            .ToListAsync(ct);

        if (deadLetterRuns.Count == 0)
        {
            _logger.LogDebug("MLDeadLetterWorker: no dead-letter runs found.");
            return;
        }

        _logger.LogInformation(
            "MLDeadLetterWorker: found {Count} dead-letter failed run(s) older than {Days} days.",
            deadLetterRuns.Count, retryAfterDays);

        foreach (var run in deadLetterRuns)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ProcessDeadLetterRunAsync(
                    run.Id, run.Symbol, run.Timeframe, run.CompletedAt!.Value, run.ErrorMessage,
                    maxRetries, alertDest, readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MLDeadLetterWorker: error processing dead-letter run {Id} ({Symbol}/{Tf}) — skipping.",
                    run.Id, run.Symbol, run.Timeframe);
            }
        }
    }

    /// <summary>
    /// Processes a single dead-letter run: checks for recent success on the same
    /// symbol/timeframe, enforces the retry cap, and resets or alerts accordingly.
    /// </summary>
    private async Task ProcessDeadLetterRunAsync(
        long              runId,
        string            symbol,
        Timeframe         timeframe,
        DateTime          completedAt,
        string?           errorMessage,
        int               maxRetries,
        string            alertDest,
        DbContext         readCtx,
        DbContext         writeCtx,
        CancellationToken ct)
    {
        // Check if any successful run for the same symbol/timeframe exists since the failure.
        bool hasSuccessSince = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == symbol              &&
                           r.Timeframe == timeframe           &&
                           r.Status    == RunStatus.Completed &&
                           r.CompletedAt != null              &&
                           r.CompletedAt > completedAt        &&
                           !r.IsDeleted, ct);

        if (hasSuccessSince)
        {
            _logger.LogDebug(
                "MLDeadLetterWorker: run {Id} ({Symbol}/{Tf}) — successful run exists since failure. Skipping.",
                runId, symbol, timeframe);
            return;
        }

        // Read current dead-letter retry count from EngineConfig using the WRITE context
        // to prevent lost-update races when multiple workers process dead letters concurrently
        // for the same symbol/timeframe (read replica lag could cause both to read the same value).
        string retryCountKey = $"MLDeadLetter:{symbol}:{timeframe}:RetryCount";
        int    currentRetries = await GetConfigAsync<int>(writeCtx, retryCountKey, 0, ct);

        if (currentRetries >= maxRetries)
        {
            _logger.LogWarning(
                "MLDeadLetterWorker: run {Id} ({Symbol}/{Tf}) — dead-letter retry cap reached " +
                "({Current}/{Max}). Creating alert.",
                runId, symbol, timeframe, currentRetries, maxRetries);

            // Check if alert already exists to avoid duplication.
            bool alertExists = await readCtx.Set<Alert>()
                .AnyAsync(a => a.Symbol    == symbol                  &&
                               a.AlertType == AlertType.MLModelDegraded &&
                               a.IsActive  && !a.IsDeleted, ct);

            if (!alertExists)
            {
                writeCtx.Set<Alert>().Add(new Alert
                {
                    AlertType     = AlertType.MLModelDegraded,
                    Symbol        = symbol,
                    Channel       = AlertChannel.Webhook,
                    Destination   = alertDest,
                    ConditionJson = JsonSerializer.Serialize(new
                    {
                        reason            = "dead_letter_retry_cap_exceeded",
                        severity          = "critical",
                        symbol,
                        timeframe         = timeframe.ToString(),
                        runId,
                        deadLetterRetries = currentRetries,
                        maxRetries,
                        lastError         = errorMessage ?? "unknown",
                    }),
                    IsActive = true,
                });

                await writeCtx.SaveChangesAsync(ct);
            }

            return;
        }

        // Reset the failed run to Queued with fresh attempt state.
        var now = DateTime.UtcNow;
        string retryNote = $"[DeadLetter retry at {now:O}]";
        string updatedError = string.IsNullOrWhiteSpace(errorMessage)
            ? retryNote
            : $"{errorMessage} {retryNote}";

        await writeCtx.Set<MLTrainingRun>()
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status,       RunStatus.Queued)
                .SetProperty(r => r.AttemptCount,  0)
                .SetProperty(r => r.NextRetryAt,   (DateTime?)null)
                .SetProperty(r => r.ErrorMessage,  updatedError)
                .SetProperty(r => r.CompletedAt,   (DateTime?)null)
                .SetProperty(r => r.PickedUpAt,    (DateTime?)null)
                .SetProperty(r => r.WorkerInstanceId, (Guid?)null), ct);

        // Increment the dead-letter retry counter in EngineConfig.
        await UpsertConfigAsync(writeCtx, retryCountKey, (currentRetries + 1).ToString(), ct);
        await writeCtx.SaveChangesAsync(ct);

        _logger.LogWarning(
            "MLDeadLetterWorker: RESET run {Id} ({Symbol}/{Tf}) to Queued — dead-letter retry " +
            "{Retry}/{Max}. Original error: {Error}",
            runId, symbol, timeframe, currentRetries + 1, maxRetries,
            errorMessage ?? "unknown");
    }

    // ── Config helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table,
    /// falling back to <paramref name="defaultValue"/> if the key is absent or
    /// the stored value cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        DbContext         ctx,
        string            key,
        T                 defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    /// <summary>
    /// Creates or updates an <see cref="EngineConfig"/> entry. If the key already exists,
    /// updates its value and <see cref="EngineConfig.LastUpdatedAt"/> timestamp. Otherwise
    /// creates a new record with <see cref="ConfigDataType.String"/> and hot-reload enabled.
    /// </summary>
    private static async Task UpsertConfigAsync(
        DbContext         writeCtx,
        string            key,
        string            value,
        CancellationToken ct)
    {
        var existing = await writeCtx.Set<EngineConfig>()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (existing is not null)
        {
            existing.Value         = value;
            existing.LastUpdatedAt = DateTime.UtcNow;
        }
        else
        {
            writeCtx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key             = key,
                Value           = value,
                Description     = $"Dead-letter retry counter — managed by MLDeadLetterWorker.",
                DataType        = ConfigDataType.String,
                IsHotReloadable = true,
                LastUpdatedAt   = DateTime.UtcNow,
            });
        }
    }
}
