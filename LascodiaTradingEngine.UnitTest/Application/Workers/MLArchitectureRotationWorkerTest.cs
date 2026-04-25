using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLArchitectureRotationWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_QueuesAdaBoost_WhenSupportedAndUnderrepresented()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            supportedArchitectures:
            [
                LearnerArchitecture.BaggedLogistic,
                LearnerArchitecture.Gbm,
                LearnerArchitecture.Elm,
                LearnerArchitecture.Dann,
                LearnerArchitecture.AdaBoost,
            ],
            seed: db =>
            {
                SeedActiveModel(db, 1, "EURUSD", Timeframe.H1);
                AddConfig(db, "MLArchitectureRotation:MinRunsPerWindow", "1");

                SeedCompletedRun(db, 11, "EURUSD", Timeframe.H1, LearnerArchitecture.BaggedLogistic, now.AddHours(-8).UtcDateTime);
                SeedCompletedRun(db, 12, "EURUSD", Timeframe.H1, LearnerArchitecture.Gbm, now.AddHours(-7).UtcDateTime);
                SeedCompletedRun(db, 13, "EURUSD", Timeframe.H1, LearnerArchitecture.Elm, now.AddHours(-6).UtcDateTime);
                SeedCompletedRun(db, 14, "EURUSD", Timeframe.H1, LearnerArchitecture.Dann, now.AddHours(-5).UtcDateTime);
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var queuedRun = Assert.Single(await harness.LoadQueuedRunsAsync());
        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.ContextCount);
        Assert.Equal(1, result.QueuedRunCount);
        Assert.Equal(LearnerArchitecture.AdaBoost, queuedRun.LearnerArchitecture);
        Assert.Equal(TriggerType.Scheduled, queuedRun.TriggerType);
        Assert.Equal(10, queuedRun.Priority);
    }

    [Fact]
    public async Task RunCycleAsync_RecentInfrastructureFailureSuppressesArchitecture_ButOldFailureDoesNot()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            supportedArchitectures:
            [
                LearnerArchitecture.BaggedLogistic,
                LearnerArchitecture.Gbm,
                LearnerArchitecture.Elm,
                LearnerArchitecture.AdaBoost,
                LearnerArchitecture.Dann,
            ],
            seed: db =>
            {
                SeedActiveModel(db, 1, "EURUSD", Timeframe.H1);
                AddConfig(db, "MLArchitectureRotation:MinRunsPerWindow", "1");
                AddConfig(db, "MLArchitectureRotation:InfraFailureLookbackHours", "24");

                SeedCompletedRun(db, 11, "EURUSD", Timeframe.H1, LearnerArchitecture.BaggedLogistic, now.AddHours(-8).UtcDateTime);
                SeedCompletedRun(db, 12, "EURUSD", Timeframe.H1, LearnerArchitecture.Gbm, now.AddHours(-7).UtcDateTime);
                SeedCompletedRun(db, 13, "EURUSD", Timeframe.H1, LearnerArchitecture.Elm, now.AddHours(-6).UtcDateTime);

                SeedFailedRun(db, 21, "EURUSD", Timeframe.H1, LearnerArchitecture.AdaBoost, now.AddHours(-1).UtcDateTime, "TorchSharp bootstrap failed");
                SeedFailedRun(db, 22, "EURUSD", Timeframe.H1, LearnerArchitecture.Dann, now.AddDays(-3).UtcDateTime, "libtorch missing");
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var queuedRun = Assert.Single(await harness.LoadQueuedRunsAsync());
        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.QueuedRunCount);
        Assert.Equal(LearnerArchitecture.Dann, queuedRun.LearnerArchitecture);
    }

    [Fact]
    public async Task RunCycleAsync_FailedRunsDoNotCountTowardRotationQuota()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            supportedArchitectures:
            [
                LearnerArchitecture.BaggedLogistic,
                LearnerArchitecture.Gbm,
                LearnerArchitecture.Elm,
                LearnerArchitecture.AdaBoost,
            ],
            seed: db =>
            {
                SeedActiveModel(db, 1, "EURUSD", Timeframe.H1);
                AddConfig(db, "MLArchitectureRotation:MinRunsPerWindow", "2");

                SeedCompletedRun(db, 11, "EURUSD", Timeframe.H1, LearnerArchitecture.BaggedLogistic, now.AddHours(-8).UtcDateTime);
                SeedCompletedRun(db, 12, "EURUSD", Timeframe.H1, LearnerArchitecture.BaggedLogistic, now.AddHours(-7).UtcDateTime);
                SeedCompletedRun(db, 13, "EURUSD", Timeframe.H1, LearnerArchitecture.Gbm, now.AddHours(-8).UtcDateTime);
                SeedCompletedRun(db, 14, "EURUSD", Timeframe.H1, LearnerArchitecture.Gbm, now.AddHours(-7).UtcDateTime);
                SeedCompletedRun(db, 15, "EURUSD", Timeframe.H1, LearnerArchitecture.Elm, now.AddHours(-8).UtcDateTime);
                SeedCompletedRun(db, 16, "EURUSD", Timeframe.H1, LearnerArchitecture.Elm, now.AddHours(-7).UtcDateTime);

                SeedFailedRun(db, 21, "EURUSD", Timeframe.H1, LearnerArchitecture.AdaBoost, now.AddHours(-5).UtcDateTime, "training failed");
                SeedFailedRun(db, 22, "EURUSD", Timeframe.H1, LearnerArchitecture.AdaBoost, now.AddHours(-4).UtcDateTime, "training failed");
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var queuedRun = Assert.Single(await harness.LoadQueuedRunsAsync());
        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.QueuedRunCount);
        Assert.Equal(LearnerArchitecture.AdaBoost, queuedRun.LearnerArchitecture);
    }

    [Fact]
    public async Task RunCycleAsync_StaleQueuedRun_DoesNotBlockFreshRotationRun()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            supportedArchitectures:
            [
                LearnerArchitecture.BaggedLogistic,
                LearnerArchitecture.Gbm,
                LearnerArchitecture.Elm,
                LearnerArchitecture.AdaBoost,
            ],
            seed: db =>
            {
                SeedActiveModel(db, 1, "EURUSD", Timeframe.H1);
                AddConfig(db, "MLArchitectureRotation:MinRunsPerWindow", "1");
                AddConfig(db, "MLArchitectureRotation:ActiveRunFreshnessHours", "24");

                SeedCompletedRun(db, 11, "EURUSD", Timeframe.H1, LearnerArchitecture.BaggedLogistic, now.AddHours(-8).UtcDateTime);
                SeedCompletedRun(db, 12, "EURUSD", Timeframe.H1, LearnerArchitecture.Gbm, now.AddHours(-7).UtcDateTime);
                SeedCompletedRun(db, 13, "EURUSD", Timeframe.H1, LearnerArchitecture.Elm, now.AddHours(-6).UtcDateTime);

                db.Set<MLTrainingRun>().Add(new MLTrainingRun
                {
                    Id = 21,
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    TriggerType = TriggerType.Scheduled,
                    Status = RunStatus.Queued,
                    FromDate = now.AddDays(-365).UtcDateTime,
                    ToDate = now.AddDays(-1).UtcDateTime,
                    StartedAt = now.AddDays(-3).UtcDateTime,
                    LearnerArchitecture = LearnerArchitecture.AdaBoost,
                    Priority = 5,
                    IsDeleted = false
                });
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var queuedAdaBoostRuns = (await harness.LoadAllRunsAsync())
            .Where(run => run.LearnerArchitecture == LearnerArchitecture.AdaBoost
                       && run.Status == RunStatus.Queued)
            .OrderBy(run => run.Id)
            .ToList();

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.QueuedRunCount);
        Assert.Equal(2, queuedAdaBoostRuns.Count);
        Assert.True(queuedAdaBoostRuns[1].StartedAt > queuedAdaBoostRuns[0].StartedAt);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutQueuingRuns()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            supportedArchitectures:
            [
                LearnerArchitecture.BaggedLogistic,
                LearnerArchitecture.AdaBoost,
            ],
            seed: db =>
            {
                SeedActiveModel(db, 1, "EURUSD", Timeframe.H1);
                AddConfig(db, "MLArchitectureRotation:MinRunsPerWindow", "1");
            },
            timeProvider: new TestTimeProvider(now),
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await harness.LoadQueuedRunsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_InvalidSettingsAreClampedSafely()
    {
        using var harness = CreateHarness(
            supportedArchitectures:
            [
                LearnerArchitecture.BaggedLogistic,
                LearnerArchitecture.AdaBoost,
            ],
            seed: db =>
            {
                AddConfig(db, "MLArchitectureRotation:PollIntervalSeconds", "-1");
                AddConfig(db, "MLArchitectureRotation:MinRunsPerWindow", "0");
                AddConfig(db, "MLArchitectureRotation:WindowDays", "0");
                AddConfig(db, "MLArchitectureRotation:CooldownMinutes", "-5");
                AddConfig(db, "MLTraining:TrainingDataWindowDays", "0");
                AddConfig(db, "MLArchitectureRotation:LockTimeoutSeconds", "-2");
                AddConfig(db, "MLArchitectureRotation:MaxContextsPerCycle", "0");
                AddConfig(db, "MLArchitectureRotation:ActiveRunFreshnessHours", "0");
                AddConfig(db, "MLArchitectureRotation:InfraFailureLookbackHours", "0");
                AddConfig(db, "MLTraining:BlockedArchitectures", "AdaBoost");
            });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromHours(2), result.Settings.PollInterval);
        Assert.Equal(2, result.Settings.MinRunsPerWindow);
        Assert.Equal(7, result.Settings.WindowDays);
        Assert.Equal(60, result.Settings.CooldownMinutes);
        Assert.Equal(365, result.Settings.TrainingDataWindowDays);
        Assert.Equal(5, result.Settings.LockTimeoutSeconds);
        Assert.Equal(128, result.Settings.MaxContextsPerCycle);
        Assert.Equal(24, result.Settings.ActiveRunFreshnessHours);
        Assert.Equal(24, result.Settings.InfraFailureLookbackHours);
        Assert.Equal(1000, result.Settings.MaxPendingScheduledRuns);
        Assert.Equal(3, result.Settings.MaxFailuresPerWindow);
        Assert.NotEmpty(result.Settings.InfraFailurePatterns);
        Assert.Contains(LearnerArchitecture.AdaBoost, result.Settings.BlockedArchitectures);
    }

    [Fact]
    public async Task RunCycleAsync_BatchProcessesMultipleContexts_QueuesUnderrepresentedArchitecturePerContext()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            supportedArchitectures:
            [
                LearnerArchitecture.BaggedLogistic,
                LearnerArchitecture.Gbm,
                LearnerArchitecture.AdaBoost,
            ],
            seed: db =>
            {
                SeedActiveModel(db, 1, "EURUSD", Timeframe.H1);
                SeedActiveModel(db, 2, "GBPUSD", Timeframe.H4);
                AddConfig(db, "MLArchitectureRotation:MinRunsPerWindow", "1");

                SeedCompletedRun(db, 11, "EURUSD", Timeframe.H1, LearnerArchitecture.BaggedLogistic, now.AddHours(-8).UtcDateTime);
                SeedCompletedRun(db, 12, "EURUSD", Timeframe.H1, LearnerArchitecture.Gbm, now.AddHours(-7).UtcDateTime);
                SeedCompletedRun(db, 13, "GBPUSD", Timeframe.H4, LearnerArchitecture.BaggedLogistic, now.AddHours(-6).UtcDateTime);
                SeedCompletedRun(db, 14, "GBPUSD", Timeframe.H4, LearnerArchitecture.Gbm, now.AddHours(-5).UtcDateTime);
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var queuedRuns = await harness.LoadQueuedRunsAsync();
        Assert.Null(result.SkippedReason);
        Assert.Equal(2, result.ContextCount);
        Assert.Equal(2, result.QueuedRunCount);
        Assert.Equal(2, queuedRuns.Count);
        Assert.All(queuedRuns, run => Assert.Equal(LearnerArchitecture.AdaBoost, run.LearnerArchitecture));
        Assert.Contains(queuedRuns, run => run.Symbol == "EURUSD" && run.Timeframe == Timeframe.H1);
        Assert.Contains(queuedRuns, run => run.Symbol == "GBPUSD" && run.Timeframe == Timeframe.H4);
    }

    [Fact]
    public async Task RunCycleAsync_QueueBackpressure_HaltsQueueingWhenAtCap()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            supportedArchitectures:
            [
                LearnerArchitecture.BaggedLogistic,
                LearnerArchitecture.AdaBoost,
            ],
            seed: db =>
            {
                SeedActiveModel(db, 1, "EURUSD", Timeframe.H1);
                AddConfig(db, "MLArchitectureRotation:MinRunsPerWindow", "1");
                AddConfig(db, "MLArchitectureRotation:MaxPendingScheduledRuns", "10");

                for (int i = 0; i < 10; i++)
                {
                    db.Set<MLTrainingRun>().Add(new MLTrainingRun
                    {
                        Id = 100 + i,
                        Symbol = "USDJPY",
                        Timeframe = Timeframe.D1,
                        TriggerType = TriggerType.Scheduled,
                        Status = RunStatus.Queued,
                        FromDate = now.AddDays(-365).UtcDateTime,
                        ToDate = now.AddDays(-1).UtcDateTime,
                        StartedAt = now.AddDays(-2).UtcDateTime,
                        LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                        Priority = 5,
                        IsDeleted = false
                    });
                }

                SeedCompletedRun(db, 11, "EURUSD", Timeframe.H1, LearnerArchitecture.BaggedLogistic, now.AddHours(-8).UtcDateTime);
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.True(result.BackpressureHit);
        Assert.Equal(0, result.QueuedRunCount);
    }

    [Fact]
    public async Task RunCycleAsync_NonInfraFailureBudget_SuppressesArchitecture()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            supportedArchitectures:
            [
                LearnerArchitecture.BaggedLogistic,
                LearnerArchitecture.AdaBoost,
            ],
            seed: db =>
            {
                SeedActiveModel(db, 1, "EURUSD", Timeframe.H1);
                AddConfig(db, "MLArchitectureRotation:MinRunsPerWindow", "1");
                AddConfig(db, "MLArchitectureRotation:MaxFailuresPerWindow", "3");

                SeedCompletedRun(db, 11, "EURUSD", Timeframe.H1, LearnerArchitecture.BaggedLogistic, now.AddHours(-8).UtcDateTime);

                SeedFailedRun(db, 21, "EURUSD", Timeframe.H1, LearnerArchitecture.AdaBoost, now.AddDays(-2).UtcDateTime, "data shape mismatch");
                SeedFailedRun(db, 22, "EURUSD", Timeframe.H1, LearnerArchitecture.AdaBoost, now.AddDays(-1).UtcDateTime, "data shape mismatch");
                SeedFailedRun(db, 23, "EURUSD", Timeframe.H1, LearnerArchitecture.AdaBoost, now.AddHours(-2).UtcDateTime, "data shape mismatch");
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(0, result.QueuedRunCount);
        Assert.Empty(await harness.LoadQueuedRunsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_StarvedArchitectureGetsElevatedPriority_RecentlySuccessfulGetsBasePriority()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            supportedArchitectures:
            [
                LearnerArchitecture.BaggedLogistic,
                LearnerArchitecture.Gbm,
                LearnerArchitecture.Elm,
                LearnerArchitecture.AdaBoost,
            ],
            seed: db =>
            {
                SeedActiveModel(db, 1, "EURUSD", Timeframe.H1);
                AddConfig(db, "MLArchitectureRotation:MinRunsPerWindow", "2");
                AddConfig(db, "MLArchitectureRotation:WindowDays", "7");
                AddConfig(db, "MLArchitectureRotation:CooldownMinutes", "0");
                AddConfig(db, "MLArchitectureRotation:InfraFailureLookbackHours", "1");

                SeedCompletedRun(db, 11, "EURUSD", Timeframe.H1, LearnerArchitecture.BaggedLogistic, now.AddHours(-8).UtcDateTime);
                SeedCompletedRun(db, 12, "EURUSD", Timeframe.H1, LearnerArchitecture.BaggedLogistic, now.AddHours(-7).UtcDateTime);

                SeedCompletedRun(db, 13, "EURUSD", Timeframe.H1, LearnerArchitecture.Gbm, now.AddDays(-30).UtcDateTime);
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var queuedRuns = await harness.LoadQueuedRunsAsync();
        Assert.Null(result.SkippedReason);

        var adaBoostRun = Assert.Single(queuedRuns, run => run.LearnerArchitecture == LearnerArchitecture.AdaBoost);
        Assert.Equal(10, adaBoostRun.Priority);

        var elmRun = Assert.Single(queuedRuns, run => run.LearnerArchitecture == LearnerArchitecture.Elm);
        Assert.Equal(10, elmRun.Priority);
    }

    [Fact]
    public async Task RunCycleAsync_ConfigurableInfraFailurePatterns_OverrideDefaults()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            supportedArchitectures:
            [
                LearnerArchitecture.BaggedLogistic,
                LearnerArchitecture.AdaBoost,
                LearnerArchitecture.Dann,
            ],
            seed: db =>
            {
                SeedActiveModel(db, 1, "EURUSD", Timeframe.H1);
                AddConfig(db, "MLArchitectureRotation:MinRunsPerWindow", "1");
                AddConfig(db, "MLArchitectureRotation:InfraFailurePatterns", "custom-driver-fault");

                SeedCompletedRun(db, 11, "EURUSD", Timeframe.H1, LearnerArchitecture.BaggedLogistic, now.AddHours(-8).UtcDateTime);

                SeedFailedRun(db, 21, "EURUSD", Timeframe.H1, LearnerArchitecture.AdaBoost, now.AddHours(-2).UtcDateTime, "Custom-Driver-Fault encountered");
                SeedFailedRun(db, 22, "EURUSD", Timeframe.H1, LearnerArchitecture.Dann, now.AddHours(-2).UtcDateTime, "TorchSharp bootstrap failed");
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var queuedRuns = await harness.LoadQueuedRunsAsync();
        Assert.Null(result.SkippedReason);

        Assert.DoesNotContain(queuedRuns, run => run.LearnerArchitecture == LearnerArchitecture.AdaBoost);
        Assert.Contains(queuedRuns, run => run.LearnerArchitecture == LearnerArchitecture.Dann);
    }

    private static WorkerHarness CreateHarness(
        IReadOnlyCollection<LearnerArchitecture> supportedArchitectures,
        Action<MLArchitectureRotationWorkerTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var effectiveTimeProvider = timeProvider ?? new TestTimeProvider(now);

        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLArchitectureRotationWorkerTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetRequiredService<MLArchitectureRotationWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(provider => provider.GetRequiredService<MLArchitectureRotationWorkerTestContext>());

        if (supportedArchitectures.Contains(LearnerArchitecture.BaggedLogistic))
            services.AddScoped<IMLModelTrainer, FakeTrainer>();

        foreach (var architecture in supportedArchitectures.Where(architecture => architecture != LearnerArchitecture.BaggedLogistic))
            services.AddKeyedScoped<IMLModelTrainer, FakeTrainer>(architecture);

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLArchitectureRotationWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLArchitectureRotationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLArchitectureRotationWorker>.Instance,
            metrics: null,
            timeProvider: effectiveTimeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, connection, worker);
    }

    private static void SeedActiveModel(
        MLArchitectureRotationWorkerTestContext db,
        long id,
        string symbol,
        Timeframe timeframe)
    {
        db.Set<MLModel>().Add(new MLModel
        {
            Id = id,
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = "1.0.0",
            FilePath = "/tmp/model.bin",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainedAt = new DateTime(2026, 04, 20, 12, 0, 0, DateTimeKind.Utc),
            ModelBytes = [1, 2, 3],
            LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
            IsDeleted = false,
            RowVersion = 1
        });
    }

    private static void SeedCompletedRun(
        MLArchitectureRotationWorkerTestContext db,
        long id,
        string symbol,
        Timeframe timeframe,
        LearnerArchitecture architecture,
        DateTime completedAtUtc)
    {
        db.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Id = id,
            Symbol = symbol,
            Timeframe = timeframe,
            TriggerType = TriggerType.Scheduled,
            Status = RunStatus.Completed,
            FromDate = completedAtUtc.AddDays(-365),
            ToDate = completedAtUtc.AddHours(-1),
            StartedAt = completedAtUtc.AddHours(-2),
            CompletedAt = completedAtUtc,
            LearnerArchitecture = architecture,
            Priority = 5,
            IsDeleted = false
        });
    }

    private static void SeedFailedRun(
        MLArchitectureRotationWorkerTestContext db,
        long id,
        string symbol,
        Timeframe timeframe,
        LearnerArchitecture architecture,
        DateTime completedAtUtc,
        string errorMessage)
    {
        db.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Id = id,
            Symbol = symbol,
            Timeframe = timeframe,
            TriggerType = TriggerType.Scheduled,
            Status = RunStatus.Failed,
            FromDate = completedAtUtc.AddDays(-365),
            ToDate = completedAtUtc.AddHours(-1),
            StartedAt = completedAtUtc.AddHours(-2),
            CompletedAt = completedAtUtc,
            ErrorMessage = errorMessage,
            LearnerArchitecture = architecture,
            Priority = 5,
            IsDeleted = false
        });
    }

    private static void AddConfig(
        MLArchitectureRotationWorkerTestContext db,
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

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        MLArchitectureRotationWorker worker) : IDisposable
    {
        public MLArchitectureRotationWorker Worker { get; } = worker;

        public async Task<List<MLTrainingRun>> LoadQueuedRunsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLArchitectureRotationWorkerTestContext>();
            return await db.Set<MLTrainingRun>()
                .AsNoTracking()
                .Where(run => run.Status == RunStatus.Queued)
                .OrderBy(run => run.Id)
                .ToListAsync();
        }

        public async Task<List<MLTrainingRun>> LoadAllRunsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLArchitectureRotationWorkerTestContext>();
            return await db.Set<MLTrainingRun>()
                .AsNoTracking()
                .OrderBy(run => run.Id)
                .ToListAsync();
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class MLArchitectureRotationWorkerTestContext(DbContextOptions<MLArchitectureRotationWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EngineConfig>(builder =>
            {
                builder.HasKey(config => config.Id);
                builder.HasQueryFilter(config => !config.IsDeleted);
                builder.Property(config => config.DataType).HasConversion<string>();
                builder.HasIndex(config => config.Key).IsUnique();
            });

            modelBuilder.Entity<MLModel>(builder =>
            {
                builder.HasKey(model => model.Id);
                builder.HasQueryFilter(model => !model.IsDeleted);
                builder.Property(model => model.Timeframe).HasConversion<string>();
                builder.Property(model => model.Status).HasConversion<string>();
                builder.Property(model => model.LearnerArchitecture).HasConversion<string>();
                builder.Property(model => model.RowVersion).HasDefaultValue(0u).ValueGeneratedNever();

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

            modelBuilder.Entity<MLTrainingRun>(builder =>
            {
                builder.HasKey(run => run.Id);
                builder.HasQueryFilter(run => !run.IsDeleted);
                builder.Property(run => run.Timeframe).HasConversion<string>();
                builder.Property(run => run.TriggerType).HasConversion<string>();
                builder.Property(run => run.Status).HasConversion<string>();
                builder.Property(run => run.LearnerArchitecture).HasConversion<string>();

                builder.Ignore(run => run.MLModel);
            });
        }
    }

    private sealed class FakeTrainer : IMLModelTrainer
    {
        public Task<TrainingResult> TrainAsync(
            List<TrainingSample> samples,
            TrainingHyperparams hp,
            ModelSnapshot? warmStart = null,
            long? parentModelId = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("Fake trainer should not be invoked in MLArchitectureRotationWorker tests.");
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
