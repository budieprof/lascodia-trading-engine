using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Applies the PELT (Pruned Exact Linear Time) algorithm to detect multiple structural
/// change points in the sequence of log-returns for each active ML model's traded symbol.
/// Runs every 24 hours.
///
/// <para>
/// <b>Why change-point detection on price returns?</b><br/>
/// ML trading models are trained on a specific statistical regime of the market (volatility,
/// autocorrelation, correlation structure). When the return-generating process itself changes —
/// e.g., after a central-bank intervention, a new volatility regime, or a structural shift
/// in market microstructure — the model's features and calibration become stale. Detecting
/// changes in the price process is therefore an early-warning signal that the model's
/// training distribution has become non-stationary, often before the prediction accuracy
/// metrics visibly degrade.
/// </para>
///
/// <para>
/// <b>The PELT algorithm:</b><br/>
/// PELT solves the penalised cost minimisation problem:
/// <code>
///   argmin_{τ₁,...,τₖ} [ Σᵢ C(y_{τᵢ+1:τᵢ₊₁}) + k·β ]
/// </code>
/// Where:
/// <list type="bullet">
///   <item><c>C(·)</c> — segment cost function (Gaussian negative log-likelihood for each segment).</item>
///   <item><c>β</c> — penalty per change point, set to <c>ln(n)</c> (the BIC criterion). This automatically balances model complexity against fit quality.</item>
///   <item><c>k</c> — number of change points; determined automatically by the optimisation.</item>
/// </list>
/// PELT extends the classic dynamic programming solution with a pruning rule: any candidate
/// partition that provably cannot be part of the optimal solution is removed from the search space.
/// This reduces average complexity from O(n²) (Binary Segmentation) to O(n) amortised — making
/// it feasible to run nightly on 90 days of candle data.
/// </para>
///
/// <para>
/// <b>Cost function — Gaussian negative log-likelihood:</b><br/>
/// For a segment of returns y_{s+1:t} with length n = t−s:
/// <code>
///   C(s, t) = n · (ln(σ̂²) + 1)
/// </code>
/// Where σ̂² is the sample variance of the returns in [s+1, t]. This is derived by substituting
/// the MLE of the Gaussian parameters (μ̂ = sample mean, σ̂² = sample variance) into the
/// negative log-likelihood of the normal distribution. See <see cref="SegmentCost"/>.
/// </para>
///
/// <para>
/// <b>BIC penalty:</b><br/>
/// <c>β = ln(n)</c> is the Bayesian Information Criterion (BIC) penalty per change point.
/// The BIC trades off goodness-of-fit against model complexity; it tends to select a more
/// parsimonious number of change points compared to AIC (<c>β = 2</c>). Using BIC means
/// the algorithm will only declare a change point if the reduction in cost clearly justifies
/// adding another segment boundary.
/// </para>
///
/// <para>
/// <b>Pruning rule:</b><br/>
/// At each position t, candidate start points τ are pruned from future consideration if:
/// <code>
///   F[τ] + C(τ, t) &gt; F[t]
/// </code>
/// Because adding a further change point at or after t can only increase costs relative to
/// starting from t, any τ satisfying the above inequality cannot lead to the optimal solution.
/// </para>
///
/// <para>
/// <b>Output:</b><br/>
/// Results are written to <c>MLPeltChangePointLog</c> which records the number and positions
/// of detected change points, the total cost F[m], and the BIC penalty. A high change-point
/// count or a recently detected change point in the last 5–10 candles is a strong signal that
/// the model's training distribution has become stale.
/// </para>
///
/// <para>
/// <b>Pipeline position:</b><br/>
/// This worker operates on raw price data (candles) rather than prediction outcomes, giving
/// it a fundamentally different — and often leading — signal compared to the outcome-based
/// detectors (<see cref="MLCusumDriftWorker"/>, <see cref="MLAdwinDriftWorker"/>). It is
/// therefore part of the proactive monitoring layer, complementing the reactive accuracy monitors.
/// </para>
/// </summary>
public sealed class MLPeltChangePointWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLPeltChangePointWorker> _logger;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">
    /// DI scope factory. A fresh scope (and EF Core contexts) is created on every run cycle
    /// to prevent context bloat from tracking large candle sets across multiple days.
    /// </param>
    /// <param name="logger">Structured logger for informational and error output.</param>
    public MLPeltChangePointWorker(IServiceScopeFactory scopeFactory, ILogger<MLPeltChangePointWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point invoked by the .NET hosted-service infrastructure on application start.
    /// Runs the PELT change-point scan once every 24 hours until the application shuts down.
    /// </summary>
    /// <param name="stoppingToken">Graceful-shutdown token from the .NET host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLPeltChangePointWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLPeltChangePointWorker error"); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    /// <summary>
    /// Executes one full PELT change-point detection pass across all active, non-auxiliary models.
    /// For each model, computes log-returns from the last 90 days of candles and runs the PELT
    /// dynamic programming algorithm to find the globally optimal set of change points.
    /// </summary>
    /// <remarks>
    /// Meta-learner and MAML-initializer models are excluded because they are not tied to a
    /// primary symbol/timeframe price series in the same way as base models.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Exclude auxiliary models — they don't own a primary price series.
        var models = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .ToListAsync(ct);

        foreach (var model in models)
        {
            // ── Load candle data for the last 90 days ──────────────────────────
            // 90 days gives enough history to detect multi-week structural shifts while
            // keeping the return series length computationally manageable for O(n) PELT.
            var candles = await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe
                         && c.Timestamp >= DateTime.UtcNow.AddDays(-90) && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            // Require at least 10 candles to compute a meaningful return series
            // (we need n-1 returns from n candles, and PELT needs segments of reasonable length).
            if (candles.Count < 10) continue;

            int n = candles.Count;

            // ── Compute log-returns (close-to-close) ──────────────────────────
            // r_i = (Close_{i+1} - Close_i) / Close_i  (simple return approximation)
            // The 1e-8 additive guard prevents division-by-zero for zero-price candles
            // (which should not occur in real data but may appear in test/seed data).
            double[] returns = new double[n - 1];
            for (int i = 0; i < n - 1; i++)
                returns[i] = ((double)candles[i + 1].Close - (double)candles[i].Close)
                             / ((double)candles[i].Close + 1e-8);

            int m = returns.Length; // Total number of return observations

            // ── BIC penalty per change point ──────────────────────────────────
            // β = ln(m) is the Bayesian Information Criterion penalty for adding one change
            // point to the model. Using BIC (rather than AIC or a fixed penalty) means the
            // algorithm scales the required evidence for each additional change point with
            // the sample size — avoiding over-segmentation on longer series.
            double penalty = Math.Log(m);

            // ── Precompute prefix sums for O(1) segment cost evaluation ───────
            // prefSum[i]  = Σ returns[0..i-1]     (sum of returns up to index i)
            // prefSum2[i] = Σ returns[0..i-1]²    (sum of squared returns up to index i)
            // These allow the sample mean and variance of any segment [s, t) to be
            // computed in O(1) time during the DP inner loop, which is critical for
            // achieving the O(n) amortised complexity of PELT.
            double[] prefSum  = new double[m + 1];
            double[] prefSum2 = new double[m + 1];
            for (int i = 0; i < m; i++)
            {
                prefSum[i + 1]  = prefSum[i]  + returns[i];
                prefSum2[i + 1] = prefSum2[i] + returns[i] * returns[i];
            }

            // ── PELT dynamic programming ───────────────────────────────────────
            // F[t] = minimum total penalised cost for segmenting returns[0..t-1] optimally.
            // prev[t] = the optimal "last change point" before position t.
            // Initialisation: F[0] = -penalty so the DP recurrence can add the penalty
            // for the first segment without an off-by-one adjustment.
            double[] F    = new double[m + 1];
            int[]    prev = new int[m + 1];
            F[0] = -penalty;

            // `candidates` is the set of "live" candidate start points for the current segment.
            // Initially only {0} is a candidate — the series starts at position 0.
            var candidates = new List<int> { 0 };

            for (int t = 1; t <= m; t++)
            {
                // ── DP recurrence: find the best split point τ ≤ t ───────────
                // F[t] = min over τ ∈ candidates { F[τ] + C(τ, t) + β }
                // where C(τ, t) is the Gaussian NLL cost of segment [τ, t).
                double bestCost = double.MaxValue;
                int bestTau = 0;
                foreach (int tau in candidates)
                {
                    double cost = F[tau] + SegmentCost(tau, t, prefSum, prefSum2) + penalty;
                    if (cost < bestCost) { bestCost = cost; bestTau = tau; }
                }
                F[t]    = bestCost;
                prev[t] = bestTau; // Record the optimal predecessor for backtracking

                // ── PELT pruning step ──────────────────────────────────────────
                // After updating F[t], prune candidate start points that can never be
                // optimal for any future position t' > t.
                //
                // Pruning criterion (from Killick et al., 2012):
                // Remove τ from the candidate set if: F[τ] + C(τ, t) > F[t]
                //
                // Intuition: if the cost of the segment [τ, t) plus the optimal cost
                // up to τ already exceeds the current best cost at t (without even adding
                // future segments), then τ cannot lead to the optimal partition for any
                // future t' ≥ t. This is the key pruning inequality that gives PELT its
                // O(n) amortised complexity.
                var pruned = new List<int>();
                foreach (int tau in candidates)
                    if (F[tau] + SegmentCost(tau, t, prefSum, prefSum2) <= F[t])
                        pruned.Add(tau); // Keep τ — it still has a chance to be optimal later
                pruned.Add(t); // Always add t as a candidate for the next step
                candidates = pruned;
            }

            // ── Backtrack to recover the optimal change-point positions ───────
            // Starting from the end of the series (position m), follow the prev[] chain
            // back to position 0 to recover all detected change points.
            // Each prev[cur] is the start of the last segment ending at cur — so it is
            // a change-point boundary. The loop terminates when prev[cur] == 0 (i.e., the
            // final segment reaches back to the very beginning of the series).
            var changePoints = new List<int>();
            int cur = m;
            while (prev[cur] != 0)
            {
                changePoints.Add(prev[cur]);
                cur = prev[cur];
            }
            changePoints.Reverse(); // Put in chronological order (ascending index)

            // ── Persist the result ─────────────────────────────────────────────
            writeDb.Set<MLPeltChangePointLog>().Add(new MLPeltChangePointLog
            {
                MLModelId              = model.Id,
                Symbol                 = model.Symbol,
                Timeframe              = model.Timeframe.ToString(),
                ChangePointCount       = changePoints.Count,
                ChangePointIndicesJson = JsonSerializer.Serialize(changePoints), // Indices into the returns array
                Penalty                = penalty,    // BIC penalty used (ln(m))
                TotalCost              = F[m],        // Optimal total penalised cost for the full series
                ComputedAt             = DateTime.UtcNow
            });

            await writeDb.SaveChangesAsync(ct);

            _logger.LogInformation(
                "MLPeltChangePointWorker: {S}/{T} changePoints={CP} totalCost={Cost:F4} penalty={Pen:F4}",
                model.Symbol, model.Timeframe, changePoints.Count, F[m], penalty);
        }
    }

    /// <summary>
    /// Computes the Gaussian negative log-likelihood cost for the segment of returns spanning
    /// indices <c>[start, end)</c> in the return series. Used as the cost function C(s, t) in
    /// the PELT dynamic programming recurrence.
    /// </summary>
    /// <remarks>
    /// <b>Derivation:</b><br/>
    /// Assume the returns in segment [start, end) are i.i.d. Gaussian: y_i ~ N(μ, σ²).
    /// The maximum-likelihood estimates are:
    /// <code>
    ///   μ̂  = (1/n) Σ yᵢ
    ///   σ̂² = (1/n) Σ (yᵢ − μ̂)² = (1/n) Σ yᵢ² − μ̂²
    /// </code>
    /// Substituting into the negative log-likelihood of the normal distribution and simplifying:
    /// <code>
    ///   NLL = (n/2) · ln(2πσ̂²) + n/2
    ///       = (n/2) · (ln(2π) + ln(σ̂²) + 1)
    /// </code>
    /// Dropping the constant term (n/2)·ln(2π) (which cancels in the minimisation), the
    /// cost function becomes:
    /// <code>
    ///   C(s, t) = n · (ln(σ̂²) + 1)
    /// </code>
    /// This is the value returned by this method. The prefix-sum arrays enable the
    /// segment mean and variance to be computed in O(1) time per call.
    ///
    /// <b>Numerical guard:</b><br/>
    /// If the computed variance is non-positive (can occur for perfectly flat segments,
    /// e.g., all returns are identical), it is clamped to 1e-10 to avoid ln(0) = −∞.
    /// </remarks>
    /// <param name="start">Inclusive start index into the prefix-sum arrays.</param>
    /// <param name="end">Exclusive end index (i.e., the segment covers returns[start..end-1]).</param>
    /// <param name="prefSum">
    /// Prefix sums of the return series: <c>prefSum[i] = Σ returns[0..i-1]</c>.
    /// </param>
    /// <param name="prefSum2">
    /// Prefix sums of squared returns: <c>prefSum2[i] = Σ returns[0..i-1]²</c>.
    /// </param>
    /// <returns>
    /// Gaussian negative log-likelihood cost <c>n · (ln(σ̂²) + 1)</c> for the segment,
    /// or 0 for an empty segment.
    /// </returns>
    private static double SegmentCost(int start, int end, double[] prefSum, double[] prefSum2)
    {
        int len = end - start;
        if (len <= 0) return 0;

        // Compute sum and sum-of-squares for the segment [start, end) using prefix arrays — O(1).
        double sum  = prefSum[end]  - prefSum[start];
        double sum2 = prefSum2[end] - prefSum2[start];

        // MLE mean and variance of the segment.
        double mean = sum / len;
        // Variance via the computational formula: E[x²] − (E[x])²
        double var  = sum2 / len - mean * mean;

        // Clamp to a small positive value to avoid ln(0) for constant segments.
        if (var <= 0) var = 1e-10;

        // Gaussian NLL cost (constant 2π term dropped as it cancels in comparisons).
        return len * (Math.Log(var) + 1);
    }
}
