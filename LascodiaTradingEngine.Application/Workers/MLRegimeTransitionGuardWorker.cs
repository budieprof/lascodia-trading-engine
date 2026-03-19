using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
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

    public MLRegimeTransitionGuardWorker(
        IServiceScopeFactory                    scopeFactory,
        ILogger<MLRegimeTransitionGuardWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

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

    private async Task GuardTransitionsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowBars = await GetConfigAsync<int>   (readCtx, CK_WindowBars, 20,   ct);
        double penalty    = await GetConfigAsync<double>(readCtx, CK_Penalty,    0.80, ct);

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

    private async Task ProcessPairAsync(
        string                                  symbol,
        Timeframe                               timeframe,
        int                                     windowBars,
        double                                  penalty,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load the two most recent regime snapshots
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

        var latest    = recent[0]; // newest
        var previous  = recent[1]; // one before newest

        string configKey = $"{KeyPrefix}{symbol}:{timeframe}{KeySuffix}";

        if (latest.Regime != previous.Regime)
        {
            // A transition occurred — compute bars elapsed since the latest snapshot
            var barDuration  = TimeframeExpectedGap(timeframe);
            double barsElapsed = barDuration.TotalSeconds > 0
                ? (DateTime.UtcNow - latest.DetectedAt).TotalSeconds / barDuration.TotalSeconds
                : double.MaxValue;

            if (barsElapsed < windowBars)
            {
                // Within transition window: apply penalty
                string penaltyStr = penalty.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                await UpsertConfigAsync(writeCtx, configKey, penaltyStr, ct);

                _logger.LogDebug(
                    "RegimeTransitionGuard: {Symbol}/{Tf} — regime changed from {Prev} to {New} " +
                    "{Bars:F1} bars ago (window={Window}) → penaltyFactor={Penalty}",
                    symbol, timeframe, previous.Regime, latest.Regime, barsElapsed, windowBars, penalty);
            }
            else
            {
                // Transition window expired: lift penalty
                await UpsertConfigAsync(writeCtx, configKey, "1.0", ct);

                _logger.LogDebug(
                    "RegimeTransitionGuard: {Symbol}/{Tf} — transition window expired " +
                    "({Bars:F1} bars ≥ {Window}) → penaltyFactor reset to 1.0",
                    symbol, timeframe, barsElapsed, windowBars);
            }
        }
        else
        {
            // No transition — ensure penalty is lifted
            await UpsertConfigAsync(writeCtx, configKey, "1.0", ct);

            _logger.LogDebug(
                "RegimeTransitionGuard: {Symbol}/{Tf} — regime stable ({Regime}), penalty 1.0",
                symbol, timeframe, latest.Regime);
        }
    }

    // ── EngineConfig upsert ───────────────────────────────────────────────────

    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        int rows = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Value,         value)
                .SetProperty(c => c.LastUpdatedAt, DateTime.UtcNow),
                ct);

        if (rows == 0)
        {
            writeCtx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key             = key,
                Value           = value,
                DataType        = Domain.Enums.ConfigDataType.Decimal,
                Description     = "Regime-transition confidence penalty factor for MLSignalScorer. " +
                                  "Written by MLRegimeTransitionGuardWorker.",
                IsHotReloadable = true,
                LastUpdatedAt   = DateTime.UtcNow,
            });
            await writeCtx.SaveChangesAsync(ct);
        }
    }

    // ── Timeframe helper ──────────────────────────────────────────────────────

    private static TimeSpan TimeframeExpectedGap(Timeframe tf) => tf switch
    {
        Timeframe.M1  => TimeSpan.FromMinutes(1),
        Timeframe.M5  => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.H1  => TimeSpan.FromHours(1),
        Timeframe.H4  => TimeSpan.FromHours(4),
        Timeframe.D1  => TimeSpan.FromHours(24),
        _             => TimeSpan.FromHours(1),
    };

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
