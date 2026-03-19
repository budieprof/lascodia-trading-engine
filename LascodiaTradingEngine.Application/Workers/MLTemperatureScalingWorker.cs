using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Temperature Scaling Calibration (Rec #362).
/// Runs every 3 days. Binary-searches for optimal temperature T ∈ [0.1, 10.0]
/// that minimises NLL on the last 200 prediction logs.
/// Computes ECE before and after calibration.
/// </summary>
public sealed class MLTemperatureScalingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLTemperatureScalingWorker> _logger;

    private const int    MaxSamples   = 200;
    private const int    BsIterations = 20;
    private const int    EceBins      = 10;

    public MLTemperatureScalingWorker(IServiceScopeFactory scopeFactory, ILogger<MLTemperatureScalingWorker> logger)
    { _scopeFactory = scopeFactory; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLTemperatureScalingWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLTemperatureScalingWorker error"); }
            await Task.Delay(TimeSpan.FromDays(3), stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        var models = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .ToListAsync(ct);

        foreach (var model in models)
        {
            var logs = await readDb.Set<MLModelPredictionLog>()
                .AsNoTracking()
                .Where(p => p.MLModelId == model.Id && !p.IsDeleted && p.DirectionCorrect != null)
                .OrderByDescending(p => p.PredictedAt)
                .Take(MaxSamples)
                .ToListAsync(ct);

            if (logs.Count < 20) continue;

            // Build logits and labels
            double[] confs  = logs.Select(p => Math.Clamp((double)p.ConfidenceScore, 1e-7, 1 - 1e-7)).ToArray();
            double[] logits = confs.Select(c => Math.Log(c / (1.0 - c))).ToArray();
            double[] labels = logs.Select(p => (p.DirectionCorrect ?? false) ? 1.0 : 0.0).ToArray();

            // Pre-calibration NLL and ECE (T=1)
            double preNll = ComputeNll(logits, labels, 1.0);
            double preEce = ComputeEce(confs, labels);

            // Binary search for optimal temperature
            double tLow = 0.1, tHigh = 10.0;
            double optT = 1.0;
            for (int iter = 0; iter < BsIterations; iter++)
            {
                double tMid     = (tLow + tHigh) / 2.0;
                double tMidPlus = tMid + 0.01;

                double nllMid     = ComputeNll(logits, labels, tMid);
                double nllMidPlus = ComputeNll(logits, labels, tMidPlus);

                // Gradient direction: if NLL decreases going higher, move tLow up
                if (nllMid > nllMidPlus)
                    tLow = tMid;
                else
                    tHigh = tMid;

                optT = (tLow + tHigh) / 2.0;
            }

            // Post-calibration NLL and ECE
            double postNll  = ComputeNll(logits, labels, optT);
            double[] calConf = logits.Select(lg => Sigmoid(lg / optT)).ToArray();
            double postEce  = ComputeEce(calConf, labels);

            var existing = await writeDb.Set<MLTemperatureScalingLog>()
                .FirstOrDefaultAsync(x => x.MLModelId == model.Id && !x.IsDeleted, ct);

            if (existing == null)
            {
                writeDb.Set<MLTemperatureScalingLog>().Add(new MLTemperatureScalingLog
                {
                    MLModelId            = model.Id,
                    Symbol               = model.Symbol,
                    Timeframe = model.Timeframe.ToString(),
                    OptimalTemperature   = optT,
                    PreCalibrationEce    = preEce,
                    PostCalibrationEce   = postEce,
                    PreCalibrationNll    = preNll,
                    PostCalibrationNll   = postNll,
                    CalibrationSamples   = logs.Count,
                    ComputedAt           = DateTime.UtcNow
                });
            }
            else
            {
                existing.OptimalTemperature = optT;
                existing.PreCalibrationEce  = preEce;
                existing.PostCalibrationEce = postEce;
                existing.PreCalibrationNll  = preNll;
                existing.PostCalibrationNll = postNll;
                existing.CalibrationSamples = logs.Count;
                existing.ComputedAt         = DateTime.UtcNow;
            }

            _logger.LogInformation(
                "MLTemperatureScalingWorker: {S}/{T} optT={T2:F3}, ECE: {Pre:F4}→{Post:F4}",
                model.Symbol, model.Timeframe, optT, preEce, postEce);
        }

        await writeDb.SaveChangesAsync(ct);
    }

    private static double ComputeNll(double[] logits, double[] labels, double temperature)
    {
        double nll = 0;
        for (int i = 0; i < logits.Length; i++)
        {
            double p = Sigmoid(logits[i] / temperature);
            p = Math.Clamp(p, 1e-7, 1 - 1e-7);
            nll += -(labels[i] * Math.Log(p) + (1 - labels[i]) * Math.Log(1 - p));
        }
        return nll / logits.Length;
    }

    private static double ComputeEce(double[] confs, double[] labels)
    {
        double ece = 0;
        int n = confs.Length;
        for (int b = 0; b < EceBins; b++)
        {
            double lo = (double)b / EceBins;
            double hi = (double)(b + 1) / EceBins;
            var inBin = Enumerable.Range(0, n)
                .Where(i => confs[i] >= lo && confs[i] < hi)
                .ToList();
            if (inBin.Count == 0) continue;
            double meanConf = inBin.Average(i => confs[i]);
            double meanAcc  = inBin.Average(i => labels[i]);
            ece += (double)inBin.Count / n * Math.Abs(meanConf - meanAcc);
        }
        return ece;
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));
}
