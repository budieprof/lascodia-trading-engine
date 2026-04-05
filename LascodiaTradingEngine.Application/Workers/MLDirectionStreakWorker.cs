using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects models that are stuck predicting the same direction for an extended period,
/// which is a signal that the model has degenerated into a directional bias rather than
/// responding dynamically to market conditions.
///
/// <para>
/// <b>Problem:</b> A model can achieve nominal accuracy by always predicting Buy in a
/// sustained uptrend. When the trend reverses the model continues to predict Buy, causing
/// systematic losses. Simple rolling accuracy metrics won't catch this early because
/// accuracy only degrades gradually after the trend turns.
/// </para>
///
/// <para>
/// <b>Distinction from MLPredictionSkewWorker:</b>
/// <see cref="MLPredictionSkewWorker"/> evaluates the BUY/SELL ratio over a calendar
/// window (e.g. 14 days), catching slow-onset skew caused by class-imbalanced training
/// data. This worker evaluates the <em>most recent N predictions</em> regardless of
/// when they occurred, catching <em>sudden onset</em> directional lock — e.g., a model
/// that was balanced yesterday but has issued only Buy signals for the last 30 bars.
/// </para>
///
/// <para>
/// <b>Streak detection algorithm:</b>
/// <list type="number">
///   <item>For each active model, load the last <c>WindowSize</c> prediction log records
///         ordered by <c>PredictedAt</c> descending (both resolved and unresolved).</item>
///   <item>Count Buy vs Sell in that window; identify the dominant direction.</item>
///   <item>Compute dominantFraction = dominantCount / windowSize.</item>
///   <item>If dominantFraction &gt; <c>MaxSameDirectionFraction</c>, the model is
///         considered "stuck" and an alert is fired.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Polling interval:</b> 3600 seconds (1 hour) by default, configurable via
/// <c>MLStreak:PollIntervalSeconds</c>. The hourly cadence is appropriate for detecting
/// streaks that develop over hours-to-days.
/// </para>
///
/// <para>
/// <b>ML lifecycle contribution:</b> Acts as an early-warning system for directional
/// lock that precedes full model collapse. Unlike the skew worker, it does not
/// automatically queue a retrain — the operator alert is considered sufficient since
/// the streak may reflect genuine trending market conditions rather than model failure.
/// </para>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLStreak:PollIntervalSeconds</c>        — default 3600 (1 h)</item>
///   <item><c>MLStreak:WindowSize</c>                 — number of recent predictions, default 30</item>
///   <item><c>MLStreak:MaxSameDirectionFraction</c>   — max tolerated fraction, default 0.85</item>
///   <item><c>MLStreak:AlertDestination</c>           — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLDirectionStreakWorker : BackgroundService
{
    private const string CK_PollSecs   = "MLStreak:PollIntervalSeconds";
    private const string CK_Window     = "MLStreak:WindowSize";
    private const string CK_MaxFrac    = "MLStreak:MaxSameDirectionFraction";
    private const string CK_AlertDest  = "MLStreak:AlertDestination";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLDirectionStreakWorker>  _logger;

    /// <summary>
    /// Initializes the worker.
    /// </summary>
    /// <param name="scopeFactory">Per-iteration DI scope factory for EF DbContexts.</param>
    /// <param name="logger">Structured logger.</param>
    public MLDirectionStreakWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLDirectionStreakWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Background service main loop. Each iteration creates a fresh DI scope,
    /// reads the hot-reloadable poll interval, and invokes
    /// <see cref="CheckStreaksAsync"/> to evaluate all active models.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLDirectionStreakWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await CheckStreaksAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLDirectionStreakWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLDirectionStreakWorker stopping.");
    }

    // ── Streak detection core ─────────────────────────────────────────────────

    /// <summary>
    /// Reads configuration and iterates all active models, running streak detection
    /// for each. Only the minimal projection (Id, Symbol, Timeframe) is fetched
    /// to keep the query payload small. Per-model errors are isolated.
    /// </summary>
    private async Task CheckStreaksAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowSize   = await GetConfigAsync<int>   (readCtx, CK_Window,    30,      ct);
        double maxFraction  = await GetConfigAsync<double>(readCtx, CK_MaxFrac,   0.85,    ct);
        string alertDest    = await GetConfigAsync<string>(readCtx, CK_AlertDest, "ml-ops", ct);

        // Only select the columns needed for streak analysis — no need to load the
        // full model entity graph.
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        if (activeModels.Count == 0) return;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckModelStreakAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    windowSize, maxFraction, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "DirectionStreak: check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Evaluates the direction streak for a single ML model over the most recent
    /// <paramref name="windowSize"/> prediction log records. Applies three complementary
    /// tests: (1) dominant-fraction threshold, (2) Wald-Wolfowitz runs test for randomness,
    /// (3) Shannon entropy threshold. Severe streaks queue automatic retraining with
    /// class-rebalancing metadata.
    /// </summary>
    private async Task CheckModelStreakAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        int                                     windowSize,
        double                                  maxFraction,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        var recent = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId == modelId && !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)
            .Take(windowSize)
            .AsNoTracking()
            .Select(l => l.PredictedDirection)
            .ToListAsync(ct);

        if (recent.Count < windowSize)
        {
            _logger.LogDebug(
                "DirectionStreak: model {Id} ({Symbol}/{Tf}) — only {N}/{Window} predictions available, skipping.",
                modelId, symbol, timeframe, recent.Count, windowSize);
            return;
        }

        // ── Basic direction counts ───────────────────────────────────────────
        int    n             = recent.Count;
        int    buyCount      = recent.Count(d => d == TradeDirection.Buy);
        int    sellCount     = n - buyCount;
        int    dominantCount = Math.Max(buyCount, sellCount);
        double dominantFrac  = (double)dominantCount / n;
        var    dominantDir   = buyCount >= sellCount ? TradeDirection.Buy : TradeDirection.Sell;

        // ── Shannon entropy ──────────────────────────────────────────────────
        // Binary entropy: H = -p*log2(p) - (1-p)*log2(1-p). Max = 1.0 (balanced),
        // Min = 0.0 (all same direction). Low entropy indicates directional lock.
        double pBuy    = (double)buyCount / n;
        double entropy = 0.0;
        if (pBuy > 0 && pBuy < 1)
            entropy = -(pBuy * Math.Log2(pBuy) + (1.0 - pBuy) * Math.Log2(1.0 - pBuy));

        // ── Wald-Wolfowitz runs test for randomness ──────────────────────────
        // A "run" is a maximal consecutive sequence of the same direction.
        // Too few runs indicates directional lock; too many indicates alternating bias.
        int runs = 1;
        for (int i = 1; i < n; i++)
        {
            if (recent[i] != recent[i - 1])
                runs++;
        }

        // Expected runs and variance under the null hypothesis of random order:
        // E(R) = 1 + 2*n1*n2/(n1+n2)
        // Var(R) = 2*n1*n2*(2*n1*n2 - n1 - n2) / ((n1+n2)^2 * (n1+n2-1))
        double n1 = buyCount;
        double n2 = sellCount;
        double expectedRuns = 1.0 + (2.0 * n1 * n2) / (n1 + n2);
        double varRuns = (n1 + n2) > 1
            ? (2.0 * n1 * n2 * (2.0 * n1 * n2 - n1 - n2)) / ((n1 + n2) * (n1 + n2) * ((n1 + n2) - 1.0))
            : 0;
        double runsZScore = varRuns > 0 ? (runs - expectedRuns) / Math.Sqrt(varRuns) : 0;

        // ── Longest consecutive run ──────────────────────────────────────────
        int longestRun = 1, currentRun = 1;
        for (int i = 1; i < n; i++)
        {
            if (recent[i] == recent[i - 1]) currentRun++;
            else                             currentRun = 1;
            longestRun = Math.Max(longestRun, currentRun);
        }

        _logger.LogDebug(
            "DirectionStreak: model {Id} ({Symbol}/{Tf}) — Buy={B} Sell={S} dominant={Dir} ({Frac:P1}) " +
            "entropy={H:F3} runs={R} runsZ={Z:F2} longestRun={LR}",
            modelId, symbol, timeframe, buyCount, sellCount, dominantDir, dominantFrac,
            entropy, runs, runsZScore, longestRun);

        // ── Determine if any test indicates directional lock ─────────────────
        // 1. Dominant fraction exceeds threshold (original check)
        bool fracFailed   = dominantFrac > maxFraction;
        // 2. Entropy below 0.5 indicates severe imbalance (50% of max possible)
        bool entropyFailed = entropy < 0.5;
        // 3. Runs Z-score < -2.0 indicates significantly fewer runs than expected (p < 0.05)
        bool runsFailed    = runsZScore < -2.0;
        // 4. Longest consecutive run exceeds 60% of window (e.g., 18/30 same direction in a row)
        bool longestRunFailed = longestRun > (int)(windowSize * 0.6);

        // Require at least 2 of 4 tests to fail to avoid false positives from any single test
        int failCount = (fracFailed ? 1 : 0) + (entropyFailed ? 1 : 0)
                      + (runsFailed ? 1 : 0) + (longestRunFailed ? 1 : 0);

        if (failCount < 2) return;

        // ── Determine severity: 3+ tests = severe (retrain), 2 = warning (alert only) ──
        bool isSevere = failCount >= 3;

        _logger.LogWarning(
            "DirectionStreak: model {Id} ({Symbol}/{Tf}) — {FailCount}/4 tests failed. " +
            "fracFailed={FF} entropyFailed={EF} runsFailed={RF} longestRunFailed={LRF}. Severity={Sev}",
            modelId, symbol, timeframe, failCount, fracFailed, entropyFailed, runsFailed, longestRunFailed,
            isSevere ? "SEVERE" : "WARNING");

        // ── Deduplicated alert ───────────────────────────────────────────────
        string dedupKey = $"direction-streak:{symbol}:{timeframe}:{modelId}";
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.DeduplicationKey == dedupKey
                        && a.IsActive && !a.IsDeleted, ct);

        if (!alertExists)
        {
            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType        = AlertType.MLModelDegraded,
                Symbol           = symbol,
                Channel          = isSevere ? AlertChannel.Telegram : AlertChannel.Webhook,
                Destination      = alertDest,
                Severity         = isSevere ? AlertSeverity.High : AlertSeverity.Medium,
                DeduplicationKey = dedupKey,
                CooldownSeconds  = 3600,
                ConditionJson    = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reason            = "direction_streak",
                    severity          = isSevere ? "severe" : "warning",
                    symbol,
                    timeframe         = timeframe.ToString(),
                    modelId,
                    dominantDirection = dominantDir.ToString(),
                    dominantFraction  = Math.Round(dominantFrac, 4),
                    entropy           = Math.Round(entropy, 4),
                    runsZScore        = Math.Round(runsZScore, 4),
                    runs,
                    expectedRuns      = Math.Round(expectedRuns, 2),
                    longestConsecutiveRun = longestRun,
                    windowSize,
                    testsFailedCount  = failCount,
                    detectedAt        = DateTime.UtcNow.ToString("O")
                }),
                IsActive = true,
            });
        }

        // ── Auto-queue retrain for severe streaks ────────────────────────────
        if (isSevere)
        {
            bool retrainExists = await readCtx.Set<MLTrainingRun>()
                .AnyAsync(r => r.Symbol == symbol
                            && r.Timeframe == timeframe
                            && r.Status == RunStatus.Queued
                            && !r.IsDeleted, ct);

            if (!retrainExists)
            {
                writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
                {
                    Symbol               = symbol,
                    Timeframe            = timeframe,
                    Status               = RunStatus.Queued,
                    ErrorMessage         = $"[DirectionStreak] Auto-retrain: {failCount}/4 tests failed " +
                                           $"(dominant={dominantDir} {dominantFrac:P0}, entropy={entropy:F3}, " +
                                           $"runsZ={runsZScore:F2}, longestRun={longestRun}). " +
                                           "Recommend class-rebalancing and dropout regularisation.",
                    HyperparamConfigJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        triggeredBy           = "MLDirectionStreakWorker",
                        classRebalance        = true,
                        dominantDirection     = dominantDir.ToString(),
                        dominantFraction      = dominantFrac,
                    }),
                });

                _logger.LogWarning(
                    "DirectionStreak: queued retrain for {Symbol}/{Tf} model {Id} due to severe directional lock",
                    symbol, timeframe, modelId);
            }
        }

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/>. Returns
    /// <paramref name="defaultValue"/> if the key is absent or unparseable.
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
