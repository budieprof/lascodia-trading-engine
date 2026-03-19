using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes per-regime live direction accuracy for each active model and persists the
/// results as <see cref="MLModelRegimeAccuracy"/> rows (upserted, one row per
/// model × regime combination).
///
/// <b>Motivation:</b> All drift and suppression workers pool predictions across all
/// market regimes. A model that is 65% accurate overall can be simultaneously 30%
/// accurate in <c>Ranging</c> markets and 90% accurate in <c>Trending</c> markets.
/// Without per-regime breakdowns the blended accuracy masks this regime-specific
/// weakness, and signals continue to be scored in regimes where the model has no edge.
///
/// <b>Algorithm:</b>
/// <list type="bullet">
///   <item>Load resolved <see cref="MLModelPredictionLog"/> entries from the last
///         <c>WindowDays</c> days.</item>
///   <item>Load <see cref="MarketRegimeSnapshot"/> entries for the same symbol/timeframe
///         window and build a sorted timeline for efficient nearest-timestamp lookup.</item>
///   <item>For each prediction log, find the regime snapshot whose
///         <c>DetectedAt</c> is closest to and ≤ <c>PredictedAt</c>.</item>
///   <item>Group by regime; compute accuracy = correct / total.</item>
///   <item>Upsert one <see cref="MLModelRegimeAccuracy"/> row per (model, regime) pair
///         using EF <c>ExecuteUpdateAsync</c> / insert on conflict.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLRegimeAccuracy:PollIntervalSeconds</c> — default 3600 (1 h)</item>
///   <item><c>MLRegimeAccuracy:WindowDays</c>          — rolling accuracy window, default 14</item>
///   <item><c>MLRegimeAccuracy:MinPredictions</c>      — min resolved predictions per regime, default 10</item>
/// </list>
/// </summary>
public sealed class MLRegimeAccuracyWorker : BackgroundService
{
    private const string CK_PollSecs       = "MLRegimeAccuracy:PollIntervalSeconds";
    private const string CK_WindowDays     = "MLRegimeAccuracy:WindowDays";
    private const string CK_MinPredictions = "MLRegimeAccuracy:MinPredictions";

    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<MLRegimeAccuracyWorker> _logger;

    public MLRegimeAccuracyWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<MLRegimeAccuracyWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLRegimeAccuracyWorker started.");

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

                await ComputeRegimeAccuracyAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLRegimeAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLRegimeAccuracyWorker stopping.");
    }

    // ── Projection types ──────────────────────────────────────────────────────

    private sealed record PredLog(long Id, DateTime PredictedAt, bool DirectionCorrect);
    private sealed record RegimeSlice(DateTime DetectedAt, Domain.Enums.MarketRegime Regime);

    // ── Main computation ──────────────────────────────────────────────────────

    private async Task ComputeRegimeAccuracyAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int windowDays     = await GetConfigAsync<int>(readCtx, CK_WindowDays,     14, ct);
        int minPredictions = await GetConfigAsync<int>(readCtx, CK_MinPredictions, 10, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ComputeModelRegimeAccuracyAsync(
                    model, readCtx, writeCtx,
                    windowDays, minPredictions, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RegimeAccuracy: failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    private async Task ComputeModelRegimeAccuracyAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minPredictions,
        CancellationToken                       ct)
    {
        var since = DateTime.UtcNow.AddDays(-windowDays);

        // Load resolved prediction logs for this model within the window
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.DirectionCorrect != null      &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => new PredLog(l.Id, l.PredictedAt, l.DirectionCorrect!.Value))
            .OrderBy(l => l.PredictedAt)
            .ToListAsync(ct);

        if (logs.Count < minPredictions)
        {
            _logger.LogDebug(
                "RegimeAccuracy: {Symbol}/{Tf} model {Id}: only {N} logs (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, logs.Count, minPredictions);
            return;
        }

        // Load regime snapshots for same symbol/timeframe — slightly extended window for lookback
        var regimeTimeline = await readCtx.Set<MarketRegimeSnapshot>()
            .Where(r => r.Symbol    == model.Symbol    &&
                        r.Timeframe == model.Timeframe &&
                        r.DetectedAt >= since.AddDays(-1) &&
                        !r.IsDeleted)
            .AsNoTracking()
            .Select(r => new RegimeSlice(r.DetectedAt, r.Regime))
            .OrderBy(r => r.DetectedAt)
            .ToListAsync(ct);

        if (regimeTimeline.Count == 0)
        {
            _logger.LogDebug(
                "RegimeAccuracy: {Symbol}/{Tf} model {Id}: no regime snapshots available — skip.",
                model.Symbol, model.Timeframe, model.Id);
            return;
        }

        // For each log, binary-search the regime timeline for the last snapshot ≤ PredictedAt
        var byRegime = new Dictionary<Domain.Enums.MarketRegime, (int Total, int Correct)>();

        foreach (var log in logs)
        {
            var regime = FindRegimeAt(regimeTimeline, log.PredictedAt);
            if (regime is null) continue;

            var key = regime.Value;
            byRegime.TryGetValue(key, out var existing);
            byRegime[key] = (existing.Total + 1, existing.Correct + (log.DirectionCorrect ? 1 : 0));
        }

        if (byRegime.Count == 0) return;

        var now = DateTime.UtcNow;
        int rowsWritten = 0;

        foreach (var (regime, (total, correct)) in byRegime)
        {
            if (total < minPredictions)
                continue;

            double accuracy = correct / (double)total;

            _logger.LogDebug(
                "RegimeAccuracy: {Symbol}/{Tf} model {Id} regime={Regime}: " +
                "total={Total} correct={Correct} accuracy={Acc:P1}",
                model.Symbol, model.Timeframe, model.Id, regime, total, correct, accuracy);

            // Upsert: update if row exists, insert otherwise
            int updated = await writeCtx.Set<MLModelRegimeAccuracy>()
                .Where(r => r.MLModelId == model.Id && r.Regime == regime)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.TotalPredictions,   total)
                    .SetProperty(r => r.CorrectPredictions, correct)
                    .SetProperty(r => r.Accuracy,           accuracy)
                    .SetProperty(r => r.WindowStart,        since)
                    .SetProperty(r => r.ComputedAt,         now),
                    ct);

            if (updated == 0)
            {
                // Row does not exist — insert
                writeCtx.Set<MLModelRegimeAccuracy>().Add(new MLModelRegimeAccuracy
                {
                    MLModelId          = model.Id,
                    Symbol             = model.Symbol,
                    Timeframe          = model.Timeframe,
                    Regime             = regime,
                    TotalPredictions   = total,
                    CorrectPredictions = correct,
                    Accuracy           = accuracy,
                    WindowStart        = since,
                    ComputedAt         = now,
                });
                await writeCtx.SaveChangesAsync(ct);
            }

            rowsWritten++;
        }

        if (rowsWritten > 0)
            _logger.LogInformation(
                "RegimeAccuracy: {Symbol}/{Tf} model {Id}: upserted {N} regime accuracy rows.",
                model.Symbol, model.Timeframe, model.Id, rowsWritten);
    }

    // ── Nearest-snapshot lookup ───────────────────────────────────────────────

    /// <summary>
    /// Binary-searches <paramref name="timeline"/> (sorted ascending by <c>DetectedAt</c>)
    /// for the last entry whose <c>DetectedAt</c> ≤ <paramref name="at"/>.
    /// Returns <c>null</c> when no such entry exists.
    /// </summary>
    private static Domain.Enums.MarketRegime? FindRegimeAt(
        List<RegimeSlice> timeline,
        DateTime          at)
    {
        int lo = 0, hi = timeline.Count - 1, result = -1;

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (timeline[mid].DetectedAt <= at)
            {
                result = mid;
                lo     = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result >= 0 ? timeline[result].Regime : null;
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
