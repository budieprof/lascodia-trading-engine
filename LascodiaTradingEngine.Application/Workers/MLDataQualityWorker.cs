using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Guards the quality of market-data inputs fed to active ML models.
///
/// <b>Why this matters:</b> Degraded input data silently corrupts live predictions.
/// A gap in candle history causes the feature extractor to use stale values;
/// a price spike inflates volatility features far beyond the training distribution;
/// a stale <see cref="LivePrice"/> record means all Kelly sizing and confidence
/// modifiers are working from an outdated mid price.
///
/// <b>Checks performed per active (Symbol, Timeframe) pair:</b>
/// <list type="number">
///   <item><b>Candle gap</b> — the most recent closed candle was delivered more
///         than <c>GapMultiplier × expected_bar_seconds</c> ago, meaning at least
///         one bar is missing from the feed.</item>
///   <item><b>Price spike</b> — the latest closed candle's close deviates more
///         than <c>SpikeSigmas</c> standard deviations from the rolling 50-bar
///         mean, indicating a data-feed error or extreme outlier.</item>
///   <item><b>Stale live price</b> — <see cref="LivePrice.Timestamp"/> has not
///         been updated within <c>LivePriceStalenessSeconds</c>.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLDataQuality:PollIntervalSeconds</c>        — default 300 (5 min)</item>
///   <item><c>MLDataQuality:GapMultiplier</c>              — bar-gap multiplier, default 2.5</item>
///   <item><c>MLDataQuality:SpikeSigmas</c>                — spike detection threshold, default 4.0</item>
///   <item><c>MLDataQuality:SpikeLookbackBars</c>          — rolling window for spike check, default 50</item>
///   <item><c>MLDataQuality:LivePriceStalenessSeconds</c>  — live price max age, default 300</item>
///   <item><c>MLDataQuality:AlertDestination</c>           — default "market-data"</item>
/// </list>
/// </summary>
public sealed class MLDataQualityWorker : BackgroundService
{
    private const string CK_PollSecs      = "MLDataQuality:PollIntervalSeconds";
    private const string CK_GapMult       = "MLDataQuality:GapMultiplier";
    private const string CK_SpikeSigmas   = "MLDataQuality:SpikeSigmas";
    private const string CK_SpikeBars     = "MLDataQuality:SpikeLookbackBars";
    private const string CK_LiveStale     = "MLDataQuality:LivePriceStalenessSeconds";
    private const string CK_AlertDest     = "MLDataQuality:AlertDestination";

    // Expected bar duration in seconds per Timeframe enum value
    private static readonly Dictionary<Timeframe, int> BarSeconds = new()
    {
        { Timeframe.M1,  60     },
        { Timeframe.M5,  300    },
        { Timeframe.M15, 900    },
        { Timeframe.H1,  3600   },
        { Timeframe.H4,  14400  },
        { Timeframe.D1,  86400  },
    };

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLDataQualityWorker>      _logger;

    public MLDataQualityWorker(
        IServiceScopeFactory          scopeFactory,
        ILogger<MLDataQualityWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLDataQualityWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 300;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 300, stoppingToken);

                await CheckAllPairsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLDataQualityWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLDataQualityWorker stopping.");
    }

    // ── Check core ────────────────────────────────────────────────────────────

    private async Task CheckAllPairsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        double gapMult       = await GetConfigAsync<double>(readCtx, CK_GapMult,    2.5,   ct);
        double spikeSigmas   = await GetConfigAsync<double>(readCtx, CK_SpikeSigmas, 4.0,  ct);
        int    spikeBars     = await GetConfigAsync<int>   (readCtx, CK_SpikeBars,  50,    ct);
        int    liveStale     = await GetConfigAsync<int>   (readCtx, CK_LiveStale,  300,   ct);
        string alertDest     = await GetConfigAsync<string>(readCtx, CK_AlertDest,  "market-data", ct);

        // Distinct (Symbol, Timeframe) pairs from active models
        var pairs = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckPairAsync(
                    pair.Symbol, pair.Timeframe,
                    now, gapMult, spikeSigmas, spikeBars, liveStale, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "DataQuality: check failed for {Symbol}/{Tf} — skipping.",
                    pair.Symbol, pair.Timeframe);
            }
        }
    }

    private async Task CheckPairAsync(
        string                                  symbol,
        Timeframe                               timeframe,
        DateTime                                now,
        double                                  gapMultiplier,
        double                                  spikeSigmas,
        int                                     spikeLookbackBars,
        int                                     livePriceStalenessSeconds,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int barSecs = BarSeconds.TryGetValue(timeframe, out int s) ? s : 3600;

        // ── Check 1: Candle gap ───────────────────────────────────────────────
        var latestCandle = await readCtx.Set<Candle>()
            .Where(c => c.Symbol    == symbol    &&
                        c.Timeframe == timeframe  &&
                        c.IsClosed               &&
                        !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .AsNoTracking()
            .Select(c => new { c.Timestamp })
            .FirstOrDefaultAsync(ct);

        if (latestCandle is not null)
        {
            double secondsSinceLast = (now - latestCandle.Timestamp).TotalSeconds;
            double gapThreshold     = gapMultiplier * barSecs;

            _logger.LogDebug(
                "DataQuality: {Symbol}/{Tf} — lastCandle={Ts} secsSince={S:F0} gapThr={G:F0}",
                symbol, timeframe, latestCandle.Timestamp, secondsSinceLast, gapThreshold);

            if (secondsSinceLast > gapThreshold)
            {
                _logger.LogWarning(
                    "DataQuality: {Symbol}/{Tf} — CANDLE GAP: {S:F0}s since last bar (threshold {G:F0}s).",
                    symbol, timeframe, secondsSinceLast, gapThreshold);

                await TryAddAlertAsync(writeCtx, readCtx, symbol, alertDest,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        reason             = "data_quality_gap",
                        severity           = "warning",
                        symbol,
                        timeframe          = timeframe.ToString(),
                        secondsSinceLastBar = secondsSinceLast,
                        gapThresholdSeconds = gapThreshold,
                        lastCandleTimestamp = latestCandle.Timestamp,
                    }), ct);
            }
        }

        // ── Check 2: Price spike ──────────────────────────────────────────────
        var recentCandles = await readCtx.Set<Candle>()
            .Where(c => c.Symbol    == symbol    &&
                        c.Timeframe == timeframe  &&
                        c.IsClosed               &&
                        !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .Take(spikeLookbackBars + 1)        // +1 so the latest is tested against the prior N
            .AsNoTracking()
            .Select(c => new { c.Timestamp, c.Close })
            .ToListAsync(ct);

        if (recentCandles.Count >= 3)
        {
            // The most recent bar is the candidate; the rest form the baseline
            var baseline = recentCandles.Skip(1).Select(c => (double)c.Close).ToList();
            double mean  = baseline.Average();
            double std   = Math.Sqrt(baseline.Average(v => (v - mean) * (v - mean)));

            if (std > 0)
            {
                double latestClose = (double)recentCandles[0].Close;
                double zScore      = Math.Abs(latestClose - mean) / std;

                _logger.LogDebug(
                    "DataQuality: {Symbol}/{Tf} — close={C} mean={M:F5} std={Sd:F5} z={Z:F2}",
                    symbol, timeframe, latestClose, mean, std, zScore);

                if (zScore > spikeSigmas)
                {
                    _logger.LogWarning(
                        "DataQuality: {Symbol}/{Tf} — PRICE SPIKE: z={Z:F2} (threshold {T:F1}). close={C}",
                        symbol, timeframe, zScore, spikeSigmas, latestClose);

                    await TryAddAlertAsync(writeCtx, readCtx, symbol, alertDest,
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            reason         = "data_quality_spike",
                            severity       = "warning",
                            symbol,
                            timeframe      = timeframe.ToString(),
                            latestClose,
                            rollingMean    = mean,
                            rollingStdDev  = std,
                            zScore,
                            spikeThreshold = spikeSigmas,
                            candleTimestamp = recentCandles[0].Timestamp,
                        }), ct);
                }
            }
        }

        // ── Check 3: Stale live price ─────────────────────────────────────────
        var livePrice = await readCtx.Set<LivePrice>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Symbol == symbol, ct);

        if (livePrice is not null)
        {
            double livePriceAge = (now - livePrice.Timestamp).TotalSeconds;

            _logger.LogDebug(
                "DataQuality: {Symbol} — livePrice.Timestamp={Ts} ageSecs={A:F0}",
                symbol, livePrice.Timestamp, livePriceAge);

            if (livePriceAge > livePriceStalenessSeconds)
            {
                _logger.LogWarning(
                    "DataQuality: {Symbol} — STALE LIVE PRICE: {A:F0}s old (threshold {T}s).",
                    symbol, livePriceAge, livePriceStalenessSeconds);

                await TryAddAlertAsync(writeCtx, readCtx, symbol, alertDest,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        reason                 = "live_price_stale",
                        severity               = "warning",
                        symbol,
                        livePriceTimestamp     = livePrice.Timestamp,
                        ageSeconds             = livePriceAge,
                        stalenessThresholdSecs = livePriceStalenessSeconds,
                    }), ct);
            }
        }
    }

    // ── Alert helper ──────────────────────────────────────────────────────────

    private static async Task TryAddAlertAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        string                                  symbol,
        string                                  alertDest,
        string                                  conditionJson,
        CancellationToken                       ct)
    {
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                    &&
                           a.AlertType == AlertType.DataQualityIssue &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.DataQualityIssue,
            Symbol        = symbol,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = conditionJson,
            IsActive      = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
