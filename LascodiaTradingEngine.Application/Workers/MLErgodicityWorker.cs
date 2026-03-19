using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes ergodicity economics metrics for each active ML model (Rec #519).
///
/// <para>
/// Ergodicity economics distinguishes ensemble-average growth (arithmetic mean) from
/// time-average growth (geometric mean). The ergodicity gap between these two quantities
/// drives a downward adjustment to the naive Kelly fraction, producing a position size
/// that maximises long-run compounded wealth rather than expected value.
/// </para>
///
/// Algorithm per cycle:
/// <list type="number">
///   <item>Load active, non-deleted ML models.</item>
///   <item>For each model, load the last WindowDays (up to 200) resolved prediction logs.</item>
///   <item>Convert outcomes to "returns": correct prediction = +confidence-0.5; incorrect = -(confidence-0.5).</item>
///   <item>Compute ensemble growth rate, Peters time-average, ergodicity gap.</item>
///   <item>Compute naive and ergodicity-adjusted Kelly fractions.</item>
///   <item>Persist <see cref="MLErgodicityLog"/> per model.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLErgodicity:PollIntervalHours</c> — default 24</item>
///   <item><c>MLErgodicity:WindowDays</c>        — rolling history in days, default 30</item>
///   <item><c>MLErgodicity:MinSamples</c>        — minimum prediction log count required, default 20</item>
/// </list>
/// </summary>
public sealed class MLErgodicityWorker : BackgroundService
{
    private const string CK_PollHours  = "MLErgodicity:PollIntervalHours";
    private const string CK_WindowDays = "MLErgodicity:WindowDays";
    private const string CK_MinSamples = "MLErgodicity:MinSamples";

    private readonly IServiceScopeFactory        _scopeFactory;
    private readonly ILogger<MLErgodicityWorker> _logger;

    public MLErgodicityWorker(
        IServiceScopeFactory         scopeFactory,
        ILogger<MLErgodicityWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLErgodicityWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollHours = 24;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollHours      = await GetConfigAsync<int>(ctx, CK_PollHours,  24, stoppingToken);
                int windowDays = await GetConfigAsync<int>(ctx, CK_WindowDays, 30, stoppingToken);
                int minSamples = await GetConfigAsync<int>(ctx, CK_MinSamples, 20, stoppingToken);

                await RunErgodicityAsync(ctx, wCtx, windowDays, minSamples, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLErgodicityWorker loop error");
            }

            await Task.Delay(TimeSpan.FromHours(pollHours), stoppingToken);
        }

        _logger.LogInformation("MLErgodicityWorker stopping.");
    }

    // ── Ergodicity core ───────────────────────────────────────────────────────

    private async Task RunErgodicityAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minSamples,
        CancellationToken                       ct)
    {
        var now    = DateTime.UtcNow;
        var cutoff = now.AddDays(-windowDays);

        var models = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => !m.IsDeleted && m.IsActive)
            .ToListAsync(ct);

        foreach (var model in models)
        {
            var logs = await readCtx.Set<MLModelPredictionLog>()
                .AsNoTracking()
                .Where(l => l.MLModelId == model.Id && !l.IsDeleted && l.PredictedAt >= cutoff
                            && l.DirectionCorrect.HasValue)
                .OrderByDescending(l => l.PredictedAt)
                .Take(200)
                .ToListAsync(ct);

            if (logs.Count < minSamples)
            {
                _logger.LogDebug("MLErgodicityWorker: model {Id} ({Sym}) skipped — only {N} logs",
                    model.Id, model.Symbol, logs.Count);
                continue;
            }

            // Convert to returns
            double[] r = new double[logs.Count];
            for (int i = 0; i < logs.Count; i++)
            {
                double conf = (double)logs[i].ConfidenceScore;
                r[i] = logs[i].DirectionCorrect == true
                    ? conf - 0.5
                    : -(conf - 0.5);
            }

            // Ensemble growth rate (arithmetic mean)
            double mu = r.Average();

            // Peters time-average growth: mean(log(1 + max(r, -0.9999)))
            double timeAvg = r.Average(v => Math.Log(1.0 + Math.Max(v, -0.9999)));

            // Ergodicity gap
            double gap = mu - timeAvg;

            // Variance of returns
            double sigma2 = r.Sum(v => (v - mu) * (v - mu)) / Math.Max(r.Length - 1, 1);

            // Naive Kelly = mu / sigma2
            double naiveKelly = mu / Math.Max(sigma2, 1e-10);

            // Ergodicity-adjusted Kelly: clamp to [-2, 2]
            double adjKelly = naiveKelly * (1.0 - gap / Math.Max(sigma2, 1e-10));
            adjKelly = Math.Max(-2.0, Math.Min(2.0, adjKelly));

            var log = new MLErgodicityLog
            {
                MLModelId                = model.Id,
                Symbol                   = model.Symbol,
                EnsembleGrowthRate       = (decimal)mu,
                TimeAverageGrowthRate    = (decimal)timeAvg,
                ErgodicityGap            = (decimal)gap,
                NaiveKellyFraction       = (decimal)naiveKelly,
                ErgodicityAdjustedKelly  = (decimal)adjKelly,
                GrowthRateVariance       = (decimal)sigma2,
                ComputedAt               = now,
            };

            writeCtx.Set<MLErgodicityLog>().Add(log);
            await writeCtx.SaveChangesAsync(ct);

            _logger.LogDebug(
                "MLErgodicityWorker: model {Id} ({Sym}) mu={Mu:F4} timeAvg={TA:F4} gap={G:F4} kelly={K:F4} adjKelly={AK:F4}",
                model.Id, model.Symbol, mu, timeAvg, gap, naiveKelly, adjKelly);
        }
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
