using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLIsotonicRecalibrationWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_RecalibratesEligibleTcnSnapshotAndEvictsCache()
    {
        await using var db = CreateDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var model = AddModel(db, " eurusd ", Timeframe.H1, CreateSnapshot(tcn: true, ConstantBadBreakpoints()));
        AddModel(db, "GBPUSD", Timeframe.H1, CreateSnapshot(tcn: false, ConstantBadBreakpoints()), isSuppressed: true);
        await db.SaveChangesAsync();
        AddResolvedLogs(db, model.Id);
        await db.SaveChangesAsync();
        cache.Set($"MLSnapshot:{model.Id}", CreateSnapshot(tcn: true, ConstantBadBreakpoints()));

        var worker = CreateWorker(db, cache);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        var saved = await db.Set<MLModel>().SingleAsync(m => m.Id == model.Id);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(saved.ModelBytes!)!;

        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(1, result.SnapshotsUpdated);
        Assert.Equal(0, result.ModelsSkipped);
        Assert.False(cache.TryGetValue($"MLSnapshot:{model.Id}", out _));
        Assert.True(snapshot.Ece < 0.01);
        Assert.Equal(2, snapshot.IsotonicBreakpoints.Length / 2);
        Assert.Equal(snapshot.IsotonicBreakpoints, snapshot.TcnCalibrationArtifact!.IsotonicBreakpoints);
        Assert.Equal(30, snapshot.TcnCalibrationArtifact.IsotonicSampleCount);
    }

    [Fact]
    public async Task RunCycleAsync_UsesLatestCaseInsensitiveRuntimeDisableBeforePatching()
    {
        await using var db = CreateDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var now = DateTime.UtcNow;
        AddConfig(db, "MLIsotonicRecal:Enabled", "true", now.AddMinutes(-2));
        AddConfig(db, "mlisotonicrecal:enabled", "false", now.AddMinutes(-1));
        var model = AddModel(db, "EURUSD", Timeframe.H1, CreateSnapshot(tcn: false, ConstantBadBreakpoints()));
        await db.SaveChangesAsync();
        AddResolvedLogs(db, model.Id);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, cache);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(
            (await db.Set<MLModel>().SingleAsync(m => m.Id == model.Id)).ModelBytes!)!;

        Assert.Equal("disabled", result.SkippedReason);
        Assert.Equal(ConstantBadBreakpoints(), snapshot.IsotonicBreakpoints);
    }

    [Fact]
    public async Task RunCycleAsync_SkipsWhenDistributedLockIsBusy()
    {
        await using var db = CreateDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var model = AddModel(db, "EURUSD", Timeframe.H1, CreateSnapshot(tcn: false, ConstantBadBreakpoints()));
        await db.SaveChangesAsync();
        AddResolvedLogs(db, model.Id);
        await db.SaveChangesAsync();
        var distributedLock = new TestDistributedLock(acquire: false);

        var worker = CreateWorker(db, cache, distributedLock);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(
            (await db.Set<MLModel>().SingleAsync(m => m.Id == model.Id)).ModelBytes!)!;

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(1, distributedLock.Attempts);
        Assert.Equal(ConstantBadBreakpoints(), snapshot.IsotonicBreakpoints);
    }

    [Fact]
    public async Task RunCycleAsync_RequiresConfiguredMinimumEceImprovement()
    {
        await using var db = CreateDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        AddConfig(db, "MLIsotonicRecal:MinimumEceImprovement", "0.75", DateTime.UtcNow);
        var model = AddModel(db, "EURUSD", Timeframe.H1, CreateSnapshot(tcn: false, ConstantBadBreakpoints()));
        await db.SaveChangesAsync();
        AddResolvedLogs(db, model.Id);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, cache);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(
            (await db.Set<MLModel>().SingleAsync(m => m.Id == model.Id)).ModelBytes!)!;

        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(0, result.SnapshotsUpdated);
        Assert.Equal(1, result.ModelsSkipped);
        Assert.Equal(ConstantBadBreakpoints(), snapshot.IsotonicBreakpoints);
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLIsotonicRecalibrationWorker CreateWorker(
        WriteApplicationDbContext db,
        IMemoryCache cache,
        IDistributedLock? distributedLock = null)
    {
        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);

        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => readContext.Object);
        services.AddScoped(_ => writeContext.Object);
        var provider = services.BuildServiceProvider();

        return new MLIsotonicRecalibrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            cache,
            NullLogger<MLIsotonicRecalibrationWorker>.Instance,
            distributedLock,
            options: new MLIsotonicRecalibrationOptions
            {
                MinResolved = 10,
                MaxPredictionLogsPerModel = 100,
                PollIntervalSeconds = 60,
                MaxModelsPerCycle = 10,
                DbCommandTimeoutSeconds = 30
            });
    }

    private static MLModel AddModel(
        WriteApplicationDbContext db,
        string symbol,
        Timeframe timeframe,
        ModelSnapshot snapshot,
        bool isSuppressed = false)
    {
        var model = new MLModel
        {
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = Guid.NewGuid().ToString("N"),
            FilePath = "memory",
            Status = MLModelStatus.Active,
            IsActive = true,
            IsSuppressed = isSuppressed,
            TrainingSamples = 100,
            TrainedAt = DateTime.UtcNow.AddDays(-10),
            ActivatedAt = DateTime.UtcNow.AddDays(-5),
            ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot)
        };

        db.Set<MLModel>().Add(model);
        return model;
    }

    private static void AddResolvedLogs(WriteApplicationDbContext db, long modelId)
    {
        var now = DateTime.UtcNow;
        for (int i = 0; i < 15; i++)
        {
            AddResolvedLog(db, modelId, TradeDirection.Sell, TradeDirection.Sell, 0.20m, now.AddMinutes(-(i + 1)));
            AddResolvedLog(db, modelId, TradeDirection.Buy, TradeDirection.Buy, 0.80m, now.AddMinutes(-(i + 31)));
        }
    }

    private static void AddResolvedLog(
        WriteApplicationDbContext db,
        long modelId,
        TradeDirection predicted,
        TradeDirection actual,
        decimal rawProbability,
        DateTime outcomeRecordedAt)
        => db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
        {
            TradeSignalId = modelId * 100_000 + db.Set<MLModelPredictionLog>().Local.Count + 1,
            MLModelId = modelId,
            ModelRole = ModelRole.Champion,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            PredictedDirection = predicted,
            ConfidenceScore = 0.75m,
            RawProbability = rawProbability,
            DecisionThresholdUsed = 0.50m,
            ActualDirection = actual,
            DirectionCorrect = predicted == actual,
            PredictedAt = outcomeRecordedAt.AddMinutes(-5),
            OutcomeRecordedAt = outcomeRecordedAt
        });

    private static ModelSnapshot CreateSnapshot(bool tcn, double[] breakpoints)
    {
        var snapshot = new ModelSnapshot
        {
            Type = tcn ? "TCN" : "BaggedLogistic",
            Version = "test",
            Features = ["f1"],
            ExpectedInputFeatures = 1,
            PlattA = 1.0,
            PlattB = 0.0,
            TemperatureScale = 0.0,
            OptimalThreshold = 0.5,
            ConditionalCalibrationRoutingThreshold = 0.5,
            IsotonicBreakpoints = breakpoints.ToArray(),
            TrainedAtUtc = DateTime.UtcNow.AddDays(-10)
        };

        if (tcn)
        {
            snapshot.TcnCalibrationArtifact = new TcnCalibrationArtifact
            {
                GlobalPlattA = 1.0,
                GlobalPlattB = 0.0,
                TemperatureScale = 0.0,
                ConditionalRoutingThreshold = 0.5,
                IsotonicBreakpoints = breakpoints.ToArray(),
                IsotonicSampleCount = 10,
                IsotonicBreakpointCount = breakpoints.Length / 2
            };
        }

        return snapshot;
    }

    private static double[] ConstantBadBreakpoints()
        => [0.0, 0.8, 1.0, 0.8];

    private static void AddConfig(
        WriteApplicationDbContext db,
        string key,
        string value,
        DateTime lastUpdatedAt)
        => db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = ConfigDataType.String,
            IsHotReloadable = true,
            LastUpdatedAt = lastUpdatedAt
        });

    private sealed class TestDistributedLock(bool acquire) : IDistributedLock
    {
        public int Attempts { get; private set; }

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
            => TryAcquireAsync(lockKey, TimeSpan.Zero, ct);

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
        {
            Attempts++;
            return Task.FromResult<IAsyncDisposable?>(acquire ? new Handle() : null);
        }

        private sealed class Handle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
