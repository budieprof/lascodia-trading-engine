using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class EvolutionaryGeneratorWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_PersistsUniqueCanonicalizedOffspring_AndQueuesInitialBacktests()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var bundle = await NewContextAsync();
        var ctx = bundle.Ctx;

        ctx.Strategies.AddRange(
            NewStrategy(
                id: 10,
                strategyType: StrategyType.CompositeML,
                symbol: "EURUSD",
                timeframe: Timeframe.H1,
                parametersJson: """{"Fast":9,"Slow":21}""",
                status: StrategyStatus.Active,
                lifecycleStage: StrategyLifecycleStage.Active,
                generation: 1),
            NewStrategy(
                id: 11,
                strategyType: StrategyType.RSIReversion,
                symbol: "EURUSD",
                timeframe: Timeframe.H1,
                parametersJson: """{"Period":14}""",
                status: StrategyStatus.Active,
                lifecycleStage: StrategyLifecycleStage.Active,
                generation: 0),
            NewStrategy(
                id: 20,
                strategyType: StrategyType.CompositeML,
                symbol: "EURUSD",
                timeframe: Timeframe.H1,
                parametersJson: """{"Fast":14,"Slow":34}""",
                status: StrategyStatus.Active,
                lifecycleStage: StrategyLifecycleStage.Active,
                generation: 0));

        ctx.StrategyPerformanceSnapshots.AddRange(
            NewSnapshot(100, strategyId: 10, sharpeRatio: 1.23m, evaluatedAtUtc: now.AddMinutes(-10).UtcDateTime),
            NewSnapshot(101, strategyId: 11, sharpeRatio: 0.87m, evaluatedAtUtc: now.AddMinutes(-5).UtcDateTime));
        await ctx.SaveChangesAsync();

        var generator = new TestEvolutionaryStrategyGenerator
        {
            Candidates =
            [
                new EvolutionaryCandidate(
                    ParentStrategyId: 10,
                    Generation: 999,
                    Symbol: "eurusd",
                    Timeframe: Timeframe.H1,
                    StrategyType: StrategyType.CompositeML,
                    ParametersJson: """{"Slow":34,"Fast":12}""",
                    MutationDescription: "duplicate-a"),
                new EvolutionaryCandidate(
                    ParentStrategyId: 10,
                    Generation: 999,
                    Symbol: "EURUSD",
                    Timeframe: Timeframe.H1,
                    StrategyType: StrategyType.CompositeML,
                    ParametersJson: """{"Fast":12,"Slow":34}""",
                    MutationDescription: "duplicate-b"),
                new EvolutionaryCandidate(
                    ParentStrategyId: 11,
                    Generation: 999,
                    Symbol: "EURUSD",
                    Timeframe: Timeframe.H1,
                    StrategyType: StrategyType.RSIReversion,
                    ParametersJson: """{"Fast":12,"Slow":34}""",
                    MutationDescription: "different-type"),
                new EvolutionaryCandidate(
                    ParentStrategyId: 10,
                    Generation: 999,
                    Symbol: "EURUSD",
                    Timeframe: Timeframe.H1,
                    StrategyType: StrategyType.CompositeML,
                    ParametersJson: """{"Fast":14,"Slow":34}""",
                    MutationDescription: "existing-strategy")
            ]
        };
        var validationRunFactory = new TestValidationRunFactory(now.UtcDateTime);

        using var provider = BuildProvider(ctx, generator, validationRunFactory);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        ctx.ChangeTracker.Clear();

        var insertedStrategies = await ctx.Strategies
            .IgnoreQueryFilters()
            .Where(strategy => strategy.ParentStrategyId != null)
            .OrderBy(strategy => strategy.Id)
            .ToListAsync();
        var backtestRuns = await ctx.BacktestRuns
            .IgnoreQueryFilters()
            .OrderBy(run => run.Id)
            .ToListAsync();

        Assert.Equal(4, result.ProposedCandidateCount);
        Assert.Equal(2, result.InsertedCandidateCount);
        Assert.Equal(2, result.QueuedBacktestCount);
        Assert.Equal(1, result.DuplicateProposalCount);
        Assert.Equal(1, result.ExistingStrategyCount);
        Assert.Equal(0, result.IneligibleParentCount);
        Assert.Equal(0, result.InvalidParameterCount);
        Assert.Equal(0, result.PersistenceFailureCount);

        Assert.Equal(2, insertedStrategies.Count);
        Assert.Equal(2, backtestRuns.Count);

        var compositeChild = insertedStrategies.Single(strategy => strategy.StrategyType == StrategyType.CompositeML);
        Assert.Equal("""{"Fast":12,"Slow":34}""", compositeChild.ParametersJson);
        Assert.Equal(2, compositeChild.Generation);
        Assert.Equal(123, compositeChild.ValidationPriority);
        Assert.Equal(StrategyStatus.Paused, compositeChild.Status);
        Assert.Equal(StrategyLifecycleStage.Draft, compositeChild.LifecycleStage);
        Assert.False(string.IsNullOrWhiteSpace(compositeChild.GenerationCycleId));
        Assert.False(string.IsNullOrWhiteSpace(compositeChild.GenerationCandidateId));

        var rsiChild = insertedStrategies.Single(strategy => strategy.StrategyType == StrategyType.RSIReversion);
        Assert.Equal(1, rsiChild.Generation);
        Assert.Equal(87, rsiChild.ValidationPriority);
        Assert.Equal("""{"Fast":12,"Slow":34}""", rsiChild.ParametersJson);

        Assert.All(backtestRuns, run => Assert.Equal(ValidationQueueSource.StrategyGenerationInitial, run.QueueSource));
        Assert.Contains(backtestRuns, run => run.StrategyId == compositeChild.Id && run.ValidationQueueKey == $"strategy-candidate:{compositeChild.GenerationCandidateId}:backtest:initial");
        Assert.Contains(backtestRuns, run => run.StrategyId == rsiChild.Id && run.ValidationQueueKey == $"strategy-candidate:{rsiChild.GenerationCandidateId}:backtest:initial");
    }

    [Fact]
    public async Task RunCycleAsync_RevalidatesParentEligibilityOnWriteSide()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var bundle = await NewContextAsync();
        var ctx = bundle.Ctx;

        ctx.Strategies.Add(NewStrategy(
            id: 10,
            strategyType: StrategyType.CompositeML,
            symbol: "EURUSD",
            timeframe: Timeframe.H1,
            parametersJson: """{"Fast":9,"Slow":21}""",
            status: StrategyStatus.Paused,
            lifecycleStage: StrategyLifecycleStage.Draft,
            generation: 1));
        await ctx.SaveChangesAsync();

        var generator = new TestEvolutionaryStrategyGenerator
        {
            Candidates =
            [
                new EvolutionaryCandidate(
                    ParentStrategyId: 10,
                    Generation: 2,
                    Symbol: "EURUSD",
                    Timeframe: Timeframe.H1,
                    StrategyType: StrategyType.CompositeML,
                    ParametersJson: """{"Fast":12,"Slow":34}""",
                    MutationDescription: "stale-read-parent")
            ]
        };
        var validationRunFactory = new TestValidationRunFactory(now.UtcDateTime);

        using var provider = BuildProvider(ctx, generator, validationRunFactory);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        ctx.ChangeTracker.Clear();

        Assert.Equal(1, result.IneligibleParentCount);
        Assert.Equal(0, result.InsertedCandidateCount);
        Assert.Equal(0, result.QueuedBacktestCount);
        Assert.Equal(1, await ctx.Strategies.IgnoreQueryFilters().CountAsync());
        Assert.Empty(await ctx.BacktestRuns.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_DisabledAndInvalidConfigValuesAreHandledSafely()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var bundle = await NewContextAsync();
        var ctx = bundle.Ctx;

        ctx.EngineConfigs.AddRange(
            NewConfig(1, "Evolution:Enabled", "false"),
            NewConfig(2, "Evolution:PollIntervalSeconds", "-15", ConfigDataType.Int),
            NewConfig(3, "Evolution:MaxOffspringPerCycle", "999", ConfigDataType.Int));
        await ctx.SaveChangesAsync();

        var generator = new TestEvolutionaryStrategyGenerator();
        var validationRunFactory = new TestValidationRunFactory(now.UtcDateTime);

        using var provider = BuildProvider(ctx, generator, validationRunFactory);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal("disabled", result.SkippedReason);
        Assert.Equal(TimeSpan.FromMinutes(1), result.Settings.PollInterval);
        Assert.Equal(100, result.Settings.MaxOffspring);
        Assert.Equal(0, generator.CallCount);
        Assert.Equal(0, validationRunFactory.BuildCount);
    }

    [Fact]
    public async Task RunCycleAsync_WhenDistributedLockBusy_SkipsWithoutMutatingState()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var bundle = await NewContextAsync();
        var ctx = bundle.Ctx;

        ctx.Strategies.Add(NewStrategy(
            id: 10,
            strategyType: StrategyType.CompositeML,
            symbol: "EURUSD",
            timeframe: Timeframe.H1,
            parametersJson: """{"Fast":9,"Slow":21}""",
            status: StrategyStatus.Active,
            lifecycleStage: StrategyLifecycleStage.Active,
            generation: 1));
        await ctx.SaveChangesAsync();

        var generator = new TestEvolutionaryStrategyGenerator
        {
            Candidates =
            [
                new EvolutionaryCandidate(
                    ParentStrategyId: 10,
                    Generation: 2,
                    Symbol: "EURUSD",
                    Timeframe: Timeframe.H1,
                    StrategyType: StrategyType.CompositeML,
                    ParametersJson: """{"Fast":12,"Slow":34}""",
                    MutationDescription: "lock-busy")
            ]
        };
        var validationRunFactory = new TestValidationRunFactory(now.UtcDateTime);

        using var provider = BuildProvider(ctx, generator, validationRunFactory);
        var worker = CreateWorker(provider, timeProvider, new TestDistributedLock(lockAvailable: false));

        var result = await worker.RunCycleAsync(CancellationToken.None);
        ctx.ChangeTracker.Clear();

        Assert.Equal("lock_busy", result.SkippedReason);
        Assert.Equal(0, generator.CallCount);
        Assert.Equal(0, validationRunFactory.BuildCount);
        Assert.Equal(1, await ctx.Strategies.IgnoreQueryFilters().CountAsync());
        Assert.Empty(await ctx.BacktestRuns.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_PersistenceFailureRollsBackStrategyInsert()
    {
        var now = new DateTimeOffset(2026, 04, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        await using var bundle = await NewContextAsync();
        var ctx = bundle.Ctx;

        ctx.Strategies.Add(NewStrategy(
            id: 10,
            strategyType: StrategyType.CompositeML,
            symbol: "EURUSD",
            timeframe: Timeframe.H1,
            parametersJson: """{"Fast":9,"Slow":21}""",
            status: StrategyStatus.Active,
            lifecycleStage: StrategyLifecycleStage.Active,
            generation: 1));
        await ctx.SaveChangesAsync();

        var generator = new TestEvolutionaryStrategyGenerator
        {
            Candidates =
            [
                new EvolutionaryCandidate(
                    ParentStrategyId: 10,
                    Generation: 2,
                    Symbol: "EURUSD",
                    Timeframe: Timeframe.H1,
                    StrategyType: StrategyType.CompositeML,
                    ParametersJson: """{"Fast":12,"Slow":34}""",
                    MutationDescription: "factory-fails")
            ]
        };
        var validationRunFactory = new TestValidationRunFactory(now.UtcDateTime)
        {
            ThrowOnBuildBacktest = true
        };

        using var provider = BuildProvider(ctx, generator, validationRunFactory);
        var worker = CreateWorker(provider, timeProvider);

        var result = await worker.RunCycleAsync(CancellationToken.None);
        ctx.ChangeTracker.Clear();

        Assert.Equal(1, result.PersistenceFailureCount);
        Assert.Equal(0, result.InsertedCandidateCount);
        Assert.Equal(0, result.QueuedBacktestCount);
        Assert.Equal(1, await ctx.Strategies.IgnoreQueryFilters().CountAsync());
        Assert.Empty(await ctx.BacktestRuns.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData(0,    1, 1, 1)]    // jitter disabled → returns base unchanged
    [InlineData(60,   1, 1, 61)]   // base 1s + uniform[0, 60] ∈ [1, 61]
    [InlineData(600, 60, 60, 660)] // base 60s + uniform[0, 600] ∈ [60, 660]
    public void ApplyJitter_RespectsBoundsAndDisableSemantics(int jitterSeconds, int baseSeconds, int minTotal, int maxTotal)
    {
        var result = EvolutionaryGeneratorWorker.ApplyJitter(TimeSpan.FromSeconds(baseSeconds), jitterSeconds);
        Assert.InRange(result.TotalSeconds, minTotal, maxTotal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86_401)]
    public void Validator_RejectsOutOfRangePollJitter(int value)
    {
        var validator = new EvolutionaryGeneratorOptionsValidator();
        var result = validator.Validate(name: null,
            new EvolutionaryGeneratorOptions { PollJitterSeconds = value });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("PollJitterSeconds"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(17)]
    public void Validator_RejectsOutOfRangeBackoffShift(int value)
    {
        var validator = new EvolutionaryGeneratorOptionsValidator();
        var result = validator.Validate(name: null,
            new EvolutionaryGeneratorOptions { FailureBackoffCapShift = value });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("FailureBackoffCapShift"));
    }

    [Fact]
    public void Validator_RejectsZeroFleetSystemicCycles()
    {
        var validator = new EvolutionaryGeneratorOptionsValidator();
        var result = validator.Validate(name: null,
            new EvolutionaryGeneratorOptions { FleetSystemicConsecutiveZeroInsertCycles = 0 });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("FleetSystemicConsecutiveZeroInsertCycles"));
    }

    [Fact]
    public void Validator_RejectsZeroStalenessHours()
    {
        var validator = new EvolutionaryGeneratorOptionsValidator();
        var result = validator.Validate(name: null,
            new EvolutionaryGeneratorOptions { StalenessAlertHours = 0 });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("StalenessAlertHours"));
    }

    [Fact]
    public void CalculateDelay_UsesExponentialBackoffWithCeiling()
    {
        Assert.Equal(
            TimeSpan.FromDays(1),
            EvolutionaryGeneratorWorker.CalculateDelay(TimeSpan.FromDays(1), consecutiveFailures: 0));
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            EvolutionaryGeneratorWorker.CalculateDelay(TimeSpan.FromDays(1), consecutiveFailures: 1));
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            EvolutionaryGeneratorWorker.CalculateDelay(TimeSpan.FromDays(1), consecutiveFailures: 2));
        Assert.Equal(
            TimeSpan.FromHours(1),
            EvolutionaryGeneratorWorker.CalculateDelay(TimeSpan.FromDays(1), consecutiveFailures: 10));
    }

    private static EvolutionaryGeneratorWorker CreateWorker(
        ServiceProvider provider,
        TimeProvider timeProvider,
        IDistributedLock? distributedLock = null,
        EvolutionaryGeneratorOptions? options = null)
        => new(
            NullLogger<EvolutionaryGeneratorWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            metrics: null,
            timeProvider: timeProvider,
            healthMonitor: null,
            distributedLock: distributedLock,
            dbExceptionClassifier: null,
            options: options);

    private static ServiceProvider BuildProvider(
        EvolutionaryGeneratorTestContext context,
        TestEvolutionaryStrategyGenerator generator,
        TestValidationRunFactory validationRunFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWriteApplicationDbContext>(context);
        services.AddSingleton<IEvolutionaryStrategyGenerator>(generator);
        services.AddSingleton<IValidationRunFactory>(validationRunFactory);
        return services.BuildServiceProvider();
    }

    private static Strategy NewStrategy(
        long id,
        StrategyType strategyType,
        string symbol,
        Timeframe timeframe,
        string parametersJson,
        StrategyStatus status,
        StrategyLifecycleStage lifecycleStage,
        int generation)
        => new()
        {
            Id = id,
            Name = $"strategy-{id}",
            Description = $"strategy-{id}",
            StrategyType = strategyType,
            Symbol = symbol,
            Timeframe = timeframe,
            ParametersJson = parametersJson,
            Status = status,
            LifecycleStage = lifecycleStage,
            LifecycleStageEnteredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            Generation = generation
        };

    private static StrategyPerformanceSnapshot NewSnapshot(
        long id,
        long strategyId,
        decimal sharpeRatio,
        DateTime evaluatedAtUtc)
        => new()
        {
            Id = id,
            StrategyId = strategyId,
            WindowTrades = 10,
            WinningTrades = 6,
            LosingTrades = 4,
            WinRate = 0.6m,
            ProfitFactor = 1.4m,
            SharpeRatio = sharpeRatio,
            MaxDrawdownPct = 5m,
            TotalPnL = 100m,
            HealthScore = 0.8m,
            HealthStatus = StrategyHealthStatus.Healthy,
            EvaluatedAt = evaluatedAtUtc
        };

    private static EngineConfig NewConfig(
        long id,
        string key,
        string value,
        ConfigDataType dataType = ConfigDataType.String)
        => new()
        {
            Id = id,
            Key = key,
            Value = value,
            DataType = dataType,
            IsHotReloadable = true,
            LastUpdatedAt = DateTime.UtcNow
        };

    private static async Task<EvolutionaryGeneratorContextBundle> NewContextAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<EvolutionaryGeneratorTestContext>()
            .UseSqlite(connection)
            .Options;

        var context = new EvolutionaryGeneratorTestContext(options);
        await context.Database.EnsureCreatedAsync();
        return new EvolutionaryGeneratorContextBundle(context, connection);
    }

    private sealed record EvolutionaryGeneratorContextBundle(
        EvolutionaryGeneratorTestContext Ctx,
        SqliteConnection Connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Ctx.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class EvolutionaryGeneratorTestContext(DbContextOptions<EvolutionaryGeneratorTestContext> options)
        : DbContext(options), IWriteApplicationDbContext
    {
        public DbSet<EngineConfig> EngineConfigs => Set<EngineConfig>();
        public DbSet<Strategy> Strategies => Set<Strategy>();
        public DbSet<StrategyPerformanceSnapshot> StrategyPerformanceSnapshots => Set<StrategyPerformanceSnapshot>();
        public DbSet<BacktestRun> BacktestRuns => Set<BacktestRun>();
        public DbSet<Alert> Alerts => Set<Alert>();

        public DbContext GetDbContext() => this;

        public new Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => base.SaveChangesAsync(cancellationToken);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EngineConfig>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Key).IsRequired();
                builder.Property(x => x.Value).IsRequired();
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.HasIndex(x => x.Key).IsUnique();
            });

            modelBuilder.Entity<Strategy>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).IsRequired();
                builder.Property(x => x.Symbol).IsRequired();
                builder.Property(x => x.StrategyType).HasConversion<string>();
                builder.Property(x => x.Timeframe).HasConversion<string>();
                builder.Property(x => x.Status).HasConversion<string>();
                builder.Property(x => x.LifecycleStage).HasConversion<string>();
                builder.HasQueryFilter(x => !x.IsDeleted);
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

            modelBuilder.Entity<StrategyPerformanceSnapshot>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.Property(x => x.HealthStatus).HasConversion<string>();
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.HasOne(x => x.Strategy)
                    .WithMany()
                    .HasForeignKey(x => x.StrategyId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<BacktestRun>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Symbol).IsRequired();
                builder.Property(x => x.Timeframe).HasConversion<string>();
                builder.Property(x => x.Status).HasConversion<string>();
                builder.Property(x => x.QueueSource).HasConversion<string>();
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.HasIndex(x => x.ValidationQueueKey).IsUnique();
                builder.HasOne(x => x.Strategy)
                    .WithMany()
                    .HasForeignKey(x => x.StrategyId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<Alert>(builder =>
            {
                builder.HasKey(a => a.Id);
                builder.HasQueryFilter(a => !a.IsDeleted);
                builder.Property(a => a.AlertType).HasConversion<string>();
                builder.Property(a => a.Severity).HasConversion<string>();
                builder.HasIndex(a => a.DeduplicationKey);
            });
        }
    }

    private sealed class TestEvolutionaryStrategyGenerator : IEvolutionaryStrategyGenerator
    {
        public IReadOnlyList<EvolutionaryCandidate> Candidates { get; set; } = [];
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<EvolutionaryCandidate>> ProposeOffspringAsync(int maxOffspring, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<EvolutionaryCandidate>>(Candidates.Take(maxOffspring).ToList());
        }
    }

    private sealed class TestValidationRunFactory(DateTime nowUtc) : IValidationRunFactory
    {
        public bool ThrowOnBuildBacktest { get; set; }
        public int BuildCount { get; private set; }

        public Task<BacktestRun> BuildBacktestRunAsync(
            DbContext writeDb,
            BacktestQueueRequest request,
            CancellationToken ct)
        {
            BuildCount++;
            if (ThrowOnBuildBacktest)
                throw new InvalidOperationException("synthetic backtest build failure");

            return Task.FromResult(new BacktestRun
            {
                StrategyId = request.StrategyId,
                Symbol = request.Symbol,
                Timeframe = request.Timeframe,
                FromDate = request.FromDate,
                ToDate = request.ToDate,
                InitialBalance = request.InitialBalance,
                Status = RunStatus.Queued,
                QueueSource = request.QueueSource,
                Priority = request.Priority,
                ParametersSnapshotJson = request.ParametersSnapshotJson,
                ValidationQueueKey = request.ValidationQueueKey,
                CreatedAt = nowUtc,
                QueuedAt = nowUtc,
                AvailableAt = nowUtc,
            });
        }

        public Task<WalkForwardRun> BuildWalkForwardRunAsync(
            DbContext writeDb,
            WalkForwardQueueRequest request,
            CancellationToken ct)
            => throw new NotSupportedException();
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
