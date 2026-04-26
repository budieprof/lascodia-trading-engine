using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLMetricsExportWorkerTest
{
    private static readonly DateTimeOffset Now = new(2026, 04, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunOnceAsync_WritesModelSpecificMetricsAndLatestLegacyAlias()
    {
        using var harness = CreateHarness(db =>
        {
            SeedActiveModel(db, 1, "EURUSD", Timeframe.H1, Now.AddDays(-10).UtcDateTime, Now.AddDays(-8).UtcDateTime);
            SeedActiveModel(db, 2, "EURUSD", Timeframe.H1, Now.AddDays(-4).UtcDateTime, Now.AddDays(-2).UtcDateTime);

            SeedResolvedLog(db, 101, 1, "EURUSD", Timeframe.H1, Now.AddHours(-4).UtcDateTime, TradeDirection.Buy, TradeDirection.Buy, 0.90m);
            SeedResolvedLog(db, 102, 1, "EURUSD", Timeframe.H1, Now.AddHours(-3).UtcDateTime, TradeDirection.Sell, TradeDirection.Sell, 0.20m);
            SeedResolvedLog(db, 201, 2, "EURUSD", Timeframe.H1, Now.AddHours(-2).UtcDateTime, TradeDirection.Buy, TradeDirection.Buy, 0.80m);
            SeedResolvedLog(db, 202, 2, "EURUSD", Timeframe.H1, Now.AddHours(-1).UtcDateTime, TradeDirection.Buy, TradeDirection.Sell, 0.80m);
        });

        var result = await harness.Worker.RunOnceAsync();

        Assert.Null(result.SkippedReason);
        Assert.Equal(2, result.ModelsExported);

        Assert.Equal("1.000000", (await harness.LoadConfigAsync("MLMetrics:Model:1:DirectionAccuracy"))?.Value);
        Assert.Equal("0.500000", (await harness.LoadConfigAsync("MLMetrics:Model:2:DirectionAccuracy"))?.Value);
        Assert.Equal("2", (await harness.LoadConfigAsync("MLMetrics:EURUSD:H1:ModelId"))?.Value);
        Assert.Equal("0.500000", (await harness.LoadConfigAsync("MLMetrics:EURUSD:H1:DirectionAccuracy"))?.Value);
        Assert.Equal("0.500000", (await harness.LoadConfigAsync("MLMetrics:EURUSD:H1:Accuracy"))?.Value);
        Assert.Equal("2", (await harness.LoadConfigAsync("MLMetrics:EURUSD:H1:SampleCount"))?.Value);
    }

    [Fact]
    public async Task RunOnceAsync_ExcludesMissingActualDirectionFromBrierSamples()
    {
        using var harness = CreateHarness(db =>
        {
            SeedActiveModel(db, 10, "GBPUSD", Timeframe.H1, Now.AddDays(-5).UtcDateTime, Now.AddDays(-5).UtcDateTime);
            SeedResolvedLog(db, 1001, 10, "GBPUSD", Timeframe.H1, Now.AddHours(-2).UtcDateTime, TradeDirection.Buy, null, 0.90m, directionCorrect: true);
            SeedResolvedLog(db, 1002, 10, "GBPUSD", Timeframe.H1, Now.AddHours(-1).UtcDateTime, TradeDirection.Buy, TradeDirection.Buy, 0.80m);
        });

        await harness.Worker.RunOnceAsync();

        Assert.Equal("1", (await harness.LoadConfigAsync("MLMetrics:Model:10:BrierSampleCount"))?.Value);
        Assert.Equal("0.040000", (await harness.LoadConfigAsync("MLMetrics:Model:10:BrierScore"))?.Value);
        Assert.Equal("1.000000", (await harness.LoadConfigAsync("MLMetrics:Model:10:DirectionAccuracy"))?.Value);
    }

    [Fact]
    public async Task RunOnceAsync_ConvertsLegacyConfidenceToBuyProbabilityByPredictedDirection()
    {
        using var harness = CreateHarness(db =>
        {
            SeedActiveModel(db, 20, "USDJPY", Timeframe.M15, Now.AddDays(-3).UtcDateTime, Now.AddDays(-2).UtcDateTime);
            SeedResolvedLog(
                db,
                2001,
                20,
                "USDJPY",
                Timeframe.M15,
                Now.AddHours(-1).UtcDateTime,
                TradeDirection.Sell,
                TradeDirection.Sell,
                servedProbability: null,
                confidenceScore: 0.80m);
        });

        await harness.Worker.RunOnceAsync();

        Assert.Equal("1", (await harness.LoadConfigAsync("MLMetrics:Model:20:BrierSampleCount"))?.Value);
        Assert.Equal("0.040000", (await harness.LoadConfigAsync("MLMetrics:Model:20:BrierScore"))?.Value);
    }

    [Fact]
    public async Task RunOnceAsync_WhenDistributedLockBusy_SkipsWithoutWritingMetrics()
    {
        using var harness = CreateHarness(
            db => SeedActiveModel(db, 30, "AUDUSD", Timeframe.H1, Now.AddDays(-3).UtcDateTime, Now.AddDays(-2).UtcDateTime),
            distributedLock: new BusyDistributedLock());

        var result = await harness.Worker.RunOnceAsync();

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Null(await harness.LoadConfigAsync("MLMetrics:Model:30:LastUpdated"));
    }

    [Fact]
    public async Task RunOnceAsync_ClampsInvalidRuntimeConfig()
    {
        using var harness = CreateHarness(db =>
        {
            AddConfig(db, "MLMetrics:PollIntervalSeconds", "-5", ConfigDataType.Int);
            AddConfig(db, "MLMetrics:WindowDays", "-10", ConfigDataType.Int);
            AddConfig(db, "MLMetrics:MaxPredictionLogsPerModel", "1", ConfigDataType.Int);
        });

        var result = await harness.Worker.RunOnceAsync();

        Assert.Equal("no_active_models", result.SkippedReason);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Settings.PollInterval);
        Assert.Equal(1, result.Settings.WindowDays);
        Assert.Equal(10, result.Settings.MaxPredictionLogsPerModel);
    }

    private static WorkerHarness CreateHarness(
        Action<MLMetricsExportWorkerTestContext> seed,
        IDistributedLock? distributedLock = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLMetricsExportWorkerTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IReadApplicationDbContext>(provider => provider.GetRequiredService<MLMetricsExportWorkerTestContext>());
        services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetRequiredService<MLMetricsExportWorkerTestContext>());

        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLMetricsExportWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLMetricsExportWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLMetricsExportWorker>.Instance,
            timeProvider: new TestTimeProvider(Now),
            distributedLock: distributedLock,
            options: new MLMetricsExportOptions());

        return new WorkerHarness(provider, connection, worker);
    }

    private static void SeedActiveModel(
        MLMetricsExportWorkerTestContext db,
        long modelId,
        string symbol,
        Timeframe timeframe,
        DateTime trainedAt,
        DateTime activatedAt)
    {
        db.Set<MLModel>().Add(new MLModel
        {
            Id = modelId,
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = $"1.0.{modelId}",
            FilePath = $"/tmp/model-{modelId}.json",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainedAt = trainedAt,
            ActivatedAt = activatedAt,
            IsDeleted = false
        });
    }

    private static void SeedResolvedLog(
        MLMetricsExportWorkerTestContext db,
        long logId,
        long modelId,
        string symbol,
        Timeframe timeframe,
        DateTime predictedAt,
        TradeDirection predictedDirection,
        TradeDirection? actualDirection,
        decimal? servedProbability,
        decimal confidenceScore = 0.50m,
        bool? directionCorrect = null)
    {
        db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
        {
            Id = logId,
            TradeSignalId = logId,
            MLModelId = modelId,
            ModelRole = ModelRole.Champion,
            Symbol = symbol,
            Timeframe = timeframe,
            PredictedDirection = predictedDirection,
            ConfidenceScore = confidenceScore,
            ServedCalibratedProbability = servedProbability,
            PredictedAt = predictedAt,
            OutcomeRecordedAt = predictedAt.AddMinutes(30),
            ActualDirection = actualDirection,
            DirectionCorrect = directionCorrect ?? (actualDirection.HasValue && predictedDirection == actualDirection),
            EnsembleDisagreement = 0.10m,
            LatencyMs = 25,
            IsDeleted = false
        });
    }

    private static void AddConfig(
        MLMetricsExportWorkerTestContext db,
        string key,
        string value,
        ConfigDataType dataType)
    {
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = dataType,
            LastUpdatedAt = Now.UtcDateTime,
            IsDeleted = false
        });
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        MLMetricsExportWorker worker)
        : IDisposable
    {
        public MLMetricsExportWorker Worker { get; } = worker;

        public async Task<EngineConfig?> LoadConfigAsync(string key)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLMetricsExportWorkerTestContext>();
            return await db.Set<EngineConfig>().AsNoTracking().SingleOrDefaultAsync(config => config.Key == key);
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class BusyDistributedLock : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);
    }

    private sealed class MLMetricsExportWorkerTestContext(DbContextOptions<MLMetricsExportWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EngineConfig>(builder =>
            {
                builder.HasKey(config => config.Id);
                builder.Property(config => config.DataType).HasConversion<string>();
                builder.HasIndex(config => config.Key).IsUnique();
                builder.HasQueryFilter(config => !config.IsDeleted);
            });

            modelBuilder.Entity<MLModel>(builder =>
            {
                builder.HasKey(model => model.Id);
                builder.Property(model => model.Timeframe).HasConversion<string>();
                builder.Property(model => model.Status).HasConversion<string>();
                builder.Property(model => model.LearnerArchitecture).HasConversion<string>();
                builder.Property(model => model.RowVersion).HasDefaultValue(0u).ValueGeneratedNever();
                builder.HasQueryFilter(model => !model.IsDeleted);

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

            modelBuilder.Entity<MLModelPredictionLog>(builder =>
            {
                builder.HasKey(log => log.Id);
                builder.Property(log => log.Timeframe).HasConversion<string>();
                builder.Property(log => log.PredictedDirection).HasConversion<string>();
                builder.Property(log => log.ModelRole).HasConversion<string>();
                builder.Property(log => log.ActualDirection).HasConversion<string>();
                builder.HasQueryFilter(log => !log.IsDeleted);

                builder.Ignore(log => log.TradeSignal);
                builder.Ignore(log => log.MLModel);
                builder.Ignore(log => log.MLConformalCalibration);
            });
        }
    }
}
