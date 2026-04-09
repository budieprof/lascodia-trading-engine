using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

internal sealed class InMemoryBacktestRunClaimService : IBacktestRunClaimService
{
    public void EnsureSupportedProvider(DbContext writeDb)
    {
    }

    public Task<BacktestRunClaim> ClaimNextRunAsync(
        DbContext writeDb,
        DateTime nowUtc,
        string workerId,
        CancellationToken ct)
    {
        var candidate = writeDb.Set<BacktestRun>()
            .AsQueryable()
            .Where(run => run.Status == RunStatus.Queued && !run.IsDeleted && run.AvailableAt <= nowUtc)
            .OrderByDescending(run => run.Priority)
            .ThenBy(run => run.QueuedAt)
            .ThenBy(run => run.Id)
            .FirstOrDefault();

        if (candidate == null)
            return Task.FromResult(new BacktestRunClaim(null, Guid.Empty));

        var leaseToken = Guid.NewGuid();
        candidate.Status = RunStatus.Running;
        candidate.ClaimedAt = nowUtc;
        candidate.ClaimedByWorkerId = workerId;
        candidate.LastAttemptAt = nowUtc;
        candidate.LastHeartbeatAt = nowUtc;
        candidate.ExecutionLeaseExpiresAt = nowUtc.AddMinutes(5);
        candidate.ExecutionLeaseToken = leaseToken;

        return Task.FromResult(new BacktestRunClaim(candidate.Id, leaseToken));
    }

    public Task<(int Requeued, int Orphaned)> RequeueExpiredRunsAsync(
        DbContext writeDb,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var activeStrategyIds = writeDb.Set<Strategy>()
            .AsQueryable()
            .Where(strategy => !strategy.IsDeleted)
            .Select(strategy => strategy.Id)
            .ToHashSet();

        int requeued = 0;
        int orphaned = 0;
        foreach (var run in writeDb.Set<BacktestRun>()
                     .AsQueryable()
                     .Where(run => run.Status == RunStatus.Running
                                && !run.IsDeleted
                                && run.ExecutionLeaseExpiresAt != null
                                && run.ExecutionLeaseExpiresAt < nowUtc)
                     .ToList())
        {
            if (activeStrategyIds.Contains(run.StrategyId))
            {
                run.Status = RunStatus.Queued;
                run.QueuedAt = nowUtc;
                run.AvailableAt = nowUtc;
                run.ClaimedAt = null;
                run.ClaimedByWorkerId = null;
                run.ExecutionStartedAt = null;
                run.CompletedAt = null;
                run.ErrorMessage = null;
                run.FailureCode = null;
                run.FailureDetailsJson = null;
                run.LastAttemptAt = null;
                run.LastHeartbeatAt = null;
                run.ExecutionLeaseExpiresAt = null;
                run.ExecutionLeaseToken = null;
                run.ResultJson = null;
                run.TotalTrades = null;
                run.WinRate = null;
                run.ProfitFactor = null;
                run.MaxDrawdownPct = null;
                run.SharpeRatio = null;
                run.FinalBalance = null;
                run.TotalReturn = null;
                requeued++;
            }
            else
            {
                run.Status = RunStatus.Failed;
                run.CompletedAt = nowUtc;
                run.ErrorMessage = "Strategy deleted during backtest run";
                run.FailureCode = ValidationRunFailureCodes.StrategyDeleted;
                run.FailureDetailsJson = ValidationRunException.SerializeDetails(new
                {
                    run.Id,
                    run.StrategyId
                });
                run.ClaimedByWorkerId = null;
                run.ExecutionLeaseExpiresAt = null;
                run.ExecutionLeaseToken = null;
                orphaned++;
            }
        }

        return Task.FromResult((requeued, orphaned));
    }
}

internal sealed class InMemoryWalkForwardRunClaimService : IWalkForwardRunClaimService
{
    public void EnsureSupportedProvider(DbContext writeDb)
    {
    }

    public Task<WalkForwardRunClaim> ClaimNextRunAsync(
        DbContext writeDb,
        DateTime nowUtc,
        string workerId,
        CancellationToken ct)
    {
        var candidate = writeDb.Set<WalkForwardRun>()
            .AsQueryable()
            .Where(run => run.Status == RunStatus.Queued && !run.IsDeleted && run.AvailableAt <= nowUtc)
            .OrderByDescending(run => run.Priority)
            .ThenBy(run => run.QueuedAt)
            .ThenBy(run => run.Id)
            .FirstOrDefault();

        if (candidate == null)
            return Task.FromResult(new WalkForwardRunClaim(null, Guid.Empty));

        var leaseToken = Guid.NewGuid();
        candidate.Status = RunStatus.Running;
        candidate.ClaimedAt = nowUtc;
        candidate.ClaimedByWorkerId = workerId;
        candidate.LastAttemptAt = nowUtc;
        candidate.LastHeartbeatAt = nowUtc;
        candidate.ExecutionLeaseExpiresAt = nowUtc.AddMinutes(5);
        candidate.ExecutionLeaseToken = leaseToken;

        return Task.FromResult(new WalkForwardRunClaim(candidate.Id, leaseToken));
    }

    public Task<(int Requeued, int Orphaned)> RequeueExpiredRunsAsync(
        DbContext writeDb,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var activeStrategyIds = writeDb.Set<Strategy>()
            .AsQueryable()
            .Where(strategy => !strategy.IsDeleted)
            .Select(strategy => strategy.Id)
            .ToHashSet();

        int requeued = 0;
        int orphaned = 0;
        foreach (var run in writeDb.Set<WalkForwardRun>()
                     .AsQueryable()
                     .Where(run => run.Status == RunStatus.Running
                                && !run.IsDeleted
                                && run.ExecutionLeaseExpiresAt != null
                                && run.ExecutionLeaseExpiresAt < nowUtc)
                     .ToList())
        {
            if (activeStrategyIds.Contains(run.StrategyId))
            {
                run.Status = RunStatus.Queued;
                run.QueuedAt = nowUtc;
                run.AvailableAt = nowUtc;
                run.ClaimedAt = null;
                run.ClaimedByWorkerId = null;
                run.ExecutionStartedAt = null;
                run.CompletedAt = null;
                run.ErrorMessage = null;
                run.FailureCode = null;
                run.FailureDetailsJson = null;
                run.LastAttemptAt = null;
                run.LastHeartbeatAt = null;
                run.ExecutionLeaseExpiresAt = null;
                run.ExecutionLeaseToken = null;
                run.AverageOutOfSampleScore = null;
                run.ScoreConsistency = null;
                run.WindowResultsJson = null;
                requeued++;
            }
            else
            {
                run.Status = RunStatus.Failed;
                run.CompletedAt = nowUtc;
                run.ErrorMessage = "Strategy deleted during walk-forward run";
                run.FailureCode = ValidationRunFailureCodes.StrategyDeleted;
                run.FailureDetailsJson = ValidationRunException.SerializeDetails(new
                {
                    run.Id,
                    run.StrategyId
                });
                run.ClaimedByWorkerId = null;
                run.ExecutionLeaseExpiresAt = null;
                run.ExecutionLeaseToken = null;
                orphaned++;
            }
        }

        return Task.FromResult((requeued, orphaned));
    }
}
