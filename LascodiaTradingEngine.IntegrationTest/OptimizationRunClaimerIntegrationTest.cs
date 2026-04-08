using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

public class OptimizationRunClaimerIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public OptimizationRunClaimerIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ClaimNextRunAsync_ClaimsOldestQueuedRun_AndLeavesExecutionStartedUnset()
    {
        await ResetDatabaseAsync();

        await using var seedCtx = CreateWriteContext();
        long olderStrategyId = await SeedStrategyAsync(seedCtx, "OlderQueued");
        long newerStrategyId = await SeedStrategyAsync(seedCtx, "NewerQueued");

        var olderQueuedAt = new DateTime(2026, 04, 07, 9, 0, 0, DateTimeKind.Utc);
        var newerQueuedAt = olderQueuedAt.AddHours(2);

        var olderQueuedRun = new OptimizationRun
        {
            StrategyId = olderStrategyId,
            TriggerType = TriggerType.Scheduled,
            Status = OptimizationRunStatus.Queued,
            StartedAt = newerQueuedAt,
            QueuedAt = olderQueuedAt,
        };
        var newerQueuedRun = new OptimizationRun
        {
            StrategyId = newerStrategyId,
            TriggerType = TriggerType.Scheduled,
            Status = OptimizationRunStatus.Queued,
            StartedAt = olderQueuedAt,
            QueuedAt = newerQueuedAt,
        };

        seedCtx.Set<OptimizationRun>().AddRange(olderQueuedRun, newerQueuedRun);
        await seedCtx.SaveChangesAsync();

        await using var claimCtx = CreateWriteContext();
        var result = await OptimizationRunClaimer.ClaimNextRunAsync(
            claimCtx,
            maxConcurrentRuns: 1,
            leaseDuration: TimeSpan.FromMinutes(10),
            nowUtc: new DateTime(2026, 04, 08, 10, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Equal(olderQueuedRun.Id, result.RunId);

        await using var verifyCtx = CreateReadContext();
        var claimedRun = await verifyCtx.Set<OptimizationRun>().SingleAsync(r => r.Id == olderQueuedRun.Id);
        Assert.Equal(OptimizationRunStatus.Running, claimedRun.Status);
        Assert.NotNull(claimedRun.ClaimedAt);
        Assert.Null(claimedRun.ExecutionStartedAt);
    }

    [Fact]
    public async Task ClaimNextRunAsync_WhenCalledConcurrently_ClaimsDistinctRunsWithoutDuplicates()
    {
        await ResetDatabaseAsync();

        await using var seedCtx = CreateWriteContext();
        long strategyAId = await SeedStrategyAsync(seedCtx, "ConcurrentA");
        long strategyBId = await SeedStrategyAsync(seedCtx, "ConcurrentB");

        seedCtx.Set<OptimizationRun>().AddRange(
            new OptimizationRun
            {
                StrategyId = strategyAId,
                TriggerType = TriggerType.Scheduled,
                Status = OptimizationRunStatus.Queued,
                StartedAt = new DateTime(2026, 04, 08, 8, 0, 0, DateTimeKind.Utc),
                QueuedAt = new DateTime(2026, 04, 08, 8, 0, 0, DateTimeKind.Utc),
            },
            new OptimizationRun
            {
                StrategyId = strategyBId,
                TriggerType = TriggerType.Scheduled,
                Status = OptimizationRunStatus.Queued,
                StartedAt = new DateTime(2026, 04, 08, 8, 5, 0, DateTimeKind.Utc),
                QueuedAt = new DateTime(2026, 04, 08, 8, 5, 0, DateTimeKind.Utc),
            });
        await seedCtx.SaveChangesAsync();

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<long?> ClaimAsync()
        {
            await startGate.Task;
            await using var claimCtx = CreateWriteContext();
            var result = await OptimizationRunClaimer.ClaimNextRunAsync(
                claimCtx,
                maxConcurrentRuns: 2,
                leaseDuration: TimeSpan.FromMinutes(10),
                nowUtc: new DateTime(2026, 04, 08, 9, 0, 0, DateTimeKind.Utc),
                CancellationToken.None);
            return result.RunId;
        }

        var firstClaim = ClaimAsync();
        var secondClaim = ClaimAsync();
        startGate.TrySetResult();

        var claimedRunIds = await Task.WhenAll(firstClaim, secondClaim);

        Assert.All(claimedRunIds, id => Assert.NotNull(id));
        Assert.Equal(2, claimedRunIds.Distinct().Count());

        await using var verifyCtx = CreateReadContext();
        var runningCount = await verifyCtx.Set<OptimizationRun>()
            .CountAsync(r => r.Status == OptimizationRunStatus.Running);
        Assert.Equal(2, runningCount);
    }

    [Fact]
    public async Task RequeueExpiredRunsAsync_RequeuesActiveStrategyRun_AndClearsLeaseMetadata()
    {
        await ResetDatabaseAsync();

        await using var seedCtx = CreateWriteContext();
        long strategyId = await SeedStrategyAsync(seedCtx, "LeaseRecovery");
        var originalStartedAt = new DateTime(2026, 04, 01, 9, 0, 0, DateTimeKind.Utc);
        var originalQueuedAt = new DateTime(2026, 04, 02, 9, 0, 0, DateTimeKind.Utc);
        var expiredRun = new OptimizationRun
        {
            StrategyId = strategyId,
            TriggerType = TriggerType.Scheduled,
            Status = OptimizationRunStatus.Running,
            StartedAt = originalStartedAt,
            QueuedAt = originalQueuedAt,
            ClaimedAt = originalQueuedAt.AddMinutes(10),
            ExecutionStartedAt = originalQueuedAt.AddMinutes(11),
            LastHeartbeatAt = originalQueuedAt.AddMinutes(20),
            ExecutionLeaseExpiresAt = new DateTime(2026, 04, 08, 9, 0, 0, DateTimeKind.Utc),
            ExecutionLeaseToken = Guid.NewGuid(),
        };

        seedCtx.Set<OptimizationRun>().Add(expiredRun);
        await seedCtx.SaveChangesAsync();

        await using var writeCtx = CreateWriteContext();
        var nowUtc = new DateTime(2026, 04, 08, 10, 0, 0, DateTimeKind.Utc);
        var result = await OptimizationRunClaimer.RequeueExpiredRunsAsync(
            writeCtx,
            nowUtc,
            CancellationToken.None);

        Assert.Equal((1, 0), result);

        await using var verifyCtx = CreateReadContext();
        var recoveredRun = await verifyCtx.Set<OptimizationRun>().SingleAsync(r => r.Id == expiredRun.Id);
        Assert.Equal(OptimizationRunStatus.Queued, recoveredRun.Status);
        Assert.Equal(originalStartedAt, recoveredRun.StartedAt);
        Assert.Equal(nowUtc, recoveredRun.QueuedAt);
        Assert.Null(recoveredRun.ClaimedAt);
        Assert.Null(recoveredRun.ExecutionStartedAt);
        Assert.Null(recoveredRun.LastHeartbeatAt);
        Assert.Null(recoveredRun.ExecutionLeaseExpiresAt);
        Assert.Null(recoveredRun.ExecutionLeaseToken);
    }

    private WriteApplicationDbContext CreateWriteContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private ReadApplicationDbContext CreateReadContext()
    {
        var options = new DbContextOptionsBuilder<ReadApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new ReadApplicationDbContext(options, new HttpContextAccessor());
    }

    private async Task ResetDatabaseAsync()
    {
        await using var context = CreateWriteContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();
    }

    private static async Task<long> SeedStrategyAsync(WriteApplicationDbContext context, string name)
    {
        var strategy = new Strategy
        {
            Name = name,
            Description = $"{name} strategy",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":12,"Slow":26}""",
            Status = StrategyStatus.Active
        };
        context.Set<Strategy>().Add(strategy);
        await context.SaveChangesAsync();
        return strategy.Id;
    }
}
