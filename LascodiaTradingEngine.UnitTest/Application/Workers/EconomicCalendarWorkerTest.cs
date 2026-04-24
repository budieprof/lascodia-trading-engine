using System.Diagnostics.Metrics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class EconomicCalendarWorkerTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;

    public EconomicCalendarWorkerTest()
    {
        _metrics = new TradingMetrics(_meterFactory);
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task RunCycleAsync_InlineRefreshPatchesActualForEventWithoutExternalKey()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.CurrencyPairs.Add(MakeCurrencyPair());
        db.EconomicEvents.Add(new EconomicEvent
        {
            Id = 1,
            Title = "US CPI YoY",
            Currency = "USD",
            Impact = EconomicImpact.High,
            ScheduledAt = now.AddMinutes(-30).UtcDateTime,
            Actual = null,
            ExternalKey = null,
            Source = EconomicEventSource.ForexFactory
        });
        await db.SaveChangesAsync();

        var feed = new TestEconomicCalendarFeed
        {
            UpcomingHandler = (_, fromUtc, _, _) =>
            {
                if (fromUtc < now.UtcDateTime)
                {
                    return Task.FromResult<IReadOnlyList<EconomicCalendarEvent>>(
                    [
                        new EconomicCalendarEvent(
                            "US CPI YoY",
                            "USD",
                            EconomicImpact.High,
                            now.AddMinutes(-30).UtcDateTime,
                            "3.2%",
                            "3.0%",
                            "3.1%",
                            "us-cpi-yoy",
                            EconomicEventSource.Investing)
                    ]);
                }

                return Task.FromResult<IReadOnlyList<EconomicCalendarEvent>>([]);
            }
        };

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, feed, dispatcher);
        var worker = CreateWorker(provider, NewOptions(), timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        var patchedEvent = await db.EconomicEvents.SingleAsync();

        Assert.Equal(1, result.PatchedActualCount);
        Assert.Equal(1, result.InlineActualCandidateCount);
        Assert.Equal("3.1%", patchedEvent.Actual);
        Assert.Equal(0, result.DispatchedAlertCount);
    }

    [Fact]
    public async Task RunCycleAsync_SustainedEmptyFeedDispatchesDurableAlert_AndRecoveryResolvesIt()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.CurrencyPairs.Add(MakeCurrencyPair());
        await db.SaveChangesAsync();

        var feed = new TestEconomicCalendarFeed();
        feed.UpcomingHandler = (_, fromUtc, _, _) =>
        {
            if (fromUtc < now.UtcDateTime)
                return Task.FromResult<IReadOnlyList<EconomicCalendarEvent>>([]);

            feed.UpcomingCalls++;
            return Task.FromResult<IReadOnlyList<EconomicCalendarEvent>>(feed.UpcomingCalls switch
            {
                1 => [],
                2 => [],
                _ =>
                [
                    new EconomicCalendarEvent(
                        "ECB Rate Decision",
                        "EUR",
                        EconomicImpact.High,
                        now.AddHours(3).UtcDateTime,
                        "3.00%",
                        "2.75%",
                        null,
                        "ecb-rate-1",
                        EconomicEventSource.ForexFactory)
                ]
            });
        };

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, feed, dispatcher);
        var worker = CreateWorker(
            provider,
            NewOptions(emptyThreshold: 2),
            timeProvider);

        var first = await worker.RunCycleAsync(CancellationToken.None);
        var second = await worker.RunCycleAsync(CancellationToken.None);
        var third = await worker.RunCycleAsync(CancellationToken.None);
        var alert = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "EconomicCalendar:SustainedEmptyFetches");

        Assert.Equal(0, first.DispatchedAlertCount);
        Assert.Equal(1, second.DispatchedAlertCount);
        Assert.True(alert.LastTriggeredAt.HasValue);
        Assert.Equal(1, third.ResolvedAlertCount);
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
        Assert.Contains("EconomicCalendar:SustainedEmptyFetches", dispatcher.DispatchedDeduplicationKeys);
        Assert.Contains("EconomicCalendar:SustainedEmptyFetches", dispatcher.AutoResolvedDeduplicationKeys);
    }

    [Fact]
    public async Task RunCycleAsync_FeedFailuresDispatchCircuitBreakerAlert_AndSuccessfulProbeResolvesIt()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.CurrencyPairs.Add(MakeCurrencyPair());
        await db.SaveChangesAsync();

        var feed = new TestEconomicCalendarFeed();
        feed.UpcomingHandler = (_, fromUtc, _, _) =>
        {
            if (fromUtc < now.UtcDateTime)
                return Task.FromResult<IReadOnlyList<EconomicCalendarEvent>>([]);

            feed.UpcomingCalls++;
            return feed.UpcomingCalls switch
            {
                <= 2 => throw new InvalidOperationException("upstream unavailable"),
                _ => Task.FromResult<IReadOnlyList<EconomicCalendarEvent>>(
                [
                    new EconomicCalendarEvent(
                        "BoE Rate Decision",
                        "GBP",
                        EconomicImpact.High,
                        now.AddHours(4).UtcDateTime,
                        "4.50%",
                        "4.50%",
                        null,
                        "boe-rate-1",
                        EconomicEventSource.ForexFactory)
                ])
            };
        };

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, feed, dispatcher);
        var worker = CreateWorker(
            provider,
            NewOptions(feedRetryCount: 0, circuitBreakerThreshold: 2),
            timeProvider);

        var first = await worker.RunCycleAsync(CancellationToken.None);
        var second = await worker.RunCycleAsync(CancellationToken.None);
        var third = await worker.RunCycleAsync(CancellationToken.None);
        var alert = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "EconomicCalendar:FeedCircuitBreaker");

        Assert.Equal(1, first.ConsecutiveFeedFailures);
        Assert.Equal(0, first.DispatchedAlertCount);
        Assert.Equal(2, second.ConsecutiveFeedFailures);
        Assert.Equal(1, second.DispatchedAlertCount);
        Assert.Equal(1, third.ResolvedAlertCount);
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
        Assert.Contains("EconomicCalendar:FeedCircuitBreaker", dispatcher.DispatchedDeduplicationKeys);
        Assert.Contains("EconomicCalendar:FeedCircuitBreaker", dispatcher.AutoResolvedDeduplicationKeys);
    }

    [Fact]
    public async Task RunCycleAsync_ConfigValuesAreClamped()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.CurrencyPairs.Add(MakeCurrencyPair());
        await db.SaveChangesAsync();

        var feed = new TestEconomicCalendarFeed
        {
            UpcomingHandler = (_, _, _, _) => Task.FromResult<IReadOnlyList<EconomicCalendarEvent>>([])
        };

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, feed, dispatcher);
        var worker = CreateWorker(
            provider,
            new EconomicCalendarOptions
            {
                PollingIntervalHours = -5,
                LookaheadDays = 0,
                ActualsPatchBatchSize = 0,
                StaleEventCutoffDays = 999,
                FeedCallTimeoutSeconds = 0,
                FeedRetryCount = -2,
                ActualsPatchRetryCount = 99,
                ActualsPatchMaxConcurrency = 999,
                SkipWeekends = false,
                FeedCircuitBreakerThreshold = 0,
                SustainedEmptyFetchThreshold = 0
            },
            timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(15), result.Settings.PollingInterval);
        Assert.Equal(1, result.Settings.LookaheadDays);
        Assert.Equal(1, result.Settings.ActualsPatchBatchSize);
        Assert.Equal(30, result.Settings.StaleEventCutoffDays);
        Assert.Equal(1, result.Settings.FeedCallTimeoutSeconds);
        Assert.Equal(0, result.Settings.FeedRetryCount);
        Assert.Equal(5, result.Settings.ActualsPatchRetryCount);
        Assert.Equal(20, result.Settings.ActualsPatchMaxConcurrency);
        Assert.Equal(1, result.Settings.FeedCircuitBreakerThreshold);
        Assert.Equal(1, result.Settings.SustainedEmptyFetchThreshold);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutCallingFeed()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.CurrencyPairs.Add(MakeCurrencyPair());
        await db.SaveChangesAsync();

        var feed = new TestEconomicCalendarFeed
        {
            UpcomingHandler = (_, _, _, _) => Task.FromResult<IReadOnlyList<EconomicCalendarEvent>>(
            [
                new EconomicCalendarEvent(
                    "US PCE",
                    "USD",
                    EconomicImpact.High,
                    now.AddHours(2).UtcDateTime,
                    "2.4%",
                    "2.5%",
                    null,
                    "us-pce-1",
                    EconomicEventSource.ForexFactory)
            ])
        };

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, feed, dispatcher);
        var worker = CreateWorker(
            provider,
            NewOptions(),
            timeProvider,
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(0, feed.TotalUpcomingInvocations);
        Assert.Equal(0, await db.EconomicEvents.CountAsync());
        Assert.Equal(0, await db.Alerts.CountAsync());
    }

    private EconomicCalendarWorker CreateWorker(
        ServiceProvider provider,
        EconomicCalendarOptions options,
        TimeProvider timeProvider,
        IDistributedLock? distributedLock = null)
        => new(
            NullLogger<EconomicCalendarWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            metrics: _metrics,
            timeProvider: timeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

    private static ServiceProvider BuildProvider(
        EconomicCalendarWorkerTestContext context,
        TestEconomicCalendarFeed feed,
        IAlertDispatcher dispatcher)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton<IWriteApplicationDbContext>(context);
        services.AddSingleton<IEconomicCalendarFeed>(feed);
        services.AddSingleton(dispatcher);
        return services.BuildServiceProvider();
    }

    private static EconomicCalendarOptions NewOptions(
        int emptyThreshold = 3,
        int feedRetryCount = 1,
        int circuitBreakerThreshold = 3)
        => new()
        {
            PollingIntervalHours = 6,
            LookaheadDays = 7,
            ActualsPatchBatchSize = 50,
            StaleEventCutoffDays = 7,
            FeedCallTimeoutSeconds = 30,
            FeedRetryCount = feedRetryCount,
            ActualsPatchRetryCount = 1,
            ActualsPatchMaxConcurrency = 4,
            SkipWeekends = false,
            FeedCircuitBreakerThreshold = circuitBreakerThreshold,
            SustainedEmptyFetchThreshold = emptyThreshold
        };

    private static EconomicCalendarWorkerTestContext NewContext()
    {
        var options = new DbContextOptionsBuilder<EconomicCalendarWorkerTestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new EconomicCalendarWorkerTestContext(options);
    }

    private static CurrencyPair MakeCurrencyPair()
        => new()
        {
            Id = 1,
            Symbol = "EURUSD",
            BaseCurrency = "EUR",
            QuoteCurrency = "USD",
            IsActive = true
        };

    private sealed class EconomicCalendarWorkerTestContext(DbContextOptions<EconomicCalendarWorkerTestContext> options)
        : DbContext(options), IWriteApplicationDbContext
    {
        public DbSet<Alert> Alerts => Set<Alert>();
        public DbSet<CurrencyPair> CurrencyPairs => Set<CurrencyPair>();
        public DbSet<EconomicEvent> EconomicEvents => Set<EconomicEvent>();
        public DbSet<EngineConfig> EngineConfigs => Set<EngineConfig>();

        public DbContext GetDbContext() => this;
    }

    private sealed class TestEconomicCalendarFeed : IEconomicCalendarFeed
    {
        public Func<IEnumerable<string>, DateTime, DateTime, CancellationToken, Task<IReadOnlyList<EconomicCalendarEvent>>> UpcomingHandler { get; set; }
            = (_, _, _, _) => Task.FromResult<IReadOnlyList<EconomicCalendarEvent>>([]);

        public Func<string, CancellationToken, Task<string?>> ActualHandler { get; set; }
            = (_, _) => Task.FromResult<string?>(null);

        public int TotalUpcomingInvocations { get; private set; }
        public int UpcomingCalls { get; set; }

        public async Task<IReadOnlyList<EconomicCalendarEvent>> GetUpcomingEventsAsync(
            IEnumerable<string> currencies,
            DateTime fromUtc,
            DateTime toUtc,
            CancellationToken ct)
        {
            TotalUpcomingInvocations++;
            return await UpcomingHandler(currencies, fromUtc, toUtc, ct);
        }

        public Task<string?> GetActualAsync(string externalKey, CancellationToken ct)
            => ActualHandler(externalKey, ct);
    }

    private sealed class TestAlertDispatcher(TimeProvider timeProvider) : IAlertDispatcher
    {
        public List<string?> DispatchedDeduplicationKeys { get; } = [];
        public List<string?> AutoResolvedDeduplicationKeys { get; } = [];

        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
        {
            alert.LastTriggeredAt = timeProvider.GetUtcNow().UtcDateTime;
            DispatchedDeduplicationKeys.Add(alert.DeduplicationKey);
            return Task.CompletedTask;
        }

        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
        {
            if (conditionStillActive || alert.AutoResolvedAt.HasValue)
                return Task.CompletedTask;

            alert.AutoResolvedAt = timeProvider.GetUtcNow().UtcDateTime;
            AutoResolvedDeduplicationKeys.Add(alert.DeduplicationKey);
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

    private sealed class TestMeterFactory : IMeterFactory
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
}
