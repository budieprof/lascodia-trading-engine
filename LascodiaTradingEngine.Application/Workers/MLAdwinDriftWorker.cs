using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// ADWIN adaptive windowing drift detection for ML model accuracy (Rec #140).
/// Runs daily. For each active model with >= 60 resolved prediction logs, applies
/// ADWIN change detection over the last 100 outcomes. Tests all valid split points
/// and triggers drift if |mean(left) - mean(right)| > ε_cut.
/// </summary>
public sealed class MLAdwinDriftWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLAdwinDriftWorker> _logger;

    private const int    MinLogs  = 60;
    private const int    LogTake  = 100;
    private const double Delta    = 0.002; // ADWIN δ

    public MLAdwinDriftWorker(IServiceScopeFactory scopeFactory, ILogger<MLAdwinDriftWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLAdwinDriftWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLAdwinDriftWorker error"); }
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
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
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            var logs = await readDb.Set<MLModelPredictionLog>()
                .Where(l => l.MLModelId == model.Id && l.DirectionCorrect.HasValue && !l.IsDeleted)
                .OrderBy(l => l.PredictedAt)
                .ToListAsync(ct);

            if (logs.Count < MinLogs) continue;

            // Take last 100
            var recent = logs.Count > LogTake ? logs.GetRange(logs.Count - LogTake, LogTake) : logs;
            int n = recent.Count;
            int[] outcomes = recent.Select(l => l.DirectionCorrect == true ? 1 : 0).ToArray();

            bool   driftDetected = false;
            int    bestT         = n / 2;
            double bestEpsilon   = 0;
            double bestMean1     = 0;
            double bestMean2     = 0;

            // Precompute prefix sums
            double[] prefix = new double[n + 1];
            for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + outcomes[i];

            for (int t = 30; t <= n - 30; t++)
            {
                int    m1  = t;
                int    m2  = n - t;
                double mu1 = prefix[t] / m1;
                double mu2 = (prefix[n] - prefix[t]) / m2;
                double eps = Math.Sqrt((1.0 / (2 * m1) + 1.0 / (2 * m2)) * Math.Log(4.0 * n / Delta));

                if (Math.Abs(mu1 - mu2) > eps)
                {
                    driftDetected = true;
                    bestT         = t;
                    bestEpsilon   = eps;
                    bestMean1     = mu1;
                    bestMean2     = mu2;
                    break;
                }
            }

            writeDb.Set<MLAdwinDriftLog>().Add(new MLAdwinDriftLog
            {
                MLModelId     = model.Id,
                Symbol        = model.Symbol,
                Timeframe     = model.Timeframe,
                DriftDetected = driftDetected,
                Window1Mean   = bestMean1,
                Window2Mean   = bestMean2,
                EpsilonCut    = bestEpsilon,
                Window1Size   = bestT,
                Window2Size   = n - bestT,
                DetectedAt    = DateTime.UtcNow
            });

            if (driftDetected)
            {
                _logger.LogWarning(
                    "MLAdwinDriftWorker: {S}/{T} ADWIN drift detected — |{M1:F4} - {M2:F4}| > ε={E:F4}.",
                    model.Symbol, model.Timeframe, bestMean1, bestMean2, bestEpsilon);
            }
            else
            {
                _logger.LogInformation(
                    "MLAdwinDriftWorker: {S}/{T} no drift detected (n={N}).",
                    model.Symbol, model.Timeframe, n);
            }

            await writeDb.SaveChangesAsync(ct);
        }
    }
}
