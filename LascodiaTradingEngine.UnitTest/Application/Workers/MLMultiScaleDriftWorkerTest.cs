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

public sealed class MLMultiScaleDriftWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_SuddenDrift_QueuesRetrainAndAlerts()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var dispatcher = new RecordingAlertDispatcher();

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                // Long window 21 days: 60% accuracy. Short window (last 3 days): 0% accuracy.
                // gap = 0 - 0.6 = -0.6 ≪ -0.07 → sudden drift.
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddDays(-20).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 30).Concat(Enumerable.Repeat(false, 20)).ToList()));
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddDays(-2).UtcDateTime,
                    outcomes: Enumerable.Repeat(false, 10),
                    idStart: 1000));
            },
            timeProvider: new TestTimeProvider(now),
            alertDispatcher: dispatcher);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.SuddenDriftCount);
        Assert.Equal(0, result.GradualDriftCount);
        Assert.Equal(1, result.RetrainingQueued);

        var run = Assert.Single(await harness.LoadTrainingRunsAsync());
        Assert.Equal("MultiSignal", run.DriftTriggerType);
        Assert.Equal(0, run.Priority); // sudden drift gets priority 0

        var dispatched = Assert.Single(dispatcher.Dispatched);
        Assert.Equal(AlertSeverity.Critical, dispatched.alert.Severity);
        Assert.Contains("multiscale-drift:EURUSD:H1:sudden", dispatched.alert.DeduplicationKey);
    }

    [Fact]
    public async Task RunCycleAsync_GradualDrift_QueuesRetrainAtHighSeverity()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var dispatcher = new RecordingAlertDispatcher();

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                // Both windows ~40% accurate (well below 50% floor) and not far apart → gradual.
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddDays(-20).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 20).Concat(Enumerable.Repeat(false, 30)).ToList()));
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddDays(-2).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 4).Concat(Enumerable.Repeat(false, 6)).ToList(),
                    idStart: 1000));
            },
            timeProvider: new TestTimeProvider(now),
            alertDispatcher: dispatcher);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.SuddenDriftCount);
        Assert.Equal(1, result.GradualDriftCount);
        Assert.Equal(1, result.RetrainingQueued);

        var dispatched = Assert.Single(dispatcher.Dispatched);
        Assert.Equal(AlertSeverity.High, dispatched.alert.Severity);
    }

    [Fact]
    public async Task RunCycleAsync_NoDrift_DoesNotQueueOrAlert()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var dispatcher = new RecordingAlertDispatcher();

        using var harness = CreateHarness(
            seed: db =>
            {
                SeedActiveModel(db, 1);
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddDays(-20).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 40).Concat(Enumerable.Repeat(false, 10)).ToList()));
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddDays(-2).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 8).Concat(Enumerable.Repeat(false, 2)).ToList(),
                    idStart: 1000));
            },
            timeProvider: new TestTimeProvider(now),
            alertDispatcher: dispatcher);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.SuddenDriftCount);
        Assert.Equal(0, result.GradualDriftCount);
        Assert.Empty(dispatcher.Dispatched);
        Assert.Empty(await harness.LoadTrainingRunsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_RetrainCooldown_SuppressesNewQueue()
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
                    DriftTriggerType = "MultiSignal",
                    StartedAt = now.AddHours(-7).UtcDateTime,
                    CompletedAt = now.AddHours(-6).UtcDateTime,
                    FromDate = now.AddDays(-365).UtcDateTime,
                    ToDate = now.AddHours(-7).UtcDateTime,
                    LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                    IsDeleted = false,
                });
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddDays(-20).UtcDateTime,
                    outcomes: Enumerable.Repeat(true, 30).Concat(Enumerable.Repeat(false, 20)).ToList()));
                db.Set<MLModelPredictionLog>().AddRange(NewLogs(
                    modelId: 1,
                    startUtc: now.AddDays(-2).UtcDateTime,
                    outcomes: Enumerable.Repeat(false, 10),
                    idStart: 1000));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.SuddenDriftCount);
        Assert.Equal(0, result.RetrainingQueued);
        Assert.Single(await harness.LoadTrainingRunsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_LockBusy_Skips()
    {
        using var harness = CreateHarness(
            seed: _ => { },
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
    }

    private static WorkerHarness CreateHarness(
        Action<MLMultiScaleDriftWorkerTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null,
        IAlertDispatcher? alertDispatcher = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLMultiScaleDriftWorkerTestContext>(o => o.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<MLMultiScaleDriftWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<MLMultiScaleDriftWorkerTestContext>());

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLMultiScaleDriftWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLMultiScaleDriftWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLMultiScaleDriftWorker>.Instance,
            distributedLock: distributedLock,
            healthMonitor: null,
            metrics: null,
            timeProvider: timeProvider,
            alertDispatcher: alertDispatcher);

        return new WorkerHarness(provider, connection, worker);
    }

    private static void SeedActiveModel(MLMultiScaleDriftWorkerTestContext db, long id)
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
        long modelId, DateTime startUtc, IEnumerable<bool> outcomes, long idStart = 1)
    {
        var logs = new List<MLModelPredictionLog>();
        int i = 0;
        foreach (var correct in outcomes)
        {
            var ts = startUtc.AddHours(i);
            logs.Add(new MLModelPredictionLog
            {
                Id = idStart + i,
                TradeSignalId = idStart + i,
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
        MLMultiScaleDriftWorker worker) : IDisposable
    {
        public MLMultiScaleDriftWorker Worker { get; } = worker;

        public async Task<List<MLTrainingRun>> LoadTrainingRunsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLMultiScaleDriftWorkerTestContext>();
            return await db.Set<MLTrainingRun>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class MLMultiScaleDriftWorkerTestContext(DbContextOptions<MLMultiScaleDriftWorkerTestContext> options)
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
