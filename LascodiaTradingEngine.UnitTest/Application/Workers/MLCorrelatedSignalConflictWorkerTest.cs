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

public class MLCorrelatedSignalConflictWorkerTest
{
    private const string PairDeduplicationKey = "ml-correlated-signal-conflict:EURUSD:GBPUSD";

    [Fact]
    public async Task RunCycleAsync_ConflictingApprovedSignals_RejectsSafeSignalsAndCreatesAlert()
    {
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        await using var db = CreateDbContext();
        db.Set<TradeSignal>().AddRange(
            CreateSignal(1, "eurusd", TradeDirection.Buy, now.AddMinutes(-5)),
            CreateSignal(2, "GBPUSD", TradeDirection.Sell, now.AddMinutes(-4)));
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, now);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.ConflictsDetected);
        Assert.Equal(1, result.AlertsUpserted);
        Assert.Equal(2, result.SignalsRejected);

        var signals = await db.Set<TradeSignal>().OrderBy(signal => signal.Id).ToListAsync();
        Assert.All(signals, signal =>
        {
            Assert.Equal(TradeSignalStatus.Rejected, signal.Status);
            Assert.Contains("correlated approved ML signals conflict", signal.RejectionReason);
        });

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Equal(AlertType.MLModelDegraded, alert.AlertType);
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.Equal(PairDeduplicationKey, alert.DeduplicationKey);

        using var condition = JsonDocument.Parse(alert.ConditionJson);
        Assert.Equal("correlated_signal_conflict", condition.RootElement.GetProperty("reason").GetString());
        Assert.Equal(2, condition.RootElement.GetProperty("conflictingSignalCount").GetInt32());
    }

    [Fact]
    public async Task RunCycleAsync_SymmetricPairMap_DeduplicatesPairConflict()
    {
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        await using var db = CreateDbContext();
        db.Set<TradeSignal>().AddRange(
            CreateSignal(1, "EURUSD", TradeDirection.Buy, now.AddMinutes(-5)),
            CreateSignal(2, "GBPUSD", TradeDirection.Sell, now.AddMinutes(-4)));
        await db.SaveChangesAsync();

        var worker = CreateWorker(
            db,
            now,
            options: Options("""{"EURUSD":["GBPUSD"],"GBPUSD":["EURUSD"]}"""));
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.ConfiguredPairCount);
        Assert.Equal(1, result.ConflictsDetected);
        Assert.Single(await db.Set<Alert>().Where(alert => alert.DeduplicationKey == PairDeduplicationKey).ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_OrderLinkedSignal_StillRaisesConflictButOnlyRejectsUnlinkedSignal()
    {
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        await using var db = CreateDbContext();
        db.Set<TradeSignal>().AddRange(
            CreateSignal(1, "EURUSD", TradeDirection.Buy, now.AddMinutes(-5), orderId: 99),
            CreateSignal(2, "GBPUSD", TradeDirection.Sell, now.AddMinutes(-4)));
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, now);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.ConflictsDetected);
        Assert.Equal(1, result.SignalsRejected);

        var orderLinked = await db.Set<TradeSignal>().SingleAsync(signal => signal.Id == 1);
        var unlinked = await db.Set<TradeSignal>().SingleAsync(signal => signal.Id == 2);
        Assert.Equal(TradeSignalStatus.Approved, orderLinked.Status);
        Assert.Equal(TradeSignalStatus.Rejected, unlinked.Status);
        Assert.True(await db.Set<Alert>().AnyAsync(alert => alert.DeduplicationKey == PairDeduplicationKey && alert.IsActive));
    }

    [Fact]
    public async Task RunCycleAsync_OlderStillApprovedSignal_IsNotHiddenByNewestSameDirectionSignal()
    {
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        await using var db = CreateDbContext();
        db.Set<TradeSignal>().AddRange(
            CreateSignal(1, "EURUSD", TradeDirection.Sell, now.AddMinutes(-12)),
            CreateSignal(2, "EURUSD", TradeDirection.Buy, now.AddMinutes(-2)),
            CreateSignal(3, "GBPUSD", TradeDirection.Buy, now.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, now);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.ConflictsDetected);
        Assert.Equal(2, result.SignalsRejected);
        Assert.Equal(TradeSignalStatus.Rejected, (await db.Set<TradeSignal>().FindAsync(1L))!.Status);
        Assert.Equal(TradeSignalStatus.Approved, (await db.Set<TradeSignal>().FindAsync(2L))!.Status);
        Assert.Equal(TradeSignalStatus.Rejected, (await db.Set<TradeSignal>().FindAsync(3L))!.Status);
    }

    [Fact]
    public async Task RunCycleAsync_ResolvedConflict_AutoResolvesActivePairAlert()
    {
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        await using var db = CreateDbContext();
        db.Set<Alert>().Add(new Alert
        {
            AlertType = AlertType.MLModelDegraded,
            Severity = AlertSeverity.High,
            DeduplicationKey = PairDeduplicationKey,
            ConditionJson = "{}",
            IsActive = true
        });
        db.Set<TradeSignal>().AddRange(
            CreateSignal(1, "EURUSD", TradeDirection.Buy, now.AddMinutes(-5)),
            CreateSignal(2, "GBPUSD", TradeDirection.Buy, now.AddMinutes(-4)));
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, now);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.ConflictsDetected);
        Assert.Equal(1, result.AlertsResolved);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.Equal(now.UtcDateTime, alert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunCycleAsync_InvalidPairMap_SkipsWithoutMutatingAlerts()
    {
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        await using var db = CreateDbContext();
        db.Set<Alert>().Add(new Alert
        {
            AlertType = AlertType.MLModelDegraded,
            Severity = AlertSeverity.High,
            DeduplicationKey = PairDeduplicationKey,
            ConditionJson = "{}",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, now, options: Options("{bad-json"));
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("invalid_pair_map", result.SkippedReason);
        Assert.True((await db.Set<Alert>().SingleAsync()).IsActive);
    }

    [Fact]
    public async Task RunCycleAsync_DistributedLockBusy_SkipsWithoutProcessing()
    {
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        await using var db = CreateDbContext();
        db.Set<TradeSignal>().AddRange(
            CreateSignal(1, "EURUSD", TradeDirection.Buy, now.AddMinutes(-5)),
            CreateSignal(2, "GBPUSD", TradeDirection.Sell, now.AddMinutes(-4)));
        await db.SaveChangesAsync();

        var distributedLock = new Mock<IDistributedLock>();
        distributedLock
            .Setup(locker => locker.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var worker = CreateWorker(db, now, distributedLock.Object);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
        Assert.All(await db.Set<TradeSignal>().ToListAsync(), signal => Assert.Equal(TradeSignalStatus.Approved, signal.Status));
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLCorrelatedSignalConflictWorker CreateWorker(
        DbContext db,
        DateTimeOffset now,
        IDistributedLock? distributedLock = null,
        MLCorrelatedSignalConflictOptions? options = null)
    {
        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(context => context.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => writeContext.Object);
        var provider = services.BuildServiceProvider();

        return new MLCorrelatedSignalConflictWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLCorrelatedSignalConflictWorker>>(),
            options ?? Options(),
            metrics: null,
            timeProvider: new TestTimeProvider(now),
            healthMonitor: null,
            distributedLock: distributedLock);
    }

    private static MLCorrelatedSignalConflictOptions Options(string pairMapJson = """{"EURUSD":["GBPUSD"]}""")
        => new()
        {
            InitialDelaySeconds = 0,
            PollIntervalSeconds = 300,
            PollJitterSeconds = 0,
            WindowMinutes = 60,
            PairMapJson = pairMapJson,
            MaxSignalsPerCycle = 100,
            AlertCooldownSeconds = 1_800,
            RejectConflictingApprovedSignals = true
        };

    private static TradeSignal CreateSignal(
        long id,
        string symbol,
        TradeDirection direction,
        DateTimeOffset generatedAt,
        long? orderId = null)
        => new()
        {
            Id = id,
            StrategyId = 1,
            Symbol = symbol,
            Direction = direction,
            MLPredictedDirection = direction,
            MLConfidenceScore = 0.80m,
            Status = TradeSignalStatus.Approved,
            OrderId = orderId,
            EntryPrice = 1.1000m,
            SuggestedLotSize = 0.10m,
            Confidence = 0.75m,
            GeneratedAt = generatedAt.UtcDateTime,
            ExpiresAt = generatedAt.AddMinutes(30).UtcDateTime
        };
}
