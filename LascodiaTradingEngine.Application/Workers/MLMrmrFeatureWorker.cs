using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes Maximum Relevance Minimum Redundancy (mRMR) feature rankings for each
/// active symbol/timeframe pair and persists them to <see cref="MLMrmrFeatureRanking"/> (Rec #41).
/// Runs daily. Uses mutual information estimated from discretised 10-bin histograms.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is mRMR?</b>
/// mRMR is a feature selection criterion that selects features which are simultaneously:
/// <list type="bullet">
///   <item><b>Maximally relevant</b> — high mutual information I(feature; target), meaning
///         the feature carries strong signal about the prediction target.</item>
///   <item><b>Minimally redundant</b> — low average mutual information with already-selected
///         features, meaning it contributes new information not already covered.</item>
/// </list>
/// The MRMR score for candidate feature f given already-selected set S is:
/// <c>score(f) = I(f; target) − (1/|S|) × Σ_{s∈S} I(f; s)</c>
/// </para>
/// <para>
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>Load 120 days of candles and build feature/label samples.</item>
///   <item>Discretise each continuous feature into 10 uniform-width bins.</item>
///   <item>Compute I(feature; target) for each feature using a joint frequency histogram.</item>
///   <item>Compute I(feature_i; feature_j) for all pairs — the redundancy matrix.</item>
///   <item>Greedily select features one at a time: at each step, choose the remaining
///         feature with the highest MRMR score and add it to the selected set.</item>
///   <item>Upsert one <see cref="MLMrmrFeatureRanking"/> row per feature per symbol/timeframe
///         with the rank, relevance, redundancy, and MRMR score.</item>
/// </list>
/// </para>
/// <para>
/// <b>Mutual information estimation:</b> Discrete histogram-based estimation over 10 bins
/// provides a good balance between bias and variance for the sample sizes available (100–several
/// thousand rows). The estimator is: I(X;Y) = Σ_{x,y} p(x,y) × log(p(x,y) / (p(x)p(y))).
/// </para>
/// <para>
/// <b>Pipeline role:</b> Rankings are consumed by the MLTrainingWorker to prioritise feature
/// engineering effort and optionally suppress low-ranking features via
/// <c>HyperparamOverrides.DisabledFeatureIndices</c>.
/// </para>
/// </remarks>
public sealed class MLMrmrFeatureWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLMrmrFeatureWorker> _logger;

    /// <summary>
    /// Initialises the worker with a DI scope factory and a logger.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create a fresh DI scope each polling cycle.</param>
    /// <param name="logger">Structured logger for mRMR ranking diagnostics.</param>
    public MLMrmrFeatureWorker(IServiceScopeFactory scopeFactory, ILogger<MLMrmrFeatureWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs a daily polling loop delegating to
    /// <see cref="RunAsync"/>. Non-cancellation errors are caught so the loop recovers
    /// from transient DB failures without stopping.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host shuts down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLMrmrFeatureWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLMrmrFeatureWorker error"); }
            // Run daily — mutual information rankings change slowly with market conditions.
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    /// <summary>
    /// Main cycle logic. For each distinct active (symbol, timeframe) pair, loads 120 days
    /// of candles, builds feature samples, computes the mutual information matrix, and runs
    /// the greedy mRMR selection algorithm. Results are upserted to <see cref="MLMrmrFeatureRanking"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Use distinct (symbol, timeframe) pairs — one set of rankings per market/resolution.
        // Exclude meta-learners and MAML initialisers whose features are not raw indicators.
        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Load 120 days of candles — a window long enough for stable MI estimates
            // while remaining recent enough to reflect current market structure.
            var cutoff  = DateTime.UtcNow.AddDays(-120);
            var candles = await readDb.Set<Candle>()
                .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe
                         && c.Timestamp >= cutoff && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            if (candles.Count < MLFeatureHelper.LookbackWindow + 100) continue;

            var samples  = MLFeatureHelper.BuildTrainingSamples(candles);
            if (samples.Count < 100) continue;

            int F       = MLFeatureHelper.FeatureCount;
            int N       = samples.Count;
            // Direction labels: 0 = bearish, 1 = bullish — the binary prediction target.
            int[] targets = samples.Select(s => s.Direction).ToArray();

            // ── Step 1: Compute mutual information between each feature and the target ──
            // Mutual information I(X; Y) = H(X) + H(Y) − H(X,Y)
            //   estimated via joint frequency histograms over discretised bins.
            // binsX=10 for features, binsY=2 for binary target.
            const int Bins = 10;
            double[] miWithTarget = new double[F];
            double[,] miMatrix    = new double[F, F];

            for (int f = 0; f < F; f++)
            {
                float[] col = samples.Select(s => s.Features.Length > f ? s.Features[f] : 0f).ToArray();
                // Discretise continuous feature into 10 equal-width bins, then compute MI with target.
                miWithTarget[f] = MutualInfo(Discretise(col, Bins), targets, Bins, 2);
            }

            // ── Step 2: Compute mutual information between all feature pairs ─────────
            // This forms the redundancy matrix: miMatrix[f, g] = I(feature_f; feature_g).
            // Symmetry: miMatrix[f,g] == miMatrix[g,f], so only upper triangle is computed.
            for (int f = 0; f < F; f++)
                for (int g = f + 1; g < F; g++)
                {
                    float[] cf = samples.Select(s => s.Features.Length > f ? s.Features[f] : 0f).ToArray();
                    float[] cg = samples.Select(s => s.Features.Length > g ? s.Features[g] : 0f).ToArray();
                    double mi = MutualInfo(Discretise(cf, Bins), Discretise(cg, Bins), Bins, Bins);
                    miMatrix[f, g] = miMatrix[g, f] = mi;
                }

            // ── Step 3: Greedy mRMR selection ─────────────────────────────────────
            // At each step, choose the remaining feature with the highest MRMR score:
            //   score(f) = I(f; target) − (1/|S|) × Σ_{s∈S} I(f; s)
            // where S is the set of already-selected features.
            // The first feature selected (rank 0) is simply the one with highest I(f; target)
            // because the redundancy term is zero when S is empty.
            var selected   = new List<int>();
            var remaining  = Enumerable.Range(0, F).ToList();
            var mrmrScores = new double[F];

            for (int rank = 0; rank < F; rank++)
            {
                double best  = double.NegativeInfinity;
                int    bestF = remaining[0];

                foreach (int fi in remaining)
                {
                    // Redundancy = average mutual information with already-selected features.
                    // Zero for the first selection step (empty S).
                    double red   = selected.Count == 0 ? 0
                        : selected.Average(s => miMatrix[fi, s]);
                    double score = miWithTarget[fi] - red;
                    if (score > best) { best = score; bestF = fi; }
                    mrmrScores[fi] = score;
                }

                selected.Add(bestF);
                remaining.Remove(bestF);

                // Upsert the ranking row for this feature.
                var existing = await writeDb.Set<MLMrmrFeatureRanking>()
                    .FirstOrDefaultAsync(r => r.Symbol     == model.Symbol
                                           && r.Timeframe  == model.Timeframe
                                           && r.FeatureName == MLFeatureHelper.FeatureNames[bestF]
                                           && !r.IsDeleted, ct);

                // Recompute redundancy excluding bestF itself from the selected set for accurate storage.
                double red2 = selected.Count <= 1 ? 0
                    : selected.Take(selected.Count - 1).Average(s => miMatrix[bestF, s]);

                if (existing != null)
                {
                    // Update in place — rank and scores may change as more candle data accumulates.
                    existing.MrmrRank            = rank;
                    existing.MutualInfoWithTarget = miWithTarget[bestF];
                    existing.RedundancyScore      = red2;
                    existing.MrmrScore            = mrmrScores[bestF];
                    existing.SampleCount          = N;
                    existing.ComputedAt           = DateTime.UtcNow;
                }
                else
                {
                    writeDb.Set<MLMrmrFeatureRanking>().Add(new MLMrmrFeatureRanking
                    {
                        Symbol               = model.Symbol,
                        Timeframe            = model.Timeframe,
                        FeatureName          = MLFeatureHelper.FeatureNames[bestF],
                        MrmrRank             = rank,
                        MutualInfoWithTarget = miWithTarget[bestF],
                        RedundancyScore      = red2,
                        MrmrScore            = mrmrScores[bestF],
                        SampleCount          = N,
                        ComputedAt           = DateTime.UtcNow
                    });
                }
            }

            await writeDb.SaveChangesAsync(ct);
            _logger.LogDebug("MLMrmrFeatureWorker ranked {F} features for {S}/{T}.",
                F, model.Symbol, model.Timeframe);
        }
    }

    /// <summary>
    /// Discretises a continuous feature array into equal-width integer bin indices.
    /// </summary>
    /// <remarks>
    /// Equal-width binning (as opposed to equal-frequency/quantile binning) is used here
    /// for simplicity and speed. The mutual information estimator is relatively robust to
    /// the binning scheme when the number of samples is large relative to the number of bins.
    /// A zero-range array (constant feature) returns all zeros — the feature will have
    /// zero mutual information with any target and rank last in mRMR.
    /// </remarks>
    /// <param name="values">Raw continuous feature values.</param>
    /// <param name="bins">Number of equal-width bins.</param>
    /// <returns>Integer bin indices in [0, bins-1].</returns>
    private static int[] Discretise(float[] values, int bins)
    {
        float min   = values.Min();
        float max   = values.Max();
        float range = max - min;
        // Constant feature → all values fall in bin 0; MI will be 0.
        if (range < 1e-9f) return new int[values.Length];
        return values.Select(v => (int)Math.Min(bins - 1, (v - min) / range * bins)).ToArray();
    }

    /// <summary>
    /// Estimates the mutual information I(X; Y) between two discrete random variables
    /// using a joint frequency histogram.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Formula: I(X;Y) = Σ_{x,y} p(x,y) × log(p(x,y) / (p(x) × p(y)))
    /// where probabilities are estimated as observed frequencies divided by n.
    /// </para>
    /// <para>
    /// Zero joint-count cells are skipped (as 0 × log(0) → 0 by convention), avoiding
    /// NaN values from log(0). This is the standard approach for histogram-based MI.
    /// </para>
    /// <para>
    /// The estimator has a positive bias for small samples (it overestimates MI when n
    /// is small relative to binsX × binsY), but this is acceptable for ranking purposes
    /// since the bias is approximately uniform across features.
    /// </para>
    /// </remarks>
    /// <param name="x">Discretised values of the first variable (e.g. feature bin indices).</param>
    /// <param name="y">Discretised values of the second variable (e.g. target or another feature).</param>
    /// <param name="binsX">Number of bins for x (max value + 1).</param>
    /// <param name="binsY">Number of bins for y (max value + 1).</param>
    /// <returns>Estimated mutual information in nats (natural logarithm). Always ≥ 0.</returns>
    private static double MutualInfo(int[] x, int[] y, int binsX, int binsY)
    {
        int n = x.Length;
        // Build joint count matrix and marginal count arrays.
        int[,] joint = new int[binsX, binsY];
        int[]  px    = new int[binsX];
        int[]  py    = new int[binsY];

        for (int i = 0; i < n; i++)
        {
            int xi = Math.Min(x[i], binsX - 1);
            int yi = Math.Min(y[i], binsY - 1);
            joint[xi, yi]++;
            px[xi]++;
            py[yi]++;
        }

        // Accumulate MI = Σ p(x,y) × log(p(x,y) / (p(x) × p(y)))
        double mi = 0;
        for (int a = 0; a < binsX; a++)
            for (int b = 0; b < binsY; b++)
            {
                // Skip empty cells — contributes 0 to MI by convention.
                if (joint[a, b] == 0) continue;
                double pxy = (double)joint[a, b] / n;
                double pa  = (double)px[a] / n;
                double pb  = (double)py[b] / n;
                // Pointwise MI contribution: p(x,y) × log(p(x,y) / (p(x)×p(y)))
                mi += pxy * Math.Log(pxy / (pa * pb));
            }
        return mi;
    }
}
