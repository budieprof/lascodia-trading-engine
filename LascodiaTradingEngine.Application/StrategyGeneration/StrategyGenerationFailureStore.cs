using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationFailureStore))]
/// <summary>
/// EF-backed store for unresolved or operator-visible strategy-generation failures.
/// </summary>
internal sealed class StrategyGenerationFailureStore : IStrategyGenerationFailureStore
{
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationFailureStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<StrategyGenerationFailure>> LoadUnreportedFailuresAsync(
        DbContext readDb,
        CancellationToken ct)
    {
        var failureSet = TryGetSet<StrategyGenerationFailure>(readDb);
        if (failureSet == null)
            return [];

        return await failureSet
            .AsNoTracking()
            .Where(f => !f.IsDeleted && !f.IsReported && f.ResolvedAtUtc == null)
            .OrderBy(f => f.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task MarkFailuresReportedAsync(
        DbContext writeDb,
        IReadOnlyCollection<long> failureIds,
        CancellationToken ct)
    {
        if (failureIds.Count == 0)
            return;

        var failureSet = TryGetSet<StrategyGenerationFailure>(writeDb);
        if (failureSet == null)
            return;

        var failures = await failureSet
            .Where(f => failureIds.Contains(f.Id) && !f.IsDeleted)
            .ToListAsync(ct);

        foreach (var failure in failures)
            failure.IsReported = true;
    }

    public async Task MarkFailuresResolvedAsync(
        DbContext writeDb,
        IReadOnlyCollection<string> candidateIds,
        CancellationToken ct)
    {
        if (candidateIds.Count == 0)
            return;

        var failureSet = TryGetSet<StrategyGenerationFailure>(writeDb);
        if (failureSet == null)
            return;

        var failures = await failureSet
            .Where(f => candidateIds.Contains(f.CandidateId) && !f.IsDeleted && f.ResolvedAtUtc == null)
            .ToListAsync(ct);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var failure in failures)
            failure.ResolvedAtUtc = now;
    }

    public async Task RecordFailuresAsync(
        DbContext writeDb,
        IReadOnlyCollection<StrategyGenerationFailureRecord> failures,
        CancellationToken ct)
    {
        if (failures.Count == 0)
            return;

        // Deduplicate by candidate and failure stage so repeated retries do not flood the
        // failure table with copies of the same unresolved persistence problem.
        var failureSet = TryGetSet<StrategyGenerationFailure>(writeDb);
        if (failureSet == null)
            return;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var failure in failures)
        {
            bool exists = await failureSet.AnyAsync(
                f => !f.IsDeleted
                  && f.ResolvedAtUtc == null
                  && f.CandidateId == failure.CandidateId
                  && f.FailureStage == failure.FailureStage,
                ct);
            if (exists)
                continue;

            failureSet.Add(new StrategyGenerationFailure
            {
                CandidateId = failure.CandidateId,
                CycleId = failure.CycleId,
                CandidateHash = failure.CandidateHash,
                StrategyType = failure.StrategyType,
                Symbol = failure.Symbol,
                Timeframe = failure.Timeframe,
                ParametersJson = failure.ParametersJson,
                FailureStage = failure.FailureStage,
                FailureReason = failure.FailureReason,
                DetailsJson = failure.DetailsJson,
                CreatedAtUtc = now,
                IsReported = false,
            });
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
