using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationArtifactReplayService))]
internal sealed class StrategyGenerationArtifactReplayService : IStrategyGenerationArtifactReplayService
{
    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStrategyGenerationPendingArtifactStore _pendingArtifactStore;
    private readonly IStrategyGenerationEventFactory _eventFactory;
    private readonly IStrategyGenerationHealthStore _healthStore;
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationArtifactReplayService(
        ILogger<StrategyGenerationWorker> logger,
        IServiceScopeFactory scopeFactory,
        IStrategyGenerationPendingArtifactStore pendingArtifactStore,
        IStrategyGenerationEventFactory eventFactory,
        IStrategyGenerationHealthStore healthStore,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _pendingArtifactStore = pendingArtifactStore;
        _eventFactory = eventFactory;
        _healthStore = healthStore;
        _timeProvider = timeProvider;
    }

    public async Task PersistAndDrainPendingPostPersistArtifactsAsync(
        DbContext readDb,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        IReadOnlyCollection<StrategyGenerationPendingArtifactRecord> pendingArtifacts,
        CancellationToken ct)
    {
        UpdatePendingArtifactBacklog(pendingArtifacts, 0);
        await PersistPendingPostPersistArtifactsAsync(writeCtx, pendingArtifacts, ct);
        int remainingCount = await DrainPendingPostPersistArtifactsAsync(
            readDb,
            writeCtx,
            eventService,
            auditLogger,
            pendingArtifacts,
            ct);
        RecordReplayOutcome(pendingArtifacts.Count, 0, remainingCount);
    }

    public async Task ReplayPendingPostPersistArtifactsAsync(
        DbContext readDb,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        CancellationToken ct)
    {
        var pendingLoad = await _pendingArtifactStore.LoadPendingArtifactsAsync(readDb, ct);
        UpdatePendingArtifactBacklog(pendingLoad.PendingArtifacts, pendingLoad.CorruptArtifacts.Count);
        if (pendingLoad.CorruptArtifacts.Count > 0)
        {
            _logger.LogWarning(
                "StrategyGenerationWorker: quarantining {Count} corrupt deferred post-persist artifact rows",
                pendingLoad.CorruptArtifacts.Count);
            await _pendingArtifactStore.QuarantineCorruptArtifactsAsync(
                writeCtx.GetDbContext(),
                pendingLoad.CorruptArtifacts,
                ct);
            await writeCtx.SaveChangesAsync(ct);
            RecordCorruptArtifactQuarantine(pendingLoad.CorruptArtifacts);
        }

        if (pendingLoad.PendingArtifacts.Count == 0)
        {
            RecordReplayOutcome(0, pendingLoad.CorruptArtifacts.Count, 0);
            return;
        }

        _logger.LogWarning(
            "StrategyGenerationWorker: replaying {Count} deferred post-persist artifacts from a prior partial failure",
            pendingLoad.PendingArtifacts.Count);

        int remainingCount = await DrainPendingPostPersistArtifactsAsync(
            readDb,
            writeCtx,
            eventService,
            auditLogger,
            pendingLoad.PendingArtifacts,
            ct);
        RecordReplayOutcome(
            pendingLoad.PendingArtifacts.Count,
            pendingLoad.CorruptArtifacts.Count,
            remainingCount);
    }

    private async Task PersistPendingPostPersistArtifactsAsync(
        IWriteApplicationDbContext writeCtx,
        IReadOnlyCollection<StrategyGenerationPendingArtifactRecord> pendingArtifacts,
        CancellationToken ct)
    {
        await _pendingArtifactStore.ReplacePendingArtifactsAsync(writeCtx.GetDbContext(), pendingArtifacts, ct);
        await writeCtx.SaveChangesAsync(ct);
        UpdatePendingArtifactBacklog(pendingArtifacts, 0);
    }

    private async Task<int> DrainPendingPostPersistArtifactsAsync(
        DbContext readDb,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        IReadOnlyCollection<StrategyGenerationPendingArtifactRecord> pendingArtifacts,
        CancellationToken ct)
    {
        var remaining = pendingArtifacts.ToList();
        if (remaining.Count == 0)
            return 0;

        const int maxArtifactRetries = 10;

        for (int i = 0; i < remaining.Count; i++)
        {
            var pending = remaining[i];

            if (pending.AttemptCount >= maxArtifactRetries)
            {
                var quarantinedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                string quarantineReason = Truncate(
                    $"Artifact for strategy {pending.StrategyId} exhausted {maxArtifactRetries} retries"
                    + (string.IsNullOrWhiteSpace(pending.LastErrorMessage)
                        ? string.Empty
                        : $": {pending.LastErrorMessage}"));
                _logger.LogError(
                    "StrategyGenerationWorker: artifact for strategy {StrategyId} exhausted {Max} retries — quarantining",
                    pending.StrategyId, maxArtifactRetries);

                var quarantined = pending with
                {
                    NeedsCreationAudit = false,
                    NeedsCreatedEvent = false,
                    NeedsAutoPromoteEvent = false,
                    LastAttemptAtUtc = quarantinedAtUtc,
                    LastErrorMessage = quarantineReason,
                    QuarantinedAtUtc = quarantinedAtUtc,
                    TerminalFailureReason = quarantineReason,
                };

                await PersistQuarantinedArtifactAsync(writeCtx, quarantined, ct);
                RecordReplayFailure(quarantineReason);
                remaining.RemoveAt(i);
                i--;
                await PersistPendingPostPersistArtifactsAsync(writeCtx, remaining, ct);
                continue;
            }

            try
            {
                var updated = await ProcessPendingPostPersistArtifactAsync(
                    readDb,
                    writeCtx,
                    eventService,
                    auditLogger,
                    pending,
                    ct);

                if (IsArtifactFullyProcessed(updated))
                {
                    remaining.RemoveAt(i);
                    i--;
                }
                else
                {
                    remaining[i] = updated;
                }

                await PersistPendingPostPersistArtifactsAsync(writeCtx, remaining, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "StrategyGenerationWorker: deferred post-persist processing failed for strategy {StrategyId} (attempt {Attempt}/{Max})",
                    pending.StrategyId, pending.AttemptCount, maxArtifactRetries);
                RecordReplayFailure(ex.Message);
            }
        }

        if (remaining.Count == 0)
            _healthStore.RecordPhaseSuccess("artifact_replay", 0, _timeProvider.GetUtcNow().UtcDateTime);

        return remaining.Count;
    }

    private async Task<StrategyGenerationPendingArtifactRecord> ProcessPendingPostPersistArtifactAsync(
        DbContext readDb,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        StrategyGenerationPendingArtifactRecord pending,
        CancellationToken ct)
    {
        var writeDb = writeCtx.GetDbContext();
        var artifactSet = TryGetSet<StrategyGenerationPendingArtifact>(writeDb);
        StrategyGenerationPendingArtifact? trackedArtifact = artifactSet == null
            ? null
            : await artifactSet.FirstOrDefaultAsync(a => a.CandidateId == pending.CandidateId && !a.IsDeleted, ct);
        var trackedStrategy = await writeDb.Set<Domain.Entities.Strategy>().FindAsync([pending.StrategyId], ct);
        var strategy = trackedStrategy is { IsDeleted: false }
            ? new
            {
                trackedStrategy.Id,
                trackedStrategy.Name,
                trackedStrategy.Symbol,
                trackedStrategy.Timeframe,
                trackedStrategy.StrategyType,
                trackedStrategy.CreatedAt,
                trackedStrategy.IsDeleted,
                trackedStrategy.ScreeningMetricsJson,
            }
            : await readDb.Set<Domain.Entities.Strategy>()
                .IncludingSoftDeleted()
                .AsNoTracking()
                .Where(s => s.Id == pending.StrategyId)
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Symbol,
                    s.Timeframe,
                    s.StrategyType,
                    s.CreatedAt,
                    s.IsDeleted,
                    s.ScreeningMetricsJson,
                })
                .FirstOrDefaultAsync(ct);

        if (strategy == null || strategy.IsDeleted)
        {
            pending = pending with
            {
                NeedsCreationAudit = false,
                NeedsCreatedEvent = false,
                NeedsAutoPromoteEvent = false,
            };
            pending = RecordAttempt(pending, _timeProvider.GetUtcNow().UtcDateTime, null);
            await PersistTrackedArtifactStateAsync(writeCtx, trackedArtifact, pending, ct);
            return pending;
        }

        var attemptTimestamp = _timeProvider.GetUtcNow().UtcDateTime;
        bool attemptRecorded = false;
        var candidate = pending.Candidate.ToOutcome() with
        {
            Metrics = ScreeningMetrics.FromJson(strategy.ScreeningMetricsJson ?? pending.Candidate.ScreeningMetricsJson)
                ?? pending.Candidate.ToOutcome().Metrics,
        };
        // Strategy is loaded by Id, so Symbol/Timeframe/StrategyType already match the in-memory
        // candidate by construction — only the DB-assigned and generation-linkage fields change.
        candidate.Strategy.Id = pending.StrategyId;
        candidate.Strategy.Name = strategy.Name;
        candidate.Strategy.CreatedAt = strategy.CreatedAt;
        candidate.Strategy.ScreeningMetricsJson = strategy.ScreeningMetricsJson ?? candidate.Strategy.ScreeningMetricsJson;
        candidate.Strategy.GenerationCycleId = pending.CycleId;
        candidate.Strategy.GenerationCandidateId = pending.CandidateId;
        pending = await ReconcileDeferredEventDispatchesAsync(pending, ct);
        if (IsArtifactFullyProcessed(pending))
        {
            await PersistTrackedArtifactStateAsync(writeCtx, trackedArtifact, pending, ct);
            return pending;
        }

        try
        {
            if (pending.NeedsCreationAudit)
            {
                bool creationAuditExists = await CreationAuditExistsAsync(writeDb, readDb, pending.StrategyId, ct);
                if (!creationAuditExists)
                    await auditLogger.LogCreationAsync(candidate, pending.StrategyId, ct);

                pending = RecordAttempt(
                    pending with
                    {
                        NeedsCreationAudit = false,
                        CreationAuditLoggedAtUtc = pending.CreationAuditLoggedAtUtc ?? attemptTimestamp,
                    },
                    attemptTimestamp,
                    null,
                    ref attemptRecorded);
                await PersistTrackedArtifactStateAsync(writeCtx, trackedArtifact, pending, ct);
            }

            if (pending.NeedsCreatedEvent)
            {
                var createdEvent = _eventFactory.BuildCandidateCreatedEvent(candidate, pending.StrategyId);
                var updatedPending = RecordAttempt(
                    pending with
                    {
                        NeedsCreatedEvent = false,
                        CandidateCreatedEventId = createdEvent.Id,
                    },
                    attemptTimestamp,
                    null,
                    ref attemptRecorded);
                ApplyTrackedArtifactState(trackedArtifact, updatedPending);
                await eventService.SaveAndPublish(writeCtx, createdEvent);
                pending = await ReconcileDeferredEventDispatchesAsync(updatedPending, ct);
                await PersistTrackedArtifactStateAsync(writeCtx, trackedArtifact, pending, ct);
            }

            if (pending.NeedsAutoPromoteEvent)
            {
                var mutableStrategy = trackedStrategy is { IsDeleted: false }
                    ? trackedStrategy
                    : await writeDb.Set<Domain.Entities.Strategy>().FirstOrDefaultAsync(
                        s => s.Id == pending.StrategyId && !s.IsDeleted,
                        ct);
                if (mutableStrategy != null)
                {
                    var metrics = ScreeningMetrics.FromJson(mutableStrategy.ScreeningMetricsJson)
                        ?? candidate.Metrics
                        ?? new ScreeningMetrics { Regime = candidate.Regime.ToString(), ScreenedAtUtc = candidate.Strategy.CreatedAt };
                    if (!metrics.IsAutoPromoted)
                    {
                        mutableStrategy.ScreeningMetricsJson = (metrics with { IsAutoPromoted = true }).ToJson();
                    }

                    var updatedMetrics = ScreeningMetrics.FromJson(mutableStrategy.ScreeningMetricsJson)
                        ?? candidate.Metrics
                        ?? new ScreeningMetrics
                        {
                            Regime = candidate.Regime.ToString(),
                            IsAutoPromoted = true,
                            ScreenedAtUtc = candidate.Strategy.CreatedAt,
                        };
                    candidate.Strategy.ScreeningMetricsJson = mutableStrategy.ScreeningMetricsJson ?? updatedMetrics.ToJson();
                    candidate = candidate with { Metrics = updatedMetrics };
                }

                _logger.LogInformation(
                    "StrategyGenerationWorker: fast-track — {Name} prioritised for accelerated validation",
                    candidate.Strategy.Name);

                var autoPromotedEvent = _eventFactory.BuildAutoPromotedEvent(candidate, pending.StrategyId);
                var updatedPending = RecordAttempt(
                    pending with
                    {
                        NeedsAutoPromoteEvent = false,
                        AutoPromotedEventId = autoPromotedEvent.Id,
                    },
                    attemptTimestamp,
                    null,
                    ref attemptRecorded);
                ApplyTrackedArtifactState(trackedArtifact, updatedPending);
                await eventService.SaveAndPublish(writeCtx, autoPromotedEvent);
                pending = await ReconcileDeferredEventDispatchesAsync(updatedPending, ct);
                await PersistTrackedArtifactStateAsync(writeCtx, trackedArtifact, pending, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "StrategyGenerationWorker: deferred post-persist processing paused for strategy {StrategyId}",
                pending.StrategyId);
            RecordReplayFailure(ex.Message);
            pending = RecordAttempt(pending, attemptTimestamp, ex.Message, ref attemptRecorded);
            await PersistTrackedArtifactStateAsync(writeCtx, trackedArtifact, pending, ct);
        }

        return pending;
    }

    private async Task<StrategyGenerationPendingArtifactRecord> ReconcileDeferredEventDispatchesAsync(
        StrategyGenerationPendingArtifactRecord pending,
        CancellationToken ct)
    {
        var eventIds = new List<Guid>(2);
        if (pending.CandidateCreatedEventId.HasValue && pending.CandidateCreatedEventDispatchedAtUtc == null)
            eventIds.Add(pending.CandidateCreatedEventId.Value);
        if (pending.AutoPromotedEventId.HasValue && pending.AutoPromotedEventDispatchedAtUtc == null)
            eventIds.Add(pending.AutoPromotedEventId.Value);
        if (eventIds.Count == 0)
            return pending;

        var statuses = await LoadEventStatusesAsync(eventIds, ct);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        if (pending.CandidateCreatedEventId.HasValue
            && pending.CandidateCreatedEventDispatchedAtUtc == null
            && statuses.TryGetValue(pending.CandidateCreatedEventId.Value, out var createdStatus)
            && createdStatus.State == Lascodia.Trading.Engine.IntegrationEventLogEF.EventStateEnum.Published)
        {
            pending = pending with
            {
                CandidateCreatedEventDispatchedAtUtc = nowUtc,
                LastErrorMessage = null,
            };
        }

        if (pending.AutoPromotedEventId.HasValue
            && pending.AutoPromotedEventDispatchedAtUtc == null
            && statuses.TryGetValue(pending.AutoPromotedEventId.Value, out var promotedStatus)
            && promotedStatus.State == Lascodia.Trading.Engine.IntegrationEventLogEF.EventStateEnum.Published)
        {
            pending = pending with
            {
                AutoPromotedEventDispatchedAtUtc = nowUtc,
                LastErrorMessage = null,
            };
        }

        if (!IsArtifactFullyProcessed(pending))
        {
            string waitingMessage = BuildWaitingEventMessage(pending, statuses);
            if (!string.IsNullOrWhiteSpace(waitingMessage))
            {
                pending = pending with
                {
                    LastAttemptAtUtc = nowUtc,
                    LastErrorMessage = waitingMessage,
                };
            }
        }

        return pending;
    }

    private static StrategyGenerationPendingArtifactRecord RecordAttempt(
        StrategyGenerationPendingArtifactRecord pending,
        DateTime attemptAtUtc,
        string? error)
        => pending with
        {
            AttemptCount = pending.AttemptCount + 1,
            LastAttemptAtUtc = attemptAtUtc,
            LastErrorMessage = error,
        };

    private static StrategyGenerationPendingArtifactRecord RecordAttempt(
        StrategyGenerationPendingArtifactRecord pending,
        DateTime attemptAtUtc,
        string? error,
        ref bool attemptRecorded)
    {
        if (!attemptRecorded)
        {
            attemptRecorded = true;
            return RecordAttempt(pending, attemptAtUtc, error);
        }

        return pending with
        {
            LastAttemptAtUtc = attemptAtUtc,
            LastErrorMessage = error,
        };
    }

    private static void ApplyTrackedArtifactState(
        StrategyGenerationPendingArtifact? trackedArtifact,
        StrategyGenerationPendingArtifactRecord pending)
    {
        if (trackedArtifact == null)
            return;

        trackedArtifact.StrategyId = pending.StrategyId;
        trackedArtifact.CycleId = pending.CycleId;
        trackedArtifact.CandidatePayloadJson = System.Text.Json.JsonSerializer.Serialize(pending.Candidate);
        trackedArtifact.NeedsCreationAudit = pending.NeedsCreationAudit;
        trackedArtifact.NeedsCreatedEvent = pending.NeedsCreatedEvent;
        trackedArtifact.NeedsAutoPromoteEvent = pending.NeedsAutoPromoteEvent;
        trackedArtifact.AttemptCount = pending.AttemptCount;
        trackedArtifact.LastAttemptAtUtc = pending.LastAttemptAtUtc;
        trackedArtifact.LastErrorMessage = pending.LastErrorMessage;
        trackedArtifact.CreationAuditLoggedAtUtc = pending.CreationAuditLoggedAtUtc;
        trackedArtifact.CandidateCreatedEventId = pending.CandidateCreatedEventId;
        trackedArtifact.CandidateCreatedEventDispatchedAtUtc = pending.CandidateCreatedEventDispatchedAtUtc;
        trackedArtifact.AutoPromotedEventId = pending.AutoPromotedEventId;
        trackedArtifact.AutoPromotedEventDispatchedAtUtc = pending.AutoPromotedEventDispatchedAtUtc;
        trackedArtifact.QuarantinedAtUtc = pending.QuarantinedAtUtc;
        trackedArtifact.TerminalFailureReason = pending.TerminalFailureReason;
        trackedArtifact.IsDeleted = false;
    }

    private static async Task PersistTrackedArtifactStateAsync(
        IWriteApplicationDbContext writeCtx,
        StrategyGenerationPendingArtifact? trackedArtifact,
        StrategyGenerationPendingArtifactRecord pending,
        CancellationToken ct)
    {
        if (trackedArtifact == null)
            return;

        ApplyTrackedArtifactState(trackedArtifact, pending);
        await writeCtx.SaveChangesAsync(ct);
    }

    private static async Task PersistQuarantinedArtifactAsync(
        IWriteApplicationDbContext writeCtx,
        StrategyGenerationPendingArtifactRecord pending,
        CancellationToken ct)
    {
        var writeDb = writeCtx.GetDbContext();
        var artifactSet = TryGetSet<StrategyGenerationPendingArtifact>(writeDb);
        if (artifactSet == null)
            return;

        var trackedArtifact = await artifactSet.FirstOrDefaultAsync(
            a => a.CandidateId == pending.CandidateId && !a.IsDeleted,
            ct);
        if (trackedArtifact == null)
            return;

        ApplyTrackedArtifactState(trackedArtifact, pending);
        await writeCtx.SaveChangesAsync(ct);
    }

    private static async Task<bool> CreationAuditExistsAsync(
        DbContext writeDb,
        DbContext readDb,
        long strategyId,
        CancellationToken ct)
    {
        if (writeDb.Set<DecisionLog>().Local.Any(d =>
                d.EntityType == "Strategy"
                && d.EntityId == strategyId
                && d.DecisionType == "StrategyGeneration"
                && d.Outcome == "Created"))
        {
            return true;
        }

        return await readDb.Set<DecisionLog>()
            .AsNoTracking()
            .AnyAsync(d =>
                d.EntityType == "Strategy"
                && d.EntityId == strategyId
                && d.DecisionType == "StrategyGeneration"
                && d.Outcome == "Created", ct);
    }

    private static DbSet<TEntity>? TryGetSet<TEntity>(DbContext dbContext)
        where TEntity : class
    {
        try
        {
            return dbContext.Set<TEntity>();
        }
        catch
        {
            return null;
        }
    }

    private void UpdatePendingArtifactBacklog(
        IReadOnlyCollection<StrategyGenerationPendingArtifactRecord> pendingArtifacts,
        int corruptArtifactCount)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var oldestAttemptAtUtc = pendingArtifacts
            .Where(a => a.LastAttemptAtUtc.HasValue)
            .Select(a => a.LastAttemptAtUtc)
            .OrderBy(a => a)
            .FirstOrDefault();

        _healthStore.UpdateState(state => state with
        {
            PendingArtifacts = pendingArtifacts.Count,
            OldestPendingArtifactAttemptAtUtc = oldestAttemptAtUtc,
            LastReplayCorruptArtifactCount = corruptArtifactCount,
            CapturedAtUtc = nowUtc,
        });
    }

    private void RecordReplayOutcome(int artifactCount, int corruptArtifactCount, int remainingCount)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        _healthStore.UpdateState(state => state with
        {
            PendingArtifacts = remainingCount,
            OldestPendingArtifactAttemptAtUtc = remainingCount == 0 ? null : state.OldestPendingArtifactAttemptAtUtc,
            LastReplayCompletedAtUtc = nowUtc,
            LastReplayArtifactCount = artifactCount,
            LastReplayCorruptArtifactCount = corruptArtifactCount,
            LastReplayFailureAtUtc = remainingCount == 0 ? null : state.LastReplayFailureAtUtc,
            LastReplayFailureMessage = remainingCount == 0 ? null : state.LastReplayFailureMessage,
            CapturedAtUtc = nowUtc,
        });
    }

    private void RecordReplayFailure(string message)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        _healthStore.UpdateState(state => state with
        {
            LastReplayFailureAtUtc = nowUtc,
            LastReplayFailureMessage = Truncate(message),
            CapturedAtUtc = nowUtc,
        });
        _healthStore.RecordPhaseFailure("artifact_replay", message, nowUtc);
    }

    private static string Truncate(string message)
        => message.Length <= 500 ? message : message[..500];

    private async Task<IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot>> LoadEventStatusesAsync(
        IReadOnlyCollection<Guid> eventIds,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var eventLogReader = scope.ServiceProvider.GetRequiredService<IEventLogReader>();
        return await eventLogReader.GetEventStatusSnapshotsAsync(eventIds, ct);
    }

    private static bool IsArtifactFullyProcessed(StrategyGenerationPendingArtifactRecord pending)
    {
        bool createdPending = pending.CandidateCreatedEventId.HasValue && pending.CandidateCreatedEventDispatchedAtUtc == null;
        bool autoPromotePending = pending.AutoPromotedEventId.HasValue && pending.AutoPromotedEventDispatchedAtUtc == null;
        return !pending.NeedsCreationAudit
            && !pending.NeedsCreatedEvent
            && !pending.NeedsAutoPromoteEvent
            && !createdPending
            && !autoPromotePending;
    }

    private static string BuildWaitingEventMessage(
        StrategyGenerationPendingArtifactRecord pending,
        IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot> statuses)
    {
        var fragments = new List<string>(2);
        if (pending.CandidateCreatedEventId.HasValue && pending.CandidateCreatedEventDispatchedAtUtc == null)
            fragments.Add(BuildEventStatusFragment("candidate_created", pending.CandidateCreatedEventId.Value, statuses));
        if (pending.AutoPromotedEventId.HasValue && pending.AutoPromotedEventDispatchedAtUtc == null)
            fragments.Add(BuildEventStatusFragment("auto_promoted", pending.AutoPromotedEventId.Value, statuses));

        fragments.RemoveAll(string.IsNullOrWhiteSpace);
        return fragments.Count == 0 ? string.Empty : string.Join("; ", fragments);
    }

    private static string BuildEventStatusFragment(
        string label,
        Guid eventId,
        IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot> statuses)
    {
        if (!statuses.TryGetValue(eventId, out var status))
            return $"{label}:{eventId}:missing_from_event_log";

        return $"{label}:{eventId}:{status.State}:attempts={status.TimesSent}";
    }

    private void RecordCorruptArtifactQuarantine(IReadOnlyCollection<StrategyGenerationCorruptArtifactRecord> corruptArtifacts)
    {
        if (corruptArtifacts.Count == 0)
            return;

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        string? reason = corruptArtifacts
            .Select(a => a.Reason)
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
        _healthStore.UpdateState(state => state with
        {
            QuarantinedArtifacts = state.QuarantinedArtifacts + corruptArtifacts.Count,
            LastArtifactQuarantinedAtUtc = nowUtc,
            LastArtifactQuarantineReason = reason,
            CapturedAtUtc = nowUtc,
        });
        _healthStore.RecordPhaseFailure("artifact_quarantine", reason ?? "Corrupt pending artifact quarantined", nowUtc);
    }
}
