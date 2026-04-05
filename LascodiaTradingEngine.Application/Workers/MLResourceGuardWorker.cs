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
/// Safety-net worker that monitors active <see cref="MLTrainingRun"/> records for resource
/// abuse and terminates runaway training jobs before they degrade the engine's overall
/// performance or exhaust system resources.
///
/// <para>
/// Two independent checks are performed each cycle:
/// <list type="bullet">
///   <item><b>Elapsed time</b> — when a run has been in <see cref="RunStatus.Running"/>
///         state for longer than <c>MLResourceGuard:MaxRunMinutes</c> (default 180),
///         it is marked as <see cref="RunStatus.Failed"/> with a descriptive error message.
///         This catches training jobs that are deadlocked, stuck in an infinite loop, or
///         processing an unexpectedly large dataset.</item>
///   <item><b>Process memory</b> — when the managed heap size (via
///         <see cref="GC.GetTotalMemory(bool)"/>) exceeds
///         <c>MLResourceGuard:MaxMemoryMB</c> (default 4096 MB), an alert is created to
///         warn operators of memory pressure. This is a process-wide check rather than
///         per-run, since .NET managed heap is shared across all training runs.</item>
/// </list>
/// </para>
///
/// <para>Configuration keys (read from <see cref="EngineConfig"/>):</para>
/// <list type="bullet">
///   <item><c>MLResourceGuard:PollIntervalSeconds</c> — default 60 (1 min)</item>
///   <item><c>MLResourceGuard:MaxRunMinutes</c>       — per-run time limit, default 180</item>
///   <item><c>MLResourceGuard:MaxMemoryMB</c>         — process memory alert threshold, default 4096</item>
///   <item><c>MLResourceGuard:AlertDestination</c>    — alert destination, default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLResourceGuardWorker : BackgroundService
{
    private const string CK_PollSecs   = "MLResourceGuard:PollIntervalSeconds";
    private const string CK_MaxRunMins = "MLResourceGuard:MaxRunMinutes";
    private const string CK_MaxMemMB   = "MLResourceGuard:MaxMemoryMB";
    private const string CK_AlertDest  = "MLResourceGuard:AlertDestination";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLResourceGuardWorker>    _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new async DI scope per poll cycle so scoped EF Core
    /// contexts are correctly disposed after each resource guard pass.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLResourceGuardWorker(
        IServiceScopeFactory            scopeFactory,
        ILogger<MLResourceGuardWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Polls every <c>MLResourceGuard:PollIntervalSeconds</c>
    /// seconds (default 60) to detect runaway training runs and memory pressure.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLResourceGuardWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 60;

            try
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(stoppingToken);
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                    var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var readCtx  = readDb.GetDbContext();
                    var writeCtx = writeDb.GetDbContext();

                    pollSecs = await GetConfigAsync<int>(readCtx, CK_PollSecs, 60, stoppingToken);

                    await CheckRunTimeoutsAsync(readCtx, writeCtx, stoppingToken);
                    await CheckMemoryPressureAsync(readCtx, writeCtx, stoppingToken);
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
                _logger.LogError(ex, "MLResourceGuardWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLResourceGuardWorker stopping.");
    }

    // ── Run timeout check ────────────────────────────────────────────────────

    /// <summary>
    /// Finds all training runs in <see cref="RunStatus.Running"/> state whose elapsed time
    /// (UtcNow - PickedUpAt) exceeds the configured maximum. Each timed-out run is marked
    /// as Failed and an alert is created.
    /// </summary>
    private async Task CheckRunTimeoutsAsync(
        DbContext         readCtx,
        DbContext         writeCtx,
        CancellationToken ct)
    {
        int    maxRunMins = await GetConfigAsync<int>   (readCtx, CK_MaxRunMins, 180,     ct);
        string alertDest  = await GetConfigAsync<string>(readCtx, CK_AlertDest,  "ml-ops", ct);

        var now    = DateTime.UtcNow;
        var cutoff = now.AddMinutes(-maxRunMins);

        var timedOutRuns = await readCtx.Set<MLTrainingRun>()
            .Where(r => r.Status     == RunStatus.Running &&
                        r.PickedUpAt != null              &&
                        r.PickedUpAt < cutoff             &&
                        !r.IsDeleted)
            .AsNoTracking()
            .Select(r => new { r.Id, r.Symbol, r.Timeframe, r.PickedUpAt })
            .ToListAsync(ct);

        foreach (var run in timedOutRuns)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                double elapsedMins = (now - run.PickedUpAt!.Value).TotalMinutes;
                string reason = $"Resource limit exceeded: training run elapsed {elapsedMins:F0} min " +
                                $"(max {maxRunMins} min)";

                _logger.LogWarning(
                    "ResourceGuard: terminating run {Id} ({Symbol}/{Tf}) — {Reason}.",
                    run.Id, run.Symbol, run.Timeframe, reason);

                // Mark the run as Failed with descriptive error message.
                await writeCtx.Set<MLTrainingRun>()
                    .Where(r => r.Id == run.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status,       RunStatus.Failed)
                        .SetProperty(r => r.ErrorMessage, reason)
                        .SetProperty(r => r.CompletedAt,  now), ct);

                // Create alert for the timed-out run.
                bool alertExists = await readCtx.Set<Alert>()
                    .AnyAsync(a => a.Symbol    == run.Symbol                  &&
                                   a.AlertType == AlertType.MLModelDegraded   &&
                                   a.IsActive  && !a.IsDeleted, ct);

                if (!alertExists)
                {
                    writeCtx.Set<Alert>().Add(new Alert
                    {
                        AlertType     = AlertType.MLModelDegraded,
                        Symbol        = run.Symbol,
                        Channel       = AlertChannel.Webhook,
                        Destination   = alertDest,
                        ConditionJson = JsonSerializer.Serialize(new
                        {
                            reason         = "resource_guard_timeout",
                            severity       = "critical",
                            symbol         = run.Symbol,
                            timeframe      = run.Timeframe.ToString(),
                            runId          = run.Id,
                            elapsedMinutes = elapsedMins,
                            maxRunMinutes  = maxRunMins,
                        }),
                        IsActive = true,
                    });

                    await writeCtx.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ResourceGuard: error terminating run {Id} ({Symbol}/{Tf}) — skipping.",
                    run.Id, run.Symbol, run.Timeframe);
            }
        }
    }

    // ── Memory pressure check ────────────────────────────────────────────────

    /// <summary>
    /// Checks the current process managed heap size against the configured threshold.
    /// If exceeded, creates an alert to warn operators. This is a process-wide check
    /// (not per-run) since the .NET managed heap is shared across all training runs.
    /// </summary>
    private async Task CheckMemoryPressureAsync(
        DbContext         readCtx,
        DbContext         writeCtx,
        CancellationToken ct)
    {
        int    maxMemMB  = await GetConfigAsync<int>   (readCtx, CK_MaxMemMB,   4096,    ct);
        string alertDest = await GetConfigAsync<string>(readCtx, CK_AlertDest,  "ml-ops", ct);

        long   heapBytes = GC.GetTotalMemory(false);
        double heapMB    = heapBytes / (1024.0 * 1024.0);

        _logger.LogDebug(
            "ResourceGuard: managed heap = {HeapMB:F0} MB (threshold {Max} MB).",
            heapMB, maxMemMB);

        if (heapMB <= maxMemMB)
            return;

        _logger.LogWarning(
            "ResourceGuard: managed heap {HeapMB:F0} MB exceeds threshold {Max} MB.",
            heapMB, maxMemMB);

        // Deduplicate: only one active memory-pressure alert at a time.
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == string.Empty                    &&
                           a.AlertType == AlertType.MLModelDegraded       &&
                           a.IsActive  && !a.IsDeleted                    &&
                           a.ConditionJson.Contains("resource_guard_memory"), ct);

        if (alertExists)
            return;

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = string.Empty,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = JsonSerializer.Serialize(new
            {
                reason      = "resource_guard_memory",
                severity    = "warning",
                heapMB      = heapMB,
                maxMemoryMB = maxMemMB,
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ────────────────────────────────────────────────────────

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
}
