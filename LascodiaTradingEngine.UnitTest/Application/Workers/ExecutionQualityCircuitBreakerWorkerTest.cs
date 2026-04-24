using System.Text.Json;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class ExecutionQualityCircuitBreakerWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_AbsoluteSlippageWindow_PausesStrategy_WhenSignedAverageWouldHideBreach()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().AddRange(
                NewConfig("ExecQuality:WindowFills", "3"),
                NewConfig("ExecQuality:MaxAvgSlippagePips", "3.0"));

            db.Set<Strategy>().Add(NewStrategy(1, StrategyStatus.Active));
            db.Set<ExecutionQualityLog>().AddRange(
                NewLog(1, 1, 4.0m, 500, 1.0m, now.AddMinutes(-1).UtcDateTime),
                NewLog(2, 1, -4.0m, 500, 1.0m, now.AddMinutes(-2).UtcDateTime),
                NewLog(3, 1, 4.0m, 500, 1.0m, now.AddMinutes(-3).UtcDateTime));
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var strategy = await harness.LoadStrategyAsync(1);
        Assert.Equal(1, result.CandidateStrategyCount);
        Assert.Equal(1, result.EvaluatedStrategyCount);
        Assert.Equal(1, result.BreachCount);
        Assert.Equal(1, result.PauseCount);
        Assert.Equal(StrategyStatus.Paused, strategy.Status);
        Assert.Equal("ExecutionQuality", strategy.PauseReason);

        var decision = Assert.Single(harness.Decisions, command => command.DecisionType == "ExecQualityCircuitBreak");
        Assert.Contains("avgAbsSlippage=4.00", decision.Reason, StringComparison.Ordinal);
        using var contextJson = JsonDocument.Parse(decision.ContextJson!);
        Assert.Equal(4.0, contextJson.RootElement.GetProperty("avgAbsoluteSlippagePips").GetDouble());
    }

    [Fact]
    public async Task RunCycleAsync_IgnoresZeroLatencySamples_WhenEvaluatingLatencyBreaches()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().AddRange(
                NewConfig("ExecQuality:WindowFills", "3"),
                NewConfig("ExecQuality:MaxAvgSlippagePips", "10.0"),
                NewConfig("ExecQuality:MaxAvgLatencyMs", "2000"));

            db.Set<Strategy>().Add(NewStrategy(1, StrategyStatus.Active));
            db.Set<ExecutionQualityLog>().AddRange(
                NewLog(1, 1, 0.2m, 0, 1.0m, now.AddMinutes(-1).UtcDateTime),
                NewLog(2, 1, 0.3m, 0, 1.0m, now.AddMinutes(-2).UtcDateTime),
                NewLog(3, 1, 0.4m, 4500, 1.0m, now.AddMinutes(-3).UtcDateTime));
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var strategy = await harness.LoadStrategyAsync(1);
        Assert.Equal(1, result.BreachCount);
        Assert.Equal(1, result.PauseCount);
        Assert.Equal(StrategyStatus.Paused, strategy.Status);

        var decision = Assert.Single(harness.Decisions, command => command.DecisionType == "ExecQualityCircuitBreak");
        Assert.Contains("avgLatency=4500 ms", decision.Reason, StringComparison.Ordinal);
        using var contextJson = JsonDocument.Parse(decision.ContextJson!);
        Assert.Equal(4500.0, contextJson.RootElement.GetProperty("avgLatencyMs").GetDouble());
    }

    [Fact]
    public async Task RunCycleAsync_ExecutionQualityPausedStrategyRecoversBelowHysteresis_AutoResumes()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().AddRange(
                NewConfig("ExecQuality:WindowFills", "3"),
                NewConfig("ExecQuality:MaxAvgSlippagePips", "3.0"),
                NewConfig("ExecQuality:MaxAvgLatencyMs", "2000"),
                NewConfig("ExecQuality:HysteresisMarginPct", "0.20"));

            db.Set<Strategy>().Add(NewStrategy(1, StrategyStatus.Paused, "ExecutionQuality"));
            db.Set<ExecutionQualityLog>().AddRange(
                NewLog(1, 1, 2.0m, 1000, 1.0m, now.AddMinutes(-1).UtcDateTime),
                NewLog(2, 1, 2.0m, 1000, 1.0m, now.AddMinutes(-2).UtcDateTime),
                NewLog(3, 1, 2.0m, 1000, 1.0m, now.AddMinutes(-3).UtcDateTime));
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var strategy = await harness.LoadStrategyAsync(1);
        Assert.Equal(1, result.ResumeCount);
        Assert.Equal(StrategyStatus.Active, strategy.Status);
        Assert.Null(strategy.PauseReason);

        var decision = Assert.Single(harness.Decisions, command => command.DecisionType == "ExecQualityRecovery");
        Assert.Contains("avgAbsSlippage=2.00", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCycleAsync_NotEnoughFreshFills_DoesNotResumeStrategyFromStaleWindow()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().AddRange(
                NewConfig("ExecQuality:WindowFills", "3"),
                NewConfig("ExecQuality:LookbackDays", "1"));

            db.Set<Strategy>().Add(NewStrategy(1, StrategyStatus.Paused, "ExecutionQuality"));
            db.Set<ExecutionQualityLog>().AddRange(
                NewLog(1, 1, 1.0m, 500, 1.0m, now.AddHours(-2).UtcDateTime),
                NewLog(2, 1, 1.0m, 500, 1.0m, now.AddHours(-6).UtcDateTime),
                NewLog(3, 1, 1.0m, 500, 1.0m, now.AddDays(-3).UtcDateTime));
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var strategy = await harness.LoadStrategyAsync(1);
        Assert.Equal(1, result.CandidateStrategyCount);
        Assert.Equal(0, result.EvaluatedStrategyCount);
        Assert.Equal(1, result.InsufficientFreshDataCount);
        Assert.Equal(StrategyStatus.Paused, strategy.Status);
        Assert.Equal("ExecutionQuality", strategy.PauseReason);
        Assert.Empty(harness.Decisions);
    }

    [Fact]
    public async Task RunCycleAsync_InvalidConfigValuesAreNormalizedSafely()
    {
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().AddRange(
                NewConfig("ExecQuality:PollIntervalMinutes", "-10"),
                NewConfig("ExecQuality:WindowFills", "0"),
                NewConfig("ExecQuality:MaxAvgSlippagePips", "-1"),
                NewConfig("ExecQuality:MaxAvgLatencyMs", "-1"),
                NewConfig("ExecQuality:HysteresisMarginPct", "2.5"),
                NewConfig("ExecQuality:LookbackDays", "0"),
                NewConfig("ExecQuality:MinAvgFillRate", "2"),
                NewConfig("ExecQuality:AutoPauseEnabled", "1"));
        });

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(1), result.Settings.PollInterval);
        Assert.Equal(3, result.Settings.WindowFills);
        Assert.Equal(3.0, result.Settings.MaxAverageAbsoluteSlippagePips);
        Assert.Equal(2000.0, result.Settings.MaxAverageLatencyMs);
        Assert.Equal(0.95, result.Settings.HysteresisMargin);
        Assert.Equal(30, result.Settings.LookbackDays);
        Assert.Equal(1.0, result.Settings.MinAverageFillRate);
        Assert.True(result.Settings.AutoPauseEnabled);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutMutatingState()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().AddRange(
                NewConfig("ExecQuality:WindowFills", "3"),
                NewConfig("ExecQuality:MaxAvgSlippagePips", "3.0"));

            db.Set<Strategy>().Add(NewStrategy(1, StrategyStatus.Active));
            db.Set<ExecutionQualityLog>().AddRange(
                NewLog(1, 1, 5.0m, 500, 1.0m, now.AddMinutes(-1).UtcDateTime),
                NewLog(2, 1, 5.0m, 500, 1.0m, now.AddMinutes(-2).UtcDateTime),
                NewLog(3, 1, 5.0m, 500, 1.0m, now.AddMinutes(-3).UtcDateTime));
        }, new TestTimeProvider(now), new TestDistributedLock(lockAvailable: false));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var strategy = await harness.LoadStrategyAsync(1);
        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(StrategyStatus.Active, strategy.Status);
        Assert.Null(strategy.PauseReason);
        Assert.Empty(harness.Decisions);
    }

    [Fact]
    public async Task RunCycleAsync_AutoPauseDisabled_LogsWarningWithoutPausing()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().AddRange(
                NewConfig("ExecQuality:WindowFills", "3"),
                NewConfig("ExecQuality:AutoPauseEnabled", "false"),
                NewConfig("ExecQuality:MaxAvgSlippagePips", "3.0"));

            db.Set<Strategy>().Add(NewStrategy(1, StrategyStatus.Active));
            db.Set<ExecutionQualityLog>().AddRange(
                NewLog(1, 1, 5.0m, 500, 1.0m, now.AddMinutes(-1).UtcDateTime),
                NewLog(2, 1, 5.0m, 500, 1.0m, now.AddMinutes(-2).UtcDateTime),
                NewLog(3, 1, 5.0m, 500, 1.0m, now.AddMinutes(-3).UtcDateTime));
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var strategy = await harness.LoadStrategyAsync(1);
        Assert.Equal(1, result.WarningCount);
        Assert.Equal(0, result.PauseCount);
        Assert.Equal(StrategyStatus.Active, strategy.Status);
        Assert.Null(strategy.PauseReason);

        var decision = Assert.Single(harness.Decisions, command => command.DecisionType == "ExecQualityWarning");
        Assert.Contains("AutoPause disabled", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCycleAsync_ConfiguredFillRateFloor_PausesStrategyOnPoorAverageFillRate()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        using var harness = CreateHarness(db =>
        {
            db.Set<EngineConfig>().AddRange(
                NewConfig("ExecQuality:WindowFills", "3"),
                NewConfig("ExecQuality:MaxAvgSlippagePips", "10.0"),
                NewConfig("ExecQuality:MaxAvgLatencyMs", "10000"),
                NewConfig("ExecQuality:MinAvgFillRate", "0.80"));

            db.Set<Strategy>().Add(NewStrategy(1, StrategyStatus.Active));
            db.Set<ExecutionQualityLog>().AddRange(
                NewLog(1, 1, 0.1m, 500, 0.70m, now.AddMinutes(-1).UtcDateTime),
                NewLog(2, 1, 0.1m, 500, 0.75m, now.AddMinutes(-2).UtcDateTime),
                NewLog(3, 1, 0.1m, 500, 0.80m, now.AddMinutes(-3).UtcDateTime));
        }, new TestTimeProvider(now));

        var result = await harness.Worker.RunCycleAsync(CancellationToken.None);

        var strategy = await harness.LoadStrategyAsync(1);
        Assert.Equal(1, result.BreachCount);
        Assert.Equal(1, result.PauseCount);
        Assert.Equal(StrategyStatus.Paused, strategy.Status);

        var decision = Assert.Single(harness.Decisions, command => command.DecisionType == "ExecQualityCircuitBreak");
        Assert.Contains("avgFillRate=0.750 < threshold 0.800", decision.Reason, StringComparison.Ordinal);
    }

    private static WorkerHarness CreateHarness(
        Action<TestExecutionQualityCircuitBreakerDbContext> seed,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<TestExecutionQualityCircuitBreakerDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<TestExecutionQualityCircuitBreakerDbContext>());

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
            var db = scope.ServiceProvider.GetRequiredService<TestExecutionQualityCircuitBreakerDbContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }

        var worker = new ExecutionQualityCircuitBreakerWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExecutionQualityCircuitBreakerWorker>.Instance,
            metrics: null,
            timeProvider: timeProvider,
            healthMonitor: null,
            distributedLock: distributedLock);

        return new WorkerHarness(provider, connection, worker, decisions);
    }

    private static Strategy NewStrategy(long id, StrategyStatus status, string? pauseReason = null)
        => new()
        {
            Id = id,
            Name = $"strategy-{id}",
            Description = $"strategy-{id}",
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            StrategyType = StrategyType.CompositeML,
            ParametersJson = "{}",
            Status = status,
            PauseReason = pauseReason,
            LifecycleStage = StrategyLifecycleStage.Active,
            LifecycleStageEnteredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            Generation = 0
        };

    private static ExecutionQualityLog NewLog(
        long orderId,
        long strategyId,
        decimal slippagePips,
        long submitToFillMs,
        decimal fillRate,
        DateTime recordedAtUtc)
        => new()
        {
            OrderId = orderId,
            StrategyId = strategyId,
            Symbol = "EURUSD",
            Session = TradingSession.London,
            RequestedPrice = 1.1000m,
            FilledPrice = 1.1001m,
            SlippagePips = slippagePips,
            SubmitToFillMs = submitToFillMs,
            WasPartialFill = fillRate < 1.0m,
            FillRate = fillRate,
            RecordedAt = recordedAtUtc
        };

    private static EngineConfig NewConfig(string key, string value)
        => new()
        {
            Key = key,
            Value = value,
            IsDeleted = false
        };

    private sealed class WorkerHarness(
        ServiceProvider provider,
        SqliteConnection connection,
        ExecutionQualityCircuitBreakerWorker worker,
        List<LogDecisionCommand> decisions) : IDisposable
    {
        public ExecutionQualityCircuitBreakerWorker Worker { get; } = worker;
        public List<LogDecisionCommand> Decisions { get; } = decisions;

        public async Task<Strategy> LoadStrategyAsync(long strategyId)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TestExecutionQualityCircuitBreakerDbContext>();
            return await db.Set<Strategy>()
                .IgnoreQueryFilters()
                .SingleAsync(strategy => strategy.Id == strategyId);
        }

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    private sealed class TestExecutionQualityCircuitBreakerDbContext(DbContextOptions<TestExecutionQualityCircuitBreakerDbContext> options)
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

            modelBuilder.Entity<Strategy>(builder =>
            {
                builder.HasKey(strategy => strategy.Id);
                builder.HasQueryFilter(strategy => !strategy.IsDeleted);
                builder.Property(strategy => strategy.Status).HasConversion<string>();
                builder.Property(strategy => strategy.StrategyType).HasConversion<string>();
                builder.Property(strategy => strategy.Timeframe).HasConversion<string>();
                builder.Property(strategy => strategy.LifecycleStage).HasConversion<string>();
                builder.Ignore(strategy => strategy.RiskProfile);
                builder.Ignore(strategy => strategy.TradeSignals);
                builder.Ignore(strategy => strategy.Orders);
                builder.Ignore(strategy => strategy.BacktestRuns);
                builder.Ignore(strategy => strategy.OptimizationRuns);
                builder.Ignore(strategy => strategy.WalkForwardRuns);
                builder.Ignore(strategy => strategy.Allocations);
                builder.Ignore(strategy => strategy.PerformanceSnapshots);
                builder.Ignore(strategy => strategy.ExecutionQualityLogs);
            });

            modelBuilder.Entity<ExecutionQualityLog>(builder =>
            {
                builder.HasKey(log => log.Id);
                builder.HasQueryFilter(log => !log.IsDeleted);
                builder.Property(log => log.Session).HasConversion<string>();
                builder.Ignore(log => log.Order);
                builder.Ignore(log => log.Strategy);
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
