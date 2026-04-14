using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

public class DatabaseIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DatabaseIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private WriteApplicationDbContext CreateWriteContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private ReadApplicationDbContext CreateReadContext()
    {
        var options = new DbContextOptionsBuilder<ReadApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new ReadApplicationDbContext(options, new HttpContextAccessor());
    }

    private async Task EnsureMigrated()
    {
        await using var context = CreateWriteContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();
    }

    /// <summary>
    /// Seeds the prerequisite entities (TradingAccount, Strategy) that Orders depend on
    /// via foreign keys, and returns their IDs.
    /// </summary>
    private async Task<(long TradingAccountId, long StrategyId)> SeedPrerequisitesAsync()
    {
        await using var context = CreateWriteContext();

        var tradingAccount = new TradingAccount
        {
            AccountId = "101-001-TEST-001",
            AccountName = "Integration Test Account",
            BrokerServer = "api-fxpractice.oanda.com",
            BrokerName = "Oanda",
            AccountType = AccountType.Demo,
            Currency = "USD",
            Balance = 10000m,
            Equity = 10000m,
            MarginUsed = 0m,
            MarginAvailable = 10000m,
            IsActive = true,
            IsPaper = true
        };
        context.Set<TradingAccount>().Add(tradingAccount);

        var strategy = new Strategy
        {
            Name = "Test MA Crossover",
            Description = "Integration test strategy",
            StrategyType = StrategyType.MovingAverageCrossover,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = "{\"FastPeriod\":9,\"SlowPeriod\":21}",
            Status = StrategyStatus.Active
        };
        context.Set<Strategy>().Add(strategy);
        await context.SaveChangesAsync();

        return (tradingAccount.Id, strategy.Id);
    }

    [Fact]
    public async Task MigrateAsync_ShouldApplyAllMigrations_WithoutErrors()
    {
        // Arrange & Act
        await EnsureMigrated();

        // Assert — if we get here without exception, migrations applied successfully.
        await using var context = CreateWriteContext();
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
        Assert.Empty(pendingMigrations);
    }

    [Fact]
    public async Task Order_CreateAndRead_ShouldPersistAndRetrieveCorrectly()
    {
        // Arrange
        await EnsureMigrated();
        var (tradingAccountId, strategyId) = await SeedPrerequisitesAsync();

        // Act — create an order
        long orderId;
        await using (var writeCtx = CreateWriteContext())
        {
            var order = new Order
            {
                Symbol = "EURUSD",
                Session = TradingSession.London,
                TradingAccountId = tradingAccountId,
                StrategyId = strategyId,
                OrderType = OrderType.Buy,
                ExecutionType = ExecutionType.Market,
                Quantity = 0.1m,
                Price = 0m,
                StopLoss = 1.0900m,
                TakeProfit = 1.1100m,
                Status = OrderStatus.Pending,
                IsPaper = true
            };
            writeCtx.Set<Order>().Add(order);
            await writeCtx.SaveChangesAsync();
            orderId = order.Id;
        }

        // Assert — read it back via the read context
        await using var readCtx = CreateReadContext();
        var retrieved = await readCtx.Set<Order>().FirstOrDefaultAsync(o => o.Id == orderId);

        Assert.NotNull(retrieved);
        Assert.Equal("EURUSD", retrieved.Symbol);
        Assert.Equal(OrderType.Buy, retrieved.OrderType);
        Assert.Equal(ExecutionType.Market, retrieved.ExecutionType);
        Assert.Equal(0.1m, retrieved.Quantity);
        Assert.Equal(1.0900m, retrieved.StopLoss);
        Assert.Equal(1.1100m, retrieved.TakeProfit);
        Assert.Equal(OrderStatus.Pending, retrieved.Status);
        Assert.True(retrieved.IsPaper);
    }

    [Fact]
    public async Task Order_SoftDelete_ShouldBeExcludedByGlobalQueryFilter()
    {
        // Arrange
        await EnsureMigrated();
        var (tradingAccountId, strategyId) = await SeedPrerequisitesAsync();

        long orderId;
        await using (var writeCtx = CreateWriteContext())
        {
            var order = new Order
            {
                Symbol = "GBPUSD",
                Session = TradingSession.NewYork,
                TradingAccountId = tradingAccountId,
                StrategyId = strategyId,
                OrderType = OrderType.Sell,
                ExecutionType = ExecutionType.Limit,
                Quantity = 0.5m,
                Price = 1.2700m,
                Status = OrderStatus.Pending,
                IsPaper = true
            };
            writeCtx.Set<Order>().Add(order);
            await writeCtx.SaveChangesAsync();
            orderId = order.Id;
        }

        // Act — soft-delete the order
        await using (var writeCtx = CreateWriteContext())
        {
            var order = await writeCtx.Set<Order>().FindAsync(orderId);
            Assert.NotNull(order);
            order.IsDeleted = true;
            await writeCtx.SaveChangesAsync();
        }

        // Assert — standard query should NOT return the soft-deleted order
        await using var readCtx = CreateReadContext();
        var filtered = await readCtx.Set<Order>().FirstOrDefaultAsync(o => o.Id == orderId);
        Assert.Null(filtered);

        // Assert — IgnoreQueryFilters should still return it
        var unfiltered = await readCtx.Set<Order>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == orderId);
        Assert.NotNull(unfiltered);
        Assert.True(unfiltered.IsDeleted);
    }

    [Fact]
    public async Task Candle_CreateMultiple_ShouldSupportPaginationQuery()
    {
        // Arrange
        await EnsureMigrated();

        await using (var writeCtx = CreateWriteContext())
        {
            var baseTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 20; i++)
            {
                writeCtx.Set<Candle>().Add(new Candle
                {
                    Symbol = "USDJPY",
                    Timeframe = Timeframe.H1,
                    Open = 150.00m + i * 0.01m,
                    High = 150.50m + i * 0.01m,
                    Low = 149.80m + i * 0.01m,
                    Close = 150.20m + i * 0.01m,
                    Volume = 1000 + i,
                    Timestamp = baseTime.AddHours(i),
                    IsClosed = true
                });
            }
            await writeCtx.SaveChangesAsync();
        }

        // Act — query with skip/take (simulating pagination)
        await using var readCtx = CreateReadContext();
        var page1 = await readCtx.Set<Candle>()
            .Where(c => c.Symbol == "USDJPY")
            .OrderBy(c => c.Timestamp)
            .Skip(0).Take(10)
            .ToListAsync();

        var page2 = await readCtx.Set<Candle>()
            .Where(c => c.Symbol == "USDJPY")
            .OrderBy(c => c.Timestamp)
            .Skip(10).Take(10)
            .ToListAsync();

        // Assert
        Assert.Equal(10, page1.Count);
        Assert.Equal(10, page2.Count);
        Assert.True(page1.Last().Timestamp < page2.First().Timestamp);

        var totalCount = await readCtx.Set<Candle>()
            .Where(c => c.Symbol == "USDJPY")
            .CountAsync();
        Assert.Equal(20, totalCount);
    }

    [Fact]
    public async Task Order_UpdateStatus_ShouldPersistChanges()
    {
        // Arrange
        await EnsureMigrated();
        var (tradingAccountId, strategyId) = await SeedPrerequisitesAsync();

        long orderId;
        await using (var writeCtx = CreateWriteContext())
        {
            var order = new Order
            {
                Symbol = "AUDUSD",
                Session = TradingSession.Asian,
                TradingAccountId = tradingAccountId,
                StrategyId = strategyId,
                OrderType = OrderType.Buy,
                ExecutionType = ExecutionType.Market,
                Quantity = 0.2m,
                Price = 0m,
                Status = OrderStatus.Pending,
                IsPaper = true
            };
            writeCtx.Set<Order>().Add(order);
            await writeCtx.SaveChangesAsync();
            orderId = order.Id;
        }

        // Act — simulate order submission and fill
        await using (var writeCtx = CreateWriteContext())
        {
            var order = await writeCtx.Set<Order>().FindAsync(orderId);
            Assert.NotNull(order);
            order.Status = OrderStatus.Submitted;
            order.BrokerOrderId = "BROKER-12345";
            await writeCtx.SaveChangesAsync();
        }

        await using (var writeCtx = CreateWriteContext())
        {
            var order = await writeCtx.Set<Order>().FindAsync(orderId);
            Assert.NotNull(order);
            order.Status = OrderStatus.Filled;
            order.FilledPrice = 0.6750m;
            order.FilledQuantity = 0.2m;
            order.FilledAt = DateTime.UtcNow;
            await writeCtx.SaveChangesAsync();
        }

        // Assert
        await using var readCtx = CreateReadContext();
        var filled = await readCtx.Set<Order>().FirstOrDefaultAsync(o => o.Id == orderId);

        Assert.NotNull(filled);
        Assert.Equal(OrderStatus.Filled, filled.Status);
        Assert.Equal("BROKER-12345", filled.BrokerOrderId);
        Assert.Equal(0.6750m, filled.FilledPrice);
        Assert.Equal(0.2m, filled.FilledQuantity);
        Assert.NotNull(filled.FilledAt);
    }

    [Fact]
    public async Task Strategy_CreateAndQueryByStatus_ShouldFilterCorrectly()
    {
        // Arrange
        await EnsureMigrated();

        await using (var writeCtx = CreateWriteContext())
        {
            writeCtx.Set<Strategy>().Add(new Strategy
            {
                Name = "Active Strategy A",
                Description = "Test active",
                StrategyType = StrategyType.RSIReversion,
                Symbol = "NZDUSD",
                Timeframe = Timeframe.M15,
                ParametersJson = "{\"Period\":14,\"Overbought\":70,\"Oversold\":30}",
                Status = StrategyStatus.Active
            });

            writeCtx.Set<Strategy>().Add(new Strategy
            {
                Name = "Paused Strategy B",
                Description = "Test paused",
                StrategyType = StrategyType.BreakoutScalper,
                Symbol = "USDCAD",
                Timeframe = Timeframe.M5,
                ParametersJson = "{\"LookbackBars\":20}",
                Status = StrategyStatus.Paused
            });

            await writeCtx.SaveChangesAsync();
        }

        // Act
        await using var readCtx = CreateReadContext();
        var activeStrategies = await readCtx.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active)
            .ToListAsync();

        var pausedStrategies = await readCtx.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Paused)
            .ToListAsync();

        // Assert
        Assert.Contains(activeStrategies, s => s.Name == "Active Strategy A");
        Assert.Contains(pausedStrategies, s => s.Name == "Paused Strategy B");
        Assert.DoesNotContain(activeStrategies, s => s.Name == "Paused Strategy B");
    }
}
