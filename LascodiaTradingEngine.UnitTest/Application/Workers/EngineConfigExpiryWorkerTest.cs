using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class EngineConfigExpiryWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_ExpiresOnlyManagedEphemeralKeys()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var contextBundle = await NewContextAsync();
        var ctx = contextBundle.Ctx;

        ctx.EngineConfigs.AddRange(
            NewConfig(1, "MLCooldown:EURUSD:H1:ExpiresAt", now.AddMinutes(-5).ToString("O")),
            NewConfig(2, "MLDrift:EURUSD:H1:AdwinDriftDetected", now.AddMinutes(-1).ToString("O")),
            NewConfig(3, "MLDegradation:EURUSD:DetectedAt", now.AddHours(-3).ToString("O")),
            NewConfig(4, "MLDriftAgreement:EURUSD:H1:LastChecked", now.AddMinutes(-30).ToString("O")),
            NewConfig(5, "MLCooldown:GBPUSD:H1:ExpiresAt", now.AddHours(2).ToString("O")));
        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        ctx.ChangeTracker.Clear();
        var rows = await ctx.EngineConfigs
            .IgnoreQueryFilters()
            .OrderBy(x => x.Id)
            .ToListAsync();

        Assert.Equal(2, result.ExpiredEntryCount);
        Assert.Equal(0, result.StaleMetricsBlockCount);
        Assert.Equal(0, result.StaleMetricsEntryCount);
        Assert.True(rows[0].IsDeleted);
        Assert.True(rows[1].IsDeleted);
        Assert.False(rows[2].IsDeleted);
        Assert.False(rows[3].IsDeleted);
        Assert.False(rows[4].IsDeleted);
    }

    [Fact]
    public async Task RunCycleAsync_StaleMetricsBlockIsPruned_WithoutTouchingNonExpiryTimestampState()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var contextBundle = await NewContextAsync();
        var ctx = contextBundle.Ctx;

        ctx.EngineConfigs.AddRange(
            NewConfig(1, "MLMetrics:EURUSD:H1:LastUpdated", now.AddHours(-2).ToString("O")),
            NewConfig(2, "MLMetrics:EURUSD:H1:Accuracy", "0.54", ConfigDataType.Decimal),
            NewConfig(3, "MLMetrics:EURUSD:H1:SampleCount", "42", ConfigDataType.Int),
            NewConfig(4, "MLDriftAgreement:EURUSD:H1:LastChecked", now.AddHours(-2).ToString("O")),
            NewConfig(5, "MLMetrics:GBPUSD:H1:LastUpdated", now.AddMinutes(-20).ToString("O")),
            NewConfig(6, "MLMetrics:GBPUSD:H1:Accuracy", "0.61", ConfigDataType.Decimal));
        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        ctx.ChangeTracker.Clear();
        var rows = await ctx.EngineConfigs
            .IgnoreQueryFilters()
            .OrderBy(x => x.Id)
            .ToListAsync();

        Assert.Equal(0, result.ExpiredEntryCount);
        Assert.Equal(1, result.StaleMetricsBlockCount);
        Assert.Equal(3, result.StaleMetricsEntryCount);
        Assert.True(rows[0].IsDeleted);
        Assert.True(rows[1].IsDeleted);
        Assert.True(rows[2].IsDeleted);
        Assert.False(rows[3].IsDeleted);
        Assert.False(rows[4].IsDeleted);
        Assert.False(rows[5].IsDeleted);
    }

    [Fact]
    public async Task RunCycleAsync_InvalidPollIntervalIsClamped()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var contextBundle = await NewContextAsync();
        var ctx = contextBundle.Ctx;

        ctx.EngineConfigs.Add(NewConfig(1, "EngineConfig:ExpiryPollIntervalSeconds", "-15", ConfigDataType.Int));
        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(1), result.Settings.PollInterval);
        Assert.Equal(0, result.ExpiredEntryCount);
        Assert.Equal(0, result.StaleMetricsEntryCount);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutMutatingState()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var contextBundle = await NewContextAsync();
        var ctx = contextBundle.Ctx;

        ctx.EngineConfigs.Add(NewConfig(1, "MLCooldown:EURUSD:H1:ExpiresAt", now.AddMinutes(-10).ToString("O")));
        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider, timeProvider, new TestDistributedLock(lockAvailable: false));

        var result = await worker.RunCycleAsync(CancellationToken.None);
        ctx.ChangeTracker.Clear();
        var row = await ctx.EngineConfigs.IgnoreQueryFilters().SingleAsync();

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.False(row.IsDeleted);
    }

    [Fact]
    public void CalculateDelay_UsesExponentialBackoffWithCeiling()
    {
        Assert.Equal(
            TimeSpan.FromHours(6),
            EngineConfigExpiryWorker.CalculateDelay(TimeSpan.FromHours(6), consecutiveFailures: 0));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            EngineConfigExpiryWorker.CalculateDelay(TimeSpan.FromHours(6), consecutiveFailures: 1));
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            EngineConfigExpiryWorker.CalculateDelay(TimeSpan.FromHours(6), consecutiveFailures: 2));
        Assert.Equal(
            TimeSpan.FromMinutes(30),
            EngineConfigExpiryWorker.CalculateDelay(TimeSpan.FromHours(6), consecutiveFailures: 10));
    }

    private static EngineConfigExpiryWorker CreateWorker(
        ServiceProvider provider,
        TimeProvider timeProvider,
        IDistributedLock? distributedLock = null)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EngineConfigExpiryWorker>.Instance,
            metrics: null,
            timeProvider: timeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

    private static ServiceProvider BuildProvider(EngineConfigExpiryTestContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWriteApplicationDbContext>(context);
        return services.BuildServiceProvider();
    }

    private static EngineConfig NewConfig(
        long id,
        string key,
        string value,
        ConfigDataType dataType = ConfigDataType.String)
        => new()
        {
            Id = id,
            Key = key,
            Value = value,
            DataType = dataType,
            IsHotReloadable = true,
            LastUpdatedAt = DateTime.UtcNow
        };

    private static async Task<EngineConfigExpiryContextBundle> NewContextAsync()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        await conn.OpenAsync();

        var options = new DbContextOptionsBuilder<EngineConfigExpiryTestContext>()
            .UseSqlite(conn)
            .Options;

        var ctx = new EngineConfigExpiryTestContext(options);
        await ctx.Database.EnsureCreatedAsync();
        return new EngineConfigExpiryContextBundle(ctx, conn);
    }

    private sealed record EngineConfigExpiryContextBundle(
        EngineConfigExpiryTestContext Ctx,
        SqliteConnection Conn) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Ctx.DisposeAsync();
            await Conn.DisposeAsync();
        }
    }

    private sealed class EngineConfigExpiryTestContext(DbContextOptions<EngineConfigExpiryTestContext> options)
        : DbContext(options), IWriteApplicationDbContext
    {
        public DbSet<EngineConfig> EngineConfigs => Set<EngineConfig>();

        public DbContext GetDbContext() => this;

        public new Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => base.SaveChangesAsync(cancellationToken);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EngineConfig>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Key).IsRequired();
                builder.Property(x => x.Value).IsRequired();
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.HasIndex(x => x.Key).IsUnique();
            });
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
