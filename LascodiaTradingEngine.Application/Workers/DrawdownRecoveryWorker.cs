using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
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
///   <item><description><c>DrawdownRecovery:SnapshotStaleAfterSeconds</c> — default 180 s</description></item>
/// </list>
/// </summary>
public sealed class DrawdownRecoveryWorker : BackgroundService
{
    private const int DefaultPollIntervalSeconds = 30;
    private const int DefaultSnapshotStaleAfterSeconds = 180;

    /// <summary>EngineConfig key that stores the polling interval in seconds (default 30).</summary>
    private const string CK_PollSecs       = "DrawdownRecovery:PollIntervalSeconds";
    private const string CK_SnapshotStaleAfterSecs = "DrawdownRecovery:SnapshotStaleAfterSeconds";

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
    private const string DrawdownPauseReason = "DrawdownRecovery";
    private const string EmptyAutoPausedIdsJson = "[]";

    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<DrawdownRecoveryWorker> _logger;
    private readonly DrawdownRecoveryModeProvider _modeProvider;
    private readonly TimeProvider _timeProvider;
    private int _consecutiveErrors;
    private long? _lastStaleSnapshotWarningId;

    /// <summary>
    /// Initialises the worker.
    /// </summary>
    /// <param name="scopeFactory">Factory for creating scoped DI contexts per polling cycle.</param>
    /// <param name="modeProvider">Cached provider invalidated whenever this worker persists a mode change.</param>
    /// <param name="logger">Structured logger for this worker.</param>
    public DrawdownRecoveryWorker(
        IServiceScopeFactory             scopeFactory,
        DrawdownRecoveryModeProvider     modeProvider,
        ILogger<DrawdownRecoveryWorker>  logger,
        TimeProvider?                    timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _modeProvider = modeProvider;
        _logger       = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
            int pollSecs = DefaultPollIntervalSeconds;

            try
            {
                pollSecs = await ReadPollIntervalSecondsAsync(stoppingToken);
                await RunCycleAsync(stoppingToken);
                _consecutiveErrors = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — exit the loop without logging an error.
                break;
            }
            catch (Exception ex)
            {
                _consecutiveErrors++;
                int backoffSeconds = Math.Min(300, 30 * (1 << Math.Min(_consecutiveErrors, 4)));
                _logger.LogError(ex,
                    "DrawdownRecoveryWorker loop error (consecutive={Count}), backing off {Backoff}s",
                    _consecutiveErrors, backoffSeconds);
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("DrawdownRecoveryWorker stopping.");
    }

    /// <summary>
    /// Runs a single enforcement cycle. Internal for deterministic unit test access.
    /// </summary>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        // Create a new async scope per cycle — this ensures scoped EF contexts
        // (IReadApplicationDbContext, IWriteApplicationDbContext) are freshly
        // instantiated and disposed after each cycle.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await EnforceModeAsync(
            writeDb.GetDbContext(),
            mediator,
            ct);
    }

    /// <summary>
    /// Reads and normalizes the hot-reloadable poll interval.
    /// Internal for deterministic unit test access.
    /// </summary>
    internal async Task<int> ReadPollIntervalSecondsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        return await GetPositiveIntConfigAsync(
            readDb.GetDbContext(),
            CK_PollSecs,
            DefaultPollIntervalSeconds,
            ct);
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
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        CancellationToken                       ct)
    {
        bool modeTransitionCommitted = false;
        var strategy = writeCtx.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async token =>
        {
            await using var tx = await writeCtx.Database.BeginTransactionAsync(token);

            // Load latest drawdown snapshot inside the transaction so the worker acts on
            // the most current persisted mode available at the moment enforcement begins.
            var latest = await LoadLatestSnapshotAsync(writeCtx, token);

            // No snapshots yet (e.g. engine just started) — nothing to enforce.
            if (latest is null)
                return;

            var snapshotRecordedAtUtc = NormalizeUtc(latest.RecordedAt);
            int staleAfterSeconds = await ReadSnapshotStaleAfterSecondsAsync(writeCtx, token);
            var snapshotAge = _timeProvider.GetUtcNow().UtcDateTime - snapshotRecordedAtUtc;
            if (snapshotAge < TimeSpan.Zero)
                snapshotAge = TimeSpan.Zero;

            // Defensive stale-data guard: do not pause or resume strategies from an old
            // snapshot if DrawdownMonitorWorker has stalled or the engine just restarted.
            if (snapshotAge > TimeSpan.FromSeconds(staleAfterSeconds))
            {
                if (_lastStaleSnapshotWarningId != latest.Id)
                {
                    _logger.LogWarning(
                        "DrawdownRecoveryWorker: latest drawdown snapshot {SnapshotId} is stale (RecordedAt={RecordedAt:o}, AgeSeconds={Age:F0}, MaxAgeSeconds={MaxAge}). " +
                        "Skipping enforcement until a fresh snapshot arrives.",
                        latest.Id,
                        snapshotRecordedAtUtc,
                        snapshotAge.TotalSeconds,
                        staleAfterSeconds);
                    _lastStaleSnapshotWarningId = latest.Id;
                }

                return;
            }

            _lastStaleSnapshotWarningId = null;

            RecoveryMode currentMode = latest.RecoveryMode;
            RecoveryMode previousMode = await ReadPersistedModeAsync(writeCtx, token);

            // Early exit — mode has not changed, no action required.
            if (currentMode == previousMode)
                return;

            _logger.LogInformation(
                "DrawdownRecoveryWorker: mode transition {From} → {To} (DrawdownPct={DD:F2}%, SnapshotId={SnapshotId}, RecordedAt={RecordedAt:o})",
                previousMode, currentMode, latest.DrawdownPct, latest.Id, snapshotRecordedAtUtc);

            List<long> pausedIds = [];
            List<long> resumedIds = [];

            // ── Handle transition ─────────────────────────────────────────────
            if (currentMode == RecoveryMode.Halted)
            {
                // Critical drawdown threshold crossed — pause all active strategies immediately
                // to stop the engine from accumulating further losses.
                pausedIds = await PauseAllActiveStrategiesAsync(
                    writeCtx,
                    mediator,
                    latest,
                    previousMode,
                    currentMode,
                    snapshotAge,
                    token);
            }
            else if (previousMode == RecoveryMode.Halted)
            {
                // Account has recovered from a halted state — re-enable only the strategies
                // this worker paused automatically. Manually paused strategies are intentionally
                // excluded to avoid overriding an operator's explicit pause decision.
                resumedIds = await ResumeAutoPausedStrategiesAsync(
                    writeCtx,
                    mediator,
                    latest,
                    previousMode,
                    currentMode,
                    snapshotAge,
                    token);

                // If the new mode is Reduced (not Normal), log that strategies are resumed
                // but should continue with reduced lot sizing until fully recovered.
                if (currentMode == RecoveryMode.Reduced)
                {
                    _logger.LogWarning(
                        "DrawdownRecoveryWorker: transitioned Halted → Reduced (not yet Normal). " +
                        "Strategies resumed but lot sizing should remain reduced. DrawdownPct={DD:F2}%",
                        latest.DrawdownPct);
                }
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

            // ── Persist the new active mode ───────────────────────────────────
            // Storing the mode in EngineConfig allows other workers and query handlers to
            // read the current drawdown state without querying the snapshot table.
            await UpsertConfigAsync(writeCtx, CK_ActiveMode, currentMode.ToString(), token);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "Account",
                EntityId     = 0,
                DecisionType = "DrawdownModeTransition",
                Outcome      = currentMode.ToString(),
                Reason       = $"Mode changed from {previousMode} to {currentMode}. DrawdownPct={latest.DrawdownPct:F2}%",
                ContextJson  = BuildTransitionAuditContextJson(
                    latest,
                    previousMode,
                    currentMode,
                    snapshotRecordedAtUtc,
                    snapshotAge,
                    pausedIds,
                    resumedIds),
                Source       = "DrawdownRecoveryWorker"
            }, token);

            await tx.CommitAsync(token);
            modeTransitionCommitted = true;
        }, ct);

        if (modeTransitionCommitted)
        {
            // Drop the cached snapshot in DrawdownRecoveryModeProvider only after the
            // transaction commits so hot-path callers never observe an uncommitted mode.
            _modeProvider.Invalidate();
        }
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
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task<List<long>> PauseAllActiveStrategiesAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        DrawdownSnapshot                        latest,
        RecoveryMode                            previousMode,
        RecoveryMode                            currentMode,
        TimeSpan                                snapshotAge,
        CancellationToken                       ct)
    {
        // Collect candidate IDs before the bulk update. The follow-up query below
        // re-reads the actual drawdown-owned rows so we never resume a strategy whose
        // state changed concurrently between the SELECT and UPDATE.
        var candidateIds = await writeCtx.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .Select(s => s.Id)
            .ToListAsync(ct);

        // Single bulk UPDATE — avoids N round-trips and minimises the window during which
        // strategies could generate new signals before being paused.
        if (candidateIds.Count > 0)
        {
            await writeCtx.Set<Strategy>()
                .Where(s => candidateIds.Contains(s.Id) &&
                            s.Status == StrategyStatus.Active &&
                            !s.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, StrategyStatus.Paused)
                    .SetProperty(x => x.PauseReason, DrawdownPauseReason),
                    ct);
        }

        // Re-read the final owned set so we only persist and audit strategies that are
        // still paused specifically by DrawdownRecovery.
        var pausedIds = candidateIds.Count == 0
            ? []
            : await writeCtx.Set<Strategy>()
                .AsNoTracking()
                .Where(s => candidateIds.Contains(s.Id) &&
                            s.Status == StrategyStatus.Paused &&
                            s.PauseReason == DrawdownPauseReason &&
                            !s.IsDeleted)
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .ToListAsync(ct);

        // Persist the owned set on every Halted transition, even when empty, so stale
        // lists from a previous halt cannot be replayed on recovery.
        var json = SerializeStrategyIds(pausedIds);
        await UpsertConfigAsync(writeCtx, CK_AutoPausedIds, json, ct);

        _logger.LogWarning(
            "DrawdownRecoveryWorker: HALTED — paused {Count} active strategy/strategies " +
            "due to DrawdownPct={DD:F2}%. IDs: {Ids}",
            pausedIds.Count, latest.DrawdownPct, string.Join(", ", pausedIds));

        // Individual audit entries per strategy for a complete, queryable audit trail.
        foreach (var id in pausedIds)
        {
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "Strategy",
                EntityId     = id,
                DecisionType = "AutoPause",
                Outcome      = "Paused",
                Reason       = $"Account entered Halted drawdown mode (DrawdownPct={latest.DrawdownPct:F2}%). Auto-paused by DrawdownRecoveryWorker.",
                ContextJson  = BuildStrategyAuditContextJson(
                    latest,
                    previousMode,
                    currentMode,
                    NormalizeUtc(latest.RecordedAt),
                    snapshotAge,
                    operation: "Pause"),
                Source       = "DrawdownRecoveryWorker"
            }, ct);
        }

        return pausedIds;
    }

    /// <summary>
    /// Resumes only the strategies that were automatically paused by
    /// <see cref="PauseAllActiveStrategiesAsync"/>. The list is sourced from the
    /// <c>DrawdownRecovery:AutoPausedStrategyIds</c> EngineConfig entry written at
    /// pause time. After resumption the entry is cleared to prevent stale IDs from
    /// being reactivated on a future recovery cycle.
    /// </summary>
    /// <param name="writeCtx">Write DB context for the bulk strategy update and config clear.</param>
    /// <param name="mediator">MediatR used to write per-strategy audit entries.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task<List<long>> ResumeAutoPausedStrategiesAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        DrawdownSnapshot                        latest,
        RecoveryMode                            previousMode,
        RecoveryMode                            currentMode,
        TimeSpan                                snapshotAge,
        CancellationToken                       ct)
    {
        var trackedIds = await GetTrackedAutoPausedStrategyIdsAsync(writeCtx, ct);
        var resumableIds = trackedIds.Count == 0
            ? []
            : await writeCtx.Set<Strategy>()
                .AsNoTracking()
                .Where(s => trackedIds.Contains(s.Id) &&
                            s.Status == StrategyStatus.Paused &&
                            s.PauseReason == DrawdownPauseReason &&
                            !s.IsDeleted)
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .ToListAsync(ct);

        // Re-activate only the previously auto-paused strategies.
        // The PauseReason filter preserves manual/operator and other subsystem pauses.
        if (resumableIds.Count > 0)
        {
            await writeCtx.Set<Strategy>()
                .Where(s => resumableIds.Contains(s.Id) &&
                            s.Status == StrategyStatus.Paused &&
                            s.PauseReason == DrawdownPauseReason &&
                            !s.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, StrategyStatus.Active)
                    .SetProperty(x => x.PauseReason, (string?)null),
                    ct);
        }

        // Clear the stored list — prevents a second resume on the next polling cycle
        // if the mode bounces back to Halted briefly, and releases ownership of any
        // tracked strategy that was manually re-paused or otherwise changed meanwhile.
        await UpsertConfigAsync(writeCtx, CK_AutoPausedIds, EmptyAutoPausedIdsJson, ct);

        _logger.LogInformation(
            "DrawdownRecoveryWorker: resumed {Count} auto-paused strategy/strategies (IDs: {Ids}).",
            resumableIds.Count, string.Join(", ", resumableIds));

        foreach (var id in resumableIds)
        {
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "Strategy",
                EntityId     = id,
                DecisionType = "AutoResume",
                Outcome      = "Active",
                Reason       = $"Account exited Halted drawdown mode into {currentMode}. Strategy auto-resumed by DrawdownRecoveryWorker.",
                ContextJson  = BuildStrategyAuditContextJson(
                    latest,
                    previousMode,
                    currentMode,
                    NormalizeUtc(latest.RecordedAt),
                    snapshotAge,
                    operation: "Resume"),
                Source       = "DrawdownRecoveryWorker"
            }, ct);
        }

        return resumableIds;
    }

    // ── EngineConfig upsert ───────────────────────────────────────────────────

    /// <summary>
    /// Updates the value of an existing <see cref="EngineConfig"/> row or inserts a new one
    /// if the key does not yet exist. The shared helper uses an atomic PostgreSQL upsert in
    /// production and a tracked fallback for non-Postgres providers used in tests.
    /// </summary>
    /// <param name="writeCtx">Write DB context for the upsert operation.</param>
    /// <param name="key">The EngineConfig key to upsert.</param>
    /// <param name="value">The new string value to store.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    private static Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(writeCtx, key, value, ct: ct);

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a raw EngineConfig value by key.
    /// </summary>
    private static async Task<string?> GetConfigValueAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        return entry?.Value;
    }

    private async Task<int> ReadSnapshotStaleAfterSecondsAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
        => await GetPositiveIntConfigAsync(
            ctx,
            CK_SnapshotStaleAfterSecs,
            DefaultSnapshotStaleAfterSeconds,
            ct);

    private async Task<int> GetPositiveIntConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        int                                     defaultValue,
        CancellationToken                       ct)
    {
        var raw = await GetConfigValueAsync(ctx, key, ct);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        _logger.LogWarning(
            "DrawdownRecoveryWorker: invalid positive integer '{Value}' for {Key} — using default {Default}.",
            raw,
            key,
            defaultValue);

        return defaultValue;
    }

    private async Task<RecoveryMode> ReadPersistedModeAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
    {
        var raw = await GetConfigValueAsync(ctx, CK_ActiveMode, ct);
        if (string.IsNullOrWhiteSpace(raw))
            return RecoveryMode.Normal;

        if (Enum.TryParse<RecoveryMode>(raw.Trim(), ignoreCase: true, out var parsed))
            return parsed;

        _logger.LogWarning(
            "DrawdownRecoveryWorker: invalid recovery mode '{Value}' in {Key} — defaulting to {DefaultMode}.",
            raw,
            CK_ActiveMode,
            RecoveryMode.Normal);

        return RecoveryMode.Normal;
    }

    private static async Task<DrawdownSnapshot?> LoadLatestSnapshotAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
        => await ctx.Set<DrawdownSnapshot>()
            .AsNoTracking()
            .OrderByDescending(s => s.RecordedAt)
            .ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

    private async Task<List<long>> GetTrackedAutoPausedStrategyIdsAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
    {
        var idsEntry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == CK_AutoPausedIds, ct);

        if (!string.IsNullOrWhiteSpace(idsEntry?.Value))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<long>>(idsEntry.Value);
                var sanitized = SanitizeStrategyIds(parsed);
                if (sanitized.Count > 0)
                    return sanitized;

                return [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "DrawdownRecoveryWorker: failed to deserialize auto-paused strategy IDs — falling back to strategy state. Raw value: {Value}",
                    idsEntry.Value.Length > 200 ? idsEntry.Value[..200] + "..." : idsEntry.Value);
            }
        }

        var fallbackIds = await ctx.Set<Strategy>()
            .AsNoTracking()
            .Where(s => s.Status == StrategyStatus.Paused &&
                        s.PauseReason == DrawdownPauseReason &&
                        !s.IsDeleted)
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (fallbackIds.Count > 0)
        {
            _logger.LogWarning(
                "DrawdownRecoveryWorker: auto-paused strategy ID list missing/unreadable — recovered {Count} strategy/strategies from persisted PauseReason state.",
                fallbackIds.Count);
        }

        return fallbackIds;
    }

    private static List<long> SanitizeStrategyIds(IEnumerable<long>? ids)
        => ids?
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList()
        ?? [];

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc         => value,
            DateTimeKind.Local       => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _                        => value
        };

    private static string BuildTransitionAuditContextJson(
        DrawdownSnapshot   latest,
        RecoveryMode       previousMode,
        RecoveryMode       currentMode,
        DateTime           snapshotRecordedAtUtc,
        TimeSpan           snapshotAge,
        IReadOnlyList<long> pausedIds,
        IReadOnlyList<long> resumedIds)
        => JsonSerializer.Serialize(new
        {
            SnapshotId              = latest.Id,
            SnapshotRecordedAtUtc   = snapshotRecordedAtUtc,
            SnapshotAgeSeconds      = Math.Round(snapshotAge.TotalSeconds, 3),
            DrawdownPct             = latest.DrawdownPct,
            PreviousMode            = previousMode.ToString(),
            CurrentMode             = currentMode.ToString(),
            AutoPausedStrategyCount = pausedIds.Count,
            AutoPausedStrategyIds   = pausedIds,
            AutoResumedStrategyCount = resumedIds.Count,
            AutoResumedStrategyIds   = resumedIds
        });

    private static string BuildStrategyAuditContextJson(
        DrawdownSnapshot latest,
        RecoveryMode     previousMode,
        RecoveryMode     currentMode,
        DateTime         snapshotRecordedAtUtc,
        TimeSpan         snapshotAge,
        string           operation)
        => JsonSerializer.Serialize(new
        {
            Operation            = operation,
            SnapshotId           = latest.Id,
            SnapshotRecordedAtUtc = snapshotRecordedAtUtc,
            SnapshotAgeSeconds   = Math.Round(snapshotAge.TotalSeconds, 3),
            DrawdownPct          = latest.DrawdownPct,
            PreviousMode         = previousMode.ToString(),
            CurrentMode          = currentMode.ToString()
        });

    private static string SerializeStrategyIds(IEnumerable<long> ids)
        => JsonSerializer.Serialize(SanitizeStrategyIds(ids));
}
