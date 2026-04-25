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

public class MLDirectionStreakWorkerTest
{
    private static readonly DateTimeOffset FixedNow = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunCycleAsync_SevereDirectionLock_CreatesAlertDispatchesAndQueuesRetrain()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel(101, "eurusd", Timeframe.H1));
        db.Set<MLModelPredictionLog>().AddRange(CreatePredictions(
            101,
            "EURUSD",
            Timeframe.H1,
            Enumerable.Repeat(TradeDirection.Buy, 30).ToArray()));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(1, result.StreaksDetected);
        Assert.Equal(1, result.SevereStreaks);
        Assert.Equal(1, result.AlertsDispatched);
        Assert.Equal(1, result.RetrainsQueued);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Equal(AlertType.MLModelDegraded, alert.AlertType);
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.Equal("EURUSD", alert.Symbol);
        Assert.Equal("ml-direction-streak:EURUSD:H1:101", alert.DeduplicationKey);
        Assert.Equal(FixedNow.UtcDateTime, alert.LastTriggeredAt);

        using var condition = JsonDocument.Parse(alert.ConditionJson);
        Assert.Equal("direction_streak", condition.RootElement.GetProperty("reason").GetString());
        Assert.Equal("ml-ops", condition.RootElement.GetProperty("destination").GetString());
        Assert.Equal("Buy", condition.RootElement.GetProperty("dominantDirection").GetString());
        Assert.Equal(3, condition.RootElement.GetProperty("testsFailedCount").GetInt32());

        var retrain = await db.Set<MLTrainingRun>().SingleAsync();
        Assert.Equal("EURUSD", retrain.Symbol);
        Assert.Equal(Timeframe.H1, retrain.Timeframe);
        Assert.Equal(RunStatus.Queued, retrain.Status);
        Assert.Equal(TriggerType.AutoDegrading, retrain.TriggerType);
        Assert.Equal(FixedNow.UtcDateTime.AddDays(-365), retrain.FromDate);
        Assert.Equal(FixedNow.UtcDateTime, retrain.ToDate);
        Assert.Contains("DirectionStreak", retrain.ErrorMessage);

        using var hyperparams = JsonDocument.Parse(retrain.HyperparamConfigJson!);
        Assert.Equal("MLDirectionStreakWorker", hyperparams.RootElement.GetProperty("triggeredBy").GetString());
        Assert.True(hyperparams.RootElement.GetProperty("classRebalance").GetBoolean());
        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_BalancedWindow_ResolvesExistingWorkerAlert()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel(102, "GBPUSD", Timeframe.H1));
        db.Set<MLModelPredictionLog>().AddRange(CreatePredictions(
            102,
            "GBPUSD",
            Timeframe.H1,
            AlternatingDirections(30)));
        db.Set<Alert>().Add(CreateAlert("GBPUSD", Timeframe.H1, 102));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(0, result.StreaksDetected);
        Assert.Equal(1, result.AlertsResolved);
        Assert.Empty(await db.Set<MLTrainingRun>().ToListAsync());

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.Equal(FixedNow.UtcDateTime, alert.AutoResolvedAt);
        dispatcher.Verify(
            d => d.TryAutoResolveAsync(It.IsAny<Alert>(), false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_ExistingAlertWithinCooldown_UpdatesButDoesNotDispatchAgain()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel(103, "USDJPY", Timeframe.H1));
        db.Set<MLModelPredictionLog>().AddRange(CreatePredictions(
            103,
            "USDJPY",
            Timeframe.H1,
            Enumerable.Repeat(TradeDirection.Sell, 30).ToArray()));
        db.Set<Alert>().Add(CreateAlert(
            "USDJPY",
            Timeframe.H1,
            103,
            lastTriggeredAt: FixedNow.UtcDateTime.AddMinutes(-5)));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, options: Options(alertCooldownSeconds: 3_600), dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.StreaksDetected);
        Assert.Equal(1, result.AlertsSuppressedByCooldown);
        Assert.Equal(0, result.AlertsDispatched);
        Assert.Equal(1, result.RetrainsQueued);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Equal(FixedNow.UtcDateTime.AddMinutes(-5), alert.LastTriggeredAt);

        using var condition = JsonDocument.Parse(alert.ConditionJson);
        Assert.Equal("Sell", condition.RootElement.GetProperty("dominantDirection").GetString());
        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_InsufficientPredictions_SkipsWithoutAlertOrRetrain()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel(104, "AUDUSD", Timeframe.H1));
        db.Set<MLModelPredictionLog>().AddRange(CreatePredictions(
            104,
            "AUDUSD",
            Timeframe.H1,
            Enumerable.Repeat(TradeDirection.Buy, 10).ToArray()));
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(1, result.ModelsSkipped);
        Assert.Equal(0, result.StreaksDetected);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
        Assert.Empty(await db.Set<MLTrainingRun>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_ModelNoLongerEligible_ResolvesStaleDirectionStreakAlert()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel(105, "NZDUSD", Timeframe.H1, hasModelBytes: false));
        db.Set<Alert>().Add(CreateAlert("NZDUSD", Timeframe.H1, 105));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("no_active_models", result.SkippedReason);
        Assert.Equal(0, result.ModelsEvaluated);
        Assert.Equal(1, result.ModelsSkipped);
        Assert.Equal(1, result.AlertsResolved);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.Equal(FixedNow.UtcDateTime, alert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunCycleAsync_LockBusy_SkipsWithoutMutatingDatabase()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel(106, "CADJPY", Timeframe.H1));
        db.Set<MLModelPredictionLog>().AddRange(CreatePredictions(
            106,
            "CADJPY",
            Timeframe.H1,
            Enumerable.Repeat(TradeDirection.Buy, 30).ToArray()));
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
        Assert.Equal(0, result.ModelsEvaluated);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
        Assert.Empty(await db.Set<MLTrainingRun>().ToListAsync());
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLDirectionStreakWorker CreateWorker(
        DbContext db,
        MLDirectionStreakOptions? options = null,
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

        return new MLDirectionStreakWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLDirectionStreakWorker>>(),
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

    private static MLDirectionStreakOptions Options(int alertCooldownSeconds = 0)
        => new()
        {
            Enabled = true,
            InitialDelaySeconds = 0,
            PollIntervalSeconds = 3_600,
            PollJitterSeconds = 0,
            WindowSize = 30,
            MaxSameDirectionFraction = 0.85,
            EntropyThreshold = 0.50,
            RunsZScoreThreshold = -2.0,
            LongestRunFraction = 0.60,
            MinFailedTestsToAlert = 2,
            MinFailedTestsToRetrain = 3,
            AutoQueueRetrain = true,
            RetrainLookbackDays = 365,
            MaxModelsPerCycle = 100,
            MaxRetrainsPerCycle = 25,
            AlertCooldownSeconds = alertCooldownSeconds,
            LockTimeoutSeconds = 1,
            AlertDestination = "ml-ops"
        };

    private static MLModel CreateModel(
        long id,
        string symbol,
        Timeframe timeframe,
        bool hasModelBytes = true)
        => new()
        {
            Id = id,
            Symbol = symbol,
            Timeframe = timeframe,
            Status = MLModelStatus.Active,
            IsActive = true,
            IsDeleted = false,
            ModelVersion = "1.0.0",
            ModelBytes = hasModelBytes ? new byte[] { 1, 2, 3 } : Array.Empty<byte>(),
            ExpectedValue = 1m,
            ActivatedAt = FixedNow.UtcDateTime.AddDays(-1)
        };

    private static IEnumerable<MLModelPredictionLog> CreatePredictions(
        long modelId,
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<TradeDirection> directions)
    {
        for (var i = 0; i < directions.Count; i++)
        {
            yield return new MLModelPredictionLog
            {
                TradeSignalId = modelId * 1_000 + i + 1,
                MLModelId = modelId,
                Symbol = symbol,
                Timeframe = timeframe,
                ModelRole = ModelRole.Champion,
                PredictedDirection = directions[i],
                PredictedMagnitudePips = 10m,
                ConfidenceScore = 0.80m,
                PredictedAt = FixedNow.UtcDateTime.AddMinutes(-i)
            };
        }
    }

    private static TradeDirection[] AlternatingDirections(int count)
    {
        var directions = new TradeDirection[count];
        for (var i = 0; i < count; i++)
            directions[i] = i % 2 == 0 ? TradeDirection.Buy : TradeDirection.Sell;

        return directions;
    }

    private static Alert CreateAlert(
        string symbol,
        Timeframe timeframe,
        long modelId,
        DateTime? lastTriggeredAt = null)
        => new()
        {
            AlertType = AlertType.MLModelDegraded,
            Symbol = symbol,
            Severity = AlertSeverity.High,
            DeduplicationKey = $"ml-direction-streak:{symbol}:{timeframe}:{modelId}",
            ConditionJson = "{}",
            IsActive = true,
            CooldownSeconds = 3_600,
            LastTriggeredAt = lastTriggeredAt
        };
}
