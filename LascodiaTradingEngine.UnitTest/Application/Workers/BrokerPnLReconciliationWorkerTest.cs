using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class BrokerPnLReconciliationWorkerTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly FakeKillSwitchService _killSwitch = new();
    private readonly FakeDistributedLock _distributedLock = new();
    private readonly Mock<IAlertDispatcher> _alertDispatcher = new();

    public BrokerPnLReconciliationWorkerTest()
    {
        _metrics = new TradingMetrics(_meterFactory);
        _timeProvider.SetNow(new DateTime(2026, 4, 22, 12, 0, 0, DateTimeKind.Utc));

        _alertDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Alert, string, CancellationToken>((alert, _, _) => alert.LastTriggeredAt = _timeProvider.Now)
            .Returns(Task.CompletedTask);

        _alertDispatcher
            .Setup(d => d.TryAutoResolveAsync(It.IsAny<Alert>(), false, It.IsAny<CancellationToken>()))
            .Callback<Alert, bool, CancellationToken>((alert, _, _) => alert.AutoResolvedAt = _timeProvider.Now)
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task CriticalVariance_TripsGlobalKillSwitch_AndPersistsCriticalAlert()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 970m));
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.CriticalCount);
        Assert.True(result.KillSwitchActivated);
        Assert.Equal(1, _killSwitch.SetGlobalCount);
        Assert.Contains("critical threshold", _killSwitch.LastReason);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerPnLTestDbContext>();
        var alert = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "BrokerPnL:Variance:1:critical");
        Assert.True(alert.IsActive);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.NotNull(alert.LastTriggeredAt);

        _alertDispatcher.Verify(d => d.DispatchAsync(
                It.Is<Alert>(a => a.DeduplicationKey == "BrokerPnL:Variance:1:critical"),
                It.Is<string>(message => message.Contains("critical threshold", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WarningVariance_AlertsWithoutKillSwitch()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 992m));
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.WarningCount);
        Assert.Equal(0, result.CriticalCount);
        Assert.False(result.KillSwitchActivated);
        Assert.Equal(0, _killSwitch.SetGlobalCount);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerPnLTestDbContext>();
        var alert = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "BrokerPnL:Variance:1:warning");
        Assert.True(alert.IsActive);
        Assert.Equal(AlertSeverity.High, alert.Severity);
    }

    [Fact]
    public async Task StaleSnapshot_IsNotCompared_AndRaisesFreshnessAlert()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 970m, reportedAt: _timeProvider.Now.AddHours(-3)));
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.MissingFreshSnapshotCount);
        Assert.Equal(0, result.CheckedCount);
        Assert.Equal(0, result.CriticalCount);
        Assert.Equal(0, _killSwitch.SetGlobalCount);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerPnLTestDbContext>();
        var alert = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "BrokerPnL:StaleSnapshot:1");
        Assert.True(alert.IsActive);
        Assert.Equal(AlertSeverity.High, alert.Severity);
    }

    [Fact]
    public async Task HealthyVariance_ResolvesPriorVarianceAlerts()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 1000m));
            db.Alerts.Add(new Alert
            {
                AlertType = AlertType.DataQualityIssue,
                Severity = AlertSeverity.Critical,
                DeduplicationKey = "BrokerPnL:Variance:1:critical",
                ConditionJson = "{}",
                IsActive = true,
                LastTriggeredAt = _timeProvider.Now.AddMinutes(-30),
            });
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.OkCount);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerPnLTestDbContext>();
        var alert = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "BrokerPnL:Variance:1:critical");
        Assert.False(alert.IsActive);
        Assert.NotNull(alert.AutoResolvedAt);

        _alertDispatcher.Verify(d => d.TryAutoResolveAsync(
                It.Is<Alert>(a => a.DeduplicationKey == "BrokerPnL:Variance:1:critical"),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvalidBrokerEquity_SkipsComparison_AndRaisesInvalidEquityAlert()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 0m));
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.InvalidSnapshotCount);
        Assert.Equal(0, result.CriticalCount);
        Assert.Equal(0, _killSwitch.SetGlobalCount);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerPnLTestDbContext>();
        var alert = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "BrokerPnL:InvalidBrokerEquity:1");
        Assert.True(alert.IsActive);
        Assert.Equal(AlertSeverity.High, alert.Severity);
    }

    [Fact]
    public async Task ActiveAlertWithinPersistedCooldown_UpdatesConditionWithoutRedispatch()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 970m));
            db.Alerts.Add(new Alert
            {
                AlertType = AlertType.DataQualityIssue,
                Severity = AlertSeverity.Critical,
                DeduplicationKey = "BrokerPnL:Variance:1:critical",
                ConditionJson = "{}",
                IsActive = true,
                LastTriggeredAt = _timeProvider.Now.AddMinutes(-10),
                CooldownSeconds = 3600,
            });
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.CriticalCount);
        _alertDispatcher.Verify(d => d.DispatchAsync(
                It.Is<Alert>(a => a.DeduplicationKey == "BrokerPnL:Variance:1:critical"),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BusyDistributedLock_SkipsCycle()
    {
        using var provider = BuildProvider();
        var busyLock = new FakeDistributedLock(acquire: false);

        var result = await CreateWorker(provider, busyLock).ReconcileAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
    }

    [Fact]
    public async Task CurrencyMismatch_SkipsVarianceCheck_AndRaisesMismatchAlert()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 970m, currency: "EUR"));
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.CurrencyMismatchCount);
        Assert.Equal(0, result.CheckedCount);
        Assert.Equal(0, result.CriticalCount);
        Assert.False(result.KillSwitchActivated);
        Assert.Equal(0, _killSwitch.SetGlobalCount);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerPnLTestDbContext>();
        var alert = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "BrokerPnL:CurrencyMismatch:1");
        Assert.True(alert.IsActive);
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.Equal(AlertType.DataQualityIssue, alert.AlertType);

        Assert.DoesNotContain(await db.Alerts.ToListAsync(),
            a => a.DeduplicationKey == "BrokerPnL:Variance:1:critical");
    }

    [Fact]
    public async Task BrokerReportsNoCurrency_FallsBackToAccountCurrency_AndReconciles()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 970m, currency: string.Empty));
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, result.CurrencyMismatchCount);
        Assert.Equal(1, result.CheckedCount);
        Assert.Equal(1, result.CriticalCount);
    }

    [Fact]
    public async Task BalanceVariance_Critical_TrigggersAlertAndKillSwitch_EvenWhenEquityMatches()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m, balance: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 1000m, balance: 970m));
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.CriticalCount);
        Assert.True(result.KillSwitchActivated);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerPnLTestDbContext>();
        var balanceAlert = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "BrokerPnL:Variance:1:balance:critical");
        Assert.True(balanceAlert.IsActive);
        Assert.Equal(AlertSeverity.Critical, balanceAlert.Severity);
        Assert.Equal(AlertType.BrokerReconciliation, balanceAlert.AlertType);

        Assert.DoesNotContain(await db.Alerts.ToListAsync(),
            a => a.DeduplicationKey == "BrokerPnL:Variance:1:critical" && a.IsActive);
    }

    [Fact]
    public async Task StaleSnapshot_ResolvesPriorVarianceAlerts()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 970m, reportedAt: _timeProvider.Now.AddHours(-3)));
            db.Alerts.Add(new Alert
            {
                AlertType = AlertType.BrokerReconciliation,
                Severity = AlertSeverity.Critical,
                DeduplicationKey = "BrokerPnL:Variance:1:critical",
                ConditionJson = "{}",
                IsActive = true,
                LastTriggeredAt = _timeProvider.Now.AddMinutes(-30),
            });
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.MissingFreshSnapshotCount);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerPnLTestDbContext>();
        var variance = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "BrokerPnL:Variance:1:critical");
        Assert.False(variance.IsActive);
        Assert.NotNull(variance.AutoResolvedAt);

        var stale = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "BrokerPnL:StaleSnapshot:1");
        Assert.True(stale.IsActive);
    }

    [Fact]
    public async Task InvalidBrokerEquity_ResolvesPriorBalanceVarianceAlert()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 0m, balance: 0m));
            db.Alerts.Add(new Alert
            {
                AlertType = AlertType.BrokerReconciliation,
                Severity = AlertSeverity.Critical,
                DeduplicationKey = "BrokerPnL:Variance:1:balance:critical",
                ConditionJson = "{}",
                IsActive = true,
                LastTriggeredAt = _timeProvider.Now.AddMinutes(-30),
            });
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.InvalidSnapshotCount);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerPnLTestDbContext>();
        var balanceAlert = await db.Alerts.SingleAsync(a => a.DeduplicationKey == "BrokerPnL:Variance:1:balance:critical");
        Assert.False(balanceAlert.IsActive);
        Assert.NotNull(balanceAlert.AutoResolvedAt);
    }

    [Fact]
    public async Task WhitespaceOnlyBrokerCurrency_IsTreatedAsNotReported_AndReconciles()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 970m, currency: "   "));
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, result.CurrencyMismatchCount);
        Assert.Equal(1, result.CheckedCount);
        Assert.Equal(1, result.CriticalCount);
    }

    [Fact]
    public async Task CriticalVariance_BelowRequiredAccountThreshold_DoesNotTripKillSwitch()
    {
        using var provider = BuildProvider();
        await SeedAsync(provider, db =>
        {
            db.EngineConfigs.Add(new EngineConfig
            {
                Key = "BrokerPnLReconciliation:RequiredCriticalAccountsForGlobalKill",
                Value = "2",
                DataType = ConfigDataType.Int,
                IsHotReloadable = true,
            });
            db.TradingAccounts.Add(Account(equity: 1000m));
            db.BrokerAccountSnapshots.Add(Snapshot(equity: 970m));
        });

        var result = await CreateWorker(provider).ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, result.CriticalCount);
        Assert.False(result.KillSwitchActivated);
        Assert.Equal(0, _killSwitch.SetGlobalCount);
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        string databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<BrokerPnLTestDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<BrokerPnLTestDbContext>());
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<BrokerPnLTestDbContext>());
        services.AddSingleton(_alertDispatcher.Object);
        return services.BuildServiceProvider();
    }

    private BrokerPnLReconciliationWorker CreateWorker(
        IServiceProvider provider,
        IDistributedLock? distributedLock = null)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BrokerPnLReconciliationWorker>.Instance,
            _metrics,
            _killSwitch,
            distributedLock ?? _distributedLock,
            _timeProvider);

    private async Task SeedAsync(ServiceProvider provider, Action<BrokerPnLTestDbContext> seed)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerPnLTestDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private TradingAccount Account(decimal equity, decimal? balance = null)
        => new()
        {
            Id = 1,
            AccountId = "A-1",
            AccountName = "Primary",
            BrokerServer = "Demo-Server",
            BrokerName = "Demo",
            Currency = "USD",
            Equity = equity,
            Balance = balance ?? equity,
            IsActive = true,
            LastSyncedAt = _timeProvider.Now,
        };

    private BrokerAccountSnapshot Snapshot(
        decimal equity,
        DateTime? reportedAt = null,
        string currency = "USD",
        decimal? balance = null)
        => new()
        {
            Id = 10,
            TradingAccountId = 1,
            InstanceId = "ea-primary",
            Balance = balance ?? equity,
            Equity = equity,
            Currency = currency,
            FreeMargin = equity,
            ReportedAt = reportedAt ?? _timeProvider.Now.AddMinutes(-1),
        };

    private sealed class BrokerPnLTestDbContext(DbContextOptions<BrokerPnLTestDbContext> options)
        : DbContext(options), IWriteApplicationDbContext, IReadApplicationDbContext
    {
        public DbSet<TradingAccount> TradingAccounts => Set<TradingAccount>();
        public DbSet<BrokerAccountSnapshot> BrokerAccountSnapshots => Set<BrokerAccountSnapshot>();
        public DbSet<EngineConfig> EngineConfigs => Set<EngineConfig>();
        public DbSet<Alert> Alerts => Set<Alert>();

        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TradingAccount>().HasKey(x => x.Id);
            modelBuilder.Entity<TradingAccount>().Ignore(x => x.Orders);
            modelBuilder.Entity<TradingAccount>().Ignore(x => x.EAInstances);
            modelBuilder.Entity<BrokerAccountSnapshot>().HasKey(x => x.Id);
            modelBuilder.Entity<EngineConfig>().HasKey(x => x.Id);
            modelBuilder.Entity<Alert>().HasKey(x => x.Id);
        }
    }

    private sealed class FakeKillSwitchService : IKillSwitchService
    {
        public int SetGlobalCount { get; private set; }
        public string? LastReason { get; private set; }
        public bool GlobalKilled { get; private set; }

        public ValueTask<bool> IsGlobalKilledAsync(CancellationToken ct = default)
            => ValueTask.FromResult(GlobalKilled);

        public ValueTask<bool> IsStrategyKilledAsync(long strategyId, CancellationToken ct = default)
            => ValueTask.FromResult(false);

        public Task SetGlobalAsync(bool enabled, string reason, CancellationToken ct = default)
        {
            GlobalKilled = enabled;
            SetGlobalCount++;
            LastReason = reason;
            return Task.CompletedTask;
        }

        public Task SetStrategyAsync(long strategyId, bool enabled, string reason, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeDistributedLock(bool acquire = true) : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
            => TryAcquireAsync(lockKey, TimeSpan.Zero, ct);

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(acquire ? new Releaser() : null as IAsyncDisposable);

        public Task<IAsyncDisposable?> TryAcquireAsync(long lockId, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(acquire ? new Releaser() : null as IAsyncDisposable);

        private sealed class Releaser : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public DateTime Now => _now.UtcDateTime;
        public void SetNow(DateTime utc) => _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
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
