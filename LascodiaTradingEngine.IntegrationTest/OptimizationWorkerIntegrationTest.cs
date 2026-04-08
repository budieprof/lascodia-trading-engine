using System.Diagnostics;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetOptimizationWorkerHealth;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

public class OptimizationWorkerIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public OptimizationWorkerIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OptimizationWorker_SeparatesExecutionHealth_AndCompletesRequeuedRunsAfterRestart()
    {
        await ResetDatabaseAsync();

        await using (var seedCtx = CreateWriteContext())
        {
            await UpsertConfigsAsync(seedCtx, BuildWorkerConfigs(maxConcurrentRuns: 2));

            long strategyAId = await SeedStrategyAsync(seedCtx, "WorkerA");
            long strategyBId = await SeedStrategyAsync(seedCtx, "WorkerB");

            seedCtx.Set<OptimizationRun>().AddRange(
                new OptimizationRun
                {
                    StrategyId = strategyAId,
                    TriggerType = TriggerType.Scheduled,
                    Status = OptimizationRunStatus.Queued,
                    StartedAt = new DateTime(2026, 04, 08, 8, 0, 0, DateTimeKind.Utc),
                    QueuedAt = new DateTime(2026, 04, 08, 8, 0, 0, DateTimeKind.Utc),
                },
                new OptimizationRun
                {
                    StrategyId = strategyBId,
                    TriggerType = TriggerType.Scheduled,
                    Status = OptimizationRunStatus.Queued,
                    StartedAt = new DateTime(2026, 04, 08, 8, 5, 0, DateTimeKind.Utc),
                    QueuedAt = new DateTime(2026, 04, 08, 8, 5, 0, DateTimeKind.Utc),
                });

            await seedCtx.SaveChangesAsync();
        }

        var blockingExecutor = new BlockingOptimizationRunExecutor(expectedConcurrentRuns: 2);
        await using (var firstHarness = CreateHarness(blockingExecutor))
        {
            await firstHarness.Worker.StartAsync(CancellationToken.None);
            await blockingExecutor.AllRunsStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var health = await WaitForHealthAsync(
                firstHarness,
                snapshot => snapshot.ActiveProcessingSlots == 2
                    && snapshot.CoordinatorWorker?.WorkerName == OptimizationWorkerHealthNames.CoordinatorWorker
                    && snapshot.OptimizationWorker?.WorkerName == OptimizationWorkerHealthNames.ExecutionWorker,
                TimeSpan.FromSeconds(10));

            Assert.Equal(OptimizationWorkerHealthNames.CoordinatorWorker, health.CoordinatorWorker!.WorkerName);
            Assert.Equal(OptimizationWorkerHealthNames.ExecutionWorker, health.OptimizationWorker!.WorkerName);
            Assert.Equal(2, health.ActiveProcessingSlots);
            Assert.Equal(2, health.ConfiguredMaxConcurrentRuns);

            await firstHarness.Worker.StopAsync(CancellationToken.None);
        }

        await using (var verifyCtx = CreateReadContext())
        {
            var requeuedRuns = await verifyCtx.Set<OptimizationRun>()
                .OrderBy(r => r.Id)
                .ToListAsync();

            Assert.Equal(2, requeuedRuns.Count);
            Assert.All(requeuedRuns, run => Assert.Equal(OptimizationRunStatus.Queued, run.Status));
            Assert.All(requeuedRuns, run => Assert.NotEqual(default, run.QueuedAt));
            Assert.All(requeuedRuns, run => Assert.Null(run.ClaimedAt));
            Assert.All(requeuedRuns, run => Assert.Null(run.ExecutionStartedAt));
            Assert.All(requeuedRuns, run => Assert.Null(run.ExecutionLeaseToken));
            Assert.All(requeuedRuns, run => Assert.Null(run.ExecutionLeaseExpiresAt));
        }

        await using (var secondHarness = CreateHarness(new CompletingOptimizationRunExecutor()))
        {
            await secondHarness.Worker.StartAsync(CancellationToken.None);

            await WaitUntilAsync(async () =>
            {
                await using var ctx = CreateReadContext();
                return await ctx.Set<OptimizationRun>()
                    .CountAsync(r => r.Status == OptimizationRunStatus.Completed) == 2;
            }, TimeSpan.FromSeconds(15));

            await secondHarness.Worker.StopAsync(CancellationToken.None);
        }

        await using var finalCtx = CreateReadContext();
        var completedRuns = await finalCtx.Set<OptimizationRun>()
            .OrderBy(r => r.Id)
            .ToListAsync();

        Assert.Equal(2, completedRuns.Count);
        Assert.All(completedRuns, run => Assert.Equal(OptimizationRunStatus.Completed, run.Status));
        Assert.All(completedRuns, run => Assert.NotNull(run.ExecutionStartedAt));
        Assert.All(completedRuns, run => Assert.NotNull(run.ResultsPersistedAt));
        Assert.All(completedRuns, run => Assert.Equal(OptimizationExecutionStage.Completed, run.ExecutionStage));
    }

    private WorkerHarness CreateHarness(IOptimizationRunExecutor executor)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TradingMetrics>(sp => new TradingMetrics(
            sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
        services.AddSingleton<IWorkerHealthMonitor, WorkerHealthMonitor>();
        services.AddSingleton<IOptimizationWorkerHealthStore, OptimizationWorkerHealthStore>();
        services.AddSingleton<OptimizationConfigProvider>();
        services.AddSingleton<OptimizationRunScopedConfigService>();
        services.AddSingleton<OptimizationRunLeaseManager>();
        services.AddScoped<OptimizationValidator>();
        services.AddScoped<OptimizationRunPreflightService>();
        services.AddScoped<IOptimizationRunProcessor, OptimizationRunProcessor>();
        services.AddScoped<IOptimizationRunExecutor>(_ => executor);
        services.AddScoped<IReadApplicationDbContext>(_ => CreateReadContext());
        services.AddScoped<IWriteApplicationDbContext>(_ => CreateWriteContext());
        services.AddSingleton<IBacktestEngine, NoOpBacktestEngine>();
        services.AddSingleton<IMediator, NoOpMediator>();
        services.AddSingleton<IAlertDispatcher, NoOpAlertDispatcher>();
        services.AddSingleton<IIntegrationEventService, NoOpIntegrationEventService>();

        var provider = services.BuildServiceProvider(validateScopes: true);
        var worker = new OptimizationWorker(
            provider.GetRequiredService<ILogger<OptimizationWorker>>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TradingMetrics>(),
            provider.GetRequiredService<IWorkerHealthMonitor>(),
            provider.GetRequiredService<IOptimizationWorkerHealthStore>(),
            provider.GetRequiredService<OptimizationConfigProvider>(),
            new NoOpLoopCoordinator(),
            provider.GetRequiredService<TimeProvider>(),
            pollingInterval: TimeSpan.FromMilliseconds(50),
            shutdownDrainTimeout: TimeSpan.FromSeconds(5));

        return new WorkerHarness(
            provider,
            worker,
            provider.GetRequiredService<IWorkerHealthMonitor>(),
            provider.GetRequiredService<IOptimizationWorkerHealthStore>());
    }

    private async Task<OptimizationWorkerHealthDto> WaitForHealthAsync(
        WorkerHarness harness,
        Func<OptimizationWorkerHealthDto, bool> predicate,
        TimeSpan timeout)
    {
        OptimizationWorkerHealthDto? last = null;
        await WaitUntilAsync(async () =>
        {
            var response = await new GetOptimizationWorkerHealthQueryHandler(harness.HealthMonitor, harness.HealthStore)
                .Handle(new GetOptimizationWorkerHealthQuery(), CancellationToken.None);
            last = response.data;
            return last is not null && predicate(last);
        }, timeout);

        return last!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
                return;

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException($"Condition was not satisfied within {timeout.TotalSeconds:F1}s.");
    }

    private WriteApplicationDbContext CreateWriteContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private ReadApplicationDbContext CreateReadContext()
    {
        var options = new DbContextOptionsBuilder<ReadApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new ReadApplicationDbContext(options, new HttpContextAccessor());
    }

    private async Task ResetDatabaseAsync()
    {
        await using var context = CreateWriteContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();
    }

    private static async Task<long> SeedStrategyAsync(WriteApplicationDbContext context, string name)
    {
        var strategy = new Strategy
        {
            Name = name,
            Description = $"{name} strategy",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":12,"Slow":26}""",
            Status = StrategyStatus.Active
        };
        context.Set<Strategy>().Add(strategy);
        await context.SaveChangesAsync();
        return strategy.Id;
    }

    private static async Task UpsertConfigsAsync(WriteApplicationDbContext context, IEnumerable<EngineConfig> configs)
    {
        foreach (var config in configs)
        {
            var existing = await context.Set<EngineConfig>().FirstOrDefaultAsync(c => c.Key == config.Key);
            if (existing is null)
            {
                context.Set<EngineConfig>().Add(config);
                continue;
            }

            existing.Value = config.Value;
            existing.DataType = config.DataType;
            existing.IsDeleted = false;
            existing.LastUpdatedAt = DateTime.UtcNow;
        }
    }

    private static IReadOnlyList<EngineConfig> BuildWorkerConfigs(int maxConcurrentRuns)
    {
        return
        [
            NewConfig("Optimization:MaxConcurrentRuns", maxConcurrentRuns.ToString()),
            NewConfig("Optimization:SchedulePollSeconds", "1"),
            NewConfig("Optimization:RequireEADataAvailability", "false"),
            NewConfig("Optimization:SeasonalBlackoutEnabled", "false"),
            NewConfig("Optimization:SuppressDuringDrawdownRecovery", "false"),
            NewConfig("Optimization:RegimeStabilityHours", "0"),
            NewConfig("Optimization:MaxRunTimeoutMinutes", "30")
        ];
    }

    private static EngineConfig NewConfig(string key, string value) => new()
    {
        Key = key,
        Value = value,
        DataType = InferDataType(value),
        IsDeleted = false
    };

    private static ConfigDataType InferDataType(string value)
    {
        if (bool.TryParse(value, out _))
            return ConfigDataType.Bool;
        if (int.TryParse(value, out _))
            return ConfigDataType.Int;
        if (decimal.TryParse(value, out _))
            return ConfigDataType.Decimal;
        return ConfigDataType.String;
    }

    private sealed class WorkerHarness : IAsyncDisposable
    {
        public WorkerHarness(
            ServiceProvider provider,
            OptimizationWorker worker,
            IWorkerHealthMonitor healthMonitor,
            IOptimizationWorkerHealthStore healthStore)
        {
            Provider = provider;
            Worker = worker;
            HealthMonitor = healthMonitor;
            HealthStore = healthStore;
        }

        public ServiceProvider Provider { get; }
        public OptimizationWorker Worker { get; }
        public IWorkerHealthMonitor HealthMonitor { get; }
        public IOptimizationWorkerHealthStore HealthStore { get; }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
        }
    }

    private sealed class NoOpLoopCoordinator : IOptimizationWorkerLoopCoordinator
    {
        public Task WarmStartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ExecuteCycleAsync(OptimizationWorkerCycleContext cycleContext, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class BlockingOptimizationRunExecutor : IOptimizationRunExecutor
    {
        private readonly int _expectedConcurrentRuns;
        private int _activeRuns;

        public BlockingOptimizationRunExecutor(int expectedConcurrentRuns)
        {
            _expectedConcurrentRuns = expectedConcurrentRuns;
        }

        public TaskCompletionSource AllRunsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(
            OptimizationRun run,
            Strategy strategy,
            OptimizationConfig config,
            DbContext db,
            DbContext writeDb,
            IWriteApplicationDbContext writeCtx,
            IMediator mediator,
            IAlertDispatcher alertDispatcher,
            IIntegrationEventService eventService,
            Stopwatch sw,
            CancellationToken ct,
            CancellationToken runCt)
        {
            int activeRuns = Interlocked.Increment(ref _activeRuns);
            if (activeRuns >= _expectedConcurrentRuns)
                AllRunsStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, runCt);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRuns);
            }
        }
    }

    private sealed class CompletingOptimizationRunExecutor : IOptimizationRunExecutor
    {
        public async Task ExecuteAsync(
            OptimizationRun run,
            Strategy strategy,
            OptimizationConfig config,
            DbContext db,
            DbContext writeDb,
            IWriteApplicationDbContext writeCtx,
            IMediator mediator,
            IAlertDispatcher alertDispatcher,
            IIntegrationEventService eventService,
            Stopwatch sw,
            CancellationToken ct,
            CancellationToken runCt)
        {
            var nowUtc = DateTime.UtcNow;
            run.ResultsPersistedAt = nowUtc;
            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Completed, nowUtc);
            OptimizationRunProgressTracker.SetStage(run, OptimizationExecutionStage.Completed, "Integration test completion.", nowUtc);
            await writeCtx.SaveChangesAsync(ct);
        }
    }

    private sealed class NoOpBacktestEngine : IBacktestEngine
    {
        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            return Task.FromResult(new BacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = initialBalance,
                TotalTrades = 0,
                Trades = []
            });
        }
    }

    private sealed class NoOpMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => Task.FromResult(default(TResponse)!);

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => Task.FromResult<object?>(null);

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<object?>();
    }

    private sealed class NoOpAlertDispatcher : IAlertDispatcher
    {
        public Task DispatchAsync(Alert alert, string message, CancellationToken ct) => Task.CompletedTask;
        public Task DispatchBySeverityAsync(Alert alert, string message, CancellationToken ct) => Task.CompletedTask;
        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoOpIntegrationEventService : IIntegrationEventService
    {
        public Task SaveAndPublish(IDbContext context, IntegrationEvent evt) => Task.CompletedTask;
    }
}
