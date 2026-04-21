using LascodiaTradingEngine.Application.Common.Interfaces;
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
using Xunit;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLKellyFractionWorkerTest
{
    private static readonly DateTimeOffset Now = new(2026, 04, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryClassifyFallbackOutcome_UsesProfitabilityBeforeDirectionCorrectness()
    {
        var log = new MLModelPredictionLog
        {
            DirectionCorrect = true,
            WasProfitable = false,
            ActualMagnitudePips = 12m
        };

        bool classified = MLKellyFractionWorker.TryClassifyFallbackOutcome(log, out var outcome);

        Assert.True(classified);
        Assert.False(outcome.IsWin);
        Assert.Equal(12.0, outcome.Magnitude);
    }

    [Fact]
    public async Task RunOnceAsync_InsufficientResolvedSamples_DoesNotLiftPreviousNegativeSuppression()
    {
        await using var db = CreateDbContext();
        var model = AddModel(db, isSuppressed: true);
        db.Set<MLKellyFractionLog>().Add(new MLKellyFractionLog
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe.ToString(),
            KellyFraction = -0.10,
            HalfKelly = -0.05,
            WinRate = 0.40,
            WinLossRatio = 1.0,
            NegativeEV = true,
            TotalResolvedSamples = 40,
            UsableSamples = 40,
            WinCount = 16,
            LossCount = 24,
            IsReliable = true,
            Status = "Computed",
            ComputedAt = Now.UtcDateTime.AddDays(-1)
        });
        AddConfig(db, "MLKelly:EURUSD:H1:1:KellyCap", "0.0000", ConfigDataType.Decimal);

        for (int i = 0; i < 5; i++)
            await AddPredictionAsync(db, model, wasProfitable: true, directionCorrect: true, magnitudePips: 10m);

        var worker = CreateWorker(db);

        await worker.RunOnceAsync();

        var refreshedModel = await db.Set<MLModel>().SingleAsync(m => m.Id == model.Id);
        var latestLog = await db.Set<MLKellyFractionLog>()
            .OrderByDescending(l => l.ComputedAt)
            .FirstAsync(l => l.MLModelId == model.Id);
        var cap = await db.Set<EngineConfig>().SingleAsync(c => c.Key == "MLKelly:EURUSD:H1:1:KellyCap");

        Assert.True(refreshedModel.IsSuppressed);
        Assert.False(latestLog.IsReliable);
        Assert.Equal("InsufficientResolvedSamples", latestLog.Status);
        Assert.Equal("0.0000", cap.Value);
    }

    [Fact]
    public async Task RunOnceAsync_EnforcesMinimumAfterUsabilityFiltering()
    {
        await using var db = CreateDbContext();
        var model = AddModel(db);

        for (int i = 0; i < 28; i++)
            await AddPredictionAsync(db, model, wasProfitable: true, directionCorrect: true, magnitudePips: null);
        await AddPredictionAsync(db, model, wasProfitable: true, directionCorrect: true, magnitudePips: 10m);
        await AddPredictionAsync(db, model, wasProfitable: false, directionCorrect: false, magnitudePips: 5m);

        var worker = CreateWorker(db);

        await worker.RunOnceAsync();

        var log = await db.Set<MLKellyFractionLog>().SingleAsync(l => l.MLModelId == model.Id);

        Assert.False(log.IsReliable);
        Assert.Equal("InsufficientUsableSamples", log.Status);
        Assert.Equal(30, log.TotalResolvedSamples);
        Assert.Equal(2, log.UsableSamples);
        Assert.Empty(db.Set<EngineConfig>().Where(c => c.Key.EndsWith(":KellyCap")));
    }

    [Fact]
    public async Task RunOnceAsync_ComputesKellyFromPerLotRealizedPnl()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLKellyFraction:MaxAbsKelly", "1.0", ConfigDataType.Decimal);
        AddNoShrinkageConfig(db);
        var model = AddModel(db);

        for (int i = 0; i < 20; i++)
        {
            var signal = await AddPredictionAsync(db, model, wasProfitable: true, directionCorrect: true, magnitudePips: 10m);
            await AddClosedPositionAsync(db, signal.Id, realizedPnl: 20m, openLots: 2m);
        }

        for (int i = 0; i < 10; i++)
        {
            var signal = await AddPredictionAsync(db, model, wasProfitable: false, directionCorrect: false, magnitudePips: 5m);
            await AddClosedPositionAsync(db, signal.Id, realizedPnl: -5m, openLots: 0.5m);
        }

        var worker = CreateWorker(db);

        await worker.RunOnceAsync();

        var log = await db.Set<MLKellyFractionLog>().SingleAsync(l => l.MLModelId == model.Id);
        var cap = await db.Set<EngineConfig>().SingleAsync(c => c.Key == "MLKelly:EURUSD:H1:1:KellyCap");

        Assert.True(log.IsReliable);
        Assert.Equal("Computed", log.Status);
        Assert.Equal(30, log.UsableSamples);
        Assert.Equal(30, log.PnlBasedSamples);
        Assert.Equal("PnlPerLot", log.NormalizationMode);
        Assert.Equal(1.0, log.WinLossRatio, precision: 6);
        Assert.Equal(1.0 / 3.0, log.KellyFraction, precision: 6);
        Assert.Equal("0.1667", cap.Value);
    }

    [Fact]
    public async Task RunOnceAsync_UsesRiskMultipleWhenStopLossAndContractSpecsExist()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLKellyFraction:MaxAbsKelly", "1.0", ConfigDataType.Decimal);
        AddNoShrinkageConfig(db);
        AddCurrencyPair(db);
        var model = AddModel(db);

        for (int i = 0; i < 20; i++)
        {
            var signal = await AddPredictionAsync(db, model, wasProfitable: true, directionCorrect: true, magnitudePips: 10m);
            await AddClosedPositionAsync(db, signal.Id, realizedPnl: 1_000m, openLots: 1m);
        }

        for (int i = 0; i < 10; i++)
        {
            var signal = await AddPredictionAsync(db, model, wasProfitable: false, directionCorrect: false, magnitudePips: 5m);
            await AddClosedPositionAsync(db, signal.Id, realizedPnl: -500m, openLots: 0.5m);
        }

        var worker = CreateWorker(db);

        await worker.RunOnceAsync();

        var log = await db.Set<MLKellyFractionLog>().SingleAsync(l => l.MLModelId == model.Id);

        Assert.Equal("RiskMultiple", log.NormalizationMode);
        Assert.Equal(1.0, log.WinLossRatio, precision: 6);
        Assert.Equal(1.0 / 3.0, log.KellyFraction, precision: 6);
    }

    [Fact]
    public async Task RunOnceAsync_NegativeConservativeKelly_SuppressesAndDeploysZeroCap()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLKellyFraction:MaxAbsKelly", "1.0", ConfigDataType.Decimal);
        AddNoShrinkageConfig(db);
        var model = AddModel(db);

        for (int i = 0; i < 10; i++)
        {
            var signal = await AddPredictionAsync(db, model, wasProfitable: true, directionCorrect: true, magnitudePips: 5m);
            await AddClosedPositionAsync(db, signal.Id, realizedPnl: 5m, openLots: 1m);
        }

        for (int i = 0; i < 20; i++)
        {
            var signal = await AddPredictionAsync(db, model, wasProfitable: false, directionCorrect: false, magnitudePips: 10m);
            await AddClosedPositionAsync(db, signal.Id, realizedPnl: -10m, openLots: 1m);
        }

        var worker = CreateWorker(db);

        await worker.RunOnceAsync();

        var refreshedModel = await db.Set<MLModel>().SingleAsync(m => m.Id == model.Id);
        var log = await db.Set<MLKellyFractionLog>().SingleAsync(l => l.MLModelId == model.Id);
        var cap = await db.Set<EngineConfig>().SingleAsync(c => c.Key == "MLKelly:EURUSD:H1:1:KellyCap");

        Assert.True(refreshedModel.IsSuppressed);
        Assert.True(log.NegativeEV);
        Assert.True(log.KellyFraction < 0.0);
        Assert.Equal("0.0000", cap.Value);
    }

    [Fact]
    public async Task RunOnceAsync_IgnoresChallengerLogsForServedChampionSizing()
    {
        await using var db = CreateDbContext();
        var model = AddModel(db);

        for (int i = 0; i < 30; i++)
        {
            await AddPredictionAsync(
                db,
                model,
                wasProfitable: false,
                directionCorrect: false,
                magnitudePips: 10m,
                modelRole: ModelRole.Challenger);
        }

        var worker = CreateWorker(db);

        await worker.RunOnceAsync();

        var refreshedModel = await db.Set<MLModel>().SingleAsync(m => m.Id == model.Id);
        var log = await db.Set<MLKellyFractionLog>().SingleAsync(l => l.MLModelId == model.Id);

        Assert.False(refreshedModel.IsSuppressed);
        Assert.False(log.IsReliable);
        Assert.Equal("InsufficientResolvedSamples", log.Status);
        Assert.Equal(0, log.TotalResolvedSamples);
    }

    [Fact]
    public void ConservativeKellyHelpers_ShrinkAndLowerBoundNoisyEdges()
    {
        var config = new MLKellyFractionWorker.KellyRuntimeConfig(
            WindowDays: 60,
            MinUsableSamples: 30,
            MinWins: 5,
            MinLosses: 5,
            MaxAbsKelly: 0.25,
            PriorTrades: 20.0,
            WinRateLowerBoundZ: 1.0,
            OutlierPercentile: 0.95,
            MaxOutcomeMagnitude: 10.0);

        double lowerBound = MLKellyFractionWorker.ComputeConservativeWinRate(20, 30, config);
        double shrinkage = MLKellyFractionWorker.ComputeShrinkage(30, config);
        var capped = MLKellyFractionWorker.ApplyOutcomeCap([1.0, 2.0, 100.0], [1.0, 2.0], config);

        Assert.True(lowerBound < 20.0 / 30.0);
        Assert.Equal(0.6, shrinkage, precision: 6);
        Assert.Equal(10.0, capped.Cap);
        Assert.Equal(10.0, capped.Wins.Max());
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLKellyFractionWorker CreateWorker(WriteApplicationDbContext db)
    {
        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);

        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => readContext.Object);
        services.AddScoped(_ => writeContext.Object);
        var provider = services.BuildServiceProvider();

        return new MLKellyFractionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLKellyFractionWorker>>(),
            new TestTimeProvider(Now));
    }

    private static MLModel AddModel(WriteApplicationDbContext db, bool isSuppressed = false)
    {
        var model = new MLModel
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ModelVersion = "test",
            Status = MLModelStatus.Active,
            IsActive = true,
            IsSuppressed = isSuppressed,
            ModelBytes = [1, 2, 3]
        };
        db.Set<MLModel>().Add(model);
        db.SaveChanges();
        return model;
    }

    private static async Task<TradeSignal> AddPredictionAsync(
        WriteApplicationDbContext db,
        MLModel model,
        bool wasProfitable,
        bool directionCorrect,
        decimal? magnitudePips,
        ModelRole modelRole = ModelRole.Champion)
    {
        var signal = new TradeSignal
        {
            StrategyId = 1,
            Symbol = model.Symbol,
            Direction = TradeDirection.Buy,
            EntryPrice = 1.1000m,
            StopLoss = 1.0900m,
            SuggestedLotSize = 0.1m,
            Confidence = 0.75m,
            MLModelId = model.Id,
            Status = TradeSignalStatus.Approved,
            GeneratedAt = Now.UtcDateTime.AddHours(-2),
            ExpiresAt = Now.UtcDateTime.AddHours(1)
        };
        db.Set<TradeSignal>().Add(signal);
        await db.SaveChangesAsync();

        db.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
        {
            TradeSignalId = signal.Id,
            MLModelId = model.Id,
            ModelRole = modelRole,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            PredictedDirection = TradeDirection.Buy,
            PredictedMagnitudePips = 10m,
            ConfidenceScore = 0.75m,
            ActualMagnitudePips = magnitudePips,
            WasProfitable = wasProfitable,
            DirectionCorrect = directionCorrect,
            PredictedAt = Now.UtcDateTime.AddHours(-2),
            OutcomeRecordedAt = Now.UtcDateTime.AddHours(-1)
        });
        await db.SaveChangesAsync();
        return signal;
    }

    private static async Task AddClosedPositionAsync(
        WriteApplicationDbContext db,
        long tradeSignalId,
        decimal realizedPnl,
        decimal openLots)
    {
        var order = new Order
        {
            TradeSignalId = tradeSignalId,
            Symbol = "EURUSD",
            TradingAccountId = 1,
            StrategyId = 1,
            OrderType = realizedPnl >= 0m ? OrderType.Buy : OrderType.Sell,
            ExecutionType = ExecutionType.Market,
            Quantity = openLots,
            Price = 1.1000m,
            Status = OrderStatus.Filled
        };
        db.Set<Order>().Add(order);
        await db.SaveChangesAsync();

        db.Set<Position>().Add(new Position
        {
            Symbol = "EURUSD",
            Direction = realizedPnl >= 0m ? PositionDirection.Long : PositionDirection.Short,
            OpenLots = openLots,
            AverageEntryPrice = 1.1000m,
            Status = PositionStatus.Closed,
            RealizedPnL = realizedPnl,
            OpenOrderId = order.Id,
            ClosedAt = Now.UtcDateTime.AddMinutes(-30)
        });
        await db.SaveChangesAsync();
    }

    private static void AddConfig(
        WriteApplicationDbContext db,
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
            LastUpdatedAt = Now.UtcDateTime
        });
        db.SaveChanges();
    }

    private static void AddCurrencyPair(WriteApplicationDbContext db)
    {
        db.Set<CurrencyPair>().Add(new CurrencyPair
        {
            Symbol = "EURUSD",
            BaseCurrency = "EUR",
            QuoteCurrency = "USD",
            DecimalPlaces = 5,
            ContractSize = 100_000m,
            PipSize = 0.0001m,
            MinLotSize = 0.01m,
            MaxLotSize = 100m,
            LotStep = 0.01m,
            IsActive = true
        });
        db.SaveChanges();
    }

    private static void AddNoShrinkageConfig(WriteApplicationDbContext db)
    {
        AddConfig(db, "MLKellyFraction:PriorTrades", "0", ConfigDataType.Decimal);
        AddConfig(db, "MLKellyFraction:WinRateLowerBoundZ", "0", ConfigDataType.Decimal);
    }
}
