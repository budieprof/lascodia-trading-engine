using System.Diagnostics.Metrics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.IntegrationTest;

/// <summary>
/// End-to-end test for <see cref="CandleAggregationWorker"/> on real Postgres.
/// Validates that the EF <c>GroupBy/Max</c> translation for the pre-fetched
/// <c>latestMap</c> works against Npgsql (the most provider-sensitive query in
/// the worker), that the weekend-gap fix holds on a real DB, and that the
/// unique-index backstop on <c>(Symbol, Timeframe, Timestamp)</c> fires as
/// documented.
/// </summary>
public class CandleAggregationWorkerPostgresIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public CandleAggregationWorkerPostgresIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Happy-path: one active symbol, 60 full M1 candles, one worker cycle.
    /// The single H1 aggregate must land in the DB with correct OHLCV, and a
    /// second cycle must be a no-op (the pre-existing H1 now dominates the
    /// latestMap).
    /// </summary>
    [Fact]
    public async Task RunCycleAsync_SynthesisesH1FromM1_InsertedIntoPostgres()
    {
        await EnsureMigratedAsync();

        var periodStart = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        var now         = periodStart.AddHours(2);  // 12:00 UTC — period 10:00-11:00 is closed

        await using (var seed = CreateContext())
        {
            seed.Set<CurrencyPair>().Add(NewPair("EURUSD"));
            for (int i = 0; i < 60; i++)
                seed.Set<Candle>().Add(NewM1("EURUSD", periodStart.AddMinutes(i), i));
            await seed.SaveChangesAsync();
        }

        var worker = CreateWorker(now);
        var first = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, first.PeriodsSynthesized);
        Assert.Equal(0, first.PairsFailed);
        Assert.False(first.BudgetExhausted);

        await using (var assertCtx = CreateContext())
        {
            var h1 = await assertCtx.Set<Candle>()
                .SingleAsync(c => c.Symbol == "EURUSD" && c.Timeframe == Timeframe.H1);

            Assert.Equal(periodStart, h1.Timestamp);
            Assert.True(h1.IsClosed);
            Assert.Equal(60 * 100m, h1.Volume);
            // Open = first M1 open, Close = last M1 close, High/Low = min/max across the batch.
            Assert.Equal(1.10000m,                        h1.Open);
            Assert.Equal(1.10000m + 59 * 0.0001m + 0.00025m, h1.Close);
            Assert.True(h1.High > h1.Open);
            Assert.True(h1.Low  < h1.Open);
        }

        // Second cycle: the H1 from cycle 1 now dominates latestMap, so the
        // worker has no complete period to synthesise and writes nothing.
        var second = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Equal(0, second.PeriodsSynthesized);
        Assert.Equal(0, second.PairsFailed);
    }

    /// <summary>
    /// Regression test for the weekend-gap halt bug on a real DB. A pre-existing
    /// Friday 21:00 H1 plus an empty weekend window plus Sunday 22:00 M1 data
    /// must result in the Sunday 22:00 H1 being synthesised — NOT the worker
    /// stalling forever on the first empty period.
    /// </summary>
    [Fact]
    public async Task RunCycleAsync_WeekendGap_SynthesisesPostGapH1()
    {
        await EnsureMigratedAsync();

        var fri21 = new DateTime(2026, 3, 13, 21, 0, 0, DateTimeKind.Utc);  // Friday 21:00 UTC
        var sun22 = new DateTime(2026, 3, 15, 22, 0, 0, DateTimeKind.Utc);  // Sunday 22:00 UTC
        var now   = sun22.AddHours(1).AddMinutes(5);                        // 23:05 Sunday

        await using (var seed = CreateContext())
        {
            seed.Set<CurrencyPair>().Add(NewPair("EURUSD"));
            // Pre-existing H1 that would otherwise anchor the walker at Friday 22:00.
            seed.Set<Candle>().Add(new Candle
            {
                Symbol = "EURUSD", Timeframe = Timeframe.H1, Timestamp = fri21,
                Open = 1.10m, High = 1.11m, Low = 1.09m, Close = 1.105m,
                Volume = 100m, IsClosed = true,
            });
            // Fresh M1 run starting after the weekend silence.
            for (int i = 0; i < 60; i++)
                seed.Set<Candle>().Add(NewM1("EURUSD", sun22.AddMinutes(i), i));
            await seed.SaveChangesAsync();
        }

        var worker = CreateWorker(now);
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.PeriodsSynthesized);
        Assert.Equal(0, result.PairsFailed);

        await using var assertCtx = CreateContext();
        var sundayH1 = await assertCtx.Set<Candle>()
            .SingleAsync(c => c.Symbol == "EURUSD" && c.Timeframe == Timeframe.H1 && c.Timestamp == sun22);
        Assert.True(sundayH1.IsClosed);
    }

    /// <summary>
    /// Proves the defense-in-depth unique-index backstop on
    /// <c>IX_Candle_Symbol_Timeframe_Timestamp</c> is enforced by the schema.
    /// If two writers race past the worker's HashSet guard, the second save
    /// surfaces as a <see cref="DbUpdateException"/> — which the worker
    /// classifies as a benign unique-race, not a real failure.
    /// </summary>
    [Fact]
    public async Task UniqueIndex_RejectsDuplicateSymbolTimeframeTimestamp()
    {
        await EnsureMigratedAsync();

        var ts = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);

        await using (var first = CreateContext())
        {
            first.Set<Candle>().Add(new Candle
            {
                Symbol = "EURUSD", Timeframe = Timeframe.H1, Timestamp = ts,
                Open = 1.10m, High = 1.11m, Low = 1.09m, Close = 1.105m,
                Volume = 100m, IsClosed = true,
            });
            await first.SaveChangesAsync();
        }

        await using var second = CreateContext();
        second.Set<Candle>().Add(new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, Timestamp = ts,
            Open = 9.99m, High = 9.99m, Low = 9.99m, Close = 9.99m,
            Volume = 1m, IsClosed = true,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private CandleAggregationWorker CreateWorker(DateTime now)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new DbContextAccessor(CreateContext()));
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        var provider = services.BuildServiceProvider();

        return new CandleAggregationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CandleAggregationWorker>.Instance,
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

    private static CurrencyPair NewPair(string symbol) => new()
    {
        Symbol        = symbol,
        BaseCurrency  = symbol.Length >= 3 ? symbol[..3]  : "USD",
        QuoteCurrency = symbol.Length >= 6 ? symbol[3..6] : "USD",
        DecimalPlaces = 5,
        ContractSize  = 100_000m,
        PipSize       = 0.0001m,
        MinLotSize    = 0.01m,
        MaxLotSize    = 100m,
        LotStep       = 0.01m,
        IsActive      = true,
    };

    private static Candle NewM1(string symbol, DateTime ts, int i)
    {
        var open = 1.10000m + i * 0.0001m;
        return new Candle
        {
            Symbol    = symbol,
            Timeframe = Timeframe.M1,
            Timestamp = DateTime.SpecifyKind(ts, DateTimeKind.Utc),
            Open      = open,
            High      = open + 0.0005m,
            Low       = open - 0.0005m,
            Close     = open + 0.00025m,
            Volume    = 100m,
            IsClosed  = true,
        };
    }

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
