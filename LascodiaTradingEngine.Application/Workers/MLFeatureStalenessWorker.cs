using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Lag-1 autocorrelation per feature to detect stale predictors (Rec #194).
/// Computes autocorr for each feature's time series across 200 candle samples;
/// features with |autocorr| &gt; 0.95 are flagged as stale.
/// Runs weekly.
/// </summary>
public sealed class MLFeatureStalenessWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureStalenessWorker> _logger;

    public MLFeatureStalenessWorker(IServiceScopeFactory scopeFactory, ILogger<MLFeatureStalenessWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureStalenessWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLFeatureStalenessWorker error"); }
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
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            var candles = await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe && !c.IsDeleted)
                .OrderByDescending(c => c.Timestamp)
                .Take(200)
                .ToListAsync(ct);

            candles.Reverse();
            if (candles.Count < MLFeatureHelper.LookbackWindow + 2) continue;

            var samples = MLFeatureHelper.BuildTrainingSamples(candles);
            if (samples.Count < 10) continue;

            int F = MLFeatureHelper.FeatureCount;
            int staleCount = 0;

            for (int fi = 0; fi < F && !ct.IsCancellationRequested; fi++)
            {
                double[] x = samples.Select(s => (double)(s.Features.Length > fi ? s.Features[fi] : 0f)).ToArray();
                double autocorr = ComputeLag1Autocorr(x);
                bool isStale = Math.Abs(autocorr) > 0.95;
                if (isStale) staleCount++;

                string featureName = MLFeatureHelper.FeatureNames[fi];

                var existing = await writeDb.Set<MLFeatureStalenessLog>()
                    .FirstOrDefaultAsync(l => l.MLModelId == model.Id && l.FeatureName == featureName && !l.IsDeleted, ct);

                if (existing == null)
                {
                    writeDb.Set<MLFeatureStalenessLog>().Add(new MLFeatureStalenessLog
                    {
                        MLModelId   = model.Id,
                        Symbol      = model.Symbol,
                        Timeframe   = model.Timeframe,
                        FeatureName = featureName,
                        Lag1Autocorr = autocorr,
                        IsStale     = isStale,
                        ComputedAt  = DateTime.UtcNow
                    });
                }
                else
                {
                    existing.Lag1Autocorr = autocorr;
                    existing.IsStale     = isStale;
                    existing.ComputedAt  = DateTime.UtcNow;
                }
            }

            await writeDb.SaveChangesAsync(ct);

            _logger.LogInformation(
                "MLFeatureStalenessWorker: {S}/{T} stale features={C}/{F}.",
                model.Symbol, model.Timeframe, staleCount, F);
        }
    }

    private static double ComputeLag1Autocorr(double[] x)
    {
        if (x.Length < 3) return 0;

        double[] lag0 = x[..^1];
        double[] lag1 = x[1..];

        double mean0 = lag0.Average();
        double mean1 = lag1.Average();

        double cov = 0, std0 = 0, std1 = 0;
        for (int i = 0; i < lag0.Length; i++)
        {
            cov  += (lag0[i] - mean0) * (lag1[i] - mean1);
            std0 += Math.Pow(lag0[i] - mean0, 2);
            std1 += Math.Pow(lag1[i] - mean1, 2);
        }

        double denom = Math.Sqrt(std0) * Math.Sqrt(std1);
        return denom < 1e-9 ? 0 : cov / denom;
    }
}
