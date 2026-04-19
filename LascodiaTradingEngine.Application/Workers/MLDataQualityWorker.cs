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

    /// <summary>
    /// Initializes the worker.
    /// </summary>
    /// <param name="scopeFactory">Per-iteration DI scope factory.</param>
    /// <param name="logger">Structured logger.</param>
    public MLDataQualityWorker(
        IServiceScopeFactory          scopeFactory,
        ILogger<MLDataQualityWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Background service main loop. Polls every 5 minutes by default — the most
    /// frequent among the ML workers because data quality issues (candle gaps, price
    /// spikes, stale live prices) can appear suddenly and silently corrupt live
    /// predictions within a single bar period if left undetected.
    /// </summary>
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

    /// <summary>
    /// Resolves the distinct (Symbol, Timeframe) pairs covered by active models,
    /// then runs all three data quality checks for each pair. A single <c>now</c>
    /// timestamp is computed once and passed through to avoid clock drift across
    /// the per-pair checks within one iteration.
    /// </summary>
    private async Task CheckAllPairsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        double gapMult     = await GetConfigAsync<double>(readCtx, CK_GapMult,     2.5,          ct);
        double spikeSigmas = await GetConfigAsync<double>(readCtx, CK_SpikeSigmas, 4.0,          ct);
        int    spikeBars   = await GetConfigAsync<int>   (readCtx, CK_SpikeBars,   50,           ct);
        int    liveStale   = await GetConfigAsync<int>   (readCtx, CK_LiveStale,   300,          ct);
        string alertDest   = await GetConfigAsync<string>(readCtx, CK_AlertDest,   "market-data", ct);

        // Distinct (Symbol, Timeframe) pairs — each pair maps to a different candle feed
        // and live price stream, so data quality is checked at the pair level.
        var pairs = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        // Capture a single "now" for the iteration so all gap/staleness checks
        // use a consistent reference time rather than accumulating micro-drift.
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

    /// <summary>
    /// Executes all three data quality checks for a single (symbol, timeframe) pair
    /// and raises <see cref="AlertType.DataQualityIssue"/> alerts when issues are found.
    /// Each check is independent — all three run regardless of whether an earlier check
    /// already detected a problem.
    /// </summary>
    /// <param name="symbol">Currency pair symbol (e.g. "EURUSD").</param>
    /// <param name="timeframe">Model timeframe (determines expected bar duration).</param>
    /// <param name="now">Consistent UTC reference time for the iteration.</param>
    /// <param name="gapMultiplier">
    /// Multiplier applied to the expected bar duration to derive the gap threshold.
    /// E.g. 2.5 on H1 = alert if no candle for 2.5 hours.
    /// </param>
    /// <param name="spikeSigmas">Z-score threshold for the price spike check.</param>
    /// <param name="spikeLookbackBars">
    /// Number of prior bars used to compute the rolling mean and std for spike detection.
    /// </param>
    /// <param name="livePriceStalenessSeconds">Maximum age of a live price record in seconds.</param>
    /// <param name="alertDest">Webhook destination for data quality alerts.</param>
    /// <param name="readCtx">Read-only EF DbContext.</param>
    /// <param name="writeCtx">Write EF DbContext.</param>
    /// <param name="ct">Cancellation token.</param>
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
        // Determine expected bar duration in seconds for this timeframe.
        // Unknown timeframes fall back to H1 (3600 seconds).
        int barSecs = BarSeconds.TryGetValue(timeframe, out int s) ? s : 3600;

        // ── Check 1: Candle gap ───────────────────────────────────────────────
        // Detects missing bars in the candle feed. The gap threshold is a multiple
        // of the expected bar duration:
        //   gapThreshold = gapMultiplier × barSecs
        // E.g. gapMultiplier=2.5, barSecs=3600 → alert if no H1 candle for 9000s (2.5h).
        // This tolerates minor delays (DST transitions, brief feed interruptions) while
        // catching genuine data outages.
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
                        reason              = "data_quality_gap",
                        severity            = "warning",
                        symbol,
                        timeframe           = timeframe.ToString(),
                        secondsSinceLastBar = secondsSinceLast,
                        gapThresholdSeconds = gapThreshold,
                        lastCandleTimestamp = latestCandle.Timestamp,
                    }), ct);
            }
        }

        // ── Check 2: Price spike ──────────────────────────────────────────────
        // Detects data-feed errors or extreme outliers in the latest candle close price.
        // Algorithm:
        //   1. Load the spikeLookbackBars + 1 most recent closed candles.
        //   2. The most recent candle (index 0) is the candidate; the prior N form the baseline.
        //   3. Compute rolling mean and std over the baseline closes.
        //   4. Z-score = |latestClose − mean| / std
        //   5. Alert if Z-score > spikeSigmas (default 4.0 — a 4-sigma event).
        //
        // A 4-sigma threshold ensures common volatility events don't trigger false positives
        // while catching genuine data errors (e.g. a broker feed returning a price 10× off).
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
            // Baseline = all bars except the most recent (index 0)
            var baseline = recentCandles.Skip(1).Select(c => (double)c.Close).ToList();
            double mean  = baseline.Average();
            // Population standard deviation over the baseline window
            double std   = Math.Sqrt(baseline.Average(v => (v - mean) * (v - mean)));

            if (std > 0)
            {
                double latestClose = (double)recentCandles[0].Close;
                // Z-score measures how many standard deviations the latest close is
                // from the rolling mean — high values indicate anomalous price movement.
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
                            reason          = "data_quality_spike",
                            severity        = "warning",
                            symbol,
                            timeframe       = timeframe.ToString(),
                            latestClose,
                            rollingMean     = mean,
                            rollingStdDev   = std,
                            zScore,
                            spikeThreshold  = spikeSigmas,
                            candleTimestamp = recentCandles[0].Timestamp,
                        }), ct);
                }
            }
        }

        // ── Check 3: Stale live price ─────────────────────────────────────────
        // Detects when the live price stream has stopped updating. The live price is
        // used by MLSignalScorer for Kelly sizing and spread computation. A stale price
        // means the scorer is working from outdated market data, potentially sizing
        // positions incorrectly or failing spread filters.
        //
        // The staleness threshold (default 300s = 5 min) is intentionally equal to the
        // worker poll interval so that a stale live price is caught on the next cycle.
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
                // Logged at Debug because operator-facing visibility already comes from
                // PriceCacheFreshnessCheck (health endpoint) and InDatabaseLivePriceCache's
                // simultaneous-staleness CRITICAL alert. Per-symbol duplicate Warnings
                // here were producing ~8/min of redundant log output during EA outage.
                // The DB alert row is still written so downstream alerting is unaffected.
                _logger.LogDebug(
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

    /// <summary>
    /// Inserts a <see cref="AlertType.DataQualityIssue"/> alert for the given symbol
    /// if no active alert of that type already exists (deduplication guard). Uses a
    /// separate read context for the existence check and the write context for the insert
    /// so the check and write operate on consistent snapshots within the same DI scope.
    /// </summary>
    /// <param name="writeCtx">Write EF DbContext for inserting the alert.</param>
    /// <param name="readCtx">Read-only EF DbContext for the deduplication check.</param>
    /// <param name="symbol">Currency pair symbol associated with the data quality issue.</param>
    /// <param name="alertDest">Webhook destination label.</param>
    /// <param name="conditionJson">Serialized JSON containing the issue details.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task TryAddAlertAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        string                                  symbol,
        string                                  alertDest,
        string                                  conditionJson,
        CancellationToken                       ct)
    {
        // Only insert a new alert if there is no existing active DataQualityIssue alert
        // for this symbol — prevents accumulating duplicate alerts across polling cycles.
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                    &&
                           a.AlertType == AlertType.DataQualityIssue &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.DataQualityIssue,
            Symbol        = symbol,
            ConditionJson = conditionJson,
            IsActive      = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/>. Returns
    /// <paramref name="defaultValue"/> if the key is absent or the stored value
    /// cannot be converted to <typeparamref name="T"/>.
    /// </summary>
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
