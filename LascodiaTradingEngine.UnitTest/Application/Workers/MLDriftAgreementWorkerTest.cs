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

public class MLDriftAgreementWorkerTest
{
    private static readonly DateTimeOffset FixedNow = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunCycleAsync_ConsensusThresholdReached_PersistsDispatchesAndRecordsAgreement()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel(201, "eurusd", Timeframe.H1));
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLDrift:EURUSD:H1:ConsecutiveFailures",
            Value = "2",
            DataType = ConfigDataType.Int,
        });
        db.Set<MLDriftFlag>().Add(CreateAdwinFlag("EURUSD", Timeframe.H1));
        db.Set<MLTrainingRun>().Add(CreateTrainingRun("EURUSD", Timeframe.H1, "CusumDrift"));
        db.Set<MLTrainingRun>().Add(CreateTrainingRun("EURUSD", Timeframe.H1, "CovariateShift", id: 2));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(1, result.ConsensusAlertsRaised);
        Assert.Equal(0, result.AnomalyAlertsRaised);
        Assert.Equal(1, result.AlertsDispatched);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("EURUSD", alert.Symbol);
        Assert.Equal("drift-agreement:EURUSD:H1", alert.DeduplicationKey);
        Assert.Equal(FixedNow.UtcDateTime, alert.LastTriggeredAt);

        using var condition = JsonDocument.Parse(alert.ConditionJson);
        Assert.Equal("drift_detector_consensus", condition.RootElement.GetProperty("reason").GetString());
        Assert.Equal(4, condition.RootElement.GetProperty("agreeingDetectors").GetInt32());
        Assert.Equal("ml-ops", condition.RootElement.GetProperty("destination").GetString());

        var agreementCount = await db.Set<EngineConfig>()
            .SingleAsync(config => config.Key == "MLDriftAgreement:EURUSD:H1:AgreeingDetectors");
        Assert.Equal("4", agreementCount.Value);

        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_SuppressedModelWithoutDetectors_RaisesAnomalyAlert()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel(202, "GBPUSD", Timeframe.H1, suppressed: true));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(0, result.ConsensusAlertsRaised);
        Assert.Equal(1, result.AnomalyAlertsRaised);
        Assert.Equal(1, result.AlertsDispatched);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.Equal("drift-agreement-anomaly:GBPUSD:H1", alert.DeduplicationKey);

        using var condition = JsonDocument.Parse(alert.ConditionJson);
        Assert.Equal("suppressed_without_detector_agreement", condition.RootElement.GetProperty("reason").GetString());
        Assert.True(condition.RootElement.GetProperty("modelSuppressed").GetBoolean());
    }

    [Fact]
    public async Task RunCycleAsync_AgreementClears_ResolvesExistingConsensusAlert()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel(203, "USDJPY", Timeframe.H1));
        db.Set<Alert>().Add(CreateAlert("drift-agreement:USDJPY:H1", "USDJPY"));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(1, result.AlertsResolved);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.Equal(FixedNow.UtcDateTime, alert.AutoResolvedAt);
        dispatcher.Verify(
            d => d.TryAutoResolveAsync(It.IsAny<Alert>(), false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_ExistingConsensusAlertWithinCooldown_UpdatesWithoutDispatching()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel(204, "AUDUSD", Timeframe.H1));
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLDrift:AUDUSD:H1:ConsecutiveFailures",
            Value = "1",
            DataType = ConfigDataType.Int,
        });
        db.Set<MLDriftFlag>().Add(CreateAdwinFlag("AUDUSD", Timeframe.H1));
        db.Set<MLTrainingRun>().Add(CreateTrainingRun("AUDUSD", Timeframe.H1, "CusumDrift"));
        db.Set<MLTrainingRun>().Add(CreateTrainingRun("AUDUSD", Timeframe.H1, "MultiSignal", id: 2));
        db.Set<Alert>().Add(CreateAlert(
            "drift-agreement:AUDUSD:H1",
            "AUDUSD",
            lastTriggeredAt: FixedNow.UtcDateTime.AddMinutes(-5)));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, options: Options(alertCooldownSeconds: 3_600), dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.ConsensusAlertsRaised);
        Assert.Equal(0, result.AlertsDispatched);
        Assert.Equal(1, result.AlertsSuppressedByCooldown);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Equal(FixedNow.UtcDateTime.AddMinutes(-5), alert.LastTriggeredAt);

        using var condition = JsonDocument.Parse(alert.ConditionJson);
        Assert.Equal(4, condition.RootElement.GetProperty("agreeingDetectors").GetInt32());
        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_NoActiveModels_ResolvesStaleWorkerAlert()
    {
        await using var db = CreateDbContext();
        db.Set<Alert>().Add(CreateAlert("drift-agreement:NZDUSD:H1", "NZDUSD"));
        await db.SaveChangesAsync();

        var dispatcher = CreateDispatcher();
        var worker = CreateWorker(db, dispatcher: dispatcher.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("no_active_models", result.SkippedReason);
        Assert.Equal(0, result.ModelsEvaluated);
        Assert.Equal(1, result.AlertsResolved);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.Equal(FixedNow.UtcDateTime, alert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunCycleAsync_LockBusy_SkipsWithoutMutatingDatabase()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel(205, "CADJPY", Timeframe.H1));
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
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLDriftAgreementWorker CreateWorker(
        DbContext db,
        MLDriftAgreementOptions? options = null,
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

        return new MLDriftAgreementWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLDriftAgreementWorker>>(),
            options ?? Options(),
            distributedLock,
            healthMonitor: null,
            metrics: null,
            timeProvider: new TestTimeProvider(FixedNow));
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

    private static MLDriftAgreementOptions Options(int alertCooldownSeconds = 0)
        => new()
        {
            Enabled = true,
            InitialDelaySeconds = 0,
            PollIntervalSeconds = 21_600,
            PollJitterSeconds = 0,
            CusumAlertWindowHours = 24,
            ShiftRunWindowHours = 48,
            ConsensusThreshold = 4,
            MaxModelsPerCycle = 100,
            AlertCooldownSeconds = alertCooldownSeconds,
            AlertDestination = "ml-ops",
            LockTimeoutSeconds = 1,
            DbCommandTimeoutSeconds = 60,
        };

    private static MLModel CreateModel(
        long id,
        string symbol,
        Timeframe timeframe,
        bool suppressed = false)
        => new()
        {
            Id = id,
            Symbol = symbol,
            Timeframe = timeframe,
            Status = MLModelStatus.Active,
            IsActive = true,
            IsDeleted = false,
            IsSuppressed = suppressed,
            ModelVersion = "1.0.0",
            ModelBytes = new byte[] { 1, 2, 3 },
            ActivatedAt = FixedNow.UtcDateTime.AddDays(-1),
        };

    private static MLDriftFlag CreateAdwinFlag(string symbol, Timeframe timeframe)
        => new()
        {
            Symbol = symbol,
            Timeframe = timeframe,
            DetectorType = "AdwinDrift",
            FirstDetectedAtUtc = FixedNow.UtcDateTime.AddHours(-1),
            LastRefreshedAtUtc = FixedNow.UtcDateTime.AddMinutes(-5),
            ExpiresAtUtc = FixedNow.UtcDateTime.AddHours(1),
            ConsecutiveDetections = 2,
        };

    private static MLTrainingRun CreateTrainingRun(
        string symbol,
        Timeframe timeframe,
        string triggerType,
        long id = 1)
        => new()
        {
            Id = id,
            Symbol = symbol,
            Timeframe = timeframe,
            DriftTriggerType = triggerType,
            TriggerType = TriggerType.AutoDegrading,
            Status = RunStatus.Queued,
            StartedAt = FixedNow.UtcDateTime.AddHours(-1),
            FromDate = FixedNow.UtcDateTime.AddDays(-365),
            ToDate = FixedNow.UtcDateTime,
        };

    private static Alert CreateAlert(
        string deduplicationKey,
        string symbol,
        DateTime? lastTriggeredAt = null)
        => new()
        {
            AlertType = AlertType.MLModelDegraded,
            Symbol = symbol,
            Severity = AlertSeverity.Critical,
            DeduplicationKey = deduplicationKey,
            ConditionJson = "{}",
            IsActive = true,
            CooldownSeconds = 3_600,
            LastTriggeredAt = lastTriggeredAt,
        };
}
