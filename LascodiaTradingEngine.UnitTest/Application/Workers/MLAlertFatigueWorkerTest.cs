using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class MLAlertFatigueWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_HighRecentMlAlertVolumeLowRemediation_DispatchesFatigueAlert()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLAlertFatigue:MinAlertThreshold", "4");
                AddConfig(db, "MLAlertFatigue:MinRemediatedRatio", "0.50");

                db.Set<Alert>().AddRange(
                    NewMlAlert(1, now.AddHours(-8).UtcDateTime, isActive: true),
                    NewMlAlert(2, now.AddHours(-7).UtcDateTime, isActive: true),
                    NewMlAlert(3, now.AddHours(-6).UtcDateTime, isActive: true),
                    NewMlAlert(4, now.AddHours(-5).UtcDateTime, isActive: false, autoResolvedAt: now.AddHours(-4).UtcDateTime));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var fatigueAlert = Assert.Single(await harness.LoadAlertsAsync(), alert => alert.DeduplicationKey == "ml-alert-fatigue");
        Assert.Null(result.SkippedReason);
        Assert.Equal(4, result.TotalTriggeredAlerts);
        Assert.Equal(1, result.RemediatedAlerts);
        Assert.Equal(3, result.ActiveAlerts);
        Assert.Equal(0.25, result.RemediatedRatio, 6);
        Assert.True(result.FatigueDetected);
        Assert.Equal(1, result.DispatchedAlertCount);
        Assert.Equal(0, result.ResolvedAlertCount);

        Assert.Equal(AlertType.ConfigurationDrift, fatigueAlert.AlertType);
        Assert.True(fatigueAlert.IsActive);
        Assert.NotNull(fatigueAlert.LastTriggeredAt);
        Assert.Equal(AlertSeverity.Critical, fatigueAlert.Severity);

        var totalTriggered = await harness.LoadConfigAsync("MLAlertFatigue:TotalTriggeredAlerts");
        var remediated = await harness.LoadConfigAsync("MLAlertFatigue:RemediatedAlerts");
        var active = await harness.LoadConfigAsync("MLAlertFatigue:ActiveAlerts");
        var ratio = await harness.LoadConfigAsync("MLAlertFatigue:RemediatedRatio");
        Assert.Equal("4", totalTriggered?.Value);
        Assert.Equal("1", remediated?.Value);
        Assert.Equal("3", active?.Value);
        Assert.Equal("0.2500", ratio?.Value);
    }

    [Fact]
    public async Task RunCycleAsync_LegacySelfMetaAlert_IsExcludedFromMeasuredWindow_AndResolved()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLAlertFatigue:MinAlertThreshold", "4");
                AddConfig(db, "MLAlertFatigue:MinRemediatedRatio", "0.50");

                db.Set<Alert>().AddRange(
                    NewMlAlert(1, now.AddHours(-8).UtcDateTime, isActive: true),
                    NewMlAlert(2, now.AddHours(-7).UtcDateTime, isActive: true),
                    NewMlAlert(3, now.AddHours(-6).UtcDateTime, isActive: true),
                    NewMlAlert(
                        99,
                        now.AddHours(-1).UtcDateTime,
                        isActive: true,
                        alertType: AlertType.MLModelDegraded,
                        deduplicationKey: "ml-alert-fatigue"));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var fatigueAlert = Assert.Single(
            await harness.LoadAlertsAsync(ignoreQueryFilters: true),
            alert => alert.DeduplicationKey == "ml-alert-fatigue");
        Assert.Null(result.SkippedReason);
        Assert.Equal(3, result.TotalTriggeredAlerts);
        Assert.False(result.FatigueDetected);
        Assert.Equal(0, result.DispatchedAlertCount);
        Assert.Equal(1, result.ResolvedAlertCount);

        Assert.False(fatigueAlert.IsActive);
        Assert.NotNull(fatigueAlert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunCycleAsync_AlertsWithoutRecentTrigger_AreExcluded()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLAlertFatigue:MinAlertThreshold", "1");
                AddConfig(db, "MLAlertFatigue:MinRemediatedRatio", "1.00");

                db.Set<Alert>().Add(new Alert
                {
                    Id = 1,
                    AlertType = AlertType.MLModelDegraded,
                    ConditionJson = "{}",
                    IsActive = true,
                    Severity = AlertSeverity.Medium,
                    CooldownSeconds = 3600,
                    LastTriggeredAt = null,
                    IsDeleted = false
                });
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Null(result.SkippedReason);
        Assert.Equal(0, result.TotalTriggeredAlerts);
        Assert.Equal(0, result.DispatchedAlertCount);
        Assert.DoesNotContain(await harness.LoadAlertsAsync(), alert => alert.DeduplicationKey == "ml-alert-fatigue");
    }

    [Fact]
    public async Task RunCycleAsync_FatigueClears_ResolvesExistingMetaAlert()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLAlertFatigue:MinAlertThreshold", "3");
                AddConfig(db, "MLAlertFatigue:MinRemediatedRatio", "0.40");

                db.Set<Alert>().AddRange(
                    NewMlAlert(1, now.AddHours(-6).UtcDateTime, isActive: false, autoResolvedAt: now.AddHours(-5).UtcDateTime),
                    NewMlAlert(2, now.AddHours(-5).UtcDateTime, isActive: false, autoResolvedAt: now.AddHours(-4).UtcDateTime),
                    NewMlAlert(3, now.AddHours(-4).UtcDateTime, isActive: false, autoResolvedAt: now.AddHours(-3).UtcDateTime),
                    NewMlAlert(
                        50,
                        now.AddHours(-2).UtcDateTime,
                        isActive: true,
                        alertType: AlertType.ConfigurationDrift,
                        deduplicationKey: "ml-alert-fatigue"));
            },
            timeProvider: new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var fatigueAlert = Assert.Single(
            await harness.LoadAlertsAsync(ignoreQueryFilters: true),
            alert => alert.DeduplicationKey == "ml-alert-fatigue");
        Assert.Null(result.SkippedReason);
        Assert.Equal(3, result.TotalTriggeredAlerts);
        Assert.Equal(3, result.RemediatedAlerts);
        Assert.False(result.FatigueDetected);
        Assert.Equal(0, result.DispatchedAlertCount);
        Assert.Equal(1, result.ResolvedAlertCount);

        Assert.False(fatigueAlert.IsActive);
        Assert.NotNull(fatigueAlert.AutoResolvedAt);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutMutatingState()
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);

        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLAlertFatigue:MinAlertThreshold", "1");
                db.Set<Alert>().Add(NewMlAlert(1, now.AddHours(-1).UtcDateTime, isActive: true));
            },
            timeProvider: new TestTimeProvider(now),
            distributedLock: new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("lock_busy", result.SkippedReason);
        var alerts = await harness.LoadAlertsAsync();
        var configs = await harness.LoadConfigsAsync();
        Assert.Single(alerts);
        Assert.Single(configs);
        Assert.DoesNotContain(alerts, alert => alert.DeduplicationKey == "ml-alert-fatigue");
        Assert.DoesNotContain(configs, config => config.Key == "MLAlertFatigue:TotalTriggeredAlerts");
    }

    [Fact]
    public async Task RunCycleAsync_InvalidSettingsAreClamped_AndLegacyRatioKeyIsHonored()
    {
        using var harness = CreateHarness(
            seed: db =>
            {
                AddConfig(db, "MLAlertFatigue:PollIntervalSeconds", "-1");
                AddConfig(db, "MLAlertFatigue:WindowDays", "0");
                AddConfig(db, "MLAlertFatigue:MinAlertThreshold", "0");
                AddConfig(db, "MLAlertFatigue:MinActionRatio", "0.35");
                AddConfig(db, "MLAlertFatigue:LockTimeoutSeconds", "-2");
                AddConfig(db, AlertCooldownDefaults.CK_MLEscalation, "0");
            });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromDays(1), result.Settings.PollInterval);
        Assert.Equal(7, result.Settings.WindowDays);
        Assert.Equal(20, result.Settings.MinAlertThreshold);
        Assert.Equal(0.35, result.Settings.MinRemediatedRatio, 6);
        Assert.Equal(5, result.Settings.LockTimeoutSeconds);
        Assert.Equal(AlertCooldownDefaults.Default_MLEscalation, result.Settings.CooldownSeconds);
    }

    private static WorkerHarness CreateHarness(
        Action<MLAlertFatigueWorkerTestContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var now = new DateTimeOffset(2026, 04, 25, 12, 0, 0, TimeSpan.Zero);
        var effectiveTimeProvider = timeProvider ?? new TestTimeProvider(now);

        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MLAlertFatigueWorkerTestContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(provider => provider.GetRequiredService<MLAlertFatigueWorkerTestContext>());
        services.AddScoped<IReadApplicationDbContext>(provider => provider.GetRequiredService<MLAlertFatigueWorkerTestContext>());

        var alertDispatcher = new TestAlertDispatcher(effectiveTimeProvider);
        services.AddSingleton<IAlertDispatcher>(alertDispatcher);

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MLAlertFatigueWorkerTestContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new MLAlertFatigueWorker(
            NullLogger<MLAlertFatigueWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            metrics: null,
            timeProvider: effectiveTimeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, connection, worker);
    }

    private static Alert NewMlAlert(
        long id,
        DateTime? lastTriggeredAtUtc,
        bool isActive,
        DateTime? autoResolvedAt = null,
        AlertType alertType = AlertType.MLModelDegraded,
        string? deduplicationKey = null)
        => new()
        {
            Id = id,
            AlertType = alertType,
            ConditionJson = "{}",
            IsActive = isActive,
            LastTriggeredAt = lastTriggeredAtUtc,
            Severity = AlertSeverity.Medium,
            DeduplicationKey = deduplicationKey,
            CooldownSeconds = 3600,
            AutoResolvedAt = autoResolvedAt,
            IsDeleted = false
        };

    private static void AddConfig(
        MLAlertFatigueWorkerTestContext db,
        string key,
        string value)
    {
        db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = ConfigDataType.String,
            IsHotReloadable = true,
            LastUpdatedAt = DateTime.UtcNow,
            IsDeleted = false
        });
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        MLAlertFatigueWorker worker) : IDisposable
    {
        public MLAlertFatigueWorker Worker { get; } = worker;

        public async Task<List<Alert>> LoadAlertsAsync(bool ignoreQueryFilters = false)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAlertFatigueWorkerTestContext>();

            IQueryable<Alert> query = db.Set<Alert>().AsNoTracking();
            if (ignoreQueryFilters)
                query = query.IgnoreQueryFilters();

            return await query
                .OrderBy(alert => alert.Id)
                .ToListAsync();
        }

        public async Task<List<EngineConfig>> LoadConfigsAsync(bool ignoreQueryFilters = false)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAlertFatigueWorkerTestContext>();

            IQueryable<EngineConfig> query = db.Set<EngineConfig>().AsNoTracking();
            if (ignoreQueryFilters)
                query = query.IgnoreQueryFilters();

            return await query
                .OrderBy(config => config.Key)
                .ToListAsync();
        }

        public async Task<EngineConfig?> LoadConfigAsync(string key, bool ignoreQueryFilters = false)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MLAlertFatigueWorkerTestContext>();

            IQueryable<EngineConfig> query = db.Set<EngineConfig>().AsNoTracking();
            if (ignoreQueryFilters)
                query = query.IgnoreQueryFilters();

            return await query.SingleOrDefaultAsync(config => config.Key == key);
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class MLAlertFatigueWorkerTestContext(DbContextOptions<MLAlertFatigueWorkerTestContext> options)
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
                builder.Property(config => config.DataType).HasConversion<string>();
                builder.HasIndex(config => config.Key).IsUnique();
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
