using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes the Kelly Criterion optimal position-sizing fraction (f*) for each active
/// ML model using simulated returns from recent candle data. Suppresses models with
/// negative expected value (f* &lt; 0) and caps the position fraction at 25%.
/// Runs every 24 hours.
/// </summary>
/// <remarks>
/// <b>Kelly Criterion background:</b>
/// The Kelly Criterion (Kelly 1956) is a position-sizing formula that maximises the
/// expected logarithm of wealth, equivalent to maximising the long-run geometric growth
/// rate. For a binary win/loss game:
///   f* = (p × b − q) / b
/// where:
/// <list type="bullet">
///   <item>p = win probability (fraction of trades that are profitable)</item>
///   <item>q = 1 − p = loss probability</item>
///   <item>b = mean_win / mean_loss (win-to-loss magnitude ratio)</item>
/// </list>
///
/// <b>Half-Kelly:</b> Full Kelly produces high volatility because estimation error in
/// p and b causes the actual fraction to overshoot. The worker stores
/// <c>HalfKelly = 0.5 × f*</c> as the recommended position fraction, which halves the
/// theoretical variance while retaining ~75% of expected geometric growth
/// (MacLean et al., 2010).
///
/// <b>Negative EV suppression:</b> When f* &lt; 0 the model has negative expected
/// geometric growth rate. The model is flagged <c>IsSuppressed = true</c> so the signal
/// pipeline ignores its outputs until the next successful retrain.
///
/// <b>Polling interval:</b> 24 hours. Daily computation uses a 60-day candle lookback
/// for statistical stability while reflecting recent market conditions.
///
/// <b>ML lifecycle contribution:</b> Provides a final risk-adjusted position sizing
/// gate before signals reach the order execution layer.
/// </remarks>
public sealed class MLKellyFractionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLKellyFractionWorker> _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per daily computation cycle so scoped EF Core
    /// contexts are correctly disposed after each pass.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLKellyFractionWorker(IServiceScopeFactory scopeFactory, ILogger<MLKellyFractionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Executes immediately on startup then re-runs every
    /// 24 hours to recompute Kelly fractions for all active models.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLKellyFractionWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLKellyFractionWorker error"); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    /// <summary>
    /// Core Kelly computation cycle. Simulates trade returns from a 60-day candle
    /// history using each model's first learner weights, then computes and persists
    /// the Kelly fraction and Half-Kelly to <c>MLKellyFractionLog</c>. Models with
    /// negative expected value are suppressed.
    /// </summary>
    /// <remarks>
    /// Simulation methodology:
    /// <list type="number">
    ///   <item>
    ///     Load up to 60 days of candles for the model's symbol/timeframe.
    ///     Skip models with fewer than 20 candles.
    ///   </item>
    ///   <item>
    ///     Deserialise the model snapshot and extract the first learner's weight vector
    ///     (index 0) as a signal proxy for the full ensemble direction.
    ///   </item>
    ///   <item>
    ///     For each training sample, compute the signal from the dot product of weights
    ///     and features (+1 = Buy, −1 = Sell). Multiply by the next-bar close return
    ///     to get signed P&amp;L. Accumulate wins and losses separately.
    ///   </item>
    ///   <item>
    ///     Compute p, q, b, f* and Half-Kelly. Cap at ±25%.
    ///   </item>
    ///   <item>
    ///     Suppress the model if f* &lt; 0. Persist results to <c>MLKellyFractionLog</c>.
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Exclude meta-learners and MAML initialisers — they have no direct candle history.
        var models = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer
                     && m.ModelBytes != null)
            .ToListAsync(ct);

        foreach (var model in models)
        {
            // 60-day candle lookback: recent enough to reflect current regime,
            // long enough for stable win-rate and win/loss ratio estimates.
            var candles = await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe
                         && c.Timestamp >= DateTime.UtcNow.AddDays(-60) && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            if (candles.Count < 20) continue;

            ModelSnapshot? snap;
            try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
            catch { continue; }
            if (snap?.Weights == null || snap.Weights.Length == 0) continue;

            // Use first learner's weight vector as a directional signal proxy.
            double[] weights = snap.Weights[0];
            var samples = MLFeatureHelper.BuildTrainingSamples(candles);
            if (samples.Count < 10) continue;

            var wins   = new List<double>(); // magnitudes of profitable predictions
            var losses = new List<double>(); // magnitudes (absolute) of losing predictions

            for (int i = 0; i < samples.Count - 1; i++)
            {
                var s = samples[i];

                // Compute the linear model score: positive = Buy signal, negative = Sell.
                double dot = 0;
                for (int j = 0; j < weights.Length && j < s.Features.Length; j++)
                    dot += weights[j] * s.Features[j];
                int signal = dot >= 0 ? 1 : -1;

                // Signed next-bar return: positive if the signal direction was correct.
                double nextClose = (double)candles[i + 1].Close;
                double thisClose = (double)candles[i].Close;
                double ret = (nextClose - thisClose) / (thisClose + 1e-8) * signal;

                if (ret > 0) wins.Add(ret);
                else         losses.Add(Math.Abs(ret));
            }

            // Require at least one win and one loss to compute a meaningful b ratio.
            if (wins.Count == 0 || losses.Count == 0) continue;

            // Kelly formula: f* = (p × b − q) / b
            double p     = (double)wins.Count / (wins.Count + losses.Count); // win probability
            double q     = 1 - p;                                             // loss probability
            double b     = wins.Average() / (losses.Average() + 1e-8);       // win/loss ratio
            double fStar = (p * b - q) / (b + 1e-8);                         // full Kelly fraction

            // Half-Kelly: conservative sizing that halves variance while retaining
            // most of the geometric growth benefit. Capped at ±25% of account equity.
            double halfKelly = Math.Clamp(0.5 * fStar, -0.25, 0.25);

            // Negative expected value flag: if f* < 0, the model loses money on average.
            bool negEv = fStar < 0;

            // Persist Kelly computation as an audit log record.
            writeDb.Set<MLKellyFractionLog>().Add(new MLKellyFractionLog
            {
                MLModelId     = model.Id,
                Symbol        = model.Symbol,
                Timeframe     = model.Timeframe.ToString(),
                KellyFraction = Math.Clamp(fStar, -0.25, 0.25), // capped full Kelly
                HalfKelly     = halfKelly,                        // recommended position fraction
                WinRate       = p,
                WinLossRatio  = b,
                NegativeEV    = negEv,
                ComputedAt    = DateTime.UtcNow
            });

            // Suppress the model if it has negative EV to prevent live trading on a
            // geometrically destructive model. The suppression is lifted on the next
            // successful retrain which resets IsSuppressed = false.
            if (negEv)
            {
                var tracked = await writeDb.Set<MLModel>().FindAsync(new object[] { model.Id }, ct);
                if (tracked != null) tracked.IsSuppressed = true;
            }

            await writeDb.SaveChangesAsync(ct);

            _logger.LogInformation(
                "MLKellyFractionWorker: {S}/{T} f*={F:F4} halfKelly={H:F4} winRate={P:F3} b={B:F3} negEV={N}",
                model.Symbol, model.Timeframe, fStar, halfKelly, p, b, negEv);
        }
    }
}
