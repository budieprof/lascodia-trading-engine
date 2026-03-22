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
    /// <paramref name="windowSize"/> prediction log records.
    /// </summary>
    /// <param name="modelId">Database ID of the model to check.</param>
    /// <param name="symbol">Currency pair symbol (e.g. "EURUSD").</param>
    /// <param name="timeframe">Timeframe of the model.</param>
    /// <param name="windowSize">
    /// Number of most-recent predictions to examine. The check is only performed
    /// when at least this many records are available — requiring the full window
    /// prevents spurious alerts on newly deployed models.
    /// </param>
    /// <param name="maxFraction">
    /// Maximum fraction of predictions allowed in the dominant direction before
    /// an alert is raised. E.g. 0.85 means &gt;85% same direction triggers the check.
    /// </param>
    /// <param name="alertDest">Webhook destination for the alert.</param>
    /// <param name="readCtx">Read-only EF DbContext.</param>
    /// <param name="writeCtx">Write EF DbContext.</param>
    /// <param name="ct">Cancellation token.</param>
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
        // Load the N most recent predictions (resolved or unresolved — we only care about direction).
        // Using OrderByDescending + Take is more efficient than a date-range filter when the
        // goal is simply "the latest N records".
        var recent = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId == modelId && !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)
            .Take(windowSize)
            .AsNoTracking()
            .Select(l => l.PredictedDirection)
            .ToListAsync(ct);

        // Require a full window before evaluating — an incomplete window can produce
        // artificially high dominance fractions on models with few predictions.
        if (recent.Count < windowSize)
        {
            _logger.LogDebug(
                "DirectionStreak: model {Id} ({Symbol}/{Tf}) — only {N}/{Window} predictions available, skipping.",
                modelId, symbol, timeframe, recent.Count, windowSize);
            return;
        }

        // ── Streak computation ────────────────────────────────────────────────
        // Count each direction, identify the dominant side, and compute its fraction.
        // Tie-breaking favours Buy (arbitrary; the alert fires regardless of which side wins).
        int    buyCount       = recent.Count(d => d == TradeDirection.Buy);
        int    sellCount      = recent.Count - buyCount;
        int    dominantCount  = Math.Max(buyCount, sellCount);
        double dominantFrac   = (double)dominantCount / recent.Count;
        var    dominantDir    = buyCount >= sellCount ? TradeDirection.Buy : TradeDirection.Sell;

        _logger.LogDebug(
            "DirectionStreak: model {Id} ({Symbol}/{Tf}) — last {N}: Buy={B} Sell={S} dominant={Dir} ({Frac:P1})",
            modelId, symbol, timeframe, recent.Count, buyCount, sellCount, dominantDir, dominantFrac);

        // If dominant fraction is within the acceptable range, no action needed.
        if (dominantFrac <= maxFraction) return;

        _logger.LogWarning(
            "DirectionStreak: model {Id} ({Symbol}/{Tf}) — {Frac:P1} of last {N} predictions are {Dir} " +
            "(threshold {Thr:P0}). Possible directional bias.",
            modelId, symbol, timeframe, dominantFrac, recent.Count, dominantDir, maxFraction);

        // ── Deduplicated alert ────────────────────────────────────────────────
        // Only fire one active MLModelDegraded alert per symbol at a time to prevent
        // alert flooding. Operators resolve the alert after investigating.
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                     &&
                           a.AlertType == AlertType.MLModelDegraded  &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = symbol,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason            = "direction_streak",
                severity          = "warning",
                symbol,
                timeframe         = timeframe.ToString(),
                modelId,
                dominantDirection = dominantDir.ToString(),
                dominantFraction  = dominantFrac,
                windowSize,
            }),
            IsActive = true,
        });

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
