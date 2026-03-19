using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Runs bivariate Granger causality tests for each of the 29 features of every active ML model.
/// Non-causal features (p ≥ 0.05) are recorded in <see cref="MLCausalFeatureAudit"/> and flagged
/// for masking in the next training run via <c>HyperparamOverrides.DisabledFeatureIndices</c>.
/// </summary>
/// <remarks>
/// Granger causality uses an F-test to check whether lagged values of a feature
/// improve prediction of the mid-price return series beyond an AR(p) baseline.
/// This filters spuriously correlated features whose apparent predictive power is
/// driven by regime coincidence rather than causal structure.
/// Runs weekly per active model; earlier results are soft-deleted before inserting new ones.
/// </remarks>
public class MLCausalFeatureWorker : BackgroundService
{
    private readonly ILogger<MLCausalFeatureWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan _interval     = TimeSpan.FromDays(7);
    private static readonly TimeSpan _initialDelay = TimeSpan.FromMinutes(15);

    /// <summary>Significance threshold for Granger causality (default 0.05).</summary>
    private const double PValueThreshold = 0.05;

    /// <summary>Maximum lag order evaluated (AIC selects up to this).</summary>
    private const int MaxLag = 10;

    /// <summary>Number of recent prediction log outcomes to use for the return series.</summary>
    private const int SeriesLength = 200;

    public MLCausalFeatureWorker(
        ILogger<MLCausalFeatureWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLCausalFeatureWorker starting");
        await Task.Delay(_initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "MLCausalFeatureWorker cycle failed"); }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb       = readCtx.GetDbContext();
        var writeDb      = writeCtx.GetDbContext();

        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            await AuditModelAsync(model, readDb, writeDb, ct);
        }
    }

    private async Task AuditModelAsync(
        MLModel model,
        Microsoft.EntityFrameworkCore.DbContext readDb,
        Microsoft.EntityFrameworkCore.DbContext writeDb,
        CancellationToken ct)
    {
        // Load recent resolved prediction logs for the return series
        var logs = await readDb.Set<MLModelPredictionLog>()
            .Where(p => p.MLModelId == model.Id
                     && p.ActualMagnitudePips != null
                     && !p.IsDeleted)
            .OrderByDescending(p => p.PredictedAt)
            .Take(SeriesLength)
            .Select(p => (double)(p.ActualMagnitudePips ?? 0))
            .ToListAsync(ct);

        if (logs.Count < 50)
        {
            _logger.LogDebug("Skipping Granger test for model {Id} — only {N} resolved logs", model.Id, logs.Count);
            return;
        }

        // Load candles for feature reconstruction
        var candles = await readDb.Set<Candle>()
            .Where(c => c.Symbol    == model.Symbol
                     && c.Timeframe == model.Timeframe
                     && !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .Take(SeriesLength + MLFeatureHelper.LookbackWindow + 5)
            .OrderBy(c => c.Timestamp)
            .ToListAsync(ct);

        if (candles.Count < MLFeatureHelper.LookbackWindow + 2) return;

        var samples = MLFeatureHelper.BuildTrainingSamples(candles);
        if (samples.Count < 50) return;

        // Soft-delete existing audits for this model
        var existing = await writeDb.Set<MLCausalFeatureAudit>()
            .Where(a => a.MLModelId == model.Id && !a.IsDeleted)
            .ToListAsync(ct);

        foreach (var a in existing) a.IsDeleted = true;

        var returnSeries = samples.Select(s => (double)s.Magnitude).ToArray();

        for (int fi = 0; fi < MLFeatureHelper.FeatureCount; fi++)
        {
            var featureSeries = samples.Select(s => (double)s.Features[fi]).ToArray();
            int bestLag   = SelectLagByAic(returnSeries, featureSeries, MaxLag);
            var (fStat, pValue) = GrangerFTest(returnSeries, featureSeries, bestLag);

            writeDb.Set<MLCausalFeatureAudit>().Add(new MLCausalFeatureAudit
            {
                MLModelId          = model.Id,
                Symbol             = model.Symbol,
                Timeframe          = model.Timeframe,
                FeatureIndex       = fi,
                FeatureName        = fi < MLFeatureHelper.FeatureNames.Length
                                     ? MLFeatureHelper.FeatureNames[fi] : $"Feature_{fi}",
                GrangerFStat       = (decimal)fStat,
                GrangerPValue      = (decimal)pValue,
                LagOrder           = bestLag,
                IsCausal           = pValue < PValueThreshold,
                IsMaskedForTraining = false,
                ComputedAt         = DateTime.UtcNow
            });
        }

        await writeDb.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Granger audit complete for model {Id} ({Symbol}/{Timeframe}): {Causal}/{Total} causal features",
            model.Id, model.Symbol, model.Timeframe,
            samples.Count > 0 ? "?" : "0", MLFeatureHelper.FeatureCount);
    }

    // ── Granger F-test ────────────────────────────────────────────────────────

    /// <summary>
    /// Bivariate Granger F-test. Returns (F-stat, approximate p-value).
    /// Restricted model: AR(p) on y. Unrestricted: AR(p) on y + lagged x.
    /// F = ((RSS_R - RSS_U)/q) / (RSS_U/(n-2p-1))
    /// p-value approximated via F-distribution CDF.
    /// </summary>
    private static (double fStat, double pValue) GrangerFTest(double[] y, double[] x, int lag)
    {
        int n = Math.Min(y.Length, x.Length) - lag;
        if (n <= 2 * lag + 1) return (0, 1);

        double rssR = ComputeRss(y, x, lag, includeX: false);
        double rssU = ComputeRss(y, x, lag, includeX: true);

        if (rssR <= 0 || rssU <= 0) return (0, 1);

        double fStat = ((rssR - rssU) / lag) / (rssU / (n - 2 * lag - 1));
        fStat = Math.Max(fStat, 0);

        // Approximate p-value via Wilson-Hilferty transform on F(lag, n-2*lag-1)
        double d1 = lag, d2 = n - 2 * lag - 1;
        if (d2 <= 0) return (fStat, 1.0);

        double x2 = fStat * d1;
        double chi2 = x2 * (1 - 2.0 / (9 * d1)) / Math.Sqrt(2.0 / (9 * d1));
        double pValue = 1.0 - NormalCdf(chi2);

        return (fStat, Math.Clamp(pValue, 0, 1));
    }

    private static double ComputeRss(double[] y, double[] x, int lag, bool includeX)
    {
        int n = Math.Min(y.Length, x.Length) - lag;
        if (n <= 0) return double.MaxValue;

        // Build design matrix [1, y_lag1..lagP, (x_lag1..lagP if includeX)]
        int cols = 1 + lag + (includeX ? lag : 0);
        double[,] X = new double[n, cols];
        double[] Y  = new double[n];

        for (int i = 0; i < n; i++)
        {
            int t = i + lag;
            Y[i]    = y[t];
            X[i, 0] = 1; // intercept
            for (int l = 1; l <= lag; l++)
                X[i, l] = y[t - l];
            if (includeX)
                for (int l = 1; l <= lag; l++)
                    X[i, lag + l] = x[t - l];
        }

        var beta = OlsSolve(X, Y, n, cols);
        double rss = 0;
        for (int i = 0; i < n; i++)
        {
            double pred = 0;
            for (int j = 0; j < cols; j++) pred += X[i, j] * beta[j];
            double res = Y[i] - pred;
            rss += res * res;
        }
        return rss;
    }

    private static double[] OlsSolve(double[,] X, double[] y, int n, int cols)
    {
        // Normal equations: (X'X)β = X'y  — small enough for direct solve
        var xtx = new double[cols, cols];
        var xty = new double[cols];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                xty[j] += X[i, j] * y[i];
                for (int k = 0; k < cols; k++)
                    xtx[j, k] += X[i, j] * X[i, k];
            }
        }
        // Ridge regularisation for numerical stability
        for (int j = 0; j < cols; j++) xtx[j, j] += 1e-6;
        return SolveLinear(xtx, xty, cols);
    }

    private static double[] SolveLinear(double[,] A, double[] b, int n)
    {
        var x = new double[n];
        // Gauss-Jordan elimination
        var mat = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) mat[i, j] = A[i, j];
            mat[i, n] = b[i];
        }
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(mat[row, col]) > Math.Abs(mat[pivot, col])) pivot = row;
            for (int j = 0; j <= n; j++) (mat[col, j], mat[pivot, j]) = (mat[pivot, j], mat[col, j]);
            double div = mat[col, col];
            if (Math.Abs(div) < 1e-12) continue;
            for (int j = col; j <= n; j++) mat[col, j] /= div;
            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                double factor = mat[row, col];
                for (int j = col; j <= n; j++) mat[row, j] -= factor * mat[col, j];
            }
        }
        for (int i = 0; i < n; i++) x[i] = mat[i, n];
        return x;
    }

    private static int SelectLagByAic(double[] y, double[] x, int maxLag)
    {
        int best = 1;
        double bestAic = double.MaxValue;
        for (int lag = 1; lag <= maxLag; lag++)
        {
            int n = Math.Min(y.Length, x.Length) - lag;
            if (n <= 2 * lag + 1) break;
            double rss = ComputeRss(y, x, lag, includeX: true);
            int k = 1 + 2 * lag;
            double aic = n * Math.Log(rss / n) + 2 * k;
            if (aic < bestAic) { bestAic = aic; best = lag; }
        }
        return best;
    }

    private static double NormalCdf(double z)
    {
        // Abramowitz and Stegun approximation
        if (z < -8) return 0;
        if (z >  8) return 1;
        double p = 0.5 * (1 + Erf(z / Math.Sqrt(2)));
        return p;
    }

    private static double Erf(double x)
    {
        const double a1 =  0.254829592, a2 = -0.284496736, a3 =  1.421413741;
        const double a4 = -1.453152027, a5 =  1.061405429, p  =  0.3275911;
        double sign = x >= 0 ? 1 : -1;
        x = Math.Abs(x);
        double t = 1 / (1 + p * x);
        double y = 1 - (a1*t + a2*t*t + a3*t*t*t + a4*t*t*t*t + a5*t*t*t*t*t) * Math.Exp(-x*x);
        return sign * y;
    }
}
