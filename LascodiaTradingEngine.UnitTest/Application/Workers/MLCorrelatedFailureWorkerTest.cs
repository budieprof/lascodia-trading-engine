using System.Diagnostics.Metrics;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLCorrelatedFailureWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_Activates_Systemic_Pause_When_Failure_Ratio_Exceeds_Alarm()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var first = CreateModel(1, "EURUSD");
        var second = CreateModel(2, "GBPUSD");
        var third = CreateModel(3, "USDJPY");
        db.Set<MLModel>().AddRange(first, second, third);
        AddPredictionLogs(db, first, now, correct: false, count: 30, startTradeSignalId: 1);
        AddPredictionLogs(db, second, now, correct: false, count: 30, startTradeSignalId: 100);
        AddPredictionLogs(db, third, now, correct: true, count: 30, startTradeSignalId: 200);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("true", await GetConfigValueAsync(db, "MLTraining:SystemicPauseActive"));
        var log = await db.Set<MLCorrelatedFailureLog>().SingleAsync();
        Assert.True(log.PauseActivated);
        Assert.Equal(2, log.FailingModelCount);
        Assert.Equal(3, log.TotalModelCount);
        Assert.Equal(3, log.ActiveModelCount);
        Assert.Equal(3, log.EvaluatedModelCount);
        Assert.Equal(2.0 / 3.0, log.FailureRatio, precision: 6);
        Assert.Contains("FailingModels", log.FailureDetailsJson);
        Assert.Single(await db.Set<Alert>().Where(a => a.AlertType == AlertType.SystemicMLDegradation).ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Counts_Failing_Models_Not_Distinct_Symbols()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var h1 = CreateModel(1, "EURUSD", Timeframe.H1);
        var m15 = CreateModel(2, "EURUSD", Timeframe.M15);
        var m5 = CreateModel(3, "EURUSD", Timeframe.M5);
        db.Set<MLModel>().AddRange(h1, m15, m5);
        AddPredictionLogs(db, h1, now, correct: false, count: 30, startTradeSignalId: 1);
        AddPredictionLogs(db, m15, now, correct: false, count: 30, startTradeSignalId: 100);
        AddPredictionLogs(db, m5, now, correct: true, count: 30, startTradeSignalId: 200);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        var log = await db.Set<MLCorrelatedFailureLog>().SingleAsync();
        Assert.Equal(2, log.FailingModelCount);
        Assert.Equal(3, log.EvaluatedModelCount);
        Assert.Equal(2.0 / 3.0, log.FailureRatio, precision: 6);

        using var symbols = JsonDocument.Parse(log.SymbolsAffectedJson);
        Assert.Single(symbols.RootElement.EnumerateArray());
        Assert.Equal("EURUSD", symbols.RootElement[0].GetString());

        using var details = JsonDocument.Parse(log.FailureDetailsJson!);
        Assert.Equal(2, details.RootElement.GetProperty("FailingModels").GetArrayLength());
    }

    [Fact]
    public async Task RunCycleAsync_Does_Not_Activate_Pause_Before_Minimum_Evaluated_Models()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD");
        db.Set<MLModel>().Add(model);
        AddPredictionLogs(db, model, now, correct: false, count: 30, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(await GetConfigValueAsync(db, "MLTraining:SystemicPauseActive"));
        Assert.Empty(await db.Set<MLCorrelatedFailureLog>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Lifts_Systemic_Pause_When_Failure_Ratio_Recovers()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLTraining:SystemicPauseActive",
            Value = "true",
            DataType = ConfigDataType.Bool,
            IsHotReloadable = true,
            LastUpdatedAt = now.AddMinutes(-10)
        });
        var first = CreateModel(1, "EURUSD");
        var second = CreateModel(2, "GBPUSD");
        var third = CreateModel(3, "USDJPY");
        db.Set<MLModel>().AddRange(first, second, third);
        AddPredictionLogs(db, first, now, correct: true, count: 30, startTradeSignalId: 1);
        AddPredictionLogs(db, second, now, correct: true, count: 30, startTradeSignalId: 100);
        AddPredictionLogs(db, third, now, correct: true, count: 30, startTradeSignalId: 200);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("false", await GetConfigValueAsync(db, "MLTraining:SystemicPauseActive"));
        var log = await db.Set<MLCorrelatedFailureLog>().SingleAsync();
        Assert.False(log.PauseActivated);
        Assert.Equal(0, log.FailingModelCount);
        Assert.Equal(3, log.TotalModelCount);
    }

    [Fact]
    public async Task RunCycleAsync_Does_Not_Duplicate_Log_Or_Alert_When_Already_Paused()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        AddConfig(db, "MLTraining:SystemicPauseActive", "true", ConfigDataType.Bool);
        var first = CreateModel(1, "EURUSD");
        var second = CreateModel(2, "GBPUSD");
        var third = CreateModel(3, "USDJPY");
        db.Set<MLModel>().AddRange(first, second, third);
        AddPredictionLogs(db, first, now, correct: false, count: 30, startTradeSignalId: 1);
        AddPredictionLogs(db, second, now, correct: false, count: 30, startTradeSignalId: 100);
        AddPredictionLogs(db, third, now, correct: false, count: 30, startTradeSignalId: 200);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("true", await GetConfigValueAsync(db, "MLTraining:SystemicPauseActive"));
        Assert.Empty(await db.Set<MLCorrelatedFailureLog>().ToListAsync());
        Assert.Empty(await db.Set<Alert>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Skips_When_Distributed_Lock_Is_Busy()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var first = CreateModel(1, "EURUSD");
        var second = CreateModel(2, "GBPUSD");
        var third = CreateModel(3, "USDJPY");
        db.Set<MLModel>().AddRange(first, second, third);
        AddPredictionLogs(db, first, now, correct: false, count: 30, startTradeSignalId: 1);
        AddPredictionLogs(db, second, now, correct: false, count: 30, startTradeSignalId: 100);
        AddPredictionLogs(db, third, now, correct: false, count: 30, startTradeSignalId: 200);
        await db.SaveChangesAsync();

        var distributedLock = new Mock<IDistributedLock>();
        distributedLock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var worker = CreateWorker(db, distributedLock.Object);
        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(await GetConfigValueAsync(db, "MLTraining:SystemicPauseActive"));
        Assert.Empty(await db.Set<MLCorrelatedFailureLog>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Uses_Outcome_Time_For_Window()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        AddConfig(db, "MLCorrelated:MinModelsForAlarm", "2", ConfigDataType.Int);
        var first = CreateModel(1, "EURUSD");
        var second = CreateModel(2, "GBPUSD");
        var third = CreateModel(3, "USDJPY");
        db.Set<MLModel>().AddRange(first, second, third);
        AddPredictionLogs(db, first, now, correct: false, count: 30, startTradeSignalId: 1, predictedAt: now.AddDays(-30), outcomeAt: now.AddMinutes(-5));
        AddPredictionLogs(db, second, now, correct: false, count: 30, startTradeSignalId: 100, predictedAt: now.AddDays(-30), outcomeAt: now.AddMinutes(-10));
        AddPredictionLogs(db, third, now, correct: false, count: 30, startTradeSignalId: 200, predictedAt: now.AddMinutes(-10), outcomeAt: now.AddDays(-30));
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        var log = await db.Set<MLCorrelatedFailureLog>().SingleAsync();
        Assert.Equal(2, log.EvaluatedModelCount);
        Assert.Equal(2, log.FailingModelCount);
    }

    [Fact]
    public async Task RunCycleAsync_Excludes_Non_Live_Model_Types()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var liveOne = CreateModel(1, "EURUSD");
        var liveTwo = CreateModel(2, "GBPUSD");
        var liveThree = CreateModel(3, "USDJPY");
        var meta = CreateModel(4, "AUDUSD");
        meta.IsMetaLearner = true;
        var maml = CreateModel(5, "NZDUSD");
        maml.IsMamlInitializer = true;
        var suppressed = CreateModel(6, "USDCAD");
        suppressed.IsSuppressed = true;
        db.Set<MLModel>().AddRange(liveOne, liveTwo, liveThree, meta, maml, suppressed);
        AddPredictionLogs(db, liveOne, now, correct: false, count: 30, startTradeSignalId: 1);
        AddPredictionLogs(db, liveTwo, now, correct: false, count: 30, startTradeSignalId: 100);
        AddPredictionLogs(db, liveThree, now, correct: true, count: 30, startTradeSignalId: 200);
        AddPredictionLogs(db, meta, now, correct: false, count: 30, startTradeSignalId: 300);
        AddPredictionLogs(db, maml, now, correct: false, count: 30, startTradeSignalId: 400);
        AddPredictionLogs(db, suppressed, now, correct: false, count: 30, startTradeSignalId: 500);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        var log = await db.Set<MLCorrelatedFailureLog>().SingleAsync();
        Assert.Equal(3, log.ActiveModelCount);
        Assert.Equal(3, log.EvaluatedModelCount);
        Assert.Equal(2, log.FailingModelCount);
    }

    [Fact]
    public async Task RunCycleAsync_Clamps_Poll_Interval_And_Parses_Config_Invariantly()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLCorrelated:PollIntervalSeconds", "1", ConfigDataType.Int);
        AddConfig(db, "MLCorrelated:AlarmRatio", "0,40", ConfigDataType.Decimal);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        var pollSecs = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(30, pollSecs);
    }

    [Fact]
    public async Task RunCycleAsync_Applies_Hysteresis_When_Recovery_Ratio_Reaches_Alarm()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        AddConfig(db, "MLTraining:SystemicPauseActive", "true", ConfigDataType.Bool);
        AddConfig(db, "MLCorrelated:AlarmRatio", "0.40", ConfigDataType.Decimal);
        AddConfig(db, "MLCorrelated:RecoveryRatio", "0.50", ConfigDataType.Decimal);
        var first = CreateModel(1, "EURUSD");
        var second = CreateModel(2, "GBPUSD");
        var third = CreateModel(3, "USDJPY");
        db.Set<MLModel>().AddRange(first, second, third);
        AddPredictionLogs(db, first, now, correct: false, count: 30, startTradeSignalId: 1);
        AddPredictionLogs(db, second, now, correct: true, count: 30, startTradeSignalId: 100);
        AddPredictionLogs(db, third, now, correct: true, count: 30, startTradeSignalId: 200);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("true", await GetConfigValueAsync(db, "MLTraining:SystemicPauseActive"));
        Assert.Empty(await db.Set<MLCorrelatedFailureLog>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Revives_Soft_Deleted_Systemic_Pause_Config()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLTraining:SystemicPauseActive",
            Value = "false",
            DataType = ConfigDataType.Bool,
            IsHotReloadable = true,
            LastUpdatedAt = now.AddMinutes(-10),
            IsDeleted = true
        });
        var first = CreateModel(1, "EURUSD");
        var second = CreateModel(2, "GBPUSD");
        var third = CreateModel(3, "USDJPY");
        db.Set<MLModel>().AddRange(first, second, third);
        AddPredictionLogs(db, first, now, correct: false, count: 30, startTradeSignalId: 1);
        AddPredictionLogs(db, second, now, correct: false, count: 30, startTradeSignalId: 100);
        AddPredictionLogs(db, third, now, correct: false, count: 30, startTradeSignalId: 200);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        var config = await db.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Key == "MLTraining:SystemicPauseActive");
        Assert.False(config.IsDeleted);
        Assert.Equal("true", config.Value);
    }

    [Fact]
    public async Task RunCycleAsync_Skips_State_Change_During_Cooldown()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        AddConfig(db, "MLCorrelated:StateChangeCooldownMinutes", "60", ConfigDataType.Int);
        db.Set<MLCorrelatedFailureLog>().Add(new MLCorrelatedFailureLog
        {
            DetectedAt = now.AddMinutes(-5),
            ActiveModelCount = 3,
            EvaluatedModelCount = 3,
            TotalModelCount = 3,
            FailingModelCount = 3,
            FailureRatio = 1.0,
            SymbolsAffectedJson = "[]",
            PauseActivated = false
        });
        var first = CreateModel(1, "EURUSD");
        var second = CreateModel(2, "GBPUSD");
        var third = CreateModel(3, "USDJPY");
        db.Set<MLModel>().AddRange(first, second, third);
        AddPredictionLogs(db, first, now, correct: false, count: 30, startTradeSignalId: 1);
        AddPredictionLogs(db, second, now, correct: false, count: 30, startTradeSignalId: 100);
        AddPredictionLogs(db, third, now, correct: false, count: 30, startTradeSignalId: 200);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(await GetConfigValueAsync(db, "MLTraining:SystemicPauseActive"));
        Assert.Single(await db.Set<MLCorrelatedFailureLog>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Works_With_Relational_Sqlite_Provider()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            await command.ExecuteNonQueryAsync();
        }
        await using var db = CreateSqliteDbContext(connection);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var first = CreateModel(1, "EURUSD");
        var second = CreateModel(2, "GBPUSD");
        var third = CreateModel(3, "USDJPY");
        db.Set<MLModel>().AddRange(first, second, third);
        AddPredictionLogs(db, first, now, correct: false, count: 30, startTradeSignalId: 1);
        AddPredictionLogs(db, second, now, correct: false, count: 30, startTradeSignalId: 100);
        AddPredictionLogs(db, third, now, correct: true, count: 30, startTradeSignalId: 200);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("true", await GetConfigValueAsync(db, "MLTraining:SystemicPauseActive"));
        Assert.Single(await db.Set<MLCorrelatedFailureLog>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Uses_Configured_Profitability_Failure_Metric()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        AddConfig(db, "MLCorrelated:FailureMetric", "Profitability", ConfigDataType.String);
        var first = CreateModel(1, "EURUSD");
        var second = CreateModel(2, "GBPUSD");
        var third = CreateModel(3, "USDJPY");
        db.Set<MLModel>().AddRange(first, second, third);
        AddPredictionLogs(db, first, now, correct: true, count: 30, startTradeSignalId: 1, profitable: false);
        AddPredictionLogs(db, second, now, correct: true, count: 30, startTradeSignalId: 100, profitable: false);
        AddPredictionLogs(db, third, now, correct: true, count: 30, startTradeSignalId: 200, profitable: true);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        await worker.RunCycleAsync(CancellationToken.None);

        var log = await db.Set<MLCorrelatedFailureLog>().SingleAsync();
        Assert.Equal(2, log.FailingModelCount);
        Assert.Equal(2.0 / 3.0, log.FailureRatio, precision: 6);
    }

    [Fact]
    public async Task RunCycleAsync_Chunks_Model_Statistics_Query()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var models = Enumerable.Range(1, 5)
            .Select(i => CreateModel(i, $"S{i:000}"))
            .ToArray();
        db.Set<MLModel>().AddRange(models);
        for (int i = 0; i < models.Length; i++)
        {
            AddPredictionLogs(db, models[i], now, correct: i < 3, count: 30, startTradeSignalId: 1 + (i * 100));
        }
        await db.SaveChangesAsync();

        var worker = CreateWorker(
            db,
            options: new LascodiaTradingEngine.Application.Common.Options.MLCorrelatedFailureOptions
            {
                ModelStatsBatchSize = 2,
                MinModelsForAlarm = 2
            });
        await worker.RunCycleAsync(CancellationToken.None);

        var log = await db.Set<MLCorrelatedFailureLog>().SingleAsync();
        Assert.Equal(5, log.EvaluatedModelCount);
        Assert.Equal(2, log.FailingModelCount);
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static SqliteApplicationDbContext CreateSqliteDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SqliteApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SqliteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLCorrelatedFailureWorker CreateWorker(
        DbContext db,
        IDistributedLock? distributedLock = null,
        LascodiaTradingEngine.Application.Common.Options.MLCorrelatedFailureOptions? options = null)
    {
        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);

        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => readContext.Object);
        services.AddScoped(_ => writeContext.Object);
        var provider = services.BuildServiceProvider();

        return new MLCorrelatedFailureWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLCorrelatedFailureWorker>>(),
            distributedLock,
            TimeProvider.System,
            null,
            new TradingMetrics(new TestMeterFactory()),
            options);
    }

    private static MLModel CreateModel(long id, string symbol, Timeframe timeframe = Timeframe.H1) => new()
    {
        Id = id,
        Symbol = symbol,
        Timeframe = timeframe,
        ModelVersion = "1.0.0",
        FilePath = "/tmp/model.json",
        Status = MLModelStatus.Active,
        IsActive = true,
        TrainingSamples = 100,
        TrainedAt = DateTime.UtcNow.AddDays(-10),
        ActivatedAt = DateTime.UtcNow.AddDays(-5)
    };

    private static void AddPredictionLogs(
        DbContext db,
        MLModel model,
        DateTime now,
        bool correct,
        int count,
        long startTradeSignalId,
        DateTime? predictedAt = null,
        DateTime? outcomeAt = null,
        bool? profitable = null)
    {
        for (int i = 0; i < count; i++)
        {
            var predictionTime = predictedAt ?? now.AddMinutes(-count + i);
            var outcomeTime = outcomeAt ?? now.AddMinutes(-count + i);
            db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
            {
                TradeSignalId = startTradeSignalId + i,
                MLModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                PredictedAt = predictionTime,
                OutcomeRecordedAt = outcomeTime,
                DirectionCorrect = correct,
                WasProfitable = profitable ?? correct,
                ConfidenceScore = 0.75m
            });
        }
    }

    private static void AddConfig(
        DbContext db,
        string key,
        string value,
        ConfigDataType dataType)
        => db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = dataType,
            IsHotReloadable = true,
            LastUpdatedAt = DateTime.UtcNow
        });

    private static async Task<string?> GetConfigValueAsync(DbContext db, string key)
        => await db.Set<EngineConfig>()
            .Where(c => c.Key == key && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync();

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);
        public void Dispose() { }
    }

    private sealed class SqliteApplicationDbContext(
        DbContextOptions<SqliteApplicationDbContext> options,
        IHttpContextAccessor httpContextAccessor)
        : ApplicationDbContext<SqliteApplicationDbContext>(
            options,
            httpContextAccessor,
            typeof(WriteApplicationDbContext).Assembly),
            IReadApplicationDbContext,
            IWriteApplicationDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<MLModel>()
                .Property(m => m.RowVersion)
                .ValueGeneratedNever();
        }
    }
}
