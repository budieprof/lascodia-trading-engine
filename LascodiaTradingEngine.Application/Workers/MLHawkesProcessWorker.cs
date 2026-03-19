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

    public MLHawkesProcessWorker(IServiceScopeFactory scopeFactory, ILogger<MLHawkesProcessWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

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

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            var cutoff  = DateTime.UtcNow.AddDays(-30);
            var signals = await readDb.Set<TradeSignal>()
                .Where(s => s.Symbol == model.Symbol && !s.IsDeleted
                         && s.GeneratedAt >= cutoff)
                .OrderBy(s => s.GeneratedAt)
                .Select(s => s.GeneratedAt)
                .ToListAsync(ct);

            if (signals.Count < 20) continue;

            var (mu, alpha, beta, ll) = FitHawkesKernel(signals);
            if (double.IsNaN(mu) || double.IsNaN(alpha) || double.IsNaN(beta)) continue;

            var existing = await writeDb.Set<MLHawkesKernelParams>()
                .FirstOrDefaultAsync(k => k.Symbol == model.Symbol
                                       && k.Timeframe == model.Timeframe
                                       && !k.IsDeleted, ct);
            if (existing != null)
            {
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
    /// Fits Hawkes kernel via gradient ascent (30 iterations, projected to valid range).
    /// Returns (μ, α, β, log-likelihood).
    /// </summary>
    private static (double Mu, double Alpha, double Beta, double LL) FitHawkesKernel(
        List<DateTime> timestamps)
    {
        if (timestamps.Count < 3) return (double.NaN, double.NaN, double.NaN, double.NaN);

        // Convert to hours since first event
        var t0 = timestamps[0];
        var ts = timestamps.Select(t => (t - t0).TotalHours).ToArray();
        double T  = ts[^1];
        int n     = ts.Length;

        // Initialise with sensible defaults
        double mu    = (double)n / T;  // empirical rate
        double alpha = 0.3;
        double beta  = 1.0;

        double lr  = 1e-4;
        double prev = double.NegativeInfinity;

        for (int iter = 0; iter < 200; iter++)
        {
            // Compute λ(t_i) and gradients via Ogata (1981) recursive formula
            double[] lambda = new double[n];
            double[] R      = new double[n]; // R_i = Σ_{j<i} exp(-β(t_i - t_j))
            R[0] = 0;
            lambda[0] = mu;
            for (int i = 1; i < n; i++)
            {
                R[i]      = Math.Exp(-beta * (ts[i] - ts[i - 1])) * (1 + R[i - 1]);
                lambda[i] = mu + alpha * R[i];
            }

            // Log-likelihood
            double integral = mu * T;
            for (int i = 0; i < n; i++)
                integral += (alpha / beta) * (1 - Math.Exp(-beta * (T - ts[i])));

            double ll = 0;
            foreach (var l in lambda)
                ll += l > 0 ? Math.Log(l) : -50;
            ll -= integral;

            if (ll <= prev && iter > 10) break;
            prev = ll;

            // Gradient ascent step
            double dMu = 0, dAlpha = 0, dBeta = 0;
            for (int i = 0; i < n; i++)
            {
                if (lambda[i] <= 0) continue;
                dMu    += 1.0 / lambda[i];
                dAlpha += R[i]      / lambda[i];
                dBeta  += -alpha * R[i] / lambda[i];  // approximate
            }
            dMu    -= T;
            dAlpha -= (1.0 / beta) * ts.Sum(ti => 1 - Math.Exp(-beta * (T - ti)));
            dBeta  += (alpha / (beta * beta)) * ts.Sum(ti => 1 - Math.Exp(-beta * (T - ti)));

            mu    = Math.Max(1e-6, mu    + lr * dMu);
            alpha = Math.Max(1e-6, Math.Min(beta * 0.99, alpha + lr * dAlpha));
            beta  = Math.Max(1e-6, beta  + lr * dBeta);
        }
        return (mu, alpha, beta, prev);
    }
}
