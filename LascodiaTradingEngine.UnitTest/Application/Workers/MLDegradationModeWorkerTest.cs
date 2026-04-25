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

public class MLDegradationModeWorkerTest
{
    private static readonly DateTimeOffset FixedNow = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunCycleAsync_NoRoutableModels_SetsFlagAndDispatchesActivatedAlert()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel("eurusd", Timeframe.H1, isActive: false));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.SymbolsEvaluated);
        Assert.Equal(1, result.DegradedSymbols);
        Assert.Equal(1, result.NewlyDegraded);
        Assert.Equal(1, result.AlertsDispatched);

        var activeFlag = await db.Set<EngineConfig>()
            .SingleAsync(entry => entry.Key == "MLDegradation:EURUSD:Active");
        Assert.Equal("true", activeFlag.Value);
        Assert.Equal(ConfigDataType.Bool, activeFlag.DataType);

        var detectedAt = await db.Set<EngineConfig>()
            .SingleAsync(entry => entry.Key == "MLDegradation:EURUSD:DetectedAt");
        Assert.Equal(FixedNow.UtcDateTime, DateTime.Parse(detectedAt.Value).ToUniversalTime());

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Equal(AlertType.MLModelDegraded, alert.AlertType);
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.Equal("EURUSD", alert.Symbol);
        Assert.Equal("ml-degradation-mode:EURUSD:activated", alert.DeduplicationKey);
        Assert.Equal(FixedNow.UtcDateTime, alert.LastTriggeredAt);

        using var condition = JsonDocument.Parse(alert.ConditionJson);
        Assert.Equal("ml_degradation_mode_activated", condition.RootElement.GetProperty("reason").GetString());
        Assert.Equal("ml-ops", condition.RootElement.GetProperty("destination").GetString());
        Assert.Equal(0, condition.RootElement.GetProperty("routableModels").GetInt32());
        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_EmptyModelBytes_TreatsModelAsUnavailable()
    {
        await using var db = CreateDbContext();
        var model = CreateModel("CADJPY", Timeframe.H1);
        model.ModelBytes = Array.Empty<byte>();
        db.Set<MLModel>().Add(model);
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.SymbolsEvaluated);
        Assert.Equal(1, result.DegradedSymbols);
        Assert.Equal(1, result.NewlyDegraded);
        Assert.Equal(1, result.AlertsDispatched);
        Assert.Contains(
            await db.Set<Alert>().Select(alert => alert.DeduplicationKey).ToListAsync(),
            key => key == "ml-degradation-mode:CADJPY:activated");
    }

    [Fact]
    public async Task RunCycleAsync_DegradedBeyondThresholds_DispatchesCriticalAndEscalationWithSpecificDedup()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel("GBPUSD", Timeframe.H1, isActive: false));
        db.Set<EngineConfig>().AddRange(
            Config("MLDegradation:GBPUSD:Active", "true", ConfigDataType.Bool),
            Config("MLDegradation:GBPUSD:DetectedAt", FixedNow.UtcDateTime.AddHours(-25).ToString("O")));
        db.Set<Alert>().Add(new Alert
        {
            AlertType = AlertType.MLModelDegraded,
            Symbol = "GBPUSD",
            ConditionJson = "{}",
            IsActive = true,
            Severity = AlertSeverity.Critical,
            DeduplicationKey = "other-worker:GBPUSD",
            LastTriggeredAt = FixedNow.UtcDateTime.AddHours(-3)
        });
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.SymbolsEvaluated);
        Assert.Equal(1, result.DegradedSymbols);
        Assert.Equal(0, result.NewlyDegraded);
        Assert.Equal(2, result.AlertsDispatched);
        Assert.Equal(2, result.AlertsEscalated);

        var alerts = await db.Set<Alert>()
            .OrderBy(alert => alert.DeduplicationKey)
            .ToListAsync();
        Assert.Equal(3, alerts.Count);
        Assert.Contains(alerts, alert => alert.DeduplicationKey == "other-worker:GBPUSD" && alert.IsActive);
        Assert.Contains(alerts, alert => alert.DeduplicationKey == "ml-degradation-mode:GBPUSD:critical");
        Assert.Contains(alerts, alert => alert.DeduplicationKey == "ml-degradation-mode:GBPUSD:escalated");
        Assert.All(
            alerts.Where(alert => alert.DeduplicationKey?.StartsWith("ml-degradation-mode:", StringComparison.Ordinal) == true),
            alert =>
            {
                Assert.Equal(AlertSeverity.Critical, alert.Severity);
                Assert.Equal(FixedNow.UtcDateTime, alert.LastTriggeredAt);
            });
        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunCycleAsync_HealthyModelClearsFlagAndResolvesWorkerAlerts()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel("AUDUSD", Timeframe.H1));
        db.Set<EngineConfig>().AddRange(
            Config("MLDegradation:AUDUSD:Active", "true", ConfigDataType.Bool),
            Config("MLDegradation:AUDUSD:DetectedAt", FixedNow.UtcDateTime.AddHours(-2).ToString("O")));
        db.Set<Alert>().AddRange(
            CreateWorkerAlert("AUDUSD", "activated"),
            CreateWorkerAlert("AUDUSD", "critical"),
            CreateWorkerAlert("AUDUSD", "escalated"));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.SymbolsEvaluated);
        Assert.Equal(0, result.DegradedSymbols);
        Assert.Equal(1, result.Recovered);
        Assert.Equal(3, result.AlertsResolved);

        var activeFlag = await db.Set<EngineConfig>()
            .SingleAsync(entry => entry.Key == "MLDegradation:AUDUSD:Active");
        Assert.Equal("false", activeFlag.Value);

        var detectedAt = await db.Set<EngineConfig>()
            .SingleAsync(entry => entry.Key == "MLDegradation:AUDUSD:DetectedAt");
        Assert.Equal(string.Empty, detectedAt.Value);

        var alerts = await db.Set<Alert>().ToListAsync();
        Assert.All(alerts, alert =>
        {
            Assert.False(alert.IsActive);
            Assert.Equal(FixedNow.UtcDateTime, alert.AutoResolvedAt);
        });
        dispatcher.Verify(
            d => d.TryAutoResolveAsync(It.IsAny<Alert>(), false, It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task RunCycleAsync_SuppressedPrimaryWithFallback_IsHealthyAndDoesNotDegrade()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().AddRange(
            CreateModel("NZDUSD", Timeframe.H1, isSuppressed: true),
            CreateModel("NZDUSD", Timeframe.H1, isFallbackChampion: true));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.SymbolsEvaluated);
        Assert.Equal(0, result.DegradedSymbols);
        Assert.Equal(0, result.NewlyDegraded);
        Assert.Empty(await db.Set<EngineConfig>().ToListAsync());
        Assert.Empty(await db.Set<Alert>().ToListAsync());
        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_LockBusy_SkipsBeforeTouchingDatabase()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel("USDJPY", Timeframe.H1, isActive: false));
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
        Assert.Equal(0, result.SymbolsEvaluated);
        Assert.Empty(await db.Set<EngineConfig>().ToListAsync());
        Assert.Empty(await db.Set<Alert>().ToListAsync());
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLDegradationModeWorker CreateWorker(
        DbContext db,
        MLDegradationModeOptions? options = null,
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

        return new MLDegradationModeWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLDegradationModeWorker>>(),
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

    private static MLDegradationModeOptions Options()
        => new()
        {
            Enabled = true,
            InitialDelaySeconds = 0,
            PollIntervalSeconds = 300,
            PollJitterSeconds = 0,
            MaxSymbolsPerCycle = 100,
            CriticalAfterMinutes = 60,
            EscalateAfterHours = 24,
            AlertCooldownSeconds = 0,
            LockTimeoutSeconds = 1,
            AlertDestination = "ml-ops",
            EscalationDestination = "ml-ops-escalation"
        };

    private static MLModel CreateModel(
        string symbol,
        Timeframe timeframe,
        bool isActive = true,
        bool isSuppressed = false,
        bool isFallbackChampion = false,
        bool hasModelBytes = true)
        => new()
        {
            Symbol = symbol,
            Timeframe = timeframe,
            IsActive = isActive,
            IsDeleted = false,
            IsSuppressed = isSuppressed,
            IsFallbackChampion = isFallbackChampion,
            Status = MLModelStatus.Active,
            ActivatedAt = FixedNow.UtcDateTime.AddDays(-1),
            ModelVersion = "1.0.0",
            ModelBytes = hasModelBytes ? new byte[] { 1, 2, 3 } : null,
            ExpectedValue = 1m
        };

    private static EngineConfig Config(
        string key,
        string value,
        ConfigDataType dataType = ConfigDataType.String)
        => new()
        {
            Key = key,
            Value = value,
            DataType = dataType,
            IsHotReloadable = true,
            IsDeleted = false
        };

    private static Alert CreateWorkerAlert(string symbol, string stage)
        => new()
        {
            AlertType = AlertType.MLModelDegraded,
            Symbol = symbol,
            ConditionJson = "{}",
            IsActive = true,
            Severity = AlertSeverity.Critical,
            DeduplicationKey = $"ml-degradation-mode:{symbol}:{stage}",
            CooldownSeconds = 300,
            LastTriggeredAt = FixedNow.UtcDateTime.AddHours(-1)
        };
}
