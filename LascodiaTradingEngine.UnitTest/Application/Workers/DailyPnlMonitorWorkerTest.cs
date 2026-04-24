using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.EmergencyFlatten.Commands.EmergencyFlatten;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class DailyPnlMonitorWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_NoBreach_NoFlattenDispatched()
    {
        using var harness = CreateHarness(db =>
        {
            var account = EntityFactory.CreateAccount(equity: 10000m);
            account.MaxAbsoluteDailyLoss = 500m;
            db.Add(account);
            db.Add(new AccountPerformanceAttribution
            {
                Id = 1,
                TradingAccountId = account.Id,
                AttributionDate = DateTime.UtcNow.Date,
                StartOfDayEquity = 10100m,
                EndOfDayEquity = 10100m,
                IsDeleted = false
            });
        });

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        harness.Mediator.Verify(
            m => m.Send(It.IsAny<EmergencyFlattenCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_BreachDetected_DispatchesEmergencyFlatten_AndPersistsMarker()
    {
        var today = DateTime.UtcNow.Date;
        var expectedTradingDayKey = TradingDayBoundaryHelper.FormatTradingDayKey(today);

        using var harness = CreateHarness(db =>
        {
            var account = EntityFactory.CreateAccount(equity: 8000m);
            account.MaxAbsoluteDailyLoss = 500m;
            db.Add(account);
            db.Add(new AccountPerformanceAttribution
            {
                Id = 1,
                TradingAccountId = account.Id,
                AttributionDate = today,
                StartOfDayEquity = 10000m,
                EndOfDayEquity = 10000m,
                IsDeleted = false
            });
        });

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        harness.Mediator.Verify(
            m => m.Send(
                It.Is<EmergencyFlattenCommand>(c => c.TriggeredByAccountId > 0
                    && c.Reason.Contains("Daily P&L loss limit breached", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        var markers = await harness.LoadEngineConfigsAsync(includeDeleted: true);
        var marker = Assert.Single(markers);
        Assert.False(marker.IsDeleted);
        Assert.Equal(expectedTradingDayKey, marker.Value);
    }

    [Fact]
    public async Task RunCycleAsync_SkipsWhenFlattenAlreadyRecordedToday_WithLegacyMarker()
    {
        var today = DateTime.UtcNow.Date;
        var todayStr = today.ToString("yyyy-MM-dd");

        using var harness = CreateHarness(db =>
        {
            var account = EntityFactory.CreateAccount(equity: 8000m);
            account.MaxAbsoluteDailyLoss = 500m;
            db.Add(account);
            db.Add(new AccountPerformanceAttribution
            {
                Id = 1,
                TradingAccountId = account.Id,
                AttributionDate = today,
                StartOfDayEquity = 10000m,
                EndOfDayEquity = 10000m,
                IsDeleted = false
            });
            db.Add(new EngineConfig
            {
                Id = 9,
                Key = $"DailyPnlFlatten:{account.Id}",
                Value = todayStr,
                IsDeleted = false
            });
        });

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        harness.Mediator.Verify(
            m => m.Send(It.IsAny<EmergencyFlattenCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_SkipsWhenFlattenAlreadyRecordedToday_WithCurrentTradingDayMarker()
    {
        var today = DateTime.UtcNow.Date;
        var tradingDayKey = TradingDayBoundaryHelper.FormatTradingDayKey(today);

        using var harness = CreateHarness(db =>
        {
            var account = EntityFactory.CreateAccount(equity: 8000m);
            account.MaxAbsoluteDailyLoss = 500m;
            db.Add(account);
            db.Add(new AccountPerformanceAttribution
            {
                Id = 1,
                TradingAccountId = account.Id,
                AttributionDate = today,
                StartOfDayEquity = 10000m,
                EndOfDayEquity = 10000m,
                IsDeleted = false
            });
            db.Add(new EngineConfig
            {
                Id = 9,
                Key = $"DailyPnlFlatten:{account.Id}",
                Value = tradingDayKey,
                IsDeleted = false
            });
        });

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        harness.Mediator.Verify(
            m => m.Send(It.IsAny<EmergencyFlattenCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_RevivesSoftDeletedFlattenMarker_InsteadOfCreatingDuplicate()
    {
        var today = DateTime.UtcNow.Date;
        var expectedTradingDayKey = TradingDayBoundaryHelper.FormatTradingDayKey(today);

        using var harness = CreateHarness(db =>
        {
            var account = EntityFactory.CreateAccount(equity: 8000m);
            account.MaxAbsoluteDailyLoss = 500m;
            db.Add(account);
            db.Add(new AccountPerformanceAttribution
            {
                Id = 1,
                TradingAccountId = account.Id,
                AttributionDate = today,
                StartOfDayEquity = 10000m,
                EndOfDayEquity = 10000m,
                IsDeleted = false
            });
            db.Add(new EngineConfig
            {
                Id = 42,
                Key = $"DailyPnlFlatten:{account.Id}",
                Value = today.AddDays(-1).ToString("yyyy-MM-dd"),
                IsDeleted = true
            });
        });

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var markers = await harness.LoadEngineConfigsAsync(includeDeleted: true);
        var marker = Assert.Single(markers);
        Assert.Equal(42, marker.Id);
        Assert.False(marker.IsDeleted);
        Assert.Equal(expectedTradingDayKey, marker.Value);
    }

    [Fact]
    public async Task RunCycleAsync_UsesPreviousAttributionClose_WhenTodayAttributionMissing()
    {
        var today = DateTime.UtcNow.Date;

        using var harness = CreateHarness(db =>
        {
            var account = EntityFactory.CreateAccount(equity: 8000m);
            account.MaxAbsoluteDailyLoss = 500m;
            db.Add(account);
            db.Add(new AccountPerformanceAttribution
            {
                Id = 1,
                TradingAccountId = account.Id,
                AttributionDate = today.AddDays(-1).AddHours(23),
                StartOfDayEquity = 9500m,
                EndOfDayEquity = 10000m,
                IsDeleted = false
            });
        });

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        harness.Mediator.Verify(
            m => m.Send(It.IsAny<EmergencyFlattenCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_UsesBrokerSnapshotFallback_AsLastResort()
    {
        var now = DateTime.UtcNow;
        var tradingDayOptions = new TradingDayOptions
        {
            RolloverMinuteOfDayUtc = 0,
            BrokerSnapshotBoundaryToleranceMinutes = 180
        };
        var tradingDayStart = TradingDayBoundaryHelper.GetTradingDayStartUtc(now, tradingDayOptions.RolloverMinuteOfDayUtc);

        using var harness = CreateHarness(db =>
        {
            var account = EntityFactory.CreateAccount(equity: 9500m);
            account.MaxAbsoluteDailyLoss = 800m;
            db.Add(account);
            db.Add(new BrokerAccountSnapshot
            {
                Id = 1,
                TradingAccountId = account.Id,
                InstanceId = "EA-001",
                Balance = 10000m,
                Equity = 10000m,
                MarginUsed = 0m,
                FreeMargin = 10000m,
                Currency = "USD",
                ReportedAt = tradingDayStart.AddMinutes(5),
                IsDeleted = false
            });
        }, tradingDayOptions: tradingDayOptions);

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        harness.Mediator.Verify(
            m => m.Send(It.IsAny<EmergencyFlattenCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_SkipsWhenMultipleActiveAccountsExist()
    {
        var today = DateTime.UtcNow.Date;

        using var harness = CreateHarness(db =>
        {
            var primary = EntityFactory.CreateAccount(equity: 8000m);
            primary.MaxAbsoluteDailyLoss = 500m;
            db.Add(primary);
            db.Add(new AccountPerformanceAttribution
            {
                Id = 1,
                TradingAccountId = primary.Id,
                AttributionDate = today,
                StartOfDayEquity = 10000m,
                EndOfDayEquity = 10000m,
                IsDeleted = false
            });

            var secondary = EntityFactory.CreateAccount(equity: 12000m);
            secondary.MaxAbsoluteDailyLoss = 0m;
            db.Add(secondary);
        });

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        harness.Mediator.Verify(
            m => m.Send(It.IsAny<EmergencyFlattenCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_DoesNotDispatchOrPersistMarker_WhenFlattenDisabled()
    {
        var today = DateTime.UtcNow.Date;

        using var harness = CreateHarness(db =>
        {
            var account = EntityFactory.CreateAccount(equity: 8000m);
            account.MaxAbsoluteDailyLoss = 500m;
            db.Add(account);
            db.Add(new AccountPerformanceAttribution
            {
                Id = 1,
                TradingAccountId = account.Id,
                AttributionDate = today,
                StartOfDayEquity = 10000m,
                EndOfDayEquity = 10000m,
                IsDeleted = false
            });
        }, flattenEnabled: false);

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        harness.Mediator.Verify(
            m => m.Send(It.IsAny<EmergencyFlattenCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Empty(await harness.LoadEngineConfigsAsync(includeDeleted: true));
    }

    private static WorkerHarness CreateHarness(
        Action<TestDailyPnlDbContext> seed,
        bool flattenEnabled = true,
        TradingDayOptions? tradingDayOptions = null)
    {
        var services = new ServiceCollection();
        var databaseName = $"daily-pnl-monitor-{Guid.NewGuid()}";

        services.AddDbContext<TestDailyPnlDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<TestDailyPnlDbContext>());
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<TestDailyPnlDbContext>());

        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<EmergencyFlattenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<bool>.Init(true, true, "Flattened", "00"));
        services.AddScoped(_ => mediator.Object);

        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDailyPnlDbContext>();
            seed(db);
            db.SaveChanges();
        }

        var worker = new DailyPnlMonitorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new DailyPnlMonitorOptions
            {
                PollIntervalSeconds = 30,
                EmergencyFlattenEnabled = flattenEnabled
            },
            tradingDayOptions ?? new TradingDayOptions(),
            NullLogger<DailyPnlMonitorWorker>.Instance);

        return new WorkerHarness(provider, worker, mediator);
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        DailyPnlMonitorWorker worker,
        Mock<IMediator> mediator) : IDisposable
    {
        public DailyPnlMonitorWorker Worker { get; } = worker;
        public Mock<IMediator> Mediator { get; } = mediator;

        public async Task<List<EngineConfig>> LoadEngineConfigsAsync(bool includeDeleted)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TestDailyPnlDbContext>();
            var query = includeDeleted
                ? db.Set<EngineConfig>().IgnoreQueryFilters()
                : db.Set<EngineConfig>().AsQueryable();

            return await query
                .OrderBy(c => c.Id)
                .ToListAsync();
        }

        public void Dispose() => provider.Dispose();
    }

    private sealed class TestDailyPnlDbContext(DbContextOptions<TestDailyPnlDbContext> options)
        : DbContext(options), IReadApplicationDbContext, IWriteApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TradingAccount>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.Ignore(x => x.Orders);
                builder.Ignore(x => x.EAInstances);
            });

            modelBuilder.Entity<AccountPerformanceAttribution>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.Ignore(x => x.TradingAccount);
            });

            modelBuilder.Entity<DrawdownSnapshot>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
            });

            modelBuilder.Entity<BrokerAccountSnapshot>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
            });

            modelBuilder.Entity<EngineConfig>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.HasIndex(x => x.Key).IsUnique();
            });
        }
    }
}
