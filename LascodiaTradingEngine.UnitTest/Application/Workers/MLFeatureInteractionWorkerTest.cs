using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;
using Xunit;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLFeatureInteractionWorkerTest
{
    [Fact]
    public void ComputePartialF_FindsIncrementalProductSignal()
    {
        var rows = new List<MLFeatureInteractionWorker.InteractionRow>();
        for (int i = 0; i < 240; i++)
        {
            double a = i % 4 < 2 ? -1.0 : 1.0;
            double b = i % 8 < 4 ? -1.0 : 1.0;
            double label = 0.5 + 0.25 * a * b + 0.01 * ((i % 7) - 3);
            rows.Add(new MLFeatureInteractionWorker.InteractionRow([a, b, (i % 5) / 5.0], label));
        }

        var candidate = MLFeatureInteractionWorker.ComputePartialF(rows, 0, 1);

        Assert.True(candidate.Score > 100);
        Assert.True(candidate.EffectSize > 0.4);
        Assert.True(candidate.PValue < 0.001);
    }

    [Fact]
    public void AppendInteractionFeatures_AppendsReplayableProducts()
    {
        var features = new[] { 2f, -3f, 5f };
        var pairs = new[]
        {
            new FeatureInteractionPairDescriptor { A = 0, B = 1, NameA = "A", NameB = "B" },
            new FeatureInteractionPairDescriptor { A = 1, B = 2, NameA = "B", NameB = "C" }
        };

        var appended = MLFeatureHelper.AppendInteractionFeatures(features, pairs);

        Assert.Equal([2f, -3f, 5f, -5f, -5f], appended);
    }

    [Fact]
    public async Task RunCycleAsync_PersistsRawFeatureInteractionAudits()
    {
        await using var db = CreateDbContext();
        var model = await AddModelAsync(db);
        AddInteractionLogs(db, model, 140, rawMode: RawFeatureMode.Valid, shapMode: ShapFeatureMode.Zero);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        await worker.RunCycleAsync(CancellationToken.None);

        var audits = await db.Set<MLFeatureInteractionAudit>()
            .OrderBy(a => a.Rank)
            .ToListAsync();

        Assert.NotEmpty(audits);
        var top = audits[0];
        Assert.Equal(0, top.FeatureIndexA);
        Assert.Equal(1, top.FeatureIndexB);
        Assert.Equal("RawFeaturePartialF", top.Method);
        Assert.True(top.IsIncludedAsFeature);
        Assert.True(top.SampleCount >= 100);
        Assert.True(top.EffectSize > 0.4);
    }

    [Fact]
    public async Task RunCycleAsync_FallsBackToShapWhenRawRowsAreMalformed()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLFeatureInteraction:MinSamples", "50", ConfigDataType.Int);
        var model = await AddModelAsync(db);
        AddInteractionLogs(db, model, 70, rawMode: RawFeatureMode.Malformed, shapMode: ShapFeatureMode.Valid);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        await worker.RunCycleAsync(CancellationToken.None);

        var top = await db.Set<MLFeatureInteractionAudit>()
            .OrderBy(a => a.Rank)
            .FirstAsync();

        Assert.Equal("ShapContributionPartialF", top.Method);
        Assert.Equal(0, top.FeatureIndexA);
        Assert.Equal(1, top.FeatureIndexB);
        Assert.Equal(70, top.SampleCount);
        Assert.True(top.IsIncludedAsFeature);
    }

    [Fact]
    public async Task RunCycleAsync_SoftDeletesPreviousAuditsBeforeWritingReplacement()
    {
        await using var db = CreateDbContext();
        var model = await AddModelAsync(db);
        db.Set<MLFeatureInteractionAudit>().Add(new MLFeatureInteractionAudit
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            FeatureIndexA = 1,
            FeatureNameA = "OldA",
            FeatureIndexB = 2,
            FeatureNameB = "OldB",
            FeatureSchemaVersion = 1,
            BaseFeatureCount = 3,
            SampleCount = 100,
            Method = "Old",
            Rank = 1,
            IsIncludedAsFeature = true,
            ComputedAt = DateTime.UtcNow.AddDays(-1)
        });
        AddInteractionLogs(db, model, 140, rawMode: RawFeatureMode.Valid, shapMode: ShapFeatureMode.Zero);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        await worker.RunCycleAsync(CancellationToken.None);

        var allAudits = await db.Set<MLFeatureInteractionAudit>()
            .IgnoreQueryFilters()
            .OrderBy(a => a.Id)
            .ToListAsync();

        Assert.Contains(allAudits, a => a.Method == "Old" && a.IsDeleted);
        Assert.Contains(allAudits, a => a.Method == "RawFeaturePartialF" && !a.IsDeleted);
    }

    [Fact]
    public async Task RunCycleAsync_ClampsUnsafeMinSamplesConfig()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLFeatureInteraction:MinSamples", "1", ConfigDataType.Int);
        var model = await AddModelAsync(db);
        AddInteractionLogs(db, model, 45, rawMode: RawFeatureMode.Valid, shapMode: ShapFeatureMode.Zero);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLFeatureInteractionAudit>().ToListAsync());
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLFeatureInteractionWorker CreateWorker(WriteApplicationDbContext db)
    {
        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);

        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => readContext.Object);
        services.AddScoped(_ => writeContext.Object);
        var provider = services.BuildServiceProvider();

        return new MLFeatureInteractionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLFeatureInteractionWorker>>());
    }

    private static async Task<MLModel> AddModelAsync(WriteApplicationDbContext db)
    {
        var snapshot = new ModelSnapshot
        {
            Type = "Test",
            Version = "1",
            Features = ["A", "B", "Noise"],
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

    private static void AddInteractionLogs(
        WriteApplicationDbContext db,
        MLModel model,
        int count,
        RawFeatureMode rawMode,
        ShapFeatureMode shapMode)
    {
        for (int i = 0; i < count; i++)
        {
            double a = i % 4 < 2 ? -1.0 : 1.0;
            double b = i % 8 < 4 ? -1.0 : 1.0;
            string? rawJson = rawMode switch
            {
                RawFeatureMode.Valid => JsonSerializer.Serialize(new[] { a, b, (i % 7) / 7.0 }),
                RawFeatureMode.Malformed => i % 3 == 0
                    ? "[1,2,"
                    : "[\"bad\",2,3]",
                _ => null
            };
            string? shapJson = shapMode switch
            {
                ShapFeatureMode.Valid => JsonSerializer.Serialize(new[] { a, b, (i % 5) / 5.0 }),
                ShapFeatureMode.Zero => JsonSerializer.Serialize(new[] { 0.0, 0.0, 0.0 }),
                _ => null
            };

            db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
            {
                TradeSignalId = i + 1,
                MLModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                ActualDirection = (a * b > 0) ^ (i % 17 == 0) ? TradeDirection.Buy : TradeDirection.Sell,
                DirectionCorrect = true,
                PredictedAt = DateTime.UtcNow.AddMinutes(-i),
                RawFeaturesJson = rawJson,
                ShapValuesJson = shapJson
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

    private enum RawFeatureMode
    {
        None,
        Valid,
        Malformed
    }

    private enum ShapFeatureMode
    {
        None,
        Valid,
        Zero
    }
}
