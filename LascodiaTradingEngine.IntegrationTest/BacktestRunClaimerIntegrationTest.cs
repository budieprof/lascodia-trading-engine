using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

public class BacktestRunClaimerIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public BacktestRunClaimerIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ClaimNextRunAsync_ClaimsOldestEligibleQueuedRun_AndSkipsFutureQueuedRetry()
    {
        await ResetDatabaseAsync();

        await using var seedCtx = CreateWriteContext();
        long eligibleStrategyId = await SeedStrategyAsync(seedCtx, "EligibleBacktest", "EURUSD");
        long delayedStrategyId = await SeedStrategyAsync(seedCtx, "DelayedBacktest", "GBPUSD");

        var nowUtc = new DateTime(2026, 04, 09, 10, 0, 0, DateTimeKind.Utc);
        var eligibleRun = new BacktestRun
        {
            StrategyId = eligibleStrategyId,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            FromDate = nowUtc.AddDays(-30),
            ToDate = nowUtc.AddDays(-1),
            InitialBalance = 10_000m,
            Status = RunStatus.Queued,
            CreatedAt = nowUtc.AddHours(-2),
            QueuedAt = nowUtc.AddMinutes(-5),
            AvailableAt = nowUtc.AddMinutes(-5),
        };
        var delayedRun = new BacktestRun
        {
            StrategyId = delayedStrategyId,
            Symbol = "GBPUSD",
            Timeframe = Timeframe.H1,
            FromDate = nowUtc.AddDays(-30),
            ToDate = nowUtc.AddDays(-1),
            InitialBalance = 10_000m,
            Status = RunStatus.Queued,
            CreatedAt = nowUtc.AddHours(-1),
            QueuedAt = nowUtc.AddMinutes(15),
            AvailableAt = nowUtc.AddMinutes(15),
            RetryCount = 1,
        };

        seedCtx.Set<BacktestRun>().AddRange(eligibleRun, delayedRun);
        await seedCtx.SaveChangesAsync();

        await using var claimCtx = CreateWriteContext();
        var result = await BacktestRunClaimer.ClaimNextRunAsync(claimCtx, nowUtc, "itest-backtest-claimer", CancellationToken.None);

        Assert.Equal(eligibleRun.Id, result.RunId);

        await using var verifyCtx = CreateReadContext();
        var claimedRun = await verifyCtx.Set<BacktestRun>().SingleAsync(r => r.Id == eligibleRun.Id);
        var untouchedRun = await verifyCtx.Set<BacktestRun>().SingleAsync(r => r.Id == delayedRun.Id);

        Assert.Equal(RunStatus.Running, claimedRun.Status);
        Assert.Equal(RunStatus.Queued, untouchedRun.Status);
        Assert.Equal(delayedRun.QueuedAt, untouchedRun.QueuedAt);
    }

    [Fact]
    public async Task ClaimNextRunAsync_WhenCalledConcurrently_ClaimsDistinctRunsWithoutDuplicates()
    {
        await ResetDatabaseAsync();

        await using var seedCtx = CreateWriteContext();
        long strategyAId = await SeedStrategyAsync(seedCtx, "BacktestConcurrentA", "EURUSD");
        long strategyBId = await SeedStrategyAsync(seedCtx, "BacktestConcurrentB", "GBPUSD");

        seedCtx.Set<BacktestRun>().AddRange(
            new BacktestRun
            {
                StrategyId = strategyAId,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                FromDate = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc),
                ToDate = new DateTime(2026, 04, 01, 0, 0, 0, DateTimeKind.Utc),
                InitialBalance = 10_000m,
                Status = RunStatus.Queued,
                CreatedAt = new DateTime(2026, 04, 08, 8, 0, 0, DateTimeKind.Utc),
                QueuedAt = new DateTime(2026, 04, 08, 8, 0, 0, DateTimeKind.Utc),
                AvailableAt = new DateTime(2026, 04, 08, 8, 0, 0, DateTimeKind.Utc),
            },
            new BacktestRun
            {
                StrategyId = strategyBId,
                Symbol = "GBPUSD",
                Timeframe = Timeframe.H1,
                FromDate = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc),
                ToDate = new DateTime(2026, 04, 01, 0, 0, 0, DateTimeKind.Utc),
                InitialBalance = 10_000m,
                Status = RunStatus.Queued,
                CreatedAt = new DateTime(2026, 04, 08, 8, 5, 0, DateTimeKind.Utc),
                QueuedAt = new DateTime(2026, 04, 08, 8, 5, 0, DateTimeKind.Utc),
                AvailableAt = new DateTime(2026, 04, 08, 8, 5, 0, DateTimeKind.Utc),
            });
        await seedCtx.SaveChangesAsync();

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<long?> ClaimAsync()
        {
            await startGate.Task;
            await using var claimCtx = CreateWriteContext();
            var result = await BacktestRunClaimer.ClaimNextRunAsync(
                claimCtx,
                new DateTime(2026, 04, 09, 10, 0, 0, DateTimeKind.Utc),
                "itest-backtest-concurrent",
                CancellationToken.None);
            return result.RunId;
        }

        var firstClaim = ClaimAsync();
        var secondClaim = ClaimAsync();
        startGate.TrySetResult();

        var claimedRunIds = await Task.WhenAll(firstClaim, secondClaim);

        Assert.All(claimedRunIds, id => Assert.NotNull(id));
        Assert.Equal(2, claimedRunIds.Distinct().Count());
    }

    [Fact]
    public async Task RequeueExpiredRunsAsync_RequeuesActiveStrategyRun_AndClearsLeaseMetadata()
    {
        await ResetDatabaseAsync();

        await using var seedCtx = CreateWriteContext();
        long strategyId = await SeedStrategyAsync(seedCtx, "BacktestLeaseRecovery", "EURUSD");
        var nowUtc = new DateTime(2026, 04, 09, 10, 0, 0, DateTimeKind.Utc);
        var expiredRun = new BacktestRun
        {
            StrategyId = strategyId,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            FromDate = nowUtc.AddDays(-30),
            ToDate = nowUtc.AddDays(-1),
            InitialBalance = 10_000m,
            Status = RunStatus.Running,
            CreatedAt = nowUtc.AddDays(-2),
            QueuedAt = nowUtc.AddDays(-2),
            ClaimedAt = nowUtc.AddDays(-1),
            ExecutionStartedAt = nowUtc.AddDays(-1).AddMinutes(1),
            LastHeartbeatAt = nowUtc.AddMinutes(-30),
            ExecutionLeaseExpiresAt = nowUtc.AddMinutes(-1),
            ExecutionLeaseToken = Guid.NewGuid(),
        };

        seedCtx.Set<BacktestRun>().Add(expiredRun);
        await seedCtx.SaveChangesAsync();

        await using var writeCtx = CreateWriteContext();
        var result = await BacktestRunClaimer.RequeueExpiredRunsAsync(writeCtx, nowUtc, CancellationToken.None);

        Assert.Equal((1, 0), result);

        await using var verifyCtx = CreateReadContext();
        var recoveredRun = await verifyCtx.Set<BacktestRun>().SingleAsync(r => r.Id == expiredRun.Id);
        Assert.Equal(RunStatus.Queued, recoveredRun.Status);
        Assert.Equal(nowUtc, recoveredRun.QueuedAt);
        Assert.Null(recoveredRun.ClaimedAt);
        Assert.Null(recoveredRun.ExecutionStartedAt);
        Assert.Null(recoveredRun.LastHeartbeatAt);
        Assert.Null(recoveredRun.ExecutionLeaseExpiresAt);
        Assert.Null(recoveredRun.ExecutionLeaseToken);
    }

    [Fact]
    public async Task RequeueExpiredRunsAsync_FailsDeletedStrategyRun_WithStructuredFailureCode()
    {
        await ResetDatabaseAsync();

        await using var seedCtx = CreateWriteContext();
        var deletedStrategy = new Strategy
        {
            Name = "Deleted backtest strategy",
            Description = "deleted",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = "{}",
            Status = StrategyStatus.Active,
            IsDeleted = true,
        };
        seedCtx.Set<Strategy>().Add(deletedStrategy);
        await seedCtx.SaveChangesAsync();

        var nowUtc = new DateTime(2026, 04, 09, 10, 0, 0, DateTimeKind.Utc);
        var orphanedRun = new BacktestRun
        {
            StrategyId = deletedStrategy.Id,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            FromDate = nowUtc.AddDays(-30),
            ToDate = nowUtc.AddDays(-1),
            InitialBalance = 10_000m,
            Status = RunStatus.Running,
            CreatedAt = nowUtc.AddDays(-2),
            QueuedAt = nowUtc.AddDays(-2),
            ExecutionLeaseExpiresAt = nowUtc.AddMinutes(-1),
            ExecutionLeaseToken = Guid.NewGuid(),
        };
        seedCtx.Set<BacktestRun>().Add(orphanedRun);
        await seedCtx.SaveChangesAsync();

        await using var writeCtx = CreateWriteContext();
        var result = await BacktestRunClaimer.RequeueExpiredRunsAsync(writeCtx, nowUtc, CancellationToken.None);

        Assert.Equal((0, 1), result);

        await using var verifyCtx = CreateReadContext();
        var failedRun = await verifyCtx.Set<BacktestRun>().SingleAsync(r => r.Id == orphanedRun.Id);
        Assert.Equal(RunStatus.Failed, failedRun.Status);
        Assert.Equal(ValidationRunFailureCodes.StrategyDeleted, failedRun.FailureCode);
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

    private static async Task<long> SeedStrategyAsync(WriteApplicationDbContext context, string name, string symbol)
    {
        var strategy = new Strategy
        {
            Name = name,
            Description = $"{name} strategy",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = symbol,
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":12,"Slow":26}""",
            Status = StrategyStatus.Active
        };
        context.Set<Strategy>().Add(strategy);
        await context.SaveChangesAsync();
        return strategy.Id;
    }
}
