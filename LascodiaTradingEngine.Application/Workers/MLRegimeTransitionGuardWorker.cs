using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Dampens ML model confidence for a configurable number of bars after a market regime
/// transition is detected, preventing the model from over-committing when the market
/// has structurally changed and historical accuracy metrics no longer apply.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>For each active model (symbol/timeframe), load the two most recent
///         <see cref="MarketRegimeSnapshot"/> records.</item>
///   <item>If they differ (a regime transition occurred), compute how many timeframe
///         bars have elapsed since the newer snapshot's <c>DetectedAt</c>.</item>
///   <item>If bars elapsed &lt; <c>TransitionWindowBars</c>, write (or update) an
///         <see cref="EngineConfig"/> row with key
///         <c>MLRegimeTransition:{Symbol}:{Timeframe}:PenaltyFactor</c>
///         set to the configured penalty value (default 0.80).</item>
///   <item>If bars elapsed ≥ <c>TransitionWindowBars</c>, reset the key to "1.0"
///         (identity — no dampening).</item>
/// </list>
///
/// <b><see cref="MLSignalScorer"/> integration:</b>
/// Step 12d reads <c>MLRegimeTransition:{Symbol}:{Timeframe}:PenaltyFactor</c>
/// and multiplies the computed confidence by the factor.
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLRegimeTransition:PollIntervalSeconds</c> — default 300 (5 min)</item>
///   <item><c>MLRegimeTransition:TransitionWindowBars</c> — default 20</item>
///   <item><c>MLRegimeTransition:PenaltyFactor</c>        — default 0.80</item>
/// </list>
/// </summary>
public sealed class MLRegimeTransitionGuardWorker : BackgroundService
{
    private const string CK_PollSecs       = "MLRegimeTransition:PollIntervalSeconds";
    private const string CK_WindowBars     = "MLRegimeTransition:TransitionWindowBars";
    private const string CK_Penalty        = "MLRegimeTransition:PenaltyFactor";
    private const string KeyPrefix         = "MLRegimeTransition:";
    private const string KeySuffix         = ":PenaltyFactor";

    private readonly IServiceScopeFactory                       _scopeFactory;
    private readonly ILogger<MLRegimeTransitionGuardWorker>     _logger;

    /// <summary>
    /// Initializes the worker.
    /// </summary>
    /// <param name="scopeFactory">Per-iteration DI scope factory.</param>
    /// <param name="logger">Structured logger.</param>
    public MLRegimeTransitionGuardWorker(
        IServiceScopeFactory                    scopeFactory,
        ILogger<MLRegimeTransitionGuardWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Background service main loop. Polls every 300 seconds (5 minutes) by default,
    /// making it one of the most frequent ML workers because regime transitions can
    /// occur rapidly and the confidence dampening must be applied before the next
    /// prediction log is scored.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLRegimeTransitionGuardWorker started.");

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

                await GuardTransitionsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLRegimeTransitionGuardWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLRegimeTransitionGuardWorker stopping.");
    }

    // ── Guard core ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the distinct set of (Symbol, Timeframe) pairs covered by active
    /// models and applies the regime-transition guard to each pair. Multiple models
    /// on the same symbol/timeframe share a single penalty key — the guard is applied
    /// at the pair level, not the model level.
    /// </summary>
    private async Task GuardTransitionsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowBars = await GetConfigAsync<int>   (readCtx, CK_WindowBars, 20,   ct);
        double penalty    = await GetConfigAsync<double>(readCtx, CK_Penalty,    0.80, ct);

        // Use Distinct so that multiple models on the same symbol/timeframe are
        // processed once — they all share the same regime snapshot and penalty key.
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        if (activeModels.Count == 0) return;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ProcessPairAsync(
                    model.Symbol, model.Timeframe,
                    windowBars, penalty,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RegimeTransitionGuard: failed for {Symbol}/{Tf} — skipping.",
                    model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Applies or lifts the regime-transition confidence penalty for a single
    /// (symbol, timeframe) pair by comparing the two most recent
    /// <see cref="MarketRegimeSnapshot"/> records.
    /// </summary>
    /// <remarks>
    /// <b>Regime transition handling logic:</b>
    /// <list type="number">
    ///   <item>Load the two most recent snapshots for the pair.</item>
    ///   <item>If regimes differ (a transition occurred):
    ///     <list type="bullet">
    ///       <item>Compute elapsed bars = (now − latestSnapshot.DetectedAt) / barDuration.</item>
    ///       <item>If elapsed &lt; windowBars: set penalty factor (e.g. 0.80) — the model is
    ///             operating in the "danger zone" immediately after a structural market change
    ///             where its historical feature-to-outcome mapping may no longer hold.</item>
    ///       <item>If elapsed ≥ windowBars: reset factor to 1.0 — the model has had time
    ///             to accumulate new observations in the new regime.</item>
    ///     </list>
    ///   </item>
    ///   <item>If regimes match (no recent transition): reset factor to 1.0.</item>
    /// </list>
    /// The penalty key written to <see cref="EngineConfig"/> follows the pattern
    /// <c>MLRegimeTransition:{Symbol}:{Timeframe}:PenaltyFactor</c> and is read by
    /// <c>MLSignalScorer</c> to multiplicatively dampen the computed confidence.
    /// </remarks>
    private async Task ProcessPairAsync(
        string                                  symbol,
        Timeframe                               timeframe,
        int                                     windowBars,
        double                                  penalty,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load the two most recent regime snapshots to detect a change in regime.
        // We need exactly 2 to compare consecutive snapshots; fewer means we cannot
        // determine whether a transition has occurred.
        var recent = await readCtx.Set<MarketRegimeSnapshot>()
            .Where(r => r.Symbol    == symbol    &&
                        r.Timeframe == timeframe  &&
                        !r.IsDeleted)
            .OrderByDescending(r => r.DetectedAt)
            .Take(2)
            .AsNoTracking()
            .ToListAsync(ct);

        if (recent.Count < 2)
        {
            _logger.LogDebug(
                "RegimeTransitionGuard: {Symbol}/{Tf} — fewer than 2 snapshots, skipping.",
                symbol, timeframe);
            return;
        }

        var latest    = recent[0]; // newest snapshot
        var previous  = recent[1]; // immediately preceding snapshot

        // The EngineConfig key is unique per (symbol, timeframe) pair.
        // MLSignalScorer reads this key and multiplies the confidence by its value.
        string configKey = $"{KeyPrefix}{symbol}:{timeframe}{KeySuffix}";

        if (latest.Regime != previous.Regime)
        {
            // ── Regime transition detected ────────────────────────────────────
            // Compute how many timeframe bars have elapsed since the regime changed.
            // Dividing wallclock seconds by bar duration converts time to "bar units",
            // making the window threshold timeframe-agnostic.
            var barDuration  = TimeframeDurationHelper.BarDuration(timeframe);
            double barsElapsed = barDuration.TotalSeconds > 0
                ? (DateTime.UtcNow - latest.DetectedAt).TotalSeconds / barDuration.TotalSeconds
                : double.MaxValue;

            if (barsElapsed < windowBars)
            {
                // Still within the post-transition dampening window — apply penalty.
                // The penalty reduces confidence by a fixed factor to prevent the model
                // from over-committing when market structure has just changed.
                string penaltyStr = penalty.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                await UpsertConfigAsync(writeCtx, configKey, penaltyStr, ct);

                _logger.LogDebug(
                    "RegimeTransitionGuard: {Symbol}/{Tf} — regime changed from {Prev} to {New} " +
                    "{Bars:F1} bars ago (window={Window}) → penaltyFactor={Penalty}",
                    symbol, timeframe, previous.Regime, latest.Regime, barsElapsed, windowBars, penalty);
            }
            else
            {
                // Transition window has expired — the model has operated in the new regime
                // long enough for its accuracy metrics to be representative again.
                await UpsertConfigAsync(writeCtx, configKey, "1.0", ct);

                _logger.LogDebug(
                    "RegimeTransitionGuard: {Symbol}/{Tf} — transition window expired " +
                    "({Bars:F1} bars ≥ {Window}) → penaltyFactor reset to 1.0",
                    symbol, timeframe, barsElapsed, windowBars);
            }
        }
        else
        {
            // No transition between the two most recent snapshots — regime is stable.
            // Ensure any previously written penalty is cleared (idempotent).
            await UpsertConfigAsync(writeCtx, configKey, "1.0", ct);

            _logger.LogDebug(
                "RegimeTransitionGuard: {Symbol}/{Tf} — regime stable ({Regime}), penalty 1.0",
                symbol, timeframe, latest.Regime);
        }
    }

    // ── EngineConfig upsert ───────────────────────────────────────────────────

    /// <summary>
    /// Inserts or updates a single <see cref="EngineConfig"/> row identified by
    /// <paramref name="key"/>. Uses <c>ExecuteUpdateAsync</c> for efficiency; falls
    /// back to an insert when the key does not yet exist (first time the guard runs
    /// for a new symbol/timeframe pair).
    /// </summary>
    /// <param name="writeCtx">Write DbContext.</param>
    /// <param name="key">The unique config key (e.g. <c>MLRegimeTransition:EURUSD:H1:PenaltyFactor</c>).</param>
    /// <param name="value">String representation of the penalty factor (e.g. "0.8000" or "1.0").</param>
    /// <param name="ct">Cancellation token.</param>
    private static Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(writeCtx, key, value, dataType: LascodiaTradingEngine.Domain.Enums.ConfigDataType.Decimal, ct: ct);

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
