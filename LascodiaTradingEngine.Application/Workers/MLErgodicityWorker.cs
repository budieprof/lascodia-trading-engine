using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes ergodicity economics metrics for each active ML model (Rec #519).
///
/// <para>
/// Ergodicity economics distinguishes ensemble-average growth (arithmetic mean) from
/// time-average growth (geometric mean). The ergodicity gap between these two quantities
/// drives a downward adjustment to the naive Kelly fraction, producing a position size
/// that maximises long-run compounded wealth rather than expected value.
/// </para>
///
/// Algorithm per cycle:
/// <list type="number">
///   <item>Load active, non-deleted ML models.</item>
///   <item>For each model, load the last WindowDays (up to 200) resolved prediction logs.</item>
///   <item>Convert outcomes to "returns": correct prediction = +confidence-0.5; incorrect = -(confidence-0.5).</item>
///   <item>Compute ensemble growth rate, Peters time-average, ergodicity gap.</item>
///   <item>Compute naive and ergodicity-adjusted Kelly fractions.</item>
///   <item>Persist <see cref="MLErgodicityLog"/> per model.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLErgodicity:PollIntervalHours</c> — default 24</item>
///   <item><c>MLErgodicity:WindowDays</c>        — rolling history in days, default 30</item>
///   <item><c>MLErgodicity:MinSamples</c>        — minimum prediction log count required, default 20</item>
/// </list>
/// </summary>
public sealed class MLErgodicityWorker : BackgroundService
{
    private const string CK_PollHours  = "MLErgodicity:PollIntervalHours";
    private const string CK_WindowDays = "MLErgodicity:WindowDays";
    private const string CK_MinSamples = "MLErgodicity:MinSamples";

    private readonly IServiceScopeFactory        _scopeFactory;
    private readonly ILogger<MLErgodicityWorker> _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new async DI scope per poll cycle so scoped EF Core
    /// contexts are correctly disposed after each ergodicity computation pass.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLErgodicityWorker(
        IServiceScopeFactory         scopeFactory,
        ILogger<MLErgodicityWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Polls every <c>MLErgodicity:PollIntervalHours</c>
    /// hours (default 24), reading the interval from <see cref="EngineConfig"/> on each
    /// cycle so it can be hot-reloaded without a restart.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLErgodicityWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default 24-hour poll interval; refreshed from DB on every cycle.
            int pollHours = 24;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Refresh all config values each cycle to support operator hot-reload.
                pollHours      = await GetConfigAsync<int>(ctx, CK_PollHours,  24, stoppingToken);
                int windowDays = await GetConfigAsync<int>(ctx, CK_WindowDays, 30, stoppingToken);
                int minSamples = await GetConfigAsync<int>(ctx, CK_MinSamples, 20, stoppingToken);

                await RunErgodicityAsync(ctx, wCtx, windowDays, minSamples, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLErgodicityWorker loop error");
            }

            await Task.Delay(TimeSpan.FromHours(pollHours), stoppingToken);
        }

        _logger.LogInformation("MLErgodicityWorker stopping.");
    }

    // ── Ergodicity core ───────────────────────────────────────────────────────

    /// <summary>
    /// For each active model, computes ergodicity economics metrics from the last
    /// <paramref name="windowDays"/> days of resolved prediction logs and persists the
    /// results to <c>MLErgodicityLog</c>.
    /// </summary>
    /// <remarks>
    /// Ergodicity economics methodology (Ole Peters, 2019):
    ///
    /// Classical expected-value theory maximises the ensemble average (arithmetic mean)
    /// of outcomes across many parallel trials. However, a single trader experiences a
    /// time sequence of outcomes, not an ensemble. For multiplicative processes (which
    /// compounding wealth is), the long-run time average (geometric mean growth rate)
    /// differs from the ensemble average:
    ///   time_average = mean(log(1 + r))
    ///   ensemble_average = mean(r)
    ///   ergodicity_gap = ensemble_average − time_average
    ///
    /// A positive gap means the ensemble average overstates the long-run per-trade
    /// growth rate. Kelly and naive position-sizing formulas derived from the ensemble
    /// average oversize positions, leading to ruin in finite time even when expected
    /// value is positive.
    ///
    /// The ergodicity-adjusted Kelly fraction corrects for this by scaling down the
    /// naive Kelly: f_adj = f_naive × (1 − gap / variance). This produces a position
    /// size that maximises the geometric growth rate (Peters optimal fraction) rather
    /// than the arithmetic expectation (naive Kelly).
    ///
    /// Return proxy: each prediction log contributes a return of:
    ///   r = (confidence − 0.5) if correct, −(confidence − 0.5) if incorrect.
    /// This uses the model's stated confidence as a proxy for the magnitude of the
    /// position's P&amp;L contribution. Positive confidence above 0.5 maps to a positive
    /// return on a correct prediction.
    /// </remarks>
    /// <param name="readCtx">Read-only EF context for models and prediction logs.</param>
    /// <param name="writeCtx">Write EF context for persisting <c>MLErgodicityLog</c> records.</param>
    /// <param name="windowDays">Rolling history window for prediction logs.</param>
    /// <param name="minSamples">Minimum resolved logs required before computing metrics.</param>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task RunErgodicityAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minSamples,
        CancellationToken                       ct)
    {
        var now    = DateTime.UtcNow;
        var cutoff = now.AddDays(-windowDays);

        var models = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => !m.IsDeleted && m.IsActive)
            .ToListAsync(ct);

        foreach (var model in models)
        {
            // Load resolved prediction logs within the rolling window.
            // Cap at 200 records to bound memory usage per model.
            var logs = await readCtx.Set<MLModelPredictionLog>()
                .AsNoTracking()
                .Where(l => l.MLModelId == model.Id &&
                            !l.IsDeleted &&
                            l.DirectionCorrect.HasValue &&
                            l.OutcomeRecordedAt != null &&
                            l.OutcomeRecordedAt >= cutoff)
                .OrderByDescending(l => l.OutcomeRecordedAt)
                .ThenByDescending(l => l.Id)
                .Take(200)
                .ToListAsync(ct);

            // Skip models without enough resolved history for reliable metric estimates.
            if (logs.Count < minSamples)
            {
                _logger.LogDebug("MLErgodicityWorker: model {Id} ({Sym}) skipped — only {N} logs",
                    model.Id, model.Symbol, logs.Count);
                continue;
            }

            // Convert prediction logs to return proxies.
            // ret > 0 for correct predictions, ret < 0 for incorrect predictions.
            // The magnitude is proportional to the model's stated confidence above 0.5.
            double[] r = new double[logs.Count];
            for (int i = 0; i < logs.Count; i++)
            {
                double pBuy = MLFeatureHelper.ResolveLoggedServedBuyProbability(logs[i]);
                double conf = logs[i].PredictedDirection == LascodiaTradingEngine.Domain.Enums.TradeDirection.Buy
                    ? pBuy
                    : 1.0 - pBuy;
                r[i] = logs[i].DirectionCorrect == true
                    ? conf - 0.5          // correct: positive return proportional to confidence
                    : -(conf - 0.5);      // incorrect: negative return proportional to confidence
            }

            // Ensemble growth rate: arithmetic mean of returns across all predictions.
            // This is what naive expected-value optimisation maximises.
            double mu = r.Average();

            // Peters time-average growth rate: mean of log(1 + r).
            // This is the long-run geometric growth rate per trade for a compounding
            // account. Clamp r to −0.9999 to prevent log(0) or log(negative).
            double timeAvg = r.Average(v => Math.Log(1.0 + Math.Max(v, -0.9999)));

            // Ergodicity gap: difference between ensemble and time averages.
            // A large positive gap indicates that naive sizing would systematically
            // oversize positions, degrading long-run compounded wealth.
            double gap = mu - timeAvg;

            // Variance of returns: used as the denominator in both Kelly formulas.
            // Bessel-corrected (divide by N-1) for an unbiased sample variance.
            double sigma2 = r.Sum(v => (v - mu) * (v - mu)) / Math.Max(r.Length - 1, 1);

            // Naive Kelly fraction: f* = μ / σ² (continuous Kelly for log-normal returns).
            // This maximises E[log(wealth)] ignoring the ergodicity correction.
            double naiveKelly = mu / Math.Max(sigma2, 1e-10);

            // Ergodicity-adjusted Kelly: scale naive Kelly by (1 − gap / σ²).
            // This applies the Peters correction: it reduces the fraction when the
            // ergodicity gap is positive (process is non-ergodic and multiplicative).
            // Clamp to [−2, 2] to prevent pathological extreme values from unstable
            // variance estimates with few samples.
            double adjKelly = naiveKelly * (1.0 - gap / Math.Max(sigma2, 1e-10));
            adjKelly = Math.Max(-2.0, Math.Min(2.0, adjKelly));

            // Persist all computed metrics as a new MLErgodicityLog record.
            // Downstream position-sizing workers read ErgodicityAdjustedKelly to
            // determine the optimal fraction of account equity to risk per signal.
            var log = new MLErgodicityLog
            {
                MLModelId                = model.Id,
                Symbol                   = model.Symbol,
                EnsembleGrowthRate       = (decimal)mu,
                TimeAverageGrowthRate    = (decimal)timeAvg,
                ErgodicityGap            = (decimal)gap,
                NaiveKellyFraction       = (decimal)naiveKelly,
                ErgodicityAdjustedKelly  = (decimal)adjKelly,
                GrowthRateVariance       = (decimal)sigma2,
                ComputedAt               = now,
            };

            writeCtx.Set<MLErgodicityLog>().Add(log);
            await writeCtx.SaveChangesAsync(ct);

            _logger.LogDebug(
                "MLErgodicityWorker: model {Id} ({Sym}) mu={Mu:F4} timeAvg={TA:F4} gap={G:F4} kelly={K:F4} adjKelly={AK:F4}",
                model.Id, model.Symbol, mu, timeAvg, gap, naiveKelly, adjKelly);
        }
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table,
    /// falling back to <paramref name="defaultValue"/> if the key is absent or
    /// the stored value cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target value type (int, double, string, etc.).</typeparam>
    /// <param name="ctx">EF Core context to query against.</param>
    /// <param name="key">The EngineConfig key to look up.</param>
    /// <param name="defaultValue">Value to return when the key is missing or invalid.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed config value or <paramref name="defaultValue"/>.</returns>
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
