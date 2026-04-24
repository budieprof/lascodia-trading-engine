using System.Diagnostics.Metrics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class DeadLetterCleanupWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_DeletesExpiredResolvedAndSoftDeletedRows_ButPreservesExpiredUnresolvedRows()
    {
        var now = new DateTime(2026, 04, 24, 0, 0, 0, DateTimeKind.Utc);
        var timeProvider = new DeadLetterCleanupFixedTimeProvider(now);
        var (ctx, conn) = NewCtx();
        using var _ctx = ctx;
        using var _conn = conn;

        ctx.Set<DeadLetterEvent>().AddRange(
            new DeadLetterEvent
            {
                Id = 1,
                HandlerName = "ResolvedHandler",
                EventType = "ResolvedEvent",
                EventPayload = "{}",
                ErrorMessage = "boom",
                DeadLetteredAt = now.AddDays(-45),
                IsResolved = true
            },
            new DeadLetterEvent
            {
                Id = 2,
                HandlerName = "UnresolvedHandler",
                EventType = "UnresolvedEvent",
                EventPayload = "{}",
                ErrorMessage = "boom",
                DeadLetteredAt = now.AddDays(-45),
                IsResolved = false
            },
            new DeadLetterEvent
            {
                Id = 3,
                HandlerName = "DeletedHandler",
                EventType = "DeletedEvent",
                EventPayload = "{}",
                ErrorMessage = "boom",
                DeadLetteredAt = now.AddDays(-45),
                IsDeleted = true
            },
            new DeadLetterEvent
            {
                Id = 4,
                HandlerName = "FreshHandler",
                EventType = "FreshEvent",
                EventPayload = "{}",
                ErrorMessage = "boom",
                DeadLetteredAt = now.AddDays(-5),
                IsResolved = true
            });
        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        using var scope = provider.CreateScope();
        var worker = CreateWorker(provider, timeProvider: timeProvider);
        var config = DeadLetterCleanupWorker.NormalizeConfig(
            intervalHours: 24,
            retentionDays: 30,
            batchSize: 100,
            lockTimeoutSeconds: 5);

        var result = await worker.RunCycleAsync(scope, config, CancellationToken.None);

        Assert.Equal(2, result.TotalDeleted);
        Assert.Equal(1, result.DeletedResolvedCount);
        Assert.Equal(1, result.DeletedSoftDeletedCount);
        Assert.Equal(1, result.ExpiredUnresolvedCount);
        Assert.Equal(0, result.RemainingEligibleDeletionCount);
        Assert.Equal(1, result.BatchesProcessed);
        Assert.False(result.HitBatchLimit);

        var remainingIds = await ctx.Set<DeadLetterEvent>()
            .IgnoreQueryFilters()
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync();

        Assert.Equal([2L, 4L], remainingIds);
    }

    [Fact]
    public async Task RunCycleAsync_BatchesDeletesAcrossMultiplePasses()
    {
        var now = new DateTime(2026, 04, 24, 0, 0, 0, DateTimeKind.Utc);
        var timeProvider = new DeadLetterCleanupFixedTimeProvider(now);
        var (ctx, conn) = NewCtx();
        using var _ctx = ctx;
        using var _conn = conn;

        for (int i = 0; i < 25; i++)
        {
            ctx.Set<DeadLetterEvent>().Add(new DeadLetterEvent
            {
                Id = i + 1,
                HandlerName = $"Handler{i}",
                EventType = $"Event{i}",
                EventPayload = "{}",
                ErrorMessage = "boom",
                DeadLetteredAt = now.AddDays(-60 - i),
                IsResolved = true
            });
        }

        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        using var scope = provider.CreateScope();
        var worker = CreateWorker(provider, timeProvider: timeProvider);
        var config = DeadLetterCleanupWorker.NormalizeConfig(
            intervalHours: 24,
            retentionDays: 30,
            batchSize: 10,
            lockTimeoutSeconds: 5);

        var result = await worker.RunCycleAsync(scope, config, CancellationToken.None);

        Assert.Equal(25, result.TotalDeleted);
        Assert.Equal(25, result.DeletedResolvedCount);
        Assert.Equal(0, result.DeletedSoftDeletedCount);
        Assert.Equal(0, result.RemainingEligibleDeletionCount);
        Assert.Equal(3, result.BatchesProcessed);
        Assert.False(result.HitBatchLimit);
        Assert.Empty(await ctx.Set<DeadLetterEvent>().IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_WhenPerCycleBatchCapIsReached_ReportsRemainingEligibleBacklog()
    {
        var now = new DateTime(2026, 04, 24, 0, 0, 0, DateTimeKind.Utc);
        var timeProvider = new DeadLetterCleanupFixedTimeProvider(now);
        var (ctx, conn) = NewCtx();
        using var _ctx = ctx;
        using var _conn = conn;

        for (int i = 0; i < DeadLetterCleanupWorker.MaxBatchesPerCycle + 1; i++)
        {
            ctx.Set<DeadLetterEvent>().Add(new DeadLetterEvent
            {
                Id = i + 1,
                HandlerName = $"ResolvedHandler{i}",
                EventType = $"ResolvedEvent{i}",
                EventPayload = "{}",
                ErrorMessage = "boom",
                DeadLetteredAt = now.AddDays(-45).AddMinutes(-i),
                IsResolved = true
            });
        }

        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        using var scope = provider.CreateScope();
        var worker = CreateWorker(provider, timeProvider: timeProvider);
        var config = DeadLetterCleanupWorker.NormalizeConfig(
            intervalHours: 24,
            retentionDays: 30,
            batchSize: 1,
            lockTimeoutSeconds: 5);

        var result = await worker.RunCycleAsync(scope, config, CancellationToken.None);

        Assert.Equal(DeadLetterCleanupWorker.MaxBatchesPerCycle, result.TotalDeleted);
        Assert.Equal(DeadLetterCleanupWorker.MaxBatchesPerCycle, result.DeletedResolvedCount);
        Assert.Equal(0, result.DeletedSoftDeletedCount);
        Assert.Equal(1, result.RemainingEligibleDeletionCount);
        Assert.Equal(DeadLetterCleanupWorker.MaxBatchesPerCycle, result.BatchesProcessed);
        Assert.True(result.HitBatchLimit);
        Assert.Single(await ctx.Set<DeadLetterEvent>().IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task LoadConfigAsync_DefaultsInvalidValues_AndClampsUnsafeRanges()
    {
        var (ctx, conn) = NewCtx();
        using var _ctx = ctx;
        using var _conn = conn;

        ctx.Set<EngineConfig>().AddRange(
            new EngineConfig { Id = 1, Key = "DeadLetter:CleanupIntervalHours", Value = "invalid" },
            new EngineConfig { Id = 2, Key = "DeadLetter:RetentionDays", Value = "0" },
            new EngineConfig { Id = 3, Key = "DeadLetter:CleanupBatchSize", Value = "500000" },
            new EngineConfig { Id = 4, Key = "DeadLetter:CleanupLockTimeoutSeconds", Value = "-4" });
        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider);

        var config = await worker.LoadConfigAsync(ctx, CancellationToken.None);

        Assert.Equal(24, config.IntervalHours);
        Assert.Equal(1, config.RetentionDays);
        Assert.Equal(10_000, config.BatchSize);
        Assert.Equal(0, config.LockTimeoutSeconds);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsCycleWithoutDeletingRows()
    {
        var now = new DateTime(2026, 04, 24, 0, 0, 0, DateTimeKind.Utc);
        var timeProvider = new DeadLetterCleanupFixedTimeProvider(now);
        var (ctx, conn) = NewCtx();
        using var _ctx = ctx;
        using var _conn = conn;

        ctx.Set<DeadLetterEvent>().Add(new DeadLetterEvent
        {
            Id = 1,
            HandlerName = "ResolvedHandler",
            EventType = "ResolvedEvent",
            EventPayload = "{}",
            ErrorMessage = "boom",
            DeadLetteredAt = now.AddDays(-45),
            IsResolved = true
        });
        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(
            provider,
            distributedLock: new DeadLetterCleanupFakeDistributedLock(acquire: false),
            timeProvider: timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Single(await ctx.Set<DeadLetterEvent>().IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public void CalculateDelay_UsesPollIntervalUntilFailure_ThenCapsFastRetry()
    {
        Assert.Equal(
            TimeSpan.FromHours(24),
            DeadLetterCleanupWorker.CalculateDelay(TimeSpan.FromHours(24), consecutiveFailures: 0));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            DeadLetterCleanupWorker.CalculateDelay(TimeSpan.FromHours(24), consecutiveFailures: 1));
        Assert.Equal(
            TimeSpan.FromMinutes(4),
            DeadLetterCleanupWorker.CalculateDelay(TimeSpan.FromHours(24), consecutiveFailures: 3));
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            DeadLetterCleanupWorker.CalculateDelay(TimeSpan.FromHours(24), consecutiveFailures: 10));
    }

    private static (DeadLetterCleanupTestContext Ctx, SqliteConnection Conn) NewCtx()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<DeadLetterCleanupTestContext>()
            .UseSqlite(conn)
            .Options;

        var ctx = new DeadLetterCleanupTestContext(options);
        ctx.Database.EnsureCreated();
        return (ctx, conn);
    }

    private static ServiceProvider BuildProvider(DeadLetterCleanupTestContext ctx)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReadApplicationDbContext>(ctx);
        services.AddSingleton<IWriteApplicationDbContext>(ctx);
        return services.BuildServiceProvider();
    }

    private static DeadLetterCleanupWorker CreateWorker(
        ServiceProvider provider,
        IDistributedLock? distributedLock = null,
        TimeProvider? timeProvider = null)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DeadLetterCleanupWorker>.Instance,
            distributedLock,
            metrics: new TradingMetrics(new DeadLetterCleanupWorkerTestMeterFactory()),
            timeProvider: timeProvider);
}

internal sealed class DeadLetterCleanupTestContext : DbContext,
    IReadApplicationDbContext, IWriteApplicationDbContext
{
    public DeadLetterCleanupTestContext(DbContextOptions<DeadLetterCleanupTestContext> options)
        : base(options)
    {
    }

    public DbContext GetDbContext() => this;

    public new int SaveChanges() => base.SaveChanges();

    public new Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => base.SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeadLetterEvent>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<EngineConfig>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.HasQueryFilter(e => !e.IsDeleted);
        });
    }
}

internal sealed class DeadLetterCleanupFixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public DeadLetterCleanupFixedTimeProvider(DateTime nowUtc)
    {
        _now = new DateTimeOffset(nowUtc, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class DeadLetterCleanupFakeDistributedLock(bool acquire = true) : IDistributedLock
{
    public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(acquire ? new NoopAsyncDisposable() : null);

    public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(acquire ? new NoopAsyncDisposable() : null);

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class DeadLetterCleanupWorkerTestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in _meters)
            meter.Dispose();
    }
}
