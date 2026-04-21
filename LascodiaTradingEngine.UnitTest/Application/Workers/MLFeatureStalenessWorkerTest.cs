using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
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
using Xunit;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLFeatureStalenessWorkerTest
{
    [Fact]
    public void ComputeLag1Autocorr_FlagsConstantSeriesAsDegenerateStaleInput()
    {
        var result = MLFeatureStalenessWorker.ComputeLag1Autocorr([4.2, 4.2, 4.2, 4.2, 4.2]);

        Assert.True(result.IsDegenerate);
        Assert.Equal(1.0, result.Correlation);
    }

    [Fact]
    public void ScoreFeatures_AppliesStaleCap()
    {
        var rows = Enumerable.Range(0, 80)
            .Select(_ => new[] { 1.0, 2.0, 3.0, 4.0 })
            .ToList();
        var config = new MLFeatureStalenessWorker.FeatureStalenessConfig(
            PollSeconds: 60,
            MinSamples: 20,
            MaxRowsPerModel: 100,
            MaxCandlesPerModel: 100,
            MaxFeatures: 4,
            MaxModelsPerCycle: 10,
            AbsAutocorrThreshold: 0.95,
            ConstantVarianceEpsilon: 1e-9,
            MaxStaleFeatureFraction: 0.25,
            RetentionDays: 90,
            LockTimeoutSeconds: 0);

        var scores = MLFeatureStalenessWorker.ScoreFeatures(rows, 4, config).ToList();
        MLFeatureStalenessWorker.ApplyStaleCap(scores, config.MaxStaleFeatureFraction);

        Assert.Equal(4, scores.Count);
        Assert.Equal(1, scores.Count(s => s.IsStale));
    }

    [Fact]
    public async Task RunCycleAsync_PersistsRawFeatureStalenessAndSoftDeletesDuplicates()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLFeatureStaleness:MinSamples", "20", ConfigDataType.Int);
        AddConfig(db, "MLFeatureStaleness:MaxStaleFeatureFraction", "0.5", ConfigDataType.Decimal);
        var model = await AddModelAsync(db);
        db.Set<MLFeatureStalenessLog>().Add(new MLFeatureStalenessLog
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            FeatureName = "OldFeature",
            Lag1Autocorr = 1.0,
            IsStale = true,
            ComputedAt = DateTime.UtcNow.AddDays(-1)
        });
        AddRawPredictionLogs(db, model, 80);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        await worker.RunCycleAsync(CancellationToken.None);

        var activeLogs = await db.Set<MLFeatureStalenessLog>()
            .OrderBy(l => l.FeatureName)
            .ToListAsync();
        var allLogs = await db.Set<MLFeatureStalenessLog>()
            .IgnoreQueryFilters()
            .ToListAsync();

        Assert.Equal(3, activeLogs.Count);
        var constant = Assert.Single(activeLogs, l => l.FeatureName == "Constant");
        Assert.True(constant.IsStale);
        Assert.True(constant.Lag1Autocorr > 0.99);
        Assert.Contains(allLogs, l => l.FeatureName == "OldFeature" && l.IsDeleted);
    }

    [Fact]
    public void ApplyDisabledFeatureMask_ZerosOnlySelectedFeatures()
    {
        var samples = new List<TrainingSample>
        {
            new([1f, 2f, 3f], 1, 1f),
            new([4f, 5f, 6f], 0, 1f)
        };

        var masked = MLTrainingWorker.ApplyDisabledFeatureMask(samples, [1]);

        Assert.Equal([1f, 0f, 3f], masked[0].Features);
        Assert.Equal([4f, 0f, 6f], masked[1].Features);
    }

    [Fact]
    public void ApplyStaleFeatureMaskToSnapshot_PreservesExistingPruning()
    {
        var snapshot = new ModelSnapshot
        {
            ActiveFeatureMask = [true, false, true, true]
        };

        MLTrainingWorker.ApplyStaleFeatureMaskToSnapshot(snapshot, [2], 4);

        Assert.Equal([true, false, false, true], snapshot.ActiveFeatureMask);
        Assert.Equal(2, snapshot.PrunedFeatureCount);
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLFeatureStalenessWorker CreateWorker(WriteApplicationDbContext db)
    {
        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);

        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => readContext.Object);
        services.AddScoped(_ => writeContext.Object);
        var provider = services.BuildServiceProvider();

        return new MLFeatureStalenessWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLFeatureStalenessWorker>>());
    }

    private static async Task<MLModel> AddModelAsync(WriteApplicationDbContext db)
    {
        var snapshot = new ModelSnapshot
        {
            Type = "Test",
            Version = "1",
            Features = ["Constant", "NoiseA", "NoiseB"],
            ExpectedInputFeatures = 3,
            FeatureSchemaVersion = 1
        };

        var model = new MLModel
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ModelVersion = "test",
            Status = MLModelStatus.Active,
            IsActive = true,
            ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot)
        };
        db.Set<MLModel>().Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    private static void AddRawPredictionLogs(WriteApplicationDbContext db, MLModel model, int count)
    {
        for (int i = 0; i < count; i++)
        {
            db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
            {
                TradeSignalId = i + 1,
                MLModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                PredictedAt = DateTime.UtcNow.AddMinutes(-count + i),
                RawFeaturesJson = JsonSerializer.Serialize(new[]
                {
                    7.0,
                    ((i * 37) % 101) / 100.0,
                    ((i * 17) % 97) / 97.0
                })
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
}
