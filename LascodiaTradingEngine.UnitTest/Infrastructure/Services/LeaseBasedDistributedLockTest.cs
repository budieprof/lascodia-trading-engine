using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Infrastructure.Services;

public sealed class LeaseBasedDistributedLockTest
{
    [Fact]
    public async Task TryAcquireAsync_ReturnsHandle_WhenNoLeaseExists()
    {
        using var harness = new Harness();

        await using var handle = await harness.Lock.TryAcquireAsync("key:a");

        Assert.NotNull(handle);
        var lease = await harness.LoadLeaseAsync("key:a");
        Assert.NotNull(lease);
        Assert.False(lease!.IsDeleted);
    }

    [Fact]
    public async Task TryAcquireAsync_ReturnsNull_WhenLeaseHeldByAnother()
    {
        using var harness = new Harness();

        await using var first = await harness.Lock.TryAcquireAsync("key:a");
        Assert.NotNull(first);

        var second = await harness.Lock.TryAcquireAsync("key:a");

        Assert.Null(second);
    }

    [Fact]
    public async Task TryAcquireAsync_StealsExpiredLease()
    {
        using var harness = new Harness();
        harness.SeedLease("key:a", expiresAtUtc: DateTime.UtcNow.AddSeconds(-30));

        await using var handle = await harness.Lock.TryAcquireAsync("key:a");

        Assert.NotNull(handle);
        var lease = await harness.LoadLeaseAsync("key:a");
        Assert.NotNull(lease);
        Assert.True(lease!.ExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task TryAcquireAsync_StealsSoftDeletedLease()
    {
        using var harness = new Harness();
        harness.SeedLease("key:a",
            expiresAtUtc: DateTime.UtcNow.AddMinutes(10),
            isDeleted: true);

        await using var handle = await harness.Lock.TryAcquireAsync("key:a");

        Assert.NotNull(handle);
        var lease = await harness.LoadLeaseAsync("key:a", includeDeleted: true);
        Assert.NotNull(lease);
        Assert.False(lease!.IsDeleted);
    }

    [Fact]
    public async Task DisposeAsync_DeletesLeaseRow()
    {
        using var harness = new Harness();

        var handle = await harness.Lock.TryAcquireAsync("key:a");
        Assert.NotNull(handle);

        await handle!.DisposeAsync();

        var lease = await harness.LoadLeaseAsync("key:a", includeDeleted: true);
        Assert.Null(lease);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        using var harness = new Harness();

        var handle = await harness.Lock.TryAcquireAsync("key:a");
        Assert.NotNull(handle);

        await handle!.DisposeAsync();
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_AfterRelease_OtherCallerCanAcquire()
    {
        using var harness = new Harness();

        var first = await harness.Lock.TryAcquireAsync("key:a");
        Assert.NotNull(first);
        await first!.DisposeAsync();

        await using var second = await harness.Lock.TryAcquireAsync("key:a");
        Assert.NotNull(second);
    }

    [Fact]
    public async Task TwoConcurrentAcquires_OnlyOneWins()
    {
        using var harness = new Harness();

        var t1 = harness.Lock.TryAcquireAsync("key:a");
        var t2 = harness.Lock.TryAcquireAsync("key:a");

        var results = await Task.WhenAll(t1, t2);

        int winners = results.Count(r => r is not null);
        Assert.Equal(1, winners);

        foreach (var handle in results)
        {
            if (handle is not null) await handle.DisposeAsync();
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;

        public LeaseBasedDistributedLock Lock { get; }

        public Harness()
        {
            _connection = new SqliteConnection("Filename=:memory:");
            _connection.Open();

            var services = new ServiceCollection();
            services.AddDbContext<LeaseTestContext>(options => options.UseSqlite(_connection));
            services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<LeaseTestContext>());

            _provider = services.BuildServiceProvider();

            using (var scope = _provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LeaseTestContext>();
                db.Database.EnsureCreated();
            }

            Lock = new LeaseBasedDistributedLock(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<LeaseBasedDistributedLock>.Instance,
                timeProvider: TimeProvider.System,
                leaseDuration: TimeSpan.FromMinutes(1),
                heartbeatInterval: TimeSpan.FromMinutes(10)); // long enough not to fire during the test
        }

        public void SeedLease(string key, DateTime expiresAtUtc, bool isDeleted = false)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LeaseTestContext>();
            db.Set<DistributedLockLease>().Add(new DistributedLockLease
            {
                Key = key,
                OwnerId = Guid.NewGuid(),
                AcquiredAtUtc = DateTime.UtcNow.AddMinutes(-1),
                ExpiresAtUtc = expiresAtUtc,
                IsDeleted = isDeleted,
            });
            db.SaveChanges();
        }

        public async Task<DistributedLockLease?> LoadLeaseAsync(string key, bool includeDeleted = false)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LeaseTestContext>();
            IQueryable<DistributedLockLease> query = db.Set<DistributedLockLease>().AsNoTracking();
            if (includeDeleted)
                query = query.IgnoreQueryFilters();
            return await query.SingleOrDefaultAsync(l => l.Key == key);
        }

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class LeaseTestContext(DbContextOptions<LeaseTestContext> options)
        : DbContext(options), IWriteApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DistributedLockLease>(builder =>
            {
                builder.HasKey(l => l.Id);
                builder.Property(l => l.Id).ValueGeneratedOnAdd();
                builder.Property(l => l.Key).IsRequired().HasMaxLength(128);
                builder.HasIndex(l => l.Key).IsUnique();
                builder.HasQueryFilter(l => !l.IsDeleted);
            });
        }
    }
}
