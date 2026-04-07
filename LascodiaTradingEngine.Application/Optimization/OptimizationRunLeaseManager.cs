using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationRunLeaseManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OptimizationRunLeaseManager> _logger;

    public OptimizationRunLeaseManager(
        IServiceScopeFactory scopeFactory,
        ILogger<OptimizationRunLeaseManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    internal static bool HasLeaseOwnershipChanged(
        Guid expectedLeaseToken,
        OptimizationRunStatus currentStatus,
        Guid? currentLeaseToken)
        => currentStatus != OptimizationRunStatus.Running || currentLeaseToken != expectedLeaseToken;

    internal static TimeSpan GetHeartbeatInterval()
        => OptimizationExecutionLeasePolicy.GetHeartbeatInterval();

    internal async Task<bool> HasLeaseOwnershipChangedAsync(
        DbContext writeDb,
        long runId,
        Guid expectedLeaseToken,
        CancellationToken ct)
    {
        var current = await writeDb.Set<OptimizationRun>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.Status, r.ExecutionLeaseToken })
            .FirstOrDefaultAsync(ct);

        return current is null || HasLeaseOwnershipChanged(expectedLeaseToken, current.Status, current.ExecutionLeaseToken);
    }

    internal async Task MaintainExecutionLeaseAsync(
        long runId,
        Guid leaseToken,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(GetHeartbeatInterval());

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var db = writeCtx.GetDbContext();
                    var nowUtc = DateTime.UtcNow;

                    int updated = await db.Set<OptimizationRun>()
                        .Where(r => r.Id == runId
                                 && !r.IsDeleted
                                 && r.Status == OptimizationRunStatus.Running
                                 && r.ExecutionLeaseToken == leaseToken)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(r => r.LastHeartbeatAt, nowUtc)
                            .SetProperty(r => r.ExecutionLeaseExpiresAt, nowUtc.Add(OptimizationExecutionLeasePolicy.LeaseDuration)), ct);

                    if (updated == 0)
                        break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await using var issueScope = _scopeFactory.CreateAsyncScope();
                    var issueWriteCtx = issueScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var issueDb = issueWriteCtx.GetDbContext();
                    await issueDb.Set<OptimizationRun>()
                        .Where(r => r.Id == runId
                                 && !r.IsDeleted
                                 && r.Status == OptimizationRunStatus.Running
                                 && r.ExecutionLeaseToken == leaseToken)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(r => r.LastOperationalIssueCode, "LeaseHeartbeatFailed")
                            .SetProperty(r => r.LastOperationalIssueMessage, TruncateForPersistence(ex.Message, 500))
                            .SetProperty(r => r.LastOperationalIssueAt, DateTime.UtcNow), CancellationToken.None);
                    _logger.LogDebug(ex,
                        "OptimizationRunLeaseManager: background lease heartbeat failed for run {RunId} (non-fatal)",
                        runId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when the owning run completes or the worker shuts down.
        }
    }

    private static string? TruncateForPersistence(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
