using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLCausalFeatureWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_UsesRawPredictionFeaturesAndSignedOutcomes_WithoutCandleFallback()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLCausal:MinSamples", "80");
                AddConfig(db, "MLCausal:MaxLogsPerModel", "200");

                SeedActiveModel(db, modelId: 1, symbol: "EURUSD", timeframe: Timeframe.H1);
                SeedCausalLogs(db, modelId: 1, symbol: "EURUSD", timeframe: Timeframe.H1, nowUtc: now.UtcDateTime, count: 160);
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var audits = await harness.LoadAuditsAsync();

        var feature0 = Assert.Single(audits, audit => audit.FeatureIndex == 0);
        var feature2 = Assert.Single(audits, audit => audit.FeatureIndex == 2);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(0, result.SkippedModelCount);
        Assert.Equal(3, result.AuditsWrittenCount);
        Assert.True(feature0.IsCausal);
        Assert.True(feature0.GrangerPValue < 0.05m);
        Assert.False(feature2.IsCausal);
        Assert.Equal("LagSignal", feature0.FeatureName);
    }

    [Fact]
    public async Task RunCycleAsync_PreservesMaskedFlagsAcrossAuditRefresh()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLCausal:MinSamples", "80");

                SeedActiveModel(db, modelId: 2, symbol: "GBPUSD", timeframe: Timeframe.M15);
                SeedCausalLogs(db, modelId: 2, symbol: "GBPUSD", timeframe: Timeframe.M15, nowUtc: now.UtcDateTime, count: 160);

                db.Set<MLCausalFeatureAudit>().Add(new MLCausalFeatureAudit
                {
                    Id = 700,
                    MLModelId = 2,
                    Symbol = "GBPUSD",
                    Timeframe = Timeframe.M15,
                    FeatureIndex = 1,
                    FeatureName = "Noise",
                    GrangerFStat = 0.1m,
                    GrangerPValue = 0.9m,
                    LagOrder = 1,
                    IsCausal = false,
                    IsMaskedForTraining = true,
                    ComputedAt = now.AddDays(-7).UtcDateTime,
                    IsDeleted = false
                });
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var activeAudits = await harness.LoadAuditsAsync();

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(1, result.PreservedMaskCount);

        // Upsert-in-place: the row keeps its Id, mask is preserved, ComputedAt advances.
        var preserved = activeAudits.Single(audit => audit.FeatureIndex == 1);
        Assert.Equal(700, preserved.Id);
        Assert.True(preserved.IsMaskedForTraining);
        Assert.False(preserved.IsDeleted);
        Assert.Equal(now.UtcDateTime, preserved.ComputedAt);
    }

    [Fact]
    public async Task RunCycleAsync_MalformedRawFeaturesSkipModelWithoutWritingAudits()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLCausal:MinSamples", "50");

                SeedActiveModel(db, modelId: 3, symbol: "USDJPY", timeframe: Timeframe.H4);
                SeedMalformedLogs(db, modelId: 3, symbol: "USDJPY", timeframe: Timeframe.H4, nowUtc: now.UtcDateTime, count: 80);
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(0, result.EvaluatedModelCount);
        Assert.Equal(1, result.SkippedModelCount);
        Assert.Empty(await harness.LoadAuditsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutMutatingState()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLCausal:MinSamples", "50");
                SeedActiveModel(db, modelId: 4, symbol: "AUDUSD", timeframe: Timeframe.H1);
                SeedCausalLogs(db, modelId: 4, symbol: "AUDUSD", timeframe: Timeframe.H1, nowUtc: now.UtcDateTime, count: 120);
            },
            timeProvider: new TestTimeProvider(now),
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await harness.LoadAuditsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_InvalidSettingsAreClampedSafely()
    {
        using var harness = CreateHarness(seed: db =>
        {
            AddConfig(db, "MLCausal:Enabled", "true");
            AddConfig(db, "MLCausal:PollIntervalSeconds", "-1");
            AddConfig(db, "MLCausal:WindowDays", "0");
            AddConfig(db, "MLCausal:MinSamples", "0");
            AddConfig(db, "MLCausal:MaxLogsPerModel", "0");
            AddConfig(db, "MLCausal:MaxModelsPerCycle", "0");
            AddConfig(db, "MLCausal:MaxLag", "0");
            AddConfig(db, "MLCausal:PValueThreshold", "-1");
            AddConfig(db, "MLCausal:LockTimeoutSeconds", "-1");
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("no_active_models", result.SkippedReason);
        Assert.Equal(TimeSpan.FromDays(7), result.Settings.PollInterval);
        Assert.Equal(180, result.Settings.WindowDays);
        Assert.Equal(80, result.Settings.MinSamples);
        Assert.Equal(512, result.Settings.MaxLogsPerModel);
        Assert.Equal(256, result.Settings.MaxModelsPerCycle);
        Assert.Equal(10, result.Settings.MaxLag);
        Assert.Equal(0.05, result.Settings.PValueThreshold, 6);
        Assert.Equal(0, result.Settings.LockTimeoutSeconds);
        Assert.Equal(0.05, result.Settings.FdrAlpha, 6);
        Assert.Equal(2, result.Settings.ConsecutiveSkipAlertThreshold);
        Assert.Equal(0.5, result.Settings.RegressionThreshold, 6);
        Assert.Equal(5, result.Settings.MinPriorCausalForRegression);
        Assert.Equal(10, result.Settings.MaxAlertsPerCycle);
    }

    [Fact]
    public void ApplyBenjaminiHochberg_RejectsOnlyPValuesUnderStepUpThreshold()
    {
        // m=10, alpha=0.05. p-values sorted ascending vs k*alpha/m thresholds:
        //   k=1: 0.001  vs 0.005  → reject
        //   k=2: 0.005  vs 0.010  → reject
        //   k=3: 0.012  vs 0.015  → reject
        //   k=4: 0.030  vs 0.020  → not under, but BH is step-up: keep scanning
        //   k=5: 0.040  vs 0.025  → not under
        //   k=6: 0.050  vs 0.030  → not under
        //   ... none after k=3 satisfy, so largest rejected rank is 3.
        // Bonferroni would reject only k=1,2 (under 0.005). BH rejects k=1..3.
        var pValues = new[] { 0.001, 0.005, 0.012, 0.030, 0.040, 0.050, 0.100, 0.200, 0.500, 0.900 };
        var rejected = MLCausalFeatureWorker.ApplyBenjaminiHochberg(pValues, alpha: 0.05);

        Assert.True(rejected[0]);
        Assert.True(rejected[1]);
        Assert.True(rejected[2]);
        for (int i = 3; i < pValues.Length; i++)
            Assert.False(rejected[i]);
    }

    [Fact]
    public void ApplyBenjaminiHochberg_AllNullPValues_RejectsNone()
    {
        var pValues = new[] { 0.5, 0.6, 0.7, 0.8, 0.9 };
        var rejected = MLCausalFeatureWorker.ApplyBenjaminiHochberg(pValues, alpha: 0.05);
        Assert.All(rejected, flag => Assert.False(flag));
    }

    [Fact]
    public void SolveSpdLinearSystem_SolvesIdentityExactly()
    {
        var a = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        var b = new double[] { 7, -3, 11 };

        var x = MLCausalFeatureWorker.SolveSpdLinearSystem(a, b);

        Assert.NotNull(x);
        Assert.Equal(7, x![0], 9);
        Assert.Equal(-3, x[1], 9);
        Assert.Equal(11, x[2], 9);
    }

    [Fact]
    public void SolveSpdLinearSystem_SolvesGeneralSpdMatrix()
    {
        // A = [[4,2,1],[2,5,3],[1,3,6]] is symmetric positive definite.
        // Reference solution computed by hand: A * [1, 2, 3]ᵀ = [11, 21, 25]ᵀ.
        var a = new double[,] { { 4, 2, 1 }, { 2, 5, 3 }, { 1, 3, 6 } };
        var b = new double[] { 11, 21, 25 };

        var x = MLCausalFeatureWorker.SolveSpdLinearSystem(a, b);

        Assert.NotNull(x);
        Assert.Equal(1.0, x![0], 9);
        Assert.Equal(2.0, x[1], 9);
        Assert.Equal(3.0, x[2], 9);
    }

    [Fact]
    public async Task RunCycleAsync_StaleMonitoring_FiresAlertWhenSkipStreakReachesThreshold()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLCausal:MinSamples", "80");
                AddConfig(db, "MLCausal:ConsecutiveSkipAlertThreshold", "2");

                SeedActiveModel(db, modelId: 50, symbol: "AUDUSD", timeframe: Timeframe.H1);
                SeedMalformedLogs(db, modelId: 50, symbol: "AUDUSD", timeframe: Timeframe.H1, nowUtc: now.UtcDateTime, count: 80);

                db.Set<EngineConfig>().Add(new EngineConfig
                {
                    Key = "MLCausal:Model:50:ConsecutiveSkips",
                    Value = "1",
                    DataType = ConfigDataType.Int,
                    IsHotReloadable = false,
                    LastUpdatedAt = DateTime.UtcNow,
                    IsDeleted = false
                });
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.SkippedModelCount);
        Assert.Equal(1, result.StaleMonitoringAlertCount);

        var alerts = await harness.LoadAlertsAsync();
        var stale = Assert.Single(alerts, alert => alert.DeduplicationKey == "ml-causal-stale:50");
        Assert.Equal(AlertType.MLMonitoringStale, stale.AlertType);
        Assert.True(stale.IsActive);
        Assert.Equal(AlertSeverity.High, stale.Severity);
    }

    [Fact]
    public async Task RunCycleAsync_StaleMonitoringResolves_WhenModelEvaluatesAgain()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLCausal:MinSamples", "80");

                SeedActiveModel(db, modelId: 51, symbol: "NZDUSD", timeframe: Timeframe.H1);
                SeedCausalLogs(db, modelId: 51, symbol: "NZDUSD", timeframe: Timeframe.H1, nowUtc: now.UtcDateTime, count: 160);

                db.Set<EngineConfig>().Add(new EngineConfig
                {
                    Key = "MLCausal:Model:51:ConsecutiveSkips",
                    Value = "5",
                    DataType = ConfigDataType.Int,
                    IsHotReloadable = false,
                    LastUpdatedAt = DateTime.UtcNow,
                    IsDeleted = false
                });

                db.Set<Alert>().Add(new Alert
                {
                    Id = 9100,
                    AlertType = AlertType.MLMonitoringStale,
                    Symbol = "NZDUSD",
                    ConditionJson = "{}",
                    DeduplicationKey = "ml-causal-stale:51",
                    IsActive = true,
                    Severity = AlertSeverity.High,
                    CooldownSeconds = 3600,
                    LastTriggeredAt = now.AddHours(-2).UtcDateTime,
                    IsDeleted = false
                });
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.EvaluatedModelCount);

        var alerts = await harness.LoadAlertsAsync(ignoreQueryFilters: true);
        var stale = Assert.Single(alerts, alert => alert.DeduplicationKey == "ml-causal-stale:51");
        Assert.False(stale.IsActive);
        Assert.NotNull(stale.AutoResolvedAt);

        var streakConfig = await harness.LoadConfigAsync("MLCausal:Model:51:ConsecutiveSkips");
        Assert.Equal("0", streakConfig?.Value);
    }

    [Fact]
    public async Task RunCycleAsync_RegressionAlert_FiresWhenCausalRatioDropsPastThreshold()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLCausal:MinSamples", "80");
                AddConfig(db, "MLCausal:RegressionThreshold", "0.5");
                AddConfig(db, "MLCausal:MinPriorCausalForRegression", "1");

                SeedActiveModel(db, modelId: 60, symbol: "EURJPY", timeframe: Timeframe.H1);
                // Logs whose only causal feature is index 0; index 1 is independent noise.
                // Out of 3 features, ~1 will be causal → ratio ≈ 0.33.
                SeedCausalLogs(db, modelId: 60, symbol: "EURJPY", timeframe: Timeframe.H1, nowUtc: now.UtcDateTime, count: 160);

                // Prior cycle reported 100% causal across the 3 features (count=3, ratio=1.0).
                db.Set<EngineConfig>().AddRange(
                    new EngineConfig
                    {
                        Key = "MLCausal:Model:60:CausalRatio",
                        Value = "1.0000",
                        DataType = ConfigDataType.Decimal,
                        IsHotReloadable = false,
                        LastUpdatedAt = DateTime.UtcNow,
                        IsDeleted = false
                    },
                    new EngineConfig
                    {
                        Key = "MLCausal:Model:60:CausalCount",
                        Value = "3",
                        DataType = ConfigDataType.Int,
                        IsHotReloadable = false,
                        LastUpdatedAt = DateTime.UtcNow,
                        IsDeleted = false
                    });
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(1, result.RegressionAlertCount);

        var alerts = await harness.LoadAlertsAsync();
        var regression = Assert.Single(alerts, alert => alert.DeduplicationKey == "ml-causal-regression:60");
        Assert.Equal(AlertType.MLModelDegraded, regression.AlertType);
        Assert.True(regression.IsActive);
    }

    [Fact]
    public async Task RunCycleAsync_AlertBackpressure_HaltsAtMaxAlertsPerCycle()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLCausal:MinSamples", "80");
                AddConfig(db, "MLCausal:ConsecutiveSkipAlertThreshold", "1");
                AddConfig(db, "MLCausal:MaxAlertsPerCycle", "1");

                for (int modelId = 70; modelId <= 72; modelId++)
                {
                    string symbol = modelId == 70 ? "EURUSD" : modelId == 71 ? "GBPUSD" : "USDJPY";
                    SeedActiveModel(db, modelId, symbol, Timeframe.H1);
                    SeedMalformedLogs(db, modelId, symbol, Timeframe.H1, now.UtcDateTime, 80);
                }
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(3, result.SkippedModelCount);
        Assert.Equal(1, result.StaleMonitoringAlertCount);
        Assert.Equal(2, result.AlertBackpressureSkippedCount);
    }

    private static WorkerHarness CreateHarness(
        Action<MLCausalFeatureWorkerTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var effectiveTimeProvider = timeProvider ?? new TestTimeProvider(now);

        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLCausalFeatureWorkerTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetRequiredService<MLCausalFeatureWorkerTestContext>());
        services.AddSingleton<IAlertDispatcher>(new TestAlertDispatcher(effectiveTimeProvider));

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLCausalFeatureWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLCausalFeatureWorker(
            NullLogger<MLCausalFeatureWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            metrics: null,
            timeProvider: effectiveTimeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, connection, worker);
    }

    private static void AddConfig(
        MLCausalFeatureWorkerTestContext db,
        string key,
        string value,
        ConfigDataType dataType = ConfigDataType.String)
    {
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = dataType,
            IsHotReloadable = true,
            LastUpdatedAt = DateTime.UtcNow,
            IsDeleted = false
        });
    }

    private static void SeedActiveModel(
        MLCausalFeatureWorkerTestContext db,
        long modelId,
        string symbol,
        Timeframe timeframe)
    {
        db.Set<MLModel>().Add(new MLModel
        {
            Id = modelId,
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = "1.0.0",
            FilePath = "test-model.json",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainedAt = DateTime.UtcNow.AddDays(-5),
            ModelBytes = CreateModelBytes(),
            LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
            IsDeleted = false
        });
    }

    private static byte[] CreateModelBytes()
        => JsonSerializer.SerializeToUtf8Bytes(new ModelSnapshot
        {
            ExpectedInputFeatures = 3,
            FeatureSchemaVersion = 1,
            Features = ["LagSignal", "Noise", "Constant"]
        });

    private static void SeedCausalLogs(
        MLCausalFeatureWorkerTestContext db,
        long modelId,
        string symbol,
        Timeframe timeframe,
        DateTime nowUtc,
        int count)
    {
        double[] lagSignal = new double[count];
        for (int index = 0; index < count; index++)
            lagSignal[index] = Math.Sin(index * 0.17) + (0.4 * Math.Cos(index * 0.07));

        double previousY = 0.0;
        for (int index = 0; index < count; index++)
        {
            double outcome = index == 0
                ? 0.0
                : (0.45 * previousY) + (1.35 * lagSignal[index - 1]) + (0.03 * Math.Sin(index * 0.11));
            previousY = outcome;

            double[] rawFeatures =
            [
                lagSignal[index],
                Math.Cos(index * 0.31),
                0.0
            ];

            var predictedAt = nowUtc.AddMinutes(-(count - index) * 15);

            db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
            {
                Id = index + 1,
                TradeSignalId = (index + 1) * 10,
                MLModelId = modelId,
                ModelRole = ModelRole.Champion,
                Symbol = symbol,
                Timeframe = timeframe,
                PredictedDirection = outcome >= 0 ? TradeDirection.Buy : TradeDirection.Sell,
                ConfidenceScore = 0.75m,
                ActualDirection = outcome >= 0 ? TradeDirection.Buy : TradeDirection.Sell,
                ActualMagnitudePips = (decimal)outcome,
                DirectionCorrect = true,
                PredictedAt = predictedAt,
                OutcomeRecordedAt = predictedAt.AddMinutes(5),
                RawFeaturesJson = JsonSerializer.Serialize(rawFeatures),
                IsDeleted = false
            });
        }
    }

    private static void SeedMalformedLogs(
        MLCausalFeatureWorkerTestContext db,
        long modelId,
        string symbol,
        Timeframe timeframe,
        DateTime nowUtc,
        int count)
    {
        long idBase = 1_000_000L + (modelId * 10_000L);
        for (int index = 0; index < count; index++)
        {
            var predictedAt = nowUtc.AddMinutes(-(count - index) * 15);
            db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
            {
                Id = idBase + index,
                TradeSignalId = (idBase + index) * 10,
                MLModelId = modelId,
                ModelRole = ModelRole.Champion,
                Symbol = symbol,
                Timeframe = timeframe,
                PredictedDirection = TradeDirection.Buy,
                ConfidenceScore = 0.60m,
                ActualDirection = TradeDirection.Buy,
                ActualMagnitudePips = 1m,
                DirectionCorrect = true,
                PredictedAt = predictedAt,
                OutcomeRecordedAt = predictedAt.AddMinutes(5),
                RawFeaturesJson = index % 2 == 0 ? "[1.0,2.0]" : "[1.0,",
                IsDeleted = false
            });
        }
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        MLCausalFeatureWorker worker) : IDisposable
    {
        public MLCausalFeatureWorker Worker { get; } = worker;

        public async Task<List<MLCausalFeatureAudit>> LoadAuditsAsync(bool ignoreQueryFilters = false)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLCausalFeatureWorkerTestContext>();

            IQueryable<MLCausalFeatureAudit> query = db.Set<MLCausalFeatureAudit>().AsNoTracking();
            if (ignoreQueryFilters)
                query = query.IgnoreQueryFilters();

            return await query.OrderBy(audit => audit.Id).ToListAsync();
        }

        public async Task<List<Alert>> LoadAlertsAsync(bool ignoreQueryFilters = false)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLCausalFeatureWorkerTestContext>();

            IQueryable<Alert> query = db.Set<Alert>().AsNoTracking();
            if (ignoreQueryFilters)
                query = query.IgnoreQueryFilters();

            return await query.OrderBy(alert => alert.Id).ToListAsync();
        }

        public async Task<EngineConfig?> LoadConfigAsync(string key)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLCausalFeatureWorkerTestContext>();
            return await db.Set<EngineConfig>()
                .AsNoTracking()
                .SingleOrDefaultAsync(config => config.Key == key);
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class MLCausalFeatureWorkerTestContext(DbContextOptions<MLCausalFeatureWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EngineConfig>(builder =>
            {
                builder.HasKey(config => config.Id);
                builder.Property(config => config.DataType).HasConversion<string>();
                builder.HasQueryFilter(config => !config.IsDeleted);
                builder.HasIndex(config => config.Key).IsUnique();
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

            modelBuilder.Entity<MLCausalFeatureAudit>(builder =>
            {
                builder.HasKey(audit => audit.Id);
                builder.Property(audit => audit.Timeframe).HasConversion<string>();
                builder.HasQueryFilter(audit => !audit.IsDeleted);
                builder.HasIndex(audit => new { audit.MLModelId, audit.FeatureIndex });

                builder.Ignore(audit => audit.MLModel);
            });

            modelBuilder.Entity<Alert>(builder =>
            {
                builder.HasKey(alert => alert.Id);
                builder.Property(alert => alert.AlertType).HasConversion<string>();
                builder.Property(alert => alert.Severity).HasConversion<string>();
                builder.HasQueryFilter(alert => !alert.IsDeleted);
                builder.HasIndex(alert => alert.DeduplicationKey)
                    .IsUnique()
                    .HasFilter("\"IsActive\" = TRUE AND \"IsDeleted\" = FALSE AND \"DeduplicationKey\" IS NOT NULL");
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

    private sealed class TestAlertDispatcher(TimeProvider timeProvider) : IAlertDispatcher
    {
        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
        {
            alert.LastTriggeredAt = timeProvider.GetUtcNow().UtcDateTime;
            return Task.CompletedTask;
        }

        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
        {
            alert.AutoResolvedAt = timeProvider.GetUtcNow().UtcDateTime;
            return Task.CompletedTask;
        }
    }
}
