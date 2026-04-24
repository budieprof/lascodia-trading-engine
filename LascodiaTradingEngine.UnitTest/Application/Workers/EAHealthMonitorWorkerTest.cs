using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class EAHealthMonitorWorkerTest : IDisposable
{
    public void Dispose() { }

    [Fact]
    public async Task RunCycleAsync_NoActiveInstancesAtStartup_TransitionsToDataUnavailableAndDispatchesAlert()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.EAInstances.Add(new EAInstance
        {
            Id = 1,
            InstanceId = "EA-OFFLINE",
            TradingAccountId = 101,
            Symbols = "EURUSD",
            Status = EAInstanceStatus.Disconnected,
            LastHeartbeat = now.AddMinutes(-20).UtcDateTime
        });
        await db.SaveChangesAsync();

        var degradationManager = new TestDegradationModeManager();
        var alertDispatcher = new TestAlertDispatcher(timeProvider);
        var eventBus = new RecordingIntegrationEventService();
        using var provider = BuildProvider(db, eventBus);
        var worker = CreateWorker(provider, degradationManager, alertDispatcher, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        var alert = await db.Alerts.SingleAsync();

        Assert.True(result.EnteredNoActiveState);
        Assert.False(result.RecoveredActiveState);
        Assert.Equal(0, result.StaleInstanceCount);
        Assert.Equal(DegradationMode.DataUnavailable, degradationManager.CurrentMode);
        Assert.True(alert.IsActive);
        Assert.Equal("EAHealthMonitor:NoActiveInstances", alert.DeduplicationKey);
        Assert.Single(alertDispatcher.DispatchedDeduplicationKeys);
        Assert.Empty(eventBus.PublishedEvents);
    }

    [Fact]
    public async Task RunCycleAsync_FreshActiveInstance_ResolvesNoActiveAlertAndReturnsToNormal()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.EAInstances.Add(new EAInstance
        {
            Id = 1,
            InstanceId = "EA-LIVE",
            TradingAccountId = 101,
            Symbols = "EURUSD",
            Status = EAInstanceStatus.Active,
            LastHeartbeat = now.AddSeconds(-5).UtcDateTime
        });
        db.Alerts.Add(new Alert
        {
            Id = 1,
            AlertType = AlertType.EADisconnected,
            DeduplicationKey = "EAHealthMonitor:NoActiveInstances",
            CooldownSeconds = 600,
            IsActive = true,
            LastTriggeredAt = now.AddMinutes(-30).UtcDateTime,
            ConditionJson = "{}",
            Severity = AlertSeverity.Critical
        });
        await db.SaveChangesAsync();

        var degradationManager = new TestDegradationModeManager(DegradationMode.DataUnavailable);
        var alertDispatcher = new TestAlertDispatcher(timeProvider);
        var eventBus = new RecordingIntegrationEventService();
        using var provider = BuildProvider(db, eventBus);
        var worker = CreateWorker(provider, degradationManager, alertDispatcher, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        var alert = await db.Alerts.SingleAsync();

        Assert.False(result.EnteredNoActiveState);
        Assert.True(result.RecoveredActiveState);
        Assert.Equal(DegradationMode.Normal, degradationManager.CurrentMode);
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
        Assert.Single(alertDispatcher.AutoResolvedDeduplicationKeys);
        Assert.Empty(alertDispatcher.DispatchedDeduplicationKeys);
    }

    [Fact]
    public async Task RunCycleAsync_StaleInstance_ReassignsSymbolsOnlyWithinSameTradingAccount()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.EAInstances.AddRange(
            new EAInstance
            {
                Id = 1,
                InstanceId = "EA-STALE",
                TradingAccountId = 101,
                Symbols = "GBPUSD,EURUSD",
                Status = EAInstanceStatus.Active,
                LastHeartbeat = now.AddMinutes(-2).UtcDateTime
            },
            new EAInstance
            {
                Id = 2,
                InstanceId = "EA-STANDBY-SAME-ACCOUNT",
                TradingAccountId = 101,
                Symbols = "USDJPY",
                Status = EAInstanceStatus.Active,
                LastHeartbeat = now.AddSeconds(-5).UtcDateTime
            },
            new EAInstance
            {
                Id = 3,
                InstanceId = "EA-OTHER-ACCOUNT",
                TradingAccountId = 202,
                Symbols = "AUDUSD",
                Status = EAInstanceStatus.Active,
                LastHeartbeat = now.AddSeconds(-5).UtcDateTime
            });
        await db.SaveChangesAsync();

        var degradationManager = new TestDegradationModeManager();
        var alertDispatcher = new TestAlertDispatcher(timeProvider);
        var eventBus = new RecordingIntegrationEventService();
        using var provider = BuildProvider(db, eventBus);
        var worker = CreateWorker(provider, degradationManager, alertDispatcher, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        var stale = await db.EAInstances.SingleAsync(x => x.Id == 1);
        var standby = await db.EAInstances.SingleAsync(x => x.Id == 2);
        var otherAccount = await db.EAInstances.SingleAsync(x => x.Id == 3);
        var disconnectedEvent = Assert.IsType<EAInstanceDisconnectedIntegrationEvent>(Assert.Single(eventBus.PublishedEvents));

        Assert.Equal(1, result.StaleInstanceCount);
        Assert.Equal(2, result.ReassignedSymbolCount);
        Assert.Equal(EAInstanceStatus.Disconnected, stale.Status);
        Assert.Equal(string.Empty, stale.Symbols);
        Assert.Equal("EURUSD,GBPUSD,USDJPY", standby.Symbols);
        Assert.Equal("AUDUSD", otherAccount.Symbols);
        Assert.Equal("EURUSD,GBPUSD", disconnectedEvent.OrphanedSymbols);
        Assert.Equal("EURUSD,GBPUSD", disconnectedEvent.ReassignedSymbols);
    }

    [Fact]
    public async Task RunCycleAsync_StaleCoordinator_FailsOverCoordinatorWithinAccount()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.EAInstances.AddRange(
            new EAInstance
            {
                Id = 1,
                InstanceId = "EA-COORDINATOR",
                TradingAccountId = 101,
                Symbols = "EURUSD",
                Status = EAInstanceStatus.Active,
                IsCoordinator = true,
                LastHeartbeat = now.AddMinutes(-2).UtcDateTime
            },
            new EAInstance
            {
                Id = 2,
                InstanceId = "EA-STANDBY",
                TradingAccountId = 101,
                Symbols = "GBPUSD",
                Status = EAInstanceStatus.Active,
                IsCoordinator = false,
                LastHeartbeat = now.AddSeconds(-3).UtcDateTime
            });
        await db.SaveChangesAsync();

        var degradationManager = new TestDegradationModeManager();
        var alertDispatcher = new TestAlertDispatcher(timeProvider);
        var eventBus = new RecordingIntegrationEventService();
        using var provider = BuildProvider(db, eventBus);
        var worker = CreateWorker(provider, degradationManager, alertDispatcher, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        var stale = await db.EAInstances.SingleAsync(x => x.Id == 1);
        var standby = await db.EAInstances.SingleAsync(x => x.Id == 2);

        Assert.Equal(1, result.CoordinatorFailoverCount);
        Assert.False(stale.IsCoordinator);
        Assert.True(standby.IsCoordinator);
        Assert.Equal(EAInstanceStatus.Disconnected, stale.Status);
    }

    [Fact]
    public async Task RunCycleAsync_ExistingRecentNoActiveAlert_IsNotRedispatchedWithinCooldown()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.EAInstances.Add(new EAInstance
        {
            Id = 1,
            InstanceId = "EA-OFFLINE",
            TradingAccountId = 101,
            Symbols = "EURUSD",
            Status = EAInstanceStatus.Disconnected,
            LastHeartbeat = now.AddMinutes(-20).UtcDateTime
        });
        db.Alerts.Add(new Alert
        {
            Id = 1,
            AlertType = AlertType.EADisconnected,
            DeduplicationKey = "EAHealthMonitor:NoActiveInstances",
            CooldownSeconds = 600,
            IsActive = true,
            LastTriggeredAt = now.AddMinutes(-5).UtcDateTime,
            ConditionJson = "{}",
            Severity = AlertSeverity.Critical
        });
        await db.SaveChangesAsync();

        var degradationManager = new TestDegradationModeManager();
        var alertDispatcher = new TestAlertDispatcher(timeProvider);
        var eventBus = new RecordingIntegrationEventService();
        using var provider = BuildProvider(db, eventBus);
        var worker = CreateWorker(provider, degradationManager, alertDispatcher, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.True(result.EnteredNoActiveState);
        Assert.Empty(alertDispatcher.DispatchedDeduplicationKeys);
        Assert.Equal(1, await db.Alerts.CountAsync());
        Assert.Equal(DegradationMode.DataUnavailable, degradationManager.CurrentMode);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsCycleWithoutMutatingState()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.EAInstances.Add(new EAInstance
        {
            Id = 1,
            InstanceId = "EA-STALE",
            TradingAccountId = 101,
            Symbols = "EURUSD",
            Status = EAInstanceStatus.Active,
            LastHeartbeat = now.AddMinutes(-2).UtcDateTime
        });
        await db.SaveChangesAsync();

        var degradationManager = new TestDegradationModeManager();
        var alertDispatcher = new TestAlertDispatcher(timeProvider);
        var eventBus = new RecordingIntegrationEventService();
        var distributedLock = new TestDistributedLock(lockAvailable: false);
        using var provider = BuildProvider(db, eventBus);
        var worker = CreateWorker(provider, degradationManager, alertDispatcher, timeProvider, distributedLock);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        var instance = await db.EAInstances.SingleAsync();

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(EAInstanceStatus.Active, instance.Status);
        Assert.Empty(eventBus.PublishedEvents);
        Assert.Empty(alertDispatcher.DispatchedDeduplicationKeys);
    }

    private static EAHealthMonitorWorker CreateWorker(
        ServiceProvider provider,
        IDegradationModeManager degradationManager,
        IAlertDispatcher alertDispatcher,
        TimeProvider timeProvider,
        IDistributedLock? distributedLock = null)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            degradationManager,
            alertDispatcher,
            NullLogger<EAHealthMonitorWorker>.Instance,
            metrics: null,
            timeProvider: timeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

    private static ServiceProvider BuildProvider(
        EAHealthMonitorTestContext context,
        IIntegrationEventService eventService)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWriteApplicationDbContext>(context);
        services.AddSingleton(eventService);
        return services.BuildServiceProvider();
    }

    private static EAHealthMonitorTestContext NewContext()
    {
        var options = new DbContextOptionsBuilder<EAHealthMonitorTestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new EAHealthMonitorTestContext(options);
    }

    private sealed class EAHealthMonitorTestContext(DbContextOptions<EAHealthMonitorTestContext> options)
        : DbContext(options), IWriteApplicationDbContext
    {
        public DbSet<EAInstance> EAInstances => Set<EAInstance>();
        public DbSet<Alert> Alerts => Set<Alert>();
        public DbSet<EngineConfig> EngineConfigs => Set<EngineConfig>();

        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EAInstance>().Ignore(x => x.TradingAccount);
        }
    }

    private sealed class RecordingIntegrationEventService : IIntegrationEventService
    {
        public List<IntegrationEvent> PublishedEvents { get; } = [];

        public async Task SaveAndPublish(IDbContext context, IntegrationEvent evt)
        {
            await context.SaveChangesAsync();
            PublishedEvents.Add(evt);
        }
    }

    private sealed class TestDegradationModeManager(DegradationMode initialMode = DegradationMode.Normal)
        : IDegradationModeManager
    {
        public DegradationMode CurrentMode { get; private set; } = initialMode;

        public List<(DegradationMode From, DegradationMode To, string Reason)> Transitions { get; } = [];

        public Task TransitionToAsync(DegradationMode newMode, string reason, CancellationToken cancellationToken)
        {
            Transitions.Add((CurrentMode, newMode, reason));
            CurrentMode = newMode;
            return Task.CompletedTask;
        }

        public void RecordSubsystemHeartbeat(string subsystemName) { }

        public bool IsSubsystemOperational(string subsystemName) => true;
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
}
