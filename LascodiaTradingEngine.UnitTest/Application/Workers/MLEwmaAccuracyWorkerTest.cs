using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLEwmaAccuracyWorkerTest
{
    [Fact]
    public async Task UpdateEwmaAsync_ProcessesLateResolvedOutcome_WhenPredictionTimeIsOlderThanWatermark()
    {
        await using var fixture = await EwmaFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddHours(-2);

        await SeedPredictionAsync(db, model, t0.AddMinutes(5), t0.AddMinutes(10), true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var worker = CreateWorker();
        await worker.UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var row = await db.Set<MLModelEwmaAccuracy>().SingleAsync();
        Assert.Equal(1, row.TotalPredictions);
        Assert.Equal(0.525, row.EwmaAccuracy, 3);

        await SeedPredictionAsync(db, model, t0, t0.AddMinutes(20), false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await worker.UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        row = await db.Set<MLModelEwmaAccuracy>().SingleAsync();
        Assert.Equal(2, row.TotalPredictions);
        Assert.Equal(0.49875, row.EwmaAccuracy, 5);
        Assert.Equal(t0, row.LastPredictionAt);
        Assert.Equal(t0.AddMinutes(20), row.LastOutcomeRecordedAt);
    }

    [Fact]
    public async Task UpdateEwmaAsync_ProcessesSameResolvedAtRows_UsingPredictionLogIdTieBreaker()
    {
        await using var fixture = await EwmaFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var resolvedAt = DateTime.UtcNow.AddMinutes(-30);

        var first = await SeedPredictionAsync(
            db,
            model,
            resolvedAt.AddMinutes(-5),
            resolvedAt,
            true);
        await db.SaveChangesAsync();

        db.Set<MLModelEwmaAccuracy>().Add(new MLModelEwmaAccuracy
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            EwmaAccuracy = 0.525,
            Alpha = 0.05,
            TotalPredictions = 1,
            LastPredictionAt = first.PredictedAt,
            LastOutcomeRecordedAt = resolvedAt,
            LastPredictionLogId = first.Id,
            ComputedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await SeedPredictionAsync(
            db,
            model,
            resolvedAt.AddMinutes(-4),
            resolvedAt,
            false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateWorker().UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var row = await db.Set<MLModelEwmaAccuracy>().SingleAsync();
        Assert.Equal(2, row.TotalPredictions);
        Assert.Equal(0.49875, row.EwmaAccuracy, 5);
        Assert.True(row.LastPredictionLogId > first.Id);
    }

    [Fact]
    public async Task UpdateEwmaAsync_NormalizesInvalidConfig_AndKeepsEwmaInRange()
    {
        await using var fixture = await EwmaFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);

        db.Set<EngineConfig>().AddRange(
            Config("MLEwma:Alpha", "2"),
            Config("MLEwma:MinPredictions", "0"),
            Config("MLEwma:WarnThreshold", "9"),
            Config("MLEwma:CriticalThreshold", "-4"),
            Config("MLEwma:AlertDestination", "  ops-ewma  "));

        await SeedPredictionAsync(
            db,
            model,
            DateTime.UtcNow.AddMinutes(-10),
            DateTime.UtcNow.AddMinutes(-1),
            true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateWorker().UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var row = await db.Set<MLModelEwmaAccuracy>().SingleAsync();
        Assert.Equal(0.05, row.Alpha);
        Assert.InRange(row.EwmaAccuracy, 0.0, 1.0);
        Assert.Equal(0.525, row.EwmaAccuracy, 3);
    }

    [Fact]
    public async Task UpdateEwmaAsync_CreatesScopedCriticalAlert_WithTypedSeverityAndDedupKey()
    {
        await using var fixture = await EwmaFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddHours(-1);

        db.Set<EngineConfig>().AddRange(
            Config("MLEwma:Alpha", "1"),
            Config("MLEwma:MinPredictions", "1"),
            Config("MLEwma:WarnThreshold", "0.50"),
            Config("MLEwma:CriticalThreshold", "0.48"),
            Config("MLEwma:AlertDestination", "  ewma-desk  "));

        db.Set<Alert>().Add(new Alert
        {
            Symbol = model.Symbol,
            AlertType = AlertType.MLModelDegraded,
            ConditionJson = "{}",
            DeduplicationKey = "unrelated-alert",
            IsActive = true,
        });

        await SeedPredictionAsync(db, model, t0, t0.AddMinutes(1), false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateWorker().UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var ewmaAlert = await db.Set<Alert>()
            .SingleAsync(a => a.DeduplicationKey == $"MLEwma:{model.Id}:{model.Symbol}:{model.Timeframe}");

        Assert.Equal(AlertSeverity.Critical, ewmaAlert.Severity);
        Assert.Equal(600, ewmaAlert.CooldownSeconds);
        Assert.Contains("\"alertDestination\":\"ewma-desk\"", ewmaAlert.ConditionJson);
        Assert.Contains("\"severity\":\"critical\"", ewmaAlert.ConditionJson);
    }

    [Fact]
    public async Task UpdateEwmaAsync_SkipsAllWork_WhenDisabledByConfig()
    {
        await using var fixture = await EwmaFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);

        db.Set<EngineConfig>().Add(Config("MLEwma:Enabled", "false"));
        await SeedPredictionAsync(
            db,
            model,
            DateTime.UtcNow.AddMinutes(-10),
            DateTime.UtcNow.AddMinutes(-1),
            false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateWorker().UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Empty(await db.Set<MLModelEwmaAccuracy>().ToListAsync());
        Assert.Empty(await db.Set<Alert>().ToListAsync());
    }

    [Fact]
    public async Task UpdateEwmaAsync_EscalatesActiveWarningAlert_ToCritical()
    {
        await using var fixture = await EwmaFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddHours(-1);

        db.Set<EngineConfig>().AddRange(
            Config("MLEwma:Alpha", "0.5"),
            Config("MLEwma:MinPredictions", "1"),
            Config("MLEwma:WarnThreshold", "0.40"),
            Config("MLEwma:CriticalThreshold", "0.20"));

        await SeedPredictionAsync(db, model, t0, t0.AddMinutes(1), false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var worker = CreateWorker();
        await worker.UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.Equal(AlertSeverity.Medium, alert.Severity);
        Assert.Contains("\"severity\":\"warning\"", alert.ConditionJson);

        await SeedPredictionAsync(db, model, t0.AddMinutes(2), t0.AddMinutes(3), false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await worker.UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Null(alert.AutoResolvedAt);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("\"severity\":\"critical\"", alert.ConditionJson);
    }

    [Fact]
    public async Task UpdateEwmaAsync_ResolvesActiveAlert_WhenEwmaRecoversAboveWarning()
    {
        await using var fixture = await EwmaFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddHours(-1);

        db.Set<EngineConfig>().AddRange(
            Config("MLEwma:Alpha", "1"),
            Config("MLEwma:MinPredictions", "1"),
            Config("MLEwma:WarnThreshold", "0.50"),
            Config("MLEwma:CriticalThreshold", "0.48"));

        await SeedPredictionAsync(db, model, t0, t0.AddMinutes(1), false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var worker = CreateWorker();
        await worker.UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);

        await SeedPredictionAsync(db, model, t0.AddMinutes(2), t0.AddMinutes(3), true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await worker.UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
    }

    [Fact]
    public async Task UpdateEwmaAsync_ResolvesWorkerOwnedAlert_WhenModelLeavesActiveSet()
    {
        await using var fixture = await EwmaFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);

        db.Attach(model);
        model.IsSuppressed = true;
        db.Set<Alert>().Add(new Alert
        {
            Symbol = model.Symbol,
            AlertType = AlertType.MLModelDegraded,
            DeduplicationKey = $"MLEwma:{model.Id}:{model.Symbol}:{model.Timeframe}",
            ConditionJson = "{}",
            IsActive = true,
            Severity = AlertSeverity.Critical,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateWorker().UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
    }

    [Fact]
    public async Task UpdateEwmaAsync_IgnoresChallengerPredictionLogs()
    {
        await using var fixture = await EwmaFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddHours(-1);

        await SeedPredictionAsync(db, model, t0, t0.AddMinutes(1), false, ModelRole.Challenger);
        await SeedPredictionAsync(db, model, t0.AddMinutes(2), t0.AddMinutes(3), true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateWorker().UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var row = await db.Set<MLModelEwmaAccuracy>().SingleAsync();
        Assert.Equal(1, row.TotalPredictions);
        Assert.Equal(0.525, row.EwmaAccuracy, 3);
    }

    [Fact]
    public async Task UpdateEwmaAsync_ProcessesResolvedOutcomesAcrossConfiguredBatches()
    {
        await using var fixture = await EwmaFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddHours(-1);

        db.Set<EngineConfig>().Add(Config("MLEwma:PredictionLogBatchSize", "1"));

        var first = await SeedPredictionAsync(db, model, t0, t0.AddMinutes(1), true);
        var second = await SeedPredictionAsync(db, model, t0.AddMinutes(2), t0.AddMinutes(3), false);
        var third = await SeedPredictionAsync(db, model, t0.AddMinutes(4), t0.AddMinutes(5), true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateWorker().UpdateEwmaAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var row = await db.Set<MLModelEwmaAccuracy>().SingleAsync();
        Assert.Equal(3, row.TotalPredictions);
        Assert.Equal(third.Id, row.LastPredictionLogId);
        Assert.True(row.LastPredictionLogId > first.Id);
        Assert.True(row.LastPredictionLogId > second.Id);
    }

    [Fact]
    public void NormalizeHelpers_RejectUnsafeValues()
    {
        Assert.Equal(0.05, MLEwmaAccuracyWorker.NormalizeAlpha(double.NaN));
        Assert.Equal(0.05, MLEwmaAccuracyWorker.NormalizeAlpha(0));
        Assert.Equal(0.05, MLEwmaAccuracyWorker.NormalizeAlpha(1.1));
        Assert.Equal(0.75, MLEwmaAccuracyWorker.NormalizeAlpha(0.75));

        Assert.Equal(0.5, MLEwmaAccuracyWorker.NormalizeProbability(double.PositiveInfinity, 0.5));
        Assert.Equal(0.5, MLEwmaAccuracyWorker.NormalizeProbability(-0.1, 0.5));
        Assert.Equal(0.8, MLEwmaAccuracyWorker.NormalizeProbability(0.8, 0.5));

        Assert.Equal(20, MLEwmaAccuracyWorker.NormalizeMinPredictions(0));
        Assert.Equal(3, MLEwmaAccuracyWorker.NormalizeMinPredictions(3));

        Assert.Equal(600, MLEwmaAccuracyWorker.NormalizePollSeconds(0));
        Assert.Equal(600, MLEwmaAccuracyWorker.NormalizePollSeconds(90_000));
        Assert.Equal(30, MLEwmaAccuracyWorker.NormalizePollSeconds(30));

        Assert.Equal("ml-ops", MLEwmaAccuracyWorker.NormalizeDestination("   "));
        Assert.Equal("desk", MLEwmaAccuracyWorker.NormalizeDestination(" desk "));
    }

    private static MLEwmaAccuracyWorker CreateWorker()
        => new(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<MLEwmaAccuracyWorker>.Instance);

    private static async Task<MLModel> SeedActiveModelAsync(EwmaTestDbContext db)
    {
        var strategy = new Strategy
        {
            Name = "EWMA Test",
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            Status = StrategyStatus.Active,
            RowVersion = 1,
        };

        var model = new MLModel
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ModelVersion = "test",
            FilePath = "memory",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainingSamples = 100,
            ActivatedAt = DateTime.UtcNow.AddHours(-1),
            RowVersion = 1,
        };

        db.Set<Strategy>().Add(strategy);
        db.Set<MLModel>().Add(model);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return model;
    }

    private static async Task<MLModelPredictionLog> SeedPredictionAsync(
        EwmaTestDbContext db,
        MLModel model,
        DateTime predictedAt,
        DateTime resolvedAt,
        bool correct,
        ModelRole role = ModelRole.Champion)
    {
        var strategy = await db.Set<Strategy>().SingleAsync();
        var signal = new TradeSignal
        {
            StrategyId = strategy.Id,
            Symbol = model.Symbol,
            Direction = TradeDirection.Buy,
            EntryPrice = 1.1000m,
            SuggestedLotSize = 0.01m,
            Confidence = 0.7m,
            GeneratedAt = predictedAt,
            ExpiresAt = predictedAt.AddHours(1),
            MLModelId = model.Id,
        };

        db.Set<TradeSignal>().Add(signal);
        await db.SaveChangesAsync();

        var log = new MLModelPredictionLog
        {
            TradeSignalId = signal.Id,
            MLModelId = model.Id,
            ModelRole = role,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            PredictedDirection = TradeDirection.Buy,
            ActualDirection = correct ? TradeDirection.Buy : TradeDirection.Sell,
            PredictedAt = predictedAt,
            OutcomeRecordedAt = resolvedAt,
            DirectionCorrect = correct,
            ConfidenceScore = 0.7m,
        };

        db.Set<MLModelPredictionLog>().Add(log);
        return log;
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

    private sealed class EwmaFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private EwmaFixture(SqliteConnection connection, EwmaTestDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public EwmaTestDbContext Db { get; }

        public static async Task<EwmaFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<EwmaTestDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new EwmaTestDbContext(options);
            await db.Database.EnsureCreatedAsync();

            return new EwmaFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class EwmaTestDbContext(DbContextOptions<EwmaTestDbContext> options)
        : ApplicationDbContext<EwmaTestDbContext>(
            options,
            new HttpContextAccessor(),
            typeof(WriteApplicationDbContext).Assembly)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<MLModel>()
                .Property(x => x.RowVersion)
                .IsConcurrencyToken(false)
                .ValueGeneratedNever();

            modelBuilder.Entity<Strategy>()
                .Property(x => x.RowVersion)
                .IsConcurrencyToken(false)
                .ValueGeneratedNever();
        }
    }
}
