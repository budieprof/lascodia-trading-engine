using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationPendingArtifactStore))]
internal sealed class StrategyGenerationPendingArtifactStore : IStrategyGenerationPendingArtifactStore
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StrategyGenerationPendingArtifactStore> _logger;

    public StrategyGenerationPendingArtifactStore(
        TimeProvider timeProvider,
        ILogger<StrategyGenerationPendingArtifactStore> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<StrategyGenerationPendingArtifactLoadResult> LoadPendingArtifactsAsync(
        DbContext readDb,
        CancellationToken ct)
    {
        var pendingArtifactSet = TryGetSet<StrategyGenerationPendingArtifact>(readDb);
        if (pendingArtifactSet == null)
            return new StrategyGenerationPendingArtifactLoadResult([], []);

        var entities = await pendingArtifactSet
            .AsNoTracking()
            .Where(a => !a.IsDeleted && a.QuarantinedAtUtc == null)
            .OrderBy(a => a.StrategyId)
            .ToListAsync(ct);

        var results = new List<StrategyGenerationPendingArtifactRecord>(entities.Count);
        var corruptArtifacts = new List<StrategyGenerationCorruptArtifactRecord>();
        foreach (var entity in entities)
        {
            try
            {
                var candidate = JsonSerializer.Deserialize<GenerationCheckpointStore.PendingCandidateState>(entity.CandidatePayloadJson);
                if (candidate == null)
                {
                    corruptArtifacts.Add(new StrategyGenerationCorruptArtifactRecord(
                        entity.Id,
                        "Pending artifact payload deserialized to null."));
                    continue;
                }

                results.Add(new StrategyGenerationPendingArtifactRecord(
                    entity.StrategyId,
                    entity.CandidateId,
                    entity.CycleId,
                    candidate,
                    entity.NeedsCreationAudit,
                    entity.NeedsCreatedEvent,
                    entity.NeedsAutoPromoteEvent,
                    entity.AttemptCount,
                    entity.LastAttemptAtUtc,
                    entity.LastErrorMessage,
                    entity.CreationAuditLoggedAtUtc,
                    entity.CandidateCreatedEventId,
                    entity.CandidateCreatedEventDispatchedAtUtc,
                    entity.AutoPromotedEventId,
                    entity.AutoPromotedEventDispatchedAtUtc,
                    entity.QuarantinedAtUtc,
                    entity.TerminalFailureReason));
            }
            catch (Exception ex)
            {
                corruptArtifacts.Add(new StrategyGenerationCorruptArtifactRecord(
                    entity.Id,
                    TruncateReason(ex.Message)));
                _logger.LogWarning(ex,
                    "StrategyGenerationPendingArtifactStore: failed to deserialize pending artifact {ArtifactId}; quarantining corrupt row",
                    entity.Id);
            }
        }

        return new StrategyGenerationPendingArtifactLoadResult(results, corruptArtifacts);
    }

    public async Task QuarantineCorruptArtifactsAsync(
        DbContext writeDb,
        IReadOnlyCollection<StrategyGenerationCorruptArtifactRecord> corruptArtifacts,
        CancellationToken ct)
    {
        if (corruptArtifacts.Count == 0)
            return;

        var pendingArtifactSet = TryGetSet<StrategyGenerationPendingArtifact>(writeDb);
        if (pendingArtifactSet == null)
            return;

        var artifactIds = corruptArtifacts.Select(a => a.ArtifactId).Distinct().ToArray();
        var tracked = await pendingArtifactSet
            .Where(a => artifactIds.Contains(a.Id) && !a.IsDeleted)
            .ToListAsync(ct);

        var reasonsById = corruptArtifacts.ToDictionary(a => a.ArtifactId, a => a.Reason);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var artifact in tracked)
        {
            artifact.NeedsCreationAudit = false;
            artifact.NeedsCreatedEvent = false;
            artifact.NeedsAutoPromoteEvent = false;
            artifact.LastAttemptAtUtc = nowUtc;
            artifact.LastErrorMessage = reasonsById.GetValueOrDefault(artifact.Id);
            artifact.QuarantinedAtUtc = nowUtc;
            artifact.TerminalFailureReason = reasonsById.GetValueOrDefault(artifact.Id);
        }
    }

    public async Task ReplacePendingArtifactsAsync(
        DbContext writeDb,
        IReadOnlyCollection<StrategyGenerationPendingArtifactRecord> pendingArtifacts,
        CancellationToken ct)
    {
        var pendingArtifactSet = TryGetSet<StrategyGenerationPendingArtifact>(writeDb);
        if (pendingArtifactSet == null)
            return;

        var tracked = await pendingArtifactSet
            .Where(a => !a.IsDeleted && a.QuarantinedAtUtc == null)
            .ToListAsync(ct);

        var incomingByCandidateId = pendingArtifacts.ToDictionary(a => a.CandidateId, StringComparer.Ordinal);
        foreach (var existing in tracked)
        {
            if (!incomingByCandidateId.ContainsKey(existing.CandidateId))
            {
                existing.IsDeleted = true;
                existing.LastAttemptAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            }
        }

        foreach (var pending in pendingArtifacts)
        {
            var existing = tracked.FirstOrDefault(a => string.Equals(a.CandidateId, pending.CandidateId, StringComparison.Ordinal));
            if (existing == null)
            {
                existing = new StrategyGenerationPendingArtifact
                {
                    CandidateId = pending.CandidateId,
                };
                pendingArtifactSet.Add(existing);
            }

            existing.StrategyId = pending.StrategyId;
            existing.CycleId = pending.CycleId;
            existing.CandidatePayloadJson = JsonSerializer.Serialize(pending.Candidate);
            existing.NeedsCreationAudit = pending.NeedsCreationAudit;
            existing.NeedsCreatedEvent = pending.NeedsCreatedEvent;
            existing.NeedsAutoPromoteEvent = pending.NeedsAutoPromoteEvent;
            existing.AttemptCount = pending.AttemptCount;
            existing.LastAttemptAtUtc = pending.LastAttemptAtUtc;
            existing.LastErrorMessage = pending.LastErrorMessage;
            existing.CreationAuditLoggedAtUtc = pending.CreationAuditLoggedAtUtc;
            existing.CandidateCreatedEventId = pending.CandidateCreatedEventId;
            existing.CandidateCreatedEventDispatchedAtUtc = pending.CandidateCreatedEventDispatchedAtUtc;
            existing.AutoPromotedEventId = pending.AutoPromotedEventId;
            existing.AutoPromotedEventDispatchedAtUtc = pending.AutoPromotedEventDispatchedAtUtc;
            existing.QuarantinedAtUtc = pending.QuarantinedAtUtc;
            existing.TerminalFailureReason = pending.TerminalFailureReason;
            existing.IsDeleted = false;
        }
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

    private static string TruncateReason(string reason)
        => reason.Length <= 1000 ? reason : reason[..1000];
}
