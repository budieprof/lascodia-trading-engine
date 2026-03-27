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
/// The EA only sends M1/M5/M15 candle closures, so this worker synthesises
/// higher-timeframe candles once a complete period of M1 data is available.
/// Runs on a configurable interval (default 60 seconds, EngineConfig key
/// <c>CandleAggregation:IntervalSeconds</c>).
/// </summary>
public class CandleAggregationWorker : BackgroundService
{
    private const int DefaultIntervalSeconds = 60;

    private static readonly Timeframe[] TargetTimeframes = { Timeframe.H1, Timeframe.H4, Timeframe.D1 };

    private static readonly Dictionary<Timeframe, int> PeriodMinutes = new()
    {
        { Timeframe.H1, 60 },
        { Timeframe.H4, 240 },
        { Timeframe.D1, 1440 },
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CandleAggregationWorker> _logger;
    private readonly TradingMetrics _metrics;

    public CandleAggregationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CandleAggregationWorker> logger,
        TradingMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _metrics      = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CandleAggregationWorker starting (defaultInterval={Interval}s)",
            DefaultIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = DefaultIntervalSeconds;

            try
            {
                intervalSeconds = await RunAggregationCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CandleAggregationWorker: error during aggregation cycle");
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "CandleAggregation"),
                    new KeyValuePair<string, object?>("reason", "unhandled"));
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }

        _logger.LogInformation("CandleAggregationWorker stopped");
    }

    /// <summary>
    /// Runs one full aggregation cycle across all active symbols and target timeframes.
    /// Returns the configured interval in seconds for the next delay.
    /// </summary>
    private async Task<int> RunAggregationCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb  = readContext.GetDbContext();
        var writeDb = writeContext.GetDbContext();

        // Read configurable interval
        var intervalSeconds = DefaultIntervalSeconds;
        var config = await readDb.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == "CandleAggregation:IntervalSeconds" && !c.IsDeleted, ct);

        if (config is not null && int.TryParse(config.Value, out var parsed) && parsed > 0)
            intervalSeconds = parsed;

        // Get all active symbols
        var symbols = await readDb.Set<CurrencyPair>()
            .AsNoTracking()
            .Where(cp => cp.IsActive && !cp.IsDeleted)
            .Select(cp => cp.Symbol)
            .ToListAsync(ct);

        var totalSynthesized = 0;

        foreach (var symbol in symbols)
        {
            foreach (var targetTf in TargetTimeframes)
            {
                var count = await AggregateTimeframeAsync(readDb, writeDb, writeContext, symbol, targetTf, ct);
                totalSynthesized += count;
            }
        }

        if (totalSynthesized > 0)
        {
            _logger.LogInformation(
                "CandleAggregationWorker: synthesized {Count} candle(s) across {Symbols} symbol(s)",
                totalSynthesized, symbols.Count);
        }

        return intervalSeconds;
    }

    /// <summary>
    /// Aggregates M1 candles into a single higher-timeframe candle for the given
    /// symbol and target timeframe. Keeps building candles until no more complete
    /// periods are found.
    /// </summary>
    /// <returns>Number of candles synthesized.</returns>
    private async Task<int> AggregateTimeframeAsync(
        DbContext readDb,
        DbContext writeDb,
        IWriteApplicationDbContext writeContext,
        string symbol,
        Timeframe targetTf,
        CancellationToken ct)
    {
        var periodMinutes = PeriodMinutes[targetTf];
        var synthesized = 0;

        // Find the latest existing candle for this symbol/timeframe to know where to start
        var latestCandle = await readDb.Set<Candle>()
            .AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == targetTf && !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .FirstOrDefaultAsync(ct);

        // Calculate the next period start
        DateTime nextPeriodStart;
        if (latestCandle is not null)
        {
            nextPeriodStart = latestCandle.Timestamp.AddMinutes(periodMinutes);
        }
        else
        {
            // No existing candle — find the earliest M1 candle and align to the target timeframe boundary
            var earliestM1 = await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Timeframe == Timeframe.M1 && c.IsClosed && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .Select(c => (DateTime?)c.Timestamp)
                .FirstOrDefaultAsync(ct);

            if (earliestM1 is null)
                return 0;

            nextPeriodStart = AlignToPeriodStart(earliestM1.Value, targetTf, periodMinutes);
        }

        // Keep building candles until we run out of complete periods
        while (true)
        {
            var periodEnd = nextPeriodStart.AddMinutes(periodMinutes);

            // Only build if the period is complete: the last M1 candle in the period
            // (at periodEnd - 1 minute) must exist, or we check that the current time
            // is past the period end AND we have M1 data spanning the period.
            var lastM1InPeriod = nextPeriodStart.AddMinutes(periodMinutes - 1);

            // Get all closed M1 candles in this period.
            // Allow up to 30 seconds of timestamp drift (EA sometimes sends XX:00:01 instead of XX:00:00).
            var driftBuffer = TimeSpan.FromSeconds(30);
            var m1Candles = await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == symbol
                         && c.Timeframe == Timeframe.M1
                         && c.IsClosed
                         && !c.IsDeleted
                         && c.Timestamp >= nextPeriodStart.Subtract(driftBuffer)
                         && c.Timestamp < periodEnd.Add(driftBuffer))
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            // Filter to only candles whose truncated minute falls within the period
            m1Candles = m1Candles
                .Where(c => TruncateToMinute(c.Timestamp) >= nextPeriodStart
                         && TruncateToMinute(c.Timestamp) < periodEnd)
                .ToList();

            if (m1Candles.Count == 0)
                break;

            // Period completeness check: the last M1 candle's truncated minute must be
            // at or after the expected final minute of the period (e.g. XX:59 for H1).
            var actualLastMinute = TruncateToMinute(m1Candles[^1].Timestamp);
            if (actualLastMinute < lastM1InPeriod)
                break;

            // Skip if a candle already exists — EA data is authoritative (direct from broker).
            // The aggregation worker only fills gaps; it never overwrites broker-sourced candles.
            var exists = await readDb.Set<Candle>()
                .AsNoTracking()
                .AnyAsync(
                    c => c.Symbol == symbol
                      && c.Timeframe == targetTf
                      && c.Timestamp == nextPeriodStart
                      && !c.IsDeleted,
                    ct);

            if (!exists)
            {
                var aggregated = new Candle
                {
                    Symbol    = symbol,
                    Timeframe = targetTf,
                    Timestamp = nextPeriodStart,
                    Open      = m1Candles[0].Open,
                    High      = m1Candles.Max(c => c.High),
                    Low       = m1Candles.Min(c => c.Low),
                    Close     = m1Candles[^1].Close,
                    Volume    = m1Candles.Sum(c => c.Volume),
                    IsClosed  = true,
                };

                await writeDb.Set<Candle>().AddAsync(aggregated, ct);
                await writeContext.SaveChangesAsync(ct);
                synthesized++;

                _logger.LogInformation(
                    "CandleAggregationWorker: synthesized {Timeframe} candle for {Symbol} at {Timestamp:u}",
                    targetTf, symbol, nextPeriodStart);
            }

            nextPeriodStart = periodEnd;
        }

        return synthesized;
    }

    /// <summary>
    /// Aligns a timestamp to the start of the enclosing period for the given timeframe.
    /// </summary>
    private static DateTime TruncateToMinute(DateTime ts) =>
        new(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0, DateTimeKind.Utc);

    private static DateTime AlignToPeriodStart(DateTime ts, Timeframe tf, int periodMinutes)
    {
        return tf switch
        {
            Timeframe.H1 => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, DateTimeKind.Utc),
            Timeframe.H4 => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour / 4 * 4, 0, 0, DateTimeKind.Utc),
            Timeframe.D1 => new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc),
            _            => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour,
                                ts.Minute / periodMinutes * periodMinutes, 0, DateTimeKind.Utc),
        };
    }
}
