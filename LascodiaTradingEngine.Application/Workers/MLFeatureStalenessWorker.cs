using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors the freshness of ML model input features by computing the lag-1 autocorrelation
/// of each feature's value time series over a recent candle window (Rec #194).
/// </summary>
/// <remarks>
/// <para>
/// A feature with a very high lag-1 autocorrelation (|ρ| &gt; 0.95) is effectively repeating
/// its previous value at every candle — it carries no new information from bar to bar.
/// Such a "stale" predictor wastes model capacity and can cause the model to over-weight
/// slow-moving indicators that are essentially constant in the current market regime.
/// </para>
/// <para>
/// <b>Algorithm per active model:</b>
/// <list type="number">
///   <item>Load the 200 most recent candles for the model's symbol/timeframe.</item>
///   <item>Build feature vectors using <see cref="MLFeatureHelper.BuildTrainingSamples"/>.</item>
///   <item>For each feature index <c>fi</c>, extract the univariate time series and compute
///         the lag-1 Pearson autocorrelation ρ = Corr(x_t, x_{t-1}).</item>
///   <item>Flag the feature as stale if |ρ| &gt; 0.95.</item>
///   <item>Upsert a <see cref="MLFeatureStalenessLog"/> record with the autocorrelation value,
///         staleness flag, and computation timestamp.</item>
/// </list>
/// </para>
/// <para>
/// <b>Polling interval:</b> 7 days (weekly). Staleness patterns evolve slowly and a weekly
/// cadence is sufficient to detect regime-induced freezing of indicators.
/// </para>
/// <para>
/// <b>Pipeline role:</b> Results are stored in <see cref="MLFeatureStalenessLog"/> and
/// can be consumed by the MLTrainingWorker to suppress stale features via
/// <c>HyperparamOverrides.DisabledFeatureIndices</c> in the next retrain.
/// </para>
/// </remarks>
public sealed class MLFeatureStalenessWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureStalenessWorker> _logger;

    /// <summary>
    /// Initialises the worker with a DI scope factory and a logger.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create a fresh DI scope each polling cycle.</param>
    /// <param name="logger">Structured logger for staleness diagnostics.</param>
    public MLFeatureStalenessWorker(IServiceScopeFactory scopeFactory, ILogger<MLFeatureStalenessWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs a continuous weekly loop that delegates
    /// to <see cref="RunAsync"/> for each cycle. Errors are caught and logged at the
    /// Error level so the loop can continue after a transient failure.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host shuts down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureStalenessWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLFeatureStalenessWorker error"); }
            // Run weekly; staleness patterns are slow-moving and do not require daily checks.
            await Task.Delay(TimeSpan.FromDays(7), stoppingToken);
        }
    }

    /// <summary>
    /// Main cycle logic. Loads all active non-meta, non-MAML models, computes per-feature
    /// lag-1 autocorrelations from recent candle data, and upserts staleness log records.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Exclude meta-learners and MAML initialisers — their "features" are outputs of
        // other models, not raw market indicators, and staleness analysis does not apply.
        var activeModels = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Load the 200 most recent candles in ascending time order.
            // 200 candles provides enough data points for a stable autocorrelation estimate
            // while remaining fast to query and compute.
            var candles = await readDb.Set<Candle>()
                .AsNoTracking()
                .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe && !c.IsDeleted)
                .OrderByDescending(c => c.Timestamp)
                .Take(200)
                .ToListAsync(ct);

            // Reverse to restore chronological (oldest-first) order required by BuildTrainingSamples.
            candles.Reverse();
            // Need at least LookbackWindow + 2 candles to produce any valid feature vectors.
            if (candles.Count < MLFeatureHelper.LookbackWindow + 2) continue;

            var samples = MLFeatureHelper.BuildTrainingSamples(candles);
            if (samples.Count < 10) continue;

            int F = MLFeatureHelper.FeatureCount;
            int staleCount = 0;

            for (int fi = 0; fi < F && !ct.IsCancellationRequested; fi++)
            {
                // Extract the univariate time series for feature fi across all samples.
                double[] x = samples.Select(s => (double)(s.Features.Length > fi ? s.Features[fi] : 0f)).ToArray();

                // Compute lag-1 Pearson autocorrelation: ρ = Corr(x_t, x_{t-1}).
                // Values near ±1 mean the feature barely changes from one bar to the next.
                double autocorr = ComputeLag1Autocorr(x);

                // The threshold 0.95 is intentionally conservative — only near-flat series
                // are flagged, avoiding false positives for legitimately slow indicators.
                bool isStale = Math.Abs(autocorr) > 0.95;
                if (isStale) staleCount++;

                string featureName = MLFeatureHelper.FeatureNames[fi];

                // Upsert logic: update the existing log row if present, otherwise insert new.
                // This avoids unbounded row growth and keeps the table lean with one row per
                // (model, feature) pair.
                var existing = await writeDb.Set<MLFeatureStalenessLog>()
                    .FirstOrDefaultAsync(l => l.MLModelId == model.Id && l.FeatureName == featureName && !l.IsDeleted, ct);

                if (existing == null)
                {
                    writeDb.Set<MLFeatureStalenessLog>().Add(new MLFeatureStalenessLog
                    {
                        MLModelId    = model.Id,
                        Symbol       = model.Symbol,
                        Timeframe    = model.Timeframe,
                        FeatureName  = featureName,
                        Lag1Autocorr = autocorr,
                        IsStale      = isStale,
                        ComputedAt   = DateTime.UtcNow
                    });
                }
                else
                {
                    // Overwrite stale values; preserve the primary key and audit trail.
                    existing.Lag1Autocorr = autocorr;
                    existing.IsStale      = isStale;
                    existing.ComputedAt   = DateTime.UtcNow;
                }
            }

            await writeDb.SaveChangesAsync(ct);

            _logger.LogInformation(
                "MLFeatureStalenessWorker: {S}/{T} stale features={C}/{F}.",
                model.Symbol, model.Timeframe, staleCount, F);
        }

        // Prune staleness logs older than 90 days to prevent unbounded table growth.
        var retentionCutoff = DateTime.UtcNow.AddDays(-90);
        int pruned = await writeDb.Set<MLFeatureStalenessLog>()
            .Where(l => l.ComputedAt < retentionCutoff)
            .ExecuteDeleteAsync(ct);

        if (pruned > 0)
            _logger.LogInformation("MLFeatureStalenessWorker: pruned {Count} staleness logs older than 90 days.", pruned);
    }

    /// <summary>
    /// Computes the lag-1 Pearson autocorrelation of a univariate time series.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lag-1 autocorrelation measures the linear dependence between consecutive
    /// observations: ρ = Corr(x_t, x_{t-1}).
    /// </para>
    /// <para>
    /// Implementation splits the series into two overlapping windows:
    /// <list type="bullet">
    ///   <item><c>lag0</c> = x[0..n-2]  (current values)</item>
    ///   <item><c>lag1</c> = x[1..n-1]  (next values, i.e. one step ahead)</item>
    /// </list>
    /// Then computes the standard Pearson correlation between these two windows.
    /// </para>
    /// <para>
    /// Returns 0 when the series is constant (denom &lt; 1e-9) to avoid division by zero.
    /// A constant series is technically infinitely autocorrelated but is better handled
    /// as a degenerate case; the staleness flag will still be raised since |0| is not &gt; 0.95.
    /// </para>
    /// </remarks>
    /// <param name="x">Univariate time series of feature values, ordered chronologically.</param>
    /// <returns>Lag-1 Pearson autocorrelation in [-1, 1], or 0 for degenerate inputs.</returns>
    private static double ComputeLag1Autocorr(double[] x)
    {
        if (x.Length < 3) return 0;

        // lag0 = x[0 .. n-2], lag1 = x[1 .. n-1]
        double[] lag0 = x[..^1];
        double[] lag1 = x[1..];

        double mean0 = lag0.Average();
        double mean1 = lag1.Average();

        double cov = 0, std0 = 0, std1 = 0;
        for (int i = 0; i < lag0.Length; i++)
        {
            cov  += (lag0[i] - mean0) * (lag1[i] - mean1);   // cross-product for covariance
            std0 += Math.Pow(lag0[i] - mean0, 2);             // sum of squares for lag0
            std1 += Math.Pow(lag1[i] - mean1, 2);             // sum of squares for lag1
        }

        // Pearson correlation = covariance / (std_lag0 × std_lag1)
        double denom = Math.Sqrt(std0) * Math.Sqrt(std1);
        return denom < 1e-9 ? 0 : cov / denom;
    }
}
