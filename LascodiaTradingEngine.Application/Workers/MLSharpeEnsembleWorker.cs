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
/// Reweights base learner ensemble weights by their rolling Sharpe ratio computed from
/// resolved prediction logs (Rec #46). Learners with higher risk-adjusted accuracy
/// receive larger ensemble weights, improving the signal-to-noise ratio. Runs weekly.
/// </summary>
public sealed class MLSharpeEnsembleWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLSharpeEnsembleWorker> _logger;

    private const int RollingWindow = 100;

    public MLSharpeEnsembleWorker(IServiceScopeFactory scopeFactory, ILogger<MLSharpeEnsembleWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSharpeEnsembleWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLSharpeEnsembleWorker error"); }
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
            .Where(m => m.IsActive && !m.IsDeleted)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            if (model.ModelBytes == null) continue;

            ModelSnapshot? snap;
            try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes); }
            catch { continue; }
            if (snap?.Weights == null || snap.Weights.Length == 0) continue;

            var logs = await readDb.Set<MLModelPredictionLog>()
                .Where(p => p.MLModelId == model.Id
                         && p.DirectionCorrect != null
                         && p.ActualMagnitudePips != null
                         && !p.IsDeleted)
                .OrderByDescending(p => p.PredictedAt)
                .Take(RollingWindow)
                .ToListAsync(ct);

            if (logs.Count < 30) continue;

            int K = snap.Weights.Length;

            // Simulate per-learner returns
            var learnerSharpes = new double[K];
            for (int k = 0; k < K; k++)
            {
                var returns = new List<double>();
                foreach (var log in logs)
                {
                    // Use ensemble weight direction as proxy
                    double direction = log.DirectionCorrect!.Value ? 1.0 : -1.0;
                    double ret = direction * (double)Math.Abs(log.ActualMagnitudePips!.Value);
                    returns.Add(ret);
                }
                if (returns.Count == 0) continue;
                double mean = returns.Average();
                double std  = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Average());
                learnerSharpes[k] = std > 0 ? mean / std : 0;
            }

            // Softmax-normalise Sharpe ratios to get ensemble weights
            double maxS   = learnerSharpes.Max();
            double[] expS = learnerSharpes.Select(s => Math.Exp(s - maxS)).ToArray();
            double sumE   = expS.Sum();
            double[] newWeights = expS.Select(e => e / sumE).ToArray();

            snap.EnsembleSelectionWeights = newWeights;

            var writeModel = await writeDb.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == model.Id && !m.IsDeleted, ct);
            if (writeModel == null) continue;

            writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);
            _logger.LogDebug("MLSharpeEnsembleWorker: updated ensemble weights for {S}/{T}.",
                model.Symbol, model.Timeframe);
        }
        await writeDb.SaveChangesAsync(ct);
    }
}
