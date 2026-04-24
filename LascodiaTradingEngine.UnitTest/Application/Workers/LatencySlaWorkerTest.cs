using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class LatencySlaWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_UsesActualSignalToFillSamples_ForTotalTickToFillSla()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: db =>
            {
                db.Set<TransactionCostAnalysis>().AddRange(
                    NewTca(1, 1500, now.AddMinutes(-10).UtcDateTime),
                    NewTca(2, 3200, now.AddMinutes(-8).UtcDateTime),
                    NewTca(3, 4100, now.AddMinutes(-5).UtcDateTime));
            },
            configureOptions: options =>
            {
                options.ConsecutiveBreachMinutesBeforeAlert = 1;
                options.MinimumSegmentSamples = 1;
                options.MinimumTotalTickToFillSamples = 3;
                options.TotalTickToFillP99Ms = 3000;
            },
            now: now);

        harness.Recorder.RecordSample(LatencySlaSegments.TickToSignal, 100, now.AddMinutes(-1));
        harness.Recorder.RecordSample(LatencySlaSegments.SignalToTier1, 50, now.AddMinutes(-1));
        harness.Recorder.RecordSample(LatencySlaSegments.Tier2RiskCheck, 80, now.AddMinutes(-1));
        harness.Recorder.RecordSample(LatencySlaSegments.EaPollToSubmit, 120, now.AddMinutes(-1));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var alert = Assert.Single(await harness.LoadAlertsAsync());
        Assert.Equal(1, result.DispatchedAlertCount);
        Assert.Equal(BuildDeduplicationKey(LatencySlaSegments.TotalTickToFill), alert.DeduplicationKey);
        Assert.True(alert.IsActive);

        using var conditionJson = JsonDocument.Parse(alert.ConditionJson);
        Assert.Equal(4100, conditionJson.RootElement.GetProperty("actualP99Ms").GetInt64());
        Assert.Equal(4100, conditionJson.RootElement.GetProperty("peakP99Ms").GetInt64());
        Assert.Equal(3, conditionJson.RootElement.GetProperty("sampleCount").GetInt32());
    }

    [Fact]
    public async Task RunCycleAsync_FreshCompliantSample_ResolvesActiveAlert()
    {
        var initialNow = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(initialNow);
        using var harness = CreateHarness(
            seed: _ => { },
            configureOptions: options =>
            {
                options.ConsecutiveBreachMinutesBeforeAlert = 1;
                options.MinimumSegmentSamples = 1;
                options.TickToSignalP99Ms = 500;
            },
            now: initialNow,
            timeProvider: timeProvider);

        harness.Recorder.RecordSample(LatencySlaSegments.TickToSignal, 900, initialNow);
        var firstResult = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, firstResult.DispatchedAlertCount);
        var activeAlert = Assert.Single(await harness.LoadAlertsAsync());
        Assert.True(activeAlert.IsActive);
        Assert.NotNull(activeAlert.LastTriggeredAt);

        var recoveryNow = initialNow.AddHours(2);
        timeProvider.SetUtcNow(recoveryNow);
        harness.Recorder.RecordSample(LatencySlaSegments.TickToSignal, 200, recoveryNow);

        var secondResult = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, secondResult.ResolvedAlertCount);
        var resolvedAlert = Assert.Single(await harness.LoadAlertsAsync(ignoreQueryFilters: true));
        Assert.False(resolvedAlert.IsActive);
        Assert.NotNull(resolvedAlert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunCycleAsync_BelowMinimumSegmentSamples_DoesNotAlert()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: _ => { },
            configureOptions: options =>
            {
                options.ConsecutiveBreachMinutesBeforeAlert = 1;
                options.MinimumSegmentSamples = 2;
                options.TickToSignalP99Ms = 500;
            },
            now: now);

        harness.Recorder.RecordSample(LatencySlaSegments.TickToSignal, 1200, now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.EvaluatedSegmentCount);
        Assert.True(result.InsufficientSampleSegmentCount >= 1);
        Assert.Empty(await harness.LoadAlertsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutMutatingAlerts()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: _ => { },
            configureOptions: options =>
            {
                options.ConsecutiveBreachMinutesBeforeAlert = 1;
                options.MinimumSegmentSamples = 1;
            },
            now: now,
            distributedLock: new TestDistributedLock(lockAvailable: false));

        harness.Recorder.RecordSample(LatencySlaSegments.TickToSignal, 900, now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Empty(await harness.LoadAlertsAsync());
    }

    [Fact]
    public async Task RunCycleAsync_InvalidSettingsAreClampedSafely()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(
            seed: _ => { },
            configureOptions: options =>
            {
                options.PollIntervalMinutes = -5;
                options.TickToSignalP99Ms = -1;
                options.SignalToTier1P99Ms = 0;
                options.Tier2RiskCheckP99Ms = -10;
                options.EaPollToSubmitP99Ms = -20;
                options.TotalTickToFillP99Ms = -30;
                options.ConsecutiveBreachMinutesBeforeAlert = 0;
                options.MinimumSegmentSamples = 0;
                options.TotalTickToFillLookbackHours = 0;
                options.MinimumTotalTickToFillSamples = 0;
            },
            now: now);

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(1), result.Settings.PollInterval);
        Assert.Equal(500, result.Settings.TickToSignalP99Ms);
        Assert.Equal(200, result.Settings.SignalToTier1P99Ms);
        Assert.Equal(100, result.Settings.Tier2RiskCheckP99Ms);
        Assert.Equal(1000, result.Settings.EaPollToSubmitP99Ms);
        Assert.Equal(3000, result.Settings.TotalTickToFillP99Ms);
        Assert.Equal(5, result.Settings.ConsecutiveBreachMinutesBeforeAlert);
        Assert.Equal(5, result.Settings.MinimumSegmentSamples);
        Assert.Equal(24, result.Settings.TotalTickToFillLookbackHours);
        Assert.Equal(10, result.Settings.MinimumTotalTickToFillSamples);
    }

    private static WorkerHarness CreateHarness(
        Action<LatencySlaWorkerTestContext> seed,
        Action<LatencySlaOptions>? configureOptions,
        DateTimeOffset now,
        TestTimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<LatencySlaWorkerTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetRequiredService<LatencySlaWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(provider => provider.GetRequiredService<LatencySlaWorkerTestContext>());

        var recorder = new LatencySlaRecorder();
        var alertDispatcher = new TestAlertDispatcher(timeProvider ?? new TestTimeProvider(now));
        services.AddSingleton<IAlertDispatcher>(alertDispatcher);
        services.AddSingleton<ILatencySlaRecorder>(recorder);

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LatencySlaWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var options = new LatencySlaOptions();
        configureOptions?.Invoke(options);

        var worker = new LatencySlaWorker(
            NullLogger<LatencySlaWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            recorder,
            metrics: null,
            timeProvider: timeProvider ?? new TestTimeProvider(now),
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, connection, worker, recorder);
    }

    private static TransactionCostAnalysis NewTca(long id, long signalToFillMs, DateTime analyzedAtUtc)
        => new()
        {
            Id = id,
            OrderId = id,
            Symbol = "EURUSD",
            ArrivalPrice = 1.1000m,
            FillPrice = 1.1002m,
            SubmissionPrice = 1.1001m,
            ImplementationShortfall = 0m,
            DelayCost = 0m,
            MarketImpactCost = 0m,
            SpreadCost = 0m,
            CommissionCost = 0m,
            TotalCost = 0m,
            TotalCostBps = 0m,
            Quantity = 1m,
            SignalToFillMs = signalToFillMs,
            SubmissionToFillMs = signalToFillMs / 2,
            AnalyzedAt = analyzedAtUtc,
            IsDeleted = false
        };

    private static string BuildDeduplicationKey(string slaName)
        => $"latency-sla:{slaName}";

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        LatencySlaWorker worker,
        LatencySlaRecorder recorder) : IDisposable
    {
        public LatencySlaWorker Worker { get; } = worker;
        public LatencySlaRecorder Recorder { get; } = recorder;

        public async Task<List<Alert>> LoadAlertsAsync(bool ignoreQueryFilters = false)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LatencySlaWorkerTestContext>();
            var query = db.Set<Alert>().AsQueryable();
            if (ignoreQueryFilters)
                query = query.IgnoreQueryFilters();

            return await query
                .OrderBy(alert => alert.Id)
                .ToListAsync();
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class LatencySlaWorkerTestContext(DbContextOptions<LatencySlaWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Alert>(builder =>
            {
                builder.HasKey(alert => alert.Id);
                builder.Property(alert => alert.AlertType).HasConversion<string>();
                builder.Property(alert => alert.Severity).HasConversion<string>();
                builder.HasQueryFilter(alert => !alert.IsDeleted);
                builder.HasIndex(alert => alert.DeduplicationKey)
                    .IsUnique()
                    .HasFilter("\"IsActive\" = TRUE AND \"IsDeleted\" = FALSE AND \"DeduplicationKey\" IS NOT NULL");
            });

            modelBuilder.Entity<EngineConfig>(builder =>
            {
                builder.HasKey(config => config.Id);
                builder.HasQueryFilter(config => !config.IsDeleted);
                builder.HasIndex(config => config.Key).IsUnique();
            });

            modelBuilder.Entity<TransactionCostAnalysis>(builder =>
            {
                builder.HasKey(row => row.Id);
                builder.HasQueryFilter(row => !row.IsDeleted);
                builder.Ignore(row => row.Order);
                builder.Ignore(row => row.TradeSignal);
            });
        }
    }

    private sealed class TestAlertDispatcher(TimeProvider timeProvider) : IAlertDispatcher
    {
        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
        {
            alert.LastTriggeredAt = timeProvider.GetUtcNow().UtcDateTime;
            return Task.CompletedTask;
        }

        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
        {
            alert.AutoResolvedAt = timeProvider.GetUtcNow().UtcDateTime;
            return Task.CompletedTask;
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
