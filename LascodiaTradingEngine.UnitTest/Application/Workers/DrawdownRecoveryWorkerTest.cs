using System.Text.Json;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class DrawdownRecoveryWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_HaltedTransition_PausesActiveStrategiesAndPersistsOwnedIds()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 24, 10, 0, 30, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<DrawdownSnapshot>().Add(new DrawdownSnapshot
            {
                Id = 10,
                CurrentEquity = 8_000m,
                PeakEquity = 10_000m,
                DrawdownPct = 20m,
                RecoveryMode = RecoveryMode.Halted,
                RecordedAt = new DateTime(2026, 04, 24, 10, 0, 0, DateTimeKind.Utc)
            });

            db.Set<EngineConfig>().Add(new EngineConfig
            {
                Key = "DrawdownRecovery:ActiveMode",
                Value = "Reduced",
                IsDeleted = false
            });

            db.Set<Strategy>().AddRange(
                new Strategy { Id = 1, Name = "A", Symbol = "EURUSD", Status = StrategyStatus.Active, IsDeleted = false },
                new Strategy { Id = 2, Name = "B", Symbol = "GBPUSD", Status = StrategyStatus.Active, IsDeleted = false },
                new Strategy { Id = 3, Name = "C", Symbol = "USDJPY", Status = StrategyStatus.Paused, PauseReason = "Manual", IsDeleted = false });
        }, new FixedTimeProvider(nowUtc));

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var strategies = await harness.LoadStrategiesAsync();
        var strategy1 = strategies.Single(s => s.Id == 1);
        var strategy2 = strategies.Single(s => s.Id == 2);
        var strategy3 = strategies.Single(s => s.Id == 3);

        Assert.Equal(StrategyStatus.Paused, strategy1.Status);
        Assert.Equal("DrawdownRecovery", strategy1.PauseReason);
        Assert.Equal(StrategyStatus.Paused, strategy2.Status);
        Assert.Equal("DrawdownRecovery", strategy2.PauseReason);
        Assert.Equal(StrategyStatus.Paused, strategy3.Status);
        Assert.Equal("Manual", strategy3.PauseReason);

        Assert.Equal("Halted", await harness.LoadConfigValueAsync("DrawdownRecovery:ActiveMode"));
        Assert.Equal("[1,2]", await harness.LoadConfigValueAsync("DrawdownRecovery:AutoPausedStrategyIds"));
        Assert.Equal(2, harness.Decisions.Count(d => d.DecisionType == "AutoPause"));
        var transitionDecision = Assert.Single(harness.Decisions, d => d.DecisionType == "DrawdownModeTransition" && d.Outcome == "Halted");
        Assert.NotNull(transitionDecision.ContextJson);

        using var transitionJson = JsonDocument.Parse(transitionDecision.ContextJson!);
        Assert.Equal(10, transitionJson.RootElement.GetProperty("SnapshotId").GetInt64());
        Assert.Equal(2, transitionJson.RootElement.GetProperty("AutoPausedStrategyCount").GetInt32());

        var autoPauseDecision = Assert.Single(harness.Decisions, d => d.DecisionType == "AutoPause" && d.EntityId == 1);
        Assert.NotNull(autoPauseDecision.ContextJson);
        using var autoPauseJson = JsonDocument.Parse(autoPauseDecision.ContextJson!);
        Assert.Equal("Pause", autoPauseJson.RootElement.GetProperty("Operation").GetString());
    }

    [Fact]
    public async Task RunCycleAsync_LeavingHalted_OnlyResumesStrategiesStillOwnedByDrawdownRecovery()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 24, 11, 0, 30, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<DrawdownSnapshot>().Add(new DrawdownSnapshot
            {
                Id = 20,
                CurrentEquity = 9_800m,
                PeakEquity = 10_000m,
                DrawdownPct = 2m,
                RecoveryMode = RecoveryMode.Normal,
                RecordedAt = new DateTime(2026, 04, 24, 11, 0, 0, DateTimeKind.Utc)
            });

            db.Set<EngineConfig>().AddRange(
                new EngineConfig
                {
                    Key = "DrawdownRecovery:ActiveMode",
                    Value = "Halted",
                    IsDeleted = false
                },
                new EngineConfig
                {
                    Key = "DrawdownRecovery:AutoPausedStrategyIds",
                    Value = "[1,2,3]",
                    IsDeleted = false
                });

            db.Set<Strategy>().AddRange(
                new Strategy { Id = 1, Name = "A", Symbol = "EURUSD", Status = StrategyStatus.Paused, PauseReason = "DrawdownRecovery", IsDeleted = false },
                new Strategy { Id = 2, Name = "B", Symbol = "GBPUSD", Status = StrategyStatus.Paused, PauseReason = "Manual", IsDeleted = false },
                new Strategy { Id = 3, Name = "C", Symbol = "USDJPY", Status = StrategyStatus.Paused, PauseReason = "StrategyHealth", IsDeleted = false });
        }, new FixedTimeProvider(nowUtc));

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var strategies = await harness.LoadStrategiesAsync();
        var strategy1 = strategies.Single(s => s.Id == 1);
        var strategy2 = strategies.Single(s => s.Id == 2);
        var strategy3 = strategies.Single(s => s.Id == 3);

        Assert.Equal(StrategyStatus.Active, strategy1.Status);
        Assert.Null(strategy1.PauseReason);
        Assert.Equal(StrategyStatus.Paused, strategy2.Status);
        Assert.Equal("Manual", strategy2.PauseReason);
        Assert.Equal(StrategyStatus.Paused, strategy3.Status);
        Assert.Equal("StrategyHealth", strategy3.PauseReason);

        Assert.Equal("Normal", await harness.LoadConfigValueAsync("DrawdownRecovery:ActiveMode"));
        Assert.Equal("[]", await harness.LoadConfigValueAsync("DrawdownRecovery:AutoPausedStrategyIds"));
        var autoResumeDecision = Assert.Single(harness.Decisions, d => d.DecisionType == "AutoResume");
        Assert.NotNull(autoResumeDecision.ContextJson);
        using var autoResumeJson = JsonDocument.Parse(autoResumeDecision.ContextJson!);
        Assert.Equal("Resume", autoResumeJson.RootElement.GetProperty("Operation").GetString());

        var transitionDecision = Assert.Single(harness.Decisions, d => d.DecisionType == "DrawdownModeTransition" && d.Outcome == "Normal");
        Assert.NotNull(transitionDecision.ContextJson);
    }

    [Fact]
    public async Task RunCycleAsync_CorruptTrackedIds_FallsBackToPersistedPauseReason()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 24, 12, 0, 30, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<DrawdownSnapshot>().Add(new DrawdownSnapshot
            {
                Id = 30,
                CurrentEquity = 9_500m,
                PeakEquity = 10_000m,
                DrawdownPct = 5m,
                RecoveryMode = RecoveryMode.Reduced,
                RecordedAt = new DateTime(2026, 04, 24, 12, 0, 0, DateTimeKind.Utc)
            });

            db.Set<EngineConfig>().AddRange(
                new EngineConfig
                {
                    Key = "DrawdownRecovery:ActiveMode",
                    Value = "Halted",
                    IsDeleted = false
                },
                new EngineConfig
                {
                    Key = "DrawdownRecovery:AutoPausedStrategyIds",
                    Value = "not-json",
                    IsDeleted = false
                });

            db.Set<Strategy>().AddRange(
                new Strategy { Id = 1, Name = "A", Symbol = "EURUSD", Status = StrategyStatus.Paused, PauseReason = "DrawdownRecovery", IsDeleted = false },
                new Strategy { Id = 2, Name = "B", Symbol = "GBPUSD", Status = StrategyStatus.Paused, PauseReason = "Manual", IsDeleted = false });
        }, new FixedTimeProvider(nowUtc));

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var strategies = await harness.LoadStrategiesAsync();
        Assert.Equal(StrategyStatus.Active, strategies.Single(s => s.Id == 1).Status);
        Assert.Null(strategies.Single(s => s.Id == 1).PauseReason);
        Assert.Equal(StrategyStatus.Paused, strategies.Single(s => s.Id == 2).Status);
        Assert.Equal("Manual", strategies.Single(s => s.Id == 2).PauseReason);

        Assert.Equal("Reduced", await harness.LoadConfigValueAsync("DrawdownRecovery:ActiveMode"));
        Assert.Equal("[]", await harness.LoadConfigValueAsync("DrawdownRecovery:AutoPausedStrategyIds"));
        Assert.Single(harness.Decisions, d => d.DecisionType == "AutoResume");
    }

    [Fact]
    public async Task ReadPollIntervalSecondsAsync_InvalidConfig_UsesDefault()
    {
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().Add(new EngineConfig
            {
                Key = "DrawdownRecovery:PollIntervalSeconds",
                Value = "0",
                IsDeleted = false
            });
        });

        int pollSecs = await harness.Worker.ReadPollIntervalSecondsAsync(CancellationToken.None);

        Assert.Equal(30, pollSecs);
    }

    [Fact]
    public async Task RunCycleAsync_StaleSnapshot_SkipsEnforcementUntilFreshSnapshotArrives()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 24, 12, 5, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<DrawdownSnapshot>().Add(new DrawdownSnapshot
            {
                Id = 40,
                CurrentEquity = 7_500m,
                PeakEquity = 10_000m,
                DrawdownPct = 25m,
                RecoveryMode = RecoveryMode.Halted,
                RecordedAt = new DateTime(2026, 04, 24, 10, 0, 0, DateTimeKind.Utc)
            });

            db.Set<EngineConfig>().AddRange(
                new EngineConfig
                {
                    Key = "DrawdownRecovery:ActiveMode",
                    Value = "Normal",
                    IsDeleted = false
                },
                new EngineConfig
                {
                    Key = "DrawdownRecovery:SnapshotStaleAfterSeconds",
                    Value = "60",
                    IsDeleted = false
                });

            db.Set<Strategy>().Add(new Strategy
            {
                Id = 1,
                Name = "A",
                Symbol = "EURUSD",
                Status = StrategyStatus.Active,
                IsDeleted = false
            });
        }, new FixedTimeProvider(nowUtc));

        await harness.Worker.RunCycleAsync(CancellationToken.None);

        var strategies = await harness.LoadStrategiesAsync();
        Assert.Equal(StrategyStatus.Active, strategies.Single().Status);
        Assert.Equal("Normal", await harness.LoadConfigValueAsync("DrawdownRecovery:ActiveMode"));
        Assert.Null(await harness.LoadConfigValueAsync("DrawdownRecovery:AutoPausedStrategyIds"));
        Assert.Empty(harness.Decisions);
    }

    [Fact]
    public async Task ModeProvider_ClampsReducedLotMultiplierToSafeRange()
    {
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().AddRange(
                new EngineConfig
                {
                    Key = "DrawdownRecovery:ActiveMode",
                    Value = "Reduced",
                    IsDeleted = false
                },
                new EngineConfig
                {
                    Key = "DrawdownRecovery:ReducedLotMultiplier",
                    Value = "1.75",
                    IsDeleted = false
                });
        });

        var snapshot = await harness.ModeProvider.GetAsync(CancellationToken.None);

        Assert.True(snapshot.IsReduced);
        Assert.Equal(1.0m, snapshot.ReducedLotMultiplier);
    }

    private static WorkerHarness CreateHarness(
        Action<TestDrawdownRecoveryDbContext> seed,
        TimeProvider? timeProvider = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddDbContext<TestDrawdownRecoveryDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<TestDrawdownRecoveryDbContext>());
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<TestDrawdownRecoveryDbContext>());

        var decisions = new List<LogDecisionCommand>();
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<IRequest<ResponseData<long>>>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ResponseData<long>>, CancellationToken>((request, _) =>
            {
                if (request is LogDecisionCommand command)
                    decisions.Add(command);
            })
            .ReturnsAsync(ResponseData<long>.Init(1, true, "Logged", "00"));
        services.AddScoped(_ => mediator.Object);

        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDrawdownRecoveryDbContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var modeProvider = new DrawdownRecoveryModeProvider(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>());

        var worker = new DrawdownRecoveryWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            modeProvider,
            NullLogger<DrawdownRecoveryWorker>.Instance,
            timeProvider);

        return new WorkerHarness(provider, connection, worker, modeProvider, decisions);
    }

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        DrawdownRecoveryWorker worker,
        DrawdownRecoveryModeProvider modeProvider,
        List<LogDecisionCommand> decisions) : IDisposable
    {
        public DrawdownRecoveryWorker Worker { get; } = worker;
        public DrawdownRecoveryModeProvider ModeProvider { get; } = modeProvider;
        public List<LogDecisionCommand> Decisions { get; } = decisions;

        public async Task<List<Strategy>> LoadStrategiesAsync()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TestDrawdownRecoveryDbContext>();
            return await db.Set<Strategy>()
                .IgnoreQueryFilters()
                .OrderBy(s => s.Id)
                .ToListAsync();
        }

        public async Task<string?> LoadConfigValueAsync(string key)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TestDrawdownRecoveryDbContext>();
            return await db.Set<EngineConfig>()
                .IgnoreQueryFilters()
                .Where(c => c.Key == key)
                .Select(c => c.Value)
                .SingleOrDefaultAsync();
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class TestDrawdownRecoveryDbContext(DbContextOptions<TestDrawdownRecoveryDbContext> options)
        : DbContext(options), IReadApplicationDbContext, IWriteApplicationDbContext
    {
        public DbContext GetDbContext() => this;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DrawdownSnapshot>(builder =>
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

            modelBuilder.Entity<Strategy>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.Property(x => x.Status).HasConversion<string>();
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
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }
}
