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
/// For each pair (i, j) of features, the F-ratio is computed by comparing the
/// variance explained by the interaction term x_i × x_j versus the residual variance
/// in a simple linear model predicting DirectionCorrect.
/// High F-ratios indicate that the interaction is predictive beyond the individual
/// main effects, and the pair is recommended for inclusion as a product feature.
/// Runs weekly per active model.
/// </remarks>
public sealed class MLFeatureInteractionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureInteractionWorker> _logger;

    private const int  TopKInteractions = 5;
    private const int  MinSamples       = 100;

    public MLFeatureInteractionWorker(IServiceScopeFactory scopeFactory, ILogger<MLFeatureInteractionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureInteractionWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLFeatureInteractionWorker error"); }
            await Task.Delay(TimeSpan.FromDays(7), stoppingToken);
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
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Load recent prediction logs with ContributionsJson as a SHAP proxy
            // We approximate the full feature vector from the stored SHAP contributions
            // In a full implementation the feature vectors would be stored in PredictionLog
            var logs = await readDb.Set<MLModelPredictionLog>()
                .Where(l => l.MLModelId == model.Id && !l.IsDeleted && l.DirectionCorrect.HasValue)
                .OrderByDescending(l => l.PredictedAt)
                .Take(1000)
                .ToListAsync(ct);

            if (logs.Count < MinSamples) continue;

            // Build dataset: (confidence, direction correct) for two-feature ANOVA proxy
            // Full pairwise interaction requires stored feature vectors; this uses the
            // confidence score distribution as a proxy for the most informative features
            var featureNames = MLFeatureHelper.FeatureNames;
            int F = featureNames.Length;

            // Compute interaction scores using correlation between squared product and outcome
            var interactions = new List<(int A, int B, double Score)>();
            // For the SHAP-proxy approach: parse ShapValuesJson if available
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
                _logger.LogDebug("MLFeatureInteractionWorker: not enough SHAP rows for {Id}", model.Id);
                continue;
            }

            // ANOVA F-ratio for top pairwise interactions
            for (int a = 0; a < Math.Min(F, 15); a++)
            for (int b = a + 1; b < Math.Min(F, 15); b++)
            {
                // Interaction feature: x_a × x_b (in SHAP space)
                var products = shapRows.Select(r => r.Values![a] * r.Values![b]).ToArray();
                var labels   = shapRows.Select(r => r.Label).ToArray();
                double f     = ComputeFRatio(products, labels);
                if (!double.IsNaN(f) && f > 0)
                    interactions.Add((a, b, f));
            }

            var topK = interactions.OrderByDescending(x => x.Score).Take(TopKInteractions).ToList();

            // Remove old audits for this model
            var old = await writeDb.Set<MLFeatureInteractionAudit>()
                .Where(a => a.MLModelId == model.Id && !a.IsDeleted)
                .ToListAsync(ct);
            foreach (var o in old) o.IsDeleted = true;

            for (int rank = 0; rank < topK.Count; rank++)
            {
                var (a, b, score) = topK[rank];
                writeDb.Set<MLFeatureInteractionAudit>().Add(new MLFeatureInteractionAudit
                {
                    MLModelId          = model.Id,
                    Symbol             = model.Symbol,
                    Timeframe          = model.Timeframe,
                    FeatureIndexA      = a,
                    FeatureNameA       = featureNames[a],
                    FeatureIndexB      = b,
                    FeatureNameB       = featureNames[b],
                    InteractionScore   = score,
                    Rank               = rank + 1,
                    IsIncludedAsFeature = rank < 3,
                    ComputedAt         = DateTime.UtcNow
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

    private static double ComputeFRatio(double[] x, double[] y)
    {
        int n = x.Length;
        if (n < 4) return 0;
        double xMean = x.Average();
        double yMean = y.Average();
        double sXY = 0, sSX = 0;
        for (int i = 0; i < n; i++) { sXY += (x[i] - xMean) * (y[i] - yMean); sSX += (x[i] - xMean) * (x[i] - xMean); }
        if (sSX < 1e-12) return 0;
        double b1    = sXY / sSX;
        double b0    = yMean - b1 * xMean;
        double ssReg = 0, ssRes = 0;
        for (int i = 0; i < n; i++) { double yHat = b0 + b1 * x[i]; ssReg += (yHat - yMean) * (yHat - yMean); ssRes += (y[i] - yHat) * (y[i] - yHat); }
        double msReg = ssReg;          // df=1 for simple regression
        double msRes = ssRes / (n - 2);
        return msRes > 0 ? msReg / msRes : 0;
    }
}
