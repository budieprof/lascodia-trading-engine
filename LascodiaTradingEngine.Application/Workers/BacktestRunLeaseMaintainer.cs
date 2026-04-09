using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

internal static class BacktestRunLeaseMaintainer
{
    internal static async Task MaintainExecutionLeaseAsync(
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        long runId,
        Guid leaseToken,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(BacktestExecutionLeasePolicy.GetHeartbeatInterval());

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var db = writeCtx.GetDbContext();
                    var nowUtc = DateTime.UtcNow;

                    int updated = await db.Set<BacktestRun>()
                        .Where(run => run.Id == runId
                                   && !run.IsDeleted
                                   && run.Status == RunStatus.Running
                                   && run.ExecutionLeaseToken == leaseToken)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(run => run.LastHeartbeatAt, nowUtc)
                            .SetProperty(run => run.ExecutionLeaseExpiresAt, nowUtc.Add(BacktestExecutionLeasePolicy.LeaseDuration)), ct);

                    if (updated == 0)
                        break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex,
                        "BacktestRunLeaseMaintainer: background lease heartbeat failed for run {RunId} (non-fatal)",
                        runId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
