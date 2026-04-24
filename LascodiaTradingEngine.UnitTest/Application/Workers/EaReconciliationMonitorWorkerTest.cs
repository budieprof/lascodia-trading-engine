using System.Diagnostics.Metrics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class EaReconciliationMonitorWorkerTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;

    public EaReconciliationMonitorWorkerTest()
    {
        _metrics = new TradingMetrics(_meterFactory);
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task RunCycleAsync_AboveThreshold_PersistsAndDispatchesDurablePerInstanceAlert()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.EAInstances.Add(new EAInstance
        {
            Id = 1,
            InstanceId = "EA-BAD",
            TradingAccountId = 101,
            Status = EAInstanceStatus.Active,
            LastHeartbeat = now.AddSeconds(-5).UtcDateTime
        });
        db.ReconciliationRuns.AddRange(
            MakeRun("EA-BAD", now.AddMinutes(-5).UtcDateTime, totalDrift: 6, orphanedEnginePositions: 3, unknownBrokerPositions: 2, mismatched: 1),
            MakeRun("EA-BAD", now.AddMinutes(-10).UtcDateTime, totalDrift: 5, orphanedEnginePositions: 1, unknownBrokerPositions: 2, orphanedEngineOrders: 1, unknownBrokerOrders: 1));
        await db.SaveChangesAsync();

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, dispatcher);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        var alert = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "EAReconciliation:EA-BAD" && a.IsActive);

        Assert.Equal(2, result.WindowRunCount);
        Assert.Equal(1, result.AlertingInstanceCount);
        Assert.Equal(1, result.DispatchedAlertCount);
        Assert.Equal("EA-BAD", result.WorstInstanceId);
        Assert.Equal(AlertType.DataQualityIssue, alert.AlertType);
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.Equal(AlertCooldownDefaults.Default_Infrastructure, alert.CooldownSeconds);
        Assert.Contains("EA-BAD", alert.ConditionJson, StringComparison.Ordinal);
        Assert.Single(dispatcher.DispatchedDeduplicationKeys);
        Assert.Equal("EAReconciliation:EA-BAD", dispatcher.DispatchedDeduplicationKeys[0]);
    }

    [Fact]
    public async Task RunCycleAsync_SevereInstanceIsNotDilutedByHealthyNeighbors()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.ReconciliationRuns.AddRange(
            MakeRun("EA-BAD", now.AddMinutes(-4).UtcDateTime, totalDrift: 10),
            MakeRun("EA-BAD", now.AddMinutes(-8).UtcDateTime, totalDrift: 10));

        for (var minute = 1; minute <= 20; minute++)
        {
            db.ReconciliationRuns.Add(
                MakeRun("EA-GOOD", now.AddMinutes(-minute).UtcDateTime, totalDrift: 0));
        }

        await db.SaveChangesAsync();

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, dispatcher);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        var activeAlerts = await db.Alerts.Where(a => a.IsActive).ToListAsync();

        Assert.Equal(1, result.AlertingInstanceCount);
        Assert.Equal(1, result.DispatchedAlertCount);
        Assert.Single(activeAlerts);
        Assert.Equal("EAReconciliation:EA-BAD", activeAlerts[0].DeduplicationKey);
        Assert.DoesNotContain(activeAlerts, a => a.DeduplicationKey == "EAReconciliation:EA-GOOD");
    }

    [Fact]
    public async Task RunCycleAsync_BelowThreshold_ResolvesOwnedAlertWithoutTouchingUnrelatedAlert()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.Alerts.AddRange(
            new Alert
            {
                Id = 1,
                AlertType = AlertType.DataQualityIssue,
                DeduplicationKey = "EAReconciliation:EA-BAD",
                IsActive = true,
                CooldownSeconds = 600,
                LastTriggeredAt = now.AddMinutes(-30).UtcDateTime,
                ConditionJson = "{}",
                Severity = AlertSeverity.High
            },
            new Alert
            {
                Id = 2,
                AlertType = AlertType.BrokerReconciliation,
                DeduplicationKey = "BrokerPnL:Variance:1:critical",
                IsActive = true,
                CooldownSeconds = 600,
                LastTriggeredAt = now.AddMinutes(-30).UtcDateTime,
                ConditionJson = "{}",
                Severity = AlertSeverity.Critical
            });
        db.ReconciliationRuns.AddRange(
            MakeRun("EA-BAD", now.AddMinutes(-5).UtcDateTime, totalDrift: 1),
            MakeRun("EA-BAD", now.AddMinutes(-10).UtcDateTime, totalDrift: 0));
        await db.SaveChangesAsync();

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, dispatcher);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        var ownedAlert = await db.Alerts.SingleAsync(a => a.Id == 1);
        var unrelatedAlert = await db.Alerts.SingleAsync(a => a.Id == 2);

        Assert.Equal(0, result.AlertingInstanceCount);
        Assert.Equal(0, result.DispatchedAlertCount);
        Assert.Equal(1, result.ResolvedAlertCount);
        Assert.False(ownedAlert.IsActive);
        Assert.NotNull(ownedAlert.AutoResolvedAt);
        Assert.True(unrelatedAlert.IsActive);
        Assert.Contains("EAReconciliation:EA-BAD", dispatcher.AutoResolvedDeduplicationKeys);
        Assert.Empty(dispatcher.DispatchedDeduplicationKeys);
    }

    [Fact]
    public async Task RunCycleAsync_NoRuns_ResolvesLingeringWorkerAlerts()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.Alerts.Add(new Alert
        {
            Id = 1,
            AlertType = AlertType.DataQualityIssue,
            DeduplicationKey = "EAReconciliation:EA-STALE",
            IsActive = true,
            CooldownSeconds = 600,
            LastTriggeredAt = now.AddMinutes(-40).UtcDateTime,
            ConditionJson = "{}",
            Severity = AlertSeverity.High
        });
        await db.SaveChangesAsync();

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, dispatcher);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        var alert = await db.Alerts.SingleAsync(a => a.Id == 1);

        Assert.Equal(0, result.WindowRunCount);
        Assert.Equal(1, result.ResolvedAlertCount);
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);
        Assert.Contains("EAReconciliation:EA-STALE", dispatcher.AutoResolvedDeduplicationKeys);
        Assert.Empty(dispatcher.DispatchedDeduplicationKeys);
    }

    [Fact]
    public async Task RunCycleAsync_RecentAlertWithinCooldown_IsNotRedispatched()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.Alerts.Add(new Alert
        {
            Id = 1,
            AlertType = AlertType.DataQualityIssue,
            DeduplicationKey = "EAReconciliation:EA-BAD",
            IsActive = true,
            CooldownSeconds = 600,
            LastTriggeredAt = now.AddMinutes(-5).UtcDateTime,
            ConditionJson = "{}",
            Severity = AlertSeverity.High
        });
        db.ReconciliationRuns.AddRange(
            MakeRun("EA-BAD", now.AddMinutes(-5).UtcDateTime, totalDrift: 7),
            MakeRun("EA-BAD", now.AddMinutes(-10).UtcDateTime, totalDrift: 6));
        await db.SaveChangesAsync();

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, dispatcher);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        var alert = await db.Alerts.SingleAsync(a => a.Id == 1);

        Assert.Equal(1, result.AlertingInstanceCount);
        Assert.Equal(0, result.DispatchedAlertCount);
        Assert.True(alert.IsActive);
        Assert.Equal(now.AddMinutes(-5).UtcDateTime, alert.LastTriggeredAt);
        Assert.Empty(dispatcher.DispatchedDeduplicationKeys);
    }

    [Fact]
    public async Task RunCycleAsync_ConfigValuesAreClamped_AndWindowCoversPollInterval()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.EngineConfigs.AddRange(
            MakeConfig("Recon:MonitorIntervalMinutes", "999"),
            MakeConfig("Recon:MonitorWindowMinutes", "1"),
            MakeConfig("Recon:MeanDriftAlertThreshold", "0"));
        db.ReconciliationRuns.Add(MakeRun("EA-CFG", now.AddMinutes(-50).UtcDateTime, totalDrift: 2));
        await db.SaveChangesAsync();

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, dispatcher);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(60, result.Settings.PollMinutes);
        Assert.Equal(60, result.Settings.WindowMinutes);
        Assert.Equal(1, result.Settings.MeanDriftAlertThreshold);
        Assert.Equal(1, result.WindowRunCount);
        Assert.Equal(1, result.AlertingInstanceCount);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutMutatingState()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var db = NewContext();
        db.ReconciliationRuns.Add(MakeRun("EA-BAD", now.AddMinutes(-5).UtcDateTime, totalDrift: 10));
        await db.SaveChangesAsync();

        var dispatcher = new TestAlertDispatcher(timeProvider);
        using var provider = BuildProvider(db, dispatcher);
        var worker = CreateWorker(provider, timeProvider, new TestDistributedLock(lockAvailable: false));

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(0, await db.Alerts.CountAsync());
        Assert.Empty(dispatcher.DispatchedDeduplicationKeys);
    }

    private EaReconciliationMonitorWorker CreateWorker(
        ServiceProvider provider,
        TimeProvider timeProvider,
        IDistributedLock? distributedLock = null)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EaReconciliationMonitorWorker>.Instance,
            metrics: _metrics,
            timeProvider: timeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

    private static ServiceProvider BuildProvider(
        EaReconciliationMonitorTestContext context,
        IAlertDispatcher alertDispatcher)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWriteApplicationDbContext>(context);
        services.AddSingleton<IAlertDispatcher>(alertDispatcher);
        return services.BuildServiceProvider();
    }

    private static EaReconciliationMonitorTestContext NewContext()
    {
        var options = new DbContextOptionsBuilder<EaReconciliationMonitorTestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new EaReconciliationMonitorTestContext(options);
    }

    private static ReconciliationRun MakeRun(
        string instanceId,
        DateTime runAtUtc,
        int totalDrift = 0,
        int orphanedEnginePositions = 0,
        int unknownBrokerPositions = 0,
        int mismatched = 0,
        int orphanedEngineOrders = 0,
        int unknownBrokerOrders = 0)
    {
        int sum = orphanedEnginePositions + unknownBrokerPositions + mismatched + orphanedEngineOrders + unknownBrokerOrders;
        return new ReconciliationRun
        {
            InstanceId = instanceId,
            RunAt = runAtUtc,
            OrphanedEnginePositions = orphanedEnginePositions,
            UnknownBrokerPositions = unknownBrokerPositions,
            MismatchedPositions = mismatched,
            OrphanedEngineOrders = orphanedEngineOrders,
            UnknownBrokerOrders = unknownBrokerOrders,
            TotalDrift = sum == 0 ? totalDrift : sum
        };
    }

    private static EngineConfig MakeConfig(string key, string value)
        => new()
        {
            Key = key,
            Value = value
        };

    private sealed class EaReconciliationMonitorTestContext(DbContextOptions<EaReconciliationMonitorTestContext> options)
        : DbContext(options), IWriteApplicationDbContext
    {
        public DbSet<Alert> Alerts => Set<Alert>();
        public DbSet<EAInstance> EAInstances => Set<EAInstance>();
        public DbSet<EngineConfig> EngineConfigs => Set<EngineConfig>();
        public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();

        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EAInstance>().Ignore(x => x.TradingAccount);
        }
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
