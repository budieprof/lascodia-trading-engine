using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLCovariateShiftWorkerTest
{
    private static readonly DateTimeOffset FixedNow = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunCycleAsync_NoActiveModels_SkipsCleanly()
    {
        await using var db = CreateDbContext();
        var worker = CreateWorker(db);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("no_active_models", result.SkippedReason);
        Assert.Equal(0, result.ModelsEvaluated);
        Assert.Empty(await db.Set<MLTrainingRun>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_ShiftDetected_QueuesRetrainingAndPersistsFeatureDiagnostics()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel("EURUSD", Timeframe.H1, CreateHighShiftSnapshot()));
        db.Set<Candle>().AddRange(GenerateCandles("EURUSD", Timeframe.H1, 120, FixedNow.UtcDateTime));
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(1, result.ShiftsDetected);
        Assert.Equal(1, result.RetrainingQueued);

        var run = await db.Set<MLTrainingRun>().SingleAsync();
        Assert.Equal("EURUSD", run.Symbol);
        Assert.Equal(Timeframe.H1, run.Timeframe);
        Assert.Equal(TriggerType.AutoDegrading, run.TriggerType);
        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal("CovariateShift", run.DriftTriggerType);
        Assert.Equal(1, run.Priority);
        Assert.Equal(FixedNow.UtcDateTime, run.ToDate);
        Assert.Equal(FixedNow.UtcDateTime.AddDays(-90), run.FromDate);

        using var metadata = JsonDocument.Parse(run.DriftMetadataJson!);
        Assert.True(metadata.RootElement.GetProperty("weightedPsi").GetDouble() > 0.20);
        Assert.True(metadata.RootElement.GetProperty("driftedFeatures").GetArrayLength() > 0);

        var config = await db.Set<EngineConfig>()
            .SingleAsync(entry => entry.Key == "MLCovariate:EURUSD:H1:DriftedFeatures");
        using var drifted = JsonDocument.Parse(config.Value);
        Assert.True(drifted.RootElement.GetArrayLength() > 0);
        Assert.Equal(ConfigDataType.Json, config.DataType);
    }

    [Fact]
    public async Task RunCycleAsync_AlreadyQueued_DoesNotDuplicateRetrainingRun()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel("GBPUSD", Timeframe.H1, CreateHighShiftSnapshot()));
        db.Set<Candle>().AddRange(GenerateCandles("GBPUSD", Timeframe.H1, 120, FixedNow.UtcDateTime));
        db.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol = "GBPUSD",
            Timeframe = Timeframe.H1,
            Status = RunStatus.Queued,
            StartedAt = FixedNow.UtcDateTime.AddMinutes(-10)
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.ShiftsDetected);
        Assert.Equal(0, result.RetrainingQueued);
        Assert.Single(await db.Set<MLTrainingRun>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_InsufficientCandles_SkipsWithoutQueueing()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(CreateModel("AUDUSD", Timeframe.H1, CreateHighShiftSnapshot()));
        db.Set<Candle>().AddRange(GenerateCandles("AUDUSD", Timeframe.H1, 35, FixedNow.UtcDateTime));
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.ModelsEvaluated);
        Assert.Equal(1, result.ModelsSkipped);
        Assert.Empty(await db.Set<MLTrainingRun>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_InvalidSnapshot_SkipsWithoutThrowing()
    {
        await using var db = CreateDbContext();
        db.Set<MLModel>().Add(new MLModel
        {
            Id = 1,
            Symbol = "USDJPY",
            Timeframe = Timeframe.H1,
            IsActive = true,
            IsDeleted = false,
            ModelBytes = "{not-json}"u8.ToArray()
        });
        db.Set<Candle>().AddRange(GenerateCandles("USDJPY", Timeframe.H1, 120, FixedNow.UtcDateTime));
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.ModelsEvaluated);
        Assert.Equal(1, result.ModelsSkipped);
        Assert.Empty(await db.Set<MLTrainingRun>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_LockBusy_SkipsBeforeTouchingDatabase()
    {
        await using var db = CreateDbContext();
        var distributedLock = new Mock<IDistributedLock>();
        distributedLock
            .Setup(locker => locker.TryAcquireAsync(
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var worker = CreateWorker(db, distributedLock: distributedLock.Object);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await db.Set<MLTrainingRun>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_RuntimeConfigClampsUnsafePollInterval()
    {
        await using var db = CreateDbContext();
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLCovariate:PollIntervalSeconds",
            Value = "1",
            DataType = ConfigDataType.Int
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("no_active_models", result.SkippedReason);
        Assert.Equal(TimeSpan.FromSeconds(60), result.Settings.PollInterval);
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLCovariateShiftWorker CreateWorker(
        DbContext db,
        MLCovariateShiftOptions? options = null,
        IDistributedLock? distributedLock = null)
    {
        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(context => context.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => writeContext.Object);
        var provider = services.BuildServiceProvider();

        return new MLCovariateShiftWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLCovariateShiftWorker>>(),
            options ?? Options(),
            metrics: null,
            timeProvider: new TestTimeProvider(FixedNow),
            healthMonitor: null,
            distributedLock: distributedLock);
    }

    private static MLCovariateShiftOptions Options()
        => new()
        {
            InitialDelaySeconds = 0,
            PollIntervalSeconds = 300,
            PollJitterSeconds = 0,
            WindowDays = 120,
            PsiThreshold = 0.20,
            PerFeaturePsiThreshold = 0.25,
            MultivariateThreshold = 1.50,
            MinCandles = 40,
            TrainingDays = 90,
            MaxModelsPerCycle = 100,
            MaxQueuedRetrains = 100,
            RetrainCooldownSeconds = 0,
            LockTimeoutSeconds = 1
        };

    private static MLModel CreateModel(string symbol, Timeframe timeframe, ModelSnapshot snapshot)
        => new()
        {
            Id = Random.Shared.Next(1, int.MaxValue),
            Symbol = symbol,
            Timeframe = timeframe,
            IsActive = true,
            IsDeleted = false,
            ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot)
        };

    private static ModelSnapshot CreateHighShiftSnapshot()
    {
        var featureCount = MLFeatureHelper.FeatureCount;
        return new ModelSnapshot
        {
            Type = "TEST",
            Version = "1.0",
            ExpectedInputFeatures = featureCount,
            FeatureSchemaVersion = 1,
            Features = MLFeatureHelper.ResolveFeatureNames(featureCount),
            Means = Enumerable.Repeat(0f, featureCount).ToArray(),
            Stds = Enumerable.Repeat(0.0001f, featureCount).ToArray(),
            FeatureImportance = Enumerable.Repeat(1f / featureCount, featureCount).ToArray()
        };
    }

    private static List<Candle> GenerateCandles(
        string symbol,
        Timeframe timeframe,
        int count,
        DateTime nowUtc)
    {
        var candles = new List<Candle>(count);
        var price = 1.1000m;
        for (var i = 0; i < count; i++)
        {
            var change = i % 4 == 0 ? 0.0012m : -0.00035m;
            price += change;
            candles.Add(new Candle
            {
                Id = i + 1,
                Symbol = symbol,
                Timeframe = timeframe,
                Timestamp = nowUtc.AddHours(-count + i + 1),
                Open = price,
                High = price + 0.0020m,
                Low = price - 0.0015m,
                Close = price + change,
                Volume = 1_000 + i * 25,
                IsClosed = true,
                IsDeleted = false
            });
        }

        return candles;
    }
}
