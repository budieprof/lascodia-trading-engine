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
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    // All config is read live from the EngineConfig table each poll cycle.
    private const string CK_PollSecs       = "MLRegimeAccuracy:PollIntervalSeconds";
    private const string CK_WindowDays     = "MLRegimeAccuracy:WindowDays";
    private const string CK_MinPredictions = "MLRegimeAccuracy:MinPredictions";

    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<MLRegimeAccuracyWorker> _logger;

    /// <summary>
    /// Initialises the worker with a DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Factory for creating per-poll scoped service lifetimes, ensuring DbContexts
    /// are cleanly disposed after each computation cycle.
    /// </param>
    /// <param name="logger">Structured logger for computation diagnostics.</param>
    public MLRegimeAccuracyWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<MLRegimeAccuracyWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs an infinite polling loop that:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope to obtain scoped read/write DB contexts.</item>
    ///   <item>Reads the current poll interval from <see cref="EngineConfig"/> (live config).</item>
    ///   <item>Delegates the per-regime accuracy computation to
    ///         <see cref="ComputeRegimeAccuracyAsync"/>.</item>
    ///   <item>Sleeps for <c>pollSecs</c> before the next cycle.</item>
    /// </list>
    /// Non-cancellation exceptions are caught and logged so a transient DB failure
    /// cannot kill the hosted service permanently.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled on host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLRegimeAccuracyWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval used when the config key is absent.
            int pollSecs = 3600;

            try
            {
                // Fresh scope per iteration keeps EF change tracking isolated.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Read interval live so operators can adjust frequency without restart.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await ComputeRegimeAccuracyAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Clean shutdown — exit the loop immediately.
                break;
            }
            catch (Exception ex)
            {
                // Log and continue; resilience is more important than strict accuracy for
                // a background monitoring worker.
                _logger.LogError(ex, "MLRegimeAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLRegimeAccuracyWorker stopping.");
    }

    // ── Projection types ──────────────────────────────────────────────────────

    /// <summary>
    /// Lightweight projection of a resolved <see cref="MLModelPredictionLog"/> row.
    /// Only the fields required for regime classification and accuracy tallying are fetched.
    /// </summary>
    private sealed record PredLog(long Id, DateTime PredictedAt, bool DirectionCorrect);

    /// <summary>
    /// Lightweight projection of a <see cref="MarketRegimeSnapshot"/> row.
    /// Used to build the sorted timeline for binary-search regime lookups.
    /// </summary>
    private sealed record RegimeSlice(DateTime DetectedAt, Domain.Enums.MarketRegime Regime);

    // ── Main computation ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads global config parameters for the current poll cycle, then iterates
    /// all active ML models and calls <see cref="ComputeModelRegimeAccuracyAsync"/>
    /// for each, isolating failures per model so one bad model cannot block the rest.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for fetching models and logs.</param>
    /// <param name="writeCtx">Write DbContext for upserting accuracy rows.</param>
    /// <param name="ct">Cancellation token checked between model iterations.</param>
    private async Task ComputeRegimeAccuracyAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load config once per cycle to avoid repeated DB round-trips inside the loop.
        int windowDays     = await GetConfigAsync<int>(readCtx, CK_WindowDays,     14, ct);
        int minPredictions = await GetConfigAsync<int>(readCtx, CK_MinPredictions, 10, ct);

        // Only compute for actively deployed models.
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Respect fast cancellation between per-model computations.
            ct.ThrowIfCancellationRequested();

            try
            {
                await ComputeModelRegimeAccuracyAsync(
                    model, readCtx, writeCtx,
                    windowDays, minPredictions, ct);
            }
            catch (Exception ex)
            {
                // Isolate per-model errors — one failure should not stop others.
                _logger.LogWarning(ex,
                    "RegimeAccuracy: failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    /// <summary>
    /// Computes per-regime direction accuracy for a single ML model:
    /// <list type="number">
    ///   <item>Loads resolved prediction logs for the model within the look-back window.</item>
    ///   <item>Loads the <see cref="MarketRegimeSnapshot"/> timeline for the same
    ///         symbol/timeframe, extended 1 day before the window to cover predictions
    ///         at the very start of the window.</item>
    ///   <item>For each prediction log, uses binary search
    ///         (<see cref="FindRegimeAt"/>) to find the regime in effect at prediction time.</item>
    ///   <item>Groups by regime and computes accuracy = correct / total per group.</item>
    ///   <item>Upserts one <see cref="MLModelRegimeAccuracy"/> row per (model, regime) pair
    ///         that meets the <paramref name="minPredictions"/> threshold.</item>
    /// </list>
    /// </summary>
    /// <param name="model">The active ML model being evaluated.</param>
    /// <param name="readCtx">Read DbContext for prediction logs and regime snapshots.</param>
    /// <param name="writeCtx">Write DbContext for upserting accuracy rows.</param>
    /// <param name="windowDays">Number of days in the rolling accuracy window.</param>
    /// <param name="minPredictions">
    /// Minimum resolved predictions required per regime before a row is written.
    /// Prevents unreliable accuracy estimates from tiny samples.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ComputeModelRegimeAccuracyAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minPredictions,
        CancellationToken                       ct)
    {
        var since = DateTime.UtcNow.AddDays(-windowDays);

        // Load resolved prediction logs for this model within the window.
        // DirectionCorrect != null means the outcome has been evaluated by
        // MLPredictionOutcomeWorker and the ground truth is known.
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.DirectionCorrect != null      &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => new PredLog(l.Id, l.PredictedAt, l.DirectionCorrect!.Value))
            .OrderBy(l => l.PredictedAt)   // chronological order for binary-search validity
            .ToListAsync(ct);

        if (logs.Count < minPredictions)
        {
            _logger.LogDebug(
                "RegimeAccuracy: {Symbol}/{Tf} model {Id}: only {N} logs (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, logs.Count, minPredictions);
            return;
        }

        // Load regime snapshots for same symbol/timeframe.
        // The window is extended 1 day back so predictions at the start of the accuracy
        // window are still covered by at least one preceding snapshot.
        var regimeTimeline = await readCtx.Set<MarketRegimeSnapshot>()
            .Where(r => r.Symbol    == model.Symbol    &&
                        r.Timeframe == model.Timeframe &&
                        r.DetectedAt >= since.AddDays(-1) &&
                        !r.IsDeleted)
            .AsNoTracking()
            .Select(r => new RegimeSlice(r.DetectedAt, r.Regime))
            .OrderBy(r => r.DetectedAt)   // ascending order required for binary search
            .ToListAsync(ct);

        if (regimeTimeline.Count == 0)
        {
            _logger.LogDebug(
                "RegimeAccuracy: {Symbol}/{Tf} model {Id}: no regime snapshots available — skip.",
                model.Symbol, model.Timeframe, model.Id);
            return;
        }

        // For each log, binary-search the regime timeline for the last snapshot ≤ PredictedAt.
        // This assigns each prediction to the market regime that was active when the
        // model made the call, rather than the regime at some arbitrary fixed time.
        var byRegime = new Dictionary<Domain.Enums.MarketRegime, (int Total, int Correct)>();

        foreach (var log in logs)
        {
            var regime = FindRegimeAt(regimeTimeline, log.PredictedAt);

            // If no regime snapshot precedes this prediction, skip (cannot classify).
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
            // Skip regimes with too few observations to produce a reliable accuracy estimate.
            if (total < minPredictions)
                continue;

            // Simple proportion accuracy: correct predictions / total predictions for this regime.
            double accuracy = correct / (double)total;

            _logger.LogDebug(
                "RegimeAccuracy: {Symbol}/{Tf} model {Id} regime={Regime}: " +
                "total={Total} correct={Correct} accuracy={Acc:P1}",
                model.Symbol, model.Timeframe, model.Id, regime, total, correct, accuracy);

            // Upsert strategy: try ExecuteUpdateAsync first (zero-allocation update path).
            // If no row exists yet, fall back to a tracked insert + SaveChangesAsync.
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
                // Row does not exist yet — insert a new one.
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
    /// Returns <c>null</c> when no such entry exists (i.e., the prediction precedes
    /// all regime snapshots in the timeline).
    ///
    /// <b>Complexity:</b> O(log N) per call — essential when processing thousands of
    /// prediction logs against a large regime snapshot timeline in a single poll cycle.
    /// </summary>
    /// <param name="timeline">
    /// Regime snapshot list sorted ascending by <c>DetectedAt</c>. The list must be
    /// sorted before calling this method (guaranteed by the <c>OrderBy</c> in the caller).
    /// </param>
    /// <param name="at">The UTC timestamp of the prediction being classified.</param>
    /// <returns>
    /// The <see cref="Domain.Enums.MarketRegime"/> that was active at time <paramref name="at"/>,
    /// or <c>null</c> if no snapshot predates the given timestamp.
    /// </returns>
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
                // This snapshot is a valid candidate; record it and search the right half
                // for a later snapshot that is still ≤ at.
                result = mid;
                lo     = mid + 1;
            }
            else
            {
                // Snapshot is after 'at' — search left half.
                hi = mid - 1;
            }
        }

        return result >= 0 ? timeline[result].Regime : null;
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key is absent or the stored
    /// string cannot be converted to <typeparamref name="T"/>.
    /// This allows live tuning of worker thresholds without a service restart.
    /// </summary>
    /// <typeparam name="T">Target CLR type (e.g. <c>int</c>, <c>double</c>).</typeparam>
    /// <param name="ctx">DbContext to query.</param>
    /// <param name="key">The <see cref="EngineConfig.Key"/> to look up.</param>
    /// <param name="defaultValue">Fallback value used when the key is missing or unparseable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed config value or <paramref name="defaultValue"/>.</returns>
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
