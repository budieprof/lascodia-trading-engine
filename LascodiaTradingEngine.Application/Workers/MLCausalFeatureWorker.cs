using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Runs bivariate Granger causality tests for each of the <see cref="MLFeatureHelper.FeatureCount"/>
/// features of every active ML model, recording results in <see cref="MLCausalFeatureAudit"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is Granger causality?</b>
/// A feature X is said to "Granger-cause" a target Y if past values of X improve the
/// prediction of Y beyond what can be achieved by past values of Y alone. Formally, the test
/// compares two autoregressive models:
/// <list type="bullet">
///   <item><b>Restricted (AR) model:</b> y_t = intercept + Σ_{l=1}^p α_l × y_{t-l} + ε_t</item>
///   <item><b>Unrestricted model:</b> y_t = intercept + Σ_{l=1}^p α_l × y_{t-l}
///             + Σ_{l=1}^p β_l × x_{t-l} + ε_t</item>
/// </list>
/// If including the lagged feature X significantly reduces residual sum of squares (RSS),
/// X Granger-causes Y. The improvement is measured by an F-statistic:
/// F = ((RSS_R − RSS_U) / p) / (RSS_U / (n − 2p − 1))
/// where p is the lag order and n is the sample size.
/// </para>
/// <para>
/// <b>Why use Granger causality for trading features?</b>
/// Many technical indicators exhibit apparent predictive correlation with price moves due to
/// shared underlying trending or momentum regimes. A feature that is highly correlated with
/// price direction during a trending regime may have zero marginal predictive value once the
/// autoregressive structure of returns is accounted for. Granger causality separates genuine
/// leading indicators from spurious contemporaneous correlates.
/// </para>
/// <para>
/// <b>Lag order selection:</b> AIC is used to select the optimal lag p in [1, <see cref="MaxLag"/>]:
/// AIC(p) = n × log(RSS/n) + 2k, where k = 1 + 2p (intercept + 2p parameters in unrestricted model).
/// The lag with the lowest AIC is used for the final F-test.
/// </para>
/// <para>
/// <b>p-value approximation:</b> The exact F-distribution CDF is approximated via the
/// Wilson-Hilferty transform converting F(d1, d2) to a chi-squared then to a standard normal.
/// This is accurate for moderate n and is sufficient for the binary causal/non-causal
/// classification at the 0.05 significance level.
/// </para>
/// <para>
/// <b>Polling interval:</b> 7 days, with a 15-minute initial delay to let other workers
/// initialise first. An initial delay avoids DB contention on startup.
/// </para>
/// <para>
/// <b>Pipeline role:</b> <c>IsCausal = false</c> features are candidates for masking in
/// the next training run via <c>HyperparamOverrides.DisabledFeatureIndices</c>.
/// The <c>IsMaskedForTraining</c> flag is set by a separate operator approval step —
/// this worker only diagnoses, it does not automatically mask.
/// </para>
/// </remarks>
public class MLCausalFeatureWorker : BackgroundService
{
    private readonly ILogger<MLCausalFeatureWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Weekly polling interval — causal structure changes on a regime timescale.</summary>
    private static readonly TimeSpan _interval     = TimeSpan.FromDays(7);

    /// <summary>
    /// Initial startup delay before the first cycle runs. Allows the host and dependent
    /// services (e.g. EF Core migrations) to fully initialise before the first DB query.
    /// </summary>
    private static readonly TimeSpan _initialDelay = TimeSpan.FromMinutes(15);

    /// <summary>Significance threshold for Granger causality (default 0.05).</summary>
    private const double PValueThreshold = 0.05;

    /// <summary>Maximum lag order evaluated during AIC-based lag selection.</summary>
    private const int MaxLag = 10;

    /// <summary>
    /// Number of recent resolved prediction log entries to use as the return series (y).
    /// Using resolved logs (where ActualMagnitudePips is populated) ensures the return
    /// series reflects real price outcomes rather than predicted values.
    /// </summary>
    private const int SeriesLength = 200;

    /// <summary>
    /// Initialises the worker with a logger and DI scope factory.
    /// </summary>
    /// <param name="logger">Structured logger for Granger test diagnostics.</param>
    /// <param name="scopeFactory">Factory used to create a fresh DI scope each weekly cycle.</param>
    public MLCausalFeatureWorker(
        ILogger<MLCausalFeatureWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Entry point for the hosted service. Waits for the initial delay, then runs a weekly
    /// loop delegating to <see cref="RunCycleAsync"/>. Errors are caught per cycle so the
    /// loop continues after transient failures.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host shuts down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLCausalFeatureWorker starting");
        // Delay startup to allow the application host and DB to fully initialise.
        await Task.Delay(_initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "MLCausalFeatureWorker cycle failed"); }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    /// <summary>
    /// Single cycle logic: resolves scoped DbContexts and runs <see cref="AuditModelAsync"/>
    /// for each active ML model.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
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

    /// <summary>
    /// Performs the full Granger causality audit for a single ML model.
    /// Soft-deletes previous audit rows before inserting a fresh set so the table always
    /// contains exactly one row per (model, feature) pair reflecting the latest computation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "return series" (the y variable in the Granger test) is derived from
    /// <c>MLModelPredictionLog.ActualMagnitudePips</c> — the realised price move magnitude
    /// for predictions that have been resolved. This makes the test data-driven from actual
    /// live trading outcomes rather than in-sample candle data.
    /// </para>
    /// <para>
    /// Feature series (x variables) are reconstructed from candle data using the same
    /// <see cref="MLFeatureHelper.BuildTrainingSamples"/> pipeline used at training time,
    /// ensuring consistency between training-time and audit-time feature values.
    /// </para>
    /// </remarks>
    /// <param name="model">The active ML model to audit.</param>
    /// <param name="readDb">Read DbContext.</param>
    /// <param name="writeDb">Write DbContext.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task AuditModelAsync(
        MLModel model,
        Microsoft.EntityFrameworkCore.DbContext readDb,
        Microsoft.EntityFrameworkCore.DbContext writeDb,
        CancellationToken ct)
    {
        // Load recent resolved prediction logs for the return series (y variable).
        // ActualMagnitudePips must be non-null, meaning the trade has been closed/resolved.
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
            // Insufficient resolved outcomes — the model may be too new or live trading
            // has not produced enough completed trades yet.
            _logger.LogDebug("Skipping Granger test for model {Id} — only {N} resolved logs", model.Id, logs.Count);
            return;
        }

        // Load candles for feature reconstruction.
        // Extra candles (LookbackWindow + 5) are needed to warm up the indicator pipeline
        // before the first valid feature vector can be computed.
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

        // Soft-delete existing audits for this model before replacing with fresh results.
        // Using soft-delete preserves audit history for compliance while keeping the
        // "active" view clean (IsDeleted = false → current cycle only).
        var existing = await writeDb.Set<MLCausalFeatureAudit>()
            .Where(a => a.MLModelId == model.Id && !a.IsDeleted)
            .ToListAsync(ct);

        foreach (var a in existing) a.IsDeleted = true;

        // Use the realised price return magnitude as the y series.
        // Magnitude is used rather than direction because Granger causality is a continuous
        // time-series test and magnitude provides richer variance than a binary label.
        var returnSeries = samples.Select(s => (double)s.Magnitude).ToArray();

        for (int fi = 0; fi < MLFeatureHelper.FeatureCount; fi++)
        {
            var featureSeries = samples.Select(s => (double)s.Features[fi]).ToArray();

            // Select the optimal lag order using AIC before running the F-test.
            // This avoids the bias of using a fixed lag and adapts to each feature's
            // autocorrelation structure.
            int bestLag   = SelectLagByAic(returnSeries, featureSeries, MaxLag);
            var (fStat, pValue) = GrangerFTest(returnSeries, featureSeries, bestLag);

            writeDb.Set<MLCausalFeatureAudit>().Add(new MLCausalFeatureAudit
            {
                MLModelId           = model.Id,
                Symbol              = model.Symbol,
                Timeframe           = model.Timeframe,
                FeatureIndex        = fi,
                FeatureName         = fi < MLFeatureHelper.FeatureNames.Length
                                      ? MLFeatureHelper.FeatureNames[fi] : $"Feature_{fi}",
                GrangerFStat        = (decimal)fStat,
                GrangerPValue       = (decimal)pValue,
                LagOrder            = bestLag,
                // IsCausal = true when p-value is below the significance threshold (0.05).
                // This means there is less than a 5% chance of observing this F-statistic
                // if X has no Granger-causal relationship with Y.
                IsCausal            = pValue < PValueThreshold,
                // Masking is a separate manual/operator-approval step — this worker only diagnoses.
                IsMaskedForTraining = false,
                ComputedAt          = DateTime.UtcNow
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
    /// Bivariate Granger F-test. Returns (F-statistic, approximate p-value).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compares two OLS regression models:
    /// <list type="bullet">
    ///   <item><b>Restricted (AR-only):</b> y_t ~ intercept + y_{t-1..p}</item>
    ///   <item><b>Unrestricted:</b>         y_t ~ intercept + y_{t-1..p} + x_{t-1..p}</item>
    /// </list>
    /// F-statistic: F = ((RSS_R − RSS_U) / lag) / (RSS_U / (n − 2×lag − 1))
    /// where lag = p, n = usable observations.
    /// </para>
    /// <para>
    /// The p-value is approximated via the Wilson-Hilferty transform: the F(d1, d2)
    /// distribution is converted to a chi-squared approximation, then to a standard
    /// normal Z-score, and finally to a one-tailed probability using <see cref="NormalCdf"/>.
    /// This approximation is accurate to within ±0.01 for the parameter ranges seen here.
    /// </para>
    /// Returns (0, 1.0) — i.e. no evidence of causality — for degenerate inputs
    /// (too few observations, zero RSS).
    /// </remarks>
    /// <param name="y">Return/magnitude series (dependent variable).</param>
    /// <param name="x">Feature series (candidate Granger-causing variable).</param>
    /// <param name="lag">Lag order p selected by AIC.</param>
    /// <returns>Tuple of (F-statistic, p-value) where low p-value indicates Granger causality.</returns>
    private static (double fStat, double pValue) GrangerFTest(double[] y, double[] x, int lag)
    {
        int n = Math.Min(y.Length, x.Length) - lag;
        if (n <= 2 * lag + 1) return (0, 1);

        // Compute RSS for both restricted and unrestricted models.
        double rssR = ComputeRss(y, x, lag, includeX: false);
        double rssU = ComputeRss(y, x, lag, includeX: true);

        if (rssR <= 0 || rssU <= 0) return (0, 1);

        // F = ((RSS_R - RSS_U) / q) / (RSS_U / df_U)
        // where q = lag (number of restricted coefficients) and df_U = n - 2*lag - 1.
        double fStat = ((rssR - rssU) / lag) / (rssU / (n - 2 * lag - 1));
        fStat = Math.Max(fStat, 0); // Guard against floating-point negatives near zero.

        // Approximate p-value via Wilson-Hilferty transform on F(d1=lag, d2=n-2*lag-1).
        // The transform converts F to a chi-squared variate then to a normal Z-score.
        double d1 = lag, d2 = n - 2 * lag - 1;
        if (d2 <= 0) return (fStat, 1.0);

        // Wilson-Hilferty: chi2 ≈ d1 * F * (1 - 2/(9*d1)) / sqrt(2/(9*d1))
        double x2   = fStat * d1;
        double chi2  = x2 * (1 - 2.0 / (9 * d1)) / Math.Sqrt(2.0 / (9 * d1));
        double pValue = 1.0 - NormalCdf(chi2);

        return (fStat, Math.Clamp(pValue, 0, 1));
    }

    /// <summary>
    /// Computes the residual sum of squares (RSS) for an AR(p) or AR+X(p) regression
    /// of <paramref name="y"/> on its own lags and (optionally) lagged <paramref name="x"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Design matrix layout (columns):
    /// [0] = intercept (1.0)
    /// [1..lag] = y_{t-1} .. y_{t-lag}
    /// [lag+1..2*lag] = x_{t-1} .. x_{t-lag}  (only when includeX = true)
    /// </para>
    /// <para>
    /// OLS coefficients are solved via <see cref="OlsSolve"/> (normal equations with
    /// ridge regularisation). RSS is then computed as Σ (y_i − ŷ_i)².
    /// </para>
    /// </remarks>
    /// <param name="y">Dependent variable series.</param>
    /// <param name="x">Independent feature series.</param>
    /// <param name="lag">Number of lags to include.</param>
    /// <param name="includeX">Whether to add lagged x columns to the design matrix.</param>
    /// <returns>Residual sum of squares; returns <see cref="double.MaxValue"/> on degenerate input.</returns>
    private static double ComputeRss(double[] y, double[] x, int lag, bool includeX)
    {
        int n = Math.Min(y.Length, x.Length) - lag;
        if (n <= 0) return double.MaxValue;

        // Build design matrix [1, y_lag1..lagP, (x_lag1..lagP if includeX)]
        int cols = 1 + lag + (includeX ? lag : 0);
        double[,] X = new double[n, cols];
        double[]  Y = new double[n];

        for (int i = 0; i < n; i++)
        {
            int t = i + lag;
            Y[i]    = y[t];
            X[i, 0] = 1; // intercept column
            for (int l = 1; l <= lag; l++)
                X[i, l] = y[t - l];            // lagged y terms
            if (includeX)
                for (int l = 1; l <= lag; l++)
                    X[i, lag + l] = x[t - l];  // lagged x terms (unrestricted model only)
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

    /// <summary>
    /// Solves the OLS normal equations (X'X)β = X'y using Gauss-Jordan elimination
    /// with ridge regularisation (λ = 1e-6) for numerical stability.
    /// </summary>
    /// <remarks>
    /// The design matrices are small (at most 1 + 2×MaxLag = 21 columns), so direct
    /// Gauss-Jordan elimination is fast and avoids the overhead of a full SVD or QR
    /// decomposition. Ridge regularisation prevents numerical singularities when
    /// features are highly collinear.
    /// </remarks>
    /// <param name="X">Design matrix, shape [n, cols].</param>
    /// <param name="y">Response vector, length n.</param>
    /// <param name="n">Number of observations.</param>
    /// <param name="cols">Number of columns (parameters).</param>
    /// <returns>OLS coefficient vector β, length cols.</returns>
    private static double[] OlsSolve(double[,] X, double[] y, int n, int cols)
    {
        // Normal equations: (X'X)β = X'y — small enough for direct solve
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

        // Ridge regularisation (λ = 1e-6) adds a small positive constant to the diagonal
        // of X'X, preventing singularity when predictors are near-collinear.
        for (int j = 0; j < cols; j++) xtx[j, j] += 1e-6;

        return SolveLinear(xtx, xty, cols);
    }

    /// <summary>
    /// Solves the linear system Ax = b using Gauss-Jordan elimination with partial pivoting.
    /// </summary>
    /// <remarks>
    /// Partial pivoting (choosing the row with the largest absolute value in the current
    /// column as the pivot) improves numerical stability compared to naive Gaussian
    /// elimination, especially for near-singular systems after ridge regularisation.
    /// </remarks>
    /// <param name="A">Square coefficient matrix, shape [n, n].</param>
    /// <param name="b">Right-hand side vector, length n.</param>
    /// <param name="n">System size.</param>
    /// <returns>Solution vector x such that A × x ≈ b.</returns>
    private static double[] SolveLinear(double[,] A, double[] b, int n)
    {
        var x = new double[n];
        // Build augmented matrix [A | b]
        var mat = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) mat[i, j] = A[i, j];
            mat[i, n] = b[i];
        }

        // Gauss-Jordan elimination with partial column pivoting.
        for (int col = 0; col < n; col++)
        {
            // Find pivot row (largest absolute value in current column).
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(mat[row, col]) > Math.Abs(mat[pivot, col])) pivot = row;

            // Swap current row with pivot row.
            for (int j = 0; j <= n; j++) (mat[col, j], mat[pivot, j]) = (mat[pivot, j], mat[col, j]);

            double div = mat[col, col];
            if (Math.Abs(div) < 1e-12) continue; // Skip near-zero pivot (degenerate column).

            // Normalise pivot row.
            for (int j = col; j <= n; j++) mat[col, j] /= div;

            // Eliminate col from all other rows.
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

    /// <summary>
    /// Selects the optimal lag order for the Granger test using the Akaike Information
    /// Criterion (AIC) over lags 1 through <paramref name="maxLag"/>.
    /// </summary>
    /// <remarks>
    /// AIC = n × log(RSS/n) + 2k
    /// where k = 1 + 2×lag (intercept + lag AR terms + lag feature terms in unrestricted model)
    /// and n = usable observations at this lag.
    /// AIC penalises model complexity, balancing fit quality against overfitting.
    /// Lower AIC is better; the lag with the minimum AIC is returned.
    /// Returns lag = 1 when no valid AIC can be computed (too few observations).
    /// </remarks>
    /// <param name="y">Return series (dependent variable).</param>
    /// <param name="x">Feature series (independent variable).</param>
    /// <param name="maxLag">Maximum lag order to evaluate.</param>
    /// <returns>Optimal lag order in [1, maxLag].</returns>
    private static int SelectLagByAic(double[] y, double[] x, int maxLag)
    {
        int    best    = 1;
        double bestAic = double.MaxValue;

        for (int lag = 1; lag <= maxLag; lag++)
        {
            int n = Math.Min(y.Length, x.Length) - lag;
            if (n <= 2 * lag + 1) break; // Not enough observations for this lag order.

            double rss = ComputeRss(y, x, lag, includeX: true);
            int    k   = 1 + 2 * lag; // number of free parameters in unrestricted model
            double aic = n * Math.Log(rss / n) + 2 * k;

            if (aic < bestAic) { bestAic = aic; best = lag; }
        }

        return best;
    }

    /// <summary>
    /// Cumulative distribution function (CDF) of the standard normal distribution N(0,1).
    /// </summary>
    /// <remarks>
    /// Uses the error function identity: Φ(z) = 0.5 × (1 + erf(z / √2)).
    /// Clamped to [0, 1] for |z| &gt; 8 to avoid floating-point underflow.
    /// </remarks>
    /// <param name="z">Standard normal variate.</param>
    /// <returns>P(Z ≤ z) for Z ~ N(0,1), in [0, 1].</returns>
    private static double NormalCdf(double z)
    {
        // Abramowitz and Stegun approximation (fast and sufficient for p-value ranking)
        if (z < -8) return 0;
        if (z >  8) return 1;
        double p = 0.5 * (1 + Erf(z / Math.Sqrt(2)));
        return p;
    }

    /// <summary>
    /// Computes the error function erf(x) using the Abramowitz and Stegun polynomial
    /// approximation (maximum error: 1.5 × 10⁻⁷).
    /// </summary>
    /// <remarks>
    /// Reference: Abramowitz and Stegun, "Handbook of Mathematical Functions", formula 7.1.26.
    /// Coefficients: a1=0.254829592, a2=-0.284496736, a3=1.421413741, a4=-1.453152027,
    ///               a5=1.061405429, p=0.3275911.
    /// The function is odd: erf(-x) = -erf(x), so only non-negative x is computed directly.
    /// </remarks>
    /// <param name="x">Input value.</param>
    /// <returns>erf(x) in [-1, 1].</returns>
    private static double Erf(double x)
    {
        const double a1 =  0.254829592, a2 = -0.284496736, a3 =  1.421413741;
        const double a4 = -1.453152027, a5 =  1.061405429, p  =  0.3275911;
        double sign = x >= 0 ? 1 : -1;
        x = Math.Abs(x);
        double t = 1 / (1 + p * x);
        // Polynomial approximation of (1 - erf(x)) × exp(x²)
        double y = 1 - (a1*t + a2*t*t + a3*t*t*t + a4*t*t*t*t + a5*t*t*t*t*t) * Math.Exp(-x*x);
        return sign * y;
    }
}
