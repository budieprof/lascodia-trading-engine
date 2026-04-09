using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using MockQueryable.Moq;
using Moq;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class WalkForwardWorkerTest
{
    [Fact]
    public async Task ProcessAsync_AutoPromotedStrategy_JumpsQueuedWalkForwardOrder()
    {
        var olderRun = new WalkForwardRun
        {
            Id = 10,
            StrategyId = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            FromDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            ToDate = new DateTime(2026, 02, 20, 0, 0, 0, DateTimeKind.Utc),
            InSampleDays = 20,
            OutOfSampleDays = 10,
            InitialBalance = 10_000m,
            ReOptimizePerFold = false,
            Status = RunStatus.Queued,
            Priority = 0,
            StartedAt = DateTime.UtcNow.AddHours(-2),
            QueuedAt = DateTime.UtcNow.AddHours(-2),
            AvailableAt = DateTime.UtcNow.AddHours(-2),
            IsDeleted = false,
        };
        var fastTrackRun = new WalkForwardRun
        {
            Id = 11,
            StrategyId = 2,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            FromDate = olderRun.FromDate,
            ToDate = olderRun.ToDate,
            InSampleDays = 20,
            OutOfSampleDays = 10,
            InitialBalance = 10_000m,
            ReOptimizePerFold = false,
            Status = RunStatus.Queued,
            Priority = 5,
            StartedAt = DateTime.UtcNow.AddHours(-1),
            QueuedAt = DateTime.UtcNow.AddHours(-1),
            AvailableAt = DateTime.UtcNow.AddHours(-1),
            IsDeleted = false,
        };

        var standardStrategy = new Strategy
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"mode":"standard"}""",
            ScreeningMetricsJson = new ScreeningMetrics
            {
                Regime = MarketRegime.Trending.ToString(),
                ObservedRegime = MarketRegime.Trending.ToString(),
                IsAutoPromoted = false,
            }.ToJson(),
            Status = StrategyStatus.Active,
            IsDeleted = false,
        };
        var autoPromotedStrategy = new Strategy
        {
            Id = 2,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"mode":"fast-track"}""",
            ScreeningMetricsJson = new ScreeningMetrics
            {
                Regime = MarketRegime.Trending.ToString(),
                ObservedRegime = MarketRegime.Trending.ToString(),
                IsAutoPromoted = true,
            }.ToJson(),
            Status = StrategyStatus.Active,
            IsDeleted = false,
        };

        var candles = Enumerable.Range(0, 60)
            .SelectMany(day => Enumerable.Range(0, 24).Select(hour => olderRun.FromDate.AddDays(day).AddHours(hour)))
            .Where(ts => ts < olderRun.ToDate)
            .Select(ts => new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = ts,
                Open = 1.10m,
                High = 1.11m,
                Low = 1.09m,
                Close = 1.10m,
                IsClosed = true,
            })
            .ToList();

        var runs = new List<WalkForwardRun> { olderRun, fastTrackRun };
        var strategies = new List<Strategy> { standardStrategy, autoPromotedStrategy };

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(runs.AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<Strategy>()).Returns(strategies.AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<Candle>()).Returns(candles.AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<EngineConfig>()).Returns(new List<EngineConfig>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<EconomicEvent>()).Returns(new List<EconomicEvent>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<CurrencyPair>()).Returns(new List<CurrencyPair>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<SpreadProfile>()).Returns(new List<SpreadProfile>().AsQueryable().BuildMockDbSet().Object);

        var readCtx = new Mock<IReadApplicationDbContext>();
        readCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var engine = new RecordingBacktestEngine();
        var services = new ServiceCollection()
            .AddSingleton(readCtx.Object)
            .AddSingleton(writeCtx.Object)
            .AddSingleton<IValidationWorkerIdentity>(new TestValidationWorkerIdentity("test-walkforward-worker"))
            .AddScoped<IValidationSettingsProvider, ValidationSettingsProvider>()
            .AddScoped<IBacktestOptionsSnapshotBuilder>(sp =>
                new BacktestOptionsSnapshotBuilder(
                    sp.GetRequiredService<IValidationSettingsProvider>(),
                    NullLogger<BacktestOptionsSnapshotBuilder>.Instance))
            .BuildServiceProvider();

        var worker = new WalkForwardWorker(
            Mock.Of<ILogger<WalkForwardWorker>>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            engine,
            new InMemoryWalkForwardRunClaimService(),
            services.GetRequiredService<IValidationSettingsProvider>(),
            services.GetRequiredService<IBacktestOptionsSnapshotBuilder>(),
            services.GetRequiredService<IValidationWorkerIdentity>(),
            Mock.Of<IWorkerHealthMonitor>());

        var method = typeof(WalkForwardWorker).GetMethod(
            "ProcessAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(worker, [CancellationToken.None])!;

        Assert.Equal(RunStatus.Queued, olderRun.Status);
        Assert.NotEqual(RunStatus.Queued, fastTrackRun.Status);
        Assert.NotEmpty(engine.SeenParameterJson);
        Assert.All(engine.SeenParameterJson, json => Assert.Equal("""{"mode":"fast-track"}""", json));
    }

    private sealed class RecordingBacktestEngine : IBacktestEngine
    {
        public List<string> SeenParameterJson { get; } = [];

        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            ct.ThrowIfCancellationRequested();
            SeenParameterJson.Add(strategy.ParametersJson);

            var trades = Enumerable.Range(0, 12)
                .Select(i => new BacktestTrade
                {
                    Direction = TradeDirection.Buy,
                    EntryPrice = 1.1000m + i * 0.0001m,
                    ExitPrice = 1.1010m + i * 0.0001m,
                    LotSize = 0.10m,
                    PnL = 60m + i,
                    GrossPnL = 60m + i,
                    EntryTime = candles[0].Timestamp.AddHours(i * 6),
                    ExitTime = candles[0].Timestamp.AddHours(i * 6 + 2),
                    ExitReason = TradeExitReason.TakeProfit,
                })
                .ToList();

            return Task.FromResult(new BacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = initialBalance + 900m,
                TotalReturn = 0.09m,
                TotalTrades = trades.Count,
                WinningTrades = trades.Count,
                LosingTrades = 0,
                WinRate = 0.72m,
                ProfitFactor = 1.90m,
                MaxDrawdownPct = 0.07m,
                SharpeRatio = 1.30m,
                AverageWin = 75m,
                AverageLoss = 0m,
                LargestWin = 100m,
                LargestLoss = 0m,
                Expectancy = 75m,
                Trades = trades,
            });
        }
    }

    private sealed class TestValidationWorkerIdentity : IValidationWorkerIdentity
    {
        public TestValidationWorkerIdentity(string instanceId)
        {
            InstanceId = instanceId;
        }

        public string InstanceId { get; }
    }
}
