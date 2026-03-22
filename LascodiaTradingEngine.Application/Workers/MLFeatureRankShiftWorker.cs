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

    /// <summary>
    /// Initialises the worker with a DI scope factory and a logger.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create a fresh DI scope each polling cycle.</param>
    /// <param name="logger">Structured logger for rank-shift diagnostics and alerts.</param>
    public MLFeatureRankShiftWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLFeatureRankShiftWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs a continuous polling loop that:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope and resolves the read/write DbContexts.</item>
    ///   <item>Reads the poll interval from <see cref="EngineConfig"/>
    ///         (key <c>MLFeatureRankShift:PollIntervalSeconds</c>, default 3600 s = 1 h).</item>
    ///   <item>Delegates rank-shift detection to <see cref="CheckRankShiftsAsync"/>.</item>
    ///   <item>Waits for the configured interval before repeating.</item>
    /// </list>
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureRankShiftWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval (1 hour). Overridden from EngineConfig each cycle.
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

    /// <summary>
    /// Loads all runtime configuration values and iterates over every currently active
    /// model (the "champion"). For each champion, it looks for the most recently
    /// superseded predecessor within the configured lookback window and delegates to
    /// <see cref="CompareModelsAsync"/> for Spearman rank comparison.
    /// </summary>
    /// <param name="readCtx">Read DbContext for querying model records.</param>
    /// <param name="writeCtx">Write DbContext for persisting alerts.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task CheckRankShiftsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load configuration for this cycle.
        int    topN       = await GetConfigAsync<int>   (readCtx, CK_TopN,      10,      ct);
        double threshold  = await GetConfigAsync<double>(readCtx, CK_Threshold, 0.50,    ct);
        int    lookback   = await GetConfigAsync<int>   (readCtx, CK_Lookback,  7,       ct);
        string alertDest  = await GetConfigAsync<string>(readCtx, CK_AlertDest, "ml-ops", ct);

        // Only compare champions against predecessors trained within the lookback window.
        // Beyond this window, drift is expected and not operationally actionable.
        var since = DateTime.UtcNow.AddDays(-lookback);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var champion in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Find the most recently superseded predecessor for this symbol/timeframe.
                // "Most recently superseded" gives the closest prior champion, making the
                // rank shift comparison as generation-local as possible.
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

    /// <summary>
    /// Compares the feature importance rankings of a champion model against its predecessor
    /// using Spearman rank correlation over the union of their top-N features.
    /// Fires an <see cref="AlertType.MLModelDegraded"/> alert when the correlation falls
    /// below the configured threshold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison uses the union (not intersection) of each model's top-N features so
    /// that newly introduced or dropped features are counted as rank divergence rather than
    /// being silently excluded.
    /// </para>
    /// <para>
    /// A feature that exists in one model but not the other is assigned an importance of 0.0
    /// for the model it is absent from. This causes it to rank last, amplifying the detected
    /// shift appropriately.
    /// </para>
    /// <para>
    /// Spearman rank correlation ranges from -1 (perfectly reversed ranking) to +1
    /// (identical ranking). A healthy retrain usually produces correlations above 0.7.
    /// Falling below 0.5 (the default threshold) indicates the new model is relying on a
    /// substantially different feature set.
    /// </para>
    /// </remarks>
    /// <param name="champion">The currently active model (most recently promoted).</param>
    /// <param name="predecessor">The immediately prior champion for the same symbol/timeframe.</param>
    /// <param name="topN">Number of top features from each model to include in the union comparison.</param>
    /// <param name="threshold">Spearman correlation below which an alert is raised.</param>
    /// <param name="alertDest">Destination string embedded in the alert record.</param>
    /// <param name="readCtx">Read DbContext for deduplication checks.</param>
    /// <param name="writeCtx">Write DbContext for persisting alerts.</param>
    /// <param name="ct">Cancellation token.</param>
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

        // Extract feature-name → importance-score dictionaries for both models.
        // See ExtractImportance for the priority logic (stored scores vs. weight-derived).
        var champImportance = ExtractImportance(champSnap);
        var predImportance  = ExtractImportance(predSnap);

        if (champImportance.Count == 0 || predImportance.Count == 0) return;

        // Take union of top-N feature names from both models.
        // Union ensures that features new in the champion or dropped from the predecessor
        // appear in the comparison as a rank divergence rather than being ignored.
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

        // Require at least 3 features in the union for a meaningful rank correlation.
        // With only 1-2 elements, Spearman is trivially 1 or -1 regardless of actual shift.
        if (union.Count < 3) return;

        // Build parallel score arrays over the union.
        // Features absent from a model receive a score of 0.0, ranking them last.
        var champScores = union.Select(f => champImportance.TryGetValue(f, out var v) ? v : 0.0).ToArray();
        var predScores  = union.Select(f => predImportance.TryGetValue(f, out var v)  ? v : 0.0).ToArray();

        // Spearman rank correlation measures how well the ordinal ranking of features
        // is preserved between the two models, regardless of the absolute importance values.
        double correlation = SpearmanRank(champScores, predScores);

        _logger.LogDebug(
            "FeatureRankShift: {Symbol}/{Tf} champion={ChampId} predecessor={PredId} " +
            "spearman={Corr:F3} union={N} features",
            champion.Symbol, champion.Timeframe, champion.Id, predecessor.Id, correlation, union.Count);

        // No alert needed — ranking is sufficiently stable.
        if (correlation >= threshold) return;

        // Identify the top-5 features with the greatest absolute importance delta between
        // champion and predecessor to help operators understand the nature of the shift.
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

        // Deduplicate: suppress a new alert if one is already active for this symbol.
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
                // Top diverging feature names for quick triage.
                topDivergingFeatures = diverging.Select(x => x.Feature).ToArray(),
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Feature importance extraction ─────────────────────────────────────────

    /// <summary>
    /// Returns a feature-name → importance-score dictionary from a <see cref="ModelSnapshot"/>.
    /// </summary>
    /// <remarks>
    /// Two sources are tried in priority order:
    /// <list type="number">
    ///   <item><b>Stored FeatureImportanceScores</b> — explicitly computed during training
    ///         (e.g. permutation importance or SHAP-based scores). Preferred because they
    ///         reflect a direct measurement of each feature's contribution.</item>
    ///   <item><b>Ensemble-averaged absolute weights</b> — fallback for older snapshots that
    ///         predate the importance-score field. For a bagged logistic ensemble, the absolute
    ///         value of the logistic weight is a reasonable proxy for feature importance.</item>
    /// </list>
    /// Returns an empty dictionary when neither source is available (prevents NPEs downstream).
    /// </remarks>
    /// <param name="snap">Deserialised model snapshot.</param>
    /// <returns>Dictionary mapping each feature name to its importance score (higher = more important).</returns>
    private static Dictionary<string, double> ExtractImportance(ModelSnapshot snap)
    {
        // Prefer explicit importance scores when available.
        if (snap.FeatureImportanceScores.Length > 0 &&
            snap.Features.Length >= snap.FeatureImportanceScores.Length)
        {
            return Enumerable.Range(0, snap.FeatureImportanceScores.Length)
                .ToDictionary(
                    i => snap.Features[i],
                    i => snap.FeatureImportanceScores[i]);
        }

        // Fallback: ensemble-averaged absolute weight per feature.
        // For each learner in the bagged ensemble, sum |weight_j| across all learners,
        // then divide by the number of learners to produce a normalised average.
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

    /// <summary>
    /// Computes the Spearman rank correlation coefficient between two equal-length arrays.
    /// </summary>
    /// <remarks>
    /// Spearman's ρ = 1 − (6 × Σd²) / (n × (n² − 1))
    /// where d_i is the difference in ranks of the i-th element between x and y.
    /// <para>
    /// This formula is equivalent to the Pearson correlation of the rank vectors and is
    /// robust to non-normal distributions and outliers — both common with feature importance
    /// scores, which are typically right-skewed with a long tail of near-zero values.
    /// </para>
    /// Returns 1.0 for arrays with fewer than 2 elements (trivially identical).
    /// </remarks>
    /// <param name="x">Importance scores for the champion model, indexed by feature.</param>
    /// <param name="y">Importance scores for the predecessor model, same indexing.</param>
    /// <returns>Spearman rank correlation in [-1, 1]; higher means more similar ranking.</returns>
    private static double SpearmanRank(double[] x, double[] y)
    {
        int n = x.Length;
        if (n < 2) return 1.0;

        // Assign descending ranks (rank 1 = highest value) to each element.
        // LINQ pipeline: attach original index → sort descending by value → assign rank → restore original order.
        int[] rxOrder = x.Select((v, i) => (v, i)).OrderByDescending(t => t.v)
                         .Select((t, rank) => (t.i, rank)).OrderBy(t => t.i)
                         .Select(t => t.rank + 1).ToArray();
        int[] ryOrder = y.Select((v, i) => (v, i)).OrderByDescending(t => t.v)
                         .Select((t, rank) => (t.i, rank)).OrderBy(t => t.i)
                         .Select(t => t.rank + 1).ToArray();

        // Compute Σd² where d_i = rxOrder[i] − ryOrder[i].
        double sumDSq = 0;
        for (int i = 0; i < n; i++)
        {
            double d = rxOrder[i] - ryOrder[i];
            sumDSq += d * d;
        }

        // Apply the standard Spearman formula.
        return 1.0 - (6.0 * sumDSq) / ((double)n * (n * n - 1));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to deserialise a model snapshot from its byte representation.
    /// Returns <c>null</c> and logs at Debug level on failure rather than propagating exceptions.
    /// </summary>
    /// <param name="bytes">Raw JSON bytes stored in <see cref="MLModel.ModelBytes"/>.</param>
    /// <param name="modelId">Model ID used in the debug log for traceability.</param>
    /// <returns>Deserialised <see cref="ModelSnapshot"/> or <c>null</c> if the bytes are corrupt/missing.</returns>
    private ModelSnapshot? TryDeserialise(byte[] bytes, long modelId)
    {
        try   { return JsonSerializer.Deserialize<ModelSnapshot>(bytes, JsonOpts); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FeatureRankShift: failed to deserialise model {Id}", modelId);
            return null;
        }
    }

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key is absent or cannot be converted.
    /// </summary>
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
