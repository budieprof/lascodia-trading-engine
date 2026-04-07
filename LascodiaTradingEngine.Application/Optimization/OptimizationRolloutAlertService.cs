using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationRolloutAlertService
{
    private readonly IAlertDispatcher _alertDispatcher;
    private readonly ILogger<OptimizationRolloutAlertService> _logger;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationRolloutAlertService(
        IAlertDispatcher alertDispatcher,
        ILogger<OptimizationRolloutAlertService> logger,
        TimeProvider timeProvider)
    {
        _alertDispatcher = alertDispatcher;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    internal async Task RaiseEvaluationFailureAlertAsync(
        IWriteApplicationDbContext writeCtx,
        DbContext writeDb,
        Strategy strategy,
        Exception ex,
        CancellationToken ct)
    {
        string deduplicationKey = $"OptimizationWorker:RolloutEval:{strategy.Id}";
        var existingAlert = await writeDb.Set<Alert>()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == deduplicationKey && !a.IsDeleted, ct);

        var nowUtc = UtcNow;
        if (existingAlert?.LastTriggeredAt is DateTime lastTriggeredAt
            && lastTriggeredAt >= nowUtc.AddHours(-6))
        {
            return;
        }

        string message =
            $"Rollout evaluation has failed {strategy.RolloutEvaluationFailureCount} consecutive times " +
            $"for strategy '{strategy.Name}' (ID={strategy.Id}). Rollout remains unchanged. " +
            $"Last error: {strategy.RolloutLastFailureMessage ?? ex.Message}";

        var alert = existingAlert ?? new Alert();
        alert.AlertType = AlertType.OptimizationLifecycleIssue;
        alert.Symbol = $"Strategy:{strategy.Id}:Rollout";
        alert.Channel = AlertChannel.Webhook;
        alert.Destination = string.Empty;
        alert.Severity = AlertSeverity.High;
        alert.IsActive = true;
        alert.LastTriggeredAt = nowUtc;
        alert.DeduplicationKey = deduplicationKey;
        alert.CooldownSeconds = (int)TimeSpan.FromHours(6).TotalSeconds;
        alert.ConditionJson = JsonSerializer.Serialize(new
        {
            Type = "RolloutEvaluationFailure",
            StrategyId = strategy.Id,
            StrategyName = strategy.Name,
            ConsecutiveFailures = strategy.RolloutEvaluationFailureCount,
            LastError = strategy.RolloutLastFailureMessage ?? ex.Message,
            Message = message,
        });

        if (existingAlert is null)
            writeDb.Set<Alert>().Add(alert);

        await writeCtx.SaveChangesAsync(ct);

        try
        {
            await _alertDispatcher.DispatchBySeverityAsync(alert, message, ct);
        }
        catch (Exception dispatchEx)
        {
            _logger.LogWarning(dispatchEx,
                "OptimizationWorker: immediate rollout-evaluation alert dispatch failed for strategy {Id} (non-fatal)",
                strategy.Id);
        }
    }
}
