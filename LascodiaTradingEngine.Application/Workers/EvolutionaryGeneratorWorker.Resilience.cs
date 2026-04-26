using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Cycle resilience helpers for <see cref="EvolutionaryGeneratorWorker"/>: unique-
/// constraint classification, jittered next-poll computation, fleet-systemic +
/// staleness alert tracking. Kept in a sibling partial so the orchestrator file stays
/// focused on the per-cycle proposal-and-persist flow.
/// </summary>
public sealed partial class EvolutionaryGeneratorWorker
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
    /// Increments <see cref="_consecutiveZeroInsertCycles"/> on cycles that proposed
    /// candidates but produced zero inserts; raises an alert once the streak crosses the
    /// configured threshold; resolves on the first productive cycle.
    /// </summary>
    private async Task UpdateFleetSystemicAlertAsync(
        EvolutionaryGeneratorCycleResult result,
        EvolutionaryGeneratorSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // Pure non-attempts (zero proposals, e.g. nothing eligible to mutate from) aren't
        // failure evidence — skip without changing the streak.
        if (result.ProposedCandidateCount == 0)
            return;

        if (result.InsertedCandidateCount > 0)
        {
            _consecutiveZeroInsertCycles = 0;
            await ResolveAlertByDedupeKeyAsync(FleetSystemicDedupeKey, _fleetSystemicAlertActive,
                () => _fleetSystemicAlertActive = false, nowUtc, ct);
            return;
        }

        _consecutiveZeroInsertCycles++;
        if (_consecutiveZeroInsertCycles < settings.FleetSystemicConsecutiveZeroInsertCycles)
            return;

        var conditionJson = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Detector = WorkerName,
            Reason = "fleet_zero_insert_streak",
            ConsecutiveCycles = _consecutiveZeroInsertCycles,
            Threshold = settings.FleetSystemicConsecutiveZeroInsertCycles,
            EvaluatedAt = nowUtc,
            LastResult = new
            {
                result.ProposedCandidateCount,
                result.IneligibleParentCount,
                result.InvalidParameterCount,
                result.DuplicateProposalCount,
                result.ExistingStrategyCount,
                result.ActiveQueueCount,
                result.PersistenceFailureCount,
            },
            Message = $"{WorkerName}: produced zero inserts for {_consecutiveZeroInsertCycles} consecutive cycle(s) (>= {settings.FleetSystemicConsecutiveZeroInsertCycles}) despite proposing candidates. Investigate parent eligibility, idempotency-key collisions, or persistence regression.",
        });

        await UpsertActiveAlertAsync(FleetSystemicDedupeKey, AlertType.SystemicMLDegradation, AlertSeverity.High, conditionJson, nowUtc, ct);
        _metrics?.EvolutionaryFleetSystemicAlerts.Add(1);
        _fleetSystemicAlertActive = true;
    }

    /// <summary>
    /// Fires <c>DataQualityIssue</c> when no draft strategy has been inserted by this
    /// worker (i.e. <c>GenerationCycleId</c> populated, status Paused, lifecycle Draft)
    /// within <c>StalenessAlertHours</c>. Auto-resolves on the next productive cycle.
    /// Skips the alert entirely when no draft has ever been inserted — fleet-alert covers
    /// that bootstrap case.
    /// </summary>
    private async Task UpdateStalenessAlertAsync(
        DbContext db,
        EvolutionaryGeneratorSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var newestCreatedAt = await db.Set<Strategy>()
            .AsNoTracking()
            .Where(s => !s.IsDeleted && s.GenerationCycleId != null && s.GenerationCycleId.StartsWith("evo-"))
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => (DateTime?)s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (newestCreatedAt is null) return;

        var ageHours = (nowUtc - newestCreatedAt.Value).TotalHours;
        bool stale = ageHours >= settings.StalenessAlertHours;

        if (stale)
        {
            var conditionJson = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Detector = WorkerName,
                Reason = "evolutionary_insert_stale",
                NewestCreatedAt = newestCreatedAt.Value,
                AgeHours = Math.Round(ageHours, 2),
                ThresholdHours = settings.StalenessAlertHours,
                EvaluatedAt = nowUtc,
                Message = $"Newest evolutionary draft is {ageHours:F1}h old (threshold {settings.StalenessAlertHours}h). Investigate worker schedule, parent-eligibility filter, or persistence failure rate.",
            });
            await UpsertActiveAlertAsync(StalenessDedupeKey, AlertType.DataQualityIssue, AlertSeverity.Medium, conditionJson, nowUtc, ct);
            _metrics?.EvolutionaryStalenessAlerts.Add(1);
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
