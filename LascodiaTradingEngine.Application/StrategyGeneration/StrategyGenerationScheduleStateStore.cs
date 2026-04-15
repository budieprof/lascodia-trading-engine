using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Durable snapshot of day-level strategy-generation scheduler state.
/// </summary>
internal sealed record StrategyGenerationScheduleStateSnapshot(
    DateTime LastRunDateUtc,
    int ConsecutiveFailures,
    DateTime CircuitBreakerUntilUtc,
    int RetriesThisWindow,
    DateTime RetryWindowDateUtc);

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationScheduleStateStore))]
/// <summary>
/// EF-backed store for strategy-generation scheduler state.
/// </summary>
internal sealed class StrategyGenerationScheduleStateStore : IStrategyGenerationScheduleStateStore
{
    private const string WorkerName = "StrategyGenerationWorker";

    private readonly TimeProvider _timeProvider;

    public StrategyGenerationScheduleStateStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public async Task<StrategyGenerationScheduleStateSnapshot> LoadAsync(DbContext readDb, CancellationToken ct)
    {
        var scheduleSet = TryGetSet<StrategyGenerationScheduleState>(readDb);
        if (scheduleSet == null)
            return new StrategyGenerationScheduleStateSnapshot(
                DateTime.MinValue,
                0,
                DateTime.MinValue,
                0,
                DateTime.MinValue);

        var typed = await scheduleSet
            .AsNoTracking()
            .Where(s => s.WorkerName == WorkerName && !s.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (typed == null)
        {
            return new StrategyGenerationScheduleStateSnapshot(
                DateTime.MinValue,
                0,
                DateTime.MinValue,
                0,
                DateTime.MinValue);
        }

        return new StrategyGenerationScheduleStateSnapshot(
            typed.LastRunDateUtc?.Date ?? DateTime.MinValue,
            typed.ConsecutiveFailures,
            typed.CircuitBreakerUntilUtc ?? DateTime.MinValue,
            typed.RetriesThisWindow,
            typed.RetryWindowDateUtc?.Date ?? DateTime.MinValue);
    }

    public async Task SaveAsync(
        DbContext writeDb,
        StrategyGenerationScheduleStateSnapshot snapshot,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var scheduleSet = TryGetSet<StrategyGenerationScheduleState>(writeDb);
        if (scheduleSet == null)
            return;

        var typed = await scheduleSet
            .FirstOrDefaultAsync(s => s.WorkerName == WorkerName && !s.IsDeleted, ct);
        if (typed == null)
        {
            typed = new StrategyGenerationScheduleState
            {
                WorkerName = WorkerName,
            };
            scheduleSet.Add(typed);
        }

        typed.LastRunDateUtc = snapshot.LastRunDateUtc == DateTime.MinValue ? null : snapshot.LastRunDateUtc.Date;
        typed.CircuitBreakerUntilUtc = snapshot.CircuitBreakerUntilUtc == DateTime.MinValue ? null : snapshot.CircuitBreakerUntilUtc;
        typed.ConsecutiveFailures = snapshot.ConsecutiveFailures;
        typed.RetriesThisWindow = snapshot.RetriesThisWindow;
        typed.RetryWindowDateUtc = snapshot.RetryWindowDateUtc == DateTime.MinValue ? null : snapshot.RetryWindowDateUtc.Date;
        typed.LastUpdatedAtUtc = nowUtc;
        typed.IsDeleted = false;
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
