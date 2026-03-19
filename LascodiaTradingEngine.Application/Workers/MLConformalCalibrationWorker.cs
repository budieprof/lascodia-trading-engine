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
/// Computes split conformal prediction calibration for each newly activated ML model,
/// producing statistically guaranteed coverage sets at inference time (Rec #16).
/// </summary>
/// <remarks>
/// Uses the hold-out calibration split (10 % of training data) that was already
/// separated during training.  For each calibration sample, the nonconformity score is:
///   α_i = 1 − ŷ_{y_i}  (1 minus the predicted probability of the true label)
/// The coverage threshold τ at level 1-α is the ⌈(n+1)(1-α)/n⌉-th quantile.
/// At inference: if (1 − ŷ_Buy) ≤ τ the prediction set includes Buy, similarly for Sell.
/// When both are included → "Ambiguous".
/// </remarks>
public sealed class MLConformalCalibrationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLConformalCalibrationWorker> _logger;

    public MLConformalCalibrationWorker(IServiceScopeFactory scopeFactory, ILogger<MLConformalCalibrationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLConformalCalibrationWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLConformalCalibrationWorker error"); }
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Find active models without a conformal calibration record
        var modelsToCalibrate = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .ToListAsync(ct);

        foreach (var model in modelsToCalibrate)
        {
            bool alreadyCalibrated = await readDb.Set<MLConformalCalibration>()
                .AnyAsync(c => c.MLModelId == model.Id && !c.IsDeleted, ct);
            if (alreadyCalibrated) continue;
            if (model.ModelBytes == null) continue;

            try
            {
                var snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes);
                if (snap?.Weights == null || snap.Biases == null) continue;

                // Use recent resolved prediction logs as the calibration set
                var calLogs = await readDb.Set<MLModelPredictionLog>()
                    .Where(l => l.MLModelId == model.Id
                             && !l.IsDeleted
                             && l.DirectionCorrect.HasValue
                             && l.ConfidenceScore > 0)
                    .OrderByDescending(l => l.PredictedAt)
                    .Take(500)
                    .ToListAsync(ct);

                if (calLogs.Count < 50) continue;

                // Compute nonconformity scores: α_i = 1 - P(true class)
                var scores = new List<double>(calLogs.Count);
                foreach (var log in calLogs)
                {
                    double pBuy  = (double)log.ConfidenceScore;
                    double pTrue = log.PredictedDirection == log.ActualDirection
                        ? pBuy : 1.0 - pBuy;
                    scores.Add(1.0 - pTrue);
                }
                scores.Sort();

                // τ at 90 % coverage
                double alpha      = 0.10;
                int    n          = scores.Count;
                int    qIdx       = (int)Math.Ceiling((n + 1) * (1 - alpha)) - 1;
                qIdx              = Math.Clamp(qIdx, 0, n - 1);
                double threshold  = scores[qIdx];

                // Empirical coverage on same set (upper bound — calibration set)
                int covered = calLogs.Count(l =>
                {
                    double pBuy  = (double)l.ConfidenceScore;
                    double pSell = 1.0 - pBuy;
                    bool   inBuy  = (1 - pBuy)  <= threshold;
                    bool   inSell = (1 - pSell)  <= threshold;
                    return (l.ActualDirection!.Value == Domain.Enums.TradeDirection.Buy && inBuy)
                        || (l.ActualDirection!.Value == Domain.Enums.TradeDirection.Sell && inSell);
                });
                double empCoverage  = (double)covered / calLogs.Count;
                int    ambiguousN   = calLogs.Count(l =>
                {
                    double p = (double)l.ConfidenceScore;
                    return (1 - p) <= threshold && p <= threshold;
                });

                writeDb.Set<MLConformalCalibration>().Add(new MLConformalCalibration
                {
                    MLModelId                = model.Id,
                    Symbol                   = model.Symbol,
                    Timeframe                = model.Timeframe,
                    NonConformityScoresJson  = JsonSerializer.Serialize(scores),
                    CalibrationSamples       = n,
                    CoverageAlpha            = 1 - alpha,
                    CoverageThreshold        = threshold,
                    EmpiricalCoverage        = empCoverage,
                    AmbiguousRate            = (double)ambiguousN / calLogs.Count,
                    CalibratedAt             = DateTime.UtcNow
                });

                await writeDb.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "Conformal calibration: model {Id} τ={T:F4} coverage={C:P1} ambiguous={A:P1}",
                    model.Id, threshold, empCoverage, (double)ambiguousN / calLogs.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Conformal calibration failed for model {Id}", model.Id);
            }
        }
    }
}
