using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLFeatureDataFreshnessWorkerTest
{
    [Fact]
    public async Task RunFreshnessAsync_WritesStaleFlagsAndCreatesAlerts_WhenSourcesAreMissingOrOld()
    {
        await using var fixture = await FeatureFreshnessFixture.CreateAsync();
        var db = fixture.Db;

        var now = DateTime.UtcNow;
        db.Set<EngineConfig>().AddRange(
            Config("MLFeatureStale:AlertDestination", "risk-desk"),
            Config("Alert:Cooldown:MLMonitoring", "1200"));
        SeedSentiment(db, now.AddHours(-48));
        SeedModel(db, "EURUSD", Timeframe.M5);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunFreshnessAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(3, result.SourceCount);
        Assert.Equal(3, result.StaleSourceCount);
        Assert.Equal(3, result.AlertsUpserted);
        Assert.Equal(0, result.AlertsResolved);
        Assert.Equal(1, result.ActiveCandlePairCount);
        Assert.Equal(12, result.ConfigRowsWritten);

        var configs = await db.Set<EngineConfig>()
            .Where(c => c.Key.StartsWith("MLFeatureStale:") && c.Key.EndsWith(":IsStale"))
            .ToDictionaryAsync(c => c.Key, c => c.Value);

        Assert.Equal("true", configs["MLFeatureStale:COT:IsStale"]);
        Assert.Equal("true", configs["MLFeatureStale:Sentiment:IsStale"]);
        Assert.Equal("true", configs["MLFeatureStale:Candle:EURUSD:M5:IsStale"]);

        var alerts = await db.Set<Alert>()
            .OrderBy(a => a.DeduplicationKey)
            .ToListAsync();

        Assert.Equal(3, alerts.Count);
        Assert.All(alerts, alert =>
        {
            Assert.True(alert.IsActive);
            Assert.Equal(AlertType.DataQualityIssue, alert.AlertType);
            Assert.Equal(1200, alert.CooldownSeconds);
            Assert.Null(alert.LastTriggeredAt);
            Assert.Contains("risk-desk", alert.ConditionJson, StringComparison.Ordinal);
        });

        Assert.Equal(AlertSeverity.Critical, alerts.Single(a => a.DeduplicationKey == "MLFeatureStale:COT").Severity);
        Assert.Equal(AlertSeverity.Medium, alerts.Single(a => a.DeduplicationKey == "MLFeatureStale:Sentiment").Severity);
        Assert.Equal(AlertSeverity.Critical, alerts.Single(a => a.DeduplicationKey == "MLFeatureStale:Candle:EURUSD:M5").Severity);
    }

    [Fact]
    public async Task RunFreshnessAsync_WritesFreshFlagsAndAutoResolvesExistingAlerts_WhenSourcesRecover()
    {
        await using var fixture = await FeatureFreshnessFixture.CreateAsync();
        var db = fixture.Db;

        var now = DateTime.UtcNow;
        var lastTriggered = now.AddMinutes(-10);
        SeedCot(db, now.AddDays(-2));
        SeedSentiment(db, now.AddHours(-2));
        SeedModel(db, "EURUSD", Timeframe.H1);
        SeedCandle(db, "EURUSD", Timeframe.H1, now.AddMinutes(-30));
        db.Set<Alert>().AddRange(
            ExistingAlert("MLFeatureStale:COT", lastTriggered),
            ExistingAlert("MLFeatureStale:Sentiment", lastTriggered),
            ExistingAlert("MLFeatureStale:Candle:EURUSD:H1", lastTriggered, "EURUSD"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunFreshnessAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(3, result.SourceCount);
        Assert.Equal(0, result.StaleSourceCount);
        Assert.Equal(0, result.AlertsUpserted);
        Assert.Equal(3, result.AlertsResolved);

        var configs = await db.Set<EngineConfig>()
            .Where(c => c.Key.StartsWith("MLFeatureStale:") && c.Key.EndsWith(":IsStale"))
            .ToDictionaryAsync(c => c.Key, c => c.Value);

        Assert.Equal("false", configs["MLFeatureStale:COT:IsStale"]);
        Assert.Equal("false", configs["MLFeatureStale:Sentiment:IsStale"]);
        Assert.Equal("false", configs["MLFeatureStale:Candle:EURUSD:H1:IsStale"]);

        var alerts = await db.Set<Alert>().OrderBy(a => a.DeduplicationKey).ToListAsync();
        Assert.All(alerts, alert =>
        {
            Assert.False(alert.IsActive);
            Assert.NotNull(alert.AutoResolvedAt);
            Assert.Equal(lastTriggered, alert.LastTriggeredAt);
        });
    }

    [Fact]
    public async Task RunFreshnessAsync_IgnoresSuppressedMetaInitializerAndInactiveModels()
    {
        await using var fixture = await FeatureFreshnessFixture.CreateAsync();
        var db = fixture.Db;

        var now = DateTime.UtcNow;
        SeedCot(db, now.AddDays(-1));
        SeedSentiment(db, now.AddHours(-1));
        SeedModel(db, "GBPUSD", Timeframe.H1, isSuppressed: true);
        SeedModel(db, "USDJPY", Timeframe.H1, isMetaLearner: true);
        SeedModel(db, "AUDUSD", Timeframe.H1, isMamlInitializer: true);
        SeedModel(db, "EURJPY", Timeframe.H1, isActive: false);
        db.Set<Alert>().Add(ExistingAlert("MLFeatureStale:Candle:GBPUSD:H1", now.AddHours(-1), "GBPUSD"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunFreshnessAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(2, result.SourceCount);
        Assert.Equal(0, result.ActiveCandlePairCount);
        Assert.Equal(1, result.AlertsResolved);
        Assert.False(await db.Set<EngineConfig>().AnyAsync(c => c.Key.StartsWith("MLFeatureStale:Candle:")));

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunFreshnessAsync_SkipsAllWork_WhenDisabledByConfig()
    {
        await using var fixture = await FeatureFreshnessFixture.CreateAsync();
        var db = fixture.Db;

        db.Set<EngineConfig>().Add(Config("MLFeatureStale:Enabled", "off"));
        SeedModel(db, "EURUSD", Timeframe.H1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateWorker().RunFreshnessAsync(db, db, CancellationToken.None);

        Assert.Equal("disabled", result.SkippedReason);
        Assert.Equal(0, result.SourceCount);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
        Assert.False(await db.Set<EngineConfig>().AnyAsync(c => c.Key != "MLFeatureStale:Enabled"));
    }

    [Fact]
    public async Task LoadConfigAsync_NormalizesUnsafeEngineConfigValues()
    {
        await using var fixture = await FeatureFreshnessFixture.CreateAsync();
        var db = fixture.Db;

        db.Set<EngineConfig>().AddRange(
            Config("MLFeatureStale:Enabled", "no"),
            Config("MLFeatureStale:InitialDelaySeconds", "-1"),
            Config("MLFeatureStale:PollIntervalSeconds", "1"),
            Config("MLFeatureStale:MaxCotAgeDays", "999"),
            Config("MLFeatureStale:MaxSentimentAgeHours", "999"),
            Config("MLFeatureStale:CandleStaleMultiplier", "NaN"),
            Config("MLFeatureStale:MaxPairsPerCycle", "-5"),
            Config("MLFeatureStale:LockTimeoutSeconds", "-1"),
            Config("MLFeatureStale:DbCommandTimeoutSeconds", "9999"),
            Config("MLFeatureStale:AlertDestination", "   "),
            Config("Alert:Cooldown:MLMonitoring", "9999999"));
        await db.SaveChangesAsync();

        var config = await MLFeatureDataFreshnessWorker.LoadConfigAsync(
            db,
            new MLFeatureDataFreshnessOptions(),
            CancellationToken.None);

        Assert.False(config.Enabled);
        Assert.Equal(TimeSpan.Zero, config.InitialDelay);
        Assert.Equal(1800, config.PollSeconds);
        Assert.Equal(10, config.MaxCotAgeDays);
        Assert.Equal(24, config.MaxSentimentAgeHours);
        Assert.Equal(3.0, config.CandleStaleMultiplier);
        Assert.Equal(5000, config.MaxPairsPerCycle);
        Assert.Equal(0, config.LockTimeoutSeconds);
        Assert.Equal(30, config.DbCommandTimeoutSeconds);
        Assert.Equal("ml-ops", config.AlertDestination);
        Assert.Equal(3600, config.AlertCooldownSeconds);
    }

    [Fact]
    public async Task RunCycleAsync_ReturnsLockBusy_WhenDistributedLockIsHeldElsewhere()
    {
        await using var fixture = await FeatureFreshnessFixture.CreateAsync();
        var db = fixture.Db;

        await using var provider = BuildProvider(db);
        var worker = new MLFeatureDataFreshnessWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLFeatureDataFreshnessWorker>.Instance,
            new BusyDistributedLock());

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
        Assert.Empty(await db.Set<EngineConfig>().ToListAsync());
    }

    private static MLFeatureDataFreshnessWorker CreateWorker(MLFeatureDataFreshnessOptions? options = null)
        => new(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<MLFeatureDataFreshnessWorker>.Instance,
            options: options);

    private static void SeedCot(FeatureFreshnessTestDbContext db, DateTime reportDate)
        => db.Set<COTReport>().Add(new COTReport
        {
            Currency = "USD",
            ReportDate = reportDate,
            CommercialLong = 10,
            CommercialShort = 5,
            NonCommercialLong = 15,
            NonCommercialShort = 7,
            RetailLong = 3,
            RetailShort = 4,
            NetNonCommercialPositioning = 8,
            TotalOpenInterest = 100,
            NetPositioningChangeWeekly = 1,
        });

    private static void SeedSentiment(FeatureFreshnessTestDbContext db, DateTime capturedAt)
        => db.Set<SentimentSnapshot>().Add(new SentimentSnapshot
        {
            Currency = "USD",
            Source = SentimentSource.COT,
            SentimentScore = 0.2m,
            Confidence = 0.8m,
            CapturedAt = capturedAt,
        });

    private static MLModel SeedModel(
        FeatureFreshnessTestDbContext db,
        string symbol,
        Timeframe timeframe,
        bool isActive = true,
        bool isSuppressed = false,
        bool isMetaLearner = false,
        bool isMamlInitializer = false)
    {
        var model = new MLModel
        {
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = $"{symbol}-{timeframe}",
            FilePath = "memory",
            Status = MLModelStatus.Active,
            IsActive = isActive,
            IsSuppressed = isSuppressed,
            IsMetaLearner = isMetaLearner,
            IsMamlInitializer = isMamlInitializer,
            TrainingSamples = 100,
            TrainedAt = DateTime.UtcNow.AddDays(-1),
            ActivatedAt = DateTime.UtcNow.AddHours(-1),
            RowVersion = 1,
        };

        db.Set<MLModel>().Add(model);
        return model;
    }

    private static void SeedCandle(FeatureFreshnessTestDbContext db, string symbol, Timeframe timeframe, DateTime timestamp)
        => db.Set<Candle>().Add(new Candle
        {
            Symbol = symbol,
            Timeframe = timeframe,
            Timestamp = timestamp,
            Open = 1,
            High = 1.1m,
            Low = 0.9m,
            Close = 1.05m,
            Volume = 100,
            IsClosed = true,
        });

    private static Alert ExistingAlert(string deduplicationKey, DateTime? lastTriggeredAt, string? symbol = null)
        => new()
        {
            AlertType = AlertType.DataQualityIssue,
            Symbol = symbol,
            DeduplicationKey = deduplicationKey,
            ConditionJson = "{}",
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

    private static ServiceProvider BuildProvider(FeatureFreshnessTestDbContext db)
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

    private sealed class FeatureFreshnessFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private FeatureFreshnessFixture(SqliteConnection connection, FeatureFreshnessTestDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public FeatureFreshnessTestDbContext Db { get; }

        public static async Task<FeatureFreshnessFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<FeatureFreshnessTestDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new FeatureFreshnessTestDbContext(options);
            await db.Database.EnsureCreatedAsync();

            return new FeatureFreshnessFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FeatureFreshnessTestDbContext(DbContextOptions<FeatureFreshnessTestDbContext> options)
        : ApplicationDbContext<FeatureFreshnessTestDbContext>(
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
