using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLCusumDriftWorkerTest
{
    [Fact]
    public void ComputeCusum_StableStream_DoesNotFire()
    {
        var outcomes = Enumerable.Repeat(true, 100).ToList(); // 100% accuracy throughout
        var scan = MLCusumDriftWorker.ComputeCusum(outcomes, k: 0.005, h: 5.0);

        Assert.False(scan.Fired);
    }

    [Fact]
    public void ComputeCusum_DegradingStream_Fires()
    {
        // First half all correct (refAcc = 1.0), second half all wrong → S+ accumulates rapidly.
        var outcomes = Enumerable.Repeat(true, 50)
            .Concat(Enumerable.Repeat(false, 50))
            .ToList();

        var scan = MLCusumDriftWorker.ComputeCusum(outcomes, k: 0.005, h: 5.0);

        Assert.True(scan.Fired);
        Assert.True(scan.SPlus >= 5.0);
        Assert.True(scan.RecentAccuracy < scan.ReferenceAccuracy);
    }

    [Fact]
    public void ComputeCusum_HigherH_DelaysOrPreventsFiring()
    {
        var outcomes = Enumerable.Repeat(true, 50)
            .Concat(Enumerable.Repeat(false, 50))
            .ToList();

        var loose = MLCusumDriftWorker.ComputeCusum(outcomes, k: 0.005, h: 5.0);
        var tight = MLCusumDriftWorker.ComputeCusum(outcomes, k: 0.005, h: 100.0);

        Assert.True(loose.Fired);
        Assert.False(tight.Fired);
    }

    [Fact]
    public async Task RunCycleAsync_DegradingShift_QueuesRetraining_AndDispatchesAlert()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var dispatcher = new RecordingAlertDispatcher();

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now),
            alertDispatcher: dispatcher);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.CandidateModelCount);
        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(1, result.DriftsDetected);
        Assert.Equal(1, result.RetrainingQueued);

        var run = Assert.Single(await harness.LoadTrainingRunsAsync());
        Assert.Equal(TriggerType.AutoDegrading, run.TriggerType);
        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal("CusumDrift", run.DriftTriggerType);

        var dispatched = Assert.Single(dispatcher.Dispatched);
        Assert.Equal(AlertType.MLModelDegraded, dispatched.alert.AlertType);
        Assert.Equal("cusum-drift:EURUSD:H1", dispatched.alert.DeduplicationKey);
        Assert.Contains("CUSUM drift", dispatched.message);
    }

    [Fact]
    public async Task RunCycleAsync_RetrainAlreadyQueued_SuppressesNewQueue_ButStillDispatchesAlert()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var dispatcher = new RecordingAlertDispatcher();

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLTrainingRun>().Add(new MLTrainingRun
                {
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    TriggerType = TriggerType.AutoDegrading,
                    Status = RunStatus.Running,
                    StartedAt = now.AddMinutes(-30).UtcDateTime,
                    FromDate = now.AddDays(-365).UtcDateTime,
                    ToDate = now.UtcDateTime,
                    LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                    IsDeleted = false,
                });
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now),
            alertDispatcher: dispatcher);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.DriftsDetected);
        Assert.Equal(0, result.RetrainingQueued);
        Assert.Single(dispatcher.Dispatched);
    }

    [Fact]
    public async Task RunCycleAsync_RecentCompletedRun_RetrainSuppressedByCooldown()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLTrainingRun>().Add(new MLTrainingRun
                {
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    TriggerType = TriggerType.AutoDegrading,
                    Status = RunStatus.Completed,
                    DriftTriggerType = "CusumDrift",
                    StartedAt = now.AddHours(-7).UtcDateTime,
                    CompletedAt = now.AddHours(-6).UtcDateTime,
                    FromDate = now.AddDays(-365).UtcDateTime,
                    ToDate = now.AddHours(-7).UtcDateTime,
                    LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                    IsDeleted = false,
                });
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.DriftsDetected);
        Assert.Equal(0, result.RetrainingQueued);
        var runs = await harness.LoadTrainingRunsAsync();
        Assert.Single(runs); // Only the seeded historical run
        Assert.Equal(RunStatus.Completed, runs[0].Status);
    }

    [Fact]
    public async Task RunCycleAsync_LockBusy_SkipsWithoutMutating()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50))));
            },
            timeProvider: new TestTimeProvider(now),
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await harness.LoadTrainingRunsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_InsufficientHistory_SkipsModel()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                // Only 20 outcomes — below default min 30
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddHours(-100).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 20)));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidateModelCount);
        Assert.Equal(0, result.EvaluatedModelCount);
        Assert.Equal(0, result.DriftsDetected);
    }

    private static WorkerHarness CreateHarness(
        Action<MLCusumDriftWorkerTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null,
        IAlertDispatcher? alertDispatcher = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLCusumDriftWorkerTestContext>(o => o.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<MLCusumDriftWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<MLCusumDriftWorkerTestContext>());

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLCusumDriftWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLCusumDriftWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLCusumDriftWorker>.Instance,
            distributedLock: distributedLock,
            healthMonitor: null,
            metrics: null,
            timeProvider: timeProvider,
            alertDispatcher: alertDispatcher);

        return new WorkerHarness(provider, connection, worker);
    }

    private static void SeedActiveModel(MLCusumDriftWorkerTestContext db, long id)
    {
        db.Set<MLModel>().Add(new MLModel
        {
            Id = id,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ModelVersion = "1.0.0",
            FilePath = "/tmp/model.bin",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainedAt = new DateTime(2026, 04, 20, 12, 0, 0, DateTimeKind.Utc),
            ModelBytes = [1, 2, 3],
            LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
            IsDeleted = false,
            RowVersion = 1,
        });
    }

    private static IReadOnlyList<MLModelPredictionLog> NewLogs(
        long modelId, DateTime startUtc, IEnumerable<bool> outcomes)
    {
        var logs = new List<MLModelPredictionLog>();
        int i = 0;
        foreach (var correct in outcomes)
        {
            var ts = startUtc.AddHours(i);
            logs.Add(new MLModelPredictionLog
            {
                Id = i + 1,
                TradeSignalId = i + 1,
                MLModelId = modelId,
                ModelRole = ModelRole.Champion,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                PredictedDirection = TradeDirection.Buy,
                PredictedMagnitudePips = 0,
                ConfidenceScore = 0.75m,
                ServedCalibratedProbability = 0.75m,
                DecisionThresholdUsed = 0.50m,
                ActualDirection = correct ? TradeDirection.Buy : TradeDirection.Sell,
                ActualMagnitudePips = correct ? 10m : -10m,
                DirectionCorrect = correct,
                PredictedAt = ts,
                OutcomeRecordedAt = ts.AddMinutes(5),
                IsDeleted = false,
            });
            i++;
        }
        return logs;
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        MLCusumDriftWorker worker) : IDisposable
    {
        public MLCusumDriftWorker Worker { get; } = worker;

        public async Task<List<MLTrainingRun>> LoadTrainingRunsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLCusumDriftWorkerTestContext>();
            return await db.Set<MLTrainingRun>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class MLCusumDriftWorkerTestContext(DbContextOptions<MLCusumDriftWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EngineConfig>(b =>
            {
                b.HasKey(c => c.Id);
                b.HasQueryFilter(c => !c.IsDeleted);
                b.Property(c => c.DataType).HasConversion<string>();
                b.HasIndex(c => c.Key).IsUnique();
            });
            modelBuilder.Entity<MLModel>(b =>
            {
                b.HasKey(m => m.Id);
                b.HasQueryFilter(m => !m.IsDeleted);
                b.Property(m => m.Timeframe).HasConversion<string>();
                b.Property(m => m.Status).HasConversion<string>();
                b.Property(m => m.LearnerArchitecture).HasConversion<string>();
                b.Property(m => m.RowVersion).HasDefaultValue(0u).ValueGeneratedNever();
                b.Ignore(m => m.TrainingRuns);
                b.Ignore(m => m.TradeSignals);
                b.Ignore(m => m.PredictionLogs);
                b.Ignore(m => m.ChampionEvaluations);
                b.Ignore(m => m.ChallengerEvaluations);
                b.Ignore(m => m.CausalFeatureAudits);
                b.Ignore(m => m.ConformalCalibrations);
                b.Ignore(m => m.FeatureInteractionAudits);
                b.Ignore(m => m.LifecycleLogs);
            });
            modelBuilder.Entity<MLModelPredictionLog>(b =>
            {
                b.HasKey(l => l.Id);
                b.HasQueryFilter(l => !l.IsDeleted);
                b.Property(l => l.ModelRole).HasConversion<string>();
                b.Property(l => l.Timeframe).HasConversion<string>();
                b.Property(l => l.PredictedDirection).HasConversion<string>();
                b.Property(l => l.ActualDirection).HasConversion<string>();
                b.Ignore(l => l.TradeSignal);
                b.Ignore(l => l.MLModel);
                b.Ignore(l => l.MLConformalCalibration);
            });
            modelBuilder.Entity<MLTrainingRun>(b =>
            {
                b.HasKey(r => r.Id);
                b.HasQueryFilter(r => !r.IsDeleted);
                b.Property(r => r.Timeframe).HasConversion<string>();
                b.Property(r => r.TriggerType).HasConversion<string>();
                b.Property(r => r.Status).HasConversion<string>();
                b.Property(r => r.LearnerArchitecture).HasConversion<string>();
                b.Ignore(r => r.MLModel);
            });
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

    private sealed class RecordingAlertDispatcher : IAlertDispatcher
    {
        public List<(Alert alert, string message)> Dispatched { get; } = new();
        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
        {
            Dispatched.Add((alert, message));
            return Task.CompletedTask;
        }
        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
            => Task.CompletedTask;
    }
}
