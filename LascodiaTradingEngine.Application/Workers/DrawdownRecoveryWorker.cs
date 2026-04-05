using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MediatR;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Enforces account-level drawdown recovery modes by monitoring the latest
/// <see cref="DrawdownSnapshot"/> and acting on mode transitions.
///
/// <para>
/// <see cref="DrawdownMonitorWorker"/> records the snapshot and sets its
/// <see cref="DrawdownSnapshot.RecoveryMode"/>, but takes no further action.
/// This worker is the enforcement layer:
/// </para>
///
/// <list type="bullet">
///   <item><description>
///     <b>Normal → Reduced</b> — logs a warning so the operator is alerted.
///     The current mode is persisted to <c>EngineConfig</c> key
///     <c>DrawdownRecovery:ActiveMode</c> so downstream components can read it.
///   </description></item>
///   <item><description>
///     <b>Normal/Reduced → Halted</b> — pauses ALL currently active strategies via
///     <c>ExecuteUpdateAsync</c>, records their IDs to
///     <c>DrawdownRecovery:AutoPausedStrategyIds</c> (JSON), and writes an audit entry
///     for each strategy paused.
///   </description></item>
///   <item><description>
///     <b>Halted → Reduced/Normal</b> — resumes only the strategies that were
///     auto-paused by this worker (using the stored IDs), clears the list, and
///     writes audit entries.
///   </description></item>
/// </list>
///
/// Configurable thresholds (EngineConfig keys with defaults):
/// <list type="bullet">
///   <item><description><c>DrawdownRecovery:PollIntervalSeconds</c> — default 30 s</description></item>
/// </list>
/// </summary>
public sealed class DrawdownRecoveryWorker : BackgroundService
{
    /// <summary>EngineConfig key that stores the polling interval in seconds (default 30).</summary>
    private const string CK_PollSecs       = "DrawdownRecovery:PollIntervalSeconds";

    /// <summary>
    /// EngineConfig key that stores the currently enforced <see cref="RecoveryMode"/>
    /// as a string (e.g. "Normal", "Reduced", "Halted"). Written by this worker after
    /// every mode transition so that other workers and queries can read the current
    /// mode without inspecting the drawdown snapshot table directly.
    /// </summary>
    private const string CK_ActiveMode     = "DrawdownRecovery:ActiveMode";

    /// <summary>
    /// EngineConfig key that stores a JSON array of strategy IDs that were automatically
    /// paused when the account entered <see cref="RecoveryMode.Halted"/>. These are the
    /// only strategies that will be automatically resumed when the mode clears, ensuring
    /// strategies paused manually by the operator are not reactivated without consent.
    /// </summary>
    private const string CK_AutoPausedIds  = "DrawdownRecovery:AutoPausedStrategyIds";

    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<DrawdownRecoveryWorker> _logger;

    /// <summary>
    /// Initialises the worker.
    /// </summary>
    /// <param name="scopeFactory">Factory for creating scoped DI contexts per polling cycle.</param>
    /// <param name="logger">Structured logger for this worker.</param>
    public DrawdownRecoveryWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<DrawdownRecoveryWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. On each iteration:
    /// <list type="number">
    ///   <item><description>Opens a fresh async DI scope.</description></item>
    ///   <item><description>Reads <c>DrawdownRecovery:PollIntervalSeconds</c> from EngineConfig (hot-reloadable).</description></item>
    ///   <item><description>Calls <see cref="EnforceModeAsync"/> to detect mode transitions and take corrective action.</description></item>
    ///   <item><description>Waits for the configured interval before the next cycle.</description></item>
    /// </list>
    /// </summary>
    /// <param name="stoppingToken">Signalled by the host on application shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DrawdownRecoveryWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default interval used if the EngineConfig row has not been created yet.
            int pollSecs = 30;

            try
            {
                // Create a new async scope per cycle — this ensures scoped EF contexts
                // (IReadApplicationDbContext, IWriteApplicationDbContext) are freshly
                // instantiated and disposed after each cycle.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                // Hot-reload the poll interval from EngineConfig on every cycle
                // so operators can adjust frequency without restarting the engine.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 30, stoppingToken);

                await EnforceModeAsync(ctx, writeCtx, mediator, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — exit the loop without logging an error.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DrawdownRecoveryWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("DrawdownRecoveryWorker stopping.");
    }

    // ── Core enforcement ──────────────────────────────────────────────────────

    /// <summary>
    /// Core logic for a single enforcement cycle. Compares the <see cref="RecoveryMode"/>
    /// recorded on the latest <see cref="DrawdownSnapshot"/> against the previously
    /// persisted mode in <c>EngineConfig</c>. If they differ, the transition is handled
    /// and the config is updated to the new mode.
    ///
    /// <para>
    /// Transition matrix:
    /// <list type="table">
    ///   <listheader><term>From → To</term><description>Action</description></listheader>
    ///   <item><term>Any → Halted</term><description>
    ///     Pause all active strategies via bulk update; store their IDs for later resumption.
    ///   </description></item>
    ///   <item><term>Halted → Any</term><description>
    ///     Resume only the strategies that were auto-paused by this worker.
    ///   </description></item>
    ///   <item><term>Any → Reduced</term><description>
    ///     Log a warning; downstream components read <c>DrawdownRecovery:ActiveMode</c>
    ///     to apply reduced lot sizing.
    ///   </description></item>
    ///   <item><term>Any → Normal</term><description>Log an info message.</description></item>
    /// </list>
    /// </para>
    ///
    /// Every transition is recorded in the audit trail via <see cref="LogDecisionCommand"/>.
    /// </summary>
    private async Task EnforceModeAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        CancellationToken                       ct)
    {
        // Load latest drawdown snapshot — this is the source of truth for the current mode.
        var latest = await readCtx.Set<DrawdownSnapshot>()
            .OrderByDescending(s => s.RecordedAt)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        // No snapshots yet (e.g. engine just started) — nothing to enforce.
        if (latest is null) return;

        RecoveryMode currentMode = latest.RecoveryMode;

        // Read the previously enforced mode from EngineConfig.
        // This acts as the worker's persistent state across restarts.
        var modeEntry = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == CK_ActiveMode, ct);

        RecoveryMode previousMode = RecoveryMode.Normal;
        if (modeEntry?.Value is not null &&
            Enum.TryParse<RecoveryMode>(modeEntry.Value, out var parsed))
        {
            previousMode = parsed;
        }

        // Early exit — mode has not changed, no action required.
        if (currentMode == previousMode) return;

        _logger.LogInformation(
            "DrawdownRecoveryWorker: mode transition {From} → {To} (DrawdownPct={DD:F2}%)",
            previousMode, currentMode, latest.DrawdownPct);

        // ── Handle transition ─────────────────────────────────────────────────
        if (currentMode == RecoveryMode.Halted)
        {
            // Critical drawdown threshold crossed — pause all active strategies immediately
            // to stop the engine from accumulating further losses.
            await PauseAllActiveStrategiesAsync(writeCtx, mediator, latest.DrawdownPct, ct);
        }
        else if (previousMode == RecoveryMode.Halted)
        {
            // Account has recovered from a halted state — re-enable only the strategies
            // this worker paused automatically. Manually paused strategies are intentionally
            // excluded to avoid overriding an operator's explicit pause decision.
            await ResumeAutoPausedStrategiesAsync(readCtx, writeCtx, mediator, ct);
        }
        else if (currentMode == RecoveryMode.Reduced)
        {
            // Drawdown entered the "caution zone" — strategies continue but should
            // reduce lot sizes. The actual lot-size reduction is the responsibility
            // of the strategy evaluators that read CK_ActiveMode.
            _logger.LogWarning(
                "DrawdownRecoveryWorker: account entered REDUCED mode — DrawdownPct={DD:F2}%. " +
                "Lot sizing should be reduced. Active strategies continue with caution.",
                latest.DrawdownPct);
        }
        else if (currentMode == RecoveryMode.Normal)
        {
            _logger.LogInformation(
                "DrawdownRecoveryWorker: account returned to NORMAL mode — DrawdownPct={DD:F2}%.",
                latest.DrawdownPct);
        }

        // ── Persist the new active mode ───────────────────────────────────────
        // Storing the mode in EngineConfig allows other workers and query handlers to
        // read the current drawdown state without querying the snapshot table.
        await UpsertConfigAsync(writeCtx, CK_ActiveMode, currentMode.ToString(), ct);

        // Record the transition in the audit trail for compliance and post-incident review.
        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = "Account",
            EntityId     = 0,
            DecisionType = "DrawdownModeTransition",
            Outcome      = currentMode.ToString(),
            Reason       = $"Mode changed from {previousMode} to {currentMode}. DrawdownPct={latest.DrawdownPct:F2}%",
            Source       = "DrawdownRecoveryWorker"
        }, ct);
    }

    // ── Strategy pause / resume ───────────────────────────────────────────────

    /// <summary>
    /// Bulk-pauses all strategies currently in the <see cref="StrategyStatus.Active"/> state
    /// and stores their IDs in <c>EngineConfig</c> so they can be precisely resumed later.
    /// Uses <c>ExecuteUpdateAsync</c> (a single bulk SQL UPDATE) rather than individual
    /// entity saves to minimise latency during the critical halt sequence.
    /// </summary>
    /// <param name="writeCtx">Write DB context used for the bulk update and config upsert.</param>
    /// <param name="mediator">MediatR used to write per-strategy audit entries.</param>
    /// <param name="drawdownPct">Current drawdown percentage, included in audit messages.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task PauseAllActiveStrategiesAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        decimal                                 drawdownPct,
        CancellationToken                       ct)
    {
        // Collect IDs before the bulk update so we know exactly which strategies were paused.
        var activeIds = await writeCtx.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (activeIds.Count == 0) return;

        // Single bulk UPDATE — avoids N round-trips and minimises the window during which
        // strategies could generate new signals before being paused.
        await writeCtx.Set<Strategy>()
            .Where(s => activeIds.Contains(s.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, StrategyStatus.Paused),
                ct);

        // Store the IDs so we can resume exactly these strategies later.
        // Serialising to JSON allows the list to survive worker restarts.
        var json = JsonSerializer.Serialize(activeIds);
        await UpsertConfigAsync(writeCtx, CK_AutoPausedIds, json, ct);

        _logger.LogWarning(
            "DrawdownRecoveryWorker: HALTED — paused {Count} active strategy/strategies " +
            "due to DrawdownPct={DD:F2}%. IDs: {Ids}",
            activeIds.Count, drawdownPct, string.Join(", ", activeIds));

        // Individual audit entries per strategy for a complete, queryable audit trail.
        foreach (var id in activeIds)
        {
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "Strategy",
                EntityId     = id,
                DecisionType = "AutoPause",
                Outcome      = "Paused",
                Reason       = $"Account entered Halted drawdown mode (DrawdownPct={drawdownPct:F2}%). Auto-paused by DrawdownRecoveryWorker.",
                Source       = "DrawdownRecoveryWorker"
            }, ct);
        }
    }

    /// <summary>
    /// Resumes only the strategies that were automatically paused by
    /// <see cref="PauseAllActiveStrategiesAsync"/>. The list is sourced from the
    /// <c>DrawdownRecovery:AutoPausedStrategyIds</c> EngineConfig entry written at
    /// pause time. After resumption the entry is cleared to prevent stale IDs from
    /// being reactivated on a future recovery cycle.
    /// </summary>
    /// <param name="readCtx">Read DB context for loading the stored ID list.</param>
    /// <param name="writeCtx">Write DB context for the bulk strategy update and config clear.</param>
    /// <param name="mediator">MediatR used to write per-strategy audit entries.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task ResumeAutoPausedStrategiesAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        CancellationToken                       ct)
    {
        // Retrieve the JSON list of strategy IDs that were auto-paused during the halt.
        var idsEntry = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == CK_AutoPausedIds, ct);

        // If the entry is missing or empty, there is nothing to resume.
        if (idsEntry?.Value is null) return;

        List<long>? ids;
        try { ids = JsonSerializer.Deserialize<List<long>>(idsEntry.Value); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "DrawdownRecoveryWorker: failed to deserialize auto-paused strategy IDs — treating as empty. Raw value: {Value}",
                idsEntry.Value?.Length > 200 ? idsEntry.Value[..200] + "…" : idsEntry.Value);
            ids = null;
        }

        if (ids is null || ids.Count == 0) return;

        // Re-activate only the previously auto-paused strategies.
        // The IsDeleted filter ensures soft-deleted strategies are not resurrected.
        await writeCtx.Set<Strategy>()
            .Where(s => ids.Contains(s.Id) && !s.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, StrategyStatus.Active),
                ct);

        // Clear the stored list — prevents a second resume on the next polling cycle
        // if the mode bounces back to Halted briefly.
        await UpsertConfigAsync(writeCtx, CK_AutoPausedIds, string.Empty, ct);

        _logger.LogInformation(
            "DrawdownRecoveryWorker: resumed {Count} auto-paused strategy/strategies (IDs: {Ids}).",
            ids.Count, string.Join(", ", ids));

        foreach (var id in ids)
        {
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "Strategy",
                EntityId     = id,
                DecisionType = "AutoResume",
                Outcome      = "Active",
                Reason       = "Account exited Halted drawdown mode. Strategy auto-resumed by DrawdownRecoveryWorker.",
                Source       = "DrawdownRecoveryWorker"
            }, ct);
        }
    }

    // ── EngineConfig upsert ───────────────────────────────────────────────────

    /// <summary>
    /// Updates the value of an existing <see cref="EngineConfig"/> row or inserts a new one
    /// if the key does not yet exist. Uses a bulk <c>ExecuteUpdateAsync</c> first (optimistic
    /// update) and falls back to an <c>Add</c> + <c>SaveChangesAsync</c> if no rows were
    /// modified. This avoids a read-before-write and is safe for concurrent workers because
    /// <c>ExecuteUpdateAsync</c> is a single atomic SQL statement.
    /// </summary>
    /// <param name="writeCtx">Write DB context for the upsert operation.</param>
    /// <param name="key">The EngineConfig key to upsert.</param>
    /// <param name="value">The new string value to store.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        // Attempt a bulk update first — zero allocations if the row already exists.
        int rows = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Value, value), ct);

        if (rows == 0)
        {
            // Row does not yet exist — insert it. This happens only on the first
            // mode transition after a fresh deployment.
            writeCtx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key           = key,
                Value         = value,
                DataType      = ConfigDataType.String,
                Description   = $"Managed by DrawdownRecoveryWorker — {key}",
                LastUpdatedAt = DateTime.UtcNow
            });
            await writeCtx.SaveChangesAsync(ct);
        }
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from the <see cref="EngineConfig"/> table by key, falling back
    /// to <paramref name="defaultValue"/> if the key does not exist or if the stored value
    /// cannot be converted to <typeparamref name="T"/>. This makes all configuration
    /// hot-reloadable without a worker restart: the next polling cycle will pick up any
    /// changes made to the EngineConfig table.
    /// </summary>
    /// <typeparam name="T">The target type (e.g. <see cref="int"/>, <see cref="bool"/>).</typeparam>
    /// <param name="ctx">Read DB context.</param>
    /// <param name="key">EngineConfig key to look up.</param>
    /// <param name="defaultValue">Fallback value returned when the key is missing or unreadable.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    /// <returns>The stored value converted to <typeparamref name="T"/>, or <paramref name="defaultValue"/>.</returns>
    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        // Convert.ChangeType handles string-to-int, string-to-bool, etc.
        // The catch block ensures a malformed config value never crashes the worker.
        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
