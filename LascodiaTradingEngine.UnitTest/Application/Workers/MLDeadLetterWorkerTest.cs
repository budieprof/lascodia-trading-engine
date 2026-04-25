using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLDeadLetterWorkerTest
{
    private static readonly DateTimeOffset FixedNow = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunCycleAsync_EligibleFailedRun_RequeuesAndIncrementsRetryCounter()
    {
        await using var db = CreateDbContext();
        db.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            Status = RunStatus.Failed,
            AttemptCount = 3,
            MaxAttempts = 3,
            StartedAt = FixedNow.UtcDateTime.AddDays(-10),
            CompletedAt = FixedNow.UtcDateTime.AddDays(-8),
            PickedUpAt = FixedNow.UtcDateTime.AddDays(-8).AddMinutes(-30),
            WorkerInstanceId = Guid.NewGuid(),
            TrainingDurationMs = 1_800_000,
            ErrorMessage = "out of memory"
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.CandidatesScanned);
        Assert.Equal(1, result.RunsRequeued);
        Assert.Equal(0, result.RetryCapsReached);

        var run = await db.Set<MLTrainingRun>().SingleAsync();
        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal(0, run.AttemptCount);
        Assert.Null(run.NextRetryAt);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.PickedUpAt);
        Assert.Null(run.WorkerInstanceId);
        Assert.Null(run.TrainingDurationMs);
        Assert.Equal(FixedNow.UtcDateTime, run.StartedAt);
        Assert.Contains("[DeadLetter retry 1/3", run.ErrorMessage);

        var counter = await db.Set<EngineConfig>()
            .SingleAsync(entry => entry.Key == "MLDeadLetter:EURUSD:H1:RetryCount");
        Assert.Equal("1", counter.Value);
        Assert.Equal(ConfigDataType.Int, counter.DataType);
    }

    [Fact]
    public async Task RunCycleAsync_SuccessSinceFailure_ResetsCounterAndResolvesRetryCapAlert()
    {
        await using var db = CreateDbContext();
        db.Set<MLTrainingRun>().AddRange(
            new MLTrainingRun
            {
                Symbol = "gbpusd",
                Timeframe = Timeframe.H1,
                Status = RunStatus.Failed,
                StartedAt = FixedNow.UtcDateTime.AddDays(-11),
                CompletedAt = FixedNow.UtcDateTime.AddDays(-10),
                ErrorMessage = "cuda unavailable"
            },
            new MLTrainingRun
            {
                Symbol = "GBPUSD",
                Timeframe = Timeframe.H1,
                Status = RunStatus.Completed,
                StartedAt = FixedNow.UtcDateTime.AddDays(-2),
                CompletedAt = FixedNow.UtcDateTime.AddDays(-1)
            });
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLDeadLetter:GBPUSD:H1:RetryCount",
            Value = "3",
            DataType = ConfigDataType.Int
        });
        db.Set<Alert>().Add(new Alert
        {
            AlertType = AlertType.MLModelDegraded,
            Symbol = "GBPUSD",
            ConditionJson = "{}",
            IsActive = true,
            Severity = AlertSeverity.Critical,
            DeduplicationKey = "ml-dead-letter:retry-cap:GBPUSD:H1",
            CooldownSeconds = 86_400,
            LastTriggeredAt = FixedNow.UtcDateTime.AddDays(-2)
        });
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.CandidatesScanned);
        Assert.Equal(1, result.RunsSkipped);
        Assert.Equal(0, result.RunsRequeued);
        Assert.Equal(1, result.AlertsResolved);
        Assert.Equal(1, result.RetryCountersReset);

        var failedRun = await db.Set<MLTrainingRun>().SingleAsync(run => run.Status == RunStatus.Failed);
        Assert.Equal("gbpusd", failedRun.Symbol);

        var counter = await db.Set<EngineConfig>()
            .SingleAsync(entry => entry.Key == "MLDeadLetter:GBPUSD:H1:RetryCount");
        Assert.Equal("0", counter.Value);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.Equal(FixedNow.UtcDateTime, alert.AutoResolvedAt);
        dispatcher.Verify(
            d => d.TryAutoResolveAsync(It.IsAny<Alert>(), false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_RetryCapReached_UpsertsAndDispatchesCriticalAlert()
    {
        await using var db = CreateDbContext();
        db.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol = "USDJPY",
            Timeframe = Timeframe.H1,
            Status = RunStatus.Failed,
            StartedAt = FixedNow.UtcDateTime.AddDays(-9),
            CompletedAt = FixedNow.UtcDateTime.AddDays(-8),
            ErrorMessage = "trainer crashed"
        });
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLDeadLetter:USDJPY:H1:RetryCount",
            Value = "3",
            DataType = ConfigDataType.Int
        });
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.CandidatesScanned);
        Assert.Equal(0, result.RunsRequeued);
        Assert.Equal(1, result.RetryCapsReached);
        Assert.Equal(1, result.AlertsDispatched);

        var run = await db.Set<MLTrainingRun>().SingleAsync();
        Assert.Equal(RunStatus.Failed, run.Status);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Equal(AlertType.MLModelDegraded, alert.AlertType);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("USDJPY", alert.Symbol);
        Assert.Equal("ml-dead-letter:retry-cap:USDJPY:H1", alert.DeduplicationKey);
        Assert.Equal(FixedNow.UtcDateTime, alert.LastTriggeredAt);

        using var condition = JsonDocument.Parse(alert.ConditionJson);
        Assert.Equal("dead_letter_retry_cap_exceeded", condition.RootElement.GetProperty("reason").GetString());
        Assert.Equal("ml-ops", condition.RootElement.GetProperty("destination").GetString());
        Assert.Equal(3, condition.RootElement.GetProperty("deadLetterRetries").GetInt32());
        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_MultipleFailuresSamePair_RequeuesOnlyNewestCandidate()
    {
        await using var db = CreateDbContext();
        var older = new MLTrainingRun
        {
            Symbol = "AUDUSD",
            Timeframe = Timeframe.H1,
            Status = RunStatus.Failed,
            StartedAt = FixedNow.UtcDateTime.AddDays(-14),
            CompletedAt = FixedNow.UtcDateTime.AddDays(-12),
            ErrorMessage = "older failure"
        };
        var newer = new MLTrainingRun
        {
            Symbol = "AUDUSD",
            Timeframe = Timeframe.H1,
            Status = RunStatus.Failed,
            StartedAt = FixedNow.UtcDateTime.AddDays(-10),
            CompletedAt = FixedNow.UtcDateTime.AddDays(-8),
            ErrorMessage = "newer failure"
        };
        db.Set<MLTrainingRun>().AddRange(older, newer);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.CandidatesScanned);
        Assert.Equal(1, result.RunsSkipped);
        Assert.Equal(1, result.RunsRequeued);

        Assert.Equal(RunStatus.Failed, older.Status);
        Assert.Equal(RunStatus.Queued, newer.Status);

        var counter = await db.Set<EngineConfig>()
            .SingleAsync(entry => entry.Key == "MLDeadLetter:AUDUSD:H1:RetryCount");
        Assert.Equal("1", counter.Value);
    }

    [Fact]
    public async Task RunCycleAsync_LockBusy_SkipsBeforeTouchingDatabase()
    {
        await using var db = CreateDbContext();
        db.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol = "NZDUSD",
            Timeframe = Timeframe.H1,
            Status = RunStatus.Failed,
            StartedAt = FixedNow.UtcDateTime.AddDays(-10),
            CompletedAt = FixedNow.UtcDateTime.AddDays(-8),
            ErrorMessage = "blocked"
        });
        await db.SaveChangesAsync();

        var distributedLock = new Mock<IDistributedLock>();
        distributedLock
            .Setup(locker => locker.TryAcquireAsync(
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var worker = CreateWorker(db, distributedLock: distributedLock.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(0, result.CandidatesScanned);
        Assert.Equal(RunStatus.Failed, (await db.Set<MLTrainingRun>().SingleAsync()).Status);
        Assert.Empty(await db.Set<EngineConfig>().ToListAsync());
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLDeadLetterWorker CreateWorker(
        DbContext db,
        MLDeadLetterOptions? options = null,
        IAlertDispatcher? dispatcher = null,
        IDistributedLock? distributedLock = null)
    {
        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(context => context.GetDbContext()).Returns(db);
        writeContext
            .Setup(context => context.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => db.SaveChangesAsync(ct));

        var services = new ServiceCollection();
        services.AddScoped(_ => writeContext.Object);
        if (dispatcher is not null)
            services.AddSingleton(dispatcher);

        var provider = services.BuildServiceProvider();

        return new MLDeadLetterWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLDeadLetterWorker>>(),
            options ?? Options(),
            metrics: null,
            timeProvider: new TestTimeProvider(FixedNow),
            healthMonitor: null,
            distributedLock: distributedLock);
    }

    private static Mock<IAlertDispatcher> CreateDispatcher()
    {
        var dispatcher = new Mock<IAlertDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(
                It.IsAny<Alert>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Alert, string, CancellationToken>((alert, _, _) =>
            {
                alert.LastTriggeredAt = FixedNow.UtcDateTime;
            })
            .Returns(Task.CompletedTask);

        dispatcher
            .Setup(d => d.TryAutoResolveAsync(
                It.IsAny<Alert>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<Alert, bool, CancellationToken>((alert, conditionStillActive, _) =>
            {
                if (!conditionStillActive)
                    alert.AutoResolvedAt = FixedNow.UtcDateTime;
            })
            .Returns(Task.CompletedTask);

        return dispatcher;
    }

    private static MLDeadLetterOptions Options()
        => new()
        {
            Enabled = true,
            InitialDelaySeconds = 0,
            PollIntervalSeconds = 3_600,
            PollJitterSeconds = 0,
            RetryAfterDays = 7,
            MaxRetries = 3,
            MaxRunsPerCycle = 100,
            MaxRequeuesPerCycle = 10,
            LockTimeoutSeconds = 1,
            AlertCooldownSeconds = 0,
            AlertDestination = "ml-ops"
        };
}
