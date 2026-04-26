using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Cycle resilience helpers for <see cref="CpcPretrainerWorker"/>: jittered + backoff-aware
/// next-poll computation, cycle-level distributed lock, fleet-systemic alert tracking, and
/// per-cycle phase-timing metric recording. Kept in a sibling partial file so the orchestrator
/// in <c>CpcPretrainerWorker.cs</c> stays focused on per-pair flow.
/// </summary>
public sealed partial class CpcPretrainerWorker
{
    /// <summary>
    /// Outcome of a cycle, surfaced to <see cref="ExecuteAsync"/> so it can compute the next
    /// poll interval (with jitter) and apply exponential backoff on consecutive failures.
    /// All values are sampled from the cycle's hot-reloaded config so operators tuning
    /// <c>EngineConfig</c> see new values applied on the next poll.
    /// </summary>
    internal readonly record struct CycleOutcome(
        int PollSeconds,
        int PollJitterSeconds,
        int FailureBackoffCapShift)
    {
        public static CycleOutcome From(MLCpcRuntimeConfig config)
            => new(config.PollSeconds, config.PollJitterSeconds, config.FailureBackoffCapShift);
    }

    /// <summary>
    /// Computes the next poll delay: <c>poll · 2^min(failures, capShift) + uniform[0, jitter]s</c>.
    /// Jitter is applied even on the success path (<paramref name="consecutiveFailures"/> = 0)
    /// so two replicas that started together don't continue to lockstep — every poll picks a
    /// fresh random offset. The exponent is clamped before computing the multiplier so an
    /// extended outage still polls at a finite rate.
    /// </summary>
    private static TimeSpan NextDelay(int pollSeconds, int jitterSeconds, int capShift, int consecutiveFailures)
    {
        long backoffMultiplier = consecutiveFailures > 0 && capShift > 0
            ? 1L << Math.Min(consecutiveFailures, capShift)
            : 1L;
        long basePart = (long)pollSeconds * backoffMultiplier;
        int jitterPart = jitterSeconds > 0
            ? Random.Shared.Next(0, jitterSeconds + 1)
            : 0;
        // Cap at 1 day — we don't want a degenerate timer; the operator should investigate.
        long totalSeconds = Math.Min(basePart + jitterPart, 86_400L);
        return TimeSpan.FromSeconds(totalSeconds);
    }

    private async Task<IAsyncDisposable?> TryAcquireCycleLockAsync(
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(config.CycleLockTimeoutSeconds);
        return await _distributedLock.TryAcquireAsync(CycleLockKey, timeout, ct);
    }

    private void RecordCycleDuration(long stopwatchStart, MLCpcRuntimeConfig config, string outcome)
    {
        if (_metrics is null) return;
        var ms = Stopwatch.GetElapsedTime(stopwatchStart).TotalMilliseconds;
        _metrics.MLCpcCycleDurationMs.Record(
            ms,
            new KeyValuePair<string, object?>("encoder_type", config.EncoderType.ToString()),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    /// <summary>
    /// Maintains a single fleet-systemic alert keyed by <see cref="FleetSystemicDedupeKey"/>.
    /// Increments <see cref="_consecutiveZeroPromotionCycles"/> on cycles where candidates
    /// were attempted but none promoted; raises an alert once the streak crosses the
    /// configured threshold; resolves on the first successful promotion.
    /// </summary>
    private async Task UpdateFleetSystemicAlertAsync(
        DbContext writeCtx,
        ICpcPretrainerAuditService auditService,
        MLCpcRuntimeConfig config,
        int attempted,
        int promoted,
        CancellationToken ct)
    {
        if (attempted == 0)
            return; // No candidates attempted → not fleet-failure evidence either way.

        if (promoted > 0)
        {
            _consecutiveZeroPromotionCycles = 0;
            await ResolveFleetSystemicAlertAsync(writeCtx, ct);
            return;
        }

        _consecutiveZeroPromotionCycles++;
        if (_consecutiveZeroPromotionCycles < config.FleetSystemicConsecutiveZeroPromotionCycles)
            return;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var conditionJson = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Detector = "CpcPretrainerWorker",
            Reason = "fleet_zero_promotion_streak",
            ConsecutiveCycles = _consecutiveZeroPromotionCycles,
            Threshold = config.FleetSystemicConsecutiveZeroPromotionCycles,
            EncoderType = config.EncoderType.ToString(),
            EvaluatedAt = now,
            Message = $"CpcPretrainerWorker has produced zero promotions for {_consecutiveZeroPromotionCycles} consecutive cycle(s) (>= {config.FleetSystemicConsecutiveZeroPromotionCycles}). Investigate trainer pipeline, gate-suite regression, or upstream data quality.",
        });

        await UpsertFleetSystemicAlertAsync(writeCtx, conditionJson, now, ct);
        _metrics?.MLCpcFleetSystemicAlerts.Add(1);
        _fleetSystemicAlertActive = true;
    }

    private async Task ResolveFleetSystemicAlertAsync(DbContext writeCtx, CancellationToken ct)
    {
        if (!_fleetSystemicAlertActive)
            return;

        var existing = await writeCtx.Set<Alert>()
            .Where(a => a.DeduplicationKey == FleetSystemicDedupeKey && a.IsActive && !a.IsDeleted)
            .ToListAsync(ct);
        if (existing.Count == 0)
        {
            _fleetSystemicAlertActive = false;
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var alert in existing)
        {
            alert.IsActive = false;
            alert.AutoResolvedAt ??= now;
        }
        await writeCtx.SaveChangesAsync(ct);
        _fleetSystemicAlertActive = false;
    }

    private static async Task UpsertFleetSystemicAlertAsync(
        DbContext writeCtx,
        string conditionJson,
        DateTime now,
        CancellationToken ct)
    {
        var existing = await writeCtx.Set<Alert>()
            .Where(a => a.DeduplicationKey == FleetSystemicDedupeKey && a.IsActive && !a.IsDeleted)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            existing.AlertType = AlertType.SystemicMLDegradation;
            existing.Severity = AlertSeverity.High;
            existing.ConditionJson = conditionJson;
            existing.LastTriggeredAt = now;
            existing.CooldownSeconds = 3600;
            existing.AutoResolvedAt = null;
        }
        else
        {
            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType = AlertType.SystemicMLDegradation,
                Severity = AlertSeverity.High,
                Symbol = null,
                DeduplicationKey = FleetSystemicDedupeKey,
                CooldownSeconds = 3600,
                ConditionJson = conditionJson,
                LastTriggeredAt = now,
                IsActive = true,
            });
        }
        await writeCtx.SaveChangesAsync(ct);
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static readonly NoopAsyncDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
