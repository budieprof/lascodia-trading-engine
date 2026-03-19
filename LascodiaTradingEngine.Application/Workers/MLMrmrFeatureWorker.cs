using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes Maximum Relevance Minimum Redundancy (MRMR) feature rankings for each
/// active symbol/timeframe pair and persists them to <see cref="MLMrmrFeatureRanking"/> (Rec #41).
/// Runs daily. Uses mutual information estimated from discretised 10-bin histograms.
/// </summary>
public sealed class MLMrmrFeatureWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLMrmrFeatureWorker> _logger;

    public MLMrmrFeatureWorker(IServiceScopeFactory scopeFactory, ILogger<MLMrmrFeatureWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLMrmrFeatureWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLMrmrFeatureWorker error"); }
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
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            var cutoff  = DateTime.UtcNow.AddDays(-120);
            var candles = await readDb.Set<Candle>()
                .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe
                         && c.Timestamp >= cutoff && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            if (candles.Count < MLFeatureHelper.LookbackWindow + 100) continue;

            var samples  = MLFeatureHelper.BuildTrainingSamples(candles);
            if (samples.Count < 100) continue;

            int F       = MLFeatureHelper.FeatureCount;
            int N       = samples.Count;
            int[] targets = samples.Select(s => s.Direction).ToArray();

            // Mutual information via discretised histograms (10 bins)
            const int Bins = 10;
            double[] miWithTarget = new double[F];
            double[,] miMatrix = new double[F, F];

            for (int f = 0; f < F; f++)
            {
                float[] col = samples.Select(s => s.Features.Length > f ? s.Features[f] : 0f).ToArray();
                miWithTarget[f] = MutualInfo(Discretise(col, Bins), targets, Bins, 2);
            }

            for (int f = 0; f < F; f++)
                for (int g = f + 1; g < F; g++)
                {
                    float[] cf = samples.Select(s => s.Features.Length > f ? s.Features[f] : 0f).ToArray();
                    float[] cg = samples.Select(s => s.Features.Length > g ? s.Features[g] : 0f).ToArray();
                    double mi = MutualInfo(Discretise(cf, Bins), Discretise(cg, Bins), Bins, Bins);
                    miMatrix[f, g] = miMatrix[g, f] = mi;
                }

            // Greedy MRMR selection
            var selected  = new List<int>();
            var remaining = Enumerable.Range(0, F).ToList();
            var mrmrScores = new double[F];

            for (int rank = 0; rank < F; rank++)
            {
                double best = double.NegativeInfinity;
                int bestF = remaining[0];
                foreach (int fi in remaining)
                {
                    double red = selected.Count == 0 ? 0
                        : selected.Average(s => miMatrix[fi, s]);
                    double score = miWithTarget[fi] - red;
                    if (score > best) { best = score; bestF = fi; }
                    mrmrScores[fi] = score;
                }
                selected.Add(bestF);
                remaining.Remove(bestF);

                var existing = await writeDb.Set<MLMrmrFeatureRanking>()
                    .FirstOrDefaultAsync(r => r.Symbol == model.Symbol
                                           && r.Timeframe == model.Timeframe
                                           && r.FeatureName == MLFeatureHelper.FeatureNames[bestF]
                                           && !r.IsDeleted, ct);

                double red2 = selected.Count <= 1 ? 0
                    : selected.Take(selected.Count - 1).Average(s => miMatrix[bestF, s]);

                if (existing != null)
                {
                    existing.MrmrRank            = rank;
                    existing.MutualInfoWithTarget = miWithTarget[bestF];
                    existing.RedundancyScore      = red2;
                    existing.MrmrScore            = mrmrScores[bestF];
                    existing.SampleCount          = N;
                    existing.ComputedAt           = DateTime.UtcNow;
                }
                else
                {
                    writeDb.Set<MLMrmrFeatureRanking>().Add(new MLMrmrFeatureRanking
                    {
                        Symbol               = model.Symbol,
                        Timeframe            = model.Timeframe,
                        FeatureName          = MLFeatureHelper.FeatureNames[bestF],
                        MrmrRank             = rank,
                        MutualInfoWithTarget = miWithTarget[bestF],
                        RedundancyScore      = red2,
                        MrmrScore            = mrmrScores[bestF],
                        SampleCount          = N,
                        ComputedAt           = DateTime.UtcNow
                    });
                }
            }
            await writeDb.SaveChangesAsync(ct);
            _logger.LogDebug("MLMrmrFeatureWorker ranked {F} features for {S}/{T}.",
                F, model.Symbol, model.Timeframe);
        }
    }

    private static int[] Discretise(float[] values, int bins)
    {
        float min = values.Min(), max = values.Max();
        float range = max - min;
        if (range < 1e-9f) return new int[values.Length];
        return values.Select(v => (int)Math.Min(bins - 1, (v - min) / range * bins)).ToArray();
    }

    private static double MutualInfo(int[] x, int[] y, int binsX, int binsY)
    {
        int n = x.Length;
        int[,] joint = new int[binsX, binsY];
        int[] px = new int[binsX], py = new int[binsY];
        for (int i = 0; i < n; i++)
        {
            int xi = Math.Min(x[i], binsX - 1);
            int yi = Math.Min(y[i], binsY - 1);
            joint[xi, yi]++;
            px[xi]++; py[yi]++;
        }
        double mi = 0;
        for (int a = 0; a < binsX; a++)
            for (int b = 0; b < binsY; b++)
            {
                if (joint[a, b] == 0) continue;
                double pxy = (double)joint[a, b] / n;
                double pa  = (double)px[a] / n;
                double pb  = (double)py[b] / n;
                mi += pxy * Math.Log(pxy / (pa * pb));
            }
        return mi;
    }
}
