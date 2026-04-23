using Microsoft.EntityFrameworkCore;
using Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveBrokerAccountSnapshot;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.ExpertAdvisor;

public class ReceiveBrokerAccountSnapshotCommandTest
{
    [Fact]
    public async Task Handle_WithOwnedActiveInstance_PersistsSnapshotAndRefreshesHeartbeat()
    {
        await using var db = CreateDbContext();
        db.EAInstances.Add(new EAInstance
        {
            Id = 1,
            InstanceId = "EA-001",
            TradingAccountId = 42,
            Status = EAInstanceStatus.Active,
            LastHeartbeat = DateTime.UtcNow.AddMinutes(-10),
            Symbols = "EURUSD",
            ChartSymbol = "EURUSD",
            ChartTimeframe = "M15",
            EAVersion = "1.0",
        });
        await db.SaveChangesAsync();

        var handler = new ReceiveBrokerAccountSnapshotCommandHandler(db, OwnerGuard(true).Object);

        var result = await handler.Handle(new ReceiveBrokerAccountSnapshotCommand
        {
            InstanceId = "EA-001",
            Balance = 1000m,
            Equity = 997.5m,
            MarginUsed = 12.25m,
            FreeMargin = 985.25m,
            ReportedAt = DateTime.UtcNow.AddMinutes(-1),
        }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);

        var snapshot = await db.BrokerAccountSnapshots.SingleAsync();
        Assert.Equal(42, snapshot.TradingAccountId);
        Assert.Equal("EA-001", snapshot.InstanceId);
        Assert.Equal(997.5m, snapshot.Equity);
        Assert.True(snapshot.ReportedAt <= DateTime.UtcNow);
        Assert.True(db.EAInstances.Single().LastHeartbeat > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Handle_WhenUnauthorized_DoesNotPersistSnapshot()
    {
        await using var db = CreateDbContext();
        var handler = new ReceiveBrokerAccountSnapshotCommandHandler(db, OwnerGuard(false).Object);

        var result = await handler.Handle(new ReceiveBrokerAccountSnapshotCommand
        {
            InstanceId = "EA-001",
            Balance = 1000m,
            Equity = 1000m,
            MarginUsed = 0m,
            FreeMargin = 1000m,
        }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-403", result.responseCode);
        Assert.Empty(db.BrokerAccountSnapshots);
    }

    [Fact]
    public void Validator_RejectsNegativeBrokerNumbers()
    {
        var validator = new ReceiveBrokerAccountSnapshotCommandValidator();

        var result = validator.Validate(new ReceiveBrokerAccountSnapshotCommand
        {
            InstanceId = "EA-001",
            Balance = 1000m,
            Equity = -1m,
            MarginUsed = 0m,
            FreeMargin = 1000m,
        });

        Assert.False(result.IsValid);
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
        public DbSet<BrokerAccountSnapshot> BrokerAccountSnapshots => Set<BrokerAccountSnapshot>();
        public DbSet<TradingAccount> TradingAccounts => Set<TradingAccount>();

        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EAInstance>().HasKey(x => x.Id);
            modelBuilder.Entity<EAInstance>().Ignore(x => x.TradingAccount);
            modelBuilder.Entity<BrokerAccountSnapshot>().HasKey(x => x.Id);
            modelBuilder.Entity<TradingAccount>().HasKey(x => x.Id);
            modelBuilder.Entity<TradingAccount>().Ignore(x => x.Orders);
            modelBuilder.Entity<TradingAccount>().Ignore(x => x.EAInstances);
        }
    }
}
