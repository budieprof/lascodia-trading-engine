using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Durable summary of a completed generation cycle.
/// </summary>
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
/// <summary>
/// EF-backed persistence for the strategy-generation cycle audit trail.
/// </summary>
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
        // Starting the cycle early gives later recovery paths a durable anchor even if the
        // process crashes before screening or summary publication completes.
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

    public async Task AttachFingerprintAsync(DbContext writeDb, string cycleId, string fingerprint, CancellationToken ct)
    {
        var cycle = await LoadMutableAsync(writeDb, cycleId, ct);
        if (cycle == null)
            return;

        cycle.Fingerprint = fingerprint;
        cycle.LastUpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
    }

    public async Task StageCompletionAsync(
        DbContext writeDb,
        string cycleId,
        StrategyGenerationCycleRunCompletion completion,
        CancellationToken ct)
    {
        // Stage completion before summary dispatch so the run is durably marked complete even
        // if the outbound summary event needs to be retried separately.
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

    public async Task StageSummaryDispatchAttemptAsync(
        DbContext writeDb,
        string cycleId,
        Guid eventId,
        string payloadJson,
        DateTime attemptedAtUtc,
        CancellationToken ct)
    {
        var cycle = await LoadMutableAsync(writeDb, cycleId, ct);
        if (cycle == null)
            return;

        cycle.SummaryEventId = eventId;
        cycle.SummaryEventPayloadJson = payloadJson;
        cycle.SummaryEventDispatchAttempts++;
        cycle.SummaryEventDispatchedAtUtc = null;
        cycle.SummaryEventFailedAtUtc = null;
        cycle.SummaryEventFailureMessage = null;
        cycle.LastUpdatedAtUtc = attemptedAtUtc;
    }

    public async Task MarkSummaryDispatchPublishedAsync(
        DbContext writeDb,
        string cycleId,
        DateTime dispatchedAtUtc,
        CancellationToken ct)
    {
        var cycle = await LoadMutableAsync(writeDb, cycleId, ct);
        if (cycle == null)
            return;

        cycle.SummaryEventDispatchedAtUtc = dispatchedAtUtc;
        cycle.SummaryEventFailedAtUtc = null;
        cycle.SummaryEventFailureMessage = null;
        cycle.LastUpdatedAtUtc = dispatchedAtUtc;
    }

    public async Task RecordSummaryDispatchFailureAsync(
        DbContext writeDb,
        string cycleId,
        Guid eventId,
        string payloadJson,
        string errorMessage,
        DateTime failedAtUtc,
        CancellationToken ct)
    {
        // Preserve the payload that failed so replay logic can republish the exact same summary
        // without reconstructing it from potentially changed runtime state.
        var cycle = await LoadMutableAsync(writeDb, cycleId, ct);
        if (cycle == null)
            return;

        cycle.SummaryEventId = eventId;
        cycle.SummaryEventPayloadJson = payloadJson;
        if (cycle.SummaryEventDispatchAttempts == 0)
            cycle.SummaryEventDispatchAttempts = 1;
        cycle.SummaryEventDispatchedAtUtc = null;
        cycle.SummaryEventFailedAtUtc = failedAtUtc;
        cycle.SummaryEventFailureMessage = errorMessage.Length <= 1000
            ? errorMessage
            : errorMessage[..1000];
        cycle.LastUpdatedAtUtc = failedAtUtc;
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

    public async Task<IReadOnlyList<StrategyGenerationSummaryDispatchRecord>> LoadPendingSummaryDispatchesAsync(
        DbContext readDb,
        CancellationToken ct)
    {
        var cycleSet = TryGetSet<StrategyGenerationCycleRun>(readDb);
        if (cycleSet == null)
            return [];

        return await cycleSet
            .AsNoTracking()
            .Where(c => !c.IsDeleted
                     && c.Status == "Completed"
                     && c.SummaryEventId != null
                     && c.SummaryEventDispatchedAtUtc == null)
            .OrderBy(c => c.LastUpdatedAtUtc)
            .Select(c => new StrategyGenerationSummaryDispatchRecord(
                c.CycleId,
                c.SummaryEventId!.Value,
                c.SummaryEventFailedAtUtc,
                c.SummaryEventDispatchedAtUtc,
                c.SummaryEventFailureMessage,
                c.SummaryEventPayloadJson))
            .ToListAsync(ct);
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
