using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationRunOwnedMutationGuard
{
    private readonly OptimizationRunLeaseManager _leaseManager;
    private readonly ILogger<OptimizationRunOwnedMutationGuard> _logger;

    public OptimizationRunOwnedMutationGuard(
        OptimizationRunLeaseManager leaseManager,
        ILogger<OptimizationRunOwnedMutationGuard> logger)
    {
        _leaseManager = leaseManager;
        _logger = logger;
    }

    public async Task<bool> TrySaveChangesAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        OptimizationRun run,
        Guid expectedLeaseToken,
        CancellationToken ct,
        string staleOwnerMessage)
    {
        try
        {
            await SaveChangesOrThrowAsync(writeDb, writeCtx, run, expectedLeaseToken, ct, staleOwnerMessage);
            return true;
        }
        catch (OptimizationLeaseOwnershipChangedException ex)
        {
            _logger.LogWarning(ex, staleOwnerMessage, run.Id);
            return false;
        }
    }

    public Task<bool> HasOwnershipChangedAsync(
        DbContext writeDb,
        long runId,
        Guid expectedLeaseToken,
        CancellationToken ct)
        => _leaseManager.HasLeaseOwnershipChangedAsync(writeDb, runId, expectedLeaseToken, ct);

    public async Task SaveChangesOrThrowAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        OptimizationRun run,
        Guid expectedLeaseToken,
        CancellationToken ct,
        string staleOwnerMessage)
    {
        try
        {
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            if (!await _leaseManager.HasLeaseOwnershipChangedAsync(
                    writeDb,
                    run.Id,
                    expectedLeaseToken,
                    CancellationToken.None))
            {
                throw;
            }

            throw new OptimizationLeaseOwnershipChangedException(
                run.Id,
                staleOwnerMessage.Replace("{RunId}", run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ex);
        }
    }
}
