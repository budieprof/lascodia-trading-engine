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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLFeaturePsiWorkerTest
{
    [Fact]
    public async Task RunPsiAsync_UpsertsAlertAndQueuesRetrain_WhenMajorityOfFeaturesDrift()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLFeaturePsi:MinFeatureSamples", "20", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:RetrainCooldownSeconds", "0", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:AlertCooldownSeconds", "900", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:AlertDestination", "ml-desk", ConfigDataType.String);
        AddCandles(db, "EURUSD", Timeframe.H1, 90);
        var model = AddModel(db, "EURUSD", Timeframe.H1, BuildShiftedBreakpoints(3));
        await db.SaveChangesAsync();

        var result = await CreateWorker(db).RunPsiAsync(db, db, CancellationToken.None);

        Assert.Equal(1, result.ModelsDiscovered);
        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(0, result.ModelsSkipped);
        Assert.True(result.HighPsiFeatureCount >= 2);
        Assert.Equal(1, result.AlertsUpserted);
        Assert.Equal(1, result.RetrainsQueued);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.Equal(AlertType.MLModelDegraded, alert.AlertType);
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.Equal("EURUSD", alert.Symbol);
        Assert.True(alert.IsActive);
        Assert.Equal(900, alert.CooldownSeconds);
        Assert.Equal($"MLFeaturePsi:{model.Id}", alert.DeduplicationKey);
        Assert.Contains("FeaturePSI", alert.ConditionJson, StringComparison.Ordinal);
        Assert.Contains("ml-desk", alert.ConditionJson, StringComparison.Ordinal);

        var run = await db.Set<MLTrainingRun>().SingleAsync();
        Assert.Equal("EURUSD", run.Symbol);
        Assert.Equal(Timeframe.H1, run.Timeframe);
        Assert.Equal(TriggerType.AutoDegrading, run.TriggerType);
        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal("FeaturePSI", run.DriftTriggerType);
        Assert.Equal(1, run.Priority);
        Assert.Contains("FeaturePSI", run.DriftMetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPsiAsync_ResolvesExistingAlert_WhenFeatureDistributionIsHealthy()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLFeaturePsi:MinFeatureSamples", "20", ConfigDataType.Int);
        AddCandles(db, "EURUSD", Timeframe.H1, 90);
        await db.SaveChangesAsync();
        var breakpoints = BuildBreakpointsFromCurrentDistribution(db, "EURUSD", Timeframe.H1, featureCount: 1);
        var model = AddModel(db, "EURUSD", Timeframe.H1, breakpoints);
        db.Set<Alert>().Add(new Alert
        {
            AlertType = AlertType.MLModelDegraded,
            Symbol = "EURUSD",
            DeduplicationKey = $"MLFeaturePsi:{model.Id}",
            ConditionJson = """{"detectorType":"FeaturePSI"}""",
            Severity = AlertSeverity.High,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var result = await CreateWorker(db).RunPsiAsync(db, db, CancellationToken.None);

        Assert.Equal(1, result.ModelsEvaluated);
        Assert.Equal(0, result.AlertsUpserted);
        Assert.Equal(1, result.AlertsResolved);
        Assert.Equal(0, result.RetrainsQueued);

        var alert = await db.Set<Alert>().SingleAsync();
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunPsiAsync_SkipsAllWork_WhenDisabledByConfig()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLFeaturePsi:Enabled", "off", ConfigDataType.Bool);
        AddCandles(db, "EURUSD", Timeframe.H1, 90);
        AddModel(db, "EURUSD", Timeframe.H1, BuildShiftedBreakpoints(3));
        await db.SaveChangesAsync();

        var result = await CreateWorker(db).RunPsiAsync(db, db, CancellationToken.None);

        Assert.Equal("disabled", result.SkippedReason);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
        Assert.Empty(await db.Set<MLTrainingRun>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_ReturnsLockBusy_WhenDistributedLockIsHeldElsewhere()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLFeaturePsi:MinFeatureSamples", "20", ConfigDataType.Int);
        AddCandles(db, "EURUSD", Timeframe.H1, 90);
        AddModel(db, "EURUSD", Timeframe.H1, BuildShiftedBreakpoints(3));
        await db.SaveChangesAsync();

        var result = await CreateWorker(db, new BusyDistributedLock()).RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await db.Set<Alert>().ToListAsync());
        Assert.Empty(await db.Set<MLTrainingRun>().ToListAsync());
    }

    [Fact]
    public async Task RunPsiAsync_DoesNotResolveStaleAlerts_WhenModelSetIsTruncated()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLFeaturePsi:MinFeatureSamples", "20", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:MaxModelsPerCycle", "1", ConfigDataType.Int);
        AddCandles(db, "AUDUSD", Timeframe.H1, 90);
        AddCandles(db, "EURUSD", Timeframe.H1, 90);
        await db.SaveChangesAsync();
        AddModel(db, "AUDUSD", Timeframe.H1, BuildBreakpointsFromCurrentDistribution(db, "AUDUSD", Timeframe.H1, featureCount: 1));
        var secondModel = AddModel(db, "EURUSD", Timeframe.H1, BuildBreakpointsFromCurrentDistribution(db, "EURUSD", Timeframe.H1, featureCount: 1));
        db.Set<Alert>().Add(new Alert
        {
            AlertType = AlertType.MLModelDegraded,
            Symbol = "EURUSD",
            DeduplicationKey = $"MLFeaturePsi:{secondModel.Id}",
            ConditionJson = """{"detectorType":"FeaturePSI"}""",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var result = await CreateWorker(db).RunPsiAsync(db, db, CancellationToken.None);

        Assert.Equal(1, result.ModelsDiscovered);
        Assert.Equal(1, result.ModelsEvaluated);
        var alert = await db.Set<Alert>().SingleAsync();
        Assert.True(alert.IsActive);
        Assert.Null(alert.AutoResolvedAt);
    }

    [Fact]
    public async Task LoadConfigAsync_NormalizesUnsafeEngineConfigValues()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLFeaturePsi:Enabled", "no", ConfigDataType.Bool);
        AddConfig(db, "MLFeaturePsi:InitialDelaySeconds", "-1", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:PollIntervalSeconds", "1", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:CandleWindowDays", "0", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:MinFeatureSamples", "1", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:PsiAlertThreshold", "NaN", ConfigDataType.Decimal);
        AddConfig(db, "MLFeaturePsi:PsiRetrainThreshold", "0.001", ConfigDataType.Decimal);
        AddConfig(db, "MLFeaturePsi:RetrainMajorityFraction", "2", ConfigDataType.Decimal);
        AddConfig(db, "MLFeaturePsi:MaxModelsPerCycle", "0", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:MaxFeaturesInAlert", "0", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:TrainingWindowDays", "0", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:RetrainCooldownSeconds", "-1", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:LockTimeoutSeconds", "-1", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:DbCommandTimeoutSeconds", "9999", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:AlertCooldownSeconds", "0", ConfigDataType.Int);
        AddConfig(db, "MLFeaturePsi:AlertDestination", "  ", ConfigDataType.String);
        await db.SaveChangesAsync();

        var config = await MLFeaturePsiWorker.LoadConfigAsync(
            db,
            new MLFeaturePsiOptions(),
            CancellationToken.None);

        Assert.False(config.Enabled);
        Assert.Equal(TimeSpan.Zero, config.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(7_200), config.PollInterval);
        Assert.Equal(14, config.CandleWindowDays);
        Assert.Equal(50, config.MinFeatureSamples);
        Assert.Equal(0.25, config.PsiAlertThreshold);
        Assert.Equal(0.40, config.PsiRetrainThreshold);
        Assert.Equal(0.50, config.RetrainMajorityFraction);
        Assert.Equal(256, config.MaxModelsPerCycle);
        Assert.Equal(5, config.MaxFeaturesInAlert);
        Assert.Equal(365, config.TrainingWindowDays);
        Assert.Equal(TimeSpan.FromSeconds(86_400), config.RetrainCooldown);
        Assert.Equal(0, config.LockTimeoutSeconds);
        Assert.Equal(30, config.DbCommandTimeoutSeconds);
        Assert.Equal(3_600, config.AlertCooldownSeconds);
        Assert.Equal("ml-ops", config.AlertDestination);
    }

    [Fact]
    public void GenerateUniformFromEdges_ReturnsDeterministicFiniteDistribution()
    {
        var values = MLFeaturePsiWorker.GenerateUniformFromEdges([double.NaN, 3, 1, 2, 2], 23);

        Assert.Equal(23, values.Length);
        Assert.All(values, value => Assert.True(double.IsFinite(value)));
        Assert.Equal(values, MLFeaturePsiWorker.GenerateUniformFromEdges([1, 2, 3], 23));
    }

    [Fact]
    public async Task LoadConfigAsync_ClampsRetrainThresholdToAlertThreshold()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLFeaturePsi:PsiAlertThreshold", "0.6", ConfigDataType.Decimal);
        AddConfig(db, "MLFeaturePsi:PsiRetrainThreshold", "0.4", ConfigDataType.Decimal);
        await db.SaveChangesAsync();

        var config = await MLFeaturePsiWorker.LoadConfigAsync(
            db,
            new MLFeaturePsiOptions(),
            CancellationToken.None);

        Assert.Equal(0.6, config.PsiAlertThreshold);
        Assert.Equal(0.6, config.PsiRetrainThreshold);
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLFeaturePsiWorker CreateWorker(
        WriteApplicationDbContext db,
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

        return new MLFeaturePsiWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLFeaturePsiWorker>>(),
            distributedLock);
    }

    private static MLModel AddModel(
        WriteApplicationDbContext db,
        string symbol,
        Timeframe timeframe,
        double[][] breakpoints)
    {
        var featureCount = breakpoints.Length;
        var snapshot = new ModelSnapshot
        {
            Type = "Test",
            Version = "1",
            Features = Enumerable.Range(0, featureCount).Select(i => $"F{i}").ToArray(),
            ExpectedInputFeatures = featureCount,
            FeatureSchemaVersion = 1,
            Means = new float[featureCount],
            Stds = Enumerable.Repeat(1f, featureCount).ToArray(),
            FeatureQuantileBreakpoints = breakpoints,
            ActiveFeatureMask = Enumerable.Repeat(true, featureCount).ToArray()
        };

        var model = new MLModel
        {
            Symbol = symbol,
            Timeframe = timeframe,
            ModelVersion = "test",
            FilePath = "memory",
            Status = MLModelStatus.Active,
            IsActive = true,
            ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot),
            TrainedAt = DateTime.UtcNow.AddDays(-7),
            ActivatedAt = DateTime.UtcNow.AddDays(-6)
        };

        db.Set<MLModel>().Add(model);
        return model;
    }

    private static double[][] BuildShiftedBreakpoints(int featureCount)
        => Enumerable.Range(0, featureCount)
            .Select(_ => new[] { -100d, -90d, -80d, -70d, -60d, -50d, -40d, -30d, -20d })
            .ToArray();

    private static double[][] BuildBreakpointsFromCurrentDistribution(
        WriteApplicationDbContext db,
        string symbol,
        Timeframe timeframe,
        int featureCount)
    {
        var candles = db.Set<Candle>()
            .Where(c => c.Symbol == symbol && c.Timeframe == timeframe)
            .OrderBy(c => c.Timestamp)
            .ToList();
        var features = MLFeatureHelper.BuildTrainingSamples(candles)
            .Select(sample => sample.Features.Take(featureCount).ToArray())
            .ToList();

        return MLFeatureHelper.ComputeFeatureQuantileBreakpoints(features);
    }

    private static void AddCandles(
        WriteApplicationDbContext db,
        string symbol,
        Timeframe timeframe,
        int count)
    {
        var start = DateTime.UtcNow.AddHours(-count - 2);
        for (var i = 0; i < count; i++)
        {
            var basePrice = 1.08m + (decimal)i * 0.00015m;
            var oscillation = (decimal)Math.Sin(i / 3.0) * 0.00025m;
            var close = basePrice + oscillation;
            var open = close - 0.00005m + (i % 2 == 0 ? 0.00003m : -0.00003m);
            var high = Math.Max(open, close) + 0.0002m;
            var low = Math.Min(open, close) - 0.0002m;

            db.Set<Candle>().Add(new Candle
            {
                Symbol = symbol,
                Timeframe = timeframe,
                Timestamp = start.AddHours(i),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = 100 + i,
                IsClosed = true
            });
        }
    }

    private static void AddConfig(
        WriteApplicationDbContext db,
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

    private sealed class BusyDistributedLock : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);
    }
}
