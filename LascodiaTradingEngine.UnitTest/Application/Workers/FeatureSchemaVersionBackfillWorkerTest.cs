using System.Text.Json;
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

public sealed class FeatureSchemaVersionBackfillWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_BackfillsLegacySnapshotAndMarksComplete()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<MLModel>().Add(NewModel(
                1,
                new ModelSnapshot
                {
                    Type = "BaggedLogistic",
                    Version = "1.0.0",
                    Features = FeatureNames(MLFeatureHelper.FeatureCount),
                    Means = new float[MLFeatureHelper.FeatureCount],
                    Stds = new float[MLFeatureHelper.FeatureCount]
                }));
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var model = Assert.Single(await harness.LoadModelsAsync());
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!);
        var flag = await harness.LoadCompletionFlagAsync();

        Assert.True(result.Completed);
        Assert.Equal(1, result.LegacyCandidateCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.UnresolvedCount);
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.FeatureSchemaVersion);
        Assert.NotNull(flag);
        Assert.Equal("true", flag!.Value);
        Assert.Equal(ConfigDataType.Bool, flag.DataType);
        Assert.False(flag.IsDeleted);
    }

    [Fact]
    public async Task RunCycleAsync_ConflictingEvidenceLeavesRowUnresolvedAndKeepsFlagFalse()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<MLModel>().Add(NewModel(
                1,
                new ModelSnapshot
                {
                    Type = "BaggedLogistic",
                    Version = "1.0.0",
                    Features = FeatureNames(MLFeatureHelper.FeatureCount),
                    Means = new float[MLFeatureHelper.FeatureCountV2],
                    Stds = new float[MLFeatureHelper.FeatureCountV2]
                }));
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var model = Assert.Single(await harness.LoadModelsAsync());
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!);
        var flag = await harness.LoadCompletionFlagAsync();

        Assert.False(result.Completed);
        Assert.Equal(1, result.LegacyCandidateCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.UnresolvedCount);
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.FeatureSchemaVersion);
        Assert.NotNull(flag);
        Assert.Equal("false", flag!.Value);
        Assert.Contains("Partial", flag.Description);
    }

    [Fact]
    public async Task RunCycleAsync_WhenAlreadyCompleted_SkipsWithoutTouchingModels()
    {
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().Add(new EngineConfig
            {
                Key = "Migration:FeatureSchemaVersionBackfill:Completed",
                Value = "true",
                DataType = ConfigDataType.Bool,
                IsDeleted = false
            });

            db.Set<MLModel>().Add(NewModel(
                1,
                new ModelSnapshot
                {
                    Type = "BaggedLogistic",
                    Version = "1.0.0",
                    Features = FeatureNames(MLFeatureHelper.FeatureCount)
                }));
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var model = Assert.Single(await harness.LoadModelsAsync());
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!);

        Assert.Equal("already_completed", result.SkippedReason);
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.FeatureSchemaVersion);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutWriting()
    {
        using var harness = CreateHarness(db =>
        {
            db.Set<MLModel>().Add(NewModel(
                1,
                new ModelSnapshot
                {
                    Type = "BaggedLogistic",
                    Version = "1.0.0",
                    Features = FeatureNames(MLFeatureHelper.FeatureCount)
                }));
        }, distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var model = Assert.Single(await harness.LoadModelsAsync());
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.FeatureSchemaVersion);
    }

    [Fact]
    public async Task RunCycleAsync_InvalidBatchSize_IsClamped()
    {
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().Add(new EngineConfig
            {
                Key = "Migration:FeatureSchemaVersionBackfill:BatchSize",
                Value = "0",
                IsDeleted = false
            });
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.Settings.BatchSize);
    }

    private static WorkerHarness CreateHarness(
        Action<FeatureSchemaVersionBackfillTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<FeatureSchemaVersionBackfillTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<FeatureSchemaVersionBackfillTestContext>());

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FeatureSchemaVersionBackfillTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new FeatureSchemaVersionBackfillWorker(
            NullLogger<FeatureSchemaVersionBackfillWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            metrics: null,
            timeProvider: timeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, connection, worker);
    }

    private static MLModel NewModel(long id, ModelSnapshot snapshot)
        => new()
        {
            Id = id,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ModelVersion = "1.0.0",
            FilePath = string.Empty,
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainedAt = new DateTime(2026, 04, 24, 12, 0, 0, DateTimeKind.Utc),
            ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot),
            LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
            IsDeleted = false,
            RowVersion = 1
        };

    private static string[] FeatureNames(int count)
        => Enumerable.Range(0, count)
            .Select(index => $"Feature{index}")
            .ToArray();

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        FeatureSchemaVersionBackfillWorker worker) : IDisposable
    {
        public FeatureSchemaVersionBackfillWorker Worker { get; } = worker;

        public async Task<List<MLModel>> LoadModelsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FeatureSchemaVersionBackfillTestContext>();
            return await db.Set<MLModel>()
                .OrderBy(model => model.Id)
                .ToListAsync();
        }

        public async Task<EngineConfig?> LoadCompletionFlagAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FeatureSchemaVersionBackfillTestContext>();
            return await db.Set<EngineConfig>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(config => config.Key == "Migration:FeatureSchemaVersionBackfill:Completed");
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class FeatureSchemaVersionBackfillTestContext(DbContextOptions<FeatureSchemaVersionBackfillTestContext> options)
        : DbContext(options), IWriteApplicationDbContext
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
