using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.Configurations;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class FeatureStoreBackfillWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_BackfillsOldestEligibleCandlesAndWritesLineage()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            new FeatureStoreOptions
            {
                BackfillBatchSize = 5,
                MaxCandlesPerRun = 5,
                BackfillPollIntervalSeconds = 60
            },
            db => db.Set<Candle>().AddRange(NewCandles("EURUSD", Timeframe.M5, now.UtcDateTime.AddMinutes(-200), 40)),
            new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var vectors = await harness.LoadFeatureVectorsAsync();
        var lineages = await harness.LoadLineagesAsync();
        var expectedCandleIds = Enumerable.Range(MLFeatureHelper.LookbackWindow + 1, 5)
            .Select(index => (long)index)
            .ToArray();

        Assert.Equal(MLFeatureHelper.LookbackWindow + 5, result.PendingCandleCount);
        Assert.Equal(5, result.VectorCount);
        Assert.Equal(1, result.LineageWriteCount);
        Assert.Equal(MLFeatureHelper.LookbackWindow, result.InsufficientHistoryCount);
        Assert.Equal(expectedCandleIds, vectors.Select(vector => vector.CandleId).ToArray());
        var lineage = Assert.Single(lineages);
        Assert.Equal("EURUSD", lineage.Symbol);
        Assert.Equal(Timeframe.M5, lineage.Timeframe);
    }

    [Fact]
    public async Task RunCycleAsync_StaleSchemaVector_IsRefreshedInPlace()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            new FeatureStoreOptions
            {
                BackfillBatchSize = 1,
                MaxCandlesPerRun = 1,
                BackfillPollIntervalSeconds = 60
            },
            db =>
            {
                var candles = NewCandles("EURUSD", Timeframe.M5, now.UtcDateTime.AddMinutes(-200), 40);
                db.Set<Candle>().AddRange(candles);
                db.Set<FeatureVector>().Add(new FeatureVector
                {
                    CandleId = candles[MLFeatureHelper.LookbackWindow].Id,
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.M5,
                    BarTimestamp = candles[MLFeatureHelper.LookbackWindow].Timestamp,
                    Features = BitConverter.GetBytes(42d),
                    SchemaVersion = 1,
                    SchemaHash = "stale-schema",
                    FeatureCount = 1,
                    FeatureNamesJson = "[\"Old\"]",
                    ComputedAt = now.UtcDateTime.AddMinutes(-1),
                    IsDeleted = false,
                    RowVersion = 1
                });
            },
            new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var vector = Assert.Single(await harness.LoadFeatureVectorsAsync(ignoreQueryFilters: true));

        Assert.Equal(1, result.VectorCount);
        Assert.False(vector.IsDeleted);
        Assert.Equal(harness.FeatureStore.CurrentSchemaHash, vector.SchemaHash);
        Assert.Equal(MLFeatureHelper.FeatureCount, vector.FeatureCount);
    }

    [Fact]
    public async Task RunCycleAsync_CurrentSchemaCorruptRow_IsRefreshedInPlace()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            new FeatureStoreOptions
            {
                BackfillBatchSize = 1,
                MaxCandlesPerRun = 1,
                BackfillPollIntervalSeconds = 60
            },
            db =>
            {
                var candles = NewCandles("EURUSD", Timeframe.M5, now.UtcDateTime.AddMinutes(-200), 40);
                db.Set<Candle>().AddRange(candles);
                db.Set<FeatureVector>().Add(new FeatureVector
                {
                    CandleId = candles[MLFeatureHelper.LookbackWindow].Id,
                    Symbol = "EURUSD",
                    Timeframe = Timeframe.M5,
                    BarTimestamp = candles[MLFeatureHelper.LookbackWindow].Timestamp,
                    Features = [0x01, 0x02, 0x03, 0x04],
                    SchemaVersion = 1,
                    SchemaHash = DatabaseFeatureStore.ComputeCurrentSchemaVersion(),
                    FeatureCount = MLFeatureHelper.FeatureCount,
                    FeatureNamesJson = "[\"Bad\"]",
                    ComputedAt = now.UtcDateTime.AddMinutes(-1),
                    IsDeleted = false,
                    RowVersion = 1
                });
            },
            new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var vector = Assert.Single(await harness.LoadFeatureVectorsAsync(ignoreQueryFilters: true));

        Assert.Equal(1, result.VectorCount);
        Assert.False(vector.IsDeleted);
        Assert.Equal(harness.FeatureStore.CurrentSchemaHash, vector.SchemaHash);
        Assert.Equal(MLFeatureHelper.FeatureCount * sizeof(double), vector.Features.Length);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutWriting()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            new FeatureStoreOptions
            {
                BackfillBatchSize = 5,
                MaxCandlesPerRun = 5,
                BackfillPollIntervalSeconds = 60
            },
            db => db.Set<Candle>().AddRange(NewCandles("EURUSD", Timeframe.M5, now.UtcDateTime.AddMinutes(-200), 40)),
            new TestTimeProvider(now),
            new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await harness.LoadFeatureVectorsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_InvalidOptions_AreClampedSafely()
    {
        using var harness = CreateHarness(
            new FeatureStoreOptions
            {
                BackfillBatchSize = 0,
                MaxCandlesPerRun = 0,
                BackfillPollIntervalSeconds = -1
            },
            _ => { });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(5), result.Settings.PollInterval);
        Assert.Equal(1, result.Settings.ScanPageSize);
        Assert.Equal(1, result.Settings.MaxCandlesPerRun);
    }

    private static WorkerHarness CreateHarness(
        FeatureStoreOptions options,
        Action<FeatureStoreBackfillWorkerTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<FeatureStoreBackfillWorkerTestContext>(dbOptions => dbOptions.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<FeatureStoreBackfillWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<FeatureStoreBackfillWorkerTestContext>());
        services.AddSingleton<IFeatureStore>(sp => new DatabaseFeatureStore(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatabaseFeatureStore>.Instance));

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FeatureStoreBackfillWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var featureStore = (DatabaseFeatureStore)provider.GetRequiredService<IFeatureStore>();
        var worker = new FeatureStoreBackfillWorker(
            NullLogger<FeatureStoreBackfillWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            metrics: null,
            timeProvider: timeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, connection, worker, featureStore);
    }

    private static List<Candle> NewCandles(string symbol, Timeframe timeframe, DateTime startUtc, int count)
    {
        var candles = new List<Candle>(count);
        decimal basePrice = 1.1000m;

        for (int index = 0; index < count; index++)
        {
            decimal open = basePrice + (0.0004m * index);
            decimal close = open + 0.0002m;
            candles.Add(new Candle
            {
                Id = index + 1,
                Symbol = symbol,
                Timeframe = timeframe,
                Open = open,
                High = close + 0.0003m,
                Low = open - 0.0003m,
                Close = close,
                Volume = 100 + index,
                Timestamp = startUtc.AddMinutes(5 * index),
                IsClosed = true,
                IsDeleted = false
            });
        }

        return candles;
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        FeatureStoreBackfillWorker worker,
        DatabaseFeatureStore featureStore) : IDisposable
    {
        public FeatureStoreBackfillWorker Worker { get; } = worker;
        public DatabaseFeatureStore FeatureStore { get; } = featureStore;

        public async Task<List<FeatureVector>> LoadFeatureVectorsAsync(bool ignoreQueryFilters = false)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FeatureStoreBackfillWorkerTestContext>();
            var query = db.Set<FeatureVector>().AsQueryable();
            if (ignoreQueryFilters)
                query = query.IgnoreQueryFilters();

            return await query
                .OrderBy(vector => vector.BarTimestamp)
                .ToListAsync();
        }

        public async Task<List<FeatureVectorLineage>> LoadLineagesAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FeatureStoreBackfillWorkerTestContext>();
            return await db.Set<FeatureVectorLineage>()
                .OrderBy(lineage => lineage.Id)
                .ToListAsync();
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class FeatureStoreBackfillWorkerTestContext(DbContextOptions<FeatureStoreBackfillWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Candle>(builder =>
            {
                builder.HasKey(candle => candle.Id);
                builder.HasQueryFilter(candle => !candle.IsDeleted);
                builder.Property(candle => candle.Timeframe).HasConversion<string>();
                builder.HasIndex(candle => new { candle.Symbol, candle.Timeframe, candle.Timestamp }).IsUnique();
            });

            modelBuilder.Entity<COTReport>(builder =>
            {
                builder.HasKey(report => report.Id);
                builder.HasQueryFilter(report => !report.IsDeleted);
            });

            new FeatureVectorConfiguration().Configure(modelBuilder.Entity<FeatureVector>());
            new FeatureVectorLineageConfiguration().Configure(modelBuilder.Entity<FeatureVectorLineage>());

            modelBuilder.Entity<FeatureVector>()
                .Property(vector => vector.RowVersion)
                .HasDefaultValue(0u)
                .ValueGeneratedNever();

            modelBuilder.Entity<FeatureVectorLineage>()
                .Property(lineage => lineage.RowVersion)
                .HasDefaultValue(0u)
                .ValueGeneratedNever();
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
