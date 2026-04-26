using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Chronic-tripper escalation for <see cref="MLCalibratedEdgeWorker"/>.
/// </summary>
/// <remarks>
/// A model that stays Critical for <c>ChronicCriticalThreshold</c> consecutive cycles
/// is unlikely to recover via repeated retraining. The streak counter persists in
/// <see cref="EngineConfig"/> across worker restarts; when the threshold is crossed
/// a separate <see cref="AlertType.MLModelDegraded"/> alert (with the chronic dedup
/// prefix) is dispatched to flag the model as a retirement candidate. When
/// <c>SuppressRetrainOnChronic</c> is set (default), the main worker also blocks
/// further auto-degrading retrains until operator intervention. Recovery to a
/// non-Critical state resets the counter and auto-resolves the chronic alert.
/// </remarks>
public sealed partial class MLCalibratedEdgeWorker
{
    private async Task<int> TrackChronicCriticalAndAlertIfNeededAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCalibratedEdgeWorkerSettings settings,
        MLCalibratedEdgeAlertState alertState,
        LiveEdgeSummary summary,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string counterKey = $"MLEdge:Model:{model.Id}:ConsecutiveCriticalCycles";
        int previous = await LoadExistingIntConfigAsync(db, counterKey, ct) ?? 0;

        if (alertState != MLCalibratedEdgeAlertState.Critical)
        {
            if (previous > 0)
            {
                await EngineConfigUpsert.UpsertAsync(
                    db,
                    counterKey,
                    "0",
                    ConfigDataType.Int,
                    "Consecutive cycles where this model's calibrated edge was Critical.",
                    isHotReloadable: false,
                    ct);

                if (previous >= settings.ChronicCriticalThreshold)
                {
                    await ResolveAlertAsync(
                        serviceProvider,
                        writeContext,
                        db,
                        model,
                        ChronicAlertDeduplicationPrefix,
                        nowUtc,
                        ct);
                }
            }

            return 0;
        }

        int next = previous + 1;
        await EngineConfigUpsert.UpsertAsync(
            db,
            counterKey,
            next.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Consecutive cycles where this model's calibrated edge was Critical.",
            isHotReloadable: false,
            ct);

        if (previous < settings.ChronicCriticalThreshold && next >= settings.ChronicCriticalThreshold)
        {
            await DispatchChronicAlertAsync(
                serviceProvider,
                writeContext,
                db,
                model,
                settings,
                summary,
                next,
                nowUtc,
                ct);
        }

        return next;
    }

    private async Task DispatchChronicAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCalibratedEdgeWorkerSettings settings,
        LiveEdgeSummary summary,
        int streak,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var dispatcher = serviceProvider.GetService<IAlertDispatcher>();
        if (dispatcher is null)
            return;

        try
        {
            string deduplicationKey = ChronicAlertDeduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture);
            bool exists = await db.Set<Alert>()
                .AnyAsync(alert => alert.DeduplicationKey == deduplicationKey && alert.IsActive && !alert.IsDeleted, ct);
            if (exists)
                return;

            int cooldownSeconds = await AlertCooldownDefaults.GetCooldownAsync(
                db,
                AlertCooldownDefaults.CK_MLEscalation,
                AlertCooldownDefaults.Default_MLEscalation,
                ct);

            string conditionJson = Truncate(JsonSerializer.Serialize(new
            {
                detector = "MLCalibratedEdge",
                kind = "chronic_critical",
                modelId = model.Id,
                symbol = model.Symbol,
                timeframe = model.Timeframe.ToString(),
                consecutiveCriticalCycles = streak,
                threshold = settings.ChronicCriticalThreshold,
                expectedValuePips = Math.Round(summary.ExpectedValuePips, 6),
                winRate = Math.Round(summary.WinRate, 6),
                meanProbabilityGap = Math.Round(summary.MeanProbabilityGap, 6),
                resolvedCount = summary.ResolvedCount,
                detectedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
            }), AlertConditionMaxLength);

            var alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                Severity = AlertSeverity.Critical,
                DeduplicationKey = deduplicationKey,
                CooldownSeconds = cooldownSeconds,
                ConditionJson = conditionJson,
                Symbol = model.Symbol,
                IsActive = true,
            };

            db.Set<Alert>().Add(alert);
            await writeContext.SaveChangesAsync(ct);

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "MLCalibratedEdge: model {0} ({1}/{2}) has been Critical for {3} consecutive cycles (threshold {4}). Repeated retraining is unlikely to recover; the model is a retirement candidate.",
                model.Id,
                model.Symbol,
                model.Timeframe,
                streak,
                settings.ChronicCriticalThreshold);

            await dispatcher.DispatchAsync(alert, message, ct);

            _metrics?.MLCalibratedEdgeAlertsDispatched.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                new KeyValuePair<string, object?>("state", "chronic"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch chronic-critical alert for model {ModelId}.",
                WorkerName,
                model.Id);
        }
    }
}
