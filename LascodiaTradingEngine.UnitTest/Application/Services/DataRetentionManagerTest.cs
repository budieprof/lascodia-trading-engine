using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public sealed class DataRetentionManagerTest
{
    [Fact]
    public async Task EnforceRetentionAsync_PurgesExpiredCandles()
    {
        await using var harness = await CreateHarnessAsync(new DataRetentionOptions
        {
            CandleHotDays = 30,
            BatchSize = 10
        });

        harness.Db.Set<Candle>().AddRange(
            new Candle
            {
                Id = 1,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Open = 1.1m,
                High = 1.2m,
                Low = 1.0m,
                Close = 1.15m,
                Volume = 100,
                Timestamp = DateTime.UtcNow.AddDays(-31),
                IsClosed = true,
                IsDeleted = false
            },
            new Candle
            {
                Id = 2,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Open = 1.1m,
                High = 1.2m,
                Low = 1.0m,
                Close = 1.15m,
                Volume = 100,
                Timestamp = DateTime.UtcNow.AddDays(-29),
                IsClosed = true,
                IsDeleted = false
            });
        await harness.Db.SaveChangesAsync();

        var results = await harness.Manager.EnforceRetentionAsync(CancellationToken.None);

        var remaining = await harness.Db.Set<Candle>()
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync();

        Assert.Equal([2L], remaining);
        Assert.Equal(1, results.Single(r => r.EntityType == "Candle").RowsPurged);
    }

    [Fact]
    public async Task EnforceRetentionAsync_HardDeletesOnlyAlreadyRetiredPredictionLogs()
    {
        await using var harness = await CreateHarnessAsync(new DataRetentionOptions
        {
            PredictionLogHotDays = 90,
            BatchSize = 10
        });

        harness.Db.Set<MLModelPredictionLog>().AddRange(
            new MLModelPredictionLog
            {
                Id = 1,
                TradeSignalId = 101,
                MLModelId = 201,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                PredictedDirection = TradeDirection.Buy,
                PredictedMagnitudePips = 12m,
                ConfidenceScore = 0.7m,
                PredictedAt = DateTime.UtcNow.AddDays(-120),
                IsDeleted = false
            },
            new MLModelPredictionLog
            {
                Id = 2,
                TradeSignalId = 102,
                MLModelId = 202,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                PredictedDirection = TradeDirection.Sell,
                PredictedMagnitudePips = 8m,
                ConfidenceScore = 0.6m,
                PredictedAt = DateTime.UtcNow.AddDays(-120),
                IsDeleted = true
            });
        await harness.Db.SaveChangesAsync();

        var results = await harness.Manager.EnforceRetentionAsync(CancellationToken.None);

        var remaining = await harness.Db.Set<MLModelPredictionLog>()
            .IgnoreQueryFilters()
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.IsDeleted })
            .ToListAsync();

        var survivor = Assert.Single(remaining);
        Assert.Equal(1L, survivor.Id);
        Assert.False(survivor.IsDeleted);
        Assert.Equal(1, results.Single(r => r.EntityType == "MLModelPredictionLog").RowsPurged);
    }

    [Fact]
    public async Task EnforceRetentionAsync_PrunesOldestPendingModelStrategyFirst()
    {
        await using var harness = await CreateHarnessAsync(new DataRetentionOptions
        {
            PendingModelStrategyTtlDays = 7,
            BatchSize = 1
        });

        harness.Db.Set<Strategy>().AddRange(
            new Strategy
            {
                Id = 10,
                Name = "NewestExpired",
                Symbol = "EURUSD",
                StrategyType = StrategyType.MovingAverageCrossover,
                Timeframe = Timeframe.H1,
                LifecycleStage = StrategyLifecycleStage.PendingModel,
                LifecycleStageEnteredAt = DateTime.UtcNow.AddDays(-10),
                Status = StrategyStatus.Paused,
                IsDeleted = false
            },
            new Strategy
            {
                Id = 20,
                Name = "OldestExpired",
                Symbol = "GBPUSD",
                StrategyType = StrategyType.MovingAverageCrossover,
                Timeframe = Timeframe.H1,
                LifecycleStage = StrategyLifecycleStage.PendingModel,
                LifecycleStageEnteredAt = DateTime.UtcNow.AddDays(-20),
                Status = StrategyStatus.Paused,
                IsDeleted = false
            });
        await harness.Db.SaveChangesAsync();

        var results = await harness.Manager.EnforceRetentionAsync(CancellationToken.None);

        var strategies = await harness.Db.Set<Strategy>()
            .IgnoreQueryFilters()
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.IsDeleted, s.PrunedAtUtc, s.PauseReason })
            .ToListAsync();

        Assert.Collection(strategies,
            first =>
            {
                Assert.Equal(10L, first.Id);
                Assert.False(first.IsDeleted);
                Assert.Null(first.PrunedAtUtc);
                Assert.Null(first.PauseReason);
            },
            second =>
            {
                Assert.Equal(20L, second.Id);
                Assert.True(second.IsDeleted);
                Assert.NotNull(second.PrunedAtUtc);
                Assert.Contains("PendingModel TTL expired", second.PauseReason, StringComparison.Ordinal);
            });
        Assert.Equal(1, results.Single(r => r.EntityType == "Strategy.PendingModel").RowsPurged);
    }

    [Fact]
    public async Task PurgeExpiredIdempotencyKeysAsync_DeletesOldestExpiredKeyFirst()
    {
        await using var harness = await CreateHarnessAsync(new DataRetentionOptions
        {
            BatchSize = 1
        });

        harness.Db.Set<ProcessedIdempotencyKey>().AddRange(
            new ProcessedIdempotencyKey
            {
                Id = 1,
                Key = "oldest",
                Endpoint = "/ea/heartbeat",
                ResponseStatusCode = 200,
                ResponseBodyJson = "{}",
                ProcessedAt = DateTime.UtcNow.AddDays(-3),
                ExpiresAt = DateTime.UtcNow.AddDays(-2),
                IsDeleted = false
            },
            new ProcessedIdempotencyKey
            {
                Id = 2,
                Key = "newer",
                Endpoint = "/ea/heartbeat",
                ResponseStatusCode = 200,
                ResponseBodyJson = "{}",
                ProcessedAt = DateTime.UtcNow.AddDays(-2),
                ExpiresAt = DateTime.UtcNow.AddDays(-1),
                IsDeleted = false
            });
        await harness.Db.SaveChangesAsync();

        var purged = await harness.Manager.PurgeExpiredIdempotencyKeysAsync(CancellationToken.None);

        var remainingKeys = await harness.Db.Set<ProcessedIdempotencyKey>()
            .OrderBy(k => k.Id)
            .Select(k => k.Key)
            .ToListAsync();

        Assert.Equal(1, purged);
        Assert.Equal(["newer"], remainingKeys);
    }

    private static async Task<ManagerHarness> CreateHarnessAsync(DataRetentionOptions options)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var dbOptions = new DbContextOptionsBuilder<TestDataRetentionDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new TestDataRetentionDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "IntegrationEventLog" (
                "EventId" TEXT NOT NULL PRIMARY KEY,
                "State" INTEGER NOT NULL,
                "CreationTime" TEXT NOT NULL
            );
            """);

        var manager = new DataRetentionManager(
            db,
            options,
            NullLogger<DataRetentionManager>.Instance);

        return new ManagerHarness(connection, db, manager);
    }

    private sealed class ManagerHarness(
        SqliteConnection connection,
        TestDataRetentionDbContext db,
        DataRetentionManager manager) : IAsyncDisposable
    {
        public TestDataRetentionDbContext Db { get; } = db;
        public DataRetentionManager Manager { get; } = manager;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestDataRetentionDbContext(DbContextOptions<TestDataRetentionDbContext> options)
        : DbContext(options), IWriteApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MLModelPredictionLog>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.Ignore(x => x.TradeSignal);
                builder.Ignore(x => x.MLModel);
                builder.Ignore(x => x.MLConformalCalibration);
            });

            modelBuilder.Entity<TickRecord>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
            });

            modelBuilder.Entity<Candle>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
            });

            modelBuilder.Entity<WorkerHealthSnapshot>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
            });

            modelBuilder.Entity<MarketDataAnomaly>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
            });

            modelBuilder.Entity<DecisionLog>(builder =>
            {
                builder.HasKey(x => x.Id);
            });

            modelBuilder.Entity<Strategy>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.Ignore(x => x.RiskProfile);
                builder.Ignore(x => x.TradeSignals);
                builder.Ignore(x => x.Orders);
                builder.Ignore(x => x.BacktestRuns);
                builder.Ignore(x => x.OptimizationRuns);
                builder.Ignore(x => x.WalkForwardRuns);
                builder.Ignore(x => x.Allocations);
                builder.Ignore(x => x.PerformanceSnapshots);
                builder.Ignore(x => x.ExecutionQualityLogs);
            });

            modelBuilder.Entity<ProcessedIdempotencyKey>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
            });
        }
    }
}
