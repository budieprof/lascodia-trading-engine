using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLFeatureRankShiftWorkerTest
{
    [Fact]
    public async Task RunRankShiftAsync_UpsertsAlertAndState_WhenFeatureRanksShiftBelowThreshold()
    {
        await using var fixture = await FeatureRankShiftFixture.CreateAsync();
        var db = fixture.Db;
        var now = DateTime.UtcNow;

        db.Set<EngineConfig>().AddRange(
            Config("MLFeatureRankShift:TopFeatures", "4"),
            Config("MLFeatureRankShift:RankCorrelationThreshold", "0.5"),
            Config("MLFeatureRankShift:AlertDestination", "ml-desk"),
            Config("Alert:Cooldown:MLMonitoring", "1800"));
        SeedModel(db, now.AddDays(-2), MLModelStatus.Superseded, false, ["alpha", "beta", "gamma", "delta"], [0.4, 0.3, 0.2, 0.1]);
        SeedModel(db, now.AddDays(-1), MLModelStatus.Active, true, ["alpha", "beta", "gamma", "delta"], [0.1, 0.2, 0.3, 0.4]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunRankShiftAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.CandidateModelCount);
        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(1, result.RankShiftCount);
        Assert.Equal(1, result.AlertsUpserted);
        Assert.True(result.ConfigRowsWritten >= 8);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Equal(AlertType.MLModelDegraded, alert.AlertType);
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.Equal("MLFeatureRankShift:EURUSD:H1", alert.DeduplicationKey);
        Assert.Equal(1800, alert.CooldownSeconds);
        Assert.Contains("feature_rank_shift", alert.ConditionJson, StringComparison.Ordinal);
        Assert.Contains("ml-desk", alert.ConditionJson, StringComparison.Ordinal);
        Assert.Contains("delta", alert.ConditionJson, StringComparison.Ordinal);

        var state = await ConfigValueAsync(db, "MLFeatureRankShift:EURUSD:H1:EvaluationState");
        var correlation = await ConfigValueAsync(db, "MLFeatureRankShift:EURUSD:H1:SpearmanCorrelation");
        Assert.Equal("rank_shift", state);
        Assert.True(double.Parse(correlation, CultureInfo.InvariantCulture) < 0.5);
    }

    [Fact]
    public async Task RunRankShiftAsync_AutoResolvesExistingAlert_WhenRankOrderRecovers()
    {
        await using var fixture = await FeatureRankShiftFixture.CreateAsync();
        var db = fixture.Db;
        var now = DateTime.UtcNow;
        var lastTriggered = now.AddHours(-1);

        SeedModel(db, now.AddDays(-2), MLModelStatus.Superseded, false, ["alpha", "beta", "gamma"], [0.5, 0.3, 0.1]);
        SeedModel(db, now.AddDays(-1), MLModelStatus.Active, true, ["alpha", "beta", "gamma"], [0.45, 0.25, 0.15]);
        db.Set<Alert>().Add(ExistingAlert("MLFeatureRankShift:EURUSD:H1", lastTriggered));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunRankShiftAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(0, result.RankShiftCount);
        Assert.Equal(1, result.AlertsResolved);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
        Assert.Equal(lastTriggered, alert.LastTriggeredAt);

        var state = await ConfigValueAsync(db, "MLFeatureRankShift:EURUSD:H1:EvaluationState");
        Assert.Equal("healthy", state);
    }

    [Fact]
    public async Task RunRankShiftAsync_ResolvesExistingAlert_WhenChampionNoLongerHasPredecessor()
    {
        await using var fixture = await FeatureRankShiftFixture.CreateAsync();
        var db = fixture.Db;
        var now = DateTime.UtcNow;

        SeedModel(db, now.AddDays(-1), MLModelStatus.Active, true, ["alpha", "beta", "gamma"], [0.5, 0.3, 0.1]);
        db.Set<Alert>().Add(ExistingAlert("MLFeatureRankShift:EURUSD:H1", now.AddHours(-2)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunRankShiftAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.SkippedModelCount);
        Assert.Equal(1, result.AlertsResolved);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
        Assert.Equal("no_predecessor", await ConfigValueAsync(db, "MLFeatureRankShift:EURUSD:H1:EvaluationState"));
    }

    [Fact]
    public async Task RunRankShiftAsync_DoesNotResolveUnevaluatedActiveModelAlerts_WhenModelLimitTruncates()
    {
        await using var fixture = await FeatureRankShiftFixture.CreateAsync();
        var db = fixture.Db;
        var now = DateTime.UtcNow;

        db.Set<EngineConfig>().Add(Config("MLFeatureRankShift:MaxModelsPerCycle", "1"));
        SeedModel(db, now.AddDays(-1), MLModelStatus.Active, true, ["alpha", "beta", "gamma"], [0.5, 0.3, 0.1], symbol: "AUDUSD");
        SeedModel(db, now.AddDays(-1), MLModelStatus.Active, true, ["alpha", "beta", "gamma"], [0.5, 0.3, 0.1], symbol: "EURUSD");
        db.Set<Alert>().Add(ExistingAlert("MLFeatureRankShift:EURUSD:H1", now.AddHours(-1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunRankShiftAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.CandidateModelCount);
        Assert.Equal(0, result.AlertsResolved);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Null(alert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunRankShiftAsync_UsesNewestEligibleChampion_WhenDuplicateActiveRowsExist()
    {
        await using var fixture = await FeatureRankShiftFixture.CreateAsync();
        var db = fixture.Db;
        var now = DateTime.UtcNow;

        db.Set<EngineConfig>().AddRange(
            Config("MLFeatureRankShift:TopFeatures", "3"),
            Config("MLFeatureRankShift:RankCorrelationThreshold", "0.5"));
        SeedModel(db, now.AddDays(-3), MLModelStatus.Superseded, false, ["alpha", "beta", "gamma"], [0.5, 0.3, 0.1]);
        SeedModel(db, now.AddDays(-2), MLModelStatus.Active, true, ["alpha", "beta", "gamma"], [0.1, 0.3, 0.5]);
        var newest = SeedModel(db, now.AddDays(-1), MLModelStatus.Active, true, ["alpha", "beta", "gamma"], [0.45, 0.25, 0.15]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunRankShiftAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.CandidateModelCount);
        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(0, result.RankShiftCount);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
        Assert.Equal("healthy", await ConfigValueAsync(db, "MLFeatureRankShift:EURUSD:H1:EvaluationState"));
        Assert.Equal(
            newest.Id.ToString(CultureInfo.InvariantCulture),
            await ConfigValueAsync(db, "MLFeatureRankShift:EURUSD:H1:ChampionModelId"));
    }

    [Fact]
    public async Task RunRankShiftAsync_SkipsAllWork_WhenDisabledByConfig()
    {
        await using var fixture = await FeatureRankShiftFixture.CreateAsync();
        var db = fixture.Db;

        db.Set<EngineConfig>().Add(Config("MLFeatureRankShift:Enabled", "off"));
        SeedModel(db, DateTime.UtcNow.AddDays(-1), MLModelStatus.Active, true, ["alpha", "beta", "gamma"], [0.5, 0.3, 0.1]);
        await db.SaveChangesAsync();

        var result = await CreateWorker().RunRankShiftAsync(db, db, CancellationToken.None);

        Assert.Equal("disabled", result.SkippedReason);
        Assert.Equal(0, result.CandidateModelCount);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
    }

    [Fact]
    public async Task LoadConfigAsync_NormalizesUnsafeEngineConfigValues()
    {
        await using var fixture = await FeatureRankShiftFixture.CreateAsync();
        var db = fixture.Db;

        db.Set<EngineConfig>().AddRange(
            Config("MLFeatureRankShift:Enabled", "no"),
            Config("MLFeatureRankShift:InitialDelaySeconds", "-1"),
            Config("MLFeatureRankShift:PollIntervalSeconds", "1"),
            Config("MLFeatureRankShift:TopFeatures", "0"),
            Config("MLFeatureRankShift:MinUnionFeatures", "99"),
            Config("MLFeatureRankShift:RankCorrelationThreshold", "NaN"),
            Config("MLFeatureRankShift:LookbackDays", "0"),
            Config("MLFeatureRankShift:MaxModelsPerCycle", "0"),
            Config("MLFeatureRankShift:MaxDivergingFeaturesInAlert", "0"),
            Config("MLFeatureRankShift:LockTimeoutSeconds", "-1"),
            Config("MLFeatureRankShift:DbCommandTimeoutSeconds", "9999"),
            Config("MLFeatureRankShift:AlertCooldownSeconds", "0"),
            Config("MLFeatureRankShift:AlertDestination", "   "),
            Config("Alert:Cooldown:MLMonitoring", "9999999"));
        await db.SaveChangesAsync();

        var config = await MLFeatureRankShiftWorker.LoadConfigAsync(
            db,
            new MLFeatureRankShiftOptions(),
            CancellationToken.None);

        Assert.False(config.Enabled);
        Assert.Equal(TimeSpan.Zero, config.InitialDelay);
        Assert.Equal(3_600, config.PollSeconds);
        Assert.Equal(10, config.TopFeatures);
        Assert.Equal(3, config.MinUnionFeatures);
        Assert.Equal(0.50, config.RankCorrelationThreshold);
        Assert.Equal(7, config.LookbackDays);
        Assert.Equal(1_000, config.MaxModelsPerCycle);
        Assert.Equal(5, config.MaxDivergingFeaturesInAlert);
        Assert.Equal(0, config.LockTimeoutSeconds);
        Assert.Equal(30, config.DbCommandTimeoutSeconds);
        Assert.Equal(3_600, config.AlertCooldownSeconds);
        Assert.Equal("ml-ops", config.AlertDestination);
    }

    [Fact]
    public async Task RunCycleAsync_ReturnsLockBusy_WhenDistributedLockIsHeldElsewhere()
    {
        await using var fixture = await FeatureRankShiftFixture.CreateAsync();
        var db = fixture.Db;
        await using var provider = BuildProvider(db);
        var worker = new MLFeatureRankShiftWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLFeatureRankShiftWorker>.Instance,
            new BusyDistributedLock());

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
    }

    [Fact]
    public void SpearmanRank_UsesAverageRanksForTies()
    {
        var correlation = MLFeatureRankShiftWorker.SpearmanRank([5, 5, 1, 0], [5, 1, 5, 0]);

        Assert.Equal(0.5, Math.Round(correlation, 6));
    }

    private static MLFeatureRankShiftWorker CreateWorker(MLFeatureRankShiftOptions? options = null)
        => new(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<MLFeatureRankShiftWorker>.Instance,
            options: options);

    private static async Task<string> ConfigValueAsync(FeatureRankShiftTestDbContext db, string key)
        => (await db.Set<EngineConfig>().SingleAsync(c => c.Key == key)).Value;

    private static MLModel SeedModel(
        FeatureRankShiftTestDbContext db,
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
            ConditionJson = "{\"reason\":\"feature_rank_shift\"}",
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

    private static ServiceProvider BuildProvider(FeatureRankShiftTestDbContext db)
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

    private sealed class FeatureRankShiftFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private FeatureRankShiftFixture(SqliteConnection connection, FeatureRankShiftTestDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public FeatureRankShiftTestDbContext Db { get; }

        public static async Task<FeatureRankShiftFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<FeatureRankShiftTestDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new FeatureRankShiftTestDbContext(options);
            await db.Database.EnsureCreatedAsync();

            return new FeatureRankShiftFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FeatureRankShiftTestDbContext(DbContextOptions<FeatureRankShiftTestDbContext> options)
        : ApplicationDbContext<FeatureRankShiftTestDbContext>(
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
