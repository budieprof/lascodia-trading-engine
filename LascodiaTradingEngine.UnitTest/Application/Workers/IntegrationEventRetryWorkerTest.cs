using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class IntegrationEventRetryWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_RetryablePublishedFailedEvent_RePublishesAndMarksPublished()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var entry = NewEntry(EventStateEnum.PublishedFailed, timesSent: 1, now.UtcDateTime.AddMinutes(-10));

        using var harness = CreateHarness(
            entries: [entry],
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.RetriedCount);
        Assert.Equal(EventStateEnum.Published, entry.State);
        Assert.Equal(2, entry.TimesSent);
        Assert.Single(harness.EventBus.PublishedEvents);
        Assert.Empty(harness.DeadLetterSink.Writes);
    }

    [Fact]
    public async Task RunCycleAsync_ExhaustedRetryableEvent_DeadLettersAndMarksDeadLettered()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var entry = NewEntry(EventStateEnum.PublishedFailed, timesSent: 5, now.UtcDateTime.AddMinutes(-10));

        using var harness = CreateHarness(
            entries: [entry],
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.ExhaustedCount);
        Assert.Equal(1, result.DeadLetteredCount);
        Assert.Equal(EventStateEnum.DeadLettered, entry.State);
        Assert.Single(harness.DeadLetterSink.Writes);
        Assert.Empty(harness.EventBus.PublishedEvents);
    }

    [Fact]
    public async Task RunCycleAsync_UnrecoverableRetryablePayload_DeadLettersAndMarksDeadLettered()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var entry = NewEntry(EventStateEnum.PublishedFailed, timesSent: 1, now.UtcDateTime.AddMinutes(-10));
        SetEntryProperty(entry, nameof(IntegrationEventLogEntry.Content), "{not-json");

        using var harness = CreateHarness(
            entries: [entry],
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.DeadLetteredCount);
        Assert.Equal(EventStateEnum.DeadLettered, entry.State);
        Assert.Single(harness.DeadLetterSink.Writes);
        Assert.Empty(harness.EventBus.PublishedEvents);
    }

    [Fact]
    public async Task RunCycleAsync_StalePublishedEvent_IsSafetyReplayedOnlyOnce()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var entry = NewEntry(EventStateEnum.Published, timesSent: 1, now.UtcDateTime.AddMinutes(-10));

        using var harness = CreateHarness(
            entries: [entry],
            timeProvider: new TestTimeProvider(now));

        var first = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var second = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, first.StaleRepublishedCount);
        Assert.Equal(0, second.StaleRepublishedCount);
        Assert.Equal(EventStateEnum.Published, entry.State);
        Assert.Equal(2, entry.TimesSent);
        Assert.Single(harness.EventBus.PublishedEvents);
    }

    [Fact]
    public async Task RunCycleAsync_StalePublishedReplayFailure_LeavesStatePublishedAndAgesOut()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var entry = NewEntry(EventStateEnum.Published, timesSent: 1, now.UtcDateTime.AddMinutes(-10));
        var eventBus = new FakeEventBus(new InvalidOperationException("broker down"));

        using var harness = CreateHarness(
            entries: [entry],
            eventBus: eventBus,
            timeProvider: new TestTimeProvider(now));

        var first = await harness.Worker.RunCycleAsync(CancellationToken.None);
        var second = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, first.StaleRepublishedCount);
        Assert.Equal(0, second.StaleRepublishedCount);
        Assert.Equal(EventStateEnum.Published, entry.State);
        Assert.Equal(2, entry.TimesSent);
        Assert.Equal(1, harness.EventBus.PublishAttempts);
        Assert.Empty(harness.DeadLetterSink.Writes);
    }

    [Fact]
    public async Task RunCycleAsync_WhenEventBusDegraded_SkipsRetryAndStalePaths()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var retryable = NewEntry(EventStateEnum.PublishedFailed, timesSent: 1, now.UtcDateTime.AddMinutes(-10));
        var stale = NewEntry(EventStateEnum.Published, timesSent: 1, now.UtcDateTime.AddMinutes(-10));
        var degradationManager = new TestDegradationModeManager { CurrentMode = DegradationMode.EventBusDegraded };

        using var harness = CreateHarness(
            entries: [retryable, stale],
            degradationManager: degradationManager,
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("event_bus_degraded", result.SkippedReason);
        Assert.Equal(EventStateEnum.PublishedFailed, retryable.State);
        Assert.Equal(EventStateEnum.Published, stale.State);
        Assert.Empty(harness.EventBus.PublishedEvents);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutPublishing()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var entry = NewEntry(EventStateEnum.PublishedFailed, timesSent: 1, now.UtcDateTime.AddMinutes(-10));

        using var harness = CreateHarness(
            entries: [entry],
            timeProvider: new TestTimeProvider(now),
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(EventStateEnum.PublishedFailed, entry.State);
        Assert.Empty(harness.EventBus.PublishedEvents);
    }

    [Fact]
    public async Task RunCycleAsync_InvalidConfigValues_AreClampedSafely()
    {
        using var harness = CreateHarness(
            entries: [],
            seedConfig: db =>
            {
                db.Set<EngineConfig>().AddRange(
                    NewConfig("IntegrationEventRetry:PollIntervalSeconds", "-1"),
                    NewConfig("IntegrationEventRetry:StuckThresholdSeconds", "0"),
                    NewConfig("IntegrationEventRetry:StalePublishedThresholdSeconds", "5"),
                    NewConfig("IntegrationEventRetry:MaxRetries", "0"),
                    NewConfig("IntegrationEventRetry:BatchSize", "0"));
            });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(5), result.Settings.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Settings.StuckThreshold);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Settings.StalePublishedThreshold);
        Assert.Equal(1, result.Settings.MaxRetries);
        Assert.Equal(1, result.Settings.BatchSize);
    }

    private static WorkerHarness CreateHarness(
        IReadOnlyList<IntegrationEventLogEntry> entries,
        Action<TestIntegrationEventRetryDbContext>? seedConfig = null,
        FakeEventBus? eventBus = null,
        TestDegradationModeManager? degradationManager = null,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var services = new ServiceCollection();
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<TestIntegrationEventRetryDbContext>(options =>
            options.UseInMemoryDatabase(databaseName, databaseRoot));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<TestIntegrationEventRetryDbContext>());

        var eventLogReader = new FakeEventLogReader(entries);
        var deadLetterSink = new FakeDeadLetterSink();
        var effectiveEventBus = eventBus ?? new FakeEventBus();
        var effectiveDegradationManager = degradationManager ?? new TestDegradationModeManager();

        services.AddSingleton<IEventLogReader>(eventLogReader);
        services.AddSingleton<IDeadLetterSink>(deadLetterSink);

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestIntegrationEventRetryDbContext>();
            db.Database.EnsureCreated();
            seedConfig?.Invoke(db);
            db.SaveChanges();
        }

        var worker = new IntegrationEventRetryWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            effectiveEventBus,
            effectiveDegradationManager,
            NullLogger<IntegrationEventRetryWorker>.Instance,
            metrics: null,
            timeProvider: timeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, worker, eventLogReader, deadLetterSink, effectiveEventBus);
    }

    private static IntegrationEventLogEntry NewEntry(
        EventStateEnum state,
        int timesSent,
        DateTime creationTimeUtc)
    {
        var @event = new WorkerTestIntegrationEvent(Guid.NewGuid(), creationTimeUtc, $"evt-{Guid.NewGuid():N}");
        var entry = new IntegrationEventLogEntry(@event, Guid.NewGuid())
        {
            State = state,
            TimesSent = timesSent
        };

        return entry;
    }

    private static void SetEntryProperty(IntegrationEventLogEntry entry, string propertyName, object value)
    {
        typeof(IntegrationEventLogEntry)
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(entry, value);
    }

    private static EngineConfig NewConfig(string key, string value)
        => new()
        {
            Key = key,
            Value = value,
            IsDeleted = false
        };

    private sealed class WorkerHarness(
        ServiceProvider provider,
        IntegrationEventRetryWorker worker,
        FakeEventLogReader eventLogReader,
        FakeDeadLetterSink deadLetterSink,
        FakeEventBus eventBus) : IDisposable
    {
        public IntegrationEventRetryWorker Worker { get; } = worker;
        public FakeEventLogReader EventLogReader { get; } = eventLogReader;
        public FakeDeadLetterSink DeadLetterSink { get; } = deadLetterSink;
        public FakeEventBus EventBus { get; } = eventBus;

        public void Dispose() => provider.Dispose();
    }

    private sealed class FakeEventLogReader(IReadOnlyList<IntegrationEventLogEntry> seededEntries) : IEventLogReader
    {
        private readonly List<IntegrationEventLogEntry> _entries = seededEntries.ToList();

        public Task<List<IntegrationEventLogEntry>> GetRetryableEventsAsync(
            DateTime stuckInProgressBeforeUtc,
            int batchSize,
            CancellationToken ct)
            => Task.FromResult(_entries
                .Where(entry => entry.State == EventStateEnum.PublishedFailed
                             || (entry.State == EventStateEnum.InProgress && entry.CreationTime < stuckInProgressBeforeUtc))
                .OrderBy(entry => entry.CreationTime)
                .Take(batchSize)
                .ToList());

        public Task<IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot>> GetEventStatusSnapshotsAsync(
            IReadOnlyCollection<Guid> eventIds,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot>>(
                _entries
                    .Where(entry => eventIds.Contains(entry.EventId))
                    .ToDictionary(
                        entry => entry.EventId,
                        entry => new IntegrationEventStatusSnapshot(
                            entry.EventId,
                            entry.State,
                            entry.TimesSent,
                            entry.CreationTime)));

        public Task<List<IntegrationEventLogEntry>> GetStalePublishedEventsAsync(
            DateTime stalePublishedBeforeUtc,
            int maxTimesSentExclusive,
            int batchSize,
            CancellationToken ct)
            => Task.FromResult(_entries
                .Where(entry => entry.State == EventStateEnum.Published
                             && entry.CreationTime < stalePublishedBeforeUtc
                             && entry.TimesSent < maxTimesSentExclusive)
                .OrderBy(entry => entry.CreationTime)
                .Take(batchSize)
                .ToList());

        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeDeadLetterSink : IDeadLetterSink
    {
        public List<DeadLetterWrite> Writes { get; } = [];

        public Task WriteAsync(
            string handlerName,
            string eventType,
            string eventPayloadJson,
            string errorMessage,
            string? stackTrace,
            int attempts,
            CancellationToken ct = default)
        {
            Writes.Add(new DeadLetterWrite(handlerName, eventType, eventPayloadJson, errorMessage, attempts));
            return Task.CompletedTask;
        }
    }

    private sealed record DeadLetterWrite(
        string HandlerName,
        string EventType,
        string EventPayloadJson,
        string ErrorMessage,
        int Attempts);

    private sealed class FakeEventBus(Exception? publishException = null) : IEventBus
    {
        private readonly Exception? _publishException = publishException;

        public List<IntegrationEvent> PublishedEvents { get; } = [];
        public int PublishAttempts { get; private set; }

        public void Publish(IntegrationEvent @event)
        {
            PublishAttempts++;

            if (_publishException is not null)
                throw _publishException;

            PublishedEvents.Add(@event);
        }

        public void Subscribe<T, TH>()
            where T : IntegrationEvent
            where TH : IIntegrationEventHandler<T>
        {
        }

        public void Subscribe(Type handler)
        {
        }

        public void SubscribeDynamic<TH>(string eventName)
            where TH : IDynamicIntegrationEventHandler
        {
        }

        public void UnsubscribeDynamic<TH>(string eventName)
            where TH : IDynamicIntegrationEventHandler
        {
        }

        public void Unsubscribe<T, TH>()
            where TH : IIntegrationEventHandler<T>
            where T : IntegrationEvent
        {
        }

        public void Unsubscribe(Type handlerType)
        {
        }
    }

    private sealed class TestDegradationModeManager : IDegradationModeManager
    {
        public DegradationMode CurrentMode { get; set; } = DegradationMode.Normal;

        public Task TransitionToAsync(DegradationMode newMode, string reason, CancellationToken cancellationToken)
        {
            CurrentMode = newMode;
            return Task.CompletedTask;
        }

        public void RecordSubsystemHeartbeat(string subsystemName)
        {
        }

        public bool IsSubsystemOperational(string subsystemName) => true;
    }

    private sealed class TestIntegrationEventRetryDbContext(DbContextOptions<TestIntegrationEventRetryDbContext> options)
        : DbContext(options), IWriteApplicationDbContext
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

public sealed record WorkerTestIntegrationEvent : IntegrationEvent
{
    public WorkerTestIntegrationEvent()
    {
    }

    public WorkerTestIntegrationEvent(Guid eventId, DateTime createdAtUtc, string message)
        : base(eventId, createdAtUtc)
    {
        Message = message;
    }

    public string Message { get; set; } = string.Empty;
}
