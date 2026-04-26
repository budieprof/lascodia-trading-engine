using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Cycle resilience helpers for <see cref="CalibrationSnapshotWorker"/>: jittered + backoff-
/// aware next-poll computation, cycle-level distributed lock, unique-constraint classification,
/// fleet-systemic + staleness alert tracking, and per-cycle phase-timing metric recording.
/// Kept in a sibling partial file so the orchestrator in <c>CalibrationSnapshotWorker.cs</c>
/// stays focused on the per-month rollup flow.
/// </summary>
public sealed partial class CalibrationSnapshotWorker
{
    /// <summary>
    /// Computes the next poll delay: <c>poll · 2^min(failures, capShift) + uniform[0, jitter]s</c>.
    /// Clamped to a 7-day ceiling so an extended outage still polls eventually.
    /// </summary>
    internal static TimeSpan NextDelay(CalibrationSnapshotRuntimeConfig config, int consecutiveFailures)
    {
        long backoffMultiplier = consecutiveFailures > 0 && config.FailureBackoffCapShift > 0
            ? 1L << Math.Min(consecutiveFailures, config.FailureBackoffCapShift)
            : 1L;
        long baseSeconds = (long)config.PollIntervalHours * 3600 * backoffMultiplier;
        int jitter = config.PollJitterSeconds > 0 ? Random.Shared.Next(0, config.PollJitterSeconds + 1) : 0;
        long total = Math.Min(baseSeconds + jitter, 7L * 24 * 3600);
        return TimeSpan.FromSeconds(total);
    }

    private async Task<IAsyncDisposable?> TryAcquireCycleLockAsync(
        CalibrationSnapshotRuntimeConfig config,
        CancellationToken ct)
    {
        if (_distributedLock is null) return NoopAsyncDisposable.Instance;
        var timeout = TimeSpan.FromSeconds(config.CycleLockTimeoutSeconds);
        return await _distributedLock.TryAcquireAsync(CycleLockKey, timeout, ct);
    }

    private void RecordCycleDuration(long stopwatchStart, string outcome)
    {
        var ms = Stopwatch.GetElapsedTime(stopwatchStart).TotalMilliseconds;
        _metrics.CalibrationSnapshotCycleDurationMs.Record(
            ms, new KeyValuePair<string, object?>("outcome", outcome));
    }

    private bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        if (_dbExceptionClassifier is not null)
            return _dbExceptionClassifier.IsUniqueConstraintViolation(ex);

        // Reflection fallback when DI doesn't provide the typed service — keeps Application
        // decoupled from Npgsql.
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
    /// Fires <c>SystemicMLDegradation</c> when consecutive cycles produce zero processed
    /// months despite the cycle attempting to do work. Auto-resolves on the first cycle
    /// that processes at least one month.
    /// </summary>
    private async Task UpdateFleetSystemicAlertAsync(
        int monthsFailed,
        int monthsProcessed,
        int monthsSkipped,
        CalibrationSnapshotRuntimeConfig config,
        DateTime now,
        CancellationToken ct)
    {
        bool cycleProductive = monthsProcessed > 0;
        bool cycleAttempted = monthsFailed + monthsProcessed > 0;

        if (cycleProductive)
        {
            _consecutiveFailures = 0;
            await ResolveAlertByDedupeKeyAsync(FleetSystemicDedupeKey, _fleetSystemicAlertActive, () => _fleetSystemicAlertActive = false, now, ct);
            return;
        }

        if (!cycleAttempted) return; // pure all-empty cycle isn't degradation

        // Reaching here means at least one month failed and none succeeded.
        if (_consecutiveFailures < config.FleetSystemicConsecutiveFailureCycles) return;

        var conditionJson = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Detector = WorkerName,
            Reason = "fleet_zero_progress_streak",
            ConsecutiveCycles = _consecutiveFailures,
            Threshold = config.FleetSystemicConsecutiveFailureCycles,
            EvaluatedAt = now,
            Message = $"{WorkerName} has produced zero successful month writes for {_consecutiveFailures} consecutive cycle(s) (>= {config.FleetSystemicConsecutiveFailureCycles}). Investigate DB schema, rejection-pipeline, or query regression.",
        });

        await UpsertActiveAlertAsync(FleetSystemicDedupeKey, AlertType.SystemicMLDegradation, AlertSeverity.High, conditionJson, now, ct);
        _metrics.CalibrationSnapshotFleetSystemicAlerts.Add(1);
        _fleetSystemicAlertActive = true;
    }

    /// <summary>
    /// Fires <c>DataQualityIssue</c> when the most recent <c>CalibrationSnapshot</c> is
    /// older than the configured threshold. Auto-resolves once a fresher row exists.
    /// </summary>
    private async Task UpdateStalenessAlertAsync(
        CalibrationSnapshotRuntimeConfig config,
        DateTime now,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();

        var newestComputedAt = await readCtx.GetDbContext()
            .Set<CalibrationSnapshot>()
            .AsNoTracking()
            .OrderByDescending(s => s.ComputedAt)
            .Select(s => (DateTime?)s.ComputedAt)
            .FirstOrDefaultAsync(ct);

        // No rows at all → don't alert; staleness only meaningful once the worker has ever
        // produced output. The fleet-systemic alert covers the "should be producing but
        // isn't" case.
        if (newestComputedAt is null) return;

        var ageHours = (now - newestComputedAt.Value).TotalHours;
        bool stale = ageHours >= config.StalenessAlertHours;

        if (stale)
        {
            var conditionJson = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Detector = WorkerName,
                Reason = "calibration_snapshot_stale",
                NewestComputedAt = newestComputedAt.Value,
                AgeHours = Math.Round(ageHours, 2),
                ThresholdHours = config.StalenessAlertHours,
                EvaluatedAt = now,
                Message = $"Newest CalibrationSnapshot is {ageHours:F1}h old (threshold {config.StalenessAlertHours}h). Snapshots should arrive monthly; investigate worker schedule or upstream rejection pipeline.",
            });
            await UpsertActiveAlertAsync(StalenessDedupeKey, AlertType.DataQualityIssue, AlertSeverity.Medium, conditionJson, now, ct, writeCtxOverride: writeCtx);
            _metrics.CalibrationSnapshotStalenessAlerts.Add(1);
            _stalenessAlertActive = true;
        }
        else if (_stalenessAlertActive)
        {
            await ResolveAlertByDedupeKeyAsync(StalenessDedupeKey, true, () => _stalenessAlertActive = false, now, ct, writeCtxOverride: writeCtx);
        }
    }

    private async Task UpsertActiveAlertAsync(
        string dedupeKey,
        AlertType alertType,
        AlertSeverity severity,
        string conditionJson,
        DateTime now,
        CancellationToken ct,
        IWriteApplicationDbContext? writeCtxOverride = null)
    {
        IServiceScope? localScope = null;
        IWriteApplicationDbContext writeCtx;
        if (writeCtxOverride is not null)
        {
            writeCtx = writeCtxOverride;
        }
        else
        {
            localScope = _scopeFactory.CreateScope();
            writeCtx = localScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        }

        try
        {
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
                existing.LastTriggeredAt = now;
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
                    LastTriggeredAt = now,
                    IsActive = true,
                });
            }
            await writeCtx.SaveChangesAsync(ct);
        }
        finally
        {
            localScope?.Dispose();
        }
    }

    private async Task ResolveAlertByDedupeKeyAsync(
        string dedupeKey,
        bool localFlag,
        Action clearLocalFlag,
        DateTime now,
        CancellationToken ct,
        IWriteApplicationDbContext? writeCtxOverride = null)
    {
        if (!localFlag) return;

        IServiceScope? localScope = null;
        IWriteApplicationDbContext writeCtx;
        if (writeCtxOverride is not null)
        {
            writeCtx = writeCtxOverride;
        }
        else
        {
            localScope = _scopeFactory.CreateScope();
            writeCtx = localScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        }

        try
        {
            var db = writeCtx.GetDbContext();
            var existing = await db.Set<Alert>()
                .Where(a => a.DeduplicationKey == dedupeKey && a.IsActive && !a.IsDeleted)
                .ToListAsync(ct);
            foreach (var alert in existing)
            {
                alert.IsActive = false;
                alert.AutoResolvedAt ??= now;
            }
            if (existing.Count > 0)
                await writeCtx.SaveChangesAsync(ct);
            clearLocalFlag();
        }
        finally
        {
            localScope?.Dispose();
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static readonly NoopAsyncDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
