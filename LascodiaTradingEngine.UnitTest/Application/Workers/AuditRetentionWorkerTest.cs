using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.Metrics;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

/// <summary>
/// Exercises the pruning logic in <see cref="AuditRetentionWorker.RunCycleAsync"/>
/// against an in-memory DbContext. The ExecuteAsync loop (delay/config read/poll
/// cadence) is a thin wrapper around <c>RunCycleAsync</c> + <c>Task.Delay</c> and
/// is not meaningfully testable without a live scheduler; the substantive logic
/// — what rows get deleted, under what cutoff, with what batch boundary — lives
/// in <c>RunCycleAsync</c>.
/// </summary>
public class AuditRetentionWorkerTest
{
    // SQLite in-memory is used (not InMemory provider) because ExecuteDeleteAsync
    // is a relational-only operation; the InMemory provider throws.
    private static (RetentionTestContext Ctx, SqliteConnection Conn) NewCtx()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<RetentionTestContext>()
            .UseSqlite(conn).Options;
        var ctx = new RetentionTestContext(opts);
        ctx.Database.EnsureCreated();
        return (ctx, conn);
    }

    private static ServiceProvider BuildProvider(RetentionTestContext ctx)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReadApplicationDbContext>(ctx);
        services.AddSingleton<IWriteApplicationDbContext>(ctx);
        return services.BuildServiceProvider();
    }

    private static AuditRetentionWorker NewWorker(FixedTimeProvider time, ServiceProvider provider)
    {
        return new AuditRetentionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditRetentionWorker>.Instance,
            new TradingMetrics(new TestMeterFactory()),
            time);
    }

    [Fact]
    public async Task RunCycle_DeletesExpiredSignalRejections_KeepsFreshOnes()
    {
        var now = new DateTime(2026, 04, 20, 0, 0, 0, DateTimeKind.Utc);
        var time = new FixedTimeProvider(now);
        var (ctx, conn) = NewCtx();
        using var _ctx = ctx;
        using var _conn = conn;

        ctx.Set<SignalRejectionAudit>().AddRange(
            new SignalRejectionAudit { Id = 1, Symbol = "EURUSD", Stage = "Regime",  Reason = "blocked", Source = "SW", RejectedAt = now.AddDays(-100) },
            new SignalRejectionAudit { Id = 2, Symbol = "EURUSD", Stage = "Regime",  Reason = "blocked", Source = "SW", RejectedAt = now.AddDays(-95)  },
            new SignalRejectionAudit { Id = 3, Symbol = "EURUSD", Stage = "MTF",     Reason = "missing", Source = "SW", RejectedAt = now.AddDays(-91)  },
            new SignalRejectionAudit { Id = 4, Symbol = "EURUSD", Stage = "Tier1",   Reason = "rr_low",  Source = "SB", RejectedAt = now.AddDays(-30)  },
            new SignalRejectionAudit { Id = 5, Symbol = "EURUSD", Stage = "Tier1",   Reason = "rr_low",  Source = "SB", RejectedAt = now.AddDays(-5)   });
        ctx.SaveChanges();

        using var provider = BuildProvider(ctx);
        using var scope = provider.CreateScope();
        var worker = NewWorker(time, provider);

        var result = await worker.RunCycleAsync(scope, batchSize: 1000, signalRejectionDays: 90, reconciliationDays: 180, CancellationToken.None);

        Assert.Equal(3, result.SignalRejectionDeleted);
        Assert.Equal(0, result.ReconciliationDeleted);

        var remaining = ctx.Set<SignalRejectionAudit>().OrderBy(r => r.Id).Select(r => r.Id).ToList();
        Assert.Equal(new List<long> { 4, 5 }, remaining);
    }

    [Fact]
    public async Task RunCycle_DeletesExpiredReconciliationRuns_HonoringCustomRetention()
    {
        var now = new DateTime(2026, 04, 20, 0, 0, 0, DateTimeKind.Utc);
        var time = new FixedTimeProvider(now);
        var (ctx, conn) = NewCtx();
        using var _ctx = ctx;
        using var _conn = conn;

        ctx.Set<ReconciliationRun>().AddRange(
            new ReconciliationRun { Id = 1, InstanceId = "EA1", RunAt = now.AddDays(-200) },
            new ReconciliationRun { Id = 2, InstanceId = "EA1", RunAt = now.AddDays(-181) },
            new ReconciliationRun { Id = 3, InstanceId = "EA1", RunAt = now.AddDays(-179) },
            new ReconciliationRun { Id = 4, InstanceId = "EA1", RunAt = now.AddDays(-1)   });
        ctx.SaveChanges();

        using var provider = BuildProvider(ctx);
        using var scope = provider.CreateScope();
        var worker = NewWorker(time, provider);

        var result = await worker.RunCycleAsync(scope, batchSize: 1000, signalRejectionDays: 90, reconciliationDays: 180, CancellationToken.None);

        Assert.Equal(0, result.SignalRejectionDeleted);
        Assert.Equal(2, result.ReconciliationDeleted);

        var remaining = ctx.Set<ReconciliationRun>().OrderBy(r => r.Id).Select(r => r.Id).ToList();
        Assert.Equal(new List<long> { 3, 4 }, remaining);
    }

    [Fact]
    public async Task RunCycle_BatchesDeletes_AcrossMultiplePasses()
    {
        var now = new DateTime(2026, 04, 20, 0, 0, 0, DateTimeKind.Utc);
        var time = new FixedTimeProvider(now);
        var (ctx, conn) = NewCtx();
        using var _ctx = ctx;
        using var _conn = conn;

        // 25 expired rows — force batch-size = 10 so the loop processes them in
        // three passes (10, 10, 5) before exiting.
        for (int i = 0; i < 25; i++)
        {
            ctx.Set<SignalRejectionAudit>().Add(new SignalRejectionAudit
            {
                Id = i + 1,
                Symbol = "EURUSD",
                Stage = "Regime",
                Reason = "blocked",
                Source = "SW",
                RejectedAt = now.AddDays(-100 - i),
            });
        }
        ctx.SaveChanges();

        using var provider = BuildProvider(ctx);
        using var scope = provider.CreateScope();
        var worker = NewWorker(time, provider);

        var result = await worker.RunCycleAsync(scope, batchSize: 10, signalRejectionDays: 90, reconciliationDays: 180, CancellationToken.None);

        Assert.Equal(25, result.SignalRejectionDeleted);
        Assert.Empty(ctx.Set<SignalRejectionAudit>().ToList());
    }

    [Fact]
    public async Task RunCycle_NoExpiredRows_IsNoOp()
    {
        var now = new DateTime(2026, 04, 20, 0, 0, 0, DateTimeKind.Utc);
        var time = new FixedTimeProvider(now);
        var (ctx, conn) = NewCtx();
        using var _ctx = ctx;
        using var _conn = conn;

        ctx.Set<SignalRejectionAudit>().Add(new SignalRejectionAudit
        {
            Id = 1, Symbol = "EURUSD", Stage = "Regime", Reason = "blocked", Source = "SW",
            RejectedAt = now.AddDays(-10)
        });
        ctx.SaveChanges();

        using var provider = BuildProvider(ctx);
        using var scope = provider.CreateScope();
        var worker = NewWorker(time, provider);

        var result = await worker.RunCycleAsync(scope, batchSize: 1000, signalRejectionDays: 90, reconciliationDays: 180, CancellationToken.None);

        Assert.Equal(0, result.SignalRejectionDeleted);
        Assert.Equal(0, result.ReconciliationDeleted);
        Assert.Single(ctx.Set<SignalRejectionAudit>().ToList());
    }

    [Fact]
    public async Task RunCycle_ClampsZeroOrNegativeRetention_To_OneDay()
    {
        var now = new DateTime(2026, 04, 20, 0, 0, 0, DateTimeKind.Utc);
        var time = new FixedTimeProvider(now);
        var (ctx, conn) = NewCtx();
        using var _ctx = ctx;
        using var _conn = conn;

        ctx.Set<SignalRejectionAudit>().Add(new SignalRejectionAudit
        {
            Id = 1, Symbol = "EURUSD", Stage = "Regime", Reason = "blocked", Source = "SW",
            RejectedAt = now.AddHours(-12)
        });
        ctx.SaveChanges();

        using var provider = BuildProvider(ctx);
        using var scope = provider.CreateScope();
        var worker = NewWorker(time, provider);

        // signalRejectionDays = 0 clamps to 1 → a 12-hour-old row stays within
        // the retention window.
        var result = await worker.RunCycleAsync(scope, batchSize: 1000, signalRejectionDays: 0, reconciliationDays: 180, CancellationToken.None);

        Assert.Equal(0, result.SignalRejectionDeleted);
        Assert.Single(ctx.Set<SignalRejectionAudit>().ToList());
    }
}

/// <summary>Minimal fixed-clock TimeProvider for retention tests.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public FixedTimeProvider(DateTime nowUtc) { _now = new DateTimeOffset(nowUtc, TimeSpan.Zero); }
    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = new();
    public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
    public void Dispose() { foreach (var m in _meters) m.Dispose(); }
}

internal sealed class RetentionTestContext : DbContext,
    IReadApplicationDbContext, IWriteApplicationDbContext
{
    public RetentionTestContext(DbContextOptions<RetentionTestContext> options) : base(options) { }

    public DbContext GetDbContext() => this;

    public new int SaveChanges() => base.SaveChanges();

    public new Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => base.SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SignalRejectionAudit>().HasKey(e => e.Id);
        modelBuilder.Entity<ReconciliationRun>().HasKey(e => e.Id);
        modelBuilder.Entity<EngineConfig>().HasKey(e => e.Id);
    }
}
