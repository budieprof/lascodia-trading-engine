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
/// Kelly Criterion — computes optimal bet fraction f* = (p*b - q)/b where p=win rate,
/// b=mean_win/mean_loss; caps at 25% and suppresses models with negative EV.
/// Runs every 24 hours.
/// </summary>
public sealed class MLKellyFractionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLKellyFractionWorker> _logger;

    public MLKellyFractionWorker(IServiceScopeFactory scopeFactory, ILogger<MLKellyFractionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLKellyFractionWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLKellyFractionWorker error"); }
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

        var models = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer
                     && m.ModelBytes != null)
            .ToListAsync(ct);

        foreach (var model in models)
        {
            var candles = await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe
                         && c.Timestamp >= DateTime.UtcNow.AddDays(-60) && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            if (candles.Count < 20) continue;

            ModelSnapshot? snap;
            try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
            catch { continue; }
            if (snap?.Weights == null || snap.Weights.Length == 0) continue;

            double[] weights = snap.Weights[0];
            var samples = MLFeatureHelper.BuildTrainingSamples(candles);
            if (samples.Count < 10) continue;

            var wins  = new List<double>();
            var losses = new List<double>();

            for (int i = 0; i < samples.Count - 1; i++)
            {
                var s = samples[i];
                double dot = 0;
                for (int j = 0; j < weights.Length && j < s.Features.Length; j++)
                    dot += weights[j] * s.Features[j];
                int signal = dot >= 0 ? 1 : -1;

                double nextClose = (double)candles[i + 1].Close;
                double thisClose = (double)candles[i].Close;
                double ret = (nextClose - thisClose) / (thisClose + 1e-8) * signal;

                if (ret > 0) wins.Add(ret);
                else         losses.Add(Math.Abs(ret));
            }

            if (wins.Count == 0 || losses.Count == 0) continue;

            double p     = (double)wins.Count / (wins.Count + losses.Count);
            double q     = 1 - p;
            double b     = wins.Average() / (losses.Average() + 1e-8);
            double fStar = (p * b - q) / (b + 1e-8);
            double halfKelly = Math.Clamp(0.5 * fStar, -0.25, 0.25);
            bool negEv = fStar < 0;

            writeDb.Set<MLKellyFractionLog>().Add(new MLKellyFractionLog
            {
                MLModelId     = model.Id,
                Symbol        = model.Symbol,
                Timeframe     = model.Timeframe.ToString(),
                KellyFraction = Math.Clamp(fStar, -0.25, 0.25),
                HalfKelly     = halfKelly,
                WinRate       = p,
                WinLossRatio  = b,
                NegativeEV    = negEv,
                ComputedAt    = DateTime.UtcNow
            });

            if (negEv)
            {
                var tracked = await writeDb.Set<MLModel>().FindAsync(new object[] { model.Id }, ct);
                if (tracked != null) tracked.IsSuppressed = true;
            }

            await writeDb.SaveChangesAsync(ct);

            _logger.LogInformation(
                "MLKellyFractionWorker: {S}/{T} f*={F:F4} halfKelly={H:F4} winRate={P:F3} b={B:F3} negEV={N}",
                model.Symbol, model.Timeframe, fStar, halfKelly, p, b, negEv);
        }
    }
}
