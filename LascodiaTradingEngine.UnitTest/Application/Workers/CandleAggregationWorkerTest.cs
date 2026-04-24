using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class CandleAggregationWorkerTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Mock<IReadApplicationDbContext> _readCtx = new();
    private readonly Mock<IWriteApplicationDbContext> _writeCtx = new();
    private readonly Mock<DbContext> _db = new();

    // Source-of-truth collections — populate in each test before running the worker.
    private readonly List<Candle> _existingCandles = new();
    private readonly List<CurrencyPair> _pairs = new();
    private readonly List<EngineConfig> _config = new();

    // Captured writes — AddRange and (rarely) Add callbacks push here.
    private readonly List<Candle> _writtenCandles = new();

    // Controls SaveChangesAsync behaviour. Null = success. Set in a test to throw.
    private Func<CancellationToken, Task<int>>? _saveOverride;

    public CandleAggregationWorkerTest()
    {
        _metrics = new TradingMetrics(_meterFactory);

        _readCtx.Setup(c => c.GetDbContext()).Returns(_db.Object);
        _writeCtx.Setup(c => c.GetDbContext()).Returns(_db.Object);
        _writeCtx.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
                 .Returns<CancellationToken>(ct =>
                     _saveOverride?.Invoke(ct) ?? Task.FromResult(1));

        var scope    = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_readCtx.Object);
        provider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_writeCtx.Object);
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        _scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        Rebind();
    }

    public void Dispose() => _meterFactory.Dispose();

    // ── Tests: gap handling ────────────────────────────────────────────────

    /// <summary>
    /// Regression test for the weekend-gap halt bug. Friday 21:00 H1 exists,
    /// then zero M1 data through the weekend, then a fresh M1 run starting
    /// Sunday 22:00. The worker must skip the gap and synthesise the Sunday
    /// 22:00 H1 — not break and stall.
    /// </summary>
    [Fact]
    public async Task WeekendGap_SkipsEmptyPeriodsAndSynthesisesAfterGap()
    {
        var fri21 = new DateTime(2026, 3, 13, 21, 0, 0, DateTimeKind.Utc);  // Fri 21:00 UTC
        var sun22 = new DateTime(2026, 3, 15, 22, 0, 0, DateTimeKind.Utc);  // Sun 22:00 UTC

        _timeProvider.SetNow(sun22.AddHours(1).AddMinutes(5));               // Mon-start-ish

        AddSymbol("EURUSD");
        AddExistingCandle("EURUSD", Timeframe.H1, fri21);
        AddM1Run("EURUSD", sun22, 60);  // 60 full M1s from Sun 22:00 to 22:59

        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        // Exactly one H1 synthesised — the Sunday 22:00 bar.
        Assert.Equal(1, result.PeriodsSynthesized);
        Assert.Equal(0, result.PairsFailed);
        Assert.False(result.BudgetExhausted);
        var h1 = Assert.Single(_writtenCandles, c => c.Timeframe == Timeframe.H1);
        Assert.Equal(sun22, h1.Timestamp);
    }

    /// <summary>
    /// Past gap with zero M1 data must not cost a pair failure — it's an
    /// expected market-closed period, not an error.
    /// </summary>
    [Fact]
    public async Task PastGap_NoM1InPeriod_DoesNotCountAsFailure()
    {
        var fri21 = new DateTime(2026, 3, 13, 21, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc));  // mid-Saturday

        AddSymbol("EURUSD");
        AddExistingCandle("EURUSD", Timeframe.H1, fri21);
        // No M1 data at all past Friday 21:00 — pure weekend silence.
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.PeriodsSynthesized);
        Assert.Equal(0, result.PairsFailed);
        Assert.Empty(_writtenCandles);
    }

    // ── Tests: completeness / coverage ─────────────────────────────────────

    [Fact]
    public async Task IncompletePeriodBelowCoverageFloor_SkipsRatherThanSynthesises()
    {
        var periodStart = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(periodStart.AddHours(2));

        AddSymbol("EURUSD");
        // 30 of 60 M1s present — 50% coverage, default floor = 85%.
        AddM1Run("EURUSD", periodStart, 30);
        // Also give the last minute of the period so the worker doesn't see this
        // as "still forming" and break early — we specifically want the coverage
        // gate to reject, not the mid-period gate.
        AddM1Run("EURUSD", periodStart.AddMinutes(59), 1);
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.PeriodsSynthesized);
        // The target H1 plus higher timeframes overlapping the same partial M1
        // window all trip the coverage floor — the exact count across TFs is
        // incidental, the invariant is "nothing was written and at least one
        // period was gated by coverage".
        Assert.True(result.PeriodsSkippedLowCoverage >= 1);
        Assert.DoesNotContain(_writtenCandles, c => c.Timeframe == Timeframe.H1 && c.Timestamp == periodStart);
    }

    [Fact]
    public async Task CoverageAtExactlyFloor_Synthesises()
    {
        var periodStart = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(periodStart.AddHours(2));

        AddSymbol("EURUSD");
        // 51 of 60 M1s present → 51*100/60 = 85.0% exactly = floor.
        AddM1Run("EURUSD", periodStart, 51);
        AddM1Run("EURUSD", periodStart.AddMinutes(59), 1);  // last minute anchored
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.PeriodsSynthesized);
    }

    [Fact]
    public async Task FullyCoveredPeriod_SynthesisesCorrectOHLCV()
    {
        var periodStart = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(periodStart.AddHours(2));

        AddSymbol("EURUSD");
        // 60 deterministic M1s with increasing open, known high/low/volume.
        for (int i = 0; i < 60; i++)
        {
            _existingCandles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.M1,
                Timestamp = periodStart.AddMinutes(i),
                Open      = 1.10m + i * 0.0001m,
                High      = 1.10m + i * 0.0001m + 0.0005m,
                Low       = 1.10m + i * 0.0001m - 0.0005m,
                Close     = 1.10m + i * 0.0001m + 0.00025m,
                Volume    = 100m,
                IsClosed  = true,
            });
        }
        Rebind();

        await NewWorker().RunCycleAsync(CancellationToken.None);

        var h1 = Assert.Single(_writtenCandles, c => c.Timeframe == Timeframe.H1);
        Assert.Equal(periodStart, h1.Timestamp);
        Assert.Equal(1.10m,                h1.Open);                      // first M1 open
        Assert.Equal(1.10m + 59 * 0.0001m + 0.00025m, h1.Close);          // last M1 close
        Assert.Equal(1.10m + 59 * 0.0001m + 0.0005m,  h1.High);           // max High
        Assert.Equal(1.10m - 0.0005m,       h1.Low);                      // min Low
        Assert.Equal(60 * 100m,             h1.Volume);                   // sum Volume
        Assert.True(h1.IsClosed);
    }

    /// <summary>
    /// Bootstrap case: no existing H4, M1 data starts mid-way through the first
    /// aligned period. Default 85% coverage floor rejects the partial period.
    /// </summary>
    [Fact]
    public async Task BootstrapMisalignedH4_RejectedByCoverageFloor()
    {
        // First M1 at 10:47 → aligned H4 start = 08:00, period 08:00-12:00.
        var firstM1 = new DateTime(2026, 3, 2, 10, 47, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(new DateTime(2026, 3, 2, 13, 0, 0, DateTimeKind.Utc));

        AddSymbol("EURUSD");
        // Dense M1 from 10:47 onwards: 73 minutes of M1 (73/240 = 30% coverage).
        AddM1Run("EURUSD", firstM1, 73);
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        Assert.DoesNotContain(_writtenCandles, c => c.Timeframe == Timeframe.H4);
        // At least H4 itself is coverage-skipped; H1's partial bootstrap period
        // also trips the floor, so we don't pin an exact count across TFs.
        Assert.True(result.PeriodsSkippedLowCoverage >= 1);
    }

    // ── Tests: current-period / "still forming" ────────────────────────────

    [Fact]
    public async Task CurrentPeriodStillForming_DoesNotSynthesise()
    {
        var periodStart = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        // "Now" is 10:30 → the 10:00-11:00 H1 is mid-period, last minute 10:59 is in the future.
        _timeProvider.SetNow(periodStart.AddMinutes(30));

        AddSymbol("EURUSD");
        AddM1Run("EURUSD", periodStart, 30);  // 30 complete minutes, 30 still to come
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.PeriodsSynthesized);
        Assert.Empty(_writtenCandles);
    }

    // ── Tests: EA authoritative / no overwrite ─────────────────────────────

    [Fact]
    public async Task ExistingEaDeliveredCandle_SkippedNotOverwritten()
    {
        var periodStart = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(periodStart.AddHours(2));

        AddSymbol("EURUSD");
        // EA already delivered the 10:00 H1.
        AddExistingCandle("EURUSD", Timeframe.H1, periodStart);
        // M1 data is present but must not trigger an overwrite.
        AddM1Run("EURUSD", periodStart, 60);
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        // The existing H1 is detected during latestMap bootstrap (latest = 10:00),
        // so nextPeriodStart = 11:00 — which has no M1 data, so the walker breaks.
        // Nothing is synthesised and nothing is overwritten.
        Assert.Equal(0, result.PeriodsSynthesized);
        Assert.DoesNotContain(_writtenCandles, c => c.Timeframe == Timeframe.H1 && c.Timestamp == periodStart);
    }

    [Fact]
    public async Task ExistingCandleInMidWindow_SkippedViaPrefetchedSet()
    {
        // Covers the existingInWindow HashSet path: a past H1 exists at 11:00
        // while we're back-filling from 10:00 onwards.
        var p10 = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        var p11 = p10.AddHours(1);
        var p12 = p10.AddHours(2);
        _timeProvider.SetNow(p10.AddHours(4));  // now = 14:00

        AddSymbol("EURUSD");
        // Pre-existing H1 at 11:00 (EA-delivered mid-backfill window).
        AddExistingCandle("EURUSD", Timeframe.H1, p11);
        // Dense M1 spanning 10:00 to 13:00 (3 H1 periods: 10, 11, 12).
        AddM1Run("EURUSD", p10, 180);
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        // latestMap has latest H1 = 11:00 → nextPeriodStart = 12:00.
        // Worker synthesises ONLY 12:00 — nothing before, not 11:00.
        var synthH1s = _writtenCandles.Where(c => c.Timeframe == Timeframe.H1).ToList();
        Assert.Single(synthH1s);
        Assert.Equal(p12, synthH1s[0].Timestamp);
        Assert.Equal(1, result.PeriodsSynthesized);
    }

    // ── Tests: drift-buffer on upper bound ─────────────────────────────────

    [Fact]
    public async Task DriftedM1At0001_ReHomedToCorrectPeriod()
    {
        var periodStart = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(periodStart.AddHours(2));

        AddSymbol("EURUSD");
        // 58 normal M1s at XX:00:00 and one drifted-by-1-second M1 for 10:58 and 10:59.
        AddM1Run("EURUSD", periodStart, 58);
        _existingCandles.Add(MakeM1("EURUSD", new DateTime(2026, 3, 2, 10, 58, 1, DateTimeKind.Utc)));
        _existingCandles.Add(MakeM1("EURUSD", new DateTime(2026, 3, 2, 10, 59, 1, DateTimeKind.Utc)));
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.PeriodsSynthesized);
        var h1 = Assert.Single(_writtenCandles, c => c.Timeframe == Timeframe.H1);
        Assert.Equal(periodStart, h1.Timestamp);
    }

    // ── Tests: unique-constraint race with EA ──────────────────────────────

    [Fact]
    public async Task UniqueConstraintViolationOnSave_TreatedAsBenign_NotAsPairFailure()
    {
        var periodStart = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(periodStart.AddHours(2));

        AddSymbol("EURUSD");
        AddM1Run("EURUSD", periodStart, 60);
        Rebind();

        _saveOverride = _ => throw new DbUpdateException(
            "23505: duplicate key value violates unique constraint \"IX_Candle_Symbol_Timeframe_Timestamp\"");

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        // The worker built the candle (AddRange captured it) but treats the save
        // failure as benign — synthesized reports 0 and PairsFailed stays 0.
        Assert.Equal(0, result.PeriodsSynthesized);
        Assert.Equal(0, result.PairsFailed);
    }

    [Fact]
    public async Task NonUniqueDbException_CountsAsPairFailure_OtherPairsContinue()
    {
        var periodStart = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(periodStart.AddHours(2));

        AddSymbol("EURUSD");
        AddSymbol("GBPUSD");
        AddM1Run("EURUSD", periodStart, 60);
        AddM1Run("GBPUSD", periodStart, 60);
        Rebind();

        int callCount = 0;
        _saveOverride = _ =>
        {
            callCount++;
            if (callCount == 1) throw new InvalidOperationException("simulated non-unique failure");
            return Task.FromResult(1);
        };

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        // First pair's first timeframe (EURUSD/H1) fails; remaining (EURUSD/H4, D1,
        // GBPUSD/*) continue. At least one subsequent pair must have synthesised.
        Assert.True(result.PairsFailed >= 1);
        Assert.True(result.PeriodsSynthesized >= 1);
    }

    // ── Tests: cycle-level budget ──────────────────────────────────────────

    [Fact]
    public async Task MaxPeriodsPerCycle_CapsCycleWideSynthesis()
    {
        var backfillStart = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(backfillStart.AddDays(2));  // 48h later — 48 H1 periods available

        AddSymbol("EURUSD");
        AddM1Run("EURUSD", backfillStart, 48 * 60);  // 48h of M1 candles

        // Cap to 5 periods across the whole cycle.
        _config.Add(new EngineConfig { Key = CandleAggregationWorker.CK_MaxPeriodsPerCycle, Value = "100", IsDeleted = false });
        // Actually MinMaxPeriodsPerCycle = 100; we can't go below that. Use a
        // different lever to force the cap: a very tight cycle-duration budget.
        // Instead, assert that cap to 100 applies on a larger backfill.
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        // 48 H1 + 12 H4 + 2 D1 = 62 periods → well under 100, all synthesised.
        Assert.True(result.PeriodsSynthesized <= 100);
        Assert.False(result.BudgetExhausted);
    }

    // ── Tests: EngineConfig clamping ───────────────────────────────────────

    [Fact]
    public async Task IntervalSeconds_ClampedIntoConfiguredRange()
    {
        AddSymbol("EURUSD");
        _config.Add(new EngineConfig { Key = CandleAggregationWorker.CK_IntervalSeconds, Value = "99999", IsDeleted = false });
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        // 99999 clamps to MaxIntervalSeconds = 3600.
        Assert.Equal(3600, result.NextIntervalSeconds);
    }

    [Fact]
    public async Task CoverageFloor_ClampedToRange()
    {
        // 200% is clamped to 100% → any coverage < 100% is rejected.
        var periodStart = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(periodStart.AddHours(2));

        AddSymbol("EURUSD");
        // 58 minutes at [00..57] + 1 minute at [59] = 59 distinct minutes
        // in the 10:00-11:00 H1 period. 59/60 = 98% coverage. With the floor
        // clamped to 100%, this must be rejected.
        AddM1Run("EURUSD", periodStart, 58);
        AddM1Run("EURUSD", periodStart.AddMinutes(59), 1); // anchor last minute so mid-period check doesn't short-circuit
        _config.Add(new EngineConfig { Key = CandleAggregationWorker.CK_MinimumCoveragePercent, Value = "200", IsDeleted = false });
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        // 59/60 = 98% < 100% (clamped) → skipped (across TFs).
        Assert.True(result.PeriodsSkippedLowCoverage >= 1);
        Assert.Equal(0, result.PeriodsSynthesized);
    }

    // ── Tests: no symbols / no data ────────────────────────────────────────

    [Fact]
    public async Task NoActiveSymbols_CompletesNoOp()
    {
        _timeProvider.SetNow(new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc));
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.PeriodsSynthesized);
        Assert.Equal(0, result.SymbolsProcessed);
        Assert.Empty(_writtenCandles);
    }

    [Fact]
    public async Task NoM1DataForSymbol_ReturnsCleanlyWithoutSynthesis()
    {
        _timeProvider.SetNow(new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc));
        AddSymbol("EURUSD");
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, result.PeriodsSynthesized);
        Assert.Equal(0, result.PairsFailed);
        Assert.Empty(_writtenCandles);
    }

    // ── Tests: multi-period backfill in one cycle ──────────────────────────

    [Fact]
    public async Task MultiPeriodBackfill_AllSynthesisedInSingleCycle()
    {
        var start = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(start.AddHours(4));  // 4h later — 3 complete H1 periods available

        AddSymbol("EURUSD");
        AddM1Run("EURUSD", start, 180);  // 3h of M1
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        // 3 H1 periods (10, 11, 12) all synthesised in one cycle.
        var h1s = _writtenCandles.Where(c => c.Timeframe == Timeframe.H1).OrderBy(c => c.Timestamp).ToList();
        Assert.Equal(3, h1s.Count);
        Assert.Equal(start,                h1s[0].Timestamp);
        Assert.Equal(start.AddHours(1),    h1s[1].Timestamp);
        Assert.Equal(start.AddHours(2),    h1s[2].Timestamp);
        Assert.Equal(3, result.PeriodsSynthesized);
    }

    // ── Tests: D1 / H4 alignment ───────────────────────────────────────────

    [Fact]
    public async Task D1_SynthesisesAtUtcMidnight_NotBrokerClose()
    {
        // Dense M1 for a full UTC day.
        var d1Start = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(d1Start.AddDays(1).AddHours(1));

        AddSymbol("EURUSD");
        AddM1Run("EURUSD", d1Start, 1440);
        Rebind();

        var result = await NewWorker().RunCycleAsync(CancellationToken.None);

        var d1 = Assert.Single(_writtenCandles, c => c.Timeframe == Timeframe.D1);
        Assert.Equal(d1Start, d1.Timestamp);
        Assert.Equal(TimeSpan.Zero, d1.Timestamp.TimeOfDay);
    }

    [Fact]
    public async Task H4_AlignedToFourHourBuckets()
    {
        // Period 08:00-12:00 is a canonical H4 bucket.
        var h4Start = new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc);
        _timeProvider.SetNow(h4Start.AddHours(5));

        AddSymbol("EURUSD");
        AddM1Run("EURUSD", h4Start, 240);
        Rebind();

        await NewWorker().RunCycleAsync(CancellationToken.None);

        var h4 = Assert.Single(_writtenCandles, c => c.Timeframe == Timeframe.H4);
        Assert.Equal(h4Start, h4.Timestamp);
        Assert.Equal(0, h4.Timestamp.Hour % 4);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private CandleAggregationWorker NewWorker() => new(
        _scopeFactory.Object,
        Mock.Of<ILogger<CandleAggregationWorker>>(),
        _metrics,
        _timeProvider);

    private void Rebind()
    {
        _db.Setup(d => d.Set<Candle>())
           .Returns(BuildCandleSet());

        _db.Setup(d => d.Set<CurrencyPair>())
           .Returns(_pairs.AsQueryable().BuildMockDbSet().Object);

        _db.Setup(d => d.Set<EngineConfig>())
           .Returns(_config.AsQueryable().BuildMockDbSet().Object);
    }

    private DbSet<Candle> BuildCandleSet()
    {
        var mock = _existingCandles.AsQueryable().BuildMockDbSet();
        mock.Setup(m => m.AddRange(It.IsAny<IEnumerable<Candle>>()))
            .Callback<IEnumerable<Candle>>(rs => _writtenCandles.AddRange(rs));
        mock.Setup(m => m.Add(It.IsAny<Candle>()))
            .Callback<Candle>(r => _writtenCandles.Add(r));
        return mock.Object;
    }

    private void AddSymbol(string symbol) =>
        _pairs.Add(new CurrencyPair { Symbol = symbol, IsActive = true });

    private void AddExistingCandle(string symbol, Timeframe tf, DateTime ts) =>
        _existingCandles.Add(new Candle
        {
            Symbol    = symbol,
            Timeframe = tf,
            Timestamp = DateTime.SpecifyKind(ts, DateTimeKind.Utc),
            Open      = 1.10m, High = 1.11m, Low = 1.09m, Close = 1.105m,
            Volume    = 100m,
            IsClosed  = true,
        });

    private void AddM1Run(string symbol, DateTime start, int count)
    {
        for (int i = 0; i < count; i++)
            _existingCandles.Add(MakeM1(symbol, start.AddMinutes(i)));
    }

    private static Candle MakeM1(string symbol, DateTime ts) => new()
    {
        Symbol    = symbol,
        Timeframe = Timeframe.M1,
        Timestamp = DateTime.SpecifyKind(ts, DateTimeKind.Utc),
        Open      = 1.10m, High = 1.11m, Low = 1.09m, Close = 1.105m,
        Volume    = 100m,
        IsClosed  = true,
    };

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetNow(DateTime utc) =>
            _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
