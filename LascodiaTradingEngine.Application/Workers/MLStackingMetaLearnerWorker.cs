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
/// Trains a logistic regression stacking meta-learner on the out-of-fold probability
/// outputs from all active base models for each symbol/timeframe (Rec #25).
/// </summary>
/// <remarks>
/// The stacking procedure:
///   1. Collect resolved <see cref="MLModelPredictionLog"/> records for all active base models.
///   2. For each signal: form a feature vector of each base model's <c>ConfidenceScore</c>.
///   3. Train a logistic regression meta-learner on (base_probs → actual_direction).
///   4. Persist the weights in <see cref="MLStackingMetaModel"/>.
///
/// The meta-learner runs weekly.  A minimum of 100 shared outcomes across base models
/// is required before training proceeds.
/// </remarks>
public sealed class MLStackingMetaLearnerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLStackingMetaLearnerWorker> _logger;

    public MLStackingMetaLearnerWorker(IServiceScopeFactory scopeFactory, ILogger<MLStackingMetaLearnerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLStackingMetaLearnerWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLStackingMetaLearnerWorker error"); }
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

        // Find all symbol/timeframe combinations with ≥ 2 active models
        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .ToListAsync(ct);

        var groups = activeModels
            .GroupBy(m => (m.Symbol, m.Timeframe))
            .Where(g => g.Count() >= 2)
            .ToList();

        foreach (var group in groups)
        {
            var (symbol, timeframe) = group.Key;
            var modelIds = group.Select(m => m.Id).ToList();

            // Collect all resolved prediction logs for these models, keyed by TradeSignalId
            var allLogs = await readDb.Set<MLModelPredictionLog>()
                .Where(l => modelIds.Contains(l.MLModelId)
                         && !l.IsDeleted
                         && l.DirectionCorrect.HasValue)
                .ToListAsync(ct);

            // Build stacking dataset: for each TradeSignalId, get each model's confidence
            var bySignal = allLogs
                .GroupBy(l => l.TradeSignalId)
                .Where(g => g.Select(l => l.MLModelId).Distinct().Count() >= 2)
                .ToList();

            if (bySignal.Count < 100) continue;

            int K = modelIds.Count;
            var X = new List<double[]>();
            var y = new List<int>();

            foreach (var sg in bySignal)
            {
                var row = new double[K];
                for (int k = 0; k < K; k++)
                {
                    var log = sg.FirstOrDefault(l => l.MLModelId == modelIds[k]);
                    row[k]  = log != null ? (double)log.ConfidenceScore : 0.5;
                }
                var trueDir = sg.First(l => l.ActualDirection.HasValue).ActualDirection!.Value;
                X.Add(row);
                y.Add(trueDir == Domain.Enums.TradeDirection.Buy ? 1 : 0);
            }

            var (weights, bias, accuracy, brier) = TrainLogisticMeta(X, y);

            // Deactivate previous meta-learner
            var prev = await writeDb.Set<MLStackingMetaModel>()
                .Where(s => s.Symbol == symbol && s.Timeframe == timeframe && s.IsActive && !s.IsDeleted)
                .ToListAsync(ct);
            foreach (var p in prev) p.IsActive = false;

            writeDb.Set<MLStackingMetaModel>().Add(new MLStackingMetaModel
            {
                Symbol           = symbol,
                Timeframe        = timeframe,
                BaseModelIdsJson = JsonSerializer.Serialize(modelIds),
                BaseModelCount   = K,
                MetaWeightsJson  = JsonSerializer.Serialize(weights),
                MetaBias         = bias,
                DirectionAccuracy = (decimal)accuracy,
                BrierScore       = (decimal)brier,
                IsActive         = true,
                TrainingSamples  = X.Count,
                TrainedAt        = DateTime.UtcNow
            });

            await writeDb.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Stacking meta-learner trained for {S}/{T}: {K} base models, {N} samples, acc={A:P1}",
                symbol, timeframe, K, X.Count, accuracy);
        }
    }

    private static (double[] Weights, double Bias, double Accuracy, double Brier)
        TrainLogisticMeta(List<double[]> X, List<int> y)
    {
        int N = X.Count, K = X[0].Length;
        var w = new double[K];
        double b = 0;
        double lr = 0.01;

        for (int epoch = 0; epoch < 200; epoch++)
        {
            for (int i = 0; i < N; i++)
            {
                double dot = b;
                for (int k = 0; k < K; k++) dot += w[k] * X[i][k];
                double p   = 1.0 / (1 + Math.Exp(-dot));
                double err = p - y[i];
                for (int k = 0; k < K; k++) w[k] -= lr * err * X[i][k];
                b -= lr * err;
            }
        }

        int correct = 0;
        double brierSum = 0;
        for (int i = 0; i < N; i++)
        {
            double dot = b;
            for (int k = 0; k < K; k++) dot += w[k] * X[i][k];
            double p   = 1.0 / (1 + Math.Exp(-dot));
            int pred   = p >= 0.5 ? 1 : 0;
            if (pred == y[i]) correct++;
            brierSum += (p - y[i]) * (p - y[i]);
        }
        return (w, b, (double)correct / N, brierSum / N);
    }
}
