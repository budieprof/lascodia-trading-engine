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
    private const string CK_PollSecs       = "DrawdownRecovery:PollIntervalSeconds";
    private const string CK_ActiveMode     = "DrawdownRecovery:ActiveMode";
    private const string CK_AutoPausedIds  = "DrawdownRecovery:AutoPausedStrategyIds";

    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<DrawdownRecoveryWorker> _logger;

    public DrawdownRecoveryWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<DrawdownRecoveryWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DrawdownRecoveryWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 30;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 30, stoppingToken);

                await EnforceModeAsync(ctx, writeCtx, mediator, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
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

    private async Task EnforceModeAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        CancellationToken                       ct)
    {
        // Load latest drawdown snapshot
        var latest = await readCtx.Set<DrawdownSnapshot>()
            .OrderByDescending(s => s.RecordedAt)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (latest is null) return;

        RecoveryMode currentMode = latest.RecoveryMode;

        // Read the previously enforced mode from EngineConfig
        var modeEntry = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == CK_ActiveMode, ct);

        RecoveryMode previousMode = RecoveryMode.Normal;
        if (modeEntry?.Value is not null &&
            Enum.TryParse<RecoveryMode>(modeEntry.Value, out var parsed))
        {
            previousMode = parsed;
        }

        if (currentMode == previousMode) return; // no change — nothing to enforce

        _logger.LogInformation(
            "DrawdownRecoveryWorker: mode transition {From} → {To} (DrawdownPct={DD:F2}%)",
            previousMode, currentMode, latest.DrawdownPct);

        // ── Handle transition ─────────────────────────────────────────────────
        if (currentMode == RecoveryMode.Halted)
        {
            await PauseAllActiveStrategiesAsync(writeCtx, mediator, latest.DrawdownPct, ct);
        }
        else if (previousMode == RecoveryMode.Halted)
        {
            await ResumeAutoPausedStrategiesAsync(readCtx, writeCtx, mediator, ct);
        }
        else if (currentMode == RecoveryMode.Reduced)
        {
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
        await UpsertConfigAsync(writeCtx, CK_ActiveMode, currentMode.ToString(), ct);

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

    private async Task PauseAllActiveStrategiesAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        decimal                                 drawdownPct,
        CancellationToken                       ct)
    {
        var activeIds = await writeCtx.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (activeIds.Count == 0) return;

        await writeCtx.Set<Strategy>()
            .Where(s => activeIds.Contains(s.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, StrategyStatus.Paused),
                ct);

        // Store the IDs so we can resume exactly these strategies later
        var json = JsonSerializer.Serialize(activeIds);
        await UpsertConfigAsync(writeCtx, CK_AutoPausedIds, json, ct);

        _logger.LogWarning(
            "DrawdownRecoveryWorker: HALTED — paused {Count} active strategy/strategies " +
            "due to DrawdownPct={DD:F2}%. IDs: {Ids}",
            activeIds.Count, drawdownPct, string.Join(", ", activeIds));

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

    private async Task ResumeAutoPausedStrategiesAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        CancellationToken                       ct)
    {
        var idsEntry = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == CK_AutoPausedIds, ct);

        if (idsEntry?.Value is null) return;

        List<long>? ids;
        try { ids = JsonSerializer.Deserialize<List<long>>(idsEntry.Value); }
        catch { ids = null; }

        if (ids is null || ids.Count == 0) return;

        await writeCtx.Set<Strategy>()
            .Where(s => ids.Contains(s.Id) && !s.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, StrategyStatus.Active),
                ct);

        // Clear the stored list
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

    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        int rows = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Value, value), ct);

        if (rows == 0)
        {
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

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
