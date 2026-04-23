using Microsoft.EntityFrameworkCore;
using Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessReconciliation;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.ExpertAdvisor;

public class ProcessReconciliationCommandTest
{
    [Fact]
    public async Task Handle_WhenUnauthorized_DoesNotPersistRun()
    {
        await using var db = CreateDbContext();
        var handler = new ProcessReconciliationCommandHandler(db, OwnerGuard(false).Object);

        var result = await handler.Handle(new ProcessReconciliationCommand
        {
            InstanceId = "EA-001",
        }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-403", result.responseCode);
        Assert.Empty(db.ReconciliationRuns);
    }

    [Fact]
    public async Task Handle_WithOwnedInstance_FiltersOrdersToTradingAccountAndPersistsRun()
    {
        await using var db = CreateDbContext();
        db.EAInstances.Add(new EAInstance
        {
            Id = 1,
            InstanceId = "EA-001",
            TradingAccountId = 42,
            Symbols = "EURUSD",
            ChartSymbol = "EURUSD",
            ChartTimeframe = "M15",
            EAVersion = "1.0",
        });
        db.Orders.AddRange(
            new Order
            {
                Id = 1,
                TradingAccountId = 42,
                Symbol = "EURUSD",
                Status = OrderStatus.Submitted,
                BrokerOrderId = "100",
            },
            new Order
            {
                Id = 2,
                TradingAccountId = 999,
                Symbol = "EURUSD",
                Status = OrderStatus.Submitted,
                BrokerOrderId = "other-account",
            });
        await db.SaveChangesAsync();

        var handler = new ProcessReconciliationCommandHandler(db, OwnerGuard(true).Object);

        var result = await handler.Handle(new ProcessReconciliationCommand
        {
            InstanceId = "EA-001",
            BrokerOrders =
            [
                new BrokerOrderItem
                {
                    Ticket = 100,
                    Symbol = "EURUSD",
                    OrderType = "Buy",
                    Volume = 1m,
                    Price = 1.1m,
                }
            ],
        }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal(0, result.data!.OrphanedEngineOrders);
        Assert.Equal(0, result.data.UnknownBrokerOrders);
        Assert.Single(db.ReconciliationRuns);
    }

    [Fact]
    public async Task Handle_RejectsBrokerItemsForUnownedSymbols()
    {
        await using var db = CreateDbContext();
        db.EAInstances.Add(new EAInstance
        {
            Id = 1,
            InstanceId = "EA-001",
            TradingAccountId = 42,
            Symbols = "EURUSD",
            ChartSymbol = "EURUSD",
            ChartTimeframe = "M15",
            EAVersion = "1.0",
        });
        await db.SaveChangesAsync();

        var handler = new ProcessReconciliationCommandHandler(db, OwnerGuard(true).Object);

        var result = await handler.Handle(new ProcessReconciliationCommand
        {
            InstanceId = "EA-001",
            BrokerPositions =
            [
                new BrokerPositionItem
                {
                    Ticket = 200,
                    Symbol = "GBPUSD",
                    Direction = "Long",
                    Volume = 1m,
                    OpenPrice = 1.2m,
                }
            ],
        }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-403", result.responseCode);
        Assert.Empty(db.ReconciliationRuns);
    }

    private static TestDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Mock<IEAOwnershipGuard> OwnerGuard(bool isOwner)
    {
        var mock = new Mock<IEAOwnershipGuard>();
        mock.Setup(g => g.IsOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(isOwner);
        return mock;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : DbContext(options), IWriteApplicationDbContext
    {
        public DbSet<EAInstance> EAInstances => Set<EAInstance>();
        public DbSet<Position> Positions => Set<Position>();
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();

        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EAInstance>().HasKey(x => x.Id);
            modelBuilder.Entity<EAInstance>().Ignore(x => x.TradingAccount);
            modelBuilder.Entity<Position>().HasKey(x => x.Id);
            modelBuilder.Entity<Position>().Ignore(x => x.ScaleOrders);
            modelBuilder.Entity<Order>().HasKey(x => x.Id);
            modelBuilder.Entity<Order>().Ignore(x => x.TradeSignal);
            modelBuilder.Entity<Order>().Ignore(x => x.Strategy);
            modelBuilder.Entity<Order>().Ignore(x => x.TradingAccount);
            modelBuilder.Entity<Order>().Ignore(x => x.ExecutionQualityLog);
            modelBuilder.Entity<Order>().Ignore(x => x.PositionScaleOrders);
            modelBuilder.Entity<ReconciliationRun>().HasKey(x => x.Id);
        }
    }
}
