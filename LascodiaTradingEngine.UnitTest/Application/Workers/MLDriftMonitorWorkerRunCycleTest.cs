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

/// <summary>
/// Tests for the new internal <c>RunCycleAsync</c> entrypoint added by the deep
/// hardening pass. The original Moq-based test class continues to verify behaviour
/// through the StartAsync loop; this class verifies the deterministic cycle path.
/// </summary>
public sealed class MLDriftMonitorWorkerRunCycleTest
{
    [Fact]
    public async Task RunCycleAsync_LockBusy_ReturnsLockBusySkippedReason()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: _ => { },
            timeProvider: new TestTimeProvider(now),
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(0, result.CandidateModelCount);
        Assert.Equal(0, result.RetrainingQueued);
    }

    [Fact]
    public async Task RunCycleAsync_NoActiveModels_ReturnsZeroCandidates()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: _ => { },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(0, result.CandidateModelCount);
        Assert.Equal(0, result.RetrainingQueued);
    }

    [Fact]
    public async Task RunCycleAsync_ExpiredModel_QueuesEmergencyRetrain()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                db.Set<MLModel>().Add(new MLModel
                {
                    Id = 1,
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    ModelVersion = "1.0.0",
                    FilePath = "/tmp/model.bin",
                    Status = MLModelStatus.Active,
                    IsActive = true,
                    TrainedAt = now.AddDays(-200).UtcDateTime,
                    ActivatedAt = now.AddDays(-200).UtcDateTime, // way past 90-day default expiry
                    ModelBytes = [1, 2, 3],
                    LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                    IsDeleted = false,
                    RowVersion = 1,
                });
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.CandidateModelCount);
        Assert.True(result.RetrainingQueued >= 1);

        var runs = await harness.LoadTrainingRunsAsync();
        Assert.Contains(runs, r => r.DriftTriggerType == "ModelExpiry" && r.Priority == 0);
    }

    [Fact]
    public async Task RunCycleAsync_FreshModel_NoRetrainQueued()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                db.Set<MLModel>().Add(new MLModel
                {
                    Id = 1,
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    ModelVersion = "1.0.0",
                    FilePath = "/tmp/model.bin",
                    Status = MLModelStatus.Active,
                    IsActive = true,
                    TrainedAt = now.AddDays(-3).UtcDateTime,
                    ActivatedAt = now.AddDays(-3).UtcDateTime,
                    ModelBytes = [1, 2, 3],
                    LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                    IsDeleted = false,
                    RowVersion = 1,
                });
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.CandidateModelCount);
        Assert.Equal(0, result.RetrainingQueued);
        Assert.Empty(await harness.LoadTrainingRunsAsync());
    }

    private static WorkerHarness CreateHarness(
        Action<MLDriftMonitorWorkerTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null,
        IAlertDispatcher? alertDispatcher = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLDriftMonitorWorkerTestContext>(o => o.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<MLDriftMonitorWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<MLDriftMonitorWorkerTestContext>());

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLDriftMonitorWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLDriftMonitorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLDriftMonitorWorker>.Instance,
            distributedLock: distributedLock,
            healthMonitor: null,
            metrics: null,
            timeProvider: timeProvider,
            alertDispatcher: alertDispatcher);

        return new WorkerHarness(provider, connection, worker);
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        MLDriftMonitorWorker worker) : IDisposable
    {
        public MLDriftMonitorWorker Worker { get; } = worker;

        public async Task<List<MLTrainingRun>> LoadTrainingRunsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLDriftMonitorWorkerTestContext>();
            return await db.Set<MLTrainingRun>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class MLDriftMonitorWorkerTestContext(DbContextOptions<MLDriftMonitorWorkerTestContext> options)
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
}
