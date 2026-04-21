using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

public sealed class MLConformalBreakerStateStore : IMLConformalBreakerStateStore
{
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

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var alerts = new List<BreakerAlertDispatch>();

            int expiredCount = await ClearExpiredBreakersAsync(db, now, token);
            int duplicateRepairCount = await DeactivateDuplicateActiveBreakersAsync(db, token);
            int recoveredCount = await RecoverBreakersAsync(db, recoveryCandidates, token);
            int refreshedCount = await RefreshBreakersAsync(db, refreshCandidates, token);
            int trippedCount = 0;

            foreach (var candidate in tripCandidates)
            {
                token.ThrowIfCancellationRequested();
                var resumeAt = now.Add(MLConformalBreakerWorker.GetBarDuration(candidate.Timeframe) * candidate.SuspensionBars);

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
                                && b.IsActive)
                    .OrderByDescending(b => b.SuspendedAt)
                    .ThenByDescending(b => b.Id)
                    .ToListAsync(token);

                if (existingBreakers.Count > 0)
                {
                    var existing = existingBreakers[0];
                    ApplyDiagnostics(existing, candidate.Evaluation, candidate.TargetCoverage, candidate.CoverageThreshold);
                    existing.SuspensionBars = candidate.SuspensionBars;
                    existing.SuspendedAt    = now;
                    existing.ResumeAt       = resumeAt;

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
                        MLModelId                   = candidate.MLModelId,
                        Symbol                      = candidate.Symbol,
                        Timeframe                   = candidate.Timeframe,
                        SuspensionBars              = candidate.SuspensionBars,
                        SuspendedAt                 = now,
                        ResumeAt                    = resumeAt,
                        IsActive                    = true
                    };
                    ApplyDiagnostics(breaker, candidate.Evaluation, candidate.TargetCoverage, candidate.CoverageThreshold);
                    await db.Set<MLConformalBreakerLog>().AddAsync(breaker, token);
                }

                var writeModel = await db.Set<MLModel>()
                    .FirstOrDefaultAsync(m => m.Id == candidate.MLModelId, token);
                if (writeModel is not null)
                    writeModel.IsSuppressed = true;

                _metrics.MLConformalBreakerTrips.Add(1,
                    new("reason", candidate.Evaluation.TripReason.ToString()),
                    new("symbol", candidate.Symbol),
                    new("timeframe", candidate.Timeframe.ToString()));

                var alert = BuildTripAlert(candidate, resumeAt);
                db.Set<Alert>().Add(alert);
                alerts.Add(new BreakerAlertDispatch(
                    alert,
                    $"ML conformal breaker suppressed model {candidate.MLModelId} on {candidate.Symbol}/{candidate.Timeframe}: " +
                    $"reason={candidate.Evaluation.TripReason}, coverage={candidate.Evaluation.EmpiricalCoverage:P1}, " +
                    $"target={candidate.TargetCoverage:P1}, resumeAt={resumeAt:O}."));
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
                alerts.Count,
                activeBreakers,
                alerts);
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

    private async Task<int> ClearExpiredBreakersAsync(DbContext db, DateTime now, CancellationToken token)
    {
        var expiredBreakers = await db.Set<MLConformalBreakerLog>()
            .Where(b => b.IsActive && b.ResumeAt <= now)
            .ToListAsync(token);

        foreach (var breaker in expiredBreakers)
        {
            token.ThrowIfCancellationRequested();
            breaker.IsActive = false;

            var suppressed = await db.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == breaker.MLModelId, token);
            if (suppressed is not null &&
                await MLSuppressionStateHelper.CanLiftSuppressionAsync(
                    db, suppressed, token, ignoreConformalBreakerId: breaker.Id))
                suppressed.IsSuppressed = false;

            _logger.LogInformation(
                "MLConformalBreakerWorker: RESUMED {Symbol}/{Timeframe} — breaker expired.",
                breaker.Symbol, breaker.Timeframe);
        }

        return expiredBreakers.Count;
    }

    private async Task<int> RecoverBreakersAsync(
        DbContext db,
        IReadOnlyCollection<BreakerRecoveryCandidate> recoveryCandidates,
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
            {
                activeBreaker.IsActive = false;
            }
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
                suppressed.IsSuppressed = false;

            _metrics.MLConformalBreakerRecoveries.Add(1,
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
            {
                duplicate.IsActive = false;
            }
            refreshedCount++;

            _metrics.MLConformalBreakerRefreshes.Add(1,
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
        breaker.SampleCount                 = evaluation.SampleCount;
        breaker.CoveredCount                = evaluation.CoveredCount;
        breaker.FreshSampleCount            = evaluation.SampleCount;
        breaker.EmpiricalCoverage           = evaluation.EmpiricalCoverage;
        breaker.TargetCoverage              = targetCoverage;
        breaker.CoverageThreshold           = coverageThreshold;
        breaker.TripReason                  = evaluation.TripReason;
        breaker.CoverageLowerBound          = evaluation.CoverageLowerBound;
        breaker.CoverageUpperBound          = evaluation.CoverageUpperBound;
        breaker.CoveragePValue              = evaluation.CoveragePValue;
        breaker.LastEvaluatedOutcomeAt      = evaluation.LastEvaluatedOutcomeAt;
    }

    private static Alert BuildTripAlert(BreakerTripCandidate candidate, DateTime resumeAt)
    {
        var payload = new MLConformalBreakerAlertPayload(
            candidate.MLModelId,
            candidate.Timeframe.ToString(),
            candidate.Evaluation.TripReason.ToString(),
            candidate.Evaluation.EmpiricalCoverage,
            candidate.TargetCoverage,
            candidate.Evaluation.CoverageLowerBound,
            candidate.Evaluation.CoverageUpperBound,
            candidate.Evaluation.CoveragePValue,
            candidate.Evaluation.LastEvaluatedOutcomeAt,
            resumeAt);

        return new Alert
        {
            AlertType = AlertType.MLModelDegraded,
            Symbol = candidate.Symbol,
            Severity = AlertSeverity.High,
            DeduplicationKey = $"MLConformalBreaker:{candidate.MLModelId}:{candidate.Symbol}:{candidate.Timeframe}",
            CooldownSeconds = 3600,
            ConditionJson = JsonSerializer.Serialize(payload)
        };
    }
}
