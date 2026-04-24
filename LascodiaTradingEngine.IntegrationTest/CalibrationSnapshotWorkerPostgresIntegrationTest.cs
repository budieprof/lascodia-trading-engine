using System.Diagnostics.Metrics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.IntegrationTest;

/// <summary>
/// Smoke test for the full <see cref="CalibrationSnapshotWorker"/> pipeline on real Postgres.
/// Applies every migration, seeds <see cref="SignalRejectionAudit"/> rows, runs one worker cycle,
/// and asserts the expected <see cref="CalibrationSnapshot"/> rows land with correct aggregates.
/// Also directly exercises the unique-index backstop described in the worker docstring to prove
/// the defense-in-depth claim is actually enforced by the schema, not just the in-process guard.
/// </summary>
public class CalibrationSnapshotWorkerPostgresIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public CalibrationSnapshotWorkerPostgresIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunCycleAsync_AggregatesRealPostgresRejections_WritesMonthlySnapshot()
    {
        await EnsureMigratedAsync();

        // "Now" for the test: mid-April 2026. March is the last complete month.
        var now = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var mar = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = CreateContext())
        {
            seed.Set<SignalRejectionAudit>().AddRange(
                Reject(mar.AddDays(1),  strategyId: 10, symbol: "EURUSD", stage: "Regime", reason: "regime_blocked"),
                Reject(mar.AddDays(2),  strategyId: 10, symbol: "EURUSD", stage: "Regime", reason: "regime_blocked"),
                Reject(mar.AddDays(5),  strategyId: 11, symbol: "GBPUSD", stage: "Regime", reason: "regime_blocked"),
                Reject(mar.AddDays(10), strategyId: 10, symbol: "EURUSD", stage: "MTF",    reason: "mtf_not_confirmed"));
            await seed.SaveChangesAsync();
        }

        var worker = CreateWorker(now);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.MonthsProcessed);
        Assert.Equal(2L, result.SnapshotsWritten);

        await using var assertCtx = CreateContext();
        var rows = await assertCtx.Set<CalibrationSnapshot>()
            .Where(s => s.PeriodStart == mar)
            .OrderBy(s => s.Stage).ThenBy(s => s.Reason)
            .ToListAsync();

        Assert.Equal(2, rows.Count);

        var mtfRow = Assert.Single(rows, r => r.Stage == "MTF");
        Assert.Equal("mtf_not_confirmed", mtfRow.Reason);
        Assert.Equal(1L, mtfRow.RejectionCount);
        Assert.Equal(1, mtfRow.DistinctSymbols);
        Assert.Equal(1, mtfRow.DistinctStrategies);
        Assert.Equal("Monthly", mtfRow.PeriodGranularity);
        Assert.Equal(mar.AddMonths(1), mtfRow.PeriodEnd);

        var regimeRow = Assert.Single(rows, r => r.Stage == "Regime");
        Assert.Equal("regime_blocked", regimeRow.Reason);
        Assert.Equal(3L, regimeRow.RejectionCount);
        Assert.Equal(2, regimeRow.DistinctSymbols);
        Assert.Equal(2, regimeRow.DistinctStrategies);

        // Running a second cycle must be a no-op — the AnyAsync guard covers it.
        var second = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Equal(0, second.MonthsProcessed);
        Assert.Equal(1, second.MonthsSkippedAlreadyExists);
        Assert.Equal(0L, second.SnapshotsWritten);
    }

    [Fact]
    public async Task UniqueIndex_RejectsDuplicatePeriodStageReasonTuple()
    {
        await EnsureMigratedAsync();

        var periodStart = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var first = CreateContext())
        {
            first.Set<CalibrationSnapshot>().Add(new CalibrationSnapshot
            {
                PeriodStart        = periodStart,
                PeriodEnd          = periodStart.AddMonths(1),
                PeriodGranularity  = "Monthly",
                Stage              = "Regime",
                Reason             = "regime_blocked",
                RejectionCount     = 5,
                DistinctSymbols    = 2,
                DistinctStrategies = 2,
                ComputedAt         = DateTime.UtcNow,
            });
            await first.SaveChangesAsync();
        }

        // Second insert against the same (PeriodStart, PeriodGranularity, Stage, Reason) tuple
        // must be rejected by the unique index — this is the defense-in-depth backstop that
        // protects the worker against a hypothetical race past its AnyAsync guard.
        await using var second = CreateContext();
        second.Set<CalibrationSnapshot>().Add(new CalibrationSnapshot
        {
            PeriodStart        = periodStart,
            PeriodEnd          = periodStart.AddMonths(1),
            PeriodGranularity  = "Monthly",
            Stage              = "Regime",
            Reason             = "regime_blocked",
            RejectionCount     = 99,
            DistinctSymbols    = 1,
            DistinctStrategies = 1,
            ComputedAt         = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private CalibrationSnapshotWorker CreateWorker(DateTime now)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new DbContextAccessor(CreateContext()));
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        var provider = services.BuildServiceProvider();

        return new CalibrationSnapshotWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CalibrationSnapshotWorker>.Instance,
            new TradingMetrics(new DummyMeterFactory()),
            new FixedTimeProvider(now));
    }

    private WriteApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;
        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private async Task EnsureMigratedAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();
    }

    private static SignalRejectionAudit Reject(
        DateTime at, long strategyId, string symbol, string stage, string reason) => new()
    {
        RejectedAt = at,
        StrategyId = strategyId,
        Symbol     = symbol,
        Stage      = stage,
        Reason     = reason,
        Source     = "IntegrationTest",
    };

    private sealed class DbContextAccessor(WriteApplicationDbContext context)
        : IReadApplicationDbContext, IWriteApplicationDbContext, IAsyncDisposable
    {
        public DbContext GetDbContext() => context;
        public int SaveChanges() => context.SaveChanges();
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => context.SaveChangesAsync(cancellationToken);
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
            => new(DateTime.SpecifyKind(now, DateTimeKind.Utc));
    }

    private sealed class DummyMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
