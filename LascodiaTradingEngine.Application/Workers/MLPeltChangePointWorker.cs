using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// PELT (Pruned Exact Linear Time) change point detection — exact globally-optimal multiple
/// change point detection via DP with pruning; O(n) amortised. Runs every 24 hours.
/// </summary>
public sealed class MLPeltChangePointWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLPeltChangePointWorker> _logger;

    public MLPeltChangePointWorker(IServiceScopeFactory scopeFactory, ILogger<MLPeltChangePointWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLPeltChangePointWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLPeltChangePointWorker error"); }
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
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .ToListAsync(ct);

        foreach (var model in models)
        {
            var candles = await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe
                         && c.Timestamp >= DateTime.UtcNow.AddDays(-90) && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            if (candles.Count < 10) continue;

            int n = candles.Count;
            double[] returns = new double[n - 1];
            for (int i = 0; i < n - 1; i++)
                returns[i] = ((double)candles[i + 1].Close - (double)candles[i].Close)
                             / ((double)candles[i].Close + 1e-8);

            int m = returns.Length;
            double penalty = Math.Log(m); // BIC penalty

            // Precompute prefix sums for cost function
            double[] prefSum  = new double[m + 1];
            double[] prefSum2 = new double[m + 1];
            for (int i = 0; i < m; i++)
            {
                prefSum[i + 1]  = prefSum[i]  + returns[i];
                prefSum2[i + 1] = prefSum2[i] + returns[i] * returns[i];
            }

            // PELT DP
            double[] F    = new double[m + 1];
            int[]    prev = new int[m + 1];
            F[0] = -penalty;
            var candidates = new List<int> { 0 };

            for (int t = 1; t <= m; t++)
            {
                double bestCost = double.MaxValue;
                int bestTau = 0;
                foreach (int tau in candidates)
                {
                    double cost = F[tau] + SegmentCost(tau, t, prefSum, prefSum2) + penalty;
                    if (cost < bestCost) { bestCost = cost; bestTau = tau; }
                }
                F[t]    = bestCost;
                prev[t] = bestTau;

                // Pruning: remove candidates where F[tau] + min_cost(tau..n) > F[t]
                var pruned = new List<int>();
                foreach (int tau in candidates)
                    if (F[tau] + SegmentCost(tau, t, prefSum, prefSum2) <= F[t])
                        pruned.Add(tau);
                pruned.Add(t);
                candidates = pruned;
            }

            // Backtrack to find change points
            var changePoints = new List<int>();
            int cur = m;
            while (prev[cur] != 0)
            {
                changePoints.Add(prev[cur]);
                cur = prev[cur];
            }
            changePoints.Reverse();

            writeDb.Set<MLPeltChangePointLog>().Add(new MLPeltChangePointLog
            {
                MLModelId              = model.Id,
                Symbol                 = model.Symbol,
                Timeframe              = model.Timeframe.ToString(),
                ChangePointCount       = changePoints.Count,
                ChangePointIndicesJson = JsonSerializer.Serialize(changePoints),
                Penalty                = penalty,
                TotalCost              = F[m],
                ComputedAt             = DateTime.UtcNow
            });

            await writeDb.SaveChangesAsync(ct);

            _logger.LogInformation(
                "MLPeltChangePointWorker: {S}/{T} changePoints={CP} totalCost={Cost:F4} penalty={Pen:F4}",
                model.Symbol, model.Timeframe, changePoints.Count, F[m], penalty);
        }
    }

    /// <summary>Gaussian negative log-likelihood cost for segment [start, end).</summary>
    private static double SegmentCost(int start, int end, double[] prefSum, double[] prefSum2)
    {
        int len = end - start;
        if (len <= 0) return 0;
        double sum  = prefSum[end]  - prefSum[start];
        double sum2 = prefSum2[end] - prefSum2[start];
        double mean = sum / len;
        double var  = sum2 / len - mean * mean;
        if (var <= 0) var = 1e-10;
        return len * (Math.Log(var) + 1);
    }
}
