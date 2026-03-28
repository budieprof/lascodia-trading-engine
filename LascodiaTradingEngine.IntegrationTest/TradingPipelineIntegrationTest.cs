using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

/// <summary>
/// End-to-end pipeline test: Signal → Order → Execution Report (Fill) → Position.
/// Verifies the complete trading lifecycle persists correctly without handlers/events,
/// testing the database layer and entity relationships.
/// </summary>
public class TradingPipelineIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public TradingPipelineIntegrationTest(PostgresFixture fixture)
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

    private async Task EnsureMigrated()
    {
        await using var context = CreateWriteContext();
        await context.Database.MigrateAsync();
    }

    [Fact]
    public async Task FullPipeline_Signal_To_Position_ShouldPersistCorrectly()
    {
        await EnsureMigrated();

        // ── 1. Seed prerequisites ──────────────────────────────────────
        long accountId, strategyId;
        await using (var ctx = CreateWriteContext())
        {
            var account = new TradingAccount
            {
                AccountId = "PIPELINE-TEST-" + Guid.NewGuid().ToString("N")[..8],
                AccountName = "Pipeline Test Account",
                BrokerServer = "test-server",
                BrokerName = "TestBroker",
                AccountType = AccountType.Demo,
                Currency = "USD",
                Balance = 10000m,
                Equity = 10000m,
                MarginUsed = 0m,
                MarginAvailable = 10000m,
                IsActive = true,
                IsPaper = true
            };
            ctx.Set<TradingAccount>().Add(account);

            var strategy = new Strategy
            {
                Name = "Pipeline Test Strategy",
                Description = "E2E integration test",
                StrategyType = StrategyType.MovingAverageCrossover,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = "{}",
                Status = StrategyStatus.Active
            };
            ctx.Set<Strategy>().Add(strategy);
            await ctx.SaveChangesAsync();

            accountId = account.Id;
            strategyId = strategy.Id;
        }

        // ── 2. Create TradeSignal (Pending) ────────────────────────────
        long signalId;
        await using (var ctx = CreateWriteContext())
        {
            var signal = new TradeSignal
            {
                StrategyId = strategyId,
                Symbol = "EURUSD",
                Direction = TradeDirection.Buy,
                EntryPrice = 1.10500m,
                StopLoss = 1.10000m,
                TakeProfit = 1.11000m,
                SuggestedLotSize = 0.10m,
                Confidence = 0.85m,
                Status = TradeSignalStatus.Pending,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30)
            };
            ctx.Set<TradeSignal>().Add(signal);
            await ctx.SaveChangesAsync();
            signalId = signal.Id;
        }

        // Verify signal persisted
        await using (var ctx = CreateWriteContext())
        {
            var signal = await ctx.Set<TradeSignal>().FindAsync(signalId);
            Assert.NotNull(signal);
            Assert.Equal(TradeSignalStatus.Pending, signal!.Status);
            Assert.Equal("EURUSD", signal.Symbol);
        }

        // ── 3. Approve signal ──────────────────────────────────────────
        await using (var ctx = CreateWriteContext())
        {
            var signal = await ctx.Set<TradeSignal>().FindAsync(signalId);
            Assert.NotNull(signal);
            signal!.Status = TradeSignalStatus.Approved;
            await ctx.SaveChangesAsync();
        }

        // ── 4. Create Order (Pending) from signal ──────────────────────
        long orderId;
        await using (var ctx = CreateWriteContext())
        {
            var order = new Order
            {
                TradingAccountId = accountId,
                StrategyId = strategyId,
                TradeSignalId = signalId,
                Symbol = "EURUSD",
                OrderType = OrderType.Buy,
                ExecutionType = ExecutionType.Market,
                Quantity = 0.10m,
                Price = 1.10500m,
                StopLoss = 1.10000m,
                TakeProfit = 1.11000m,
                Status = OrderStatus.Pending,
                IsPaper = true
            };
            ctx.Set<Order>().Add(order);
            await ctx.SaveChangesAsync();
            orderId = order.Id;
        }

        // ── 5. Submit to broker (Pending → Submitted) ──────────────────
        await using (var ctx = CreateWriteContext())
        {
            var order = await ctx.Set<Order>().FindAsync(orderId);
            Assert.NotNull(order);
            order!.Status = OrderStatus.Submitted;
            order.BrokerOrderId = "MT5-12345";
            await ctx.SaveChangesAsync();
        }

        // ── 6. Execution report: Fill ──────────────────────────────────
        await using (var ctx = CreateWriteContext())
        {
            var order = await ctx.Set<Order>().FindAsync(orderId);
            Assert.NotNull(order);
            order!.Status = OrderStatus.Filled;
            order.FilledPrice = 1.10510m;
            order.FilledQuantity = 0.10m;
            order.FilledAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        // Verify order is Filled
        await using (var ctx = CreateWriteContext())
        {
            var order = await ctx.Set<Order>().FindAsync(orderId);
            Assert.NotNull(order);
            Assert.Equal(OrderStatus.Filled, order!.Status);
            Assert.Equal(1.10510m, order.FilledPrice);
            Assert.Equal(0.10m, order.FilledQuantity);
            Assert.NotNull(order.FilledAt);
        }

        // ── 7. Open Position from filled order ─────────────────────────
        long positionId;
        await using (var ctx = CreateWriteContext())
        {
            var position = new Position
            {
                OpenOrderId = orderId,
                Symbol = "EURUSD",
                Direction = PositionDirection.Long,
                OpenLots = 0.10m,
                AverageEntryPrice = 1.10510m,
                StopLoss = 1.10000m,
                TakeProfit = 1.11000m,
                Status = PositionStatus.Open,
                OpenedAt = DateTime.UtcNow,
                IsPaper = true
            };
            ctx.Set<Position>().Add(position);
            await ctx.SaveChangesAsync();
            positionId = position.Id;
        }

        // Verify position is Open
        await using (var ctx = CreateWriteContext())
        {
            var position = await ctx.Set<Position>().FindAsync(positionId);
            Assert.NotNull(position);
            Assert.Equal(PositionStatus.Open, position!.Status);
            Assert.Equal("EURUSD", position.Symbol);
            Assert.Equal(0.10m, position.OpenLots);
            Assert.Equal(1.10510m, position.AverageEntryPrice);
        }

        // ── 8. Close Position (TP hit) ─────────────────────────────────
        await using (var ctx = CreateWriteContext())
        {
            var position = await ctx.Set<Position>().FindAsync(positionId);
            Assert.NotNull(position);
            position!.Status = PositionStatus.Closed;
            position.ClosedAt = DateTime.UtcNow;
            // P&L: (1.11000 - 1.10510) * 0.10 * 100000 = 49.00
            position.RealizedPnL = (1.11000m - 1.10510m) * 0.10m * 100_000m;
            await ctx.SaveChangesAsync();
        }

        // ── 9. Verify complete pipeline state ──────────────────────────
        await using (var ctx = CreateWriteContext())
        {
            var signal = await ctx.Set<TradeSignal>().FindAsync(signalId);
            var order = await ctx.Set<Order>().FindAsync(orderId);
            var position = await ctx.Set<Position>().FindAsync(positionId);

            // Signal approved
            Assert.Equal(TradeSignalStatus.Approved, signal!.Status);

            // Order filled with correct linkage
            Assert.Equal(OrderStatus.Filled, order!.Status);
            Assert.Equal(signalId, order.TradeSignalId);
            Assert.Equal(accountId, order.TradingAccountId);

            // Position closed with P&L
            Assert.Equal(PositionStatus.Closed, position!.Status);
            Assert.NotNull(position.ClosedAt);
            Assert.Equal(49.00m, position.RealizedPnL);
            Assert.Equal(orderId, position.OpenOrderId);
        }
    }

    [Fact]
    public async Task PartialFill_ShouldTrackFillRate()
    {
        await EnsureMigrated();

        // Seed
        long accountId;
        await using (var ctx = CreateWriteContext())
        {
            var account = new TradingAccount
            {
                AccountId = "PARTIAL-TEST-" + Guid.NewGuid().ToString("N")[..8],
                AccountName = "Partial Fill Test",
                BrokerServer = "test-server",
                BrokerName = "TestBroker",
                AccountType = AccountType.Demo,
                Currency = "USD",
                Balance = 10000m,
                Equity = 10000m,
                MarginUsed = 0m,
                MarginAvailable = 10000m,
                IsActive = true,
                IsPaper = true
            };
            ctx.Set<TradingAccount>().Add(account);
            await ctx.SaveChangesAsync();
            accountId = account.Id;
        }

        // Create order with 1.0 lot, fill only 0.6
        long orderId;
        await using (var ctx = CreateWriteContext())
        {
            var order = new Order
            {
                TradingAccountId = accountId,
                Symbol = "GBPUSD",
                OrderType = OrderType.Buy,
                ExecutionType = ExecutionType.Market,
                Quantity = 1.0m,
                Price = 0m,
                Status = OrderStatus.Submitted,
                IsPaper = true
            };
            ctx.Set<Order>().Add(order);
            await ctx.SaveChangesAsync();
            orderId = order.Id;
        }

        // Partial fill
        await using (var ctx = CreateWriteContext())
        {
            var order = await ctx.Set<Order>().FindAsync(orderId);
            Assert.NotNull(order);
            order!.Status = OrderStatus.Filled;
            order.FilledPrice = 1.26500m;
            order.FilledQuantity = 0.60m;
            order.FilledAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        // Verify fill rate
        await using (var ctx = CreateWriteContext())
        {
            var order = await ctx.Set<Order>().FindAsync(orderId);
            Assert.NotNull(order);
            var fillRate = order!.FilledQuantity!.Value / order.Quantity;
            Assert.Equal(0.60m, fillRate);
            Assert.True(fillRate < 1.0m, "Should be a partial fill");
        }
    }

    [Fact]
    public async Task SoftDelete_Position_ShouldBeExcludedFromQueries()
    {
        await EnsureMigrated();

        long accountId;
        await using (var ctx = CreateWriteContext())
        {
            var account = new TradingAccount
            {
                AccountId = "SOFTDEL-TEST-" + Guid.NewGuid().ToString("N")[..8],
                AccountName = "Soft Delete Test",
                BrokerServer = "test-server",
                BrokerName = "TestBroker",
                AccountType = AccountType.Demo,
                Currency = "USD",
                Balance = 10000m,
                Equity = 10000m,
                MarginUsed = 0m,
                MarginAvailable = 10000m,
                IsActive = true,
                IsPaper = true
            };
            ctx.Set<TradingAccount>().Add(account);
            await ctx.SaveChangesAsync();
            accountId = account.Id;
        }

        // Create and soft-delete a position
        long positionId;
        await using (var ctx = CreateWriteContext())
        {
            var position = new Position
            {
                Symbol = "USDJPY",
                Direction = PositionDirection.Short,
                OpenLots = 0.05m,
                AverageEntryPrice = 150.000m,
                Status = PositionStatus.Closed,
                OpenedAt = DateTime.UtcNow.AddHours(-1),
                ClosedAt = DateTime.UtcNow,
                IsPaper = true
            };
            ctx.Set<Position>().Add(position);
            await ctx.SaveChangesAsync();
            positionId = position.Id;

            // Soft delete
            position.IsDeleted = true;
            await ctx.SaveChangesAsync();
        }

        // Verify soft-deleted position excluded by global query filter
        await using (var ctx = CreateWriteContext())
        {
            var visible = await ctx.Set<Position>()
                .Where(p => p.Id == positionId)
                .FirstOrDefaultAsync();
            Assert.Null(visible); // Global filter excludes IsDeleted=true

            // But IgnoreQueryFilters finds it
            var hidden = await ctx.Set<Position>()
                .IgnoreQueryFilters()
                .Where(p => p.Id == positionId)
                .FirstOrDefaultAsync();
            Assert.NotNull(hidden);
            Assert.True(hidden!.IsDeleted);
        }
    }
}
