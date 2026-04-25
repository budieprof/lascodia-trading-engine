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

public sealed class MLAverageWeightInitWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_WritesInitializerFromCompatibleActiveSources()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(db =>
        {
            AddConfig(db, "MLAvgWeightInit:MinRunsPerSourceContext", "2");
            AddConfig(db, "MLAvgWeightInit:MinSourceModelsPerInitializer", "5");

            for (int index = 0; index < 5; index++)
            {
                SeedQualifiedSource(
                    db,
                    modelId: 100 + index,
                    runIdStart: 1000 + index * 10,
                    symbol: $"SYM{index + 1}",
                    timeframe: Timeframe.H1,
                    trainedAtUtc: now.AddHours(-(index + 1)).UtcDateTime,
                    snapshot: CreateBaggedSnapshot(seed: index + 1));
            }
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var initializer = Assert.Single(await harness.LoadInitializersAsync());
        var auditRun = Assert.Single(await harness.LoadInitializerRunsAsync());
        var snapshot = DeserializeSnapshot(initializer.ModelBytes);

        Assert.Null(result.SkippedReason);
        Assert.Equal(5, result.SourceModelsEvaluated);
        Assert.Equal(1, result.ClustersEvaluated);
        Assert.Equal(1, result.InitializersWritten);
        Assert.True(initializer.IsMamlInitializer);
        Assert.True(initializer.IsActive);
        Assert.Equal("ALL", initializer.Symbol);
        Assert.Equal(LearnerArchitecture.BaggedLogistic, initializer.LearnerArchitecture);
        Assert.StartsWith("avgwi-baggedlogistic-h1-", initializer.ModelVersion, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(initializer.DatasetHash));
        Assert.Equal(initializer.Id, auditRun.MLModelId);
        Assert.True(auditRun.IsMamlRun);
        Assert.Equal(0, auditRun.MamlInnerSteps);

        AssertArrayEqual(new[] { 4d, 5d }, snapshot.Weights[0], 10);
        AssertArrayEqual(new[] { 6d, 7d }, snapshot.Weights[1], 10);
        AssertArrayEqual(new[] { 0.3d, 0.6d }, snapshot.Biases, 10);
        AssertArrayEqual(new[] { 13f, 23f }, snapshot.Means, 5);
        AssertArrayEqual(new[] { 4f, 5f }, snapshot.Stds, 5);
        AssertArrayEqual(new[] { 0.03d, 0.06d }, snapshot.FeatureImportanceScores, 10);
        AssertArrayEqual(new[] { 0.09f, 0.12f }, snapshot.FeatureImportance, 5);
        AssertArrayEqual(new[] { 0.15d, 0.18d }, snapshot.MetaWeights, 10);
        Assert.Equal(0.21d, snapshot.MetaBias, 10);
        AssertArrayEqual(new[] { 0.24d, 0.27d }, snapshot.LearnerAccuracyWeights, 10);
        Assert.Equal(0, snapshot.ParentModelId);
        Assert.Equal(0, snapshot.GenerationNumber);
    }

    [Fact]
    public async Task RunCycleAsync_SkipsWhenLargestCompatibilityClusterIsTooSmall()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(db =>
        {
            AddConfig(db, "MLAvgWeightInit:MinRunsPerSourceContext", "2");
            AddConfig(db, "MLAvgWeightInit:MinSourceModelsPerInitializer", "4");

            for (int index = 0; index < 3; index++)
            {
                SeedQualifiedSource(
                    db,
                    modelId: 200 + index,
                    runIdStart: 2000 + index * 10,
                    symbol: $"AAA{index + 1}",
                    timeframe: Timeframe.H1,
                    trainedAtUtc: now.AddHours(-(index + 1)).UtcDateTime,
                    snapshot: CreateBaggedSnapshot(seed: index + 1, featureSchemaFingerprint: "schema-a"));
            }

            for (int index = 0; index < 2; index++)
            {
                SeedQualifiedSource(
                    db,
                    modelId: 300 + index,
                    runIdStart: 3000 + index * 10,
                    symbol: $"BBB{index + 1}",
                    timeframe: Timeframe.H1,
                    trainedAtUtc: now.AddHours(-(index + 4)).UtcDateTime,
                    snapshot: CreateBaggedSnapshot(seed: index + 4, featureSchemaFingerprint: "schema-b"));
            }
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(0, result.InitializersWritten);
        Assert.Equal(1, result.ClustersEvaluated);
        Assert.Empty(await harness.LoadInitializersAsync());
        Assert.Empty(await harness.LoadInitializerRunsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_SkipsRewriteWhenSourceFingerprintIsUnchanged()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(db =>
        {
            AddConfig(db, "MLAvgWeightInit:MinRunsPerSourceContext", "2");
            AddConfig(db, "MLAvgWeightInit:MinSourceModelsPerInitializer", "5");

            for (int index = 0; index < 5; index++)
            {
                SeedQualifiedSource(
                    db,
                    modelId: 400 + index,
                    runIdStart: 4000 + index * 10,
                    symbol: $"CCC{index + 1}",
                    timeframe: Timeframe.H1,
                    trainedAtUtc: now.AddHours(-(index + 1)).UtcDateTime,
                    snapshot: CreateBaggedSnapshot(seed: index + 1));
            }
        }, new TestTimeProvider(now));

        await harness.Worker.RunCycleAsync(CancellationToken.None);
        var existingInitializer = Assert.Single(await harness.LoadInitializersAsync());

        var secondResult = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var initializers = await harness.LoadInitializersAsync();

        Assert.Null(secondResult.SkippedReason);
        Assert.Equal(0, secondResult.InitializersWritten);
        Assert.Single(initializers);
        Assert.Equal(existingInitializer.Id, initializers[0].Id);
    }

    [Fact]
    public async Task RunCycleAsync_SupersedesPreviousInitializerWhenSourcesChange()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(db =>
        {
            AddConfig(db, "MLAvgWeightInit:MinRunsPerSourceContext", "2");
            AddConfig(db, "MLAvgWeightInit:MinSourceModelsPerInitializer", "5");

            for (int index = 0; index < 5; index++)
            {
                SeedQualifiedSource(
                    db,
                    modelId: 500 + index,
                    runIdStart: 5000 + index * 10,
                    symbol: $"DDD{index + 1}",
                    timeframe: Timeframe.H1,
                    trainedAtUtc: now.AddHours(-(index + 1)).UtcDateTime,
                    snapshot: CreateBaggedSnapshot(seed: index + 1));
            }
        }, new TestTimeProvider(now));

        await harness.Worker.RunCycleAsync(CancellationToken.None);
        var firstInitializer = Assert.Single(await harness.LoadInitializersAsync());

        await harness.SeedAsync(db =>
        {
            var mutatedSource = db.Set<MLModel>().Single(model => model.Id == 504);
            mutatedSource.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(CreateBaggedSnapshot(seed: 99));
            mutatedSource.TrainedAt = now.AddHours(1).UtcDateTime;
        });

        var secondResult = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var allModels = await harness.LoadAllInitializerModelsAsync();
        var activeInitializer = Assert.Single(allModels, model => model.IsActive);
        var supersededInitializer = Assert.Single(allModels, model => !model.IsActive);

        Assert.Null(secondResult.SkippedReason);
        Assert.Equal(1, secondResult.InitializersWritten);
        Assert.Equal(2, allModels.Count);
        Assert.Equal(firstInitializer.Id, supersededInitializer.Id);
        Assert.True(supersededInitializer.IsMamlInitializer);
        Assert.Equal(MLModelStatus.Superseded, supersededInitializer.Status);
        Assert.NotEqual(firstInitializer.Id, activeInitializer.Id);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutWritingInitializer()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(db =>
        {
            AddConfig(db, "MLAvgWeightInit:MinRunsPerSourceContext", "2");
            AddConfig(db, "MLAvgWeightInit:MinSourceModelsPerInitializer", "5");

            for (int index = 0; index < 5; index++)
            {
                SeedQualifiedSource(
                    db,
                    modelId: 600 + index,
                    runIdStart: 6000 + index * 10,
                    symbol: $"EEE{index + 1}",
                    timeframe: Timeframe.H1,
                    trainedAtUtc: now.AddHours(-(index + 1)).UtcDateTime,
                    snapshot: CreateBaggedSnapshot(seed: index + 1));
            }
        }, new TestTimeProvider(now), new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await harness.LoadInitializersAsync());
    }

    [Fact]
    public async Task RunCycleAsync_InvalidSettingsAreClampedSafely()
    {
        using var harness = CreateHarness(db =>
        {
            AddConfig(db, "MLAvgWeightInit:PollIntervalSeconds", "-1");
            AddConfig(db, "MLAvgWeightInit:MinRunsPerSourceContext", "0");
            AddConfig(db, "MLAvgWeightInit:MinSourceModelsPerInitializer", "0");
            AddConfig(db, "MLAvgWeightInit:LockTimeoutSeconds", "-5");
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromHours(48), result.Settings.PollInterval);
        Assert.Equal(5, result.Settings.MinRunsPerSourceContext);
        Assert.Equal(5, result.Settings.MinSourceModelsPerInitializer);
        Assert.Equal(5, result.Settings.LockTimeoutSeconds);
    }

    private static WorkerHarness CreateHarness(
        Action<MLAverageWeightInitWorkerTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var effectiveTimeProvider = timeProvider ?? new TestTimeProvider(now);

        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLAverageWeightInitWorkerTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetRequiredService<MLAverageWeightInitWorkerTestContext>());

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLAverageWeightInitWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLAverageWeightInitWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLAverageWeightInitWorker>.Instance,
            metrics: null,
            timeProvider: effectiveTimeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, connection, worker);
    }

    private static void SeedQualifiedSource(
        MLAverageWeightInitWorkerTestContext db,
        long modelId,
        long runIdStart,
        string symbol,
        Timeframe timeframe,
        DateTime trainedAtUtc,
        ModelSnapshot snapshot)
    {
        db.Set<MLModel>().Add(new MLModel
        {
            Id = modelId,
            Symbol = symbol,
            Timeframe = timeframe,
            LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
            ModelVersion = $"source-{modelId}",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainedAt = trainedAtUtc,
            ActivatedAt = trainedAtUtc,
            ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot),
            IsDeleted = false,
            RowVersion = 1
        });

        for (int runIndex = 0; runIndex < 2; runIndex++)
        {
            db.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Id = runIdStart + runIndex,
                Symbol = symbol,
                Timeframe = timeframe,
                TriggerType = TriggerType.Scheduled,
                Status = RunStatus.Completed,
                FromDate = trainedAtUtc.AddDays(-365),
                ToDate = trainedAtUtc.AddHours(-1),
                StartedAt = trainedAtUtc.AddHours(-2),
                CompletedAt = trainedAtUtc.AddMinutes(runIndex),
                MLModelId = modelId,
                LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                IsDeleted = false
            });
        }
    }

    private static ModelSnapshot CreateBaggedSnapshot(
        int seed,
        string featureSchemaFingerprint = "schema-v2",
        string preprocessingFingerprint = "prep-v1")
    {
        return new ModelSnapshot
        {
            Type = "BaggedLogisticEnsemble",
            Version = $"bagged-{seed}",
            FeatureSchemaVersion = 2,
            Features = ["f1", "f2"],
            FeatureSchemaFingerprint = featureSchemaFingerprint,
            PreprocessingFingerprint = preprocessingFingerprint,
            RawFeatureIndices = [0, 1],
            FeaturePipelineTransforms = [],
            ActiveFeatureMask = [true, true],
            FeatureSubsetIndices =
            [
                [0, 1],
                [0, 1]
            ],
            BaseLearnersK = 2,
            Weights =
            [
                [seed + 1d, seed + 2d],
                [seed + 3d, seed + 4d]
            ],
            Biases = [seed * 0.1d, seed * 0.2d],
            Means = [seed + 10f, seed + 20f],
            Stds = [seed + 1f, seed + 2f],
            FeatureImportanceScores = [seed * 0.01d, seed * 0.02d],
            FeatureImportance = [seed * 0.03f, seed * 0.04f],
            MetaWeights = [seed * 0.05d, seed * 0.06d],
            MetaBias = seed * 0.07d,
            LearnerAccuracyWeights = [seed * 0.08d, seed * 0.09d],
            TrainSamples = 100 + seed,
            TrainSamplesAtLastCalibration = 100 + seed,
            TrainedOn = new DateTime(2026, 04, 20, 12, 0, 0, DateTimeKind.Utc),
            ParentModelId = seed,
            GenerationNumber = seed
        };
    }

    private static ModelSnapshot DeserializeSnapshot(byte[]? bytes)
    {
        Assert.NotNull(bytes);
        return JsonSerializer.Deserialize<ModelSnapshot>(bytes!) ?? throw new InvalidOperationException("Snapshot bytes could not be deserialized.");
    }

    private static void AssertArrayEqual(double[] expected, double[] actual, int precision)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], actual[index], precision);
    }

    private static void AssertArrayEqual(float[] expected, float[] actual, int precision)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], actual[index], precision);
    }

    private static void AddConfig(
        MLAverageWeightInitWorkerTestContext db,
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
        MLAverageWeightInitWorker worker) : IDisposable
    {
        public MLAverageWeightInitWorker Worker { get; } = worker;

        public async Task SeedAsync(Action<MLAverageWeightInitWorkerTestContext> seed)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAverageWeightInitWorkerTestContext>();
            seed(db);
            await db.SaveChangesAsync();
        }

        public async Task<List<MLModel>> LoadInitializersAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAverageWeightInitWorkerTestContext>();
            return await db.Set<MLModel>()
                .AsNoTracking()
                .Where(model => model.Symbol == "ALL" && model.IsMamlInitializer && model.IsActive)
                .OrderBy(model => model.Id)
                .ToListAsync();
        }

        public async Task<List<MLModel>> LoadAllInitializerModelsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAverageWeightInitWorkerTestContext>();
            return await db.Set<MLModel>()
                .AsNoTracking()
                .Where(model => model.Symbol == "ALL" && model.IsMamlInitializer)
                .OrderBy(model => model.Id)
                .ToListAsync();
        }

        public async Task<List<MLTrainingRun>> LoadInitializerRunsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAverageWeightInitWorkerTestContext>();
            return await db.Set<MLTrainingRun>()
                .AsNoTracking()
                .Where(run => run.Symbol == "ALL" && run.IsMamlRun)
                .OrderBy(run => run.Id)
                .ToListAsync();
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class MLAverageWeightInitWorkerTestContext(DbContextOptions<MLAverageWeightInitWorkerTestContext> options)
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
                builder.Property(model => model.DatasetHash).HasMaxLength(64);

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
