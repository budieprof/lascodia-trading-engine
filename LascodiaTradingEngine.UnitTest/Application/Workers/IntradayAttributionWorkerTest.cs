using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.Configurations;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class IntradayAttributionWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_InsertsHourlySnapshot_WithRunningStrategyAndSymbolBreakdown()
    {
        var now = new DateTimeOffset(2026, 04, 24, 10, 20, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            db =>
            {
                var account = EntityFactory.CreateAccount(equity: 10375m);
                account.Id = 1;
                account.Balance = 10000m;
                db.Add(account);

                db.Add(new BrokerAccountSnapshot
                {
                    Id = 1,
                    TradingAccountId = account.Id,
                    InstanceId = "EA-001",
                    Balance = 10000m,
                    Equity = 10100m,
                    MarginUsed = 0m,
                    FreeMargin = 10100m,
                    Currency = "USD",
                    ReportedAt = new DateTime(2026, 04, 24, 0, 0, 0, DateTimeKind.Utc),
                    IsDeleted = false
                });

                db.Add(new Strategy
                {
                    Id = 11,
                    Name = "Trend EURUSD",
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    StrategyType = StrategyType.MovingAverageCrossover,
                    Status = StrategyStatus.Active,
                    LifecycleStage = StrategyLifecycleStage.Active,
                    CreatedAt = now.UtcDateTime.AddDays(-10),
                    IsDeleted = false
                });
                db.Add(new Strategy
                {
                    Id = 12,
                    Name = "Breakout GBPUSD",
                    Symbol = "GBPUSD",
                    Timeframe = Timeframe.H1,
                    StrategyType = StrategyType.BreakoutScalper,
                    Status = StrategyStatus.Active,
                    LifecycleStage = StrategyLifecycleStage.Active,
                    CreatedAt = now.UtcDateTime.AddDays(-10),
                    IsDeleted = false
                });

                db.Add(new Order
                {
                    Id = 101,
                    TradingAccountId = account.Id,
                    StrategyId = 11,
                    Symbol = "EURUSD",
                    Quantity = 1m,
                    Price = 1.1000m,
                    OrderType = OrderType.Buy,
                    Status = OrderStatus.Filled,
                    FilledPrice = 1.1000m,
                    FilledAt = now.UtcDateTime.AddHours(-1),
                    CreatedAt = now.UtcDateTime.AddHours(-2),
                    IsDeleted = false
                });
                db.Add(new Order
                {
                    Id = 102,
                    TradingAccountId = account.Id,
                    StrategyId = 12,
                    Symbol = "GBPUSD",
                    Quantity = 1m,
                    Price = 1.2500m,
                    OrderType = OrderType.Buy,
                    Status = OrderStatus.Filled,
                    FilledPrice = 1.2500m,
                    FilledAt = now.UtcDateTime.AddMinutes(-30),
                    CreatedAt = now.UtcDateTime.AddHours(-3),
                    IsDeleted = false
                });

                db.Add(new Position
                {
                    Id = 201,
                    OpenOrderId = 101,
                    Symbol = "EURUSD",
                    Direction = PositionDirection.Long,
                    OpenLots = 1m,
                    AverageEntryPrice = 1.1000m,
                    Status = PositionStatus.Open,
                    RealizedPnL = 25m,
                    UnrealizedPnL = 150m,
                    OpenedAt = now.UtcDateTime.AddHours(-2),
                    IsDeleted = false
                });
                db.Add(new Position
                {
                    Id = 202,
                    OpenOrderId = 102,
                    Symbol = "GBPUSD",
                    Direction = PositionDirection.Long,
                    OpenLots = 1m,
                    AverageEntryPrice = 1.2500m,
                    Status = PositionStatus.Closed,
                    RealizedPnL = 200m,
                    UnrealizedPnL = 0m,
                    OpenedAt = now.UtcDateTime.AddHours(-3),
                    ClosedAt = now.UtcDateTime.AddMinutes(-20),
                    IsDeleted = false
                });

                db.Add(new TransactionCostAnalysis
                {
                    Id = 301,
                    OrderId = 102,
                    Symbol = "GBPUSD",
                    TotalCost = 10m,
                    AnalyzedAt = now.UtcDateTime.AddMinutes(-15),
                    IsDeleted = false
                });
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var attribution = Assert.Single(await harness.LoadAttributionsAsync());

        Assert.Equal(1, result.AccountCount);
        Assert.Equal(1, result.InsertedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(new DateTime(2026, 04, 24, 10, 0, 0, DateTimeKind.Utc), attribution.AttributionDate);
        Assert.Equal(PerformanceAttributionGranularity.Hourly, attribution.Granularity);
        Assert.Equal(10100m, attribution.StartOfDayEquity);
        Assert.Equal(225m, attribution.RealizedPnl);
        Assert.Equal(50m, attribution.UnrealizedPnlChange);
        Assert.Equal(10375m, attribution.EndOfDayEquity);
        Assert.Equal(1, attribution.TradeCount);
        Assert.Equal(1m, attribution.WinRate);
        Assert.Equal(decimal.Round(225m + 50m, 6), decimal.Round((attribution.DailyReturnPct / 100m) * 10100m, 6));

        var strategies = ParseStrategyGroups(attribution.StrategyAttributionJson);
        Assert.Contains(strategies, item => item.StrategyId == 11 && item.Pnl == 175m && item.OpenPositions == 1);
        Assert.Contains(strategies, item => item.StrategyId == 12 && item.Pnl == 200m && item.ClosedTrades == 1);

        var symbols = ParseSymbolGroups(attribution.SymbolAttributionJson);
        Assert.Contains(symbols, item => item.Symbol == "EURUSD" && item.Pnl == 175m);
        Assert.Contains(symbols, item => item.Symbol == "GBPUSD" && item.Pnl == 200m);
    }

    [Fact]
    public async Task RunCycleAsync_RefreshesExistingHourlySnapshot_InPlace()
    {
        var now = new DateTimeOffset(2026, 04, 24, 10, 20, 0, TimeSpan.Zero);
        var hourStart = new DateTime(2026, 04, 24, 10, 0, 0, DateTimeKind.Utc);

        using var harness = CreateHarness(
            db =>
            {
                var account = EntityFactory.CreateAccount(equity: 10150m);
                account.Id = 1;
                account.Balance = 10000m;
                db.Add(account);

                db.Add(new BrokerAccountSnapshot
                {
                    Id = 1,
                    TradingAccountId = account.Id,
                    InstanceId = "EA-001",
                    Balance = 10000m,
                    Equity = 10000m,
                    MarginUsed = 0m,
                    FreeMargin = 10000m,
                    Currency = "USD",
                    ReportedAt = new DateTime(2026, 04, 24, 0, 0, 0, DateTimeKind.Utc),
                    IsDeleted = false
                });

                db.Add(new Strategy
                {
                    Id = 11,
                    Name = "Trend EURUSD",
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    StrategyType = StrategyType.MovingAverageCrossover,
                    Status = StrategyStatus.Active,
                    LifecycleStage = StrategyLifecycleStage.Active,
                    CreatedAt = now.UtcDateTime.AddDays(-10),
                    IsDeleted = false
                });

                db.Add(new Order
                {
                    Id = 101,
                    TradingAccountId = account.Id,
                    StrategyId = 11,
                    Symbol = "EURUSD",
                    Quantity = 1m,
                    Price = 1.1000m,
                    OrderType = OrderType.Buy,
                    Status = OrderStatus.Filled,
                    FilledPrice = 1.1000m,
                    FilledAt = now.UtcDateTime.AddHours(-1),
                    CreatedAt = now.UtcDateTime.AddHours(-2),
                    IsDeleted = false
                });

                db.Add(new Position
                {
                    Id = 201,
                    OpenOrderId = 101,
                    Symbol = "EURUSD",
                    Direction = PositionDirection.Long,
                    OpenLots = 1m,
                    AverageEntryPrice = 1.1000m,
                    Status = PositionStatus.Open,
                    RealizedPnL = 0m,
                    UnrealizedPnL = 150m,
                    OpenedAt = now.UtcDateTime.AddHours(-2),
                    IsDeleted = false
                });

                db.Add(new AccountPerformanceAttribution
                {
                    Id = 900,
                    TradingAccountId = account.Id,
                    AttributionDate = hourStart,
                    Granularity = PerformanceAttributionGranularity.Hourly,
                    StartOfDayEquity = 9999m,
                    EndOfDayEquity = 9999m,
                    RealizedPnl = 0m,
                    UnrealizedPnlChange = 0m,
                    DailyReturnPct = 0m,
                    StrategyAttributionJson = "[]",
                    SymbolAttributionJson = "[]",
                    IsDeleted = false
                });
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var rows = await harness.LoadAttributionsAsync();
        var attribution = Assert.Single(rows);

        Assert.Equal(0, result.InsertedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(900, attribution.Id);
        Assert.Equal(150m, attribution.UnrealizedPnlChange);
        Assert.Contains("\"EURUSD\"", attribution.SymbolAttributionJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCycleAsync_HourlyGranularity_CoexistsWithDailySnapshotAtSameTimestamp()
    {
        var now = new DateTimeOffset(2026, 04, 24, 10, 20, 0, TimeSpan.Zero);
        var hourStart = new DateTime(2026, 04, 24, 10, 0, 0, DateTimeKind.Utc);

        using var harness = CreateHarness(
            db =>
            {
                var account = EntityFactory.CreateAccount(equity: 10000m);
                account.Id = 1;
                db.Add(account);

                db.Add(new Strategy
                {
                    Id = 11,
                    Name = "Trend EURUSD",
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.H1,
                    StrategyType = StrategyType.MovingAverageCrossover,
                    Status = StrategyStatus.Active,
                    LifecycleStage = StrategyLifecycleStage.Active,
                    CreatedAt = now.UtcDateTime.AddDays(-10),
                    IsDeleted = false
                });

                db.Add(new Order
                {
                    Id = 101,
                    TradingAccountId = account.Id,
                    StrategyId = 11,
                    Symbol = "EURUSD",
                    Quantity = 1m,
                    Price = 1.1000m,
                    OrderType = OrderType.Buy,
                    Status = OrderStatus.Filled,
                    FilledPrice = 1.1000m,
                    FilledAt = now.UtcDateTime.AddHours(-1),
                    CreatedAt = now.UtcDateTime.AddHours(-2),
                    IsDeleted = false
                });

                db.Add(new Position
                {
                    Id = 201,
                    OpenOrderId = 101,
                    Symbol = "EURUSD",
                    Direction = PositionDirection.Long,
                    OpenLots = 1m,
                    AverageEntryPrice = 1.1000m,
                    Status = PositionStatus.Open,
                    UnrealizedPnL = 25m,
                    OpenedAt = now.UtcDateTime.AddHours(-2),
                    IsDeleted = false
                });

                db.Add(new AccountPerformanceAttribution
                {
                    Id = 700,
                    TradingAccountId = account.Id,
                    AttributionDate = hourStart,
                    Granularity = PerformanceAttributionGranularity.Daily,
                    StartOfDayEquity = 10000m,
                    EndOfDayEquity = 10025m,
                    RealizedPnl = 25m,
                    UnrealizedPnlChange = 0m,
                    DailyReturnPct = 0.25m,
                    StrategyAttributionJson = "[]",
                    SymbolAttributionJson = "[]",
                    IsDeleted = false
                });
            },
            timeProvider: new TestTimeProvider(now));

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var rows = await harness.LoadAttributionsAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Granularity == PerformanceAttributionGranularity.Daily);
        Assert.Contains(rows, row => row.Granularity == PerformanceAttributionGranularity.Hourly);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutWriting()
    {
        var now = new DateTimeOffset(2026, 04, 24, 10, 20, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            db =>
            {
                var account = EntityFactory.CreateAccount(equity: 10000m);
                account.Id = 1;
                db.Add(account);
            },
            timeProvider: new TestTimeProvider(now),
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await harness.LoadAttributionsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_InvalidPollInterval_IsClampedSafely()
    {
        using var harness = CreateHarness(
            _ => { },
            options: new IntradayAttributionOptions
            {
                Enabled = true,
                PollIntervalSeconds = -1
            });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(5), result.Settings.PollInterval);
    }

    private static WorkerHarness CreateHarness(
        Action<IntradayAttributionWorkerTestContext> seed,
        IntradayAttributionOptions? options = null,
        TradingDayOptions? tradingDayOptions = null,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<IntradayAttributionWorkerTestContext>(dbOptions => dbOptions.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<IntradayAttributionWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<IntradayAttributionWorkerTestContext>());

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IntradayAttributionWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new IntradayAttributionWorker(
            NullLogger<IntradayAttributionWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            options ?? new IntradayAttributionOptions(),
            tradingDayOptions ?? new TradingDayOptions(),
            metrics: null,
            timeProvider: timeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, connection, worker);
    }

    private static List<StrategyBreakdown> ParseStrategyGroups(string json)
        => JsonDocument.Parse(json)
            .RootElement
            .EnumerateArray()
            .Select(item => new StrategyBreakdown(
                item.GetProperty(nameof(StrategyBreakdown.StrategyId)).GetInt64(),
                item.GetProperty(nameof(StrategyBreakdown.Pnl)).GetDecimal(),
                item.GetProperty(nameof(StrategyBreakdown.ClosedTrades)).GetInt32(),
                item.GetProperty(nameof(StrategyBreakdown.OpenPositions)).GetInt32()))
            .ToList();

    private static List<SymbolBreakdown> ParseSymbolGroups(string json)
        => JsonDocument.Parse(json)
            .RootElement
            .EnumerateArray()
            .Select(item => new SymbolBreakdown(
                item.GetProperty(nameof(SymbolBreakdown.Symbol)).GetString() ?? string.Empty,
                item.GetProperty(nameof(SymbolBreakdown.Pnl)).GetDecimal()))
            .ToList();

    private sealed record StrategyBreakdown(long StrategyId, decimal Pnl, int ClosedTrades, int OpenPositions);

    private sealed record SymbolBreakdown(string Symbol, decimal Pnl);

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        IntradayAttributionWorker worker) : IDisposable
    {
        public IntradayAttributionWorker Worker { get; } = worker;

        public async Task<List<AccountPerformanceAttribution>> LoadAttributionsAsync(bool ignoreQueryFilters = false)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IntradayAttributionWorkerTestContext>();
            var query = db.Set<AccountPerformanceAttribution>().AsQueryable();
            if (ignoreQueryFilters)
                query = query.IgnoreQueryFilters();

            return await query
                .OrderBy(attribution => attribution.AttributionDate)
                .ThenBy(attribution => attribution.Granularity)
                .ToListAsync();
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class IntradayAttributionWorkerTestContext(DbContextOptions<IntradayAttributionWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TradingAccount>(builder =>
            {
                builder.HasKey(account => account.Id);
                builder.HasQueryFilter(account => !account.IsDeleted);
                builder.Ignore(account => account.Orders);
                builder.Ignore(account => account.EAInstances);
            });

            modelBuilder.Entity<Strategy>(builder =>
            {
                builder.HasKey(strategy => strategy.Id);
                builder.HasQueryFilter(strategy => !strategy.IsDeleted);
                builder.Ignore(strategy => strategy.RiskProfile);
                builder.Ignore(strategy => strategy.TradeSignals);
                builder.Ignore(strategy => strategy.Orders);
                builder.Ignore(strategy => strategy.BacktestRuns);
                builder.Ignore(strategy => strategy.OptimizationRuns);
                builder.Ignore(strategy => strategy.WalkForwardRuns);
                builder.Ignore(strategy => strategy.Allocations);
                builder.Ignore(strategy => strategy.PerformanceSnapshots);
                builder.Ignore(strategy => strategy.ExecutionQualityLogs);
            });

            modelBuilder.Entity<Order>(builder =>
            {
                builder.HasKey(order => order.Id);
                builder.HasQueryFilter(order => !order.IsDeleted);
                builder.Ignore(order => order.TradeSignal);
                builder.Ignore(order => order.Strategy);
                builder.Ignore(order => order.TradingAccount);
                builder.Ignore(order => order.ExecutionQualityLog);
                builder.Ignore(order => order.PositionScaleOrders);
            });

            modelBuilder.Entity<Position>(builder =>
            {
                builder.HasKey(position => position.Id);
                builder.HasQueryFilter(position => !position.IsDeleted);
                builder.Ignore(position => position.ScaleOrders);
            });

            modelBuilder.Entity<TransactionCostAnalysis>(builder =>
            {
                builder.HasKey(record => record.Id);
                builder.HasQueryFilter(record => !record.IsDeleted);
                builder.Ignore(record => record.Order);
                builder.Ignore(record => record.TradeSignal);
            });

            modelBuilder.Entity<BrokerAccountSnapshot>(builder =>
            {
                builder.HasKey(snapshot => snapshot.Id);
                builder.HasQueryFilter(snapshot => !snapshot.IsDeleted);
            });

            new AccountPerformanceAttributionConfiguration().Configure(modelBuilder.Entity<AccountPerformanceAttribution>());
            modelBuilder.Entity<AccountPerformanceAttribution>()
                .Property(attribution => attribution.RowVersion)
                .HasDefaultValue(0u)
                .ValueGeneratedNever();
        }
    }

    private sealed class TestDistributedLock(bool lockAvailable) : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(lockAvailable ? new Releaser() : null);

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
            => TryAcquireAsync(lockKey, ct);

        private sealed class Releaser : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
