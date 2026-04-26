using System.Data;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

public sealed class MLConformalBreakerStateStore : IMLConformalBreakerStateStore
{
    private const int AlertCooldownSeconds = 3600;

    private readonly ILogger<MLConformalBreakerStateStore> _logger;
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    public MLConformalBreakerStateStore(
        ILogger<MLConformalBreakerStateStore> logger,
        TradingMetrics metrics,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BreakerStateResult> ApplyAsync(
        DbContext db,
        IReadOnlyCollection<BreakerTripCandidate> tripCandidates,
        IReadOnlyCollection<BreakerRecoveryCandidate> recoveryCandidates,
        IReadOnlyCollection<BreakerRefreshCandidate> refreshCandidates,
        CancellationToken ct)
    {
        BreakerStateResult result = new(0, 0, 0, 0, 0, 0, 0, []);
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var dispatches = new List<BreakerAlertDispatch>();

            int expiredCount = await ClearExpiredBreakersAsync(db, nowUtc, dispatches, token);
            int duplicateRepairCount = await DeactivateDuplicateActiveBreakersAsync(db, token);
            int recoveredCount = await RecoverBreakersAsync(db, recoveryCandidates, nowUtc, dispatches, token);
            int refreshedCount = await RefreshBreakersAsync(db, refreshCandidates, token);
            int trippedCount = 0;
            int tripDispatches = 0;

            foreach (var candidate in tripCandidates)
            {
                token.ThrowIfCancellationRequested();

                DateTime resumeAt = nowUtc.Add(
                    MLConformalBreakerWorker.GetBarDuration(candidate.Timeframe) * candidate.SuspensionBars);

                _logger.LogWarning(
                    "MLConformalBreakerWorker: SUSPENDED {Symbol}/{Timeframe} model {ModelId} — reason={Reason}, run={Run}, coverage={Coverage:P1}, wilsonLower={Lower:P1}, wilsonUpper={Upper:P1}, threshold={Threshold:F4}",
                    candidate.Symbol,
                    candidate.Timeframe,
                    candidate.MLModelId,
                    candidate.Evaluation.TripReason,
                    candidate.Evaluation.ConsecutivePoorCoverageBars,
                    candidate.Evaluation.EmpiricalCoverage,
                    candidate.Evaluation.CoverageLowerBound,
                    candidate.Evaluation.CoverageUpperBound,
                    candidate.CoverageThreshold);

                var existingBreakers = await db.Set<MLConformalBreakerLog>()
                    .Where(b => b.MLModelId == candidate.MLModelId
                                && b.Symbol == candidate.Symbol
                                && b.Timeframe == candidate.Timeframe
                                && b.IsActive
                                && b.ResumeAt > nowUtc)
                    .OrderByDescending(b => b.SuspendedAt)
                    .ThenByDescending(b => b.Id)
                    .ToListAsync(token);

                if (existingBreakers.Count > 0)
                {
                    var existing = existingBreakers[0];
                    ApplyDiagnostics(existing, candidate.Evaluation, candidate.TargetCoverage, candidate.CoverageThreshold);
                    existing.SuspensionBars = candidate.SuspensionBars;
                    existing.SuspendedAt = nowUtc;
                    existing.ResumeAt = resumeAt;

                    foreach (var duplicate in existingBreakers.Skip(1))
                    {
                        duplicate.IsActive = false;
                        duplicateRepairCount++;
                    }
                }
                else
                {
                    var breaker = new MLConformalBreakerLog
                    {
                        MLModelId = candidate.MLModelId,
                        Symbol = candidate.Symbol,
                        Timeframe = candidate.Timeframe,
                        SuspensionBars = candidate.SuspensionBars,
                        SuspendedAt = nowUtc,
                        ResumeAt = resumeAt,
                        IsActive = true
                    };
                    ApplyDiagnostics(breaker, candidate.Evaluation, candidate.TargetCoverage, candidate.CoverageThreshold);
                    await db.Set<MLConformalBreakerLog>().AddAsync(breaker, token);
                }

                var writeModel = await db.Set<MLModel>()
                    .FirstOrDefaultAsync(m => m.Id == candidate.MLModelId, token);
                if (writeModel is not null)
                    writeModel.IsSuppressed = true;

                _metrics.MLConformalBreakerTrips.Add(
                    1,
                    new("reason", candidate.Evaluation.TripReason.ToString()),
                    new("symbol", candidate.Symbol),
                    new("timeframe", candidate.Timeframe.ToString()));

                var (alert, shouldDispatch) = await UpsertTripAlertAsync(db, candidate, resumeAt, nowUtc, token);
                if (shouldDispatch)
                {
                    dispatches.Add(new BreakerAlertDispatch(
                        alert,
                        $"ML conformal breaker suppressed model {candidate.MLModelId} on {candidate.Symbol}/{candidate.Timeframe}: " +
                        $"reason={candidate.Evaluation.TripReason}, coverage={candidate.Evaluation.EmpiricalCoverage:P1}, " +
                        $"target={candidate.TargetCoverage:P1}, resumeAt={resumeAt:O}.",
                        BreakerAlertDispatchKind.Trip));
                    tripDispatches++;
                }

                trippedCount++;
            }

            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);

            int activeBreakers = await db.Set<MLConformalBreakerLog>()
                .CountAsync(b => b.IsActive && !b.IsDeleted, token);

            if (duplicateRepairCount > 0)
                _metrics.MLConformalBreakerDuplicateRepairs.Add(duplicateRepairCount);

            result = new BreakerStateResult(
                expiredCount,
                recoveredCount,
                refreshedCount,
                trippedCount,
                duplicateRepairCount,
                tripDispatches,
                activeBreakers,
                dispatches);
        }, ct);

        return result;
    }

    private static async Task<int> DeactivateDuplicateActiveBreakersAsync(DbContext db, CancellationToken token)
    {
        var activeBreakers = await db.Set<MLConformalBreakerLog>()
            .Where(b => b.IsActive && !b.IsDeleted)
            .OrderBy(b => b.MLModelId)
            .ThenBy(b => b.Symbol)
            .ThenBy(b => b.Timeframe)
            .ThenByDescending(b => b.SuspendedAt)
            .ThenByDescending(b => b.Id)
            .ToListAsync(token);

        int deactivated = 0;
        foreach (var duplicate in activeBreakers
                     .GroupBy(b => new { b.MLModelId, b.Symbol, b.Timeframe })
                     .SelectMany(g => g.Skip(1)))
        {
            duplicate.IsActive = false;
            deactivated++;
        }

        return deactivated;
    }

    private async Task<int> ClearExpiredBreakersAsync(
        DbContext db,
        DateTime nowUtc,
        List<BreakerAlertDispatch> dispatches,
        CancellationToken token)
    {
        var expiredBreakers = await db.Set<MLConformalBreakerLog>()
            .Where(b => b.IsActive && b.ResumeAt <= nowUtc)
            .ToListAsync(token);

        foreach (var breaker in expiredBreakers)
        {
            token.ThrowIfCancellationRequested();
            breaker.IsActive = false;

            var suppressed = await db.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == breaker.MLModelId, token);
            if (suppressed is not null &&
                await MLSuppressionStateHelper.CanLiftSuppressionAsync(
                    db,
                    suppressed,
                    token,
                    ignoreConformalBreakerId: breaker.Id))
            {
                suppressed.IsSuppressed = false;
            }

            await ResolveActiveAlertAsync(db, breaker.MLModelId, breaker.Symbol, breaker.Timeframe, nowUtc, dispatches, token);

            _logger.LogInformation(
                "MLConformalBreakerWorker: RESUMED {Symbol}/{Timeframe} — breaker expired.",
                breaker.Symbol,
                breaker.Timeframe);
        }

        return expiredBreakers.Count;
    }

    private async Task<int> RecoverBreakersAsync(
        DbContext db,
        IReadOnlyCollection<BreakerRecoveryCandidate> recoveryCandidates,
        DateTime nowUtc,
        List<BreakerAlertDispatch> dispatches,
        CancellationToken token)
    {
        int recoveredCount = 0;
        foreach (var candidate in recoveryCandidates)
        {
            token.ThrowIfCancellationRequested();

            var activeBreakers = await db.Set<MLConformalBreakerLog>()
                .Where(b => b.Id == candidate.BreakerId && b.IsActive)
                .ToListAsync(token);
            activeBreakers.AddRange(await db.Set<MLConformalBreakerLog>()
                .Where(b => b.MLModelId == candidate.MLModelId
                            && b.Symbol == candidate.Symbol
                            && b.Timeframe == candidate.Timeframe
                            && b.IsActive
                            && b.Id != candidate.BreakerId)
                .ToListAsync(token));

            if (activeBreakers.Count == 0)
                continue;

            var breaker = activeBreakers
                .OrderByDescending(b => b.SuspendedAt)
                .ThenByDescending(b => b.Id)
                .First();

            foreach (var activeBreaker in activeBreakers)
                activeBreaker.IsActive = false;

            ApplyDiagnostics(breaker, candidate.Evaluation, breaker.TargetCoverage, breaker.CoverageThreshold);
            recoveredCount++;

            var suppressed = await db.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == candidate.MLModelId, token);
            if (suppressed is not null &&
                await MLSuppressionStateHelper.CanLiftSuppressionAsync(
                    db,
                    suppressed,
                    token,
                    ignoreConformalBreakerIds: activeBreakers.Select(b => b.Id).ToArray()))
            {
                suppressed.IsSuppressed = false;
            }

            await ResolveActiveAlertAsync(db, candidate.MLModelId, candidate.Symbol, candidate.Timeframe, nowUtc, dispatches, token);

            _metrics.MLConformalBreakerRecoveries.Add(
                1,
                new("symbol", candidate.Symbol),
                new("timeframe", candidate.Timeframe.ToString()));
            _logger.LogInformation(
                "MLConformalBreakerWorker: RECOVERED {Symbol}/{Timeframe} model {ModelId} — coverage={Coverage:P1}, target floor restored.",
                candidate.Symbol,
                candidate.Timeframe,
                candidate.MLModelId,
                candidate.Evaluation.EmpiricalCoverage);
        }

        return recoveredCount;
    }

    private async Task<int> RefreshBreakersAsync(
        DbContext db,
        IReadOnlyCollection<BreakerRefreshCandidate> refreshCandidates,
        CancellationToken token)
    {
        int refreshedCount = 0;
        foreach (var candidate in refreshCandidates)
        {
            token.ThrowIfCancellationRequested();

            var activeBreakers = await db.Set<MLConformalBreakerLog>()
                .Where(b => b.Id == candidate.BreakerId && b.IsActive)
                .ToListAsync(token);
            activeBreakers.AddRange(await db.Set<MLConformalBreakerLog>()
                .Where(b => b.MLModelId == candidate.MLModelId
                            && b.Symbol == candidate.Symbol
                            && b.Timeframe == candidate.Timeframe
                            && b.IsActive
                            && b.Id != candidate.BreakerId)
                .ToListAsync(token));

            if (activeBreakers.Count == 0)
                continue;

            var breaker = activeBreakers
                .OrderByDescending(b => b.SuspendedAt)
                .ThenByDescending(b => b.Id)
                .First();
            ApplyDiagnostics(breaker, candidate.Evaluation, candidate.TargetCoverage, candidate.CoverageThreshold);

            foreach (var duplicate in activeBreakers.Where(b => b.Id != breaker.Id))
                duplicate.IsActive = false;

            await RefreshActiveAlertPayloadAsync(db, candidate, breaker.ResumeAt, token);

            refreshedCount++;
            _metrics.MLConformalBreakerRefreshes.Add(
                1,
                new("reason", candidate.Evaluation.TripReason.ToString()),
                new("symbol", candidate.Symbol),
                new("timeframe", candidate.Timeframe.ToString()));
            _logger.LogInformation(
                "MLConformalBreakerWorker: REFRESHED active breaker {Symbol}/{Timeframe} model {ModelId} — reason={Reason}, coverage={Coverage:P1}; suspension window unchanged.",
                candidate.Symbol,
                candidate.Timeframe,
                candidate.MLModelId,
                candidate.Evaluation.TripReason,
                candidate.Evaluation.EmpiricalCoverage);
        }

        return refreshedCount;
    }

    private static void ApplyDiagnostics(
        MLConformalBreakerLog breaker,
        ConformalCoverageEvaluation evaluation,
        double targetCoverage,
        double coverageThreshold)
    {
        breaker.ConsecutivePoorCoverageBars = evaluation.ConsecutivePoorCoverageBars;
        breaker.SampleCount = evaluation.SampleCount;
        breaker.CoveredCount = evaluation.CoveredCount;
        breaker.FreshSampleCount = evaluation.SampleCount;
        breaker.EmpiricalCoverage = evaluation.EmpiricalCoverage;
        breaker.TargetCoverage = targetCoverage;
        breaker.CoverageThreshold = coverageThreshold;
        breaker.TripReason = evaluation.TripReason;
        breaker.CoverageLowerBound = evaluation.CoverageLowerBound;
        breaker.CoverageUpperBound = evaluation.CoverageUpperBound;
        breaker.CoveragePValue = evaluation.CoveragePValue;
        breaker.LastEvaluatedOutcomeAt = evaluation.LastEvaluatedOutcomeAt;
    }

    private async Task<(Alert Alert, bool ShouldDispatch)> UpsertTripAlertAsync(
        DbContext db,
        BreakerTripCandidate candidate,
        DateTime resumeAt,
        DateTime nowUtc,
        CancellationToken token)
    {
        string deduplicationKey = BuildDeduplicationKey(candidate.MLModelId, candidate.Symbol, candidate.Timeframe);

        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(a => !a.IsDeleted
                                   && a.IsActive
                                   && a.DeduplicationKey == deduplicationKey, token);

        bool shouldDispatch = true;
        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                DeduplicationKey = deduplicationKey,
                IsActive = true
            };
            db.Set<Alert>().Add(alert);
        }
        else
        {
            alert.AlertType = AlertType.MLModelDegraded;
            if (alert.LastTriggeredAt.HasValue &&
                nowUtc - NormalizeUtc(alert.LastTriggeredAt.Value) < TimeSpan.FromSeconds(AlertCooldownSeconds))
            {
                shouldDispatch = false;
            }
        }

        alert.Symbol = candidate.Symbol;
        alert.Severity = AlertSeverity.High;
        alert.CooldownSeconds = AlertCooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = JsonSerializer.Serialize(BuildAlertPayload(
            candidate.MLModelId,
            candidate.Timeframe,
            candidate.Evaluation,
            candidate.TargetCoverage,
            resumeAt));

        return (alert, shouldDispatch);
    }

    private async Task RefreshActiveAlertPayloadAsync(
        DbContext db,
        BreakerRefreshCandidate candidate,
        DateTime resumeAt,
        CancellationToken token)
    {
        string deduplicationKey = BuildDeduplicationKey(candidate.MLModelId, candidate.Symbol, candidate.Timeframe);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(a => !a.IsDeleted
                                   && a.IsActive
                                   && a.DeduplicationKey == deduplicationKey, token);

        if (alert is null)
            return;

        alert.Symbol = candidate.Symbol;
        alert.Severity = AlertSeverity.High;
        alert.CooldownSeconds = AlertCooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = JsonSerializer.Serialize(BuildAlertPayload(
            candidate.MLModelId,
            candidate.Timeframe,
            candidate.Evaluation,
            candidate.TargetCoverage,
            resumeAt));
    }

    private static MLConformalBreakerAlertPayload BuildAlertPayload(
        long modelId,
        Timeframe timeframe,
        ConformalCoverageEvaluation evaluation,
        double targetCoverage,
        DateTime resumeAt)
    {
        var worst = evaluation.WorstRegime();
        return new MLConformalBreakerAlertPayload(
            modelId,
            timeframe.ToString(),
            evaluation.TripReason.ToString(),
            evaluation.EmpiricalCoverage,
            targetCoverage,
            evaluation.CoverageLowerBound,
            evaluation.CoverageUpperBound,
            evaluation.CoveragePValue,
            evaluation.LastEvaluatedOutcomeAt,
            resumeAt,
            WorstRegime: worst?.Regime.ToString(),
            WorstRegimeCoverage: worst?.Breakdown.EmpiricalCoverage,
            WorstRegimeSampleCount: worst?.Breakdown.SampleCount);
    }

    private async Task ResolveActiveAlertAsync(
        DbContext db,
        long modelId,
        string symbol,
        Timeframe timeframe,
        DateTime nowUtc,
        List<BreakerAlertDispatch> dispatches,
        CancellationToken token)
    {
        string deduplicationKey = BuildDeduplicationKey(modelId, symbol, timeframe);
        var activeAlerts = await db.Set<Alert>()
            .Where(a => !a.IsDeleted
                     && a.IsActive
                     && a.DeduplicationKey == deduplicationKey)
            .ToListAsync(token);

        foreach (var alert in activeAlerts)
        {
            alert.IsActive = false;

            if (alert.LastTriggeredAt.HasValue)
            {
                dispatches.Add(new BreakerAlertDispatch(
                    alert,
                    string.Empty,
                    BreakerAlertDispatchKind.Resolve));
            }
            else
            {
                alert.AutoResolvedAt ??= nowUtc;
            }
        }
    }

    private static string BuildDeduplicationKey(long modelId, string symbol, Timeframe timeframe)
        => $"MLConformalBreaker:{modelId}:{symbol}:{timeframe}";

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
