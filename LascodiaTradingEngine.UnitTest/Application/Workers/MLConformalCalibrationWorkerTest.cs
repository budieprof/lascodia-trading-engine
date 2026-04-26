using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Alerts;
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
        // New defaults
        Assert.Equal(1, result.Settings.MaxDegreeOfParallelism);
        Assert.Equal(300, result.Settings.LongCycleWarnSeconds);
        Assert.True(result.Settings.StaleAlertEnabled);
        Assert.Equal(5, result.Settings.StaleSkipAlertThreshold);
    }

    [Fact]
    public async Task RunAsync_OverrideHierarchy_ModelIdTier_BeatsContextTiers()
    {
        // Override hierarchy: Model:{id} > Symbol:Timeframe > Symbol:* > *:Timeframe > *:* > defaults.
        // The Model:{id} override sets MinLogs=10 — only 10 logs are seeded — so calibration
        // succeeds for THIS model. The Symbol:Timeframe tier sets MinLogs=100, which would
        // otherwise fail; if the resolver picked the wider tier first the test would fail.
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var model = CreateModel(now.UtcDateTime.AddHours(-2));
        db.Set<MLModel>().Add(model);

        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = $"MLConformalCalibration:Override:Model:{model.Id}:MinLogs",
            Value = "10",
            DataType = ConfigDataType.Int,
            IsHotReloadable = true,
            LastUpdatedAt = now.UtcDateTime,
        });
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLConformalCalibration:Override:EURUSD:H1:MinLogs",
            Value = "100",
            DataType = ConfigDataType.Int,
            IsHotReloadable = true,
            LastUpdatedAt = now.UtcDateTime,
        });

        for (int i = 0; i < 10; i++)
        {
            AddResolvedLog(db, model, id: 300 + i, now.UtcDateTime.AddMinutes(-90 + i),
                TradeDirection.Buy, servedBuyProbability: 0.90m);
        }
        await db.SaveChangesAsync();

        // Cycle-wide MinLogs is 50 (default). Without the override the model would skip.
        var worker = CreateWorker(
            db,
            new TestTimeProvider(now),
            CreateOptions(minLogs: 50, targetCoverage: 0.60));

        var result = await worker.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.CalibrationsWritten);
        Assert.Equal(1, result.EvaluatedModelCount);
    }

    [Fact]
    public async Task RunAsync_OverrideHierarchy_StarStarFallback_Applies()
    {
        // *:* tier applies when no narrower tier exists.
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var model = CreateModel(now.UtcDateTime.AddHours(-2));
        db.Set<MLModel>().Add(model);

        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLConformalCalibration:Override:*:*:TargetCoverage",
            Value = "0.50",
            DataType = ConfigDataType.Decimal,
            IsHotReloadable = true,
            LastUpdatedAt = now.UtcDateTime,
        });

        for (int i = 0; i < 10; i++)
        {
            AddResolvedLog(db, model, id: 400 + i, now.UtcDateTime.AddMinutes(-90 + i),
                TradeDirection.Buy, servedBuyProbability: 0.90m);
        }
        await db.SaveChangesAsync();

        var worker = CreateWorker(
            db,
            new TestTimeProvider(now),
            CreateOptions(minLogs: 10, targetCoverage: 0.95));

        var result = await worker.RunAsync(CancellationToken.None);

        var calibration = Assert.Single(await db.Set<MLConformalCalibration>().ToListAsync());
        Assert.Equal(1, result.CalibrationsWritten);
        // The *:* override pinned target coverage to 0.50, not the cycle-wide 0.95.
        Assert.Equal(0.50, calibration.TargetCoverage, precision: 6);
    }

    [Fact]
    public async Task RunAsync_OverrideTokenWithUnknownKnob_RowDoesNotApply()
    {
        // Typo on the override knob — the row falls through to the default. Without the
        // typo this MinLogs=1000 would have prevented calibration; with the typo, the
        // cycle-wide MinLogs=10 is what governs and calibration succeeds.
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var model = CreateModel(now.UtcDateTime.AddHours(-2));
        db.Set<MLModel>().Add(model);

        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLConformalCalibration:Override:EURUSD:H1:MnLogs", // typo
            Value = "1000",
            DataType = ConfigDataType.Int,
            IsHotReloadable = true,
            LastUpdatedAt = now.UtcDateTime,
        });

        for (int i = 0; i < 10; i++)
        {
            AddResolvedLog(db, model, id: 500 + i, now.UtcDateTime.AddMinutes(-90 + i),
                TradeDirection.Buy, servedBuyProbability: 0.90m);
        }
        await db.SaveChangesAsync();

        var worker = CreateWorker(
            db,
            new TestTimeProvider(now),
            CreateOptions(minLogs: 10, targetCoverage: 0.60));

        var result = await worker.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.CalibrationsWritten);
    }

    [Fact]
    public async Task RunAsync_StaleCalibrationAlert_DispatchedAfterThreshold_ResolvedOnRecovery()
    {
        // Drive 3 cycles with insufficient logs (StaleSkipAlertThreshold=3) so the streak
        // counter trips and the stale-calibration alert fires. Then add enough logs so
        // the next cycle calibrates and the alert auto-resolves.
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var model = CreateModel(now.UtcDateTime.AddHours(-2));
        db.Set<MLModel>().Add(model);
        await db.SaveChangesAsync();

        var dispatcher = new TestAlertDispatcher();
        var options = CreateOptions(minLogs: 10, targetCoverage: 0.60);
        options.StaleSkipAlertThreshold = 3;
        var worker = CreateWorker(db, new TestTimeProvider(now), options, dispatcher: dispatcher);

        // Cycles 1 and 2: not enough logs → skip with insufficient_logs
        var c1 = await worker.RunAsync(CancellationToken.None);
        Assert.Equal(0, c1.StaleAlertsDispatched);

        var c2 = await worker.RunAsync(CancellationToken.None);
        Assert.Equal(0, c2.StaleAlertsDispatched);

        // Cycle 3: streak hits threshold → stale alert fires
        var c3 = await worker.RunAsync(CancellationToken.None);
        Assert.Equal(1, c3.StaleAlertsDispatched);

        var staleAlert = await db.Set<Alert>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == $"ml-conformal-calibration-stale:{model.Id}");
        Assert.NotNull(staleAlert);
        Assert.True(staleAlert!.IsActive);
        Assert.Equal(AlertType.MLMonitoringStale, staleAlert.AlertType);

        // Now add enough logs and run a recovery cycle. Cycle 4 should calibrate
        // and auto-resolve the alert.
        for (int i = 0; i < 10; i++)
        {
            AddResolvedLog(db, model, id: 600 + i, now.UtcDateTime.AddMinutes(-90 + i),
                TradeDirection.Buy, servedBuyProbability: 0.90m);
        }
        await db.SaveChangesAsync();

        var c4 = await worker.RunAsync(CancellationToken.None);
        Assert.Equal(1, c4.CalibrationsWritten);
        Assert.Equal(1, c4.StaleAlertsResolved);

        var resolved = await db.Set<Alert>()
            .IgnoreQueryFilters()
            .FirstAsync(a => a.DeduplicationKey == $"ml-conformal-calibration-stale:{model.Id}");
        Assert.False(resolved.IsActive);
        Assert.NotNull(resolved.AutoResolvedAt);
    }

    [Fact]
    public async Task RunAsync_StaleAlertEnabledFalse_DoesNotDispatchAlert()
    {
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var model = CreateModel(now.UtcDateTime.AddHours(-2));
        db.Set<MLModel>().Add(model);
        await db.SaveChangesAsync();

        var dispatcher = new TestAlertDispatcher();
        var options = CreateOptions(minLogs: 10, targetCoverage: 0.60);
        options.StaleAlertEnabled = false;
        options.StaleSkipAlertThreshold = 1;
        var worker = CreateWorker(db, new TestTimeProvider(now), options, dispatcher: dispatcher);

        var result = await worker.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.StaleAlertsDispatched);
        Assert.Equal(1, result.SkippedInsufficientLogsCount);
        var alerts = await db.Set<Alert>().IgnoreQueryFilters().ToListAsync();
        Assert.Empty(alerts);
    }

    [Fact]
    public async Task RunAsync_BoundedParallelism_DefaultDopOne_Succeeds()
    {
        // Smoke test: the new parallel path must not regress the simple DOP=1 case.
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var model = CreateModel(now.UtcDateTime.AddHours(-2));
        db.Set<MLModel>().Add(model);

        for (int i = 0; i < 10; i++)
        {
            AddResolvedLog(db, model, id: 700 + i, now.UtcDateTime.AddMinutes(-90 + i),
                TradeDirection.Buy, servedBuyProbability: 0.90m);
        }
        await db.SaveChangesAsync();

        var options = CreateOptions(minLogs: 10, targetCoverage: 0.60);
        options.MaxDegreeOfParallelism = 1;
        var worker = CreateWorker(db, new TestTimeProvider(now), options);

        var result = await worker.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.CalibrationsWritten);
        Assert.Equal(0, result.FailedModelCount);
    }

    [Fact]
    public async Task RunAsync_LongCycleWarnSeconds_ZeroDisablesWarn()
    {
        // Boundary smoke: LongCycleWarnSeconds=0 means warn disabled, settings flow through.
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var model = CreateModel(now.UtcDateTime.AddHours(-2));
        db.Set<MLModel>().Add(model);
        await db.SaveChangesAsync();

        var options = CreateOptions(minLogs: 10, targetCoverage: 0.60);
        options.LongCycleWarnSeconds = 0;
        var worker = CreateWorker(db, new TestTimeProvider(now), options);

        var result = await worker.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.Settings.LongCycleWarnSeconds);
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
        IDistributedLock? distributedLock = null,
        TestAlertDispatcher? dispatcher = null)
    {
        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => writeContext.Object);
        if (dispatcher is not null)
            services.AddSingleton<IAlertDispatcher>(dispatcher);
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

    private sealed class TestAlertDispatcher : IAlertDispatcher
    {
        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
        {
            alert.LastTriggeredAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
        {
            alert.AutoResolvedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }
    }
}
