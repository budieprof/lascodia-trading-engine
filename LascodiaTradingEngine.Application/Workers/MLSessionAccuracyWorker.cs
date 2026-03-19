using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes per-trading-session direction accuracy for each active ML model and persists
/// the results as <see cref="MLModelSessionAccuracy"/> rows.
///
/// <b>Motivation:</b> Forex markets behave structurally differently across sessions.
/// London is typically trending, Asian sessions are often ranging, and the London/New York
/// overlap sees the highest volatility. A model with 57% aggregate accuracy may perform
/// at 63% during London but only 51% during Asian hours. Without session-level granularity,
/// this weakness is invisible until significant live P&amp;L damage has occurred.
/// These rows allow <c>MLSignalScorer</c> to apply per-session confidence adjustments
/// (or suppress signals entirely in chronically underperforming sessions).
///
/// <b>Session classification (UTC):</b>
/// <list type="bullet">
///   <item><b>Asian</b>       — 00:00–06:59 UTC</item>
///   <item><b>London</b>      — 07:00–12:59 UTC</item>
///   <item><b>LondonNYOverlap</b> — 13:00–16:59 UTC</item>
///   <item><b>NewYork</b>     — 17:00–23:59 UTC</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLSessionAccuracy:PollIntervalSeconds</c> — default 3600 (1 h)</item>
///   <item><c>MLSessionAccuracy:WindowDays</c>          — look-back window, default 30</item>
///   <item><c>MLSessionAccuracy:MinPredictions</c>      — minimum per-session sample, default 20</item>
/// </list>
/// </summary>
public sealed class MLSessionAccuracyWorker : BackgroundService
{
    private const string CK_PollSecs       = "MLSessionAccuracy:PollIntervalSeconds";
    private const string CK_WindowDays     = "MLSessionAccuracy:WindowDays";
    private const string CK_MinPredictions = "MLSessionAccuracy:MinPredictions";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLSessionAccuracyWorker>  _logger;

    public MLSessionAccuracyWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLSessionAccuracyWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSessionAccuracyWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await ComputeSessionAccuracyAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLSessionAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSessionAccuracyWorker stopping.");
    }

    // ── Computation core ──────────────────────────────────────────────────────

    private async Task ComputeSessionAccuracyAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int windowDays     = await GetConfigAsync<int>(readCtx, CK_WindowDays,     30, ct);
        int minPredictions = await GetConfigAsync<int>(readCtx, CK_MinPredictions, 20, ct);

        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        // Process each active model
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ComputeForModelAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    windowStart, minPredictions,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SessionAccuracy: computation failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task ComputeForModelAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        DateTime                                windowStart,
        int                                     minPredictions,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load resolved prediction logs in the window
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId       == modelId       &&
                        l.DirectionCorrect != null         &&
                        l.PredictedAt      >= windowStart  &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => new { l.PredictedAt, l.DirectionCorrect })
            .ToListAsync(ct);

        if (logs.Count == 0) return;

        // Group by session
        var bySession = logs
            .GroupBy(l => ClassifySession(l.PredictedAt))
            .ToList();

        var now = DateTime.UtcNow;
        int upserted = 0;

        foreach (var group in bySession)
        {
            var session = group.Key;
            int total   = group.Count();

            if (total < minPredictions) continue;

            int    correct  = group.Count(l => l.DirectionCorrect == true);
            double accuracy = (double)correct / total;

            // Try update first; insert if no row exists
            int rows = await writeCtx.Set<MLModelSessionAccuracy>()
                .Where(r => r.MLModelId == modelId && r.Session == session)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.TotalPredictions,   total)
                    .SetProperty(r => r.CorrectPredictions, correct)
                    .SetProperty(r => r.Accuracy,           accuracy)
                    .SetProperty(r => r.WindowStart,        windowStart)
                    .SetProperty(r => r.ComputedAt,         now),
                    ct);

            if (rows == 0)
            {
                writeCtx.Set<MLModelSessionAccuracy>().Add(new MLModelSessionAccuracy
                {
                    MLModelId          = modelId,
                    Symbol             = symbol,
                    Timeframe          = timeframe,
                    Session            = session,
                    TotalPredictions   = total,
                    CorrectPredictions = correct,
                    Accuracy           = accuracy,
                    WindowStart        = windowStart,
                    ComputedAt         = now,
                });
                await writeCtx.SaveChangesAsync(ct);
            }

            upserted++;

            _logger.LogDebug(
                "SessionAccuracy: model {Id} {Symbol}/{Tf} {Session} — acc={Acc:P1} n={N}",
                modelId, symbol, timeframe, session, accuracy, total);
        }

        if (upserted > 0)
            _logger.LogInformation(
                "SessionAccuracy: model {Id} ({Symbol}/{Tf}) — computed {N} session bucket(s).",
                modelId, symbol, timeframe, upserted);
    }

    // ── Session classification ────────────────────────────────────────────────

    /// <summary>
    /// Maps a UTC timestamp to the dominant <see cref="TradingSession"/> using
    /// standard forex session open/close hours:
    /// <list type="bullet">
    ///   <item>Asian       — 00:00–06:59 UTC (Tokyo/Sydney dominant)</item>
    ///   <item>London      — 07:00–12:59 UTC (European open through US pre-market)</item>
    ///   <item>LondonNYOverlap — 13:00–16:59 UTC (highest liquidity window)</item>
    ///   <item>NewYork     — 17:00–23:59 UTC (US afternoon through close)</item>
    /// </list>
    /// </summary>
    private static TradingSession ClassifySession(DateTime utc)
    {
        int hour = utc.Hour;
        return hour switch
        {
            >= 0  and <= 6  => TradingSession.Asian,
            >= 7  and <= 12 => TradingSession.London,
            >= 13 and <= 16 => TradingSession.LondonNYOverlap,
            _               => TradingSession.NewYork,   // 17–23
        };
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
