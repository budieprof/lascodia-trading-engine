using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects the top-K pairwise feature interactions using ANOVA F-ratio analysis
/// on live prediction outcomes (Rec #34).
/// </summary>
/// <remarks>
/// <para>
/// <b>Purpose:</b> Standard ML models treat each input feature independently. However,
/// many trading signals are only predictive in combination — for example, high RSI
/// concurrent with a narrowing Bollinger Band may be more informative than either feature
/// alone. This worker identifies feature pairs whose product (interaction term x_i × x_j)
/// is significantly predictive of direction outcome, and recommends the top pairs for
/// inclusion as explicit product features in the next retrain.
/// </para>
/// <para>
/// <b>Interaction detection method — ANOVA F-ratio (via SHAP proxy):</b>
/// <list type="number">
///   <item>For each pair (i, j) of the first 15 features (to keep O(F²) tractable), form
///         the interaction term as the element-wise product of SHAP values:
///         <c>interaction_k = shap_i_k × shap_j_k</c>.</item>
///   <item>Fit a simple OLS regression: <c>DirectionCorrect ~ b0 + b1 × interaction</c>.</item>
///   <item>Compute the F-ratio = MSReg / MSRes where:
///         MSReg = SS_regression (df=1) and MSRes = SS_residual / (n-2).
///         A high F-ratio means the interaction term explains significantly more variance
///         than noise, i.e. the pair is jointly predictive.</item>
///   <item>Rank all pairs by F-ratio and write the top-<see cref="TopKInteractions"/> pairs
///         to <see cref="MLFeatureInteractionAudit"/>. The top 3 are flagged as
///         <c>IsIncludedAsFeature = true</c>, signalling they should be added to the feature
///         set in the next scheduled retrain.</item>
/// </list>
/// </para>
/// <para>
/// <b>SHAP proxy:</b> Rather than storing full raw feature vectors in prediction logs
/// (which would be expensive), this worker reads per-prediction SHAP value arrays from
/// <c>MLModelPredictionLog.ShapValuesJson</c>. SHAP values preserve the sign and relative
/// magnitude of each feature's contribution, making them suitable as a proxy for the
/// original feature values in the interaction F-ratio test.
/// </para>
/// <para>
/// <b>Polling interval:</b> 7 days (weekly). Interaction patterns evolve slowly and a
/// weekly cadence provides sufficient freshness while keeping DB load manageable.
/// </para>
/// <para>
/// <b>Pipeline role:</b> Upstream of MLTrainingWorker — audit results are read at
/// training time to inject product features into the feature engineering pipeline.
/// </para>
/// </remarks>
public sealed class MLFeatureInteractionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureInteractionWorker> _logger;

    /// <summary>Number of top-ranked interaction pairs to persist per model per cycle.</summary>
    private const int  TopKInteractions = 5;

    /// <summary>
    /// Minimum number of resolved prediction log entries required before attempting
    /// interaction analysis. Too few samples produce unreliable F-ratio estimates.
    /// </summary>
    private const int  MinSamples       = 100;

    /// <summary>
    /// Initialises the worker with a DI scope factory and a logger.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create a fresh DI scope each polling cycle.</param>
    /// <param name="logger">Structured logger for interaction diagnostics.</param>
    public MLFeatureInteractionWorker(IServiceScopeFactory scopeFactory, ILogger<MLFeatureInteractionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs a weekly loop delegating to <see cref="RunAsync"/>.
    /// Non-cancellation errors are caught and logged so the loop survives transient failures.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host shuts down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureInteractionWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLFeatureInteractionWorker error"); }
            // Run weekly — interaction patterns are stable and don't require daily computation.
            await Task.Delay(TimeSpan.FromDays(7), stoppingToken);
        }
    }

    /// <summary>
    /// Main cycle logic. For each active non-meta-learner model, loads recent prediction
    /// logs with SHAP values and computes pairwise ANOVA F-ratios over the first 15 features.
    /// Persists the top-<see cref="TopKInteractions"/> pairs to <see cref="MLFeatureInteractionAudit"/>,
    /// soft-deleting previous audit rows first.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Meta-learners operate on model outputs, not raw features, so interaction analysis
        // between raw indicators is not meaningful for them.
        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Load recent prediction logs with ContributionsJson as a SHAP proxy.
            // We approximate the full feature vector from the stored SHAP contributions.
            // In a full implementation the feature vectors would be stored in PredictionLog.
            // Only logs where DirectionCorrect is resolved (non-null) are useful as labels.
            var logs = await readDb.Set<MLModelPredictionLog>()
                .Where(l => l.MLModelId == model.Id && !l.IsDeleted && l.DirectionCorrect.HasValue)
                .OrderByDescending(l => l.PredictedAt)
                .Take(1000)
                .ToListAsync(ct);

            if (logs.Count < MinSamples) continue;

            // Build dataset: (confidence, direction correct) for two-feature ANOVA proxy.
            // Full pairwise interaction requires stored feature vectors; this uses the
            // confidence score distribution as a proxy for the most informative features.
            var featureNames = MLFeatureHelper.FeatureNames;
            int F = featureNames.Length;

            // Compute interaction scores using correlation between squared product and outcome.
            var interactions = new List<(int A, int B, double Score)>();

            // For the SHAP-proxy approach: parse ShapValuesJson if available.
            // Each row becomes (shap_values: double[], label: 0.0 or 1.0).
            var shapRows = logs
                .Where(l => l.ShapValuesJson != null)
                .Select(l =>
                {
                    try { return (Values: System.Text.Json.JsonSerializer.Deserialize<double[]>(l.ShapValuesJson!), Label: l.DirectionCorrect!.Value ? 1.0 : 0.0); }
                    catch { return (Values: (double[]?)null, Label: 0.0); }
                })
                .Where(r => r.Values != null && r.Values.Length == F)
                .ToList();

            if (shapRows.Count < 50)
            {
                // Not enough SHAP rows — model may be too new or ShapValuesJson not yet populated.
                _logger.LogDebug("MLFeatureInteractionWorker: not enough SHAP rows for {Id}", model.Id);
                continue;
            }

            // Limit to the first 15 features to keep the O(F²) pairwise loop tractable.
            // 15 features → 105 pairs, which is fast to compute even on large log sets.
            // ANOVA F-ratio for top pairwise interactions.
            for (int a = 0; a < Math.Min(F, 15); a++)
            for (int b = a + 1; b < Math.Min(F, 15); b++)
            {
                // Interaction feature: x_a × x_b (in SHAP space).
                // The product of two SHAP values approximates the interaction effect —
                // if both features consistently push the prediction in the same direction
                // together, their product will be positively correlated with the outcome.
                var products = shapRows.Select(r => r.Values![a] * r.Values![b]).ToArray();
                var labels   = shapRows.Select(r => r.Label).ToArray();
                double f     = ComputeFRatio(products, labels);
                if (!double.IsNaN(f) && f > 0)
                    interactions.Add((a, b, f));
            }

            var topK = interactions.OrderByDescending(x => x.Score).Take(TopKInteractions).ToList();

            // Soft-delete all previous audit rows for this model before inserting fresh ones.
            // This ensures the audit table always reflects the latest computation cycle
            // without accumulating unbounded historical rows.
            var old = await writeDb.Set<MLFeatureInteractionAudit>()
                .Where(a => a.MLModelId == model.Id && !a.IsDeleted)
                .ToListAsync(ct);
            foreach (var o in old) o.IsDeleted = true;

            for (int rank = 0; rank < topK.Count; rank++)
            {
                var (a, b, score) = topK[rank];
                writeDb.Set<MLFeatureInteractionAudit>().Add(new MLFeatureInteractionAudit
                {
                    MLModelId           = model.Id,
                    Symbol              = model.Symbol,
                    Timeframe           = model.Timeframe,
                    FeatureIndexA       = a,
                    FeatureNameA        = featureNames[a],
                    FeatureIndexB       = b,
                    FeatureNameB        = featureNames[b],
                    InteractionScore    = score,
                    Rank                = rank + 1,
                    // Flag the top 3 pairs as recommended product features for the next retrain.
                    IsIncludedAsFeature = rank < 3,
                    ComputedAt          = DateTime.UtcNow
                });
            }

            await writeDb.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Feature interactions for {S}/{T}: top pair ({A}×{B}) F={F:F2}",
                model.Symbol, model.Timeframe,
                topK.Count > 0 ? featureNames[topK[0].A] : "?",
                topK.Count > 0 ? featureNames[topK[0].B] : "?",
                topK.Count > 0 ? topK[0].Score : 0);
        }
    }

    /// <summary>
    /// Computes the ANOVA F-ratio for a simple linear regression of <paramref name="y"/>
    /// on the interaction term <paramref name="x"/> (i.e. the product x_i × x_j).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Model: y = b0 + b1 × x + ε
    /// </para>
    /// <para>
    /// F = MSReg / MSRes
    ///   = SS_regression / (SS_residual / (n - 2))
    /// where df_regression = 1 for simple linear regression.
    /// </para>
    /// <para>
    /// A large F-ratio means the interaction term x_i × x_j explains a disproportionately
    /// large fraction of the variance in the binary outcome compared to noise. This does NOT
    /// control for the individual main effects of x_i and x_j — a full ANOVA with main-effect
    /// terms would be more rigorous but is not needed for ranking purposes.
    /// </para>
    /// <para>
    /// Returns 0 for degenerate inputs (n &lt; 4, zero variance in x).
    /// </para>
    /// </remarks>
    /// <param name="x">Interaction term values (x_i × x_j) across all prediction log rows.</param>
    /// <param name="y">Binary outcome labels (1.0 = direction correct, 0.0 = incorrect).</param>
    /// <returns>F-ratio ≥ 0; higher values indicate stronger predictive interaction.</returns>
    private static double ComputeFRatio(double[] x, double[] y)
    {
        int n = x.Length;
        if (n < 4) return 0;

        double xMean = x.Average();
        double yMean = y.Average();

        // Compute OLS slope (b1) and intercept (b0) via normal equations.
        double sXY = 0, sSX = 0;
        for (int i = 0; i < n; i++)
        {
            sXY += (x[i] - xMean) * (y[i] - yMean); // Cov(x, y) numerator
            sSX += (x[i] - xMean) * (x[i] - xMean); // Var(x) numerator
        }

        // Guard against zero-variance predictor (constant interaction term → no information).
        if (sSX < 1e-12) return 0;

        double b1    = sXY / sSX;
        double b0    = yMean - b1 * xMean;

        // Compute SS_regression (variance explained by the fit) and SS_residual (unexplained).
        double ssReg = 0, ssRes = 0;
        for (int i = 0; i < n; i++)
        {
            double yHat  = b0 + b1 * x[i];
            ssReg += (yHat - yMean) * (yHat - yMean);  // deviation of fit from mean
            ssRes += (y[i] - yHat)  * (y[i] - yHat);   // deviation of actual from fit
        }

        double msReg = ssReg;          // df=1 for simple regression → MSReg = SSReg / 1
        double msRes = ssRes / (n - 2); // df = n-2 for residuals in simple linear regression
        return msRes > 0 ? msReg / msRes : 0;
    }
}
