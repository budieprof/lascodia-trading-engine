using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class CorrelationMatrixWorkerTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly InMemoryDatabaseRoot _databaseRoot = new();
    private readonly string _databaseName = $"corr-{Guid.NewGuid()}";
    private readonly FixedTimeProvider _timeProvider = new(new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero));
    private long _nextCurrencyPairId = 1;
    private long _nextCandleId = 1;

    public CorrelationMatrixWorkerTest()
    {
        _metrics = new TradingMetrics(_meterFactory);
    }

    public void Dispose()
    {
        _meterFactory.Dispose();
    }

    [Fact]
    public async Task RunCycleAsync_WhenUniverseBecomesIneligible_ClearsPreviouslyPublishedSnapshot()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            AddActivePair(db, "EURUSD");
            AddActivePair(db, "GBPUSD");
            AddCorrelatedDailySeries(db, "EURUSD", "GBPUSD", closeCount: 25);
        });

        var worker = CreateWorker(provider);

        int firstRunPairs = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, firstRunPairs);
        Assert.Single(worker.GetCorrelations());
        Assert.Equal(_timeProvider.GetUtcNow().UtcDateTime, worker.LastComputedAtUtc);
        Assert.Equal(_timeProvider.GetUtcNow().UtcDateTime, worker.LastAttemptedAtUtc);

        await SeedAsync(provider, db =>
        {
            foreach (var pair in db.CurrencyPairs)
                pair.IsActive = false;
        });

        int secondRunPairs = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, secondRunPairs);
        Assert.Empty(worker.GetCorrelations());
    }

    [Fact]
    public async Task RunCycleAsync_PublishesNewSnapshotWithoutMutatingPreviouslyReturnedSnapshot()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            AddActivePair(db, "EURUSD");
            AddActivePair(db, "GBPUSD");
            AddCorrelatedDailySeries(db, "EURUSD", "GBPUSD", closeCount: 25);
        });

        var worker = CreateWorker(provider);
        await worker.RunCycleAsync(CancellationToken.None);

        var firstSnapshot = worker.GetCorrelations();
        string pairKey = "EURUSD|GBPUSD";
        decimal firstCorrelation = firstSnapshot[pairKey];

        await SeedAsync(provider, db =>
        {
            foreach (var pair in db.CurrencyPairs)
                pair.IsActive = false;
        });

        await worker.RunCycleAsync(CancellationToken.None);

        var secondSnapshot = worker.GetCorrelations();

        Assert.NotSame(firstSnapshot, secondSnapshot);
        Assert.Equal(firstCorrelation, firstSnapshot[pairKey]);
        Assert.Empty(secondSnapshot);
        Assert.Equal(_timeProvider.GetUtcNow().UtcDateTime, worker.LastAttemptedAtUtc);
    }

    [Fact]
    public async Task RunCycleAsync_DeduplicatesSameDayCandlesUsingLatestClose()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            AddActivePair(db, "EURUSD");
            AddActivePair(db, "GBPUSD");

            DateTime start = _timeProvider.UtcNow.UtcDateTime.Date.AddDays(-30);
            decimal[] eurCloses =
            {
                100m, 110m, 121m, 133.1m, 146.41m,
                161.051m, 177.1561m, 194.87171m, 214.358881m, 235.794769m,
                259.374246m, 285.311671m, 313.842838m, 345.227122m, 379.749834m,
                417.724817m, 459.497299m, 505.447029m, 555.991732m, 611.590905m,
                672.749995m
            };

            for (int i = 0; i < eurCloses.Length; i++)
            {
                DateTime day = start.AddDays(i);
                AddClosedDailyCandle(db, "EURUSD", day.AddHours(10), eurCloses[i]);
                AddClosedDailyCandle(db, "GBPUSD", day.AddHours(10), eurCloses[i] * 2m);
            }

            // Duplicate same-day candle with an earlier timestamp and incorrect close.
            AddClosedDailyCandle(db, "EURUSD", start.AddDays(1).AddHours(8), 999m);
            // Latest same-day close should win.
            AddClosedDailyCandle(db, "EURUSD", start.AddDays(1).AddHours(23), eurCloses[1]);
        });

        var worker = CreateWorker(provider);

        int pairsComputed = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, pairsComputed);
        Assert.Equal(1.0000m, worker.GetCorrelations()["EURUSD|GBPUSD"]);
    }

    [Fact]
    public async Task RunCycleAsync_UsesConfiguredLookbackAgainstInjectedClock()
    {
        using var provider = BuildProvider(new CorrelationMatrixOptions
        {
            LookbackDays = 10,
            MinClosesPerSymbol = 5,
            MinOverlapPoints = 4,
        });

        await SeedAsync(provider, db =>
        {
            AddActivePair(db, "EURUSD");
            AddActivePair(db, "GBPUSD");

            // These candles are outside the configured 10-day lookback window and must be ignored.
            AddClosedDailyCandle(db, "EURUSD", _timeProvider.UtcNow.UtcDateTime.AddDays(-20), 100m);
            AddClosedDailyCandle(db, "GBPUSD", _timeProvider.UtcNow.UtcDateTime.AddDays(-20), 200m);

            for (int i = 9; i >= 0; i--)
            {
                DateTime day = _timeProvider.UtcNow.UtcDateTime.Date.AddDays(-i).AddHours(22);
                AddClosedDailyCandle(db, "EURUSD", day, 100m + (10 - i));
                AddClosedDailyCandle(db, "GBPUSD", day, 200m + (10 - i) * 2m);
            }
        });

        var worker = CreateWorker(provider, new CorrelationMatrixOptions
        {
            LookbackDays = 10,
            MinClosesPerSymbol = 5,
            MinOverlapPoints = 4,
        });

        int pairsComputed = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, pairsComputed);
        Assert.Single(worker.GetCorrelations());
    }

    [Fact]
    public async Task RunCycleAsync_FlatSeries_PublishesZeroCorrelation()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            AddActivePair(db, "EURUSD");
            AddActivePair(db, "GBPUSD");

            DateTime start = _timeProvider.UtcNow.UtcDateTime.Date.AddDays(-25);
            for (int i = 0; i < 21; i++)
            {
                DateTime day = start.AddDays(i).AddHours(22);
                AddClosedDailyCandle(db, "EURUSD", day, 100m);
                AddClosedDailyCandle(db, "GBPUSD", day, 200m);
            }
        });

        var worker = CreateWorker(provider);

        int pairsComputed = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, pairsComputed);
        Assert.Equal(0m, worker.GetCorrelations()["EURUSD|GBPUSD"]);
    }

    [Fact]
    public async Task RunCycleAsync_HonorsConfiguredOverlapThreshold()
    {
        var options = new CorrelationMatrixOptions
        {
            MinClosesPerSymbol = 6,
            MinOverlapPoints = 6,
            LookbackDays = 30,
        };

        using var provider = BuildProvider(options);
        await SeedAsync(provider, db =>
        {
            AddActivePair(db, "EURUSD");
            AddActivePair(db, "GBPUSD");

            DateTime start = _timeProvider.UtcNow.UtcDateTime.Date.AddDays(-10);

            for (int i = 0; i < 6; i++)
            {
                AddClosedDailyCandle(db, "EURUSD", start.AddDays(i).AddHours(22), 100m + i);
            }

            for (int i = 1; i < 7; i++)
            {
                AddClosedDailyCandle(db, "GBPUSD", start.AddDays(i).AddHours(22), 200m + i);
            }
        });

        var worker = CreateWorker(provider, options);

        int pairsComputed = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, pairsComputed);
        Assert.Empty(worker.GetCorrelations());
    }

    private ServiceProvider BuildProvider(CorrelationMatrixOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CorrelationMatrixTestDbContext>(dbOptions =>
            dbOptions.UseInMemoryDatabase(_databaseName, _databaseRoot));
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<CorrelationMatrixTestDbContext>());
        services.AddSingleton(options ?? new CorrelationMatrixOptions());
        services.AddSingleton<TimeProvider>(_timeProvider);
        return services.BuildServiceProvider();
    }

    private CorrelationMatrixWorker CreateWorker(ServiceProvider provider, CorrelationMatrixOptions? options = null)
        => new(
            NullLogger<CorrelationMatrixWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            _metrics,
            options ?? provider.GetRequiredService<CorrelationMatrixOptions>(),
            provider.GetRequiredService<TimeProvider>());

    private static async Task SeedAsync(ServiceProvider provider, Action<CorrelationMatrixTestDbContext> seed)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CorrelationMatrixTestDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private void AddActivePair(CorrelationMatrixTestDbContext db, string symbol)
    {
        db.CurrencyPairs.Add(new CurrencyPair
        {
            Id = _nextCurrencyPairId++,
            Symbol = symbol,
            BaseCurrency = symbol[..3],
            QuoteCurrency = symbol[3..6],
            IsActive = true,
            IsDeleted = false,
        });
    }

    private void AddCorrelatedDailySeries(
        CorrelationMatrixTestDbContext db,
        string symbolA,
        string symbolB,
        int closeCount)
    {
        DateTime start = _timeProvider.UtcNow.UtcDateTime.Date.AddDays(-closeCount - 2);
        decimal priceA = 100m;
        decimal priceB = 200m;

        for (int i = 0; i < closeCount; i++)
        {
            AddClosedDailyCandle(db, symbolA, start.AddDays(i).AddHours(22), priceA);
            AddClosedDailyCandle(db, symbolB, start.AddDays(i).AddHours(22), priceB);

            priceA *= 1.01m;
            priceB *= 1.01m;
        }
    }

    private void AddClosedDailyCandle(CorrelationMatrixTestDbContext db, string symbol, DateTime timestamp, decimal close)
    {
        db.Candles.Add(new Candle
        {
            Id = _nextCandleId++,
            Symbol = symbol,
            Timeframe = Timeframe.D1,
            Timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
            Open = close,
            High = close,
            Low = close,
            Close = close,
            Volume = 1m,
            IsClosed = true,
            IsDeleted = false,
        });
    }

    private sealed class CorrelationMatrixTestDbContext(DbContextOptions<CorrelationMatrixTestDbContext> options)
        : DbContext(options), IReadApplicationDbContext
    {
        public DbSet<CurrencyPair> CurrencyPairs => Set<CurrencyPair>();
        public DbSet<Candle> Candles => Set<Candle>();

        public DbContext GetDbContext() => this;

        public new int SaveChanges() => base.SaveChanges();

        public new Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => base.SaveChangesAsync(cancellationToken);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CurrencyPair>().HasKey(e => e.Id);
            modelBuilder.Entity<Candle>().HasKey(e => e.Id);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = nowUtc;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
