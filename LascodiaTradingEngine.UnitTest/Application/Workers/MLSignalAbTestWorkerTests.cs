using System.Reflection;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.Configurations;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLSignalAbTestWorkerTests
{
    [Fact]
    public async Task BuildAbTestStateAsync_ToleratesDuplicatePredictionLogsAndUsesServedSignalModel()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var db = fixture.Db;
        var startedAt = DateTime.UtcNow.AddHours(-1);

        var champion = await AddModelAsync(db, 1, MLModelStatus.Active, isActive: true);
        var challenger = await AddModelAsync(db, 2, MLModelStatus.Training, isActive: false);

        var signal = new TradeSignal
        {
            StrategyId = 100,
            Symbol = "EURUSD",
            Direction = TradeDirection.Buy,
            EntryPrice = 1.1m,
            SuggestedLotSize = 0.1m,
            Confidence = 0.8m,
            MLModelId = challenger.Id,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        };
        db.Set<TradeSignal>().Add(signal);
        await db.SaveChangesAsync();

        var order = new Order
        {
            TradeSignalId = signal.Id,
            Symbol = "EURUSD",
            TradingAccountId = 1,
            StrategyId = signal.StrategyId,
            Quantity = 0.1m,
            Status = OrderStatus.Filled,
        };
        db.Set<Order>().Add(order);
        await db.SaveChangesAsync();

        db.Set<Position>().Add(new Position
        {
            Symbol = "EURUSD",
            Status = PositionStatus.Closed,
            RealizedPnL = 12.5m,
            OpenOrderId = order.Id,
            OpenedAt = DateTime.UtcNow.AddMinutes(-40),
            ClosedAt = DateTime.UtcNow.AddMinutes(-10),
        });

        db.Set<MLModelPredictionLog>().AddRange(
            Prediction(signal.Id, champion.Id, ModelRole.Champion, startedAt.AddMinutes(5)),
            Prediction(signal.Id, challenger.Id, ModelRole.Challenger, startedAt.AddMinutes(6)));
        await db.SaveChangesAsync();

        var stateBuilder = new SignalAbTestStateBuilder();
        var state = await stateBuilder.BuildAsync(
            db,
            champion.Id,
            challenger.Id,
            "EURUSD",
            Timeframe.H1,
            startedAt,
            CancellationToken.None);

        Assert.Empty(state.ChampionOutcomes);
        Assert.Single(state.ChallengerOutcomes);
        Assert.Equal(12.5, state.ChallengerOutcomes[0].Pnl);
    }

    [Fact]
    public async Task PromoteChallengerAsync_DemotesAllActiveModelsAndWritesAudit()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var db = fixture.Db;

        var champion = await AddModelAsync(db, 1, MLModelStatus.Active, isActive: true, accuracy: 0.61m);
        var otherActive = await AddModelAsync(db, 3, MLModelStatus.Active, isActive: true, accuracy: 0.55m);
        var challenger = await AddModelAsync(db, 2, MLModelStatus.Training, isActive: false, accuracy: 0.67m);

        db.Set<TradeSignal>().Add(new TradeSignal
        {
            StrategyId = 100,
            Symbol = "EURUSD",
            Direction = TradeDirection.Buy,
            EntryPrice = 1.1m,
            SuggestedLotSize = 0.1m,
            Confidence = 0.8m,
            MLModelId = champion.Id,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });
        await db.SaveChangesAsync();

        db.Set<MLModelPredictionLog>().AddRange(
            ResolvedPrediction(champion.Id, true),
            ResolvedPrediction(champion.Id, false),
            ResolvedPrediction(champion.Id, true));
        await db.SaveChangesAsync();

        var lifecycleService = new MLModelLifecycleTransitionService(
            NullLogger<MLModelLifecycleTransitionService>.Instance);
        await lifecycleService.PromoteChallengerAsync(
            db,
            champion.Id,
            challenger.Id,
            "EURUSD",
            Timeframe.H1,
            CancellationToken.None);

        var models = await db.Set<MLModel>().AsNoTracking().ToListAsync();
        Assert.True(models.Single(m => m.Id == challenger.Id).IsActive);
        Assert.Equal(MLModelStatus.Active, models.Single(m => m.Id == challenger.Id).Status);
        Assert.False(models.Single(m => m.Id == champion.Id).IsActive);
        Assert.False(models.Single(m => m.Id == otherActive.Id).IsActive);
        Assert.Equal(2m / 3m, models.Single(m => m.Id == champion.Id).LiveDirectionAccuracy!.Value, 6);

        var logs = await db.Set<MLModelLifecycleLog>().AsNoTracking().ToListAsync();
        Assert.Contains(logs, l => l.MLModelId == challenger.Id && l.EventType == MLModelLifecycleEventType.AbTestPromotion);
        Assert.Contains(logs, l => l.MLModelId == champion.Id && l.EventType == MLModelLifecycleEventType.AbTestDemotion);
    }

    [Fact]
    public async Task PersistTerminalResultAsync_WritesIdempotentAuditRow()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var db = fixture.Db;
        var startedAt = DateTime.UtcNow.AddDays(-1);
        var state = new AbTestState(
            TestId: 0,
            ChampionModelId: 1,
            ChallengerModelId: 2,
            Symbol: "EURUSD",
            Timeframe: Timeframe.H1,
            StartedAtUtc: startedAt,
            ChampionOutcomes: [],
            ChallengerOutcomes: []);
        var result = new AbTestResult
        {
            Decision = AbTestDecision.PromoteChallenger,
            Reason = "test passed",
            ChampionTradeCount = 31,
            ChallengerTradeCount = 32,
            ChampionAvgPnl = 1.25,
            ChallengerAvgPnl = 2.5,
            ChampionSharpe = 0.7,
            ChallengerSharpe = 1.2,
            SprtLogLikelihoodRatio = 3.1,
        };

        var store = new SignalAbTestTerminalResultStore();
        await store.PersistAsync(db, state, result, CancellationToken.None);
        await store.PersistAsync(db, state, result, CancellationToken.None);

        var audit = Assert.Single(await db.Set<MLSignalAbTestResult>().AsNoTracking().ToListAsync());
        Assert.Equal("PromoteChallenger", audit.Decision);
        Assert.Equal(32, audit.ChallengerTradeCount);
        Assert.Equal(3.1m, audit.SprtLogLikelihoodRatio);
    }

    [Fact]
    public async Task PersistTerminalResultAsync_TreatsProviderUniqueIndexRaceAsIdempotent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ml-abtest-race-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";

        try
        {
            var setupOptions = CreateSqliteOptions<SqliteSignalAbTestContext>(connectionString);
            await using (var setup = new SqliteSignalAbTestContext(setupOptions))
            {
                await setup.Database.EnsureDeletedAsync();
                await setup.Database.EnsureCreatedAsync();
            }

            var startedAt = new DateTime(2026, 04, 22, 10, 0, 0, DateTimeKind.Utc);
            var state = new AbTestState(
                TestId: 0,
                ChampionModelId: 11,
                ChallengerModelId: 12,
                Symbol: "EURUSD",
                Timeframe: Timeframe.H1,
                StartedAtUtc: startedAt,
                ChampionOutcomes: [],
                ChallengerOutcomes: []);
            var result = new AbTestResult
            {
                Decision = AbTestDecision.KeepChampion,
                Reason = "race duplicate",
                ChampionTradeCount = 40,
                ChallengerTradeCount = 41,
                ChampionAvgPnl = 1.0,
                ChallengerAvgPnl = 0.5,
                ChampionSharpe = 1.2,
                ChallengerSharpe = 0.8,
                SprtLogLikelihoodRatio = -3.0,
            };

            var raceOptions = CreateSqliteOptions<RaceInjectingSignalAbTestContext>(connectionString);
            await using (var raceContext = new RaceInjectingSignalAbTestContext(raceOptions, connectionString))
            {
                var store = new SignalAbTestTerminalResultStore();
                await store.PersistAsync(raceContext, state, result, CancellationToken.None);
            }

            await using var assertContext = new SqliteSignalAbTestContext(setupOptions);
            var audit = Assert.Single(await assertContext.Set<MLSignalAbTestResult>().AsNoTracking().ToListAsync());
            Assert.Equal("KeepChampion", audit.Decision);
            Assert.Equal(startedAt, audit.StartedAtUtc);
        }
        finally
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ProcessSingleTestAsync_WhenModelUnavailable_PersistsInvalidatedTerminalResult()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var db = fixture.Db;
        var worker = CreateWorker();
        var challenger = await AddModelAsync(db, 2, MLModelStatus.Training, isActive: false);
        var startedAt = DateTime.UtcNow.AddHours(-2);
        var test = new MLSignalAbTest
        {
            ChampionModelId = 404,
            ChallengerModelId = challenger.Id,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            Status = MLSignalAbTestStatus.Active,
            StartedAtUtc = startedAt,
        };
        db.Set<MLSignalAbTest>().Add(test);
        await db.SaveChangesAsync();

        await InvokePrivateAsync<object>(
            worker,
            "ProcessSingleTestAsync",
            test,
            db,
            db,
            db,
            30,
            14,
            new AbTestEvaluationOptions(),
            CancellationToken.None);

        var terminalResult = Assert.Single(await db.Set<MLSignalAbTestResult>().AsNoTracking().ToListAsync());
        Assert.Equal("Invalidated", terminalResult.Decision);
        Assert.Equal(startedAt, terminalResult.StartedAtUtc);

        var completedTest = await db.Set<MLSignalAbTest>().AsNoTracking().SingleAsync(x => x.Id == test.Id);
        Assert.Equal(MLSignalAbTestStatus.Completed, completedTest.Status);
        Assert.NotNull(completedTest.CompletedAtUtc);
    }

    private static MLSignalAbTestWorker CreateWorker()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();

        var lockMock = new Mock<IDistributedLock>();
        lockMock
            .Setup(x => x.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());

        return new MLSignalAbTestWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLSignalAbTestWorker>>(),
            new SignalAbTestCoordinator(Mock.Of<ILogger<SignalAbTestCoordinator>>()),
            lockMock.Object,
            new SignalAbTestStateBuilder(),
            new SignalAbTestTerminalResultStore(),
            new MLModelLifecycleTransitionService(
                NullLogger<MLModelLifecycleTransitionService>.Instance));
    }

    private static async Task<MLModel> AddModelAsync(
        WriteApplicationDbContext db,
        long ignoredId,
        MLModelStatus status,
        bool isActive,
        decimal accuracy = 0.6m)
    {
        var model = new MLModel
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ModelVersion = $"1.0.{ignoredId}",
            FilePath = string.Empty,
            Status = status,
            IsActive = isActive,
            DirectionAccuracy = accuracy,
            BrierScore = 0.2m,
            ActivatedAt = isActive ? DateTime.UtcNow.AddDays(-2) : null,
            ModelBytes = [1, 2, 3],
        };
        db.Set<MLModel>().Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    private static MLModelPredictionLog Prediction(
        long signalId,
        long modelId,
        ModelRole role,
        DateTime predictedAt)
        => new()
        {
            TradeSignalId = signalId,
            MLModelId = modelId,
            ModelRole = role,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            PredictedDirection = TradeDirection.Buy,
            PredictedAt = predictedAt,
        };

    private static MLModelPredictionLog ResolvedPrediction(long modelId, bool correct)
        => new()
        {
            TradeSignalId = 1,
            MLModelId = modelId,
            ModelRole = ModelRole.Champion,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            PredictedDirection = TradeDirection.Buy,
            DirectionCorrect = correct,
            PredictedAt = DateTime.UtcNow.AddHours(-1),
        };

    private static async Task<T> InvokePrivateAsync<T>(
        MLSignalAbTestWorker worker,
        string methodName,
        params object?[] args)
    {
        var method = typeof(MLSignalAbTestWorker).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(worker, args)!;
        await task;

        if (typeof(T) == typeof(object))
            return default!;

        var resultProperty = task.GetType().GetProperty("Result");
        return (T)resultProperty!.GetValue(task)!;
    }

    private sealed class DbFixture : IAsyncDisposable
    {
        public WriteApplicationDbContext Db { get; }

        private DbFixture(WriteApplicationDbContext db)
        {
            Db = db;
        }

        public static async Task<DbFixture> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            var db = new WriteApplicationDbContext(options, new HttpContextAccessor());
            await db.Database.EnsureCreatedAsync();

            return new DbFixture(db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
        }
    }

    private static DbContextOptions<TContext> CreateSqliteOptions<TContext>(string connectionString)
        where TContext : DbContext
        => new DbContextOptionsBuilder<TContext>()
            .UseSqlite(connectionString)
            .Options;

    private class SqliteSignalAbTestContext : DbContext
    {
        public SqliteSignalAbTestContext(DbContextOptions options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new MLSignalAbTestResultConfiguration());
            modelBuilder.Entity<MLSignalAbTestResult>()
                .Property(x => x.RowVersion)
                .ValueGeneratedNever()
                .HasDefaultValue(0u)
                .IsConcurrencyToken();
        }
    }

    private sealed class RaceInjectingSignalAbTestContext : SqliteSignalAbTestContext
    {
        private readonly string _connectionString;
        private bool _injected;

        public RaceInjectingSignalAbTestContext(
            DbContextOptions<RaceInjectingSignalAbTestContext> options,
            string connectionString)
            : base(options)
        {
            _connectionString = connectionString;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var pendingTerminalResult = ChangeTracker
                .Entries<MLSignalAbTestResult>()
                .FirstOrDefault(e => e.State == EntityState.Added)
                ?.Entity;

            if (!_injected && pendingTerminalResult is not null)
            {
                _injected = true;

                var duplicateOptions = CreateSqliteOptions<SqliteSignalAbTestContext>(_connectionString);
                await using var duplicateContext = new SqliteSignalAbTestContext(duplicateOptions);
                duplicateContext.Set<MLSignalAbTestResult>().Add(new MLSignalAbTestResult
                {
                    ChampionModelId = pendingTerminalResult.ChampionModelId,
                    ChallengerModelId = pendingTerminalResult.ChallengerModelId,
                    Symbol = pendingTerminalResult.Symbol,
                    Timeframe = pendingTerminalResult.Timeframe,
                    StartedAtUtc = pendingTerminalResult.StartedAtUtc,
                    CompletedAtUtc = pendingTerminalResult.CompletedAtUtc.AddMilliseconds(-1),
                    Decision = pendingTerminalResult.Decision,
                    Reason = pendingTerminalResult.Reason,
                    ChampionTradeCount = pendingTerminalResult.ChampionTradeCount,
                    ChallengerTradeCount = pendingTerminalResult.ChallengerTradeCount,
                    ChampionAvgPnl = pendingTerminalResult.ChampionAvgPnl,
                    ChallengerAvgPnl = pendingTerminalResult.ChallengerAvgPnl,
                    ChampionSharpe = pendingTerminalResult.ChampionSharpe,
                    ChallengerSharpe = pendingTerminalResult.ChallengerSharpe,
                    SprtLogLikelihoodRatio = pendingTerminalResult.SprtLogLikelihoodRatio,
                    CovariateImbalanceScore = pendingTerminalResult.CovariateImbalanceScore,
                });
                await duplicateContext.SaveChangesAsync(cancellationToken);
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
