using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using MockQueryable.Moq;
using Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class BacktestWorkerTest
{
    [Fact]
    public async Task ProcessNextQueuedRunAsync_PropagatesPinnedParameters_ToAutoQueuedWalkForward()
    {
        var run = BuildQueuedRun(
            id: 1,
            fromDate: new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            toDate: new DateTime(2026, 02, 01, 0, 0, 0, DateTimeKind.Utc),
            parametersSnapshotJson: """{"mode":"approved"}""");
        var strategy = BuildStrategy(run.StrategyId, """{"mode":"live"}""");
        var candles = BuildHourlyCandles(run.Symbol, run.Timeframe, run.FromDate, run.ToDate);
        var walks = new List<WalkForwardRun>();

        var db = BuildDb(
            backtests: [run],
            strategies: [strategy],
            candles: candles,
            walks: walks);
        var engine = new RecordingBacktestEngine();
        var (worker, _, _) = CreateWorker(db, engine);

        await InvokeProcessNextQueuedRunAsync(worker, CancellationToken.None);

        Assert.Single(engine.SeenParameterJson);
        Assert.Equal("""{"mode":"approved"}""", engine.SeenParameterJson[0]);

        var walkForward = Assert.Single(walks);
        Assert.Equal("""{"mode":"approved"}""", walkForward.ParametersSnapshotJson);
        Assert.True(walkForward.InSampleDays > 0);
        Assert.True(walkForward.OutOfSampleDays > 0);
    }

    [Fact]
    public async Task ProcessNextQueuedRunAsync_SkipsAutoQueuedWalkForward_WhenWindowIsTooShort()
    {
        var run = BuildQueuedRun(
            id: 2,
            fromDate: new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            toDate: new DateTime(2026, 01, 02, 12, 0, 0, DateTimeKind.Utc));
        var strategy = BuildStrategy(run.StrategyId, """{"mode":"live"}""");
        var candles = BuildHourlyCandles(run.Symbol, run.Timeframe, run.FromDate, run.ToDate);
        var walks = new List<WalkForwardRun>();

        var db = BuildDb(
            backtests: [run],
            strategies: [strategy],
            candles: candles,
            walks: walks);
        var engine = new RecordingBacktestEngine();
        var (worker, _, _) = CreateWorker(db, engine);

        await InvokeProcessNextQueuedRunAsync(worker, CancellationToken.None);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Empty(walks);
    }

    [Fact]
    public async Task ProcessNextQueuedRunAsync_RequeuesRun_WhenShutdownCancelsExecution()
    {
        var run = BuildQueuedRun(
            id: 3,
            fromDate: new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            toDate: new DateTime(2026, 01, 08, 0, 0, 0, DateTimeKind.Utc));
        var strategy = BuildStrategy(run.StrategyId, """{"mode":"live"}""");
        var candles = BuildHourlyCandles(run.Symbol, run.Timeframe, run.FromDate, run.ToDate);

        var db = BuildDb(
            backtests: [run],
            strategies: [strategy],
            candles: candles);
        var cts = new CancellationTokenSource();
        var engine = new CancelingBacktestEngine(cts);
        var (worker, _, _) = CreateWorker(db, engine);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeProcessNextQueuedRunAsync(worker, cts.Token));

        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.ErrorMessage);
        Assert.Null(run.ResultJson);
    }

    [Fact]
    public async Task ScheduleBacktestsForStaleStrategiesAsync_QueuesRetry_WhenLastRunFailed()
    {
        var strategy = BuildStrategy(7, """{"mode":"live"}""");
        var failedRun = new BacktestRun
        {
            Id = 70,
            StrategyId = strategy.Id,
            Symbol = strategy.Symbol,
            Timeframe = strategy.Timeframe,
            FromDate = DateTime.UtcNow.AddDays(-10),
            ToDate = DateTime.UtcNow.AddDays(-3),
            InitialBalance = 10_000m,
            Status = RunStatus.Failed,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            CompletedAt = DateTime.UtcNow.AddHours(-1),
            IsDeleted = false,
        };
        var candles = BuildHourlyCandles(
            strategy.Symbol,
            strategy.Timeframe,
            DateTime.UtcNow.AddDays(-5),
            DateTime.UtcNow);

        var db = BuildDb(
            backtests: [failedRun],
            strategies: [strategy],
            candles: candles);
        var engine = new RecordingBacktestEngine();
        var (worker, _, _) = CreateWorker(db, engine);

        await InvokeScheduleBacktestsForStaleStrategiesAsync(worker, db.Object, CancellationToken.None);

        var runs = GetList<BacktestRun>(db, x => x.Set<BacktestRun>());

        Assert.Equal(2, runs.Count);
        Assert.Single(runs, run => run.Status == RunStatus.Queued);
    }

    [Fact]
    public async Task ProcessNextQueuedRunAsync_BuildsCostAwareOptions_FromConfigAndPairMetadata()
    {
        var run = BuildQueuedRun(
            id: 4,
            fromDate: new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            toDate: new DateTime(2026, 01, 06, 0, 0, 0, DateTimeKind.Utc));
        var strategy = BuildStrategy(run.StrategyId, """{"mode":"live"}""");
        var candles = BuildHourlyCandles(run.Symbol, run.Timeframe, run.FromDate, run.ToDate);
        var configs = new List<EngineConfig>
        {
            new() { Key = "Backtest:SpreadPoints", Value = "10", DataType = ConfigDataType.Decimal },
            new() { Key = "Backtest:CommissionPerLot", Value = "5", DataType = ConfigDataType.Decimal },
            new() { Key = "Backtest:SlippagePips", Value = "2", DataType = ConfigDataType.Decimal }
        };
        var pairs = new List<CurrencyPair>
        {
            new()
            {
                Symbol = "EURUSD",
                DecimalPlaces = 5,
                ContractSize = 100_000m,
                SpreadPoints = 30,
                IsDeleted = false
            }
        };

        var db = BuildDb(
            backtests: [run],
            strategies: [strategy],
            candles: candles,
            configs: configs,
            pairs: pairs);
        var engine = new RecordingBacktestEngine();
        var (worker, _, _) = CreateWorker(db, engine);

        await InvokeProcessNextQueuedRunAsync(worker, CancellationToken.None);

        var options = Assert.Single(engine.SeenOptions);
        Assert.NotNull(options);
        Assert.Equal(0.00045m, options!.SpreadPriceUnits);
        Assert.Equal(0.00020m, options.SlippagePriceUnits);
        Assert.Equal(5m, options.CommissionPerLot);
        Assert.Equal(100_000m, options.ContractSize);
    }

    [Fact]
    public async Task RecoverStaleRunsAsync_LeavesQueuedRunsUntouched_AndRequeuesLegacyRunningRuns()
    {
        var defaultStartedAtRun = BuildQueuedRun(
            id: 5,
            fromDate: new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            toDate: new DateTime(2026, 01, 06, 0, 0, 0, DateTimeKind.Utc));
        defaultStartedAtRun.CreatedAt = default;

        var staleRun = BuildQueuedRun(
            id: 6,
            fromDate: new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            toDate: new DateTime(2026, 01, 06, 0, 0, 0, DateTimeKind.Utc));
        staleRun.Status = RunStatus.Running;
        staleRun.CreatedAt = DateTime.UtcNow.AddHours(-5);

        var strategy = BuildStrategy(staleRun.StrategyId, """{"mode":"live"}""");
        var db = BuildDb(backtests: [defaultStartedAtRun, staleRun], strategies: [strategy]);
        var engine = new RecordingBacktestEngine();
        var (worker, writeCtx, _) = CreateWorker(db, engine);

        await InvokeRecoverStaleRunsAsync(worker, db.Object, writeCtx.Object, CancellationToken.None);

        Assert.Equal(RunStatus.Queued, defaultStartedAtRun.Status);
        Assert.Equal(RunStatus.Queued, staleRun.Status);
        Assert.Null(staleRun.ExecutionLeaseExpiresAt);
    }

    private static BacktestRun BuildQueuedRun(
        long id,
        DateTime fromDate,
        DateTime toDate,
        string? parametersSnapshotJson = null)
        => new()
        {
            Id = id,
            StrategyId = 99,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            FromDate = fromDate,
            ToDate = toDate,
            InitialBalance = 10_000m,
            Status = RunStatus.Queued,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            QueuedAt = DateTime.UtcNow.AddMinutes(-10),
            AvailableAt = DateTime.UtcNow.AddMinutes(-10),
            ParametersSnapshotJson = parametersSnapshotJson,
            IsDeleted = false,
        };

    private static Strategy BuildStrategy(long id, string parametersJson)
        => new()
        {
            Id = id,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = parametersJson,
            Status = StrategyStatus.Active,
            IsDeleted = false,
        };

    private static List<Candle> BuildHourlyCandles(
        string symbol,
        Timeframe timeframe,
        DateTime fromDate,
        DateTime toDate)
        => Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling((toDate - fromDate).TotalHours)))
            .Select(i => fromDate.AddHours(i))
            .Where(ts => ts <= toDate)
            .Select(ts => new Candle
            {
                Symbol = symbol,
                Timeframe = timeframe,
                Timestamp = ts,
                Open = 1.1000m,
                High = 1.1010m,
                Low = 1.0990m,
                Close = 1.1005m,
                IsClosed = true,
            })
            .ToList();

    private static Mock<DbContext> BuildDb(
        List<BacktestRun>? backtests = null,
        List<Strategy>? strategies = null,
        List<Candle>? candles = null,
        List<WalkForwardRun>? walks = null,
        List<EngineConfig>? configs = null,
        List<CurrencyPair>? pairs = null,
        List<EconomicEvent>? economicEvents = null)
    {
        backtests ??= [];
        strategies ??= [];
        candles ??= [];
        walks ??= [];
        configs ??= [];
        pairs ??= [];
        economicEvents ??= [];

        var backtestDbSet = backtests.AsQueryable().BuildMockDbSet();
        backtestDbSet.Setup(d => d.Add(It.IsAny<BacktestRun>()))
            .Callback<BacktestRun>(backtests.Add);

        var walkDbSet = walks.AsQueryable().BuildMockDbSet();
        walkDbSet.Setup(d => d.AddAsync(It.IsAny<WalkForwardRun>(), It.IsAny<CancellationToken>()))
            .Callback<WalkForwardRun, CancellationToken>((run, _) => walks.Add(run))
            .Returns<WalkForwardRun, CancellationToken>((_, _) =>
                new ValueTask<EntityEntry<WalkForwardRun>>((EntityEntry<WalkForwardRun>)null!));

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<BacktestRun>()).Returns(backtestDbSet.Object);
        db.Setup(c => c.Set<Strategy>()).Returns(strategies.AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<Candle>()).Returns(candles.AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<WalkForwardRun>()).Returns(walkDbSet.Object);
        db.Setup(c => c.Set<EngineConfig>()).Returns(configs.AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<CurrencyPair>()).Returns(pairs.AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<EconomicEvent>()).Returns(economicEvents.AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<SpreadProfile>()).Returns(new List<SpreadProfile>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        return db;
    }

    private static (BacktestWorker Worker, Mock<IWriteApplicationDbContext> WriteContext, Mock<IIntegrationEventService> EventService)
        CreateWorker(
            Mock<DbContext> db,
            IBacktestEngine engine)
    {
        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var eventService = new Mock<IIntegrationEventService>();
        eventService.Setup(x => x.SaveAndPublish(
                It.IsAny<IDbContext>(),
                It.IsAny<Lascodia.Trading.Engine.EventBus.Events.IntegrationEvent>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(writeCtx.Object)
            .AddSingleton(eventService.Object)
            .AddSingleton<IValidationWorkerIdentity>(new TestValidationWorkerIdentity("test-backtest-worker"))
            .AddScoped<IValidationSettingsProvider, ValidationSettingsProvider>()
            .AddScoped<IBacktestAutoScheduler, BacktestAutoScheduler>()
            .AddSingleton<IStrategyExecutionSnapshotBuilder, StrategyExecutionSnapshotBuilder>()
            .AddSingleton<IValidationTradingCalendar, ValidationTradingCalendar>()
            .AddSingleton<IValidationCandleSeriesGuard, ValidationCandleSeriesGuard>()
            .AddScoped<IBacktestOptionsSnapshotBuilder>(sp =>
                new BacktestOptionsSnapshotBuilder(
                    sp.GetRequiredService<IValidationSettingsProvider>(),
                    NullLogger<BacktestOptionsSnapshotBuilder>.Instance))
            .AddScoped<IValidationRunFactory>(sp =>
                new ValidationRunFactory(
                    sp.GetRequiredService<IBacktestOptionsSnapshotBuilder>(),
                    sp.GetRequiredService<IStrategyExecutionSnapshotBuilder>(),
                    TimeProvider.System))
            .AddSingleton<IAutoWalkForwardWindowPolicy, AutoWalkForwardWindowPolicy>()
            .BuildServiceProvider();

        return (
            new BacktestWorker(
                Mock.Of<ILogger<BacktestWorker>>(),
                services.GetRequiredService<IServiceScopeFactory>(),
                engine,
                new InMemoryBacktestRunClaimService(),
                services.GetRequiredService<IValidationSettingsProvider>(),
                services.GetRequiredService<IBacktestAutoScheduler>(),
                services.GetRequiredService<IValidationRunFactory>(),
                services.GetRequiredService<IBacktestOptionsSnapshotBuilder>(),
                services.GetRequiredService<IStrategyExecutionSnapshotBuilder>(),
                services.GetRequiredService<IValidationCandleSeriesGuard>(),
                services.GetRequiredService<IAutoWalkForwardWindowPolicy>(),
                services.GetRequiredService<IValidationWorkerIdentity>()),
            writeCtx,
            eventService);
    }

    private static async Task InvokeProcessNextQueuedRunAsync(BacktestWorker worker, CancellationToken ct)
    {
        var method = typeof(BacktestWorker).GetMethod(
            "ProcessNextQueuedRunAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(worker, [ct])!;
    }

    private static async Task InvokeScheduleBacktestsForStaleStrategiesAsync(
        BacktestWorker worker,
        DbContext db,
        CancellationToken ct)
    {
        var method = typeof(BacktestWorker).GetMethod(
            "ScheduleBacktestsForStaleStrategiesAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(worker, [db, ct])!;
    }

    private static async Task InvokeRecoverStaleRunsAsync(
        BacktestWorker worker,
        DbContext db,
        IWriteApplicationDbContext writeCtx,
        CancellationToken ct)
    {
        var method = typeof(BacktestWorker).GetMethod(
            "RecoverStaleRunsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(worker, [db, writeCtx, ct])!;
    }

    private static List<T> GetList<T>(Mock<DbContext> db, Func<DbContext, DbSet<T>> selector)
        where T : class
        => selector(db.Object).AsQueryable().ToList();

    private sealed class RecordingBacktestEngine : IBacktestEngine
    {
        public List<string> SeenParameterJson { get; } = [];
        public List<BacktestOptions?> SeenOptions { get; } = [];

        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            SeenParameterJson.Add(strategy.ParametersJson);
            SeenOptions.Add(options);

            return Task.FromResult(new BacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = initialBalance + 500m,
                TotalReturn = 0.05m,
                TotalTrades = 12,
                WinningTrades = 8,
                LosingTrades = 4,
                WinRate = 0.67m,
                ProfitFactor = 1.70m,
                MaxDrawdownPct = 0.08m,
                SharpeRatio = 1.20m,
                Trades = []
            });
        }
    }

    private sealed class CancelingBacktestEngine : IBacktestEngine
    {
        private readonly CancellationTokenSource _cts;

        public CancelingBacktestEngine(CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            _cts.Cancel();
            throw new OperationCanceledException(ct);
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
