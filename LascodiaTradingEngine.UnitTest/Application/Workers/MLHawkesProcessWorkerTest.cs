using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLHawkesProcessWorkerTest
{
    [Fact]
    public void FitHawkesKernel_ReturnsStationaryFiniteFitWithinBranchingCap()
    {
        var start = DateTime.UtcNow.AddDays(-5);
        var end = start.AddDays(5);
        var timestamps = Enumerable.Range(0, 80)
            .Select(i => start.AddHours(12 + i * 0.8 + (i % 5 == 0 ? 0.05 : 0.0)))
            .ToList();

        var fit = MLHawkesProcessWorker.FitHawkesKernel(
            timestamps,
            start,
            end,
            maximumBranchingRatio: 0.40,
            optimisationSweeps: 30);

        Assert.True(fit.IsValid);
        Assert.True(fit.Alpha / fit.Beta < 0.401);
        Assert.True(double.IsFinite(fit.LogLikelihood));
    }

    [Fact]
    public async Task RunCycleAsync_FitsKernelNormalizesSymbolsAndSoftDeletesDuplicates()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLHawkes:MinimumFitSamples", "20", ConfigDataType.Int);
        AddConfig(db, "MLHawkes:OptimisationSweeps", "20", ConfigDataType.Int);
        AddConfig(db, "MLHawkes:SuppressMultiplier", "2.75", ConfigDataType.Decimal);
        AddConfig(db, "MLHawkes:PollIntervalSeconds", "600", ConfigDataType.Int);

        var strategy = AddStrategy(db, " eurusd ", Timeframe.H1);
        AddSignals(db, strategy, " eurusd ", 60);
        db.Set<MLHawkesKernelParams>().AddRange(
            new MLHawkesKernelParams
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Mu = 0.1,
                Alpha = 0.1,
                Beta = 0.5,
                FitSamples = 20,
                FittedAt = DateTime.UtcNow.AddDays(-2)
            },
            new MLHawkesKernelParams
            {
                Symbol = "eurusd",
                Timeframe = Timeframe.H1,
                Mu = 0.2,
                Alpha = 0.1,
                Beta = 0.6,
                FitSamples = 25,
                FittedAt = DateTime.UtcNow.AddDays(-1)
            });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        int pollSeconds = await worker.RunCycleAsync(CancellationToken.None);

        var activeKernels = await db.Set<MLHawkesKernelParams>().ToListAsync();
        var allKernels = await db.Set<MLHawkesKernelParams>().IgnoreQueryFilters().ToListAsync();

        Assert.Equal(600, pollSeconds);
        var active = Assert.Single(activeKernels);
        Assert.Equal("EURUSD", active.Symbol);
        Assert.Equal(Timeframe.H1, active.Timeframe);
        Assert.Equal(60, active.FitSamples);
        Assert.Equal(2.75, active.SuppressMultiplier, 2);
        Assert.True(active.Alpha < active.Beta);
        Assert.Contains(allKernels, k => k.IsDeleted && k.Symbol.Equals("EURUSD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunCycleAsync_SoftDeletesKernelWhenSampleCountFallsBelowMinimum()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLHawkes:MinimumFitSamples", "20", ConfigDataType.Int);
        var strategy = AddStrategy(db, "GBPUSD", Timeframe.M15);
        AddSignals(db, strategy, "GBPUSD", 5);
        db.Set<MLHawkesKernelParams>().Add(new MLHawkesKernelParams
        {
            Symbol = "GBPUSD",
            Timeframe = Timeframe.M15,
            Mu = 0.1,
            Alpha = 0.1,
            Beta = 0.5,
            FitSamples = 30,
            FittedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLHawkesKernelParams>().ToListAsync());
        Assert.Contains(
            await db.Set<MLHawkesKernelParams>().IgnoreQueryFilters().ToListAsync(),
            k => k.Symbol == "GBPUSD" && k.IsDeleted);
    }

    [Fact]
    public async Task HawkesSignalFilter_UsesConfigurableMaxAgeAndNormalizedSymbol()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLHawkes:MaxKernelAgeHours", "24", ConfigDataType.Int);
        db.Set<MLHawkesKernelParams>().Add(new MLHawkesKernelParams
        {
            Symbol = "eurusd",
            Timeframe = Timeframe.H1,
            Mu = 0.1,
            Alpha = 0.5,
            Beta = 1.0,
            SuppressMultiplier = 1.5,
            FitSamples = 50,
            FittedAt = DateTime.UtcNow.AddHours(-2)
        });
        await db.SaveChangesAsync();

        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);
        var filter = new HawkesSignalFilter(
            readContext.Object,
            Mock.Of<ILogger<HawkesSignalFilter>>());

        bool burst = await filter.IsBurstEpisodeAsync(
            " eurusd ",
            Timeframe.H1,
            [DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-10)],
            CancellationToken.None);

        Assert.True(burst);
    }

    [Fact]
    public async Task HawkesSignalFilter_RejectsKernelOlderThanConfiguredMaxAge()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLHawkes:MaxKernelAgeHours", "1", ConfigDataType.Int);
        db.Set<MLHawkesKernelParams>().Add(new MLHawkesKernelParams
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            Mu = 0.1,
            Alpha = 0.5,
            Beta = 1.0,
            SuppressMultiplier = 1.1,
            FitSamples = 50,
            FittedAt = DateTime.UtcNow.AddHours(-2)
        });
        await db.SaveChangesAsync();

        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);
        var filter = new HawkesSignalFilter(
            readContext.Object,
            Mock.Of<ILogger<HawkesSignalFilter>>());

        bool burst = await filter.IsBurstEpisodeAsync(
            "EURUSD",
            Timeframe.H1,
            [DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(-2)],
            CancellationToken.None);

        Assert.False(burst);
    }

    [Fact]
    public async Task RunCycleAsync_SkipsCycleWhenDistributedLockIsHeldElsewhere()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLHawkes:PollIntervalSeconds", "900", ConfigDataType.Int);
        var strategy = AddStrategy(db, "EURUSD", Timeframe.H1);
        AddSignals(db, strategy, "EURUSD", 40);
        await db.SaveChangesAsync();

        var distributedLock = new TestDistributedLock(acquire: false);
        var worker = CreateWorker(db, distributedLock);

        int pollSeconds = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(900, pollSeconds);
        Assert.Equal(1, distributedLock.Attempts);
        Assert.Empty(await db.Set<MLHawkesKernelParams>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_RespectsMaxPairsPerCycle()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLHawkes:MaxPairsPerCycle", "1", ConfigDataType.Int);
        AddConfig(db, "MLHawkes:OptimisationSweeps", "10", ConfigDataType.Int);
        var eur = AddStrategy(db, "EURUSD", Timeframe.H1);
        var gbp = AddStrategy(db, "GBPUSD", Timeframe.H1);
        AddSignals(db, eur, "EURUSD", 40);
        AddSignals(db, gbp, "GBPUSD", 40);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        await worker.RunCycleAsync(CancellationToken.None);

        var kernel = Assert.Single(await db.Set<MLHawkesKernelParams>().ToListAsync());
        Assert.Equal("EURUSD", kernel.Symbol);
    }

    [Fact]
    public async Task RunCycleAsync_RespectsMaxSignalsPerPair()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLHawkes:MinimumFitSamples", "20", ConfigDataType.Int);
        AddConfig(db, "MLHawkes:MaxSignalsPerPair", "25", ConfigDataType.Int);
        AddConfig(db, "MLHawkes:OptimisationSweeps", "10", ConfigDataType.Int);
        var strategy = AddStrategy(db, "EURUSD", Timeframe.H1);
        AddSignals(db, strategy, "EURUSD", 60);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        await worker.RunCycleAsync(CancellationToken.None);

        var kernel = Assert.Single(await db.Set<MLHawkesKernelParams>().ToListAsync());
        Assert.Equal(25, kernel.FitSamples);
    }

    [Fact]
    public async Task RunCycleAsync_SkipsWhenDisabledByRuntimeConfig()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLHawkes:Enabled", "false", ConfigDataType.Bool);
        AddConfig(db, "MLHawkes:PollIntervalSeconds", "900", ConfigDataType.Int);
        var strategy = AddStrategy(db, "EURUSD", Timeframe.H1);
        AddSignals(db, strategy, "EURUSD", 40);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        int pollSeconds = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(900, pollSeconds);
        Assert.Empty(await db.Set<MLHawkesKernelParams>().ToListAsync());
    }

    [Fact]
    public async Task RunCycleAsync_UsesLatestCaseInsensitiveInvariantRuntimeConfig()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLHawkes:MinimumFitSamples", "20", ConfigDataType.Int, DateTime.UtcNow.AddMinutes(-4));
        AddConfig(db, "MLHawkes:OptimisationSweeps", "10", ConfigDataType.Int, DateTime.UtcNow.AddMinutes(-3));
        AddConfig(db, "MLHawkes:SuppressMultiplier", "1.50", ConfigDataType.Decimal, DateTime.UtcNow.AddMinutes(-2));
        AddConfig(db, "mlhawkes:suppressmultiplier", "3.25", ConfigDataType.Decimal, DateTime.UtcNow.AddMinutes(-1));
        var strategy = AddStrategy(db, "EURUSD", Timeframe.H1);
        AddSignals(db, strategy, "EURUSD", 40);
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        await worker.RunCycleAsync(CancellationToken.None);

        var kernel = Assert.Single(await db.Set<MLHawkesKernelParams>().ToListAsync());
        Assert.Equal(3.25, kernel.SuppressMultiplier, 2);
    }

    [Fact]
    public async Task RunCycleAsync_RotatesMaxPairsByOldestExistingFit()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLHawkes:MaxPairsPerCycle", "1", ConfigDataType.Int);
        AddConfig(db, "MLHawkes:OptimisationSweeps", "10", ConfigDataType.Int);
        var eur = AddStrategy(db, "EURUSD", Timeframe.H1);
        var gbp = AddStrategy(db, "GBPUSD", Timeframe.H1);
        AddSignals(db, eur, "EURUSD", 40);
        AddSignals(db, gbp, "GBPUSD", 40);
        var eurFitAt = DateTime.UtcNow.AddDays(-1);
        var gbpFitAt = DateTime.UtcNow.AddDays(-10);
        db.Set<MLHawkesKernelParams>().AddRange(
            CreateKernel("EURUSD", Timeframe.H1, eurFitAt),
            CreateKernel("GBPUSD", Timeframe.H1, gbpFitAt));
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        await worker.RunCycleAsync(CancellationToken.None);

        var kernels = await db.Set<MLHawkesKernelParams>().OrderBy(k => k.Symbol).ToListAsync();
        var eurKernel = Assert.Single(kernels, k => k.Symbol == "EURUSD");
        var gbpKernel = Assert.Single(kernels, k => k.Symbol == "GBPUSD");
        Assert.Equal(eurFitAt, eurKernel.FittedAt);
        Assert.True(gbpKernel.FittedAt > gbpFitAt);
        Assert.Equal(40, gbpKernel.FitSamples);
    }

    [Fact]
    public async Task RunCycleAsync_DoesNotFitPausedStrategyFromNonLiveModel()
    {
        await using var db = CreateDbContext();
        AddConfig(db, "MLHawkes:MinimumFitSamples", "20", ConfigDataType.Int);
        AddConfig(db, "MLHawkes:OptimisationSweeps", "10", ConfigDataType.Int);
        var strategy = AddStrategy(db, "EURUSD", Timeframe.H1, StrategyStatus.Paused);
        AddSignals(db, strategy, "EURUSD", 40);
        db.Set<MLModel>().Add(new MLModel
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive = true,
            Status = MLModelStatus.Training,
            ModelVersion = "test",
            FilePath = "test-model.json"
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(db);

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(await db.Set<MLHawkesKernelParams>().ToListAsync());
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private static MLHawkesProcessWorker CreateWorker(
        WriteApplicationDbContext db,
        IDistributedLock? distributedLock = null)
    {
        var readContext = new Mock<IReadApplicationDbContext>();
        readContext.Setup(c => c.GetDbContext()).Returns(db);

        var writeContext = new Mock<IWriteApplicationDbContext>();
        writeContext.Setup(c => c.GetDbContext()).Returns(db);

        var services = new ServiceCollection();
        services.AddScoped(_ => readContext.Object);
        services.AddScoped(_ => writeContext.Object);
        var provider = services.BuildServiceProvider();

        return new MLHawkesProcessWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<MLHawkesProcessWorker>>(),
            distributedLock);
    }

    private static Strategy AddStrategy(
        WriteApplicationDbContext db,
        string symbol,
        Timeframe timeframe,
        StrategyStatus status = StrategyStatus.Active)
    {
        var strategy = new Strategy
        {
            Name = $"{symbol}-{timeframe}",
            Symbol = symbol,
            Timeframe = timeframe,
            Status = status
        };
        db.Set<Strategy>().Add(strategy);
        return strategy;
    }

    private static void AddSignals(WriteApplicationDbContext db, Strategy strategy, string symbol, int count)
    {
        var start = DateTime.UtcNow.AddDays(-2);
        for (int i = 0; i < count; i++)
        {
            db.Set<TradeSignal>().Add(new TradeSignal
            {
                Strategy = strategy,
                StrategyId = strategy.Id,
                Symbol = symbol,
                GeneratedAt = start.AddMinutes(i * 45),
                ExpiresAt = start.AddMinutes(i * 45 + 30),
                EntryPrice = 1.1000m,
                SuggestedLotSize = 0.01m,
                Confidence = 0.75m
            });
        }
    }

    private static void AddConfig(
        WriteApplicationDbContext db,
        string key,
        string value,
        ConfigDataType dataType,
        DateTime? lastUpdatedAt = null)
        => db.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = key,
            Value = value,
            DataType = dataType,
            IsHotReloadable = true,
            LastUpdatedAt = lastUpdatedAt ?? DateTime.UtcNow
        });

    private static MLHawkesKernelParams CreateKernel(string symbol, Timeframe timeframe, DateTime fittedAt)
        => new()
        {
            Symbol = symbol,
            Timeframe = timeframe,
            Mu = 0.1,
            Alpha = 0.1,
            Beta = 0.5,
            FitSamples = 20,
            FittedAt = fittedAt
        };

    private sealed class TestDistributedLock : IDistributedLock
    {
        private readonly bool _acquire;

        public TestDistributedLock(bool acquire)
        {
            _acquire = acquire;
        }

        public int Attempts { get; private set; }

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
            => TryAcquireAsync(lockKey, TimeSpan.Zero, ct);

        public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
        {
            Attempts++;
            return Task.FromResult<IAsyncDisposable?>(_acquire ? new Handle() : null);
        }

        private sealed class Handle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
