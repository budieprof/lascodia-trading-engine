using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
public sealed class OptimizationChronicFailureEscalator
{

    private readonly ILogger<OptimizationChronicFailureEscalator> _logger;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationChronicFailureEscalator(
        ILogger<OptimizationChronicFailureEscalator> logger,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
    }

    internal async Task EscalateAsync(
        DbContext db,
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        IMediator mediator,
        IAlertDispatcher alertDispatcher,
        long strategyId,
        string strategyName,
        string strategySymbol,
        int maxConsecutiveFailures,
        int baseCooldownDays,
        CancellationToken ct)
    {
        var recentStatuses = await db.Set<OptimizationRun>()
            .Where(r => r.StrategyId == strategyId && !r.IsDeleted
                     && (r.Status == OptimizationRunStatus.Completed
                      || r.Status == OptimizationRunStatus.Approved
                      || r.Status == OptimizationRunStatus.Rejected
                      || r.Status == OptimizationRunStatus.Abandoned))
            .OrderByDescending(r => r.CompletedAt)
            .Take(maxConsecutiveFailures + 1)
            .Select(r => r.Status)
            .ToListAsync(ct);

        int consecutiveFailures = 0;
        foreach (var status in recentStatuses)
        {
            if (status == OptimizationRunStatus.Approved)
                break;

            consecutiveFailures++;
        }

        if (consecutiveFailures < maxConsecutiveFailures)
            return;

        _logger.LogWarning(
            "OptimizationWorker: strategy {Id} ({Name}) has failed auto-approval {Count} consecutive times - escalating",
            strategyId,
            strategyName,
            consecutiveFailures);

        var nowUtc = UtcNow;
        string deduplicationKey = BuildDeduplicationKey(strategyId);
        var existingAlert = await writeDb.Set<Alert>()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == deduplicationKey && !a.IsDeleted, ct);

        int alertCooldown = await AlertCooldownDefaults.GetCooldownAsync(
            writeDb, AlertCooldownDefaults.CK_OptimizationEscalation, AlertCooldownDefaults.Default_OptimizationEscalation, ct);

        if (existingAlert?.LastTriggeredAt is DateTime lastTriggeredAt
            && lastTriggeredAt >= nowUtc.AddSeconds(-Math.Max(existingAlert.CooldownSeconds, alertCooldown)))
        {
            _logger.LogDebug(
                "OptimizationWorker: suppressed duplicate chronic failure alert for strategy {Id} ({Name}) within cooldown window",
                strategyId,
                strategyName);
            return;
        }

        string alertMessage = $"Strategy '{strategyName}' (ID={strategyId}) has failed auto-approval {consecutiveFailures} consecutive times. Manual parameter review recommended. Cooldown extended to {baseCooldownDays * 2} days to reduce compute waste.";
        var alert = existingAlert ?? new Alert();
        alert.AlertType = AlertType.OptimizationLifecycleIssue;
        alert.Symbol = strategySymbol;
        alert.ConditionJson = JsonSerializer.Serialize(new
        {
            Type = "ChronicOptimizationFailure",
            StrategyId = strategyId,
            StrategyName = strategyName,
            ConsecutiveFailures = consecutiveFailures,
            Message = alertMessage,
        });
        alert.Severity = AlertSeverity.High;
        alert.IsActive = true;
        alert.LastTriggeredAt = nowUtc;
        alert.DeduplicationKey = deduplicationKey;
        alert.CooldownSeconds = alertCooldown;

        if (existingAlert is null)
            writeDb.Set<Alert>().Add(alert);

        await writeCtx.SaveChangesAsync(ct);

        try
        {
            await alertDispatcher.DispatchAsync(alert, alertMessage, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OptimizationWorker: immediate alert dispatch failed (non-fatal)");
        }

        await mediator.Send(new LogDecisionCommand
        {
            EntityType = "Strategy",
            EntityId = strategyId,
            DecisionType = "ChronicOptimizationFailure",
            Outcome = $"Escalated after {consecutiveFailures} consecutive failures",
            Reason = $"Auto-approval failed {consecutiveFailures} times; alert created, cooldown extended",
            Source = "OptimizationWorker"
        }, ct);
    }

    internal static string BuildDeduplicationKey(long strategyId)
        => $"Optimization:ChronicFailure:{strategyId}";
}
