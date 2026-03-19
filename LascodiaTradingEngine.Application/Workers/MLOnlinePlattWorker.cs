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
/// Recurrently updates the Platt scaling parameters (A, B) of active models using a
/// sliding window of recent resolved prediction logs (Rec #45). Tracks EMA drift of
/// the A parameter to detect calibration degradation over time. Runs daily.
/// </summary>
public sealed class MLOnlinePlattWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLOnlinePlattWorker> _logger;

    private const int WindowSize = 100;
    private const double Lr       = 0.01;
    private const double EmaAlpha = 0.1;  // EMA smoothing for drift tracking

    public MLOnlinePlattWorker(IServiceScopeFactory scopeFactory, ILogger<MLOnlinePlattWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLOnlinePlattWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLOnlinePlattWorker error"); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
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
            if (snap == null) continue;

            var recentLogs = await readDb.Set<MLModelPredictionLog>()
                .Where(p => p.MLModelId == model.Id
                         && p.DirectionCorrect != null
                         && !p.IsDeleted)
                .OrderByDescending(p => p.PredictedAt)
                .Take(WindowSize)
                .ToListAsync(ct);

            if (recentLogs.Count < 30) continue;

            double a = snap.PlattA, b = snap.PlattB;
            double originalA = a;

            foreach (var log in recentLogs)
            {
                double rawP  = (double)log.ConfidenceScore;
                double logit = rawP > 0 && rawP < 1 ? Math.Log(rawP / (1 - rawP)) : 0;
                double p     = 1.0 / (1 + Math.Exp(-(a * logit + b)));
                double y     = log.DirectionCorrect!.Value ? 1.0 : 0.0;
                double err   = p - y;
                a -= Lr * err * logit;
                b -= Lr * err;
            }

            double drift = Math.Abs(a - originalA);
            double newDrift = model.PlattCalibrationDrift.HasValue
                ? EmaAlpha * drift + (1 - EmaAlpha) * model.PlattCalibrationDrift.Value
                : drift;

            // Update model record
            var writeModel = await writeDb.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == model.Id && !m.IsDeleted, ct);
            if (writeModel == null) continue;

            writeModel.PlattA                = (decimal)a;
            writeModel.PlattB                = (decimal)b;
            writeModel.PlattCalibrationDrift = newDrift;
            writeModel.OnlineLearningUpdateCount++;
            writeModel.LastOnlineLearningAt  = DateTime.UtcNow;

            _logger.LogDebug("MLOnlinePlattWorker: {S}/{T} PlattA={A:F4} PlattB={B:F4} Drift={D:F4}",
                model.Symbol, model.Timeframe, a, b, newDrift);
        }
        await writeDb.SaveChangesAsync(ct);
    }
}
