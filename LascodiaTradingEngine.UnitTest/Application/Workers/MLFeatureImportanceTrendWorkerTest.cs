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

public class MLFeatureImportanceTrendWorkerTest
{
    [Fact]
    public async Task RunTrendAsync_CreatesAlertAndConfig_WhenNamedFeatureDecaysToThreshold()
    {
        await using var fixture = await FeatureImportanceTrendFixture.CreateAsync();
        var db = fixture.Db;

        var now = DateTime.UtcNow;
        db.Set<EngineConfig>().AddRange(
            Config("MLFeatureImpTrend:AlertDestination", "ml-desk"),
            Config("Alert:Cooldown:MLMonitoring", "1800"));
        SeedModel(db, now.AddDays(-3), MLModelStatus.Superseded, false, ["alpha", "beta"], [0.030, 0.070]);
        SeedModel(db, now.AddDays(-2), MLModelStatus.Superseded, false, ["alpha", "beta"], [0.012, 0.080]);
        SeedModel(db, now.AddDays(-1), MLModelStatus.Active, true, ["alpha", "beta"], [0.003, 0.090]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunTrendAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.CandidatePairCount);
        Assert.Equal(1, result.EvaluatedPairCount);
        Assert.Equal(1, result.DyingFeatureCount);
        Assert.Equal(1, result.AlertsUpserted);
        Assert.Equal(7, result.ConfigRowsWritten);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Equal(AlertType.MLModelDegraded, alert.AlertType);
        Assert.Equal(AlertSeverity.Medium, alert.Severity);
        Assert.Equal("MLFeatureImpTrend:EURUSD:H1", alert.DeduplicationKey);
        Assert.Equal(1800, alert.CooldownSeconds);
        Assert.Null(alert.LastTriggeredAt);
        Assert.Contains("alpha", alert.ConditionJson, StringComparison.Ordinal);
        Assert.Contains("ml-desk", alert.ConditionJson, StringComparison.Ordinal);

        var state = await ConfigValueAsync(db, "MLFeatureImpTrend:EURUSD:H1:EvaluationState");
        var count = await ConfigValueAsync(db, "MLFeatureImpTrend:EURUSD:H1:DyingFeatureCount");
        Assert.Equal("dying_features", state);
        Assert.Equal("1", count);
    }

    [Fact]
    public async Task RunTrendAsync_TracksImportanceByFeatureName_WhenSchemaOrderChanges()
    {
        await using var fixture = await FeatureImportanceTrendFixture.CreateAsync();
        var db = fixture.Db;

        var now = DateTime.UtcNow;
        db.Set<EngineConfig>().AddRange(
            Config("MLFeatureImpTrend:GenerationsToCheck", "2"),
            Config("MLFeatureImpTrend:MinGenerations", "2"));
        SeedModel(db, now.AddDays(-2), MLModelStatus.Superseded, false, ["alpha", "beta"], [0.040, 0.100]);
        SeedModel(db, now.AddDays(-1), MLModelStatus.Active, true, ["beta", "alpha"], [0.200, 0.004]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunTrendAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.DyingFeatureCount);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.Contains("alpha", alert.ConditionJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\":\"beta\"", alert.ConditionJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTrendAsync_AutoResolvesExistingAlert_WhenFeatureTrendRecovers()
    {
        await using var fixture = await FeatureImportanceTrendFixture.CreateAsync();
        var db = fixture.Db;

        var now = DateTime.UtcNow;
        var lastTriggered = now.AddHours(-1);
        SeedModel(db, now.AddDays(-3), MLModelStatus.Superseded, false, ["alpha", "beta"], [0.020, 0.050]);
        SeedModel(db, now.AddDays(-2), MLModelStatus.Superseded, false, ["alpha", "beta"], [0.015, 0.050]);
        SeedModel(db, now.AddDays(-1), MLModelStatus.Active, true, ["alpha", "beta"], [0.020, 0.050]);
        db.Set<Alert>().Add(ExistingAlert("MLFeatureImpTrend:EURUSD:H1", lastTriggered));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunTrendAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.EvaluatedPairCount);
        Assert.Equal(0, result.DyingFeatureCount);
        Assert.Equal(1, result.AlertsResolved);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
        Assert.Equal(lastTriggered, alert.LastTriggeredAt);

        var state = await ConfigValueAsync(db, "MLFeatureImpTrend:EURUSD:H1:EvaluationState");
        var count = await ConfigValueAsync(db, "MLFeatureImpTrend:EURUSD:H1:DyingFeatureCount");
        Assert.Equal("healthy", state);
        Assert.Equal("0", count);
    }

    [Fact]
    public async Task RunTrendAsync_DoesNotResolveUnevaluatedActivePairAlerts_WhenPairLimitTruncates()
    {
        await using var fixture = await FeatureImportanceTrendFixture.CreateAsync();
        var db = fixture.Db;

        var now = DateTime.UtcNow;
        db.Set<EngineConfig>().Add(Config("MLFeatureImpTrend:MaxPairsPerCycle", "1"));
        SeedModel(db, now.AddDays(-1), MLModelStatus.Active, true, ["alpha"], [0.001], symbol: "AUDUSD");
        SeedModel(db, now.AddDays(-1), MLModelStatus.Active, true, ["alpha"], [0.001], symbol: "EURUSD");
        db.Set<Alert>().Add(ExistingAlert("MLFeatureImpTrend:EURUSD:H1", now.AddHours(-1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunTrendAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.CandidatePairCount);
        Assert.Equal(0, result.AlertsResolved);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Null(alert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunTrendAsync_SkipsAllWork_WhenDisabledByConfig()
    {
        await using var fixture = await FeatureImportanceTrendFixture.CreateAsync();
        var db = fixture.Db;

        db.Set<EngineConfig>().Add(Config("MLFeatureImpTrend:Enabled", "off"));
        SeedModel(db, DateTime.UtcNow.AddDays(-1), MLModelStatus.Active, true, ["alpha"], [0.001]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunTrendAsync(db, db, CancellationToken.None);

        Assert.Equal("disabled", result.SkippedReason);
        Assert.Equal(0, result.CandidatePairCount);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
        Assert.False(await db.Set<EngineConfig>().AnyAsync(c => c.Key != "MLFeatureImpTrend:Enabled"));
    }

    [Fact]
    public async Task LoadConfigAsync_NormalizesUnsafeEngineConfigValues()
    {
        await using var fixture = await FeatureImportanceTrendFixture.CreateAsync();
        var db = fixture.Db;

        db.Set<EngineConfig>().AddRange(
            Config("MLFeatureImpTrend:Enabled", "no"),
            Config("MLFeatureImpTrend:InitialDelaySeconds", "-1"),
            Config("MLFeatureImpTrend:PollIntervalSeconds", "1"),
            Config("MLFeatureImpTrend:GenerationsToCheck", "999"),
            Config("MLFeatureImpTrend:MinGenerations", "999"),
            Config("MLFeatureImpTrend:ImportanceDecayThreshold", "NaN"),
            Config("MLFeatureImpTrend:MonotonicTolerance", "-1"),
            Config("MLFeatureImpTrend:MinRelativeDrop", "2"),
            Config("MLFeatureImpTrend:MaxPairsPerCycle", "-5"),
            Config("MLFeatureImpTrend:MaxFeaturesInAlert", "0"),
            Config("MLFeatureImpTrend:LockTimeoutSeconds", "-1"),
            Config("MLFeatureImpTrend:DbCommandTimeoutSeconds", "9999"),
            Config("MLFeatureImpTrend:AlertDestination", "   "),
            Config("Alert:Cooldown:MLMonitoring", "9999999"));
        await db.SaveChangesAsync();

        var config = await MLFeatureImportanceTrendWorker.LoadConfigAsync(
            db,
            new MLFeatureImportanceTrendOptions(),
            CancellationToken.None);

        Assert.False(config.Enabled);
        Assert.Equal(TimeSpan.Zero, config.InitialDelay);
        Assert.Equal(86_400, config.PollSeconds);
        Assert.Equal(4, config.GenerationsToCheck);
        Assert.Equal(3, config.MinGenerations);
        Assert.Equal(0.005, config.ImportanceDecayThreshold);
        Assert.Equal(0.0, config.MonotonicTolerance);
        Assert.Equal(0.50, config.MinRelativeDrop);
        Assert.Equal(1_000, config.MaxPairsPerCycle);
        Assert.Equal(20, config.MaxFeaturesInAlert);
        Assert.Equal(0, config.LockTimeoutSeconds);
        Assert.Equal(30, config.DbCommandTimeoutSeconds);
        Assert.Equal("ml-ops", config.AlertDestination);
        Assert.Equal(3600, config.AlertCooldownSeconds);
    }

    [Fact]
    public async Task RunCycleAsync_ReturnsLockBusy_WhenDistributedLockIsHeldElsewhere()
    {
        await using var fixture = await FeatureImportanceTrendFixture.CreateAsync();
        var db = fixture.Db;

        await using var provider = BuildProvider(db);
        var worker = new MLFeatureImportanceTrendWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLFeatureImportanceTrendWorker>.Instance,
            new BusyDistributedLock());

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
        Assert.Empty(await db.Set<EngineConfig>().ToListAsync());
    }

    private static MLFeatureImportanceTrendWorker CreateWorker(MLFeatureImportanceTrendOptions? options = null)
        => new(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<MLFeatureImportanceTrendWorker>.Instance,
            options: options);

    private static async Task<string> ConfigValueAsync(FeatureImportanceTrendTestDbContext db, string key)
        => (await db.Set<EngineConfig>().SingleAsync(c => c.Key == key)).Value;

    private static MLModel SeedModel(
        FeatureImportanceTrendTestDbContext db,
        DateTime trainedAt,
        MLModelStatus status,
        bool isActive,
        string[] features,
        double[] importance,
        string symbol = "EURUSD",
        Timeframe timeframe = Timeframe.H1)
    {
        var model = new MLModel
        {
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = $"{symbol}-{timeframe}-{trainedAt:yyyyMMddHHmmss}",
            FilePath = "memory",
            Status = status,
            IsActive = isActive,
            TrainingSamples = 100,
            TrainedAt = trainedAt,
            ActivatedAt = isActive ? trainedAt : null,
            RowVersion = 1,
            ModelBytes = JsonSerializer.SerializeToUtf8Bytes(new ModelSnapshot
            {
                Type = "BaggedLogistic",
                Features = features,
                FeatureImportanceScores = importance,
                FeatureSchemaVersion = 7,
                ExpectedInputFeatures = features.Length,
                TrainedOn = trainedAt,
            }),
        };

        db.Set<MLModel>().Add(model);
        return model;
    }

    private static Alert ExistingAlert(string deduplicationKey, DateTime? lastTriggeredAt)
        => new()
        {
            AlertType = AlertType.MLModelDegraded,
            Symbol = "EURUSD",
            DeduplicationKey = deduplicationKey,
            ConditionJson = "{\"reason\":\"feature_importance_monotone_decay\"}",
            Severity = AlertSeverity.Medium,
            CooldownSeconds = 300,
            IsActive = true,
            LastTriggeredAt = lastTriggeredAt,
        };

    private static EngineConfig Config(string key, string value)
        => new()
        {
            Key = key,
            Value = value,
            DataType = ConfigDataType.String,
            IsHotReloadable = true,
            LastUpdatedAt = DateTime.UtcNow,
        };

    private static ServiceProvider BuildProvider(FeatureImportanceTrendTestDbContext db)
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

    private sealed class FeatureImportanceTrendFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private FeatureImportanceTrendFixture(SqliteConnection connection, FeatureImportanceTrendTestDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public FeatureImportanceTrendTestDbContext Db { get; }

        public static async Task<FeatureImportanceTrendFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<FeatureImportanceTrendTestDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new FeatureImportanceTrendTestDbContext(options);
            await db.Database.EnsureCreatedAsync();

            return new FeatureImportanceTrendFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FeatureImportanceTrendTestDbContext(DbContextOptions<FeatureImportanceTrendTestDbContext> options)
        : ApplicationDbContext<FeatureImportanceTrendTestDbContext>(
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
