using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Cycle resilience helpers for <see cref="MLAdwinDriftWorker"/>: unique-constraint
/// classification, jittered backoff delay, fleet-systemic + staleness alert tracking.
/// Kept in a sibling partial so the orchestrator file stays focused on the per-cycle
/// drift evaluation flow.
/// </summary>
public sealed partial class MLAdwinDriftWorker
{
    /// <summary>
    /// Classify a DB exception as a unique-constraint violation. Prefers the injected
    /// <see cref="IDatabaseExceptionClassifier"/>; falls back to reflection so Application
    /// stays decoupled from Npgsql when the typed service isn't wired up.
    /// </summary>
    private bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        if (_dbExceptionClassifier is not null)
            return _dbExceptionClassifier.IsUniqueConstraintViolation(ex);

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name == "PostgresException")
            {
                var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
                if (sqlState == "23505") return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Adds uniform <c>[0, jitterSeconds]</c> jitter to a base interval so replicas
    /// don't poll in lockstep. Returns the base unchanged when jitter is disabled.
    /// </summary>
    internal static TimeSpan ApplyJitter(TimeSpan baseInterval, int jitterSeconds)
    {
        if (jitterSeconds <= 0 || baseInterval <= TimeSpan.Zero)
            return baseInterval;
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(0, jitterSeconds + 1));
        return baseInterval + jitter;
    }

    /// <summary>
    /// Maintains a single fleet-systemic alert keyed by <see cref="FleetSystemicDedupeKey"/>.
    /// Fires <c>SystemicMLDegradation</c> when the cycle's drift count crosses the threshold;
    /// auto-resolves on the first cycle below threshold.
    /// </summary>
    private async Task UpdateFleetSystemicAlertAsync(
        int driftCount,
        int evaluatedCount,
        AdwinWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        bool shouldAlert = driftCount >= _options.FleetSystemicDriftThreshold;
        if (!shouldAlert)
        {
            await ResolveAlertByDedupeKeyAsync(FleetSystemicDedupeKey, _fleetSystemicAlertActive,
                () => _fleetSystemicAlertActive = false, nowUtc, ct);
            return;
        }

        var conditionJson = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Detector = WorkerName,
            Reason = "fleet_systemic_drift",
            DriftCount = driftCount,
            EvaluatedCount = evaluatedCount,
            Threshold = _options.FleetSystemicDriftThreshold,
            EvaluatedAt = nowUtc,
            Message = $"{WorkerName}: {driftCount} of {evaluatedCount} evaluated models show ADWIN drift in this cycle (>= {_options.FleetSystemicDriftThreshold}). Investigate upstream causes (data feed, regime shift, calibration regression).",
        });

        await UpsertActiveAlertAsync(FleetSystemicDedupeKey, AlertType.SystemicMLDegradation, AlertSeverity.High, conditionJson, nowUtc, ct);
        _metrics?.MLAdwinFleetSystemicAlerts.Add(1);
        _fleetSystemicAlertActive = true;
    }

    /// <summary>
    /// Fires <c>DataQualityIssue</c> when the most recent <c>MLAdwinDriftLog</c> is older
    /// than <c>StalenessAlertHours</c>. Auto-resolves once a fresher row exists. Skips
    /// alerting when no rows exist at all (the worker has never produced output) — fleet
    /// alerts and worker-health monitor signals cover the bootstrap case.
    /// </summary>
    private async Task UpdateStalenessAlertAsync(
        DbContext db,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var newestDetectedAt = await db.Set<MLAdwinDriftLog>()
            .AsNoTracking()
            .OrderByDescending(r => r.DetectedAt)
            .Select(r => (DateTime?)r.DetectedAt)
            .FirstOrDefaultAsync(ct);

        if (newestDetectedAt is null) return;

        var ageHours = (nowUtc - newestDetectedAt.Value).TotalHours;
        bool stale = ageHours >= _options.StalenessAlertHours;

        if (stale)
        {
            var conditionJson = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Detector = WorkerName,
                Reason = "adwin_drift_log_stale",
                NewestDetectedAt = newestDetectedAt.Value,
                AgeHours = Math.Round(ageHours, 2),
                ThresholdHours = _options.StalenessAlertHours,
                EvaluatedAt = nowUtc,
                Message = $"Newest MLAdwinDriftLog is {ageHours:F1}h old (threshold {_options.StalenessAlertHours}h). Investigate worker schedule, distributed lock contention, or upstream prediction-log volume.",
            });
            await UpsertActiveAlertAsync(StalenessDedupeKey, AlertType.DataQualityIssue, AlertSeverity.Medium, conditionJson, nowUtc, ct);
            _metrics?.MLAdwinStalenessAlerts.Add(1);
            _stalenessAlertActive = true;
        }
        else if (_stalenessAlertActive)
        {
            await ResolveAlertByDedupeKeyAsync(StalenessDedupeKey, true,
                () => _stalenessAlertActive = false, nowUtc, ct);
        }
    }

    private async Task UpsertActiveAlertAsync(
        string dedupeKey,
        AlertType alertType,
        AlertSeverity severity,
        string conditionJson,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeCtx.GetDbContext();

        var existing = await db.Set<Alert>()
            .Where(a => a.DeduplicationKey == dedupeKey && a.IsActive && !a.IsDeleted)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            existing.AlertType = alertType;
            existing.Severity = severity;
            existing.ConditionJson = conditionJson;
            existing.LastTriggeredAt = nowUtc;
            existing.CooldownSeconds = 3600;
            existing.AutoResolvedAt = null;
        }
        else
        {
            db.Set<Alert>().Add(new Alert
            {
                AlertType = alertType,
                Severity = severity,
                Symbol = null,
                DeduplicationKey = dedupeKey,
                CooldownSeconds = 3600,
                ConditionJson = conditionJson,
                LastTriggeredAt = nowUtc,
                IsActive = true,
            });
        }
        await writeCtx.SaveChangesAsync(ct);
    }

    private async Task ResolveAlertByDedupeKeyAsync(
        string dedupeKey,
        bool localFlag,
        Action clearLocalFlag,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (!localFlag) return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeCtx.GetDbContext();

        var existing = await db.Set<Alert>()
            .Where(a => a.DeduplicationKey == dedupeKey && a.IsActive && !a.IsDeleted)
            .ToListAsync(ct);

        foreach (var alert in existing)
        {
            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;
        }
        if (existing.Count > 0)
            await writeCtx.SaveChangesAsync(ct);
        clearLocalFlag();
    }
}
