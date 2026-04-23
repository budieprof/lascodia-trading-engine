using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLHorizonAccuracyWorkerTest
{
    [Fact]
    public async Task ResolverAndAggregator_EndToEnd_WritesReliableHorizonProfile()
    {
        await using var fixture = await HorizonFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddHours(-24);

        db.Set<EngineConfig>().AddRange(
            Config("MLHorizon:MinPredictions", "3"),
            Config("MLHorizon:WilsonZ", "0"));

        for (int i = 0; i < 3; i++)
        {
            await SeedPredictionAsync(
                db,
                model,
                t0.AddHours(i).AddMinutes(10),
                TradeDirection.Buy,
                primaryCorrect: true);
        }

        for (int i = 0; i <= 18; i++)
        {
            db.Set<Candle>().Add(new Candle
            {
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                Timestamp = t0.AddHours(i),
                Open = 1.1000m + i * 0.001m,
                High = 1.1010m + i * 0.001m,
                Low = 1.0990m + i * 0.001m,
                Close = 1.1000m + i * 0.001m,
                IsClosed = true,
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var resolver = new MLMultiHorizonOutcomeWorker(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<MLMultiHorizonOutcomeWorker>.Instance);

        int resolvedFields = await resolver.ResolveHorizonsAsync(db, db, 100, 3.0, CancellationToken.None);
        db.ChangeTracker.Clear();

        await CreateAccuracyWorker().ComputeAllModelsAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(9, resolvedFields);

        var rows = await db.Set<MLModelHorizonAccuracy>()
            .Where(r => r.MLModelId == model.Id)
            .OrderBy(r => r.HorizonBars)
            .ToListAsync();

        Assert.Equal([3, 6, 12], rows.Select(r => r.HorizonBars).ToArray());
        Assert.All(rows, row =>
        {
            Assert.True(row.IsReliable);
            Assert.Equal("Computed", row.Status);
            Assert.Equal(3, row.TotalPredictions);
            Assert.Equal(3, row.CorrectPredictions);
            Assert.Equal(1.0, row.Accuracy);
            Assert.Equal(1.0, row.AccuracyLowerBound);
            Assert.Equal(3, row.PrimaryTotalPredictions);
            Assert.Equal(3, row.PrimaryCorrectPredictions);
            Assert.Equal(1.0, row.PrimaryAccuracy);
            Assert.Equal(0.0, row.PrimaryAccuracyGap);
        });
    }

    [Fact]
    public async Task MultiHorizonOutcomeWorker_ResolvesThreeSixAndTwelveBarOutcomes()
    {
        await using var fixture = await HorizonFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddHours(-20);

        await SeedPredictionAsync(db, model, t0.AddMinutes(10), TradeDirection.Buy);
        for (int i = 0; i <= 13; i++)
        {
            db.Set<Candle>().Add(new Candle
            {
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                Timestamp = t0.AddHours(i),
                Open = 1.1000m + i * 0.001m,
                High = 1.1010m + i * 0.001m,
                Low = 1.0990m + i * 0.001m,
                Close = 1.1000m + i * 0.001m,
                IsClosed = true,
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var worker = new MLMultiHorizonOutcomeWorker(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<MLMultiHorizonOutcomeWorker>.Instance);

        int resolved = await worker.ResolveHorizonsAsync(db, db, 100, 3.0, CancellationToken.None);
        db.ChangeTracker.Clear();

        var log = await db.Set<MLModelPredictionLog>().SingleAsync();
        Assert.Equal(3, resolved);
        Assert.True(log.HorizonCorrect3);
        Assert.True(log.HorizonCorrect6);
        Assert.True(log.HorizonCorrect12);
    }

    [Fact]
    public void MultiHorizonOutcomeWorker_LeavesGapContaminatedOutcomeUnresolved()
    {
        var baseline = CandleAt(DateTime.UtcNow.AddHours(-10), 1.1000m);
        var candles = new[]
        {
            baseline,
            CandleAt(baseline.Timestamp.AddHours(1), 1.1010m),
            CandleAt(baseline.Timestamp.AddHours(8), 1.1020m),
            CandleAt(baseline.Timestamp.AddHours(9), 1.1030m),
        };

        bool? result = MLMultiHorizonOutcomeWorker.ResolveHorizon(
            TradeDirection.Buy,
            baseline,
            candles,
            3,
            TimeSpan.FromHours(1),
            3.0);

        Assert.Null(result);
    }

    [Fact]
    public async Task ComputeAllModelsAsync_UsesChampionHorizonsAndCreatesScopedAlert()
    {
        await using var fixture = await HorizonFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddDays(-1);

        db.Set<EngineConfig>().AddRange(
            Config("MLHorizon:MinPredictions", "2"),
            Config("MLHorizon:HorizonGapThreshold", "0.10"),
            Config("MLHorizon:WilsonZ", "0"),
            Config("MLHorizon:AlertDestination", " horizon-desk "));

        db.Set<Alert>().Add(new Alert
        {
            Symbol = model.Symbol,
            AlertType = AlertType.MLModelDegraded,
            DeduplicationKey = "unrelated",
            ConditionJson = "{}",
            IsActive = true,
        });

        await SeedPredictionAsync(db, model, t0.AddMinutes(1), TradeDirection.Buy, true, false, true, true);
        await SeedPredictionAsync(db, model, t0.AddMinutes(2), TradeDirection.Buy, true, false, true, true);
        await SeedPredictionAsync(db, model, t0.AddMinutes(3), TradeDirection.Buy, true, false, true, true);

        for (int i = 0; i < 10; i++)
        {
            await SeedPredictionAsync(
                db,
                model,
                t0.AddMinutes(20 + i),
                TradeDirection.Buy,
                true,
                true,
                true,
                true,
                ModelRole.Challenger);
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateAccuracyWorker().ComputeAllModelsAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var h3 = await db.Set<MLModelHorizonAccuracy>()
            .SingleAsync(r => r.MLModelId == model.Id && r.HorizonBars == 3);

        Assert.True(h3.IsReliable);
        Assert.Equal("Computed", h3.Status);
        Assert.Equal(3, h3.TotalPredictions);
        Assert.Equal(0, h3.CorrectPredictions);
        Assert.Equal(1.0, h3.PrimaryAccuracy);
        Assert.Equal(1.0, h3.PrimaryAccuracyGap);
        Assert.Equal(0.0, h3.AccuracyLowerBound);

        var alert = await db.Set<Alert>()
            .SingleAsync(a => a.DeduplicationKey == $"MLHorizon:{model.Id}:{model.Symbol}:{model.Timeframe}:3");

        Assert.Equal(AlertSeverity.Medium, alert.Severity);
        Assert.Equal(3600, alert.CooldownSeconds);
        Assert.Contains("\"alertDestination\":\"horizon-desk\"", alert.ConditionJson);
        Assert.Contains("\"reason\":\"horizon_accuracy_gap\"", alert.ConditionJson);
    }

    [Fact]
    public async Task ComputeAllModelsAsync_MarksExistingRowUnreliableWhenCurrentSamplesAreInsufficient()
    {
        await using var fixture = await HorizonFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddDays(-1);

        db.Set<EngineConfig>().Add(Config("MLHorizon:MinPredictions", "3"));
        db.Set<MLModelHorizonAccuracy>().Add(new MLModelHorizonAccuracy
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            HorizonBars = 3,
            TotalPredictions = 50,
            CorrectPredictions = 40,
            Accuracy = 0.8,
            AccuracyLowerBound = 0.7,
            PrimaryTotalPredictions = 50,
            PrimaryCorrectPredictions = 40,
            PrimaryAccuracy = 0.8,
            IsReliable = true,
            Status = "Computed",
            WindowStart = t0.AddDays(-30),
            ComputedAt = t0.AddDays(-1),
            IsDeleted = true,
        });

        await SeedPredictionAsync(db, model, t0, TradeDirection.Buy, true, true, null, null);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateAccuracyWorker().ComputeAllModelsAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var h3 = await db.Set<MLModelHorizonAccuracy>()
            .SingleAsync(r => r.MLModelId == model.Id && r.HorizonBars == 3);
        Assert.False(h3.IsDeleted);
        Assert.False(h3.IsReliable);
        Assert.Equal("InsufficientHorizonSamples", h3.Status);
        Assert.Equal(1, h3.TotalPredictions);
        Assert.Equal(1, h3.CorrectPredictions);
    }

    [Fact]
    public async Task ComputeAllModelsAsync_DoesNotResurrectSoftDeletedHistoryRows()
    {
        await using var fixture = await HorizonFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddDays(-1);

        db.Set<EngineConfig>().Add(Config("MLHorizon:MinPredictions", "1"));
        db.Set<MLModelHorizonAccuracy>().AddRange(
            DeletedHorizonRow(model, 3, t0.AddDays(-10)),
            DeletedHorizonRow(model, 3, t0.AddDays(-5)));

        await SeedPredictionAsync(db, model, t0, TradeDirection.Buy, true, true, null, null);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateAccuracyWorker().ComputeAllModelsAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var allH3Rows = await db.Set<MLModelHorizonAccuracy>()
            .IgnoreQueryFilters()
            .Where(r => r.MLModelId == model.Id && r.HorizonBars == 3)
            .ToListAsync();

        Assert.Equal(3, allH3Rows.Count);
        Assert.Equal(1, allH3Rows.Count(r => !r.IsDeleted));
        Assert.Equal(2, allH3Rows.Count(r => r.IsDeleted));

        var active = Assert.Single(allH3Rows, r => !r.IsDeleted);
        Assert.True(active.IsReliable);
        Assert.Equal("Computed", active.Status);
        Assert.Equal(1.0, active.Accuracy);
    }

    [Fact]
    public async Task ComputeAllModelsAsync_ResolvesActiveGapAlertWhenBreachClears()
    {
        await using var fixture = await HorizonFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddDays(-1);
        var dedupKey = $"MLHorizon:{model.Id}:{model.Symbol}:{model.Timeframe}:3";

        db.Set<EngineConfig>().AddRange(
            Config("MLHorizon:MinPredictions", "2"),
            Config("MLHorizon:HorizonGapThreshold", "0.10"));
        db.Set<Alert>().Add(new Alert
        {
            Symbol = model.Symbol,
            AlertType = AlertType.MLModelDegraded,
            DeduplicationKey = dedupKey,
            ConditionJson = "{}",
            Severity = AlertSeverity.Medium,
            CooldownSeconds = 3600,
            IsActive = true,
        });

        await SeedPredictionAsync(db, model, t0.AddMinutes(1), TradeDirection.Buy, true, true, null, null);
        await SeedPredictionAsync(db, model, t0.AddMinutes(2), TradeDirection.Buy, true, true, null, null);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateAccuracyWorker().ComputeAllModelsAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var alert = await db.Set<Alert>()
            .SingleAsync(a => a.DeduplicationKey == dedupKey);

        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
    }

    [Fact]
    public async Task ComputeAllModelsAsync_RefreshesExistingGapAlertWhenBreachPersists()
    {
        await using var fixture = await HorizonFixture.CreateAsync();
        var db = fixture.Db;
        var model = await SeedActiveModelAsync(db);
        var t0 = DateTime.UtcNow.AddDays(-1);
        var dedupKey = $"MLHorizon:{model.Id}:{model.Symbol}:{model.Timeframe}:3";

        db.Set<EngineConfig>().AddRange(
            Config("MLHorizon:MinPredictions", "2"),
            Config("MLHorizon:HorizonGapThreshold", "0.10"),
            Config("MLHorizon:AlertDestination", "fresh-desk"));
        db.Set<Alert>().Add(new Alert
        {
            Symbol = model.Symbol,
            AlertType = AlertType.MLModelDegraded,
            DeduplicationKey = dedupKey,
            ConditionJson = """{"reason":"old_payload"}""",
            Severity = AlertSeverity.Info,
            CooldownSeconds = 30,
            AutoResolvedAt = t0.AddHours(-1),
            IsActive = true,
        });

        await SeedPredictionAsync(db, model, t0.AddMinutes(1), TradeDirection.Buy, true, false, null, null);
        await SeedPredictionAsync(db, model, t0.AddMinutes(2), TradeDirection.Buy, true, false, null, null);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateAccuracyWorker().ComputeAllModelsAsync(db, db, CancellationToken.None);
        db.ChangeTracker.Clear();

        var alert = await db.Set<Alert>()
            .SingleAsync(a => a.DeduplicationKey == dedupKey);

        Assert.True(alert.IsActive);
        Assert.Null(alert.AutoResolvedAt);
        Assert.Equal(AlertSeverity.Medium, alert.Severity);
        Assert.Equal(3600, alert.CooldownSeconds);
        Assert.Contains("\"reason\":\"horizon_accuracy_gap\"", alert.ConditionJson);
        Assert.Contains("\"alertDestination\":\"fresh-desk\"", alert.ConditionJson);
        Assert.Contains("\"sampleCount\":2", alert.ConditionJson);
    }

    [Fact]
    public void HorizonAccuracyHelpers_RejectUnsafeValuesAndComputeWilsonBound()
    {
        Assert.Equal(3600, MLHorizonAccuracyWorker.NormalizePollSeconds(0));
        Assert.Equal(30, MLHorizonAccuracyWorker.NormalizeWindowDays(-1));
        Assert.Equal(20, MLHorizonAccuracyWorker.NormalizeMinPredictions(0));
        Assert.Equal(20, MLHorizonAccuracyWorker.NormalizeMinPredictions(1_000_001));
        Assert.Equal(0.10, MLHorizonAccuracyWorker.NormalizeProbability(double.NaN, 0.10));
        Assert.Equal(1.96, MLHorizonAccuracyWorker.NormalizeWilsonZ(double.PositiveInfinity));
        Assert.Equal("ml-ops", MLHorizonAccuracyWorker.NormalizeDestination(" "));
        Assert.Equal(100, MLHorizonAccuracyWorker.NormalizeDestination(new string('x', 101)).Length);

        double lower = MLHorizonAccuracyWorker.WilsonLowerBound(8, 10, 1.96);
        Assert.InRange(lower, 0.49, 0.50);
        Assert.Equal(1.0, MLHorizonAccuracyWorker.WilsonLowerBound(20, 10, 0));
    }

    [Fact]
    public void HorizonAccuracyHelpers_ClassifyOnlyExpectedUniqueConstraintViolations()
    {
        var sqliteHorizonUnique = new DbUpdateException(
            "save failed",
            new InvalidOperationException(
                "SQLite Error 19: 'UNIQUE constraint failed: MLModelHorizonAccuracy.MLModelId, MLModelHorizonAccuracy.HorizonBars'."));

        Assert.True(MLHorizonAccuracyWorker.IsExpectedUniqueConstraintViolation(
            sqliteHorizonUnique,
            "IX_MLModelHorizonAccuracy_MLModelId_HorizonBars",
            requiredMessageTokens:
            [
                "MLModelHorizonAccuracy",
                "MLModelId",
                "HorizonBars"
            ]));

        Assert.False(MLHorizonAccuracyWorker.IsExpectedUniqueConstraintViolation(
            sqliteHorizonUnique,
            "IX_Alert_DeduplicationKey",
            requiredMessageTokens:
            [
                "Alert",
                "DeduplicationKey"
            ]));

        var postgresAlertUnique = new DbUpdateException(
            "duplicate key value violates unique constraint \"IX_Alert_DeduplicationKey\"",
            new InvalidOperationException("23505"));

        Assert.True(MLHorizonAccuracyWorker.IsExpectedUniqueConstraintViolation(
            postgresAlertUnique,
            "IX_Alert_DeduplicationKey",
            new TestDatabaseExceptionClassifier(isUnique: true),
            "Alert",
            "DeduplicationKey"));

        var nonUnique = new DbUpdateException(
            "write failed",
            new TimeoutException("timed out"));

        Assert.False(MLHorizonAccuracyWorker.IsExpectedUniqueConstraintViolation(
            nonUnique,
            "IX_Alert_DeduplicationKey",
            new TestDatabaseExceptionClassifier(isUnique: false),
            "Alert",
            "DeduplicationKey"));
    }

    private static MLHorizonAccuracyWorker CreateAccuracyWorker(IDatabaseExceptionClassifier? classifier = null)
        => new(
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<MLHorizonAccuracyWorker>.Instance,
            classifier);

    private static async Task<MLModel> SeedActiveModelAsync(HorizonTestDbContext db)
    {
        var strategy = new Strategy
        {
            Name = "Horizon Test",
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            Status = StrategyStatus.Active,
            RowVersion = 1,
        };

        var model = new MLModel
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ModelVersion = "horizon-test",
            FilePath = "memory",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainingSamples = 100,
            ActivatedAt = DateTime.UtcNow.AddDays(-2),
            RowVersion = 1,
        };

        db.Set<Strategy>().Add(strategy);
        db.Set<MLModel>().Add(model);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return model;
    }

    private static async Task<MLModelPredictionLog> SeedPredictionAsync(
        HorizonTestDbContext db,
        MLModel model,
        DateTime predictedAt,
        TradeDirection direction,
        bool? primaryCorrect = null,
        bool? h3 = null,
        bool? h6 = null,
        bool? h12 = null,
        ModelRole role = ModelRole.Champion)
    {
        var strategy = await db.Set<Strategy>().SingleAsync();
        var signal = new TradeSignal
        {
            StrategyId = strategy.Id,
            Symbol = model.Symbol,
            Direction = direction,
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
            PredictedDirection = direction,
            PredictedAt = predictedAt,
            DirectionCorrect = primaryCorrect,
            HorizonCorrect3 = h3,
            HorizonCorrect6 = h6,
            HorizonCorrect12 = h12,
            ConfidenceScore = 0.7m,
        };

        db.Set<MLModelPredictionLog>().Add(log);
        return log;
    }

    private static Candle CandleAt(DateTime timestamp, decimal close)
        => new()
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            Timestamp = timestamp,
            Open = close,
            High = close,
            Low = close,
            Close = close,
            IsClosed = true,
        };

    private static MLModelHorizonAccuracy DeletedHorizonRow(
        MLModel model,
        int horizonBars,
        DateTime computedAt)
        => new()
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            HorizonBars = horizonBars,
            TotalPredictions = 10,
            CorrectPredictions = 8,
            Accuracy = 0.8,
            AccuracyLowerBound = 0.7,
            PrimaryTotalPredictions = 10,
            PrimaryCorrectPredictions = 8,
            PrimaryAccuracy = 0.8,
            PrimaryAccuracyGap = 0.0,
            IsReliable = true,
            Status = "Computed",
            WindowStart = computedAt.AddDays(-30),
            ComputedAt = computedAt,
            IsDeleted = true,
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

    private sealed class HorizonFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private HorizonFixture(SqliteConnection connection, HorizonTestDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public HorizonTestDbContext Db { get; }

        public static async Task<HorizonFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<HorizonTestDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new HorizonTestDbContext(options);
            await db.Database.EnsureCreatedAsync();

            return new HorizonFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class HorizonTestDbContext(DbContextOptions<HorizonTestDbContext> options)
        : ApplicationDbContext<HorizonTestDbContext>(
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

    private sealed class TestDatabaseExceptionClassifier(bool isUnique) : IDatabaseExceptionClassifier
    {
        public bool IsUniqueConstraintViolation(DbUpdateException ex) => isUnique;
    }
}
