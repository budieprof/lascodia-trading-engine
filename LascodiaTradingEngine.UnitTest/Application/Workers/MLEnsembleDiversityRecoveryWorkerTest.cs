using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLEnsembleDiversityRecoveryWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_CollapsedBaggedModel_QueuesDiversityRecoveryRetrain()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                db.Set<MLModel>().Add(CreateModel(
                    id: 11,
                    symbol: "eurusd",
                    LearnerArchitecture.BaggedLogistic,
                    diversityScore: 0.91));
            },
            timeProvider: new TestTimeProvider(now),
            options: new MLEnsembleDiversityRecoveryOptions
            {
                ForcedNclLambda = 0.42,
                ForcedDiversityLambda = 0.24,
                MaxEnsembleDiversity = 0.75,
                TrainingDataWindowDays = 180,
                MinTimeBetweenRetrainsHours = 0,
            });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(1, result.CollapsesDetected);
        Assert.Equal(1, result.RetrainingQueued);

        var run = Assert.Single(await harness.LoadTrainingRunsAsync());
        Assert.Equal("EURUSD", run.Symbol);
        Assert.Equal(Timeframe.H1, run.Timeframe);
        Assert.Equal(LearnerArchitecture.BaggedLogistic, run.LearnerArchitecture);
        Assert.Equal(TriggerType.AutoDegrading, run.TriggerType);
        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal("EnsembleDiversityRecovery", run.DriftTriggerType);
        Assert.Equal(2, run.Priority);
        Assert.Equal(now.UtcDateTime, run.ToDate);
        Assert.Equal(now.AddDays(-180).UtcDateTime, run.FromDate);

        using var hyperparams = JsonDocument.Parse(run.HyperparamConfigJson!);
        Assert.Equal(0.42, hyperparams.RootElement.GetProperty("nclLambda").GetDouble(), 6);
        Assert.Equal(0.24, hyperparams.RootElement.GetProperty("diversityLambda").GetDouble(), 6);
        Assert.Equal(11, hyperparams.RootElement.GetProperty("sourceModelId").GetInt64());

        using var metadata = JsonDocument.Parse(run.DriftMetadataJson!);
        Assert.Equal("HighCorrelationIsBad", metadata.RootElement.GetProperty("metricMode").GetString());
        Assert.Equal(0.91, metadata.RootElement.GetProperty("ensembleDiversity").GetDouble(), 6);
    }

    [Fact]
    public async Task RunCycleAsync_ElmHighDisagreement_DoesNotQueueFalseRecovery()
    {
        using var harness = CreateHarness(
            seed: db =>
            {
                db.Set<MLModel>().Add(CreateModel(
                    id: 12,
                    symbol: "GBPUSD",
                    LearnerArchitecture.Elm,
                    diversityScore: 0.90));
            },
            options: new MLEnsembleDiversityRecoveryOptions
            {
                MaxEnsembleDiversity = 0.75,
                MinDisagreementDiversity = 0.05,
            });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(0, result.CollapsesDetected);
        Assert.Equal(0, result.RetrainingQueued);
        Assert.Empty(await harness.LoadTrainingRunsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_ElmLowDisagreement_QueuesRecovery()
    {
        using var harness = CreateHarness(
            seed: db =>
            {
                db.Set<MLModel>().Add(CreateModel(
                    id: 13,
                    symbol: "USDJPY",
                    LearnerArchitecture.Elm,
                    diversityScore: 0.02));
            },
            options: new MLEnsembleDiversityRecoveryOptions
            {
                MinDisagreementDiversity = 0.05,
                MinTimeBetweenRetrainsHours = 0,
            });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(1, result.CollapsesDetected);
        Assert.Equal(1, result.RetrainingQueued);

        var run = Assert.Single(await harness.LoadTrainingRunsAsync());
        using var metadata = JsonDocument.Parse(run.DriftMetadataJson!);
        Assert.Equal("LowDisagreementIsBad", metadata.RootElement.GetProperty("metricMode").GetString());
    }

    [Fact]
    public async Task RunCycleAsync_ExistingQueuedRun_SkipsDuplicateRecovery()
    {
        using var harness = CreateHarness(
            seed: db =>
            {
                db.Set<MLModel>().Add(CreateModel(
                    id: 14,
                    symbol: "AUDUSD",
                    LearnerArchitecture.BaggedLogistic,
                    diversityScore: 0.95));
                db.Set<MLTrainingRun>().Add(new MLTrainingRun
                {
                    Symbol = "AUDUSD",
                    Timeframe = Timeframe.H1,
                    Status = RunStatus.Queued,
                    TriggerType = TriggerType.AutoDegrading,
                    DriftTriggerType = "AccuracyDrift",
                    StartedAt = DateTime.UtcNow,
                    FromDate = DateTime.UtcNow.AddDays(-365),
                    ToDate = DateTime.UtcNow,
                });
            },
            options: new MLEnsembleDiversityRecoveryOptions
            {
                MinTimeBetweenRetrainsHours = 0,
            });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(1, result.CollapsesDetected);
        Assert.Equal(0, result.RetrainingQueued);
        Assert.Single(await harness.LoadTrainingRunsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_LockBusy_ReturnsLockBusySkip()
    {
        using var harness = CreateHarness(
            seed: _ => { },
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(0, result.ModelsEvaluated);
        Assert.Equal(0, result.RetrainingQueued);
    }

    [Fact]
    public async Task LoadSettingsAsync_InvalidRuntimeValuesAreClamped()
    {
        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLDiversityRecovery:PollIntervalSeconds", "-10");
                AddConfig(db, "MLDiversityRecovery:MaxEnsembleDiversity", "2");
                AddConfig(db, "MLDiversityRecovery:ForcedNclLambda", "99");
                AddConfig(db, "MLDiversityRecovery:MaxModelsPerCycle", "0");
            });

        await using var scope = harness.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<MLEnsembleDiversityRecoveryTestContext>();
        var settings = await MLEnsembleDiversityRecoveryWorker.LoadSettingsAsync(db, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(21_600), settings.PollInterval);
        Assert.Equal(0.75, settings.MaxCorrelationScore, 6);
        Assert.Equal(0.30, settings.ForcedNclLambda, 6);
        Assert.Equal(512, settings.MaxModelsPerCycle);
    }

    private static WorkerHarness CreateHarness(
        Action<MLEnsembleDiversityRecoveryTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null,
        MLEnsembleDiversityRecoveryOptions? options = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLEnsembleDiversityRecoveryTestContext>(builder => builder.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<MLEnsembleDiversityRecoveryTestContext>());
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<MLEnsembleDiversityRecoveryTestContext>());
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLEnsembleDiversityRecoveryTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLEnsembleDiversityRecoveryWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLEnsembleDiversityRecoveryWorker>.Instance,
            options: options,
            distributedLock: distributedLock,
            timeProvider: timeProvider);

        return new WorkerHarness(provider, connection, worker);
    }

    private static MLModel CreateModel(
        long id,
        string symbol,
        LearnerArchitecture architecture,
        double diversityScore)
        => new()
        {
            Id = id,
            Symbol = symbol,
            Timeframe = Timeframe.H1,
            ModelVersion = "1.0.0",
            FilePath = "/tmp/model.bin",
            Status = MLModelStatus.Active,
            IsActive = true,
            TrainedAt = new DateTime(2026, 04, 20, 0, 0, 0, DateTimeKind.Utc),
            ActivatedAt = new DateTime(2026, 04, 21, 0, 0, 0, DateTimeKind.Utc),
            LearnerArchitecture = architecture,
            ModelBytes = JsonSerializer.SerializeToUtf8Bytes(new ModelSnapshot
            {
                Type = architecture.ToString(),
                Version = "1.0.0",
                Weights = [[1.0, 0.5], [0.9, 0.4]],
                EnsembleDiversity = diversityScore,
            }),
            IsDeleted = false,
            RowVersion = 1,
        };

    private static void AddConfig(MLEnsembleDiversityRecoveryTestContext db, string key, string value)
    {
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = ConfigDataType.String,
            IsHotReloadable = true,
            LastUpdatedAt = DateTime.UtcNow,
            IsDeleted = false,
        });
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        MLEnsembleDiversityRecoveryWorker worker) : IDisposable
    {
        public MLEnsembleDiversityRecoveryWorker Worker { get; } = worker;

        public async Task<List<MLTrainingRun>> LoadTrainingRunsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLEnsembleDiversityRecoveryTestContext>();
            return await db.Set<MLTrainingRun>().AsNoTracking().OrderBy(run => run.Id).ToListAsync();
        }

        public AsyncServiceScope NewScope() => provider.CreateAsyncScope();

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class MLEnsembleDiversityRecoveryTestContext(DbContextOptions<MLEnsembleDiversityRecoveryTestContext> options)
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

            modelBuilder.Entity<MLTrainingRun>(builder =>
            {
                builder.HasKey(run => run.Id);
                builder.HasQueryFilter(run => !run.IsDeleted);
                builder.Property(run => run.Timeframe).HasConversion<string>();
                builder.Property(run => run.TriggerType).HasConversion<string>();
                builder.Property(run => run.Status).HasConversion<string>();
                builder.Property(run => run.LearnerArchitecture).HasConversion<string>();
                builder.Ignore(run => run.MLModel);
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
