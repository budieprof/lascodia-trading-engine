using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLCalibratedEdgeWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_NegativeLiveEdge_DispatchesCriticalAlert_AndQueuesRetrain()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLEdge:MinSamples", "4");
                AddConfig(db, "MLEdge:WarnEvPips", "0.50");
                AddConfig(db, "MLTraining:TrainingDataWindowDays", "90");

                SeedActiveModel(db, modelId: 1, symbol: "EURUSD", timeframe: Timeframe.H1);

                SeedResolvedLog(db, 1, 1, "EURUSD", Timeframe.H1, now.AddHours(-8).UtcDateTime, 0.90, 0.50, TradeDirection.Buy, 10.0);
                SeedResolvedLog(db, 2, 1, "EURUSD", Timeframe.H1, now.AddHours(-7).UtcDateTime, 0.85, 0.50, TradeDirection.Sell, 20.0);
                SeedResolvedLog(db, 3, 1, "EURUSD", Timeframe.H1, now.AddHours(-6).UtcDateTime, 0.10, 0.50, TradeDirection.Sell, 8.0);
                SeedResolvedLog(db, 4, 1, "EURUSD", Timeframe.H1, now.AddHours(-5).UtcDateTime, 0.30, 0.50, TradeDirection.Buy, 12.0);
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var alert = Assert.Single(await harness.LoadAlertsAsync(), item => item.DeduplicationKey == "ml-calibrated-edge:1");
        var run = Assert.Single(await harness.LoadTrainingRunsAsync());
        var evConfig = await harness.LoadConfigAsync("MLEdge:Model:1:ExpectedValue");
        var legacyEvConfig = await harness.LoadConfigAsync("MLEdge:EURUSD:H1:ExpectedValue");

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.CandidateModelCount);
        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(0, result.WarningModelCount);
        Assert.Equal(1, result.CriticalModelCount);
        Assert.Equal(1, result.RetrainingQueuedCount);
        Assert.Equal(1, result.DispatchedAlertCount);
        Assert.Equal(0, result.ResolvedAlertCount);

        Assert.True(alert.IsActive);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.NotNull(alert.LastTriggeredAt);

        Assert.Equal(TriggerType.AutoDegrading, run.TriggerType);
        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal("CalibratedEdge", run.DriftTriggerType);

        Assert.Equal("-0.5500", evConfig?.Value);
        Assert.Equal("-0.5500", legacyEvConfig?.Value);
    }

    [Fact]
    public async Task RunCycleAsync_HealthyLiveEdge_ResolvesExistingAlert()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLEdge:MinSamples", "3");
                AddConfig(db, "MLEdge:WarnEvPips", "0.50");

                SeedActiveModel(db, modelId: 2, symbol: "GBPUSD", timeframe: Timeframe.M15);
                SeedResolvedLog(db, 10, 2, "GBPUSD", Timeframe.M15, now.AddHours(-4).UtcDateTime, 0.90, 0.50, TradeDirection.Buy, 10.0);
                SeedResolvedLog(db, 11, 2, "GBPUSD", Timeframe.M15, now.AddHours(-3).UtcDateTime, 0.10, 0.50, TradeDirection.Sell, 10.0);
                SeedResolvedLog(db, 12, 2, "GBPUSD", Timeframe.M15, now.AddHours(-2).UtcDateTime, 0.80, 0.50, TradeDirection.Buy, 6.0);

                db.Set<Alert>().Add(new Alert
                {
                    Id = 900,
                    AlertType = AlertType.MLModelDegraded,
                    Symbol = "GBPUSD",
                    ConditionJson = "{}",
                    DeduplicationKey = "ml-calibrated-edge:2",
                    IsActive = true,
                    Severity = AlertSeverity.High,
                    CooldownSeconds = 3600,
                    LastTriggeredAt = now.AddHours(-1).UtcDateTime,
                    IsDeleted = false
                });
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var alert = Assert.Single(await harness.LoadAlertsAsync(ignoreQueryFilters: true), item => item.DeduplicationKey == "ml-calibrated-edge:2");

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(0, result.WarningModelCount);
        Assert.Equal(0, result.CriticalModelCount);
        Assert.Equal(0, result.DispatchedAlertCount);
        Assert.Equal(1, result.ResolvedAlertCount);

        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
        Assert.Empty(await harness.LoadTrainingRunsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_ConfidenceOnlyLegacyLogs_AreExcluded()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLEdge:MinSamples", "3");

                SeedActiveModel(db, modelId: 3, symbol: "USDJPY", timeframe: Timeframe.H1);
                SeedConfidenceOnlyLog(db, 20, 3, "USDJPY", Timeframe.H1, now.AddHours(-3).UtcDateTime, TradeDirection.Buy, TradeDirection.Buy, 8.0, 0.80m);
                SeedConfidenceOnlyLog(db, 21, 3, "USDJPY", Timeframe.H1, now.AddHours(-2).UtcDateTime, TradeDirection.Sell, TradeDirection.Sell, 9.0, 0.75m);
                SeedConfidenceOnlyLog(db, 22, 3, "USDJPY", Timeframe.H1, now.AddHours(-1).UtcDateTime, TradeDirection.Buy, TradeDirection.Buy, 6.0, 0.70m);
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.CandidateModelCount);
        Assert.Equal(0, result.EvaluatedModelCount);
        Assert.Empty(await harness.LoadAlertsAsync());
        Assert.Empty(await harness.LoadTrainingRunsAsync());
        Assert.Null(await harness.LoadConfigAsync("MLEdge:Model:3:ExpectedValue"));
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutMutatingState()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLEdge:MinSamples", "3");
                SeedActiveModel(db, modelId: 4, symbol: "AUDUSD", timeframe: Timeframe.H1);
                SeedResolvedLog(db, 30, 4, "AUDUSD", Timeframe.H1, now.AddHours(-3).UtcDateTime, 0.90, 0.50, TradeDirection.Sell, 10.0);
                SeedResolvedLog(db, 31, 4, "AUDUSD", Timeframe.H1, now.AddHours(-2).UtcDateTime, 0.90, 0.50, TradeDirection.Sell, 12.0);
                SeedResolvedLog(db, 32, 4, "AUDUSD", Timeframe.H1, now.AddHours(-1).UtcDateTime, 0.90, 0.50, TradeDirection.Sell, 14.0);
            },
            timeProvider: new TestTimeProvider(now),
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await harness.LoadAlertsAsync());
        Assert.Empty(await harness.LoadTrainingRunsAsync());
        Assert.Null(await harness.LoadConfigAsync("MLEdge:Model:4:ExpectedValue"));
    }

    [Fact]
    public async Task RunCycleAsync_InvalidSettingsAreClampedSafely()
    {
        using var harness = CreateHarness(seed: db =>
        {
            AddConfig(db, "MLEdge:Enabled", "true");
            AddConfig(db, "MLEdge:PollIntervalSeconds", "-1");
            AddConfig(db, "MLEdge:WindowDays", "0");
            AddConfig(db, "MLEdge:MinSamples", "0");
            AddConfig(db, "MLEdge:WarnEvPips", "-2");
            AddConfig(db, "MLEdge:MaxResolvedPerModel", "0");
            AddConfig(db, "MLEdge:LockTimeoutSeconds", "-5");
            AddConfig(db, "MLEdge:MinTimeBetweenRetrainsHours", "-1");
            AddConfig(db, "MLTraining:TrainingDataWindowDays", "0");
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("no_active_models", result.SkippedReason);
        Assert.Equal(TimeSpan.FromHours(1), result.Settings.PollInterval);
        Assert.Equal(30, result.Settings.WindowDays);
        Assert.Equal(10, result.Settings.MinSamples);
        Assert.Equal(0.5, result.Settings.WarnExpectedValuePips, 6);
        Assert.Equal(512, result.Settings.MaxResolvedPerModel);
        Assert.Equal(5, result.Settings.LockTimeoutSeconds);
        Assert.Equal(24, result.Settings.MinTimeBetweenRetrainsHours);
        Assert.Equal(365, result.Settings.TrainingDataWindowDays);
        Assert.Equal(5, result.Settings.MaxRetrainsPerCycle);
        Assert.Equal(5, result.Settings.ConsecutiveSkipAlertThreshold);
    }

    [Fact]
    public async Task RunCycleAsync_TwoActiveModelsInSameContext_HighestIdWritesLegacyAlias()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLEdge:MinSamples", "3");
                AddConfig(db, "MLEdge:WarnEvPips", "0.50");

                SeedActiveModel(db, modelId: 50, symbol: "EURUSD", timeframe: Timeframe.H1);
                SeedActiveModel(db, modelId: 51, symbol: "EURUSD", timeframe: Timeframe.H1);

                SeedResolvedLog(db, 100, 50, "EURUSD", Timeframe.H1, now.AddHours(-3).UtcDateTime, 0.10, 0.50, TradeDirection.Buy, 5.0);
                SeedResolvedLog(db, 101, 50, "EURUSD", Timeframe.H1, now.AddHours(-2).UtcDateTime, 0.10, 0.50, TradeDirection.Buy, 5.0);
                SeedResolvedLog(db, 102, 50, "EURUSD", Timeframe.H1, now.AddHours(-1).UtcDateTime, 0.10, 0.50, TradeDirection.Buy, 5.0);

                SeedResolvedLog(db, 200, 51, "EURUSD", Timeframe.H1, now.AddHours(-3).UtcDateTime, 0.90, 0.50, TradeDirection.Buy, 7.0);
                SeedResolvedLog(db, 201, 51, "EURUSD", Timeframe.H1, now.AddHours(-2).UtcDateTime, 0.90, 0.50, TradeDirection.Buy, 7.0);
                SeedResolvedLog(db, 202, 51, "EURUSD", Timeframe.H1, now.AddHours(-1).UtcDateTime, 0.90, 0.50, TradeDirection.Buy, 7.0);
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(2, result.EvaluatedModelCount);

        var legacyEv = await harness.LoadConfigAsync("MLEdge:EURUSD:H1:ExpectedValue");
        var legacyModelId = await harness.LoadConfigAsync("MLEdge:EURUSD:H1:ModelId");
        var model50Ev = await harness.LoadConfigAsync("MLEdge:Model:50:ExpectedValue");
        var model51Ev = await harness.LoadConfigAsync("MLEdge:Model:51:ExpectedValue");

        Assert.NotNull(model50Ev);
        Assert.NotNull(model51Ev);
        Assert.NotNull(legacyEv);
        Assert.Equal("51", legacyModelId?.Value);
        Assert.Equal(model51Ev?.Value, legacyEv?.Value);
    }

    [Fact]
    public async Task RunCycleAsync_RetrainBackpressure_HaltsAtMaxRetrainsPerCycle()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLEdge:MinSamples", "3");
                AddConfig(db, "MLEdge:WarnEvPips", "0.50");
                AddConfig(db, "MLEdge:MaxRetrainsPerCycle", "1");

                SeedActiveModel(db, modelId: 60, symbol: "EURUSD", timeframe: Timeframe.H1);
                SeedActiveModel(db, modelId: 61, symbol: "GBPUSD", timeframe: Timeframe.H1);
                SeedActiveModel(db, modelId: 62, symbol: "USDJPY", timeframe: Timeframe.H1);

                long logId = 300;
                foreach (long modelId in new long[] { 60, 61, 62 })
                {
                    string symbol = modelId switch { 60 => "EURUSD", 61 => "GBPUSD", _ => "USDJPY" };
                    for (int i = 0; i < 3; i++)
                    {
                        SeedResolvedLog(db, logId++, modelId, symbol, Timeframe.H1,
                            now.AddHours(-3 + i).UtcDateTime,
                            servedBuyProbability: 0.10,
                            decisionThreshold: 0.50,
                            actualDirection: TradeDirection.Buy,
                            actualMagnitudePips: 8.0);
                    }
                }
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(3, result.CriticalModelCount);
        Assert.Equal(1, result.RetrainingQueuedCount);
        Assert.Equal(2, result.RetrainBackpressureSkippedCount);
        Assert.Single(await harness.LoadTrainingRunsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_StaleMonitoring_FiresAlertWhenSkipStreakReachesThreshold()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLEdge:MinSamples", "5");
                AddConfig(db, "MLEdge:ConsecutiveSkipAlertThreshold", "3");

                SeedActiveModel(db, modelId: 70, symbol: "AUDUSD", timeframe: Timeframe.H1);

                db.Set<EngineConfig>().Add(new EngineConfig
                {
                    Key = "MLEdge:Model:70:ConsecutiveSkips",
                    Value = "2",
                    DataType = ConfigDataType.Int,
                    IsHotReloadable = false,
                    LastUpdatedAt = DateTime.UtcNow,
                    IsDeleted = false
                });

                SeedConfidenceOnlyLog(db, 400, 70, "AUDUSD", Timeframe.H1, now.AddHours(-3).UtcDateTime, TradeDirection.Buy, TradeDirection.Buy, 8.0, 0.80m);
                SeedConfidenceOnlyLog(db, 401, 70, "AUDUSD", Timeframe.H1, now.AddHours(-2).UtcDateTime, TradeDirection.Sell, TradeDirection.Sell, 9.0, 0.75m);
                SeedConfidenceOnlyLog(db, 402, 70, "AUDUSD", Timeframe.H1, now.AddHours(-1).UtcDateTime, TradeDirection.Buy, TradeDirection.Buy, 6.0, 0.70m);
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(0, result.EvaluatedModelCount);
        Assert.Equal(1, result.StaleMonitoringAlertCount);

        var staleAlert = Assert.Single(await harness.LoadAlertsAsync(), item => item.DeduplicationKey == "ml-calibrated-edge-stale:70");
        Assert.True(staleAlert.IsActive);
        Assert.Equal(AlertType.MLMonitoringStale, staleAlert.AlertType);
        Assert.Equal(AlertSeverity.High, staleAlert.Severity);
        Assert.NotNull(staleAlert.LastTriggeredAt);

        var streakConfig = await harness.LoadConfigAsync("MLEdge:Model:70:ConsecutiveSkips");
        Assert.Equal("3", streakConfig?.Value);
    }

    [Fact]
    public async Task RunCycleAsync_StaleMonitoring_ResolvedAndStreakResetWhenModelEvaluatesAgain()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLEdge:MinSamples", "3");
                AddConfig(db, "MLEdge:WarnEvPips", "0.50");

                SeedActiveModel(db, modelId: 80, symbol: "NZDUSD", timeframe: Timeframe.H1);

                db.Set<EngineConfig>().Add(new EngineConfig
                {
                    Key = "MLEdge:Model:80:ConsecutiveSkips",
                    Value = "7",
                    DataType = ConfigDataType.Int,
                    IsHotReloadable = false,
                    LastUpdatedAt = DateTime.UtcNow,
                    IsDeleted = false
                });

                db.Set<Alert>().Add(new Alert
                {
                    Id = 1500,
                    AlertType = AlertType.MLModelDegraded,
                    Symbol = "NZDUSD",
                    ConditionJson = "{}",
                    DeduplicationKey = "ml-calibrated-edge-stale:80",
                    IsActive = true,
                    Severity = AlertSeverity.High,
                    CooldownSeconds = 3600,
                    LastTriggeredAt = now.AddHours(-2).UtcDateTime,
                    IsDeleted = false
                });

                SeedResolvedLog(db, 500, 80, "NZDUSD", Timeframe.H1, now.AddHours(-3).UtcDateTime, 0.90, 0.50, TradeDirection.Buy, 10.0);
                SeedResolvedLog(db, 501, 80, "NZDUSD", Timeframe.H1, now.AddHours(-2).UtcDateTime, 0.90, 0.50, TradeDirection.Buy, 12.0);
                SeedResolvedLog(db, 502, 80, "NZDUSD", Timeframe.H1, now.AddHours(-1).UtcDateTime, 0.90, 0.50, TradeDirection.Buy, 14.0);
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.EvaluatedModelCount);

        var staleAlert = Assert.Single(await harness.LoadAlertsAsync(ignoreQueryFilters: true), item => item.DeduplicationKey == "ml-calibrated-edge-stale:80");
        Assert.False(staleAlert.IsActive);
        Assert.NotNull(staleAlert.AutoResolvedAt);

        var streakConfig = await harness.LoadConfigAsync("MLEdge:Model:80:ConsecutiveSkips");
        Assert.Equal("0", streakConfig?.Value);
    }

    private static WorkerHarness CreateHarness(
        Action<MLCalibratedEdgeWorkerTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var effectiveTimeProvider = timeProvider ?? new TestTimeProvider(now);

        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLCalibratedEdgeWorkerTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetRequiredService<MLCalibratedEdgeWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(provider => provider.GetRequiredService<MLCalibratedEdgeWorkerTestContext>());
        services.AddSingleton<IAlertDispatcher>(new TestAlertDispatcher(effectiveTimeProvider));

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLCalibratedEdgeWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLCalibratedEdgeWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLCalibratedEdgeWorker>.Instance,
            metrics: null,
            timeProvider: effectiveTimeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, connection, worker);
    }

    private static void AddConfig(
        MLCalibratedEdgeWorkerTestContext db,
        string key,
        string value)
    {
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = ConfigDataType.String,
            IsHotReloadable = true,
            LastUpdatedAt = DateTime.UtcNow,
            IsDeleted = false
        });
    }

    private static void SeedActiveModel(
        MLCalibratedEdgeWorkerTestContext db,
        long modelId,
        string symbol,
        Timeframe timeframe)
    {
        db.Set<MLModel>().Add(new MLModel
        {
            Id = modelId,
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = "1.0.0",
            FilePath = "test-model.json",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainedAt = DateTime.UtcNow.AddDays(-5),
            ModelBytes = Array.Empty<byte>(),
            LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
            IsDeleted = false
        });
    }

    private static void SeedResolvedLog(
        MLCalibratedEdgeWorkerTestContext db,
        long id,
        long modelId,
        string symbol,
        Timeframe timeframe,
        DateTime outcomeRecordedAtUtc,
        double servedBuyProbability,
        double decisionThreshold,
        TradeDirection actualDirection,
        double actualMagnitudePips)
    {
        var predictedDirection = servedBuyProbability >= decisionThreshold
            ? TradeDirection.Buy
            : TradeDirection.Sell;

        db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
        {
            Id = id,
            TradeSignalId = id * 100,
            MLModelId = modelId,
            ModelRole = ModelRole.Champion,
            Symbol = symbol,
            Timeframe = timeframe,
            PredictedDirection = predictedDirection,
            ConfidenceScore = 0.80m,
            ServedCalibratedProbability = (decimal)servedBuyProbability,
            DecisionThresholdUsed = (decimal)decisionThreshold,
            ActualDirection = actualDirection,
            ActualMagnitudePips = (decimal)actualMagnitudePips,
            DirectionCorrect = predictedDirection == actualDirection,
            OutcomeRecordedAt = outcomeRecordedAtUtc,
            PredictedAt = outcomeRecordedAtUtc.AddMinutes(-5),
            IsDeleted = false
        });
    }

    private static void SeedConfidenceOnlyLog(
        MLCalibratedEdgeWorkerTestContext db,
        long id,
        long modelId,
        string symbol,
        Timeframe timeframe,
        DateTime outcomeRecordedAtUtc,
        TradeDirection predictedDirection,
        TradeDirection actualDirection,
        double actualMagnitudePips,
        decimal confidenceScore)
    {
        db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
        {
            Id = id,
            TradeSignalId = id * 100,
            MLModelId = modelId,
            ModelRole = ModelRole.Champion,
            Symbol = symbol,
            Timeframe = timeframe,
            PredictedDirection = predictedDirection,
            ConfidenceScore = confidenceScore,
            ActualDirection = actualDirection,
            ActualMagnitudePips = (decimal)actualMagnitudePips,
            DirectionCorrect = predictedDirection == actualDirection,
            OutcomeRecordedAt = outcomeRecordedAtUtc,
            PredictedAt = outcomeRecordedAtUtc.AddMinutes(-5),
            IsDeleted = false
        });
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        MLCalibratedEdgeWorker worker) : IDisposable
    {
        public MLCalibratedEdgeWorker Worker { get; } = worker;

        public async Task<List<Alert>> LoadAlertsAsync(bool ignoreQueryFilters = false)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLCalibratedEdgeWorkerTestContext>();

            IQueryable<Alert> query = db.Set<Alert>().AsNoTracking();
            if (ignoreQueryFilters)
                query = query.IgnoreQueryFilters();

            return await query.OrderBy(alert => alert.Id).ToListAsync();
        }

        public async Task<List<MLTrainingRun>> LoadTrainingRunsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLCalibratedEdgeWorkerTestContext>();
            return await db.Set<MLTrainingRun>()
                .AsNoTracking()
                .OrderBy(run => run.Id)
                .ToListAsync();
        }

        public async Task<EngineConfig?> LoadConfigAsync(string key)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLCalibratedEdgeWorkerTestContext>();
            return await db.Set<EngineConfig>()
                .AsNoTracking()
                .SingleOrDefaultAsync(config => config.Key == key);
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class MLCalibratedEdgeWorkerTestContext(DbContextOptions<MLCalibratedEdgeWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Alert>(builder =>
            {
                builder.HasKey(alert => alert.Id);
                builder.Property(alert => alert.AlertType).HasConversion<string>();
                builder.Property(alert => alert.Severity).HasConversion<string>();
                builder.HasQueryFilter(alert => !alert.IsDeleted);
                builder.HasIndex(alert => alert.DeduplicationKey)
                    .IsUnique()
                    .HasFilter("\"IsActive\" = TRUE AND \"IsDeleted\" = FALSE AND \"DeduplicationKey\" IS NOT NULL");
            });

            modelBuilder.Entity<EngineConfig>(builder =>
            {
                builder.HasKey(config => config.Id);
                builder.Property(config => config.DataType).HasConversion<string>();
                builder.HasQueryFilter(config => !config.IsDeleted);
                builder.HasIndex(config => config.Key).IsUnique();
            });

            modelBuilder.Entity<MLModel>(builder =>
            {
                builder.HasKey(model => model.Id);
                builder.Property(model => model.Timeframe).HasConversion<string>();
                builder.Property(model => model.Status).HasConversion<string>();
                builder.Property(model => model.LearnerArchitecture).HasConversion<string>();
                builder.Property(model => model.RowVersion).HasDefaultValue(0u).ValueGeneratedNever();
                builder.HasQueryFilter(model => !model.IsDeleted);

                builder.Ignore(model => model.TrainingRuns);
                builder.Ignore(model => model.TradeSignals);
                builder.Ignore(model => model.PredictionLogs);
                builder.Ignore(model => model.ChampionEvaluations);
                builder.Ignore(model => model.ChallengerEvaluations);
                builder.Ignore(model => model.CausalFeatureAudits);
                builder.Ignore(model => model.ConformalCalibrations);
                builder.Ignore(model => model.FeatureInteractionAudits);
                builder.Ignore(model => model.LifecycleLogs);
            });

            modelBuilder.Entity<MLModelPredictionLog>(builder =>
            {
                builder.HasKey(log => log.Id);
                builder.Property(log => log.Timeframe).HasConversion<string>();
                builder.Property(log => log.PredictedDirection).HasConversion<string>();
                builder.Property(log => log.ModelRole).HasConversion<string>();
                builder.Property(log => log.ActualDirection).HasConversion<string>();
                builder.HasQueryFilter(log => !log.IsDeleted);

                builder.Ignore(log => log.TradeSignal);
                builder.Ignore(log => log.MLModel);
                builder.Ignore(log => log.MLConformalCalibration);
            });

            modelBuilder.Entity<MLTrainingRun>(builder =>
            {
                builder.HasKey(run => run.Id);
                builder.Property(run => run.Timeframe).HasConversion<string>();
                builder.Property(run => run.TriggerType).HasConversion<string>();
                builder.Property(run => run.Status).HasConversion<string>();
                builder.Property(run => run.LearnerArchitecture).HasConversion<string>();
                builder.HasQueryFilter(run => !run.IsDeleted);
                builder.HasIndex(run => new { run.Symbol, run.Timeframe })
                    .IsUnique()
                    .HasFilter("\"Status\" IN ('Queued','Running') AND \"IsDeleted\" = FALSE");

                builder.Ignore(run => run.MLModel);
            });
        }
    }

    private sealed class TestAlertDispatcher(TimeProvider timeProvider) : IAlertDispatcher
    {
        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
        {
            alert.LastTriggeredAt = timeProvider.GetUtcNow().UtcDateTime;
            return Task.CompletedTask;
        }

        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
        {
            alert.AutoResolvedAt = timeProvider.GetUtcNow().UtcDateTime;
            return Task.CompletedTask;
        }
    }

    private sealed class TestDistributedLock(bool lockAvailable) : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(lockAvailable ? new Releaser() : null);

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
            => TryAcquireAsync(lockKey, ct);

        private sealed class Releaser : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
