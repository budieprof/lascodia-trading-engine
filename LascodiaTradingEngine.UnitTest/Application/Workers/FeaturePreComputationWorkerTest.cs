using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.Configurations;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class FeaturePreComputationWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_WithExactlyLookbackPlusOneCandles_ComputesLatestVector()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<Strategy>().Add(NewStrategy(1, "EURUSD", Timeframe.M5));
            db.Set<Candle>().AddRange(NewCandles("EURUSD", Timeframe.M5, now.UtcDateTime.AddMinutes(-150), MLFeatureHelper.LookbackWindow + 1));
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var vectors = await harness.LoadFeatureVectorsAsync();
        var lineages = await harness.LoadLineagesAsync();

        Assert.Equal(1, result.ActivePairCount);
        Assert.Equal(1, result.EvaluatedPairCount);
        Assert.Equal(1, result.VectorCount);
        var vector = Assert.Single(vectors);
        Assert.Single(lineages);
        Assert.Equal(MLFeatureHelper.FeatureCount, vector.FeatureCount);
        Assert.Equal(harness.FeatureStore.CurrentSchemaHash, vector.SchemaHash);
    }

    [Fact]
    public async Task RunCycleAsync_CatchesUpRecentMissingBars_PerActivePair()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().Add(NewConfig("FeaturePreComputation:CatchUpBarsPerPair", "4"));
            db.Set<Strategy>().Add(NewStrategy(1, "EURUSD", Timeframe.M5));
            db.Set<Candle>().AddRange(NewCandles("EURUSD", Timeframe.M5, now.UtcDateTime.AddMinutes(-170), 35));
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var vectors = await harness.LoadFeatureVectorsAsync();
        var lineages = await harness.LoadLineagesAsync();

        Assert.Equal(4, result.PendingVectorCount);
        Assert.Equal(4, result.VectorCount);
        Assert.Equal(4, vectors.Count);
        Assert.Single(lineages);
    }

    [Fact]
    public async Task RunCycleAsync_StaleSchemaVector_IsRefreshedInPlace()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<Strategy>().Add(NewStrategy(1, "EURUSD", Timeframe.M5));
            var candles = NewCandles("EURUSD", Timeframe.M5, now.UtcDateTime.AddMinutes(-150), MLFeatureHelper.LookbackWindow + 1);
            db.Set<Candle>().AddRange(candles);
            db.Set<FeatureVector>().Add(new FeatureVector
            {
                CandleId = candles[^1].Id,
                Symbol = "EURUSD",
                Timeframe = Timeframe.M5,
                BarTimestamp = candles[^1].Timestamp,
                Features = BitConverter.GetBytes(42d),
                SchemaVersion = 1,
                SchemaHash = "stale-schema",
                FeatureCount = 1,
                FeatureNamesJson = "[\"Old\"]",
                ComputedAt = now.UtcDateTime.AddMinutes(-1),
                IsDeleted = false,
                RowVersion = 1
            });
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var vectors = await harness.LoadFeatureVectorsAsync(ignoreQueryFilters: true);

        Assert.Equal(1, result.VectorCount);
        Assert.Single(vectors);
        Assert.False(vectors[0].IsDeleted);
        Assert.Equal(harness.FeatureStore.CurrentSchemaHash, vectors[0].SchemaHash);
        Assert.Equal(MLFeatureHelper.FeatureCount, vectors[0].FeatureCount);
    }

    [Fact]
    public async Task RunCycleAsync_CurrentSchemaRowWithCorruptPayload_IsRefreshedInPlace()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<Strategy>().Add(NewStrategy(1, "EURUSD", Timeframe.M5));
            var candles = NewCandles("EURUSD", Timeframe.M5, now.UtcDateTime.AddMinutes(-150), MLFeatureHelper.LookbackWindow + 1);
            db.Set<Candle>().AddRange(candles);
            db.Set<FeatureVector>().Add(new FeatureVector
            {
                CandleId = candles[^1].Id,
                Symbol = "EURUSD",
                Timeframe = Timeframe.M5,
                BarTimestamp = candles[^1].Timestamp,
                Features = [0x01, 0x02, 0x03, 0x04],
                SchemaVersion = 1,
                SchemaHash = DatabaseFeatureStore.ComputeCurrentSchemaVersion(),
                FeatureCount = MLFeatureHelper.FeatureCount,
                FeatureNamesJson = "[\"Bad\"]",
                ComputedAt = now.UtcDateTime.AddMinutes(-1),
                IsDeleted = false,
                RowVersion = 1
            });
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var vectors = await harness.LoadFeatureVectorsAsync(ignoreQueryFilters: true);

        Assert.Equal(1, result.VectorCount);
        var vector = Assert.Single(vectors);
        Assert.False(vector.IsDeleted);
        Assert.Equal(harness.FeatureStore.CurrentSchemaHash, vector.SchemaHash);
        Assert.Equal(MLFeatureHelper.FeatureCount * sizeof(double), vector.Features.Length);
        Assert.Equal(MLFeatureHelper.FeatureCount, vector.FeatureCount);
    }

    [Fact]
    public async Task RunCycleAsync_UsesPointInTimeCotLookup_WhenPrecomputingBar()
    {
        var now = new DateTimeOffset(2026, 04, 10, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<Strategy>().Add(NewStrategy(1, "USDJPY", Timeframe.M5));
            var candles = NewCandles("USDJPY", Timeframe.M5, now.UtcDateTime.AddMinutes(-150), MLFeatureHelper.LookbackWindow + 1);
            db.Set<Candle>().AddRange(candles);
            db.Set<COTReport>().AddRange(
                NewCotReport("USD", new DateTime(2026, 04, 01, 0, 0, 0, DateTimeKind.Utc), 100_000m, 10_000m),
                NewCotReport("USD", new DateTime(2026, 04, 12, 0, 0, 0, DateTimeKind.Utc), 300_000m, 30_000m));
        }, new TestTimeProvider(now));

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var stored = Assert.Single(await harness.LoadStoredVectorsAsync());
        var cotEntry = await harness.ResolveCotEntryAsync("USDJPY", stored.BarTimestamp);
        int cotNetIndex = Array.IndexOf(MLFeatureHelper.FeatureNames, "CotBaseNetNorm");
        int cotMomentumIndex = Array.IndexOf(MLFeatureHelper.FeatureNames, "CotBaseMomentum");
        int hasCotDataIndex = Array.IndexOf(MLFeatureHelper.FeatureNames, "HasCotData");

        Assert.True(cotNetIndex >= 0);
        Assert.True(cotMomentumIndex >= 0);
        Assert.True(hasCotDataIndex >= 0);
        Assert.Equal(cotEntry.NetNorm, stored.Features[cotNetIndex], precision: 6);
        Assert.Equal(cotEntry.Momentum, stored.Features[cotMomentumIndex], precision: 6);
        Assert.Equal(cotEntry.HasData ? 1d : 0d, stored.Features[hasCotDataIndex], precision: 6);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutWriting()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<Strategy>().Add(NewStrategy(1, "EURUSD", Timeframe.M5));
            db.Set<Candle>().AddRange(NewCandles("EURUSD", Timeframe.M5, now.UtcDateTime.AddMinutes(-150), MLFeatureHelper.LookbackWindow + 1));
        }, new TestTimeProvider(now), new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await harness.LoadFeatureVectorsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_WhenNoActivePairs_ReturnsSkippedReason()
    {
        using var harness = CreateHarness(_ => { });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("no_active_pairs", result.SkippedReason);
        Assert.Equal(0, result.ActivePairCount);
        Assert.Equal(0, result.EvaluatedPairCount);
    }

    [Fact]
    public async Task RunCycleAsync_InvalidConfigValues_AreClampedSafely()
    {
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().AddRange(
                NewConfig("FeaturePreComputation:PollIntervalSeconds", "-1"),
                NewConfig("FeaturePreComputation:CatchUpBarsPerPair", "0"));
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(5), result.Settings.PollInterval);
        Assert.Equal(1, result.Settings.CatchUpBarsPerPair);
    }

    private static WorkerHarness CreateHarness(
        Action<TestFeaturePreComputationDbContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<TestFeaturePreComputationDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<TestFeaturePreComputationDbContext>());
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<TestFeaturePreComputationDbContext>());
        services.AddSingleton<IFeatureStore>(sp => new DatabaseFeatureStore(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatabaseFeatureStore>.Instance));

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestFeaturePreComputationDbContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var featureStore = (DatabaseFeatureStore)provider.GetRequiredService<IFeatureStore>();
        var worker = new FeaturePreComputationWorker(
            NullLogger<FeaturePreComputationWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            metrics: null,
            timeProvider: timeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, connection, worker, featureStore);
    }

    private static Strategy NewStrategy(long id, string symbol, Timeframe timeframe)
        => new()
        {
            Id = id,
            Name = $"strategy-{id}",
            Description = $"strategy-{id}",
            Symbol = symbol,
            Timeframe = timeframe,
            StrategyType = StrategyType.CompositeML,
            ParametersJson = "{}",
            Status = StrategyStatus.Active,
            LifecycleStage = StrategyLifecycleStage.Active,
            LifecycleStageEnteredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            Generation = 0
        };

    private static List<Candle> NewCandles(string symbol, Timeframe timeframe, DateTime startUtc, int count)
    {
        var candles = new List<Candle>(count);
        decimal basePrice = 1.1000m;

        for (int i = 0; i < count; i++)
        {
            decimal open = basePrice + (0.0004m * i);
            decimal close = open + 0.0002m;
            candles.Add(new Candle
            {
                Id = i + 1,
                Symbol = symbol,
                Timeframe = timeframe,
                Open = open,
                High = close + 0.0003m,
                Low = open - 0.0003m,
                Close = close,
                Volume = 100 + i,
                Timestamp = startUtc.AddMinutes(5 * i),
                IsClosed = true,
                IsDeleted = false
            });
        }

        return candles;
    }

    private static EngineConfig NewConfig(string key, string value)
        => new()
        {
            Key = key,
            Value = value,
            IsDeleted = false
        };

    private static COTReport NewCotReport(string currency, DateTime reportDateUtc, decimal netPosition, decimal weeklyChange)
        => new()
        {
            Currency = currency,
            ReportDate = reportDateUtc,
            NetNonCommercialPositioning = netPosition,
            NetPositioningChangeWeekly = weeklyChange,
            IsDeleted = false
        };

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        FeaturePreComputationWorker worker,
        DatabaseFeatureStore featureStore) : IDisposable
    {
        public FeaturePreComputationWorker Worker { get; } = worker;
        public DatabaseFeatureStore FeatureStore { get; } = featureStore;

        public async Task<List<FeatureVector>> LoadFeatureVectorsAsync(bool ignoreQueryFilters = false)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TestFeaturePreComputationDbContext>();
            var query = db.Set<FeatureVector>().AsQueryable();
            if (ignoreQueryFilters)
                query = query.IgnoreQueryFilters();

            return await query.OrderBy(vector => vector.BarTimestamp).ToListAsync();
        }

        public async Task<List<StoredFeatureVector>> LoadStoredVectorsAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TestFeaturePreComputationDbContext>();
            var vectors = await db.Set<FeatureVector>()
                .OrderBy(vector => vector.BarTimestamp)
                .ToListAsync();

            return vectors.Select(vector => new StoredFeatureVector(
                    vector.CandleId,
                    vector.Symbol,
                    vector.Timeframe,
                    vector.BarTimestamp,
                    ToDoubleArray(vector.Features),
                    vector.SchemaVersion,
                    Array.Empty<string>())
                {
                    SchemaHash = vector.SchemaHash
                })
                .ToList();
        }

        public async Task<List<FeatureVectorLineage>> LoadLineagesAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TestFeaturePreComputationDbContext>();
            return await db.Set<FeatureVectorLineage>()
                .OrderBy(lineage => lineage.Id)
                .ToListAsync();
        }

        public async Task<CotFeatureEntry> ResolveCotEntryAsync(string symbol, DateTime asOfUtc)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TestFeaturePreComputationDbContext>();
            var snapshot = await CotFeatureLookupSnapshot.LoadAsync(db, symbol, CancellationToken.None);
            return snapshot.Resolve(asOfUtc);
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }

        private static double[] ToDoubleArray(byte[] bytes)
        {
            if (bytes.Length == 0)
                return Array.Empty<double>();

            var values = new double[bytes.Length / sizeof(double)];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }
    }

    private sealed class TestFeaturePreComputationDbContext(DbContextOptions<TestFeaturePreComputationDbContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EngineConfig>(builder =>
            {
                builder.HasKey(config => config.Id);
                builder.HasQueryFilter(config => !config.IsDeleted);
                builder.HasIndex(config => config.Key).IsUnique();
            });

            modelBuilder.Entity<Candle>(builder =>
            {
                builder.HasKey(candle => candle.Id);
                builder.HasQueryFilter(candle => !candle.IsDeleted);
                builder.Property(candle => candle.Timeframe).HasConversion<string>();
            });

            modelBuilder.Entity<COTReport>(builder =>
            {
                builder.HasKey(report => report.Id);
                builder.HasQueryFilter(report => !report.IsDeleted);
            });

            modelBuilder.Entity<Strategy>(builder =>
            {
                builder.HasKey(strategy => strategy.Id);
                builder.HasQueryFilter(strategy => !strategy.IsDeleted);
                builder.Property(strategy => strategy.Status).HasConversion<string>();
                builder.Property(strategy => strategy.StrategyType).HasConversion<string>();
                builder.Property(strategy => strategy.Timeframe).HasConversion<string>();
                builder.Property(strategy => strategy.LifecycleStage).HasConversion<string>();
                builder.Ignore(strategy => strategy.RiskProfile);
                builder.Ignore(strategy => strategy.TradeSignals);
                builder.Ignore(strategy => strategy.Orders);
                builder.Ignore(strategy => strategy.BacktestRuns);
                builder.Ignore(strategy => strategy.OptimizationRuns);
                builder.Ignore(strategy => strategy.WalkForwardRuns);
                builder.Ignore(strategy => strategy.Allocations);
                builder.Ignore(strategy => strategy.PerformanceSnapshots);
                builder.Ignore(strategy => strategy.ExecutionQualityLogs);
            });

            new FeatureVectorConfiguration().Configure(modelBuilder.Entity<FeatureVector>());
            new FeatureVectorLineageConfiguration().Configure(modelBuilder.Entity<FeatureVectorLineage>());

            // SQLite does not auto-maintain SQL Server-style rowversion columns in tests,
            // so we provide deterministic defaults to keep persistence behavior representative.
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
