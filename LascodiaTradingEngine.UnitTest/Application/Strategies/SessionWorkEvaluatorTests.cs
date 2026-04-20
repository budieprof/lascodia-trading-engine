using System.Diagnostics.Metrics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Strategies.Evaluators;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies;

/// <summary>
/// Behavioural coverage for the three Session 1 / 2 / 3 evaluators that weren't previously
/// tested: CalendarEffect, NewsFade, CarryTrade. Each evaluator's happy-path direction logic
/// and at least one rejection branch is exercised with minimal candle fixtures.
/// </summary>
public class SessionWorkEvaluatorTests : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;

    public SessionWorkEvaluatorTests() => _metrics = new TradingMetrics(_meterFactory);
    public void Dispose() { _meterFactory.Dispose(); GC.SuppressFinalize(this); }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); _meters.Clear(); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Candle fixtures
    // ═════════════════════════════════════════════════════════════════════════

    private static List<Candle> MakeCandles(int count, decimal startPrice, decimal stepPerBar,
        DateTime startUtc, TimeSpan period, decimal spread = 0.0010m)
    {
        var list = new List<Candle>(count);
        for (int i = 0; i < count; i++)
        {
            decimal close = startPrice + stepPerBar * i;
            list.Add(new Candle
            {
                Id = i + 1,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H4,
                Open = close - spread * 0.5m,
                High = close + spread,
                Low = close - spread,
                Close = close,
                Volume = 1000m,
                Timestamp = startUtc + TimeSpan.FromTicks(period.Ticks * i),
                IsClosed = true,
            });
        }
        return list;
    }

    private static Strategy MakeStrategy(StrategyType type, string paramsJson, string symbol = "EURUSD") => new()
    {
        Id = 1, Name = $"Test {type}", StrategyType = type, Symbol = symbol,
        Timeframe = Timeframe.H4, Status = StrategyStatus.Active, ParametersJson = paramsJson,
    };

    // ═════════════════════════════════════════════════════════════════════════
    // CalendarEffectEvaluator
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CalendarEffect_MonthEnd_FadesUpMomentumToSell()
    {
        // Last day of a month (2026-04-30) with 15 rising bars before → expect SELL (fade)
        var opts = new StrategyEvaluatorOptions { AtrPeriodForSlTp = 5 };
        var evaluator = new CalendarEffectEvaluator(opts, NullLogger<CalendarEffectEvaluator>.Instance, _metrics);
        var baseTime = new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeCandles(count: 20, startPrice: 1.1000m, stepPerBar: 0.0020m,
            startUtc: baseTime, period: TimeSpan.FromHours(1));

        var strategy = MakeStrategy(StrategyType.CalendarEffect,
            """{"Mode":"MonthEnd","LookbackBars":5,"MomentumAtrThreshold":0.5,"MonthEndBusinessDays":3}""");

        var signal = await evaluator.EvaluateAsync(strategy, candles,
            (Bid: 1.1395m, Ask: 1.1397m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Sell, signal!.Direction);
    }

    [Fact]
    public async Task CalendarEffect_LondonNyOverlap_FollowsUpMomentumToBuy()
    {
        // Bar timestamp at 14:00 UTC (inside 13-16 overlap) with positive momentum → BUY (continuation)
        var opts = new StrategyEvaluatorOptions { AtrPeriodForSlTp = 5 };
        var evaluator = new CalendarEffectEvaluator(opts, NullLogger<CalendarEffectEvaluator>.Instance, _metrics);
        var baseTime = new DateTime(2026, 4, 15, 9, 0, 0, DateTimeKind.Utc);
        var candles = MakeCandles(count: 15, startPrice: 1.2000m, stepPerBar: 0.0030m,
            startUtc: baseTime, period: TimeSpan.FromHours(1));
        // candle [14] is at 23:00 UTC — outside overlap; shift so last bar lands at 14:00
        candles = MakeCandles(count: 15, startPrice: 1.2000m, stepPerBar: 0.0030m,
            startUtc: new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc), period: TimeSpan.FromHours(1));

        var strategy = MakeStrategy(StrategyType.CalendarEffect,
            """{"Mode":"LondonNyOverlap","LookbackBars":4,"MomentumAtrThreshold":0.5,"OverlapStartHourUtc":13,"OverlapEndHourUtc":16}""");

        var signal = await evaluator.EvaluateAsync(strategy, candles,
            (Bid: 1.2418m, Ask: 1.2420m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
    }

    [Fact]
    public async Task CalendarEffect_OutsideWindow_ReturnsNull()
    {
        var opts = new StrategyEvaluatorOptions { AtrPeriodForSlTp = 5 };
        var evaluator = new CalendarEffectEvaluator(opts, NullLogger<CalendarEffectEvaluator>.Instance, _metrics);
        // Mid-month (2026-04-15) — outside MonthEnd window
        var candles = MakeCandles(20, 1.1000m, 0.0020m,
            new DateTime(2026, 4, 15, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1));
        var strategy = MakeStrategy(StrategyType.CalendarEffect,
            """{"Mode":"MonthEnd","LookbackBars":5,"MomentumAtrThreshold":0.5,"MonthEndBusinessDays":3}""");

        var signal = await evaluator.EvaluateAsync(strategy, candles,
            (Bid: 1.1395m, Ask: 1.1397m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task CalendarEffect_InsufficientMomentum_ReturnsNull()
    {
        // In-window (month-end) but nearly flat momentum — rejected
        var opts = new StrategyEvaluatorOptions { AtrPeriodForSlTp = 5 };
        var evaluator = new CalendarEffectEvaluator(opts, NullLogger<CalendarEffectEvaluator>.Instance, _metrics);
        var candles = MakeCandles(20, 1.1000m, 0.00001m,
            new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1));
        var strategy = MakeStrategy(StrategyType.CalendarEffect,
            """{"Mode":"MonthEnd","LookbackBars":5,"MomentumAtrThreshold":2.0,"MonthEndBusinessDays":3}""");

        var signal = await evaluator.EvaluateAsync(strategy, candles,
            (Bid: 1.1000m, Ask: 1.1002m), CancellationToken.None);

        Assert.Null(signal);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // NewsFadeEvaluator — needs a scoped IReadApplicationDbContext, so we wire
    // an in-memory EF context via IServiceScopeFactory. A scoped factory is the
    // simplest path to match the evaluator's production DI shape.
    // ═════════════════════════════════════════════════════════════════════════

    private static (IServiceScopeFactory Factory, ApplicationDbContextFake Ctx) BuildScopeWithDb(
        DbContextOptions<ApplicationDbContextFake> dbOpts)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContextFake(dbOpts));
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContextFake>());
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContextFake>());
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IServiceScopeFactory>();
        using var scope = factory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContextFake>();
        return (factory, ctx);
    }

    [Fact]
    public async Task NewsFade_NoMatchingEvent_ReturnsNull()
    {
        var dbOpts = new DbContextOptionsBuilder<ApplicationDbContextFake>()
            .UseInMemoryDatabase($"newsfade-{Guid.NewGuid()}").Options;
        var (factory, _) = BuildScopeWithDb(dbOpts);

        var opts = new StrategyEvaluatorOptions { AtrPeriodForSlTp = 5 };
        var evaluator = new NewsFadeEvaluator(factory, opts, NullLogger<NewsFadeEvaluator>.Instance, _metrics);
        var candles = MakeCandles(25, 1.1000m, 0.0020m,
            new DateTime(2026, 4, 15, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1));
        var strategy = MakeStrategy(StrategyType.NewsFade,
            """{"MinMinutesSinceEvent":3,"MaxMinutesSinceEvent":15,"MomentumAtrThreshold":0.5}""");

        var signal = await evaluator.EvaluateAsync(strategy, candles,
            (Bid: 1.1478m, Ask: 1.1480m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task NewsFade_HighImpactEventAndStrongUpCandle_FiresSell()
    {
        var dbOpts = new DbContextOptionsBuilder<ApplicationDbContextFake>()
            .UseInMemoryDatabase($"newsfade-{Guid.NewGuid()}").Options;
        var (factory, _) = BuildScopeWithDb(dbOpts);

        var baseTime = new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);
        var candles = MakeCandles(25, 1.1000m, 0.0020m, baseTime, TimeSpan.FromHours(1));
        // Last candle spans 12:00..13:00 at index 24 (timestamp last). Craft an up-body candle
        // exceeding 0.5 × ATR so momentum passes the threshold.
        var last = candles[^1];
        last.Open = 1.1400m;
        last.Close = 1.1480m;
        last.High = 1.1490m;
        last.Low  = 1.1395m;
        candles[^1] = last;

        using var scope = factory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContextFake>();
        ctx.Set<EconomicEvent>().Add(new EconomicEvent
        {
            Id = 1,
            Title = "US NFP",
            Currency = "USD",
            Impact = EconomicImpact.High,
            ScheduledAt = candles[^1].Timestamp.AddMinutes(-5),
        });
        await ctx.SaveChangesAsync();

        var opts = new StrategyEvaluatorOptions { AtrPeriodForSlTp = 5, NewsFadeMaxSpreadAtrFraction = 0m };
        var evaluator = new NewsFadeEvaluator(factory, opts, NullLogger<NewsFadeEvaluator>.Instance, _metrics);
        var strategy = MakeStrategy(StrategyType.NewsFade,
            """{"MinMinutesSinceEvent":3,"MaxMinutesSinceEvent":30,"MomentumAtrThreshold":0.3}""");

        var signal = await evaluator.EvaluateAsync(strategy, candles,
            (Bid: 1.1478m, Ask: 1.1480m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Sell, signal!.Direction);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CarryTradeEvaluator
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CarryTrade_PersistentUpDrift_FiresBuy()
    {
        var dbOpts = new DbContextOptionsBuilder<ApplicationDbContextFake>()
            .UseInMemoryDatabase($"carry-{Guid.NewGuid()}").Options;
        var (factory, _) = BuildScopeWithDb(dbOpts);
        using (var scope = factory.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContextFake>();
            ctx.Set<CurrencyPair>().Add(new CurrencyPair
            {
                Id = 1, Symbol = "EURUSD",
                BaseCurrency = "EUR", QuoteCurrency = "USD",
                SwapLong = 1.5, SwapShort = -2.0, SwapMode = 2, // favorable for long
                IsActive = true,
            });
            await ctx.SaveChangesAsync();
        }

        var opts = new StrategyEvaluatorOptions
        {
            AtrPeriodForSlTp = 14,
            CarryTradeMaxSpreadAtrFraction = 0m, // disable spread gate
        };
        var evaluator = new CarryTradeEvaluator(factory, opts, NullLogger<CarryTradeEvaluator>.Instance, _metrics);
        // 120 candles of steady uptrend >> proxy threshold
        var candles = MakeCandles(120, 1.0500m, 0.0010m,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(4));
        var strategy = MakeStrategy(StrategyType.CarryTrade,
            """{"MinCarryStrength":0.3,"HorizonMultiplier":2.0,"RequireFavorableSwap":true}""");

        var signal = await evaluator.EvaluateAsync(strategy, candles,
            (Bid: 1.1688m, Ask: 1.1692m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
    }

    [Fact]
    public async Task CarryTrade_AdverseSwap_RejectsSignal()
    {
        var dbOpts = new DbContextOptionsBuilder<ApplicationDbContextFake>()
            .UseInMemoryDatabase($"carry-{Guid.NewGuid()}").Options;
        var (factory, _) = BuildScopeWithDb(dbOpts);
        using (var scope = factory.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContextFake>();
            ctx.Set<CurrencyPair>().Add(new CurrencyPair
            {
                Id = 1, Symbol = "EURUSD",
                BaseCurrency = "EUR", QuoteCurrency = "USD",
                SwapLong = -3.0, SwapShort = 1.0, SwapMode = 2, // adverse for long
                IsActive = true,
            });
            await ctx.SaveChangesAsync();
        }

        var opts = new StrategyEvaluatorOptions
        {
            AtrPeriodForSlTp = 14,
            CarryTradeMaxSpreadAtrFraction = 0m,
        };
        var evaluator = new CarryTradeEvaluator(factory, opts, NullLogger<CarryTradeEvaluator>.Instance, _metrics);
        var candles = MakeCandles(120, 1.0500m, 0.0010m,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(4));
        var strategy = MakeStrategy(StrategyType.CarryTrade,
            """{"MinCarryStrength":0.3,"HorizonMultiplier":2.0,"RequireFavorableSwap":true}""");

        var signal = await evaluator.EvaluateAsync(strategy, candles,
            (Bid: 1.1688m, Ask: 1.1692m), CancellationToken.None);

        Assert.Null(signal);
    }
}

/// <summary>
/// In-memory EF Core context used only by this test file. Exposes the minimal entity sets
/// the evaluators touch (EconomicEvent, CurrencyPair). Implements both read/write interfaces
/// so the evaluator's DI lookup finds it.
/// </summary>
internal sealed class ApplicationDbContextFake : DbContext,
    IReadApplicationDbContext, IWriteApplicationDbContext
{
    public ApplicationDbContextFake(DbContextOptions<ApplicationDbContextFake> options) : base(options) { }

    public DbSet<EconomicEvent>             EconomicEvents  => Set<EconomicEvent>();
    public DbSet<CurrencyPair>              CurrencyPairs   => Set<CurrencyPair>();
    public DbSet<MarketRegimeSnapshot>      Regimes         => Set<MarketRegimeSnapshot>();
    public DbSet<EngineConfig>              EngineConfigs   => Set<EngineConfig>();
    public DbSet<StrategyGenerationFailure> Failures        => Set<StrategyGenerationFailure>();

    public DbContext GetDbContext() => this;

    public new int SaveChanges() => base.SaveChanges();

    public new Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => base.SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EconomicEvent>().HasKey(e => e.Id);
        modelBuilder.Entity<CurrencyPair>().HasKey(e => e.Id);
        modelBuilder.Entity<MarketRegimeSnapshot>().HasKey(e => e.Id);
        modelBuilder.Entity<EngineConfig>().HasKey(e => e.Id);
        modelBuilder.Entity<StrategyGenerationFailure>().HasKey(e => e.Id);
    }
}
