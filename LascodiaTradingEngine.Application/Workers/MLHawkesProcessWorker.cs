using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Fits Hawkes process kernel parameters (μ, α, β) for each active symbol/timeframe
/// using maximum likelihood estimation on recent trade signal timestamps (Rec #32).
/// </summary>
/// <remarks>
/// The conditional intensity of a Hawkes process is:
///   λ(t) = μ + α × Σ_{i: t_i &lt; t} exp(−β × (t − t_i))
/// MLE via gradient ascent on the log-likelihood:
///   LL = Σ_i log λ(t_i) − ∫_0^T λ(t) dt
///   ∫_0^T λ(t) dt = μT + (α/β) Σ_i (1 − exp(−β(T − t_i)))
/// Runs daily. Stores fitted parameters in <see cref="MLHawkesKernelParams"/>.
/// </remarks>
public sealed class MLHawkesProcessWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLHawkesProcessWorker> _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per daily fitting cycle so scoped EF Core
    /// contexts are correctly disposed after each run.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLHawkesProcessWorker(IServiceScopeFactory scopeFactory, ILogger<MLHawkesProcessWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Executes immediately on startup then re-runs every
    /// 24 hours — the daily cadence aligns with the natural volatility clustering cycle
    /// in intraday FX and ensures the Hawkes parameters track evolving self-excitation.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLHawkesProcessWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLHawkesProcessWorker error"); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    /// <summary>
    /// Core Hawkes fitting cycle. For each active symbol/timeframe pair, loads the last
    /// 30 days of trade signal timestamps and fits the Hawkes kernel parameters (μ, α, β)
    /// via maximum likelihood estimation, then upserts the results into
    /// <see cref="MLHawkesKernelParams"/>.
    /// </summary>
    /// <remarks>
    /// Hawkes process context:
    /// Trade signal arrivals in financial markets are not independent Poisson events —
    /// a signal at time t_i makes subsequent signals more likely in the near future
    /// ("self-excitation"). The univariate Hawkes process models this with:
    ///   λ(t) = μ + α × Σ_{i: t_i &lt; t} exp(−β × (t − t_i))
    /// where μ is the background rate, α controls excitation amplitude, and β controls
    /// the decay rate of excitation. The ratio α/β (&lt; 1 for stationarity) gives the
    /// average number of offspring events triggered by one parent event.
    ///
    /// Downstream use: the fitted parameters are read by the signal scoring path to
    /// adjust position sizing and signal confidence based on current cluster intensity
    /// λ(t_now). A high λ(t_now) means signals are clustering — possibly driven by a
    /// news event — so position sizes may be reduced to control correlated risk.
    /// </remarks>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Collect distinct (symbol, timeframe) pairs that currently have active models.
        // Distinct() prevents fitting the same pair multiple times if multiple models
        // are active for the same symbol/timeframe.
        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Load the last 30 days of trade signal timestamps for this symbol.
            // We only need the timestamp (not the full entity) for MLE fitting.
            var cutoff  = DateTime.UtcNow.AddDays(-30);
            var signals = await readDb.Set<TradeSignal>()
                .Where(s => s.Symbol == model.Symbol && !s.IsDeleted
                         && s.GeneratedAt >= cutoff)
                .OrderBy(s => s.GeneratedAt)
                .Select(s => s.GeneratedAt)
                .ToListAsync(ct);

            // Require at least 20 events for the MLE to converge to a meaningful estimate.
            // With fewer events the variance of the parameter estimates is too high.
            if (signals.Count < 20) continue;

            // Fit Hawkes kernel parameters via gradient ascent MLE.
            var (mu, alpha, beta, ll) = FitHawkesKernel(signals);

            // Skip if the MLE returned NaN (numerical instability, e.g. all timestamps identical).
            if (double.IsNaN(mu) || double.IsNaN(alpha) || double.IsNaN(beta)) continue;

            // Upsert into MLHawkesKernelParams: update in-place if an entry already exists,
            // otherwise insert a new row. This avoids unbounded row accumulation.
            var existing = await writeDb.Set<MLHawkesKernelParams>()
                .FirstOrDefaultAsync(k => k.Symbol == model.Symbol
                                       && k.Timeframe == model.Timeframe
                                       && !k.IsDeleted, ct);
            if (existing != null)
            {
                // Update existing record in-place to preserve the row's creation audit fields.
                existing.Mu            = mu;
                existing.Alpha         = alpha;
                existing.Beta          = beta;
                existing.LogLikelihood = ll;
                existing.FitSamples    = signals.Count;
                existing.FittedAt      = DateTime.UtcNow;
            }
            else
            {
                writeDb.Set<MLHawkesKernelParams>().Add(new MLHawkesKernelParams
                {
                    Symbol         = model.Symbol,
                    Timeframe      = model.Timeframe,
                    Mu             = mu,
                    Alpha          = alpha,
                    Beta           = beta,
                    LogLikelihood  = ll,
                    FitSamples     = signals.Count,
                    FittedAt       = DateTime.UtcNow
                });
            }
            await writeDb.SaveChangesAsync(ct);
            _logger.LogDebug("Hawkes fit {S}/{T}: μ={Mu:F4} α={A:F4} β={B:F4} LL={L:F2}",
                model.Symbol, model.Timeframe, mu, alpha, beta, ll);
        }
    }

    /// <summary>
    /// Fits Hawkes kernel parameters (μ, α, β) via gradient ascent MLE using the
    /// Ogata (1981) recursive formula for efficient log-likelihood computation.
    /// Runs up to 200 iterations and stops early when the log-likelihood stops improving.
    /// </summary>
    /// <remarks>
    /// The Hawkes log-likelihood (Ogata 1981) is:
    ///   LL = Σ_i log λ(t_i) − ∫_0^T λ(t) dt
    ///
    /// The integral has an analytical form for the exponential kernel:
    ///   ∫_0^T λ(t) dt = μT + (α/β) Σ_i (1 − exp(−β(T − t_i)))
    ///
    /// The recursive R_i = Σ_{j &lt; i} exp(−β(t_i − t_j)) is computed via:
    ///   R_i = exp(−β(t_i − t_{i-1})) × (1 + R_{i-1})
    /// which reduces O(n²) computation to O(n).
    ///
    /// Constraints enforced via projection:
    /// - μ &gt; 0 (positive background rate)
    /// - α &gt; 0 (positive excitation)
    /// - β &gt; 0 (positive decay)
    /// - α &lt; β × 0.99 (ensures stationarity: branching ratio α/β &lt; 1)
    /// </remarks>
    /// <param name="timestamps">
    /// Ordered list of event times. Must have at least 3 entries.
    /// </param>
    /// <returns>
    /// Tuple of (μ, α, β, log-likelihood). Returns NaN for all fields if the
    /// timestamp list is too short or numerical instability is encountered.
    /// </returns>
    private static (double Mu, double Alpha, double Beta, double LL) FitHawkesKernel(
        List<DateTime> timestamps)
    {
        if (timestamps.Count < 3) return (double.NaN, double.NaN, double.NaN, double.NaN);

        // Convert absolute timestamps to relative hours since the first event.
        // Working in hours gives numerically stable parameter magnitudes for typical
        // intraday FX signal frequencies.
        var t0 = timestamps[0];
        var ts = timestamps.Select(t => (t - t0).TotalHours).ToArray();
        double T  = ts[^1]; // observation horizon in hours
        int n     = ts.Length;

        // Initialise parameters with data-driven defaults to aid convergence:
        // μ = empirical rate (events per hour), α = 0.3, β = 1.0 (mean reversion ~1 h).
        double mu    = (double)n / T;  // empirical background rate
        double alpha = 0.3;
        double beta  = 1.0;

        double lr  = 1e-4; // conservative learning rate to prevent oscillation
        double prev = double.NegativeInfinity;

        for (int iter = 0; iter < 200; iter++)
        {
            // Compute λ(t_i) and the recursive R_i array via Ogata's formula.
            // R_i accumulates the contribution of all past events to λ(t_i).
            double[] lambda = new double[n];
            double[] R      = new double[n]; // R_i = Σ_{j<i} exp(-β(t_i - t_j))
            R[0] = 0;
            lambda[0] = mu; // no past events at t_0
            for (int i = 1; i < n; i++)
            {
                // Recursive step: multiply previous R by the decay since last event,
                // then add 1 for the self-excitation from event i-1.
                R[i]      = Math.Exp(-beta * (ts[i] - ts[i - 1])) * (1 + R[i - 1]);
                lambda[i] = mu + alpha * R[i];
            }

            // Compute the analytical integral ∫_0^T λ(t) dt.
            double integral = mu * T;
            for (int i = 0; i < n; i++)
                integral += (alpha / beta) * (1 - Math.Exp(-beta * (T - ts[i])));

            // Evaluate log-likelihood: LL = Σ log λ(t_i) - ∫ λ dt.
            // Guard against log(0) with a large negative penalty (-50).
            double ll = 0;
            foreach (var l in lambda)
                ll += l > 0 ? Math.Log(l) : -50;
            ll -= integral;

            // Early stopping: if LL is no longer improving after the burn-in period,
            // the gradient ascent has converged (or is at a local maximum).
            if (ll <= prev && iter > 10) break;
            prev = ll;

            // Compute gradients of the log-likelihood w.r.t. μ, α, β.
            // ∂LL/∂μ = Σ_i (1/λ_i) − T
            // ∂LL/∂α = Σ_i (R_i/λ_i) − (1/β) Σ_i (1 − exp(−β(T−t_i)))
            // ∂LL/∂β ≈ −α Σ_i (R_i/λ_i) + (α/β²) Σ_i (1 − exp(−β(T−t_i)))
            double dMu = 0, dAlpha = 0, dBeta = 0;
            for (int i = 0; i < n; i++)
            {
                if (lambda[i] <= 0) continue;
                dMu    += 1.0 / lambda[i];
                dAlpha += R[i]      / lambda[i];
                dBeta  += -alpha * R[i] / lambda[i];  // approximate partial
            }
            // Subtract the integral gradient terms.
            dMu    -= T;
            dAlpha -= (1.0 / beta) * ts.Sum(ti => 1 - Math.Exp(-beta * (T - ti)));
            dBeta  += (alpha / (beta * beta)) * ts.Sum(ti => 1 - Math.Exp(-beta * (T - ti)));

            // Gradient ascent step with projection to enforce constraints:
            // μ > 0, α > 0, β > 0, and α < β*0.99 (stationarity condition).
            mu    = Math.Max(1e-6, mu    + lr * dMu);
            alpha = Math.Max(1e-6, Math.Min(beta * 0.99, alpha + lr * dAlpha));
            beta  = Math.Max(1e-6, beta  + lr * dBeta);
        }
        return (mu, alpha, beta, prev);
    }
}
