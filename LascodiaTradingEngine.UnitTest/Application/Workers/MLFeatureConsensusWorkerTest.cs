using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLFeatureConsensusWorkerTest
{
    [Fact]
    public async Task RunConsensusAsync_WritesNormalizedCrossArchitectureSnapshot()
    {
        await using var fixture = await FeatureConsensusFixture.CreateAsync();
        var db = fixture.Db;

        var first = SeedModel(db, LearnerArchitecture.BaggedLogistic, [8.0, 2.0]);
        var second = SeedModel(db, LearnerArchitecture.Gbm, [1.0, 3.0]);
        var third = SeedModel(db, LearnerArchitecture.Elm, [0.001, 9.999]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunConsensusAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.SnapshotsWritten);
        Assert.Equal(0, result.PairsSkipped);

        var snapshot = await db.Set<MLFeatureConsensusSnapshot>().SingleAsync();
        Assert.Equal("EURUSD", snapshot.Symbol);
        Assert.Equal(Timeframe.H1, snapshot.Timeframe);
        Assert.Equal(3, snapshot.ContributingModelCount);
        Assert.Equal(2, snapshot.FeatureCount);
        Assert.StartsWith("schema-fp:fp-main:importance:2:", snapshot.SchemaKey, StringComparison.Ordinal);

        var contributorIds = JsonSerializer.Deserialize<long[]>(snapshot.ContributorModelIdsJson)!;
        Assert.Equal([first.Id, second.Id, third.Id], contributorIds.Order().ToArray());

        using var consensusDoc = JsonDocument.Parse(snapshot.FeatureConsensusJson);
        var entries = consensusDoc.RootElement.EnumerateArray().ToArray();
        Assert.Equal("beta", entries[0].GetProperty("Feature").GetString());
        Assert.Equal(0.65, entries[0].GetProperty("MeanImportance").GetDouble(), 2);
        Assert.Equal("alpha", entries[1].GetProperty("Feature").GetString());
        Assert.Equal(0.35, entries[1].GetProperty("MeanImportance").GetDouble(), 2);

        using var sourceDoc = JsonDocument.Parse(snapshot.ImportanceSourceSummaryJson);
        Assert.Equal(3, sourceDoc.RootElement.GetProperty("feature_importance_scores").GetInt32());
    }

    [Fact]
    public async Task RunConsensusAsync_SkipsAllWork_WhenDisabledByConfig()
    {
        await using var fixture = await FeatureConsensusFixture.CreateAsync();
        var db = fixture.Db;

        db.Set<EngineConfig>().Add(Config("MLFeatureConsensus:Enabled", "off"));
        SeedModel(db, LearnerArchitecture.BaggedLogistic, [1.0, 0.0]);
        SeedModel(db, LearnerArchitecture.Gbm, [0.0, 1.0]);
        SeedModel(db, LearnerArchitecture.Elm, [0.5, 0.5]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunConsensusAsync(db, db, CancellationToken.None);

        Assert.Equal("disabled", result.SkippedReason);
        Assert.Empty(await db.Set<MLFeatureConsensusSnapshot>().ToListAsync());
    }

    [Fact]
    public async Task RunConsensusAsync_ExcludesSuppressedMetaInitializerAndInactiveModels()
    {
        await using var fixture = await FeatureConsensusFixture.CreateAsync();
        var db = fixture.Db;

        db.Set<EngineConfig>().AddRange(
            Config("MLFeatureConsensus:MinModelsForConsensus", "2"),
            Config("MLFeatureConsensus:MinArchitecturesForConsensus", "2"));

        var activeA = SeedModel(db, LearnerArchitecture.BaggedLogistic, [1.0, 0.001]);
        var activeB = SeedModel(db, LearnerArchitecture.Gbm, [0.001, 1.0]);
        SeedModel(db, LearnerArchitecture.Elm, [100.0, 0.0], isSuppressed: true);
        SeedModel(db, LearnerArchitecture.AdaBoost, [0.0, 100.0], isMetaLearner: true);
        SeedModel(db, LearnerArchitecture.Rocket, [50.0, 50.0], isMamlInitializer: true);
        SeedModel(db, LearnerArchitecture.TabNet, [50.0, 50.0], isActive: false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunConsensusAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.SnapshotsWritten);
        var snapshot = await db.Set<MLFeatureConsensusSnapshot>().SingleAsync();
        Assert.Equal(2, snapshot.ContributingModelCount);

        var contributorIds = JsonSerializer.Deserialize<long[]>(snapshot.ContributorModelIdsJson)!;
        Assert.Equal([activeA.Id, activeB.Id], contributorIds.Order().ToArray());
    }

    [Fact]
    public async Task RunConsensusAsync_SkipsPair_WhenFreshSnapshotAlreadyExists()
    {
        await using var fixture = await FeatureConsensusFixture.CreateAsync();
        var db = fixture.Db;

        SeedModel(db, LearnerArchitecture.BaggedLogistic, [1.0, 0.0]);
        SeedModel(db, LearnerArchitecture.Gbm, [0.0, 1.0]);
        SeedModel(db, LearnerArchitecture.Elm, [0.5, 0.5]);
        db.Set<MLFeatureConsensusSnapshot>().Add(new MLFeatureConsensusSnapshot
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            FeatureConsensusJson = "[]",
            SchemaKey = "existing",
            FeatureCount = 0,
            ContributingModelCount = 0,
            DetectedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunConsensusAsync(db, db, CancellationToken.None);

        Assert.Equal(0, result.SnapshotsWritten);
        Assert.Equal(1, result.PairsSkipped);
        Assert.Equal(1, await db.Set<MLFeatureConsensusSnapshot>().CountAsync());
    }

    [Fact]
    public async Task RunConsensusAsync_SkipsPair_WhenSchemaGroupLacksArchitectureDiversity()
    {
        await using var fixture = await FeatureConsensusFixture.CreateAsync();
        var db = fixture.Db;

        SeedModel(db, LearnerArchitecture.BaggedLogistic, [1.0, 0.0], modelVersion: "a");
        SeedModel(db, LearnerArchitecture.BaggedLogistic, [0.0, 1.0], modelVersion: "b");
        SeedModel(db, LearnerArchitecture.BaggedLogistic, [0.5, 0.5], modelVersion: "c");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunConsensusAsync(db, db, CancellationToken.None);

        Assert.Equal(0, result.SnapshotsWritten);
        Assert.Equal(1, result.PairsSkipped);
        Assert.Empty(await db.Set<MLFeatureConsensusSnapshot>().ToListAsync());
    }

    [Fact]
    public async Task LoadConfigAsync_NormalizesUnsafeEngineConfigValues()
    {
        await using var fixture = await FeatureConsensusFixture.CreateAsync();
        var db = fixture.Db;

        db.Set<EngineConfig>().AddRange(
            Config("MLFeatureConsensus:Enabled", "no"),
            Config("MLFeatureConsensus:PollIntervalSeconds", "1"),
            Config("MLFeatureConsensus:MinModelsForConsensus", "1"),
            Config("MLFeatureConsensus:MinArchitecturesForConsensus", "999"),
            Config("MLFeatureConsensus:LockTimeoutSeconds", "-1"),
            Config("MLFeatureConsensus:MinSnapshotSpacingSeconds", "999999"),
            Config("MLFeatureConsensus:MaxModelsPerPair", "1"),
            Config("MLFeatureConsensus:MaxPairsPerCycle", "-2"),
            Config("MLFeatureConsensus:DbCommandTimeoutSeconds", "9999"));
        await db.SaveChangesAsync();

        var config = await MLFeatureConsensusWorker.LoadConfigAsync(
            db,
            new MLFeatureConsensusOptions(),
            CancellationToken.None);

        Assert.False(config.Enabled);
        Assert.Equal(3600, config.PollSeconds);
        Assert.Equal(3, config.MinModels);
        Assert.Equal(2, config.MinArchitectures);
        Assert.Equal(0, config.LockTimeoutSeconds);
        Assert.Equal(3600, config.MinSnapshotSpacingSeconds);
        Assert.Equal(128, config.MaxModelsPerPair);
        Assert.Equal(1000, config.MaxPairsPerCycle);
        Assert.Equal(30, config.DbCommandTimeoutSeconds);
    }

    [Fact]
    public async Task RunCycleAsync_ReturnsLockBusy_WhenDistributedLockIsHeldElsewhere()
    {
        await using var fixture = await FeatureConsensusFixture.CreateAsync();
        var db = fixture.Db;
        SeedModel(db, LearnerArchitecture.BaggedLogistic, [1.0, 0.0]);
        SeedModel(db, LearnerArchitecture.Gbm, [0.0, 1.0]);
        SeedModel(db, LearnerArchitecture.Elm, [0.5, 0.5]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await using var provider = BuildProvider(db);
        var worker = new MLFeatureConsensusWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLFeatureConsensusWorker>.Instance,
            new BusyDistributedLock());

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await db.Set<MLFeatureConsensusSnapshot>().ToListAsync());
    }

    private static MLFeatureConsensusWorker CreateWorker(MLFeatureConsensusOptions? options = null)
        => new(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<MLFeatureConsensusWorker>.Instance,
            options: options);

    private static MLModel SeedModel(
        FeatureConsensusTestDbContext db,
        LearnerArchitecture architecture,
        double[] importance,
        string modelVersion = "test",
        bool isActive = true,
        bool isSuppressed = false,
        bool isMetaLearner = false,
        bool isMamlInitializer = false,
        string fingerprint = "fp-main")
    {
        var model = new MLModel
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ModelVersion = $"{modelVersion}-{architecture}",
            FilePath = "memory",
            Status = MLModelStatus.Active,
            IsActive = isActive,
            IsSuppressed = isSuppressed,
            IsMetaLearner = isMetaLearner,
            IsMamlInitializer = isMamlInitializer,
            LearnerArchitecture = architecture,
            TrainingSamples = 100,
            TrainedAt = DateTime.UtcNow.AddMinutes(-(int)architecture - 1),
            ActivatedAt = DateTime.UtcNow.AddMinutes(-5),
            RowVersion = 1,
            ModelBytes = JsonSerializer.SerializeToUtf8Bytes(new ModelSnapshot
            {
                Type = architecture.ToString(),
                Features = ["alpha", "beta"],
                FeatureImportanceScores = importance,
                FeatureSchemaVersion = 7,
                ExpectedInputFeatures = 2,
                FeatureSchemaFingerprint = fingerprint,
                TrainedOn = DateTime.UtcNow.AddMinutes(-(int)architecture - 1),
            }),
        };

        db.Set<MLModel>().Add(model);
        return model;
    }

    private static EngineConfig Config(string key, string value)
        => new()
        {
            Key = key,
            Value = value,
            DataType = ConfigDataType.String,
            IsHotReloadable = true,
            LastUpdatedAt = DateTime.UtcNow,
        };

    private static ServiceProvider BuildProvider(FeatureConsensusTestDbContext db)
    {
        var services = new ServiceCollection();
        services.AddScoped<IReadApplicationDbContext>(_ => new DbAccessor(db));
        services.AddScoped<IWriteApplicationDbContext>(_ => new DbAccessor(db));
        return services.BuildServiceProvider();
    }

    private sealed class DbAccessor(DbContext db) : IReadApplicationDbContext, IWriteApplicationDbContext
    {
        public DbContext GetDbContext() => db;
        public int SaveChanges() => db.SaveChanges();
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => db.SaveChangesAsync(cancellationToken);
    }

    private sealed class BusyDistributedLock : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);
    }

    private sealed class FeatureConsensusFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private FeatureConsensusFixture(SqliteConnection connection, FeatureConsensusTestDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public FeatureConsensusTestDbContext Db { get; }

        public static async Task<FeatureConsensusFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<FeatureConsensusTestDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new FeatureConsensusTestDbContext(options);
            await db.Database.EnsureCreatedAsync();

            return new FeatureConsensusFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FeatureConsensusTestDbContext(DbContextOptions<FeatureConsensusTestDbContext> options)
        : ApplicationDbContext<FeatureConsensusTestDbContext>(
            options,
            new HttpContextAccessor(),
            typeof(WriteApplicationDbContext).Assembly),
            IReadApplicationDbContext,
            IWriteApplicationDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<MLModel>()
                .Property(x => x.RowVersion)
                .IsConcurrencyToken(false)
                .ValueGeneratedNever();
        }
    }
}
