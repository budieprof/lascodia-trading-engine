using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class SlippageDriftWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_DriftAboveThreshold_DispatchesAlert()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var dispatcher = new RecordingAlertDispatcher();

        using var harness = CreateHarness(
            seed: db =>
            {
                // Recent window (last 7 days): 25 trades at high slippage
                db.Set<TransactionCostAnalysis>().AddRange(BuildTcaSeries(
                    "EURUSD", count: 25,
                    spreadCost: 1.0m, marketImpactCost: 0.5m,
                    startUtc: now.AddDays(-3).UtcDateTime));

                // Baseline window (8–37 days ago): 50 trades at low slippage
                db.Set<TransactionCostAnalysis>().AddRange(BuildTcaSeries(
                    "EURUSD", count: 50,
                    spreadCost: 0.4m, marketImpactCost: 0.1m,
                    startUtc: now.AddDays(-20).UtcDateTime));
            },
            timeProvider: new TestTimeProvider(now),
            alertDispatcher: dispatcher);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(1, result.SymbolsEvaluated);
        Assert.Equal(1, result.DriftsDetected);

        var dispatched = Assert.Single(dispatcher.Dispatched);
        Assert.Equal("EURUSD", dispatched.alert.Symbol);
        Assert.Equal(AlertType.MLModelDegraded, dispatched.alert.AlertType);
        Assert.Equal(AlertSeverity.High, dispatched.alert.Severity);
        Assert.Equal("slippage-drift:EURUSD", dispatched.alert.DeduplicationKey);
    }

    [Fact]
    public async Task RunCycleAsync_DriftBelowThreshold_DoesNotDispatch()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var dispatcher = new RecordingAlertDispatcher();

        using var harness = CreateHarness(
            seed: db =>
            {
                // Recent and baseline at near-identical slippage: ratio ≈ 1.05 < threshold 1.5
                db.Set<TransactionCostAnalysis>().AddRange(BuildTcaSeries(
                    "EURUSD", count: 25,
                    spreadCost: 0.5m, marketImpactCost: 0.05m,
                    startUtc: now.AddDays(-3).UtcDateTime));
                db.Set<TransactionCostAnalysis>().AddRange(BuildTcaSeries(
                    "EURUSD", count: 50,
                    spreadCost: 0.5m, marketImpactCost: 0.0m,
                    startUtc: now.AddDays(-20).UtcDateTime));
            },
            timeProvider: new TestTimeProvider(now),
            alertDispatcher: dispatcher);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.SymbolsEvaluated);
        Assert.Equal(0, result.DriftsDetected);
        Assert.Empty(dispatcher.Dispatched);
    }

    [Fact]
    public async Task RunCycleAsync_LowTradeCount_SkipsSymbol()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var dispatcher = new RecordingAlertDispatcher();

        using var harness = CreateHarness(
            seed: db =>
            {
                // Only 5 recent trades — below default min (20)
                db.Set<TransactionCostAnalysis>().AddRange(BuildTcaSeries(
                    "EURUSD", count: 5,
                    spreadCost: 5m, marketImpactCost: 5m,
                    startUtc: now.AddDays(-3).UtcDateTime));
                db.Set<TransactionCostAnalysis>().AddRange(BuildTcaSeries(
                    "EURUSD", count: 50,
                    spreadCost: 0.1m, marketImpactCost: 0m,
                    startUtc: now.AddDays(-20).UtcDateTime));
            },
            timeProvider: new TestTimeProvider(now),
            alertDispatcher: dispatcher);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.SymbolsEvaluated);
        Assert.Empty(dispatcher.Dispatched);
    }

    [Fact]
    public async Task RunCycleAsync_Disabled_SkipsCycle()
    {
        using var harness = CreateHarness(
            seed: db =>
            {
                db.Set<EngineConfig>().Add(new EngineConfig
                {
                    Key = "SlippageDrift:Enabled",
                    Value = "false",
                    DataType = ConfigDataType.String,
                    IsHotReloadable = true,
                    LastUpdatedAt = DateTime.UtcNow,
                    IsDeleted = false,
                });
            });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("disabled", result.SkippedReason);
    }

    [Fact]
    public async Task RunCycleAsync_LegacyEnabledKey_StillRespected()
    {
        using var harness = CreateHarness(
            seed: db =>
            {
                db.Set<EngineConfig>().Add(new EngineConfig
                {
                    Key = "SlippageDriftWorker:Enabled",
                    Value = "false",
                    DataType = ConfigDataType.String,
                    IsHotReloadable = true,
                    LastUpdatedAt = DateTime.UtcNow,
                    IsDeleted = false,
                });
            });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("disabled", result.SkippedReason);
    }

    [Fact]
    public async Task RunCycleAsync_LockBusy_SkipsWithoutMutating()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: _ => { },
            timeProvider: new TestTimeProvider(now),
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
    }

    [Fact]
    public async Task RunCycleAsync_InvalidSettingsClamped()
    {
        using var harness = CreateHarness(seed: db =>
        {
            AddConfig(db, "SlippageDrift:PollIntervalSeconds", "-1");
            AddConfig(db, "SlippageDrift:DriftThreshold", "0.5"); // <= 1 → invalid
            AddConfig(db, "SlippageDrift:RecentWindowDays", "0");
            AddConfig(db, "SlippageDrift:BaselineWindowDays", "0");
            AddConfig(db, "SlippageDrift:MinTradesInWindow", "0");
            AddConfig(db, "SlippageDrift:LockTimeoutSeconds", "-2");
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(1800), result.Settings.PollInterval);
        Assert.Equal(1.5, result.Settings.DriftThreshold, 6);
        Assert.Equal(7, result.Settings.RecentWindowDays);
        Assert.Equal(30, result.Settings.BaselineWindowDays);
        Assert.Equal(20, result.Settings.MinTradesInWindow);
        Assert.Equal(5, result.Settings.LockTimeoutSeconds);
    }

    private static IReadOnlyList<TransactionCostAnalysis> BuildTcaSeries(
        string symbol, int count, decimal spreadCost, decimal marketImpactCost, DateTime startUtc)
    {
        var rows = new List<TransactionCostAnalysis>(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new TransactionCostAnalysis
            {
                OrderId = i + 1 + (long)(startUtc - DateTime.UnixEpoch).TotalSeconds,
                Symbol = symbol,
                ArrivalPrice = 1m,
                FillPrice = 1m,
                SubmissionPrice = 1m,
                ImplementationShortfall = 0m,
                DelayCost = 0m,
                MarketImpactCost = marketImpactCost,
                SpreadCost = spreadCost,
                CommissionCost = 0m,
                TotalCost = spreadCost + marketImpactCost,
                TotalCostBps = 0m,
                Quantity = 1m,
                AnalyzedAt = startUtc.AddMinutes(i),
                IsDeleted = false,
            });
        }
        return rows;
    }

    private static void AddConfig(SlippageDriftWorkerTestContext db, string key, string value)
    {
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = ConfigDataType.String,
            IsHotReloadable = true,
            LastUpdatedAt = DateTime.UtcNow,
            IsDeleted = false,
        });
    }

    private static WorkerHarness CreateHarness(
        Action<SlippageDriftWorkerTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null,
        IAlertDispatcher? alertDispatcher = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<SlippageDriftWorkerTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<SlippageDriftWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<SlippageDriftWorkerTestContext>());

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SlippageDriftWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new SlippageDriftWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SlippageDriftWorker>.Instance,
            distributedLock: distributedLock,
            healthMonitor: null,
            metrics: null,
            timeProvider: timeProvider,
            alertDispatcher: alertDispatcher);

        return new WorkerHarness(provider, connection, worker);
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        SlippageDriftWorker worker) : IDisposable
    {
        public SlippageDriftWorker Worker { get; } = worker;

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class SlippageDriftWorkerTestContext(DbContextOptions<SlippageDriftWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EngineConfig>(builder =>
            {
                builder.HasKey(c => c.Id);
                builder.HasQueryFilter(c => !c.IsDeleted);
                builder.Property(c => c.DataType).HasConversion<string>();
                builder.HasIndex(c => c.Key).IsUnique();
            });

            modelBuilder.Entity<TransactionCostAnalysis>(builder =>
            {
                builder.HasKey(t => t.Id);
                builder.HasQueryFilter(t => !t.IsDeleted);
                builder.Ignore(t => t.Order);
                builder.Ignore(t => t.TradeSignal);
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

    private sealed class RecordingAlertDispatcher : IAlertDispatcher
    {
        public List<(Alert alert, string message)> Dispatched { get; } = new();

        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
        {
            Dispatched.Add((alert, message));
            return Task.CompletedTask;
        }

        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
            => Task.CompletedTask;
    }
}
