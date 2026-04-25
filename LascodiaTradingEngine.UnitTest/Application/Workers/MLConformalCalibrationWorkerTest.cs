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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLConformalCalibrationWorkerTest
{
    [Fact]
    public async Task RunAsync_Writes_Calibration_From_PostActivation_Evidence_And_Aligns_Snapshot()
    {
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var model = CreateModel(now.UtcDateTime.AddHours(-2));
        db.Set<MLModel>().Add(model);

        AddResolvedLog(db, model, id: 1, now.UtcDateTime.AddHours(-3), TradeDirection.Buy, servedBuyProbability: 0.05m);
        AddResolvedLog(db, model, id: 2, now.UtcDateTime.AddMinutes(-90), TradeDirection.Buy, servedBuyProbability: 0.90m);
        AddResolvedLog(db, model, id: 3, now.UtcDateTime.AddMinutes(-80), TradeDirection.Sell, servedBuyProbability: 0.20m);
        AddResolvedLog(db, model, id: 4, now.UtcDateTime.AddMinutes(-70), TradeDirection.Sell, servedBuyProbability: 0.30m);
        AddResolvedLog(db, model, id: 5, now.UtcDateTime.AddMinutes(-60), TradeDirection.Buy, servedBuyProbability: 0.60m);
        AddResolvedLog(db, model, id: 6, now.UtcDateTime.AddMinutes(-50), TradeDirection.Buy, servedBuyProbability: 0.10m);
        AddResolvedLog(db, model, id: 7, now.UtcDateTime.AddMinutes(-40), TradeDirection.Buy, servedBuyProbability: 0.95m);
        AddResolvedLog(db, model, id: 8, now.UtcDateTime.AddMinutes(-30), TradeDirection.Buy, servedBuyProbability: 0.85m);
        AddResolvedLog(db, model, id: 9, now.UtcDateTime.AddMinutes(-20), TradeDirection.Sell, servedBuyProbability: 0.25m);
        AddResolvedLog(db, model, id: 10, now.UtcDateTime.AddMinutes(-10), TradeDirection.Sell, servedBuyProbability: 0.35m);
        AddResolvedLog(db, model, id: 11, now.UtcDateTime.AddMinutes(-5), TradeDirection.Buy, servedBuyProbability: 0.55m);
        await db.SaveChangesAsync();

        var worker = CreateWorker(
            db,
            new TestTimeProvider(now),
            CreateOptions(minLogs: 10, targetCoverage: 0.60));

        var result = await worker.RunAsync(CancellationToken.None);

        var calibration = Assert.Single(await db.Set<MLConformalCalibration>().ToListAsync());
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(
            await db.Set<MLModel>().Where(m => m.Id == model.Id).Select(m => m.ModelBytes).SingleAsync());

        Assert.Equal(1, result.CalibrationsWritten);
        Assert.Equal(1, result.EvaluatedModelCount);
        Assert.Equal(10, calibration.CalibrationSamples);
        Assert.Equal(0.35, calibration.CoverageThreshold, precision: 6);
        Assert.Equal(0.60, calibration.TargetCoverage, precision: 6);
        Assert.Equal(0.70, calibration.EmpiricalCoverage!.Value, precision: 6);
        Assert.NotNull(snapshot);
        Assert.Equal(0.35, snapshot!.ConformalQHat, precision: 6);
        Assert.Equal(0.35, snapshot.ConformalQHatBuy, precision: 6);
        Assert.Equal(0.35, snapshot.ConformalQHatSell, precision: 6);
        Assert.Equal(0.60, snapshot.ConformalCoverage, precision: 6);
    }

    [Fact]
    public async Task RunAsync_Invalid_Existing_Calibration_Does_Not_Block_Replacement()
    {
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var model = CreateModel(now.UtcDateTime.AddHours(-2));
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalCalibration>().Add(new MLConformalCalibration
        {
            Id = 40,
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            CalibrationSamples = 1,
            TargetCoverage = 0.90,
            CoverageThreshold = 0.50,
            NonConformityScoresJson = "[0.5]",
            CalibratedAt = now.UtcDateTime.AddHours(-1)
        });

        for (int index = 0; index < 10; index++)
        {
            AddResolvedLog(
                db,
                model,
                id: 100 + index,
                now.UtcDateTime.AddMinutes(-90 + index),
                TradeDirection.Buy,
                servedBuyProbability: 0.90m);
        }

        await db.SaveChangesAsync();

        var worker = CreateWorker(
            db,
            new TestTimeProvider(now),
            CreateOptions(minLogs: 10, targetCoverage: 0.60));

        var result = await worker.RunAsync(CancellationToken.None);

        var calibrations = await db.Set<MLConformalCalibration>()
            .OrderBy(c => c.Id)
            .ToListAsync();

        Assert.Equal(1, result.CalibrationsWritten);
        Assert.Equal(2, calibrations.Count);
        Assert.Equal(10, calibrations.Last().CalibrationSamples);
    }

    [Fact]
    public async Task RunAsync_Stale_Existing_Calibration_Does_Not_Block_Replacement()
    {
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var model = CreateModel(now.UtcDateTime.AddHours(-2));
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalCalibration>().Add(new MLConformalCalibration
        {
            Id = 45,
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            CalibrationSamples = 10,
            TargetCoverage = 0.60,
            CoverageThreshold = 0.40,
            NonConformityScoresJson = "[0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8,0.9,1.0]",
            CalibratedAt = now.UtcDateTime.AddDays(-31)
        });

        for (int index = 0; index < 10; index++)
        {
            AddResolvedLog(
                db,
                model,
                id: 200 + index,
                now.UtcDateTime.AddMinutes(-90 + index),
                TradeDirection.Buy,
                servedBuyProbability: 0.90m);
        }

        await db.SaveChangesAsync();

        var worker = CreateWorker(
            db,
            new TestTimeProvider(now),
            CreateOptions(minLogs: 10, targetCoverage: 0.60));

        var result = await worker.RunAsync(CancellationToken.None);

        var calibrations = await db.Set<MLConformalCalibration>()
            .OrderBy(c => c.Id)
            .ToListAsync();

        Assert.Equal(1, result.CalibrationsWritten);
        Assert.Equal(2, calibrations.Count);
        Assert.Equal(now.UtcDateTime, calibrations.Last().CalibratedAt);
    }

    [Fact]
    public async Task RunAsync_Usable_Existing_Calibration_Is_Idempotent()
    {
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var model = CreateModel(now.UtcDateTime.AddHours(-2));
        db.Set<MLModel>().Add(model);
        db.Set<MLConformalCalibration>().Add(new MLConformalCalibration
        {
            Id = 50,
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            CalibrationSamples = 10,
            TargetCoverage = 0.60,
            CoverageThreshold = 0.40,
            EmpiricalCoverage = 0.80,
            NonConformityScoresJson = "[0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8,0.9,1.0]",
            CalibratedAt = now.UtcDateTime.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(
            db,
            new TestTimeProvider(now),
            CreateOptions(minLogs: 10, targetCoverage: 0.60));

        var result = await worker.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.CalibrationsWritten);
        Assert.Equal(1, result.SkippedAlreadyCalibratedCount);
        Assert.Single(await db.Set<MLConformalCalibration>().ToListAsync());
    }

    [Fact]
    public async Task RunAsync_LockBusy_Skips_Cycle()
    {
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        db.Set<MLModel>().Add(CreateModel(now.UtcDateTime.AddHours(-2)));
        await db.SaveChangesAsync();

        var worker = CreateWorker(
            db,
            new TestTimeProvider(now),
            CreateOptions(minLogs: 10, targetCoverage: 0.60),
            new TestDistributedLock(lockAvailable: false));

        var result = await worker.RunAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await db.Set<MLConformalCalibration>().ToListAsync());
    }

    [Fact]
    public async Task RunAsync_Clamps_Invalid_Options()
    {
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var worker = CreateWorker(
            db,
            new TestTimeProvider(now),
            new MLConformalCalibrationOptions
            {
                PollIntervalMinutes = 0,
                MinLogs = -10,
                MaxLogs = 1,
                TargetCoverage = 2.0,
                ModelBatchSize = 0,
                MaxCycleModels = 0,
                MaxLogAgeDays = 0,
                MaxCalibrationAgeDays = 0
            });

        var result = await worker.RunAsync(CancellationToken.None);

        Assert.Equal("no_candidate_models", result.SkippedReason);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Settings.PollInterval);
        Assert.Equal(50, result.Settings.MinLogs);
        Assert.Equal(500, result.Settings.MaxLogs);
        Assert.Equal(0.90, result.Settings.TargetCoverage, precision: 6);
        Assert.Equal(100, result.Settings.ModelBatchSize);
        Assert.Equal(10_000, result.Settings.MaxCycleModels);
        Assert.Equal(30, result.Settings.MaxLogAgeDays);
        Assert.Equal(30, result.Settings.MaxCalibrationAgeDays);
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLConformalCalibrationWorker CreateWorker(
        WriteApplicationDbContext db,
        TimeProvider timeProvider,
        MLConformalCalibrationOptions options,
        IDistributedLock? distributedLock = null)
    {
        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => writeContext.Object);
        var provider = services.BuildServiceProvider();

        return new MLConformalCalibrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<MLConformalCalibrationWorker>.Instance,
            options,
            metrics: null,
            timeProvider,
            healthMonitor: null,
            distributedLock);
    }

    private static MLConformalCalibrationOptions CreateOptions(int minLogs, double targetCoverage)
        => new()
        {
            InitialDelayMinutes = 0,
            PollIntervalMinutes = 30,
            PollJitterSeconds = 0,
            MinLogs = minLogs,
            MaxLogs = 20,
            MaxLogAgeDays = 30,
            MaxCalibrationAgeDays = 30,
            TargetCoverage = targetCoverage,
            ModelBatchSize = 10,
            MaxCycleModels = 100,
            LockTimeoutSeconds = 1,
            RequirePostActivationLogs = true
        };

    private static MLModel CreateModel(DateTime activatedAtUtc)
    {
        var snapshot = new ModelSnapshot
        {
            Type = "BaggedLogistic",
            Version = "test",
            Weights = [new[] { 1.0 }],
            Biases = [0.0],
            ConformalQHat = 0.11,
            ConformalQHatBuy = 0.22,
            ConformalQHatSell = 0.33,
            ConformalCoverage = 0.75
        };

        return new MLModel
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive = true,
            Status = MLModelStatus.Active,
            TrainedAt = activatedAtUtc.AddDays(-1),
            ActivatedAt = activatedAtUtc,
            ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot)
        };
    }

    private static void AddResolvedLog(
        WriteApplicationDbContext db,
        MLModel model,
        long id,
        DateTime outcomeRecordedAtUtc,
        TradeDirection actualDirection,
        decimal servedBuyProbability)
    {
        var predictedDirection = servedBuyProbability >= 0.5m ? TradeDirection.Buy : TradeDirection.Sell;
        db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
        {
            Id = id,
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            ModelRole = ModelRole.Champion,
            TradeSignalId = id,
            PredictedDirection = predictedDirection,
            ConfidenceScore = predictedDirection == TradeDirection.Buy
                ? servedBuyProbability
                : 1m - servedBuyProbability,
            ServedCalibratedProbability = servedBuyProbability,
            ActualDirection = actualDirection,
            DirectionCorrect = predictedDirection == actualDirection,
            OutcomeRecordedAt = DateTime.SpecifyKind(outcomeRecordedAtUtc, DateTimeKind.Utc)
        });
    }

    private sealed class TestDistributedLock(bool lockAvailable) : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(
            string lockKey,
            CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(lockAvailable ? new Releaser() : null);

        public Task<IAsyncDisposable?> TryAcquireAsync(
            string key,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable?>(lockAvailable ? new Releaser() : null);

        private sealed class Releaser : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
