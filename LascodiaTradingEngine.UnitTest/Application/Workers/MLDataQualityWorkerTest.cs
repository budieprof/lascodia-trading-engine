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

public class MLDataQualityWorkerTest
{
    private static readonly DateTimeOffset FixedNow = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunCycleAsync_MissingCandlesAndLivePrice_CreatesReasonSpecificAlerts()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel("EURUSD", Timeframe.H1));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.PairsEvaluated);
        Assert.Equal(2, result.IssuesDetected);
        Assert.Equal(2, result.AlertsDispatched);

        var alerts = await db.Set<Alert>().OrderBy(alert => alert.DeduplicationKey).ToListAsync();
        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, alert => alert.DeduplicationKey == "ml-data-quality:data_quality_missing_candles:EURUSD:H1");
        Assert.Contains(alerts, alert => alert.DeduplicationKey == "ml-data-quality:live_price_missing:EURUSD:symbol");
        Assert.All(alerts, alert =>
        {
            Assert.Equal(AlertType.DataQualityIssue, alert.AlertType);
            Assert.True(alert.IsActive);
            Assert.Equal(FixedNow.UtcDateTime, alert.LastTriggeredAt);
        });

        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunCycleAsync_GapSpikeAndStaleLivePrice_DispatchesIndependentAlerts()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel("GBPUSD", Timeframe.H1));
        db.Set<Candle>().AddRange(GenerateCandles(
            "GBPUSD",
            Timeframe.H1,
            24,
            latestTimestamp: FixedNow.UtcDateTime.AddHours(-3),
            latestClose: 1.3000m));
        db.Set<LivePrice>().Add(new LivePrice
        {
            Symbol = "GBPUSD",
            Bid = 1.1000m,
            Ask = 1.1002m,
            Timestamp = FixedNow.UtcDateTime.AddMinutes(-10)
        });
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.PairsEvaluated);
        Assert.Equal(3, result.IssuesDetected);
        Assert.Equal(3, result.AlertsDispatched);

        var dedupKeys = await db.Set<Alert>()
            .Select(alert => alert.DeduplicationKey)
            .ToListAsync();
        Assert.Contains("ml-data-quality:data_quality_gap:GBPUSD:H1", dedupKeys);
        Assert.Contains("ml-data-quality:data_quality_spike:GBPUSD:H1", dedupKeys);
        Assert.Contains("ml-data-quality:live_price_stale:GBPUSD:symbol", dedupKeys);
    }

    [Fact]
    public async Task RunCycleAsync_ExistingAlertWithinCooldown_UpdatesButDoesNotDispatchAgain()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel("USDJPY", Timeframe.H1));
        db.Set<Candle>().AddRange(GenerateCandles(
            "USDJPY",
            Timeframe.H1,
            6,
            latestTimestamp: FixedNow.UtcDateTime.AddHours(-3),
            latestClose: 150.0002m,
            baselineBase: 150.0000m));
        db.Set<LivePrice>().Add(new LivePrice
        {
            Symbol = "USDJPY",
            Bid = 150.000m,
            Ask = 150.002m,
            Timestamp = FixedNow.UtcDateTime.AddSeconds(-30)
        });
        db.Set<Alert>().Add(new Alert
        {
            AlertType = AlertType.DataQualityIssue,
            Symbol = "USDJPY",
            ConditionJson = "{}",
            IsActive = true,
            Severity = AlertSeverity.Medium,
            DeduplicationKey = "ml-data-quality:data_quality_gap:USDJPY:H1",
            CooldownSeconds = 3_600,
            LastTriggeredAt = FixedNow.UtcDateTime.AddMinutes(-5)
        });
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, options: Options(alertCooldownSeconds: 3_600), dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.IssuesDetected);
        Assert.Equal(0, result.AlertsDispatched);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Equal(FixedNow.UtcDateTime.AddMinutes(-5), alert.LastTriggeredAt);

        using var condition = JsonDocument.Parse(alert.ConditionJson);
        Assert.Equal("data_quality_gap", condition.RootElement.GetProperty("reason").GetString());
        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_HealthyFeed_ResolvesWorkerOwnedStaleAlerts()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel("AUDUSD", Timeframe.H1));
        db.Set<Candle>().Add(CreateCandle(
            "AUDUSD",
            Timeframe.H1,
            FixedNow.UtcDateTime.AddHours(-1),
            close: 0.6650m));
        db.Set<LivePrice>().Add(new LivePrice
        {
            Symbol = "AUDUSD",
            Bid = 0.6650m,
            Ask = 0.6652m,
            Timestamp = FixedNow.UtcDateTime.AddSeconds(-20)
        });
        db.Set<Alert>().Add(new Alert
        {
            AlertType = AlertType.DataQualityIssue,
            Symbol = "AUDUSD",
            ConditionJson = "{}",
            IsActive = true,
            Severity = AlertSeverity.Medium,
            DeduplicationKey = "ml-data-quality:live_price_stale:AUDUSD:symbol",
            CooldownSeconds = 300,
            LastTriggeredAt = FixedNow.UtcDateTime.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.IssuesDetected);
        Assert.Equal(1, result.AlertsResolved);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.Equal(FixedNow.UtcDateTime, alert.AutoResolvedAt);
        dispatcher.Verify(
            d => d.TryAutoResolveAsync(It.IsAny<Alert>(), false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLDataQualityWorker CreateWorker(
        DbContext db,
        MLDataQualityOptions? options = null,
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

        return new MLDataQualityWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLDataQualityWorker>>(),
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

    private static MLDataQualityOptions Options(int alertCooldownSeconds = 0)
        => new()
        {
            InitialDelaySeconds = 0,
            PollIntervalSeconds = 300,
            PollJitterSeconds = 0,
            GapMultiplier = 2.5,
            SpikeSigmas = 4.0,
            SpikeLookbackBars = 20,
            MinSpikeBaselineBars = 3,
            LivePriceStalenessSeconds = 300,
            FutureTimestampToleranceSeconds = 60,
            MaxPairsPerCycle = 100,
            LockTimeoutSeconds = 1,
            AlertCooldownSeconds = alertCooldownSeconds,
            AlertDestination = "market-data"
        };

    private static MLModel CreateModel(string symbol, Timeframe timeframe)
        => new()
        {
            Id = Random.Shared.Next(1, int.MaxValue),
            Symbol = symbol,
            Timeframe = timeframe,
            IsActive = true,
            IsDeleted = false
        };

    private static List<Candle> GenerateCandles(
        string symbol,
        Timeframe timeframe,
        int count,
        DateTime latestTimestamp,
        decimal latestClose,
        decimal baselineBase = 1.1000m)
    {
        var candles = new List<Candle>(count)
        {
            CreateCandle(symbol, timeframe, latestTimestamp, latestClose)
        };

        for (var i = 1; i < count; i++)
        {
            var close = baselineBase + (i % 5) * 0.0001m;
            candles.Add(CreateCandle(
                symbol,
                timeframe,
                latestTimestamp.AddHours(-i),
                close));
        }

        return candles;
    }

    private static Candle CreateCandle(
        string symbol,
        Timeframe timeframe,
        DateTime timestamp,
        decimal close)
        => new()
        {
            Symbol = symbol,
            Timeframe = timeframe,
            Timestamp = timestamp,
            Open = close - 0.0001m,
            High = close + 0.0002m,
            Low = close - 0.0002m,
            Close = close,
            Volume = 100,
            IsClosed = true,
            IsDeleted = false
        };
}
