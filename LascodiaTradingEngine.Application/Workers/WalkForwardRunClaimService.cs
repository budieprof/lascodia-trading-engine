using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Workers;

public readonly record struct WalkForwardRunClaim(long? RunId, Guid LeaseToken);

public interface IWalkForwardRunClaimService
{
    void EnsureSupportedProvider(DbContext writeDb);

    Task<WalkForwardRunClaim> ClaimNextRunAsync(
        DbContext writeDb,
        DateTime nowUtc,
        string workerId,
        CancellationToken ct);

    Task<(int Requeued, int Orphaned)> RequeueExpiredRunsAsync(
        DbContext writeDb,
        DateTime nowUtc,
        CancellationToken ct);
}

public sealed class PostgresWalkForwardRunClaimService : IWalkForwardRunClaimService
{
    public void EnsureSupportedProvider(DbContext writeDb)
        => WalkForwardRunClaimer.EnsureSupportedProvider(writeDb);

    public async Task<WalkForwardRunClaim> ClaimNextRunAsync(
        DbContext writeDb,
        DateTime nowUtc,
        string workerId,
        CancellationToken ct)
    {
        var result = await WalkForwardRunClaimer.ClaimNextRunAsync(writeDb, nowUtc, workerId, ct);
        return new WalkForwardRunClaim(result.RunId, result.LeaseToken);
    }

    public Task<(int Requeued, int Orphaned)> RequeueExpiredRunsAsync(
        DbContext writeDb,
        DateTime nowUtc,
        CancellationToken ct)
        => WalkForwardRunClaimer.RequeueExpiredRunsAsync(writeDb, nowUtc, ct);
}
