using System.Text.Json;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static LascodiaTradingEngine.Application.StrategyGeneration.StrategyGenerationHelpers;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationCandidatePersistenceService))]
internal sealed class StrategyGenerationCandidatePersistenceService : IStrategyGenerationCandidatePersistenceService
{
    private sealed record RecoveryAfterBatchFailureResult(
        List<ScreeningOutcome> RecoveredCandidates,
        List<StrategyGenerationFailureRecord> Failures);

    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly TradingMetrics _metrics;
    private readonly IStrategyCandidateSelectionPolicy _candidateSelectionPolicy;
    private readonly IStrategyValidationPriorityResolver _priorityResolver;
    private readonly IStrategyGenerationFailureStore _failureStore;
    private readonly IStrategyGenerationConfigProvider _configProvider;
    private readonly IStrategyGenerationArtifactReplayService _artifactReplayService;
    private readonly IValidationRunFactory _validationRunFactory;
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationCandidatePersistenceService(
        ILogger<StrategyGenerationWorker> logger,
        TradingMetrics metrics,
        IStrategyCandidateSelectionPolicy candidateSelectionPolicy,
        IStrategyValidationPriorityResolver priorityResolver,
        IStrategyGenerationFailureStore failureStore,
        IStrategyGenerationConfigProvider configProvider,
        IStrategyGenerationArtifactReplayService artifactReplayService,
        IValidationRunFactory validationRunFactory,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _metrics = metrics;
        _candidateSelectionPolicy = candidateSelectionPolicy;
        _priorityResolver = priorityResolver;
        _failureStore = failureStore;
        _configProvider = configProvider;
        _artifactReplayService = artifactReplayService;
        _validationRunFactory = validationRunFactory;
        _timeProvider = timeProvider;
    }

    public async Task<PersistCandidatesResult> PersistCandidatesAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        List<ScreeningOutcome> candidates,
        GenerationConfig config,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
            return new PersistCandidatesResult(0, 0);

        var db = readCtx.GetDbContext();
        var writeDb = writeCtx.GetDbContext();

        var candidateSymbols = candidates.Select(c => c.Strategy.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var candidateTypes = candidates.Select(c => c.Strategy.StrategyType).Distinct().ToList();
        var candidateTimeframes = candidates.Select(c => c.Strategy.Timeframe).Distinct().ToList();

        var concurrentlyCreated = await db.Set<Strategy>()
            .Where(s => !s.IsDeleted
                     && candidateSymbols.Contains(s.Symbol)
                     && candidateTypes.Contains(s.StrategyType)
                     && candidateTimeframes.Contains(s.Timeframe))
            .Select(s => new { s.StrategyType, s.Symbol, s.Timeframe })
            .ToListAsync(ct);
        var freshExistingSet = new HashSet<CandidateCombo>(
            concurrentlyCreated.Select(s => new CandidateCombo(s.StrategyType, s.Symbol, s.Timeframe)));

        var confirmed = candidates
            .Where(c => !freshExistingSet.Contains(_candidateSelectionPolicy.GetCombo(c)))
            .ToList();
        if (confirmed.Count == 0)
            return new PersistCandidatesResult(0, 0);

        var fastTrackSettings = await LoadFastTrackSettingsAsync(db, ct);

        var eliteCombos = new HashSet<CandidateCombo>();
        var walkForwardSplits = config.WalkForwardSplitPercentages;
        if (fastTrackSettings.Enabled)
        {
            foreach (var candidate in confirmed)
            {
                if (_priorityResolver.IsEliteFastTrackCandidate(
                        candidate,
                        config,
                        fastTrackSettings.ThresholdMultiplier,
                        fastTrackSettings.MinR2,
                        fastTrackSettings.MaxMonteCarloPValue,
                        walkForwardSplits))
                {
                    eliteCombos.Add(_candidateSelectionPolicy.GetCombo(candidate));
                }
            }
        }

        confirmed = confirmed
            .Select(c => ApplyValidationPriority(c, eliteCombos, fastTrackSettings.PriorityBoost))
            .ToList();

        try
        {
            await using var tx = await TryBeginTransactionAsync(writeDb, ct);
            foreach (var candidate in confirmed)
                writeDb.Set<Strategy>().Add(candidate.Strategy);
            await writeCtx.SaveChangesAsync(ct);

            var confirmedStrategyIds = confirmed.Select(c => c.Strategy.Id).ToList();
            var queuedSet = confirmedStrategyIds.Count == 0
                ? new HashSet<long>()
                : new HashSet<long>(await db.Set<BacktestRun>()
                    .Where(r => r.Status == RunStatus.Queued
                             && !r.IsDeleted
                             && confirmedStrategyIds.Contains(r.StrategyId))
                    .Select(r => r.StrategyId)
                    .ToListAsync(ct));

            foreach (var candidate in confirmed)
            {
                if (queuedSet.Contains(candidate.Strategy.Id))
                    continue;

                writeDb.Set<BacktestRun>().Add(await BuildQueuedBacktestRunAsync(writeDb, candidate, ct));
            }

            await writeCtx.SaveChangesAsync(ct);
            if (tx != null)
                await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: batch save failed — falling back to individual saves");
            foreach (var candidate in confirmed)
            {
                bool compensated = await TryCompensateUnsafelyPersistedStrategyAsync(writeDb, writeCtx, candidate.Strategy, ct);
                if (!compensated)
                {
                    _logger.LogError(
                        "StrategyGenerationWorker: failed to compensate partially persisted strategy {StrategyName} after batch save failure",
                        candidate.Strategy.Name);
                    _metrics.WorkerErrors.Add(1,
                        new KeyValuePair<string, object?>("worker", "StrategyGenerationWorker"),
                        new KeyValuePair<string, object?>("operation", "compensation_cleanup"));
                    _metrics.StrategyGenCompensationCleanupFailures.Add(1);
                }
            }

            DetachTrackedEntries(writeDb);

            var recheckExisting = await db.Set<Strategy>()
                .Where(s => !s.IsDeleted
                         && candidateSymbols.Contains(s.Symbol)
                         && candidateTypes.Contains(s.StrategyType)
                         && candidateTimeframes.Contains(s.Timeframe))
                .Select(s => new { s.StrategyType, s.Symbol, s.Timeframe })
                .ToListAsync(ct);
            var recheckSet = new HashSet<CandidateCombo>(
                recheckExisting.Select(s => new CandidateCombo(s.StrategyType, s.Symbol, s.Timeframe)));
            var recoveredCandidates = confirmed
                .Where(c => recheckSet.Contains(_candidateSelectionPolicy.GetCombo(c)))
                .ToList();
            confirmed = confirmed
                .Where(c => !recheckSet.Contains(_candidateSelectionPolicy.GetCombo(c)))
                .ToList();

            var recovery = await RecoverPersistedCandidatesAfterBatchFailureAsync(
                db,
                writeDb,
                writeCtx,
                recoveredCandidates,
                ct);

            var saved = new List<ScreeningOutcome>(recovery.RecoveredCandidates);
            var failures = new List<StrategyGenerationFailureRecord>(recovery.Failures);
            foreach (var candidate in confirmed)
            {
                try
                {
                    await using var tx = await TryBeginTransactionAsync(writeDb, ct);
                    writeDb.Set<Strategy>().Add(candidate.Strategy);
                    await writeCtx.SaveChangesAsync(ct);

                    bool queuedAlreadyExists = await db.Set<BacktestRun>()
                        .AnyAsync(r => r.StrategyId == candidate.Strategy.Id
                                    && r.Status == RunStatus.Queued
                                    && !r.IsDeleted, ct);
                    if (!queuedAlreadyExists)
                        writeDb.Set<BacktestRun>().Add(await BuildQueuedBacktestRunAsync(writeDb, candidate, ct));

                    await writeCtx.SaveChangesAsync(ct);
                    if (tx != null)
                        await tx.CommitAsync(ct);
                    saved.Add(candidate);
                }
                catch (DbUpdateException dbEx) when (StrategyGenerationDbExceptionClassifier.IsActiveStrategyDuplicateViolation(dbEx))
                {
                    DetachDirtyEntries(writeDb);

                    bool duplicateExists = await db.Set<Strategy>()
                        .AnyAsync(s => !s.IsDeleted
                                    && s.StrategyType == candidate.Strategy.StrategyType
                                    && s.Symbol == candidate.Strategy.Symbol
                                    && s.Timeframe == candidate.Strategy.Timeframe, ct);
                    if (duplicateExists)
                    {
                        _logger.LogInformation(
                            "StrategyGenerationWorker: duplicate strategy combo {Type}/{Symbol}/{Tf} was persisted concurrently elsewhere; skipping local duplicate",
                            candidate.Strategy.StrategyType,
                            candidate.Strategy.Symbol,
                            candidate.Strategy.Timeframe);
                        continue;
                    }

                    failures.Add(BuildFailureRecord(
                        candidate,
                        "individual_persist",
                        "duplicate_strategy_violation_without_visible_row",
                        JsonSerializer.Serialize(new { error = dbEx.Message })));
                }
                catch (DbUpdateException dbEx) when (StrategyGenerationDbExceptionClassifier.IsActiveValidationQueueDuplicateViolation(dbEx))
                {
                    DetachDirtyEntries(writeDb);

                    string? queueKey = _priorityResolver.BuildInitialBacktestQueueKey(candidate);
                    bool queueAlreadyExists = await db.Set<BacktestRun>()
                        .AnyAsync(r => !r.IsDeleted
                                    && r.ValidationQueueKey == queueKey
                                    && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);
                    if (queueAlreadyExists)
                    {
                        _logger.LogInformation(
                            "StrategyGenerationWorker: validation queue key {QueueKey} was claimed concurrently; treating candidate {StrategyId} as persisted",
                            queueKey,
                            candidate.Strategy.Id);
                        saved.Add(candidate);
                        continue;
                    }

                    failures.Add(BuildFailureRecord(
                        candidate,
                        "individual_persist",
                        "duplicate_validation_queue_violation_without_visible_row",
                        JsonSerializer.Serialize(new { error = dbEx.Message, queueKey })));
                }
                catch (Exception innerEx)
                {
                    _logger.LogWarning(innerEx, "StrategyGenerationWorker: save failed for {Name}", candidate.Strategy.Name);
                    bool compensated = await TryCompensateUnsafelyPersistedStrategyAsync(writeDb, writeCtx, candidate.Strategy, ct);
                    if (!compensated)
                    {
                        _logger.LogError(
                            "StrategyGenerationWorker: failed to compensate partially persisted strategy {StrategyName} after individual save failure",
                            candidate.Strategy.Name);
                        _metrics.WorkerErrors.Add(1,
                            new KeyValuePair<string, object?>("worker", "StrategyGenerationWorker"),
                            new KeyValuePair<string, object?>("operation", "compensation_cleanup"));
                        _metrics.StrategyGenCompensationCleanupFailures.Add(1);
                    }

                    failures.Add(BuildFailureRecord(
                        candidate,
                        "individual_persist",
                        "persist_candidate_failed",
                        JsonSerializer.Serialize(new { error = innerEx.Message })));
                    DetachDirtyEntries(writeDb);
                }
            }

            if (failures.Count > 0)
            {
                try
                {
                    await _failureStore.RecordFailuresAsync(writeDb, failures, ct);
                    await writeCtx.SaveChangesAsync(ct);
                }
                catch
                {
                    // Best effort.
                }
            }

            confirmed = saved;
        }

        foreach (var candidate in confirmed)
        {
            _metrics.StrategyCandidatesCreated.Add(1,
                new KeyValuePair<string, object?>("strategy_type", candidate.Strategy.StrategyType.ToString()));
        }

        try
        {
            await _failureStore.MarkFailuresResolvedAsync(
                writeDb,
                confirmed
                    .Select(c => c.Strategy.GenerationCandidateId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Cast<string>()
                    .ToArray(),
                ct);
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: failed to mark resolved persistence failures");
        }

        var pendingArtifacts = confirmed
            .Select(c => new StrategyGenerationPendingArtifactRecord(
                c.Strategy.Id,
                c.Strategy.GenerationCandidateId!,
                c.Strategy.GenerationCycleId,
                GenerationCheckpointStore.PendingCandidateState.FromOutcome(c),
                NeedsCreationAudit: true,
                NeedsCreatedEvent: true,
                NeedsAutoPromoteEvent: eliteCombos.Contains(_candidateSelectionPolicy.GetCombo(c))))
            .ToList();

        if (pendingArtifacts.Count > 0)
        {
            try
            {
                await _artifactReplayService.PersistAndDrainPendingPostPersistArtifactsAsync(
                    db,
                    writeCtx,
                    eventService,
                    auditLogger,
                    pendingArtifacts,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "StrategyGenerationWorker: deferred post-persist artifact processing failed; replay will resume next cycle");
            }
        }

        int reservePersistedCount = confirmed.Count(c =>
            string.Equals(c.Metrics?.GenerationSource, "Reserve", StringComparison.OrdinalIgnoreCase));

        return new PersistCandidatesResult(confirmed.Count, reservePersistedCount);
    }

    private async Task<StrategyGenerationFastTrackSettings> LoadFastTrackSettingsAsync(DbContext db, CancellationToken ct)
    {
        try
        {
            return await _configProvider.LoadFastTrackAsync(db, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyGenerationWorker: failed to load fast-track configuration — continuing without acceleration");
            return new StrategyGenerationFastTrackSettings(false, 2.0, 0.90, 0.01, 1_000);
        }
    }

    private async Task<IDbContextTransaction?> TryBeginTransactionAsync(DbContext writeDb, CancellationToken ct)
    {
        try
        {
            var database = writeDb.Database;
            if (database == null)
            {
                _logger.LogDebug(
                    "StrategyGenerationWorker: DbContext has no database facade — falling back to non-atomic persistence");
                return null;
            }

            return await database.BeginTransactionAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            _logger.LogWarning(ex,
                "StrategyGenerationWorker: database transaction unavailable — falling back to non-atomic persistence");
            return null;
        }
    }

    private async Task<bool> TryCompensateUnsafelyPersistedStrategyAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        Strategy strategy,
        CancellationToken ct)
    {
        if (strategy.Id <= 0)
            return true;

        try
        {
            var persisted = await writeDb.Set<Strategy>().FindAsync([strategy.Id], ct);
            if (persisted == null)
                return true;

            StrategyGenerationPruningCoordinator.MarkStrategyAsCompensatedDeletion(persisted);

            var queuedBacktests = await writeDb.Set<BacktestRun>()
                .Where(b => b.StrategyId == strategy.Id
                         && b.Status == RunStatus.Queued
                         && !b.IsDeleted)
                .ToListAsync(ct);
            foreach (var bt in queuedBacktests)
            {
                bt.Status = RunStatus.Failed;
                bt.ErrorMessage = "Strategy was compensation-deleted before backtest could run.";
                bt.CompletedAt = _timeProvider.GetUtcNow().UtcDateTime;
            }

            var queuedWalkForwards = await writeDb.Set<WalkForwardRun>()
                .Where(w => w.StrategyId == strategy.Id
                         && w.Status == RunStatus.Queued
                         && !w.IsDeleted)
                .ToListAsync(ct);
            foreach (var wf in queuedWalkForwards)
            {
                wf.Status = RunStatus.Failed;
                wf.ErrorMessage = "Strategy was compensation-deleted before walk-forward could run.";
                wf.CompletedAt = _timeProvider.GetUtcNow().UtcDateTime;
            }

            await writeCtx.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "StrategyGenerationWorker: compensation cleanup failed for strategy {StrategyId}",
                strategy.Id);
            DetachDirtyEntries(writeDb);
            return false;
        }
    }

    private async Task<RecoveryAfterBatchFailureResult> RecoverPersistedCandidatesAfterBatchFailureAsync(
        DbContext readDb,
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        IReadOnlyCollection<ScreeningOutcome> recoveredCandidates,
        CancellationToken ct)
    {
        if (recoveredCandidates.Count == 0)
            return new RecoveryAfterBatchFailureResult([], []);

        _logger.LogWarning(
            "StrategyGenerationWorker: found {Count} persisted candidates after batch failure; ensuring backtest queue and artifacts are recovered",
            recoveredCandidates.Count);

        var recovered = new List<ScreeningOutcome>();
        var failures = new List<StrategyGenerationFailureRecord>();

        foreach (var candidate in recoveredCandidates)
        {
            var persisted = await readDb.Set<Strategy>()
                .AsNoTracking()
                .Where(s => !s.IsDeleted
                         && s.StrategyType == candidate.Strategy.StrategyType
                         && s.Symbol == candidate.Strategy.Symbol
                         && s.Timeframe == candidate.Strategy.Timeframe
                         && s.Name == candidate.Strategy.Name
                         && s.ParametersJson == candidate.Strategy.ParametersJson)
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Symbol,
                    s.Timeframe,
                    s.StrategyType,
                    s.CreatedAt,
                    s.ScreeningMetricsJson,
                })
                .FirstOrDefaultAsync(ct);

            if (persisted == null)
            {
                _logger.LogInformation(
                    "StrategyGenerationWorker: skipping recovery for {Name}; matching persisted strategy was not found after batch failure",
                    candidate.Strategy.Name);
                continue;
            }

            try
            {
                bool queuedAlreadyExists = await readDb.Set<BacktestRun>()
                    .AnyAsync(r => r.StrategyId == persisted.Id
                                && r.Status == RunStatus.Queued
                                && !r.IsDeleted, ct);

                // Strategy was located by (StrategyType, Symbol, Timeframe, Name, ParametersJson),
                // so those fields already match the in-memory candidate — only DB-assigned fields change.
                candidate.Strategy.Id = persisted.Id;
                candidate.Strategy.Name = persisted.Name;
                candidate.Strategy.CreatedAt = persisted.CreatedAt;
                candidate.Strategy.ScreeningMetricsJson = persisted.ScreeningMetricsJson ?? candidate.Strategy.ScreeningMetricsJson;

                if (!queuedAlreadyExists)
                {
                    writeDb.Set<BacktestRun>().Add(await BuildQueuedBacktestRunAsync(writeDb, candidate, ct));
                    await writeCtx.SaveChangesAsync(ct);
                }

                var recoveredMetrics = ScreeningMetrics.FromJson(candidate.Strategy.ScreeningMetricsJson)
                    ?? candidate.Metrics;
                recovered.Add(candidate with { Metrics = recoveredMetrics });
            }
            catch (DbUpdateException dbEx) when (StrategyGenerationDbExceptionClassifier.IsActiveValidationQueueDuplicateViolation(dbEx))
            {
                string? queueKey = _priorityResolver.BuildInitialBacktestQueueKey(candidate);
                bool queuedAlreadyExists = await readDb.Set<BacktestRun>()
                    .AnyAsync(r => !r.IsDeleted
                                && r.ValidationQueueKey == queueKey
                                && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);
                if (queuedAlreadyExists)
                {
                    var recoveredMetrics = ScreeningMetrics.FromJson(candidate.Strategy.ScreeningMetricsJson)
                        ?? candidate.Metrics;
                    recovered.Add(candidate with { Metrics = recoveredMetrics });
                    DetachDirtyEntries(writeDb);
                    continue;
                }

                failures.Add(BuildFailureRecord(
                    candidate,
                    "batch_recovery",
                    "duplicate_validation_queue_violation_without_visible_row",
                    JsonSerializer.Serialize(new { error = dbEx.Message, queueKey })));
                DetachDirtyEntries(writeDb);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "StrategyGenerationWorker: failed to recover persisted candidate {Name} after batch failure",
                    candidate.Strategy.Name);
                failures.Add(BuildFailureRecord(
                    candidate,
                    "batch_recovery",
                    "recover_persisted_candidate_failed",
                    JsonSerializer.Serialize(new { error = ex.Message })));
                DetachDirtyEntries(writeDb);
            }
        }

        return new RecoveryAfterBatchFailureResult(recovered, failures);
    }

    private ScreeningOutcome ApplyValidationPriority(
        ScreeningOutcome candidate,
        IReadOnlySet<CandidateCombo> eliteCombos,
        int fastTrackPriorityBoost)
    {
        var identity = string.IsNullOrWhiteSpace(candidate.Strategy.GenerationCandidateId)
            ? _candidateSelectionPolicy.BuildIdentity(candidate)
            : new CandidateIdentity(
                candidate.Strategy.GenerationCandidateId!,
                _candidateSelectionPolicy.GetCombo(candidate),
                NormalizeTemplateParameters(candidate.Strategy.ParametersJson));
        var scoreBreakdown = candidate.Metrics?.SelectionScoreBreakdown ?? _candidateSelectionPolicy.Score(candidate);
        bool isElite = eliteCombos.Contains(identity.Combo);
        int priority = _priorityResolver.ResolvePriority(candidate, scoreBreakdown, isElite, fastTrackPriorityBoost);

        candidate.Strategy.GenerationCandidateId ??= identity.CandidateId;
        candidate.Strategy.ValidationPriority = priority;
        var metrics = (candidate.Metrics ?? BuildBaseMetrics(candidate)) with
        {
            CycleId = candidate.Strategy.GenerationCycleId,
            CandidateId = candidate.Strategy.GenerationCandidateId,
            SelectionScore = scoreBreakdown.TotalScore,
            SelectionScoreBreakdown = scoreBreakdown,
            ValidationPriority = priority,
            IsAutoPromoted = isElite,
        };

        candidate.Strategy.ScreeningMetricsJson = metrics.ToJson();
        return candidate with { Metrics = metrics };
    }

    private ScreeningMetrics BuildBaseMetrics(ScreeningOutcome candidate)
        => ScreeningMetrics.FromJson(candidate.Strategy.ScreeningMetricsJson)
           ?? new ScreeningMetrics
           {
               Regime = candidate.Regime.ToString(),
               ObservedRegime = candidate.ObservedRegime.ToString(),
               GenerationSource = candidate.GenerationSource,
               ScreenedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
           };

    private async Task<BacktestRun> BuildQueuedBacktestRunAsync(
        DbContext writeDb,
        ScreeningOutcome candidate,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        return await _validationRunFactory.BuildBacktestRunAsync(
            writeDb,
            new BacktestQueueRequest(
                StrategyId: candidate.Strategy.Id,
                Symbol: candidate.Strategy.Symbol,
                Timeframe: candidate.Strategy.Timeframe,
                FromDate: nowUtc.AddDays(-365),
                ToDate: nowUtc,
                InitialBalance: 10_000m,
                QueueSource: ValidationRunQueueSources.StrategyGenerationInitial,
                Priority: candidate.Strategy.ValidationPriority,
                ParametersSnapshotJson: candidate.Strategy.ParametersJson,
                ValidationQueueKey: _priorityResolver.BuildInitialBacktestQueueKey(candidate)),
            ct);
    }

    private StrategyGenerationFailureRecord BuildFailureRecord(
        ScreeningOutcome candidate,
        string failureStage,
        string failureReason,
        string? detailsJson = null)
    {
        var identity = string.IsNullOrWhiteSpace(candidate.Strategy.GenerationCandidateId)
            ? _candidateSelectionPolicy.BuildIdentity(candidate)
            : new CandidateIdentity(
                candidate.Strategy.GenerationCandidateId!,
                _candidateSelectionPolicy.GetCombo(candidate),
                NormalizeTemplateParameters(candidate.Strategy.ParametersJson));

        return new StrategyGenerationFailureRecord(
            identity.CandidateId,
            candidate.Strategy.GenerationCycleId,
            identity.CandidateId,
            candidate.Strategy.StrategyType,
            candidate.Strategy.Symbol,
            candidate.Strategy.Timeframe,
            identity.NormalizedParametersJson,
            failureStage,
            failureReason,
            detailsJson);
    }

    private static void DetachTrackedEntries(DbContext writeDb)
    {
        var trackedEntries = writeDb.ChangeTracker?.Entries()?.ToList() ?? [];
        foreach (var entry in trackedEntries)
            entry.State = EntityState.Detached;
    }

    private static void DetachDirtyEntries(DbContext writeDb)
    {
        var dirtyEntries = writeDb.ChangeTracker?.Entries()
            ?.Where(e => e.State != EntityState.Unchanged)
            .ToList() ?? [];
        foreach (var entry in dirtyEntries)
            entry.State = EntityState.Detached;
    }
}
