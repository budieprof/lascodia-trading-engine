using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationArtifactReplayService))]
internal sealed class StrategyGenerationArtifactReplayService : IStrategyGenerationArtifactReplayService
{
    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly IStrategyGenerationPendingArtifactStore _pendingArtifactStore;
    private readonly IStrategyGenerationEventFactory _eventFactory;
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationArtifactReplayService(
        ILogger<StrategyGenerationWorker> logger,
        IStrategyGenerationPendingArtifactStore pendingArtifactStore,
        IStrategyGenerationEventFactory eventFactory,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _pendingArtifactStore = pendingArtifactStore;
        _eventFactory = eventFactory;
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
        await PersistPendingPostPersistArtifactsAsync(writeCtx, pendingArtifacts, ct);
        await DrainPendingPostPersistArtifactsAsync(readDb, writeCtx, eventService, auditLogger, pendingArtifacts, ct);
    }

    public async Task ReplayPendingPostPersistArtifactsAsync(
        DbContext readDb,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        CancellationToken ct)
    {
        var pendingLoad = await _pendingArtifactStore.LoadPendingArtifactsAsync(readDb, ct);
        if (pendingLoad.CorruptArtifactIds.Count > 0)
        {
            _logger.LogWarning(
                "StrategyGenerationWorker: quarantining {Count} corrupt deferred post-persist artifact rows",
                pendingLoad.CorruptArtifactIds.Count);
            await PersistPendingPostPersistArtifactsAsync(writeCtx, pendingLoad.PendingArtifacts, ct);
        }

        if (pendingLoad.PendingArtifacts.Count == 0)
            return;

        _logger.LogWarning(
            "StrategyGenerationWorker: replaying {Count} deferred post-persist artifacts from a prior partial failure",
            pendingLoad.PendingArtifacts.Count);

        await DrainPendingPostPersistArtifactsAsync(
            readDb,
            writeCtx,
            eventService,
            auditLogger,
            pendingLoad.PendingArtifacts,
            ct);
    }

    private async Task PersistPendingPostPersistArtifactsAsync(
        IWriteApplicationDbContext writeCtx,
        IReadOnlyCollection<StrategyGenerationPendingArtifactRecord> pendingArtifacts,
        CancellationToken ct)
    {
        await _pendingArtifactStore.ReplacePendingArtifactsAsync(writeCtx.GetDbContext(), pendingArtifacts, ct);
        await writeCtx.SaveChangesAsync(ct);
    }

    private async Task DrainPendingPostPersistArtifactsAsync(
        DbContext readDb,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        IReadOnlyCollection<StrategyGenerationPendingArtifactRecord> pendingArtifacts,
        CancellationToken ct)
    {
        var remaining = pendingArtifacts.ToList();
        if (remaining.Count == 0)
            return;

        const int maxArtifactRetries = 10;

        for (int i = 0; i < remaining.Count; i++)
        {
            var pending = remaining[i];

            if (pending.AttemptCount >= maxArtifactRetries)
            {
                _logger.LogError(
                    "StrategyGenerationWorker: artifact for strategy {StrategyId} exhausted {Max} retries — quarantining",
                    pending.StrategyId, maxArtifactRetries);
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

                if (!updated.NeedsCreationAudit && !updated.NeedsCreatedEvent && !updated.NeedsAutoPromoteEvent)
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
            }
        }
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
            return pending with
            {
                NeedsCreationAudit = false,
                NeedsCreatedEvent = false,
                NeedsAutoPromoteEvent = false,
            };
        }

        var candidate = pending.Candidate.ToOutcome() with
        {
            Metrics = ScreeningMetrics.FromJson(strategy.ScreeningMetricsJson ?? pending.Candidate.ScreeningMetricsJson)
                ?? pending.Candidate.ToOutcome().Metrics,
        };
        candidate.Strategy.Id = pending.StrategyId;
        candidate.Strategy.GenerationCandidateId = pending.CandidateId;
        candidate.Strategy.GenerationCycleId = pending.CycleId;
        candidate.Strategy.Name = strategy.Name;
        candidate.Strategy.Symbol = strategy.Symbol;
        candidate.Strategy.Timeframe = strategy.Timeframe;
        candidate.Strategy.StrategyType = strategy.StrategyType;
        candidate.Strategy.CreatedAt = strategy.CreatedAt;
        candidate.Strategy.ScreeningMetricsJson = strategy.ScreeningMetricsJson ?? candidate.Strategy.ScreeningMetricsJson;

        try
        {
            if (pending.NeedsCreationAudit)
            {
                await auditLogger.LogCreationAsync(candidate, pending.StrategyId, ct);
                pending = pending with { NeedsCreationAudit = false };
            }

            if (pending.NeedsCreatedEvent)
            {
                await eventService.SaveAndPublish(writeCtx, _eventFactory.BuildCandidateCreatedEvent(candidate, pending.StrategyId));
                pending = pending with { NeedsCreatedEvent = false };
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
                        await writeCtx.SaveChangesAsync(ct);
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

                await eventService.SaveAndPublish(writeCtx, _eventFactory.BuildAutoPromotedEvent(candidate, pending.StrategyId));
                pending = pending with { NeedsAutoPromoteEvent = false };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "StrategyGenerationWorker: deferred post-persist processing paused for strategy {StrategyId}",
                pending.StrategyId);
            pending = pending with
            {
                AttemptCount = pending.AttemptCount + 1,
                LastAttemptAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                LastErrorMessage = ex.Message,
            };
        }

        pending = pending with
        {
            AttemptCount = pending.AttemptCount + 1,
            LastAttemptAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            LastErrorMessage = null,
        };

        return pending;
    }
}
