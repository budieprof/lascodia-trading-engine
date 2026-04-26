using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLModelAutoRollbackWorkerTest
{
    private static readonly DateTimeOffset FixedNow = new(2026, 04, 26, 12, 00, 00, TimeSpan.Zero);

    [Fact]
    public async Task RunCycleAsync_RollsBackDegradedModelAndWritesLifecycleAudit()
    {
        using var harness = CreateHarness(db =>
        {
            SeedModel(db, id: 100, isActive: false, status: MLModelStatus.Superseded);
            SeedModel(
                db,
                id: 200,
                isActive: true,
                status: MLModelStatus.Active,
                previousChampionModelId: 100,
                consecutiveRetrainFailures: 3);
            SeedModel(db, id: 300, isActive: true, status: MLModelStatus.Active, modelVersion: "stray-active");
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var models = await harness.LoadModelsAsync();
        var fallback = Assert.Single(models, m => m.Id == 100);
        var failing = Assert.Single(models, m => m.Id == 200);
        var stray = Assert.Single(models, m => m.Id == 300);

        Assert.Equal(1, result.DegradedModelCount);
        Assert.Equal(1, result.RollbackCandidateCount);
        Assert.Equal(1, result.RollbackCount);
        Assert.True(fallback.IsActive);
        Assert.False(fallback.IsSuppressed);
        Assert.False(fallback.IsFallbackChampion);
        Assert.Equal(MLModelStatus.Active, fallback.Status);
        Assert.Equal(FixedNow.UtcDateTime, fallback.ActivatedAt);
        Assert.False(failing.IsActive);
        Assert.Equal(MLModelStatus.Failed, failing.Status);
        Assert.Equal(FixedNow.UtcDateTime, failing.DegradationRetiredAt);
        Assert.False(stray.IsActive);
        Assert.Equal(MLModelStatus.Superseded, stray.Status);

        var lifecycleLogs = await harness.LoadLifecycleLogsAsync();
        Assert.Contains(lifecycleLogs, l =>
            l.MLModelId == failing.Id &&
            l.EventType == MLModelLifecycleEventType.DegradationRetirement &&
            l.PreviousStatus == MLModelStatus.Active &&
            l.NewStatus == MLModelStatus.Failed &&
            l.PreviousChampionModelId == fallback.Id);
        Assert.Contains(lifecycleLogs, l =>
            l.MLModelId == fallback.Id &&
            l.EventType == MLModelLifecycleEventType.AutoRollbackPromotion &&
            l.PreviousStatus == MLModelStatus.Superseded &&
            l.NewStatus == MLModelStatus.Active &&
            l.PreviousChampionModelId == failing.Id);
        Assert.Contains(lifecycleLogs, l =>
            l.MLModelId == stray.Id &&
            l.EventType == MLModelLifecycleEventType.AutoRollbackDemotion &&
            l.NewStatus == MLModelStatus.Superseded &&
            l.PreviousChampionModelId == fallback.Id);

        var decision = Assert.Single(harness.Decisions);
        Assert.Equal("AutoRollback", decision.DecisionType);
        Assert.Equal(failing.Id, decision.EntityId);
        Assert.Contains("ConsecutiveRetrainFailures=3>=3", decision.Reason);
        Assert.Contains("\"FallbackModelId\":100", decision.ContextJson);
    }

    [Fact]
    public async Task RunCycleAsync_SkipsRollback_WhenPreviousChampionIsUnsafe()
    {
        using var harness = CreateHarness(db =>
        {
            SeedModel(db, id: 100, symbol: "GBPUSD", isActive: false, status: MLModelStatus.Superseded);
            SeedModel(
                db,
                id: 200,
                isActive: true,
                status: MLModelStatus.Active,
                previousChampionModelId: 100,
                plattCalibrationDrift: 0.5);
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var models = await harness.LoadModelsAsync();
        var fallback = Assert.Single(models, m => m.Id == 100);
        var failing = Assert.Single(models, m => m.Id == 200);

        Assert.Equal(1, result.DegradedModelCount);
        Assert.Equal(1, result.RollbackCandidateCount);
        Assert.Equal(0, result.RollbackCount);
        Assert.Equal(1, result.SkippedRollbackCount);
        Assert.False(fallback.IsActive);
        Assert.True(failing.IsActive);
        Assert.Equal(MLModelStatus.Active, failing.Status);
        Assert.Empty(await harness.LoadLifecycleLogsAsync());
        Assert.Empty(harness.Decisions);
    }

    [Fact]
    public async Task RunCycleAsync_SurfacesOrphanDegradedModels_WhenNoRollbackableCandidatesExist()
    {
        using var harness = CreateHarness(db =>
        {
            SeedModel(
                db,
                id: 200,
                isActive: true,
                status: MLModelStatus.Active,
                previousChampionModelId: null,
                liveDirectionAccuracy: 0.40m,
                liveTotalPredictions: 80);
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var failing = Assert.Single(await harness.LoadModelsAsync());
        Assert.Equal(1, result.DegradedModelCount);
        Assert.Equal(0, result.RollbackCandidateCount);
        Assert.Equal(1, result.OrphanModelCount);
        Assert.Equal(0, result.RollbackCount);
        Assert.Equal(1, result.SkippedRollbackCount);
        Assert.True(failing.IsActive);
        Assert.Empty(harness.Decisions);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutMutatingModels()
    {
        using var harness = CreateHarness(
            db =>
            {
                AddConfig(db, "MLAutoRollback:PollIntervalSeconds", "1", ConfigDataType.Int);
                SeedModel(db, id: 100, isActive: false, status: MLModelStatus.Superseded);
                SeedModel(
                    db,
                    id: 200,
                    isActive: true,
                    status: MLModelStatus.Active,
                    previousChampionModelId: 100,
                    consecutiveRetrainFailures: 3);
            },
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var models = await harness.LoadModelsAsync();
        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(30, result.PollSeconds);
        Assert.True(models.Single(m => m.Id == 200).IsActive);
        Assert.False(models.Single(m => m.Id == 100).IsActive);
        Assert.Empty(await harness.LoadLifecycleLogsAsync());
        Assert.Empty(harness.Decisions);
    }

    [Fact]
    public async Task LoadConfigAsync_ClampsUnsafeValues()
    {
        using var harness = CreateHarness(db =>
        {
            AddConfig(db, "MLAutoRollback:Enabled", "off", ConfigDataType.Bool);
            AddConfig(db, "MLAutoRollback:PollIntervalSeconds", "1", ConfigDataType.Int);
            AddConfig(db, "MLAutoRollback:MaxConsecutiveRetrainFailures", "0", ConfigDataType.Int);
            AddConfig(db, "MLAutoRollback:MaxPlattCalibrationDrift", "NaN", ConfigDataType.Decimal);
            AddConfig(db, "MLAutoRollback:MinLiveDirectionAccuracy", "2", ConfigDataType.Decimal);
            AddConfig(db, "MLAutoRollback:MinLivePredictionsForAccuracyCheck", "0", ConfigDataType.Int);
            AddConfig(db, "MLAutoRollback:RollbackOnPosteriorPredictiveSurprise", "no", ConfigDataType.Bool);
            AddConfig(db, "MLAutoRollback:MaxOosDrawdown", "2", ConfigDataType.Decimal);
            AddConfig(db, "MLAutoRollback:LockTimeoutSeconds", "-1", ConfigDataType.Int);
        });

        var config = await harness.LoadConfigAsync();

        Assert.False(config.Enabled);
        Assert.Equal(30, config.PollSeconds);
        Assert.Equal(1, config.MaxRetrainFailures);
        Assert.Equal(0.30, config.MaxCalibrationDrift);
        Assert.Equal(1m, config.MinLiveDirectionAccuracy);
        Assert.Equal(1, config.MinLivePredictions);
        Assert.False(config.RollbackOnPpcSurprise);
        Assert.Equal(1.0, config.MaxOosDrawdown);
        Assert.Equal(0, config.LockTimeoutSeconds);
    }

    private static WorkerHarness CreateHarness(
        Action<MLModelAutoRollbackWorkerTestContext> seed,
        IDistributedLock? distributedLock = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLModelAutoRollbackWorkerTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<MLModelAutoRollbackWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<MLModelAutoRollbackWorkerTestContext>());

        var decisions = new List<LogDecisionCommand>();
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<IRequest<ResponseData<long>>>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ResponseData<long>>, CancellationToken>((request, _) =>
            {
                if (request is LogDecisionCommand command)
                    decisions.Add(command);
            })
            .ReturnsAsync(ResponseData<long>.Init(1, true, "Logged", "00"));
        services.AddScoped(_ => mediator.Object);

        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLModelAutoRollbackWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLModelAutoRollbackWorker(
            NullLogger<MLModelAutoRollbackWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            distributedLock,
            new TestTimeProvider(FixedNow));

        return new WorkerHarness(provider, connection, worker, decisions);
    }

    private static void SeedModel(
        DbContext db,
        long id,
        string symbol = "EURUSD",
        Timeframe timeframe = Timeframe.H1,
        bool isActive = false,
        MLModelStatus status = MLModelStatus.Superseded,
        long? previousChampionModelId = null,
        string modelVersion = "1.0.0",
        int consecutiveRetrainFailures = 0,
        double? plattCalibrationDrift = null,
        decimal? liveDirectionAccuracy = null,
        int liveTotalPredictions = 0,
        byte[]? modelBytes = null)
    {
        db.Set<MLModel>().Add(new MLModel
        {
            Id = id,
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = modelVersion,
            FilePath = $"/tmp/{id}.model",
            Status = status,
            IsActive = isActive,
            PreviousChampionModelId = previousChampionModelId,
            ConsecutiveRetrainFailures = consecutiveRetrainFailures,
            PlattCalibrationDrift = plattCalibrationDrift,
            LiveDirectionAccuracy = liveDirectionAccuracy,
            LiveTotalPredictions = liveTotalPredictions,
            ModelBytes = modelBytes ?? [1, 2, 3],
            TrainedAt = FixedNow.UtcDateTime.AddDays(-7),
            ActivatedAt = isActive ? FixedNow.UtcDateTime.AddDays(-3) : FixedNow.UtcDateTime.AddDays(-6),
            LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
            IsDeleted = false,
            RowVersion = 1
        });
    }

    private static void AddConfig(
        DbContext db,
        string key,
        string value,
        ConfigDataType dataType)
    {
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = dataType,
            IsHotReloadable = true,
            LastUpdatedAt = FixedNow.UtcDateTime,
            IsDeleted = false
        });
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        MLModelAutoRollbackWorker worker,
        List<LogDecisionCommand> decisions) : IDisposable
    {
        public MLModelAutoRollbackWorker Worker { get; } = worker;
        public List<LogDecisionCommand> Decisions { get; } = decisions;

        public async Task<List<MLModel>> LoadModelsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLModelAutoRollbackWorkerTestContext>();
            return await db.Set<MLModel>()
                .AsNoTracking()
                .IgnoreQueryFilters()
                .OrderBy(model => model.Id)
                .ToListAsync();
        }

        public async Task<List<MLModelLifecycleLog>> LoadLifecycleLogsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLModelAutoRollbackWorkerTestContext>();
            return await db.Set<MLModelLifecycleLog>()
                .AsNoTracking()
                .OrderBy(log => log.MLModelId)
                .ThenBy(log => log.EventType)
                .ToListAsync();
        }

        public async Task<AutoRollbackRuntimeConfig> LoadConfigAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLModelAutoRollbackWorkerTestContext>();
            return await MLModelAutoRollbackWorker.LoadConfigAsync(db, CancellationToken.None);
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class MLModelAutoRollbackWorkerTestContext(DbContextOptions<MLModelAutoRollbackWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
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

            modelBuilder.Entity<MLModelLifecycleLog>(builder =>
            {
                builder.HasKey(log => log.Id);
                builder.HasQueryFilter(log => !log.IsDeleted);
                builder.Property(log => log.EventType).HasConversion<string>();
                builder.Property(log => log.PreviousStatus).HasConversion<string>();
                builder.Property(log => log.NewStatus).HasConversion<string>();
                builder.Property(log => log.RowVersion).HasDefaultValue(0u).ValueGeneratedNever();
                builder.Ignore(log => log.MLModel);
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
