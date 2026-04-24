using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.ActivateTradingAccount;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.TradingAccounts;

public sealed class ActivateTradingAccountCommandTest : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestTradingAccountDbContext _dbContext;
    private readonly ActivateTradingAccountCommandHandler _handler;

    public ActivateTradingAccountCommandTest()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbContext = new TestTradingAccountDbContext(
            new DbContextOptionsBuilder<TestTradingAccountDbContext>()
                .UseSqlite(_connection)
                .Options);

        _dbContext.Database.EnsureCreated();
        _handler = new ActivateTradingAccountCommandHandler(_dbContext);
    }

    [Fact]
    public async Task Handle_ActivatesTarget_AndDeactivatesExistingActiveAccount()
    {
        _dbContext.AddRange(
            new TradingAccount
            {
                Id = 1,
                AccountId = "ACC-1",
                BrokerServer = "Broker",
                IsActive = true,
                IsDeleted = false
            },
            new TradingAccount
            {
                Id = 2,
                AccountId = "ACC-2",
                BrokerServer = "Broker",
                IsActive = false,
                IsDeleted = false
            });
        await _dbContext.SaveChangesAsync();

        var result = await _handler.Handle(new ActivateTradingAccountCommand { Id = 2 }, CancellationToken.None);

        Assert.True(result.status);

        _dbContext.ChangeTracker.Clear();
        var accounts = await _dbContext.Set<TradingAccount>()
            .IgnoreQueryFilters()
            .OrderBy(a => a.Id)
            .ToListAsync();

        Assert.False(accounts.Single(a => a.Id == 1).IsActive);
        Assert.True(accounts.Single(a => a.Id == 2).IsActive);
        Assert.Single(accounts, a => a.IsActive && !a.IsDeleted);
    }

    [Fact]
    public async Task Handle_ReturnsNotFound_WhenTargetAccountDoesNotExist()
    {
        var result = await _handler.Handle(new ActivateTradingAccountCommand { Id = 999 }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private sealed class TestTradingAccountDbContext(DbContextOptions<TestTradingAccountDbContext> options)
        : DbContext(options), IWriteApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TradingAccount>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.HasIndex(x => x.IsActive)
                    .HasDatabaseName("IX_TradingAccount_IsActive_SingleTrue")
                    .IsUnique()
                    .HasFilter("\"IsActive\" = true AND \"IsDeleted\" = false");
                builder.Ignore(x => x.Orders);
                builder.Ignore(x => x.EAInstances);
            });
        }
    }
}
