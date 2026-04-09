using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Workers;

public readonly record struct BacktestRunClaim(long? RunId, Guid LeaseToken);

public interface IBacktestRunClaimService
{
    void EnsureSupportedProvider(DbContext writeDb);

    Task<BacktestRunClaim> ClaimNextRunAsync(
        DbContext writeDb,
        DateTime nowUtc,
        string workerId,
        CancellationToken ct);

    Task<(int Requeued, int Orphaned)> RequeueExpiredRunsAsync(
        DbContext writeDb,
        DateTime nowUtc,
        CancellationToken ct);
}

public sealed class PostgresBacktestRunClaimService : IBacktestRunClaimService
{
    public void EnsureSupportedProvider(DbContext writeDb)
        => BacktestRunClaimer.EnsureSupportedProvider(writeDb);

    public async Task<BacktestRunClaim> ClaimNextRunAsync(
        DbContext writeDb,
        DateTime nowUtc,
        string workerId,
        CancellationToken ct)
    {
        var result = await BacktestRunClaimer.ClaimNextRunAsync(writeDb, nowUtc, workerId, ct);
        return new BacktestRunClaim(result.RunId, result.LeaseToken);
    }

    public Task<(int Requeued, int Orphaned)> RequeueExpiredRunsAsync(
        DbContext writeDb,
        DateTime nowUtc,
        CancellationToken ct)
        => BacktestRunClaimer.RequeueExpiredRunsAsync(writeDb, nowUtc, ct);
}
