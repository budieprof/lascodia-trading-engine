using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationCheckpointStore))]
internal sealed class StrategyGenerationCheckpointStore : IStrategyGenerationCheckpointStore
{
    private const string WorkerName = "StrategyGenerationWorker";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StrategyGenerationCheckpointStore> _logger;

    public StrategyGenerationCheckpointStore(
        TimeProvider timeProvider,
        ILogger<StrategyGenerationCheckpointStore> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<GenerationCheckpointStore.State?> LoadCheckpointAsync(
        DbContext readDb,
        DateTime cycleDateUtc,
        string expectedFingerprint,
        CancellationToken ct)
    {
        var checkpointSet = TryGetSet<StrategyGenerationCheckpoint>(readDb);
        if (checkpointSet == null)
            return null;

        var checkpoint = await checkpointSet
            .AsNoTracking()
            .Where(c => c.WorkerName == WorkerName && !c.IsDeleted)
            .OrderByDescending(c => c.LastUpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (checkpoint == null)
            return null;

        return GenerationCheckpointStore.Restore(
            checkpoint.PayloadJson,
            cycleDateUtc,
            expectedFingerprint,
            _logger);
    }

    public async Task SaveCheckpointAsync(
        DbContext writeDb,
        string cycleId,
        GenerationCheckpointStore.State state,
        ILogger? logger,
        CancellationToken ct)
    {
        var checkpointSet = TryGetSet<StrategyGenerationCheckpoint>(writeDb);
        if (checkpointSet == null)
        {
            _logger.LogDebug(
                "StrategyGenerationCheckpointStore: checkpoint set unavailable; skipping checkpoint save");
            return;
        }

        var serialization = GenerationCheckpointStore.SerializeWithStatus(state, logger);
        var checkpoint = await checkpointSet
            .FirstOrDefaultAsync(c => c.WorkerName == WorkerName && !c.IsDeleted, ct);

        if (checkpoint == null)
        {
            checkpoint = new StrategyGenerationCheckpoint
            {
                WorkerName = WorkerName,
            };
            checkpointSet.Add(checkpoint);
        }

        checkpoint.CycleId = cycleId;
        checkpoint.CycleDateUtc = state.CycleDateUtc;
        checkpoint.Fingerprint = state.Fingerprint;
        checkpoint.PayloadJson = serialization.Json;
        checkpoint.UsedRestartSafeFallback = serialization.UsedRestartSafeFallback;
        checkpoint.LastUpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        checkpoint.IsDeleted = false;
    }

    public async Task ClearCheckpointAsync(DbContext writeDb, CancellationToken ct)
    {
        var checkpointSet = TryGetSet<StrategyGenerationCheckpoint>(writeDb);
        if (checkpointSet == null)
            return;

        var checkpoint = await checkpointSet
            .FirstOrDefaultAsync(c => c.WorkerName == WorkerName && !c.IsDeleted, ct);
        if (checkpoint != null)
        {
            checkpoint.IsDeleted = true;
            checkpoint.LastUpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
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
}
