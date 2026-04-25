using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
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

public class MLErgodicityWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_Writes_Ergodicity_Log_For_Model_With_Enough_Resolved_Outcomes()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD");
        db.Set<MLModel>().Add(model);
        AddPredictionLogs(db, model, now, correctCount: 16, incorrectCount: 4, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, options: new MLErgodicityOptions
        {
            MinSamples = 20,
            MaxLogsPerModel = 20,
            WindowDays = 30
        });

        await worker.RunCycleAsync(CancellationToken.None);

        var log = await db.Set<MLErgodicityLog>().SingleAsync();
        Assert.Equal(model.Id, log.MLModelId);
        Assert.Equal("EURUSD", log.Symbol);
        Assert.True(log.EnsembleGrowthRate > 0m);
        Assert.True(log.TimeAverageGrowthRate > 0m);
        Assert.True(log.ErgodicityGap > 0m);
        Assert.InRange(log.NaiveKellyFraction, -2m, 2m);
        Assert.InRange(log.ErgodicityAdjustedKelly, -2m, 2m);
        Assert.True(log.GrowthRateVariance >= 0m);
    }

    [Fact]
    public async Task RunCycleAsync_Skips_Model_Below_MinSamples()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD");
        db.Set<MLModel>().Add(model);
        AddPredictionLogs(db, model, now, correctCount: 5, incorrectCount: 4, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, options: new MLErgodicityOptions
        {
            MinSamples = 20,
            MaxLogsPerModel = 20
        });

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLErgodicityLog>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Uses_Config_And_Chunks_Model_Log_Loading()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        db.Set<EngineConfig>().AddRange(
            new EngineConfig
            {
                Key = "MLErgodicity:PollIntervalHours",
                Value = "6",
                DataType = ConfigDataType.Int,
                IsHotReloadable = true
            },
            new EngineConfig
            {
                Key = "MLErgodicity:ModelBatchSize",
                Value = "1",
                DataType = ConfigDataType.Int,
                IsHotReloadable = true
            });

        var first = CreateModel(1, "EURUSD");
        var second = CreateModel(2, "GBPUSD");
        db.Set<MLModel>().AddRange(first, second);
        AddPredictionLogs(db, first, now, correctCount: 20, incorrectCount: 0, startTradeSignalId: 1);
        AddPredictionLogs(db, second, now, correctCount: 0, incorrectCount: 20, startTradeSignalId: 100);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, options: new MLErgodicityOptions
        {
            MinSamples = 20,
            MaxLogsPerModel = 20,
            ModelBatchSize = 250
        });

        int pollHours = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(6, pollHours);
        var logs = await db.Set<MLErgodicityLog>().OrderBy(l => l.MLModelId).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Equal(first.Id, logs[0].MLModelId);
        Assert.Equal(second.Id, logs[1].MLModelId);
    }

    [Fact]
    public async Task RunCycleAsync_Skips_When_Distributed_Lock_Is_Busy()
    {
        await using var db = CreateDbContext();
        var distributedLock = new Mock<IDistributedLock>();
        distributedLock
            .Setup(l => l.TryAcquireAsync(
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var worker = CreateWorker(
            db,
            distributedLock: distributedLock.Object,
            options: new MLErgodicityOptions { PollIntervalHours = 3 });

        int pollHours = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(3, pollHours);
        Assert.Empty(await db.Set<MLErgodicityLog>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleDetailedAsync_Skips_When_Disabled_By_Runtime_Config()
    {
        await using var db = CreateDbContext();
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = "MLErgodicity:Enabled",
            Value = "false",
            DataType = ConfigDataType.Bool,
            IsHotReloadable = true
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        var result = await worker.RunCycleDetailedAsync(CancellationToken.None);

        Assert.Equal("disabled", result.SkippedReason);
        Assert.Empty(await db.Set<MLErgodicityLog>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_Updates_Recent_Log_Instead_Of_Duplicating()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD");
        db.Set<MLModel>().Add(model);
        AddPredictionLogs(db, model, now, correctCount: 16, incorrectCount: 4, startTradeSignalId: 1);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, options: new MLErgodicityOptions
        {
            MinSamples = 20,
            MaxLogsPerModel = 20,
            PollIntervalHours = 24
        });

        await worker.RunCycleAsync(CancellationToken.None);
        var firstLog = await db.Set<MLErgodicityLog>().SingleAsync();

        await worker.RunCycleAsync(CancellationToken.None);

        var logs = await db.Set<MLErgodicityLog>().ToListAsync();
        Assert.Single(logs);
        Assert.Equal(firstLog.Id, logs[0].Id);
    }

    [Fact]
    public async Task RunCycleAsync_Treats_Unprofitable_Positive_Magnitude_As_Negative_Return()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var model = CreateModel(1, "EURUSD");
        db.Set<MLModel>().Add(model);
        AddPredictionLogs(
            db,
            model,
            now,
            correctCount: 0,
            incorrectCount: 20,
            startTradeSignalId: 1,
            incorrectMagnitudePips: 12m);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db, options: new MLErgodicityOptions
        {
            MinSamples = 20,
            MaxLogsPerModel = 20,
            ReturnPipScale = 100
        });

        await worker.RunCycleAsync(CancellationToken.None);

        var log = await db.Set<MLErgodicityLog>().SingleAsync();
        Assert.True(log.EnsembleGrowthRate < 0m);
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLErgodicityWorker CreateWorker(
        WriteApplicationDbContext db,
        IDistributedLock? distributedLock = null,
        MLErgodicityOptions? options = null)
    {
        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);

        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => readContext.Object);
        services.AddScoped(_ => writeContext.Object);
        var provider = services.BuildServiceProvider();

        return new MLErgodicityWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLErgodicityWorker>>(),
            distributedLock,
            options: options);
    }

    private static MLModel CreateModel(long id, string symbol) => new()
    {
        Id = id,
        Symbol = symbol,
        Timeframe = Timeframe.H1,
        ModelVersion = "1.0.0",
        FilePath = $"/tmp/{symbol}.json",
        Status = MLModelStatus.Active,
        IsActive = true,
        TrainingSamples = 100,
        TrainedAt = DateTime.UtcNow.AddDays(-20),
        ActivatedAt = DateTime.UtcNow.AddDays(-10)
    };

    private static void AddPredictionLogs(
        WriteApplicationDbContext db,
        MLModel model,
        DateTime now,
        int correctCount,
        int incorrectCount,
        long startTradeSignalId,
        decimal incorrectMagnitudePips = -10m)
    {
        int total = correctCount + incorrectCount;
        for (int i = 0; i < total; i++)
        {
            bool correct = i < correctCount;
            db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
            {
                TradeSignalId = startTradeSignalId + i,
                MLModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                PredictedDirection = TradeDirection.Buy,
                PredictedAt = now.AddMinutes(-total + i),
                OutcomeRecordedAt = now.AddMinutes(-total + i),
                DirectionCorrect = correct,
                ActualDirection = correct ? TradeDirection.Buy : TradeDirection.Sell,
                ActualMagnitudePips = correct ? 12m : incorrectMagnitudePips,
                WasProfitable = correct,
                ServedCalibratedProbability = 0.75m,
                ConfidenceScore = 0.75m
            });
        }
    }
}
