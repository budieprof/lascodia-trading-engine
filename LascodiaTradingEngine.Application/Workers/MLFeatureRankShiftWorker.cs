using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects structural feature-importance shifts between consecutive ML model generations,
/// providing an early warning when a newly promoted model has learned a fundamentally
/// different set of predictive signals than its predecessor.
///
/// <b>Problem:</b> A new model may pass walk-forward accuracy thresholds while relying on
/// entirely different features. This can indicate overfitting to a recent regime, a data
/// preparation error, or a genuine structural break — none of which are caught by accuracy
/// metrics alone. Operators should investigate before trusting the new model in live trading.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>For each symbol/timeframe, find the current active model and its most recently
///         superseded predecessor (the previous champion).</item>
///   <item>For each model, extract a feature importance vector. Priority: stored
///         <c>FeatureImportanceScores</c> → ensemble-averaged absolute weights.</item>
///   <item>Take the union of the top-N features from both models and compute the
///         Spearman rank correlation of their importance scores across that union.</item>
///   <item>If the correlation is below <c>RankCorrelationThreshold</c>, fire an
///         <see cref="AlertType.MLModelDegraded"/> alert with the diverging feature names.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLFeatureRankShift:PollIntervalSeconds</c>       — default 3600 (1 h)</item>
///   <item><c>MLFeatureRankShift:TopFeatures</c>               — top-N features compared, default 10</item>
///   <item><c>MLFeatureRankShift:RankCorrelationThreshold</c>  — alert below this, default 0.50</item>
///   <item><c>MLFeatureRankShift:LookbackDays</c>              — how far back to look for superseded models, default 7</item>
///   <item><c>MLFeatureRankShift:AlertDestination</c>          — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLFeatureRankShiftWorker : BackgroundService
{
    private const string CK_PollSecs   = "MLFeatureRankShift:PollIntervalSeconds";
    private const string CK_TopN       = "MLFeatureRankShift:TopFeatures";
    private const string CK_Threshold  = "MLFeatureRankShift:RankCorrelationThreshold";
    private const string CK_Lookback   = "MLFeatureRankShift:LookbackDays";
    private const string CK_AlertDest  = "MLFeatureRankShift:AlertDestination";

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLFeatureRankShiftWorker> _logger;

    public MLFeatureRankShiftWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLFeatureRankShiftWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureRankShiftWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await CheckRankShiftsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLFeatureRankShiftWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLFeatureRankShiftWorker stopping.");
    }

    // ── Rank-shift detection core ─────────────────────────────────────────────

    private async Task CheckRankShiftsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    topN       = await GetConfigAsync<int>   (readCtx, CK_TopN,      10,      ct);
        double threshold  = await GetConfigAsync<double>(readCtx, CK_Threshold, 0.50,    ct);
        int    lookback   = await GetConfigAsync<int>   (readCtx, CK_Lookback,  7,       ct);
        string alertDest  = await GetConfigAsync<string>(readCtx, CK_AlertDest, "ml-ops", ct);

        var since = DateTime.UtcNow.AddDays(-lookback);

        // Get current active models
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var champion in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Find the most recently superseded predecessor for this symbol/timeframe
                var predecessor = await readCtx.Set<MLModel>()
                    .Where(m => m.Symbol    == champion.Symbol    &&
                                m.Timeframe == champion.Timeframe  &&
                                m.Status    == MLModelStatus.Superseded &&
                                !m.IsDeleted &&
                                m.ModelBytes != null &&
                                m.TrainedAt >= since)
                    .OrderByDescending(m => m.TrainedAt)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(ct);

                if (predecessor is null) continue;

                await CompareModelsAsync(
                    champion, predecessor, topN, threshold, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FeatureRankShift: check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    champion.Id, champion.Symbol, champion.Timeframe);
            }
        }
    }

    private async Task CompareModelsAsync(
        MLModel                                 champion,
        MLModel                                 predecessor,
        int                                     topN,
        double                                  threshold,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        var champSnap = TryDeserialise(champion.ModelBytes!, champion.Id);
        var predSnap  = TryDeserialise(predecessor.ModelBytes!, predecessor.Id);

        if (champSnap is null || predSnap is null) return;

        var champImportance = ExtractImportance(champSnap);
        var predImportance  = ExtractImportance(predSnap);

        if (champImportance.Count == 0 || predImportance.Count == 0) return;

        // Take union of top-N feature names from both models
        var champTopN = champImportance
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv => kv.Key)
            .ToHashSet();
        var predTopN = predImportance
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv => kv.Key)
            .ToHashSet();
        var union = champTopN.Union(predTopN).ToList();

        if (union.Count < 3) return; // too few features for meaningful correlation

        // Rank importance values across the union for each model
        var champScores = union.Select(f => champImportance.TryGetValue(f, out var v) ? v : 0.0).ToArray();
        var predScores  = union.Select(f => predImportance.TryGetValue(f, out var v)  ? v : 0.0).ToArray();

        double correlation = SpearmanRank(champScores, predScores);

        _logger.LogDebug(
            "FeatureRankShift: {Symbol}/{Tf} champion={ChampId} predecessor={PredId} " +
            "spearman={Corr:F3} union={N} features",
            champion.Symbol, champion.Timeframe, champion.Id, predecessor.Id, correlation, union.Count);

        if (correlation >= threshold) return;

        // Alert: top diverging features
        var diverging = union
            .Select(f => new
            {
                Feature    = f,
                ChampRank  = champImportance.TryGetValue(f, out var cv) ? cv : 0.0,
                PredRank   = predImportance.TryGetValue(f, out var pv) ? pv : 0.0,
            })
            .OrderByDescending(x => Math.Abs(x.ChampRank - x.PredRank))
            .Take(5)
            .ToList();

        _logger.LogWarning(
            "FeatureRankShift: {Symbol}/{Tf} champion={ChampId} predecessor={PredId} — " +
            "Spearman={Corr:F3} below threshold {Thr:F2}. Top diverging: {Features}",
            champion.Symbol, champion.Timeframe, champion.Id, predecessor.Id,
            correlation, threshold,
            string.Join(", ", diverging.Select(x => x.Feature)));

        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == champion.Symbol             &&
                           a.AlertType == AlertType.MLModelDegraded   &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = champion.Symbol,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = JsonSerializer.Serialize(new
            {
                reason              = "feature_rank_shift",
                severity            = "warning",
                symbol              = champion.Symbol,
                timeframe           = champion.Timeframe.ToString(),
                championModelId     = champion.Id,
                predecessorModelId  = predecessor.Id,
                spearmanCorrelation = correlation,
                threshold,
                topDivergingFeatures = diverging.Select(x => x.Feature).ToArray(),
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Feature importance extraction ─────────────────────────────────────────

    /// <summary>
    /// Returns a feature-name → importance-score dictionary.
    /// Priority: stored FeatureImportanceScores → ensemble-averaged |weights|.
    /// </summary>
    private static Dictionary<string, double> ExtractImportance(ModelSnapshot snap)
    {
        // Prefer explicit importance scores when available
        if (snap.FeatureImportanceScores.Length > 0 &&
            snap.Features.Length >= snap.FeatureImportanceScores.Length)
        {
            return Enumerable.Range(0, snap.FeatureImportanceScores.Length)
                .ToDictionary(
                    i => snap.Features[i],
                    i => snap.FeatureImportanceScores[i]);
        }

        // Fallback: ensemble-averaged absolute weight per feature
        if (snap.Weights.Length == 0 || snap.Features.Length == 0)
            return new Dictionary<string, double>();

        int fCount = snap.Features.Length;
        var sums   = new double[fCount];
        foreach (var learnerWeights in snap.Weights)
        {
            for (int j = 0; j < fCount && j < learnerWeights.Length; j++)
                sums[j] += Math.Abs(learnerWeights[j]);
        }

        double n = snap.Weights.Length;
        return Enumerable.Range(0, fCount)
            .ToDictionary(i => snap.Features[i], i => sums[i] / n);
    }

    // ── Spearman rank correlation ─────────────────────────────────────────────

    private static double SpearmanRank(double[] x, double[] y)
    {
        int n = x.Length;
        if (n < 2) return 1.0;

        int[] rxOrder = x.Select((v, i) => (v, i)).OrderByDescending(t => t.v)
                         .Select((t, rank) => (t.i, rank)).OrderBy(t => t.i)
                         .Select(t => t.rank + 1).ToArray();
        int[] ryOrder = y.Select((v, i) => (v, i)).OrderByDescending(t => t.v)
                         .Select((t, rank) => (t.i, rank)).OrderBy(t => t.i)
                         .Select(t => t.rank + 1).ToArray();

        double sumDSq = 0;
        for (int i = 0; i < n; i++)
        {
            double d = rxOrder[i] - ryOrder[i];
            sumDSq += d * d;
        }

        return 1.0 - (6.0 * sumDSq) / ((double)n * (n * n - 1));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ModelSnapshot? TryDeserialise(byte[] bytes, long modelId)
    {
        try   { return JsonSerializer.Deserialize<ModelSnapshot>(bytes, JsonOpts); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FeatureRankShift: failed to deserialise model {Id}", modelId);
            return null;
        }
    }

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
