using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationFeedbackStateStore))]
/// <summary>
/// Small EF-backed key/value store for durable feedback cache payloads.
/// </summary>
internal sealed class StrategyGenerationFeedbackStateStore : IStrategyGenerationFeedbackStateStore
{
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationFeedbackStateStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public async Task<StrategyGenerationFeedbackStateRecord?> LoadAsync(
        DbContext readDb,
        string stateKey,
        CancellationToken ct)
    {
        var set = TryGetSet<StrategyGenerationFeedbackState>(readDb);
        if (set == null)
            return null;

        var state = await set
            .AsNoTracking()
            .Where(s => s.StateKey == stateKey && !s.IsDeleted)
            .FirstOrDefaultAsync(ct);

        return state == null
            ? null
            : new StrategyGenerationFeedbackStateRecord(state.StateKey, state.PayloadJson, state.LastUpdatedAtUtc);
    }

    public async Task SaveAsync(
        DbContext writeDb,
        string stateKey,
        string payloadJson,
        CancellationToken ct)
    {
        var set = TryGetSet<StrategyGenerationFeedbackState>(writeDb);
        if (set == null)
            return;

        var tracked = await set
            .FirstOrDefaultAsync(s => s.StateKey == stateKey && !s.IsDeleted, ct);

        if (tracked == null)
        {
            tracked = new StrategyGenerationFeedbackState
            {
                StateKey = stateKey,
            };
            set.Add(tracked);
        }

        tracked.PayloadJson = payloadJson;
        tracked.LastUpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        tracked.IsDeleted = false;
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
