using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

internal sealed record PersistCandidatesResult(int PersistedCount, int ReservePersistedCount);

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationPersistenceCoordinator))]
internal sealed class StrategyGenerationPersistenceCoordinator : IStrategyGenerationPersistenceCoordinator
{
    private readonly IStrategyGenerationCandidatePersistenceService _candidatePersistenceService;
    private readonly IStrategyGenerationArtifactReplayService _artifactReplayService;

    public StrategyGenerationPersistenceCoordinator(
        IStrategyGenerationCandidatePersistenceService candidatePersistenceService,
        IStrategyGenerationArtifactReplayService artifactReplayService)
    {
        _candidatePersistenceService = candidatePersistenceService;
        _artifactReplayService = artifactReplayService;
    }

    public Task<PersistCandidatesResult> PersistCandidatesAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        List<ScreeningOutcome> candidates,
        GenerationConfig config,
        CancellationToken ct)
        => _candidatePersistenceService.PersistCandidatesAsync(
            readCtx,
            writeCtx,
            eventService,
            auditLogger,
            candidates,
            config,
            ct);

    public Task ReplayPendingPostPersistArtifactsAsync(
        DbContext readDb,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        CancellationToken ct)
        => _artifactReplayService.ReplayPendingPostPersistArtifactsAsync(
            readDb,
            writeCtx,
            eventService,
            auditLogger,
            ct);
}
