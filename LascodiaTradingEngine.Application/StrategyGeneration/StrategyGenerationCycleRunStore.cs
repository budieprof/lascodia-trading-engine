using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

internal sealed record StrategyGenerationCycleRunCompletion(
    double DurationMs,
    int CandidatesCreated,
    int ReserveCandidatesCreated,
    int CandidatesScreened,
    int SymbolsProcessed,
    int SymbolsSkipped,
    int StrategiesPruned,
    int PortfolioFilterRemoved);

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationCycleRunStore))]
internal sealed class StrategyGenerationCycleRunStore : IStrategyGenerationCycleRunStore
{
    private const string WorkerName = "StrategyGenerationWorker";

    private readonly TimeProvider _timeProvider;

    public StrategyGenerationCycleRunStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public async Task StartAsync(DbContext writeDb, string cycleId, string? fingerprint, CancellationToken ct)
    {
        var cycleSet = TryGetSet<StrategyGenerationCycleRun>(writeDb);
        if (cycleSet == null)
            return;

        bool exists = await cycleSet.AnyAsync(c => c.CycleId == cycleId && !c.IsDeleted, ct);
        if (exists)
            return;

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        cycleSet.Add(new StrategyGenerationCycleRun
        {
            WorkerName = WorkerName,
            CycleId = cycleId,
            Status = "Running",
            Fingerprint = fingerprint,
            StartedAtUtc = nowUtc,
            LastUpdatedAtUtc = nowUtc,
            IsDeleted = false,
        });
    }

    public async Task CompleteAsync(
        DbContext writeDb,
        string cycleId,
        StrategyGenerationCycleRunCompletion completion,
        CancellationToken ct)
    {
        var cycle = await LoadMutableAsync(writeDb, cycleId, ct);
        if (cycle == null)
            return;

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        cycle.Status = "Completed";
        cycle.CompletedAtUtc = nowUtc;
        cycle.DurationMs = completion.DurationMs;
        cycle.CandidatesCreated = completion.CandidatesCreated;
        cycle.ReserveCandidatesCreated = completion.ReserveCandidatesCreated;
        cycle.CandidatesScreened = completion.CandidatesScreened;
        cycle.SymbolsProcessed = completion.SymbolsProcessed;
        cycle.SymbolsSkipped = completion.SymbolsSkipped;
        cycle.StrategiesPruned = completion.StrategiesPruned;
        cycle.PortfolioFilterRemoved = completion.PortfolioFilterRemoved;
        cycle.LastUpdatedAtUtc = nowUtc;
        cycle.FailureStage = null;
        cycle.FailureMessage = null;
    }

    public async Task FailAsync(
        DbContext writeDb,
        string cycleId,
        string failureStage,
        string failureMessage,
        CancellationToken ct)
    {
        var cycle = await LoadMutableAsync(writeDb, cycleId, ct);
        if (cycle == null)
            return;

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        cycle.Status = "Failed";
        cycle.CompletedAtUtc = nowUtc;
        cycle.FailureStage = failureStage;
        cycle.FailureMessage = failureMessage.Length <= 2000
            ? failureMessage
            : failureMessage[..2000];
        cycle.LastUpdatedAtUtc = nowUtc;
    }

    public async Task<StrategyGenerationCycleRun?> LoadPreviousCompletedAsync(
        DbContext readDb,
        string currentCycleId,
        CancellationToken ct)
    {
        var cycleSet = TryGetSet<StrategyGenerationCycleRun>(readDb);
        if (cycleSet == null)
            return null;

        return await cycleSet
            .AsNoTracking()
            .Where(c => c.WorkerName == WorkerName
                     && !c.IsDeleted
                     && c.Status == "Completed"
                     && c.CycleId != currentCycleId)
            .OrderByDescending(c => c.CompletedAtUtc ?? c.StartedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<StrategyGenerationCycleRun?> LoadMutableAsync(DbContext writeDb, string cycleId, CancellationToken ct)
    {
        var cycleSet = TryGetSet<StrategyGenerationCycleRun>(writeDb);
        if (cycleSet == null)
            return null;

        return await cycleSet.FirstOrDefaultAsync(c => c.CycleId == cycleId && !c.IsDeleted, ct);
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
