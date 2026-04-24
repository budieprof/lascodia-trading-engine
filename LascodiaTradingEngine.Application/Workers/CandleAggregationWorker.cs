using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Aggregates M1 candles into higher timeframes (H1, H4, D1) server-side.
/// The EA only streams short-timeframe candles; strategies, backtests and ML
/// features still need H1/H4/D1, so this worker fills the gap by synthesising
/// higher-timeframe candles once enough M1 data for a period is available.
///
/// <para>
/// <b>Gap handling:</b> a past period with zero M1 candles is treated as a
/// market-closed or data-outage gap and is skipped; the walker continues to
/// later periods. Without this, one weekend permanently halts aggregation for
/// the pair because the first empty period would break the scan loop.
/// </para>
///
/// <para>
/// <b>Completeness vs coverage:</b> a period is only synthesised when the
/// fraction of distinct minutes present is at or above
/// <c>CandleAggregation:MinimumCoveragePercent</c> (default 85%, clamped
/// <c>[50, 100]</c>). This catches mid-period M1 outages and bootstrap cases
/// where aggregation begins mid-way through the first H4 period, both of which
/// would otherwise produce a silently-wrong OHLC row.
/// </para>
///
/// <para>
/// <b>Authoritative source:</b> EA-delivered candles always win. If a candle
/// already exists for <c>(Symbol, Timeframe, Timestamp)</c> the worker skips
/// that period — it fills gaps, it never overwrites broker-sourced data. The
/// unique index <c>IX_Candle_Symbol_Timeframe_Timestamp</c> is the
/// defence-in-depth backstop: if the EA races us at save time, the worker
/// treats <c>SQLSTATE 23505</c> as benign and retries on the next cycle.
/// </para>
///
/// <para>
/// <b>D1 alignment:</b> worker-synthesised D1 candles are anchored to UTC
/// midnight. Some brokers publish D1 at NY close (~22:00 UTC); downstream
/// consumers must not mix broker-D1 and worker-D1 for the same symbol — pick
/// one source per symbol. The unique key is <c>(Symbol, Timeframe, Timestamp)</c>,
/// so the two series can coexist in the DB but will appear as disjoint rows.
/// </para>
///
/// <para>
/// <b>Scan direction is forward-only:</b> per-pair aggregation starts from the
/// latest existing candle in <see cref="Timeframe.H1"/>/<see cref="Timeframe.H4"/>/
/// <see cref="Timeframe.D1"/> (or the earliest M1 on first bootstrap) and only
/// walks forward. If the EA retroactively delivers a single higher-TF candle
/// in the middle of a long M1 stretch, the worker will <i>not</i> fill the
/// earlier periods before that candle — it resumes after it. Backward gap-fill
/// is intentionally out of scope: it needs a different query shape and
/// conflicts with the "EA is authoritative, worker only fills forward gaps"
/// semantic. Operators who need historical back-fill should either delete the
/// lone EA candle (forcing bootstrap through the earliest-M1 path) or run a
/// one-off back-fill tool.
/// </para>
///
/// <para>
/// <b>Cadence &amp; budgeting:</b> runs every
/// <c>CandleAggregation:IntervalSeconds</c> (default 60, clamped
/// <c>[5, 3600]</c>). Each cycle is bounded by
/// <c>CandleAggregation:MaxCycleDurationSeconds</c> (default 45, clamped
/// <c>[5, 300]</c>) and a hard ceiling of
/// <c>CandleAggregation:MaxPeriodsPerCycle</c> synthesised periods (default
/// 10 000). A long backfill is chunked across multiple cycles rather than
/// blocking the worker in one run.
/// </para>
///
/// <para>
/// <b>Failure isolation:</b> each <c>(symbol, timeframe)</c> pair runs in its
/// own DI scope with its own <c>SaveChangesAsync</c>. A failure for one pair
/// fails only that pair; the remaining pairs in the cycle continue.
/// </para>
///
/// <para>
/// <b>Observability:</b> emits <c>trading.workers.cycle_duration</c> histogram
/// (tagged <c>worker="CandleAggregationWorker"</c>) and
/// <c>trading.workers.errors</c> counter with a <c>reason</c> tag
/// (<c>unhandled</c>, <c>unique_race</c>, or <c>pair_failed</c>). Cycle-summary
/// logs include synthesized / low-coverage / already-exists / failed counts.
/// </para>
/// </summary>
public sealed class CandleAggregationWorker : BackgroundService
{
    // ── Config keys ─────────────────────────────────────────────────────────
    internal const string CK_IntervalSeconds         = "CandleAggregation:IntervalSeconds";
    internal const string CK_MinimumCoveragePercent  = "CandleAggregation:MinimumCoveragePercent";
    internal const string CK_MaxCycleDurationSeconds = "CandleAggregation:MaxCycleDurationSeconds";
    internal const string CK_MaxPeriodsPerCycle      = "CandleAggregation:MaxPeriodsPerCycle";

    // ── Defaults / clamps ───────────────────────────────────────────────────
    private const int DefaultIntervalSeconds =   60;
    private const int MinIntervalSeconds     =    5;
    private const int MaxIntervalSeconds     = 3600;

    private const int DefaultMinimumCoveragePercent = 85;
    private const int MinCoveragePercent            = 50;
    private const int MaxCoveragePercent            = 100;

    private const int DefaultMaxCycleDurationSeconds =  45;
    private const int MinCycleDurationSeconds        =   5;
    private const int MaxCycleDurationSecondsCap     = 300;

    private const int DefaultMaxPeriodsPerCycle =    10_000;
    private const int MinMaxPeriodsPerCycle     =       100;
    private const int MaxMaxPeriodsPerCycle     = 1_000_000;

    // Hard upper bound on inner-loop iterations per pair — guards against a
    // pathological config (coverage floor 0%, no deadline) producing an
    // unbounded walk. Generous: 200k H1 periods = 22 years.
    private const int MaxIterationsPerPair = 200_000;

    private static readonly Timeframe[] TargetTimeframes = { Timeframe.H1, Timeframe.H4, Timeframe.D1 };

    private static readonly IReadOnlyDictionary<Timeframe, int> PeriodMinutes = new Dictionary<Timeframe, int>
    {
        { Timeframe.H1,   60 },
        { Timeframe.H4,  240 },
        { Timeframe.D1, 1440 },
    };

    // EA occasionally stamps an M1 candle one second after the true minute
    // boundary (XX:00:01 instead of XX:00:00). The upper DB bound is widened
    // by this buffer; the post-query TruncateToMinute filter re-homes drifted
    // rows to the correct period. No lower buffer: backward drift is not
    // observed, and TruncateToMinute rounds down so forward-drifted rows
    // from the previous minute never fall below the lower bound anyway.
    private static readonly TimeSpan UpperDriftBuffer = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CandleAggregationWorker> _logger;
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    public CandleAggregationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CandleAggregationWorker> logger,
        TradingMetrics metrics,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _metrics      = metrics;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "CandleAggregationWorker starting (defaultInterval={Interval}s)", DefaultIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalSeconds = DefaultIntervalSeconds;
            var cycleStart = Stopwatch.GetTimestamp();

            try
            {
                var result = await RunCycleAsync(stoppingToken);
                intervalSeconds = result.NextIntervalSeconds;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CandleAggregationWorker: cycle failed");
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "CandleAggregationWorker"),
                    new KeyValuePair<string, object?>("reason", "unhandled"));
            }
            finally
            {
                _metrics.WorkerCycleDurationMs.Record(
                    Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds,
                    new KeyValuePair<string, object?>("worker", "CandleAggregationWorker"));
            }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        _logger.LogInformation("CandleAggregationWorker stopped");
    }

    /// <summary>
    /// Internal for unit-test access. Runs one full aggregation cycle across
    /// all active symbols and target timeframes. Returns a structured result
    /// so tests can assert on the cycle outcome without inspecting logs.
    /// </summary>
    internal async Task<CycleResult> RunCycleAsync(CancellationToken ct)
    {
        CycleConfig config;
        List<string> symbols;
        Dictionary<(string Symbol, Timeframe Tf), DateTime> latestMap;

        // Single scope for cycle-wide reads: config, active symbols, and the
        // pre-computed "latest existing candle per (symbol, tf)" map. Replaces
        // an N+1 lookup at the start of each pair.
        await using (var bootScope = _scopeFactory.CreateAsyncScope())
        {
            var readCtx = bootScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var readDb  = readCtx.GetDbContext();

            config  = await ReadCycleConfigAsync(readDb, ct);
            symbols = await readDb.Set<CurrencyPair>()
                .AsNoTracking()
                .Where(cp => cp.IsActive && !cp.IsDeleted)
                .Select(cp => cp.Symbol)
                .ToListAsync(ct);

            var latestRows = await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => !c.IsDeleted &&
                           (c.Timeframe == Timeframe.H1 ||
                            c.Timeframe == Timeframe.H4 ||
                            c.Timeframe == Timeframe.D1))
                .GroupBy(c => new { c.Symbol, c.Timeframe })
                .Select(g => new
                {
                    g.Key.Symbol,
                    g.Key.Timeframe,
                    LatestTimestamp = g.Max(x => x.Timestamp),
                })
                .ToListAsync(ct);

            latestMap = latestRows.ToDictionary(
                r => (r.Symbol, r.Timeframe),
                r => DateTime.SpecifyKind(r.LatestTimestamp, DateTimeKind.Utc));
        }

        var deadlineTs = Stopwatch.GetTimestamp()
                       + (long)(config.MaxCycleDurationSeconds * Stopwatch.Frequency);

        int symbolsProcessed            = 0;
        int periodsSynthesized          = 0;
        int periodsSkippedLowCoverage   = 0;
        int periodsSkippedAlreadyExists = 0;
        int pairsFailed                 = 0;
        bool budgetExhausted                = false;

        foreach (var symbol in symbols)
        {
            if (ct.IsCancellationRequested) break;
            if (Stopwatch.GetTimestamp() >= deadlineTs || periodsSynthesized >= config.MaxPeriodsPerCycle)
            {
                budgetExhausted = true;
                break;
            }

            foreach (var tf in TargetTimeframes)
            {
                if (ct.IsCancellationRequested) break;
                if (Stopwatch.GetTimestamp() >= deadlineTs || periodsSynthesized >= config.MaxPeriodsPerCycle)
                {
                    budgetExhausted = true;
                    break;
                }

                try
                {
                    var remainingBudget = config.MaxPeriodsPerCycle - periodsSynthesized;
                    var outcome = await ProcessPairAsync(
                        symbol, tf, latestMap, config, remainingBudget, deadlineTs, ct);
                    periodsSynthesized          += outcome.Synthesized;
                    periodsSkippedLowCoverage   += outcome.SkippedLowCoverage;
                    periodsSkippedAlreadyExists += outcome.SkippedAlreadyExists;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "CandleAggregationWorker: failed pair {Symbol}/{Timeframe} — continuing with remaining pairs",
                        symbol, tf);
                    pairsFailed++;
                    _metrics.WorkerErrors.Add(1,
                        new KeyValuePair<string, object?>("worker", "CandleAggregationWorker"),
                        new KeyValuePair<string, object?>("reason", "pair_failed"),
                        new KeyValuePair<string, object?>("symbol", symbol),
                        new KeyValuePair<string, object?>("timeframe", tf.ToString()));
                }
            }

            if (budgetExhausted) break;
            symbolsProcessed++;
        }

        if (periodsSynthesized > 0 || pairsFailed > 0 || budgetExhausted)
        {
            _logger.LogInformation(
                "CandleAggregationWorker: cycle complete — synthesized={Synth} lowCoverage={Cov} alreadyExists={Exists} pairsFailed={Failed} budgetExhausted={BudgetExhausted} symbolsProcessed={Symbols}/{Total}",
                periodsSynthesized, periodsSkippedLowCoverage, periodsSkippedAlreadyExists,
                pairsFailed, budgetExhausted, symbolsProcessed, symbols.Count);
        }

        return new CycleResult(
            NextIntervalSeconds:         config.IntervalSeconds,
            PeriodsSynthesized:          periodsSynthesized,
            PeriodsSkippedLowCoverage:   periodsSkippedLowCoverage,
            PeriodsSkippedAlreadyExists: periodsSkippedAlreadyExists,
            PairsFailed:                 pairsFailed,
            BudgetExhausted:             budgetExhausted,
            SymbolsProcessed:            symbolsProcessed);
    }

    /// <summary>
    /// Processes all pending periods for one <c>(symbol, timeframe)</c> pair
    /// in a dedicated DI scope. Writes are batched and persisted in a single
    /// <c>SaveChangesAsync</c>; a unique-constraint violation is treated as a
    /// benign race against EA-sourced candles.
    /// </summary>
    private async Task<PairResult> ProcessPairAsync(
        string symbol,
        Timeframe targetTf,
        IReadOnlyDictionary<(string Symbol, Timeframe Tf), DateTime> latestMap,
        CycleConfig config,
        int remainingPeriodsBudget,
        long deadlineTs,
        CancellationToken ct)
    {
        var periodMinutes = PeriodMinutes[targetTf];
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        int synthesized = 0;
        int skippedLowCoverage = 0;
        int skippedAlreadyExists = 0;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Establish the starting period for this pair.
        DateTime nextPeriodStart;
        if (latestMap.TryGetValue((symbol, targetTf), out var latest))
        {
            nextPeriodStart = latest.AddMinutes(periodMinutes);
        }
        else
        {
            var earliestM1 = await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Timeframe == Timeframe.M1
                         && c.IsClosed && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .Select(c => (DateTime?)c.Timestamp)
                .FirstOrDefaultAsync(ct);

            if (earliestM1 is null) return new PairResult(0, 0, 0);
            nextPeriodStart = AlignToPeriodStart(
                DateTime.SpecifyKind(earliestM1.Value, DateTimeKind.Utc),
                targetTf);
        }

        // Pre-fetch any already-persisted target-tf timestamps in the scan
        // window into a HashSet — replaces a per-period existence round-trip.
        // Stale data is acceptable: if EA inserts after this read, the unique
        // index on save is the final arbiter.
        var existingInWindow = new HashSet<DateTime>(
            (await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Timeframe == targetTf
                         && !c.IsDeleted && c.Timestamp >= nextPeriodStart)
                .Select(c => c.Timestamp)
                .ToListAsync(ct))
            .Select(t => DateTime.SpecifyKind(t, DateTimeKind.Utc)));

        var pending = new List<Candle>();
        int iterations = 0;

        while (true)
        {
            if (++iterations > MaxIterationsPerPair) break;
            if (ct.IsCancellationRequested) break;
            if (Stopwatch.GetTimestamp() >= deadlineTs) break;
            if (synthesized >= remainingPeriodsBudget) break;

            var periodEnd      = nextPeriodStart.AddMinutes(periodMinutes);
            var lastM1InPeriod = nextPeriodStart.AddMinutes(periodMinutes - 1);

            // Fetch M1 candles in the period (+ upper drift buffer), then
            // re-home by truncated minute.
            var queryUpper = periodEnd.Add(UpperDriftBuffer);
            var m1Raw = await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == symbol
                         && c.Timeframe == Timeframe.M1
                         && c.IsClosed
                         && !c.IsDeleted
                         && c.Timestamp >= nextPeriodStart
                         && c.Timestamp <  queryUpper)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            var m1Candles = m1Raw
                .Select(c => (Candle: c, Minute: TruncateToMinute(c.Timestamp)))
                .Where(t => t.Minute >= nextPeriodStart && t.Minute < periodEnd)
                .ToList();

            if (m1Candles.Count == 0)
            {
                // Nothing in this period.
                //   Future period: wait for the next cycle.
                //   Past gap (weekend / outage / holiday): skip the period and
                //   keep scanning — critical fix vs. the old break-on-empty
                //   behaviour, which permanently halted aggregation after the
                //   first weekend.
                if (periodEnd > now) break;
                nextPeriodStart = periodEnd;
                continue;
            }

            // Mid-period: the current bar is still forming — wait.
            var actualLastMinute = m1Candles[^1].Minute;
            if (periodEnd > now && actualLastMinute < lastM1InPeriod) break;

            // Coverage floor — catches mid-period data outages and the
            // bootstrap case where M1 data starts mid-way into the first H4.
            var distinctMinutes = m1Candles.Select(t => t.Minute).Distinct().Count();
            var coveragePercent = (int)Math.Round(100.0 * distinctMinutes / periodMinutes);
            if (coveragePercent < config.MinimumCoveragePercent)
            {
                _logger.LogDebug(
                    "CandleAggregationWorker: {Symbol}/{Tf} {Start:u} coverage {Coverage}% < floor {Floor}% — skipping",
                    symbol, targetTf, nextPeriodStart, coveragePercent, config.MinimumCoveragePercent);
                skippedLowCoverage++;
                nextPeriodStart = periodEnd;
                continue;
            }

            // Gap-fill only: if a broker-sourced candle already owns this
            // slot, skip. EA data wins.
            if (existingInWindow.Contains(nextPeriodStart))
            {
                skippedAlreadyExists++;
                nextPeriodStart = periodEnd;
                continue;
            }

            pending.Add(new Candle
            {
                Symbol    = symbol,
                Timeframe = targetTf,
                Timestamp = nextPeriodStart,
                Open      = m1Candles[0].Candle.Open,
                High      = m1Candles.Max(t => t.Candle.High),
                Low       = m1Candles.Min(t => t.Candle.Low),
                Close     = m1Candles[^1].Candle.Close,
                Volume    = m1Candles.Sum(t => t.Candle.Volume),
                IsClosed  = true,
            });
            synthesized++;
            nextPeriodStart = periodEnd;
        }

        if (pending.Count > 0)
        {
            writeDb.Set<Candle>().AddRange(pending);
            try
            {
                await writeCtx.SaveChangesAsync(ct);
                // Emit only after the save — a rolled-back batch must not inflate the counter.
                _metrics.CandlesSynthesized.Add(pending.Count,
                    new KeyValuePair<string, object?>("symbol", symbol),
                    new KeyValuePair<string, object?>("timeframe", targetTf.ToString()));
                _logger.LogInformation(
                    "CandleAggregationWorker: saved {Count} {Tf} candle(s) for {Symbol} (from {First:u} to {Last:u})",
                    pending.Count, targetTf, symbol, pending[0].Timestamp, pending[^1].Timestamp);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // EA delivered a matching candle concurrently. Treat as benign:
                // nothing in this batch is considered persisted by the caller,
                // and the next cycle will re-resolve against fresh state.
                _logger.LogInformation(ex,
                    "CandleAggregationWorker: unique-violation on batch save for {Symbol}/{Tf} ({Count} pending) — EA likely delivered a matching candle; retrying next cycle",
                    symbol, targetTf, pending.Count);
                synthesized = 0;
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "CandleAggregationWorker"),
                    new KeyValuePair<string, object?>("reason", "unique_race"),
                    new KeyValuePair<string, object?>("symbol", symbol),
                    new KeyValuePair<string, object?>("timeframe", targetTf.ToString()));
            }
        }

        return new PairResult(synthesized, skippedLowCoverage, skippedAlreadyExists);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task<CycleConfig> ReadCycleConfigAsync(DbContext readDb, CancellationToken ct)
    {
        var interval   = await ReadIntConfigAsync(readDb, CK_IntervalSeconds,         DefaultIntervalSeconds,          ct);
        var coverage   = await ReadIntConfigAsync(readDb, CK_MinimumCoveragePercent,  DefaultMinimumCoveragePercent,   ct);
        var cycleMax   = await ReadIntConfigAsync(readDb, CK_MaxCycleDurationSeconds, DefaultMaxCycleDurationSeconds,  ct);
        var maxPeriods = await ReadIntConfigAsync(readDb, CK_MaxPeriodsPerCycle,      DefaultMaxPeriodsPerCycle,       ct);

        return new CycleConfig(
            IntervalSeconds:         Math.Clamp(interval,   MinIntervalSeconds,        MaxIntervalSeconds),
            MinimumCoveragePercent:  Math.Clamp(coverage,   MinCoveragePercent,        MaxCoveragePercent),
            MaxCycleDurationSeconds: Math.Clamp(cycleMax,   MinCycleDurationSeconds,   MaxCycleDurationSecondsCap),
            MaxPeriodsPerCycle:      Math.Clamp(maxPeriods, MinMaxPeriodsPerCycle,     MaxMaxPeriodsPerCycle));
    }

    private static async Task<int> ReadIntConfigAsync(
        DbContext ctx, string key, int defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key && !c.IsDeleted, ct);
        if (entry?.Value is null || !int.TryParse(entry.Value, out var parsed)) return defaultValue;
        return parsed;
    }

    /// <summary>
    /// True when <paramref name="ex"/> wraps a PostgreSQL unique-constraint
    /// violation (SQLSTATE <c>23505</c>). Detects via <c>SqlState</c> when the
    /// provider exposes it (Npgsql), falling back to message inspection for
    /// wrapped / proxy exceptions and test doubles.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            var sqlState = cur.GetType().GetProperty("SqlState")?.GetValue(cur) as string;
            if (sqlState == "23505") return true;

            var msg = cur.Message ?? string.Empty;
            if (msg.Contains("23505", StringComparison.Ordinal)
                || msg.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("IX_Candle", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static DateTime TruncateToMinute(DateTime ts) =>
        new(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0, DateTimeKind.Utc);

    private static DateTime AlignToPeriodStart(DateTime ts, Timeframe tf) => tf switch
    {
        Timeframe.H1 => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour,            0, 0, DateTimeKind.Utc),
        Timeframe.H4 => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour / 4 * 4,    0, 0, DateTimeKind.Utc),
        Timeframe.D1 => new DateTime(ts.Year, ts.Month, ts.Day, 0,                  0, 0, DateTimeKind.Utc),
        _            => ts,
    };

    // ── test-facing shapes ──────────────────────────────────────────────────

    internal readonly record struct CycleConfig(
        int IntervalSeconds,
        int MinimumCoveragePercent,
        int MaxCycleDurationSeconds,
        int MaxPeriodsPerCycle);

    internal readonly record struct CycleResult(
        int  NextIntervalSeconds,
        int  PeriodsSynthesized,
        int  PeriodsSkippedLowCoverage,
        int  PeriodsSkippedAlreadyExists,
        int  PairsFailed,
        bool BudgetExhausted,
        int  SymbolsProcessed);

    private readonly record struct PairResult(
        int Synthesized,
        int SkippedLowCoverage,
        int SkippedAlreadyExists);
}
