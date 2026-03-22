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
/// Detects "dying features" — features whose importance has been declining monotonically
/// across consecutive model generations for the same symbol/timeframe.
///
/// When a feature's contribution consistently weakens with each retrain, it is likely
/// a stale or vanishing signal that no longer carries predictive information in the
/// current market. Continuing to include it wastes model capacity and can add noise.
///
/// Algorithm (per active symbol/timeframe):
/// <list type="number">
///   <item>Load the most recent <c>MLFeatureImpTrend:GenerationsToCheck</c> model generations
///         (all <see cref="MLModel"/> records ordered by <c>TrainedAt</c> descending,
///         including superseded models whose <c>ModelBytes</c> are still available).</item>
///   <item>Deserialise each snapshot and extract its <see cref="ModelSnapshot.FeatureImportanceScores"/>.</item>
///   <item>For each feature index <c>j</c>, check whether the importance sequence is
///         <em>strictly monotonically decreasing</em> across all loaded generations.</item>
///   <item>If a feature is monotonically declining AND its latest-generation importance
///         is below <c>MLFeatureImpTrend:ImportanceDecayThreshold</c>, flag it.</item>
///   <item>Log a warning listing all dying features and create a deduplicated
///         <see cref="AlertType.MLModelDegraded"/> alert.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLFeatureImpTrend:PollIntervalSeconds</c>    — default 86400 (24 h)</item>
///   <item><c>MLFeatureImpTrend:GenerationsToCheck</c>     — number of past model versions, default 4</item>
///   <item><c>MLFeatureImpTrend:MinGenerations</c>         — skip if fewer generations available, default 3</item>
///   <item><c>MLFeatureImpTrend:ImportanceDecayThreshold</c> — latest-gen threshold below which the
///              feature is truly dead, default 0.005</item>
/// </list>
/// </summary>
public sealed class MLFeatureImportanceTrendWorker : BackgroundService
{
    private const string CK_PollSecs        = "MLFeatureImpTrend:PollIntervalSeconds";
    private const string CK_Generations     = "MLFeatureImpTrend:GenerationsToCheck";
    private const string CK_MinGenerations  = "MLFeatureImpTrend:MinGenerations";
    private const string CK_DecayThreshold  = "MLFeatureImpTrend:ImportanceDecayThreshold";

    private readonly IServiceScopeFactory                      _scopeFactory;
    private readonly ILogger<MLFeatureImportanceTrendWorker>   _logger;

    /// <summary>
    /// Initialises the worker with a DI scope factory and a logger.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create a fresh DI scope each polling cycle,
    /// ensuring EF Core DbContexts are properly scoped and disposed.</param>
    /// <param name="logger">Structured logger for trend diagnostics and alerts.</param>
    public MLFeatureImportanceTrendWorker(
        IServiceScopeFactory                     scopeFactory,
        ILogger<MLFeatureImportanceTrendWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs a continuous polling loop that:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope and resolves the read/write DbContexts.</item>
    ///   <item>Reads the poll interval from <see cref="EngineConfig"/> (key
    ///         <c>MLFeatureImpTrend:PollIntervalSeconds</c>, default 86400 s = 24 h).</item>
    ///   <item>Delegates trend analysis to <see cref="CheckImportanceTrendsAsync"/>.</item>
    ///   <item>Waits for the configured interval before the next cycle.</item>
    /// </list>
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureImportanceTrendWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval (24 hours). Overridden from EngineConfig each cycle.
            int pollSecs = 86400;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 86400, stoppingToken);

                await CheckImportanceTrendsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLFeatureImportanceTrendWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLFeatureImportanceTrendWorker stopping.");
    }

    /// <summary>
    /// Reads configuration values and orchestrates per-symbol/timeframe trend checks.
    /// Discovers distinct (symbol, timeframe) pairs from currently active models and
    /// calls <see cref="CheckSymbolTfTrendAsync"/> for each, isolating failures per pair.
    /// </summary>
    /// <param name="readCtx">Read DbContext for querying models and their snapshots.</param>
    /// <param name="writeCtx">Write DbContext for persisting alerts.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task CheckImportanceTrendsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load configuration values once per cycle.
        int    generationsToCheck = await GetConfigAsync<int>   (readCtx, CK_Generations,    4,     ct);
        int    minGenerations     = await GetConfigAsync<int>   (readCtx, CK_MinGenerations, 3,     ct);
        double decayThreshold     = await GetConfigAsync<double>(readCtx, CK_DecayThreshold, 0.005, ct);

        // Only examine non-regime-specific models (RegimeScope == null) to avoid
        // cross-contaminating trend signals between regime-partitioned model variants.
        var symbolTfPairs = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.RegimeScope == null)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var pair in symbolTfPairs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckSymbolTfTrendAsync(
                    pair.Symbol, pair.Timeframe, readCtx, writeCtx,
                    generationsToCheck, minGenerations, decayThreshold, ct);
            }
            catch (Exception ex)
            {
                // Isolate failures — a corrupt snapshot for one pair must not block others.
                _logger.LogWarning(ex,
                    "Feature importance trend check failed for {Symbol}/{Tf} — skipping.",
                    pair.Symbol, pair.Timeframe);
            }
        }
    }

    /// <summary>
    /// Performs the monotone-decay importance trend check for a single (symbol, timeframe) pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The algorithm loads up to <paramref name="generationsToCheck"/> model versions for this
    /// pair (ordered oldest to newest), then for each feature checks whether its importance
    /// score is strictly monotonically decreasing across all loaded generations. A feature
    /// is deemed "dying" if:
    /// <list type="bullet">
    ///   <item>It is strictly decreasing across all consecutive generation pairs, AND</item>
    ///   <item>Its importance in the most recent generation is below <paramref name="decayThreshold"/>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Strict monotone decrease is a conservative criterion — any non-decreasing step
    /// (even slight plateaus) disqualifies the feature, reducing false positives.
    /// The additional absolute threshold (<paramref name="decayThreshold"/> = 0.005 by default)
    /// ensures that only features that have truly become negligible are flagged, not those
    /// still carrying modest but declining weight.
    /// </para>
    /// <para>
    /// A single deduplicated <see cref="AlertType.MLModelDegraded"/> alert is raised per
    /// symbol if any dying features are found. Deduplication prevents alert flooding when
    /// the worker runs daily and the operator has not yet acted on the previous alert.
    /// </para>
    /// </remarks>
    /// <param name="symbol">Trading symbol (e.g. "EURUSD").</param>
    /// <param name="timeframe">Candle timeframe (e.g. H1).</param>
    /// <param name="readCtx">Read DbContext.</param>
    /// <param name="writeCtx">Write DbContext.</param>
    /// <param name="generationsToCheck">Maximum number of past model versions to include in the trend window.</param>
    /// <param name="minGenerations">Minimum number of generations required to perform the check; fewer → skip.</param>
    /// <param name="decayThreshold">Absolute importance floor below which a monotonically-declining
    /// feature is classified as "dying".</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task CheckSymbolTfTrendAsync(
        string                                  symbol,
        Timeframe                               timeframe,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     generationsToCheck,
        int                                     minGenerations,
        double                                  decayThreshold,
        CancellationToken                       ct)
    {
        // Load the last N model generations (including superseded) ordered oldest → newest.
        // Including superseded models gives us the historical importance trajectory even
        // after the model has been retrained and a new champion has been promoted.
        var models = await readCtx.Set<MLModel>()
            .Where(m => m.Symbol      == symbol    &&
                        m.Timeframe   == timeframe  &&
                        m.RegimeScope == null       &&
                        m.ModelBytes  != null       &&
                        !m.IsDeleted)
            .OrderByDescending(m => m.TrainedAt)
            .Take(generationsToCheck)
            .AsNoTracking()
            .ToListAsync(ct);

        // Re-order oldest → newest so importanceMatrix[0] is the earliest generation.
        // This is important for correct monotone-decrease direction checking (g > g-1).
        models = models.OrderBy(m => m.TrainedAt).ToList();

        if (models.Count < minGenerations)
        {
            _logger.LogDebug(
                "FeatureImpTrend: {Symbol}/{Tf} only {N} generations available (need {Min}) — skip.",
                symbol, timeframe, models.Count, minGenerations);
            return;
        }

        // Deserialise each snapshot and extract per-feature importance score arrays.
        // Rows are indexed by generation (oldest first); columns by feature index.
        var importanceMatrix = new List<double[]>(models.Count);
        string[]? featureNames = null;

        foreach (var m in models)
        {
            try
            {
                var snap = JsonSerializer.Deserialize<ModelSnapshot>(m.ModelBytes!);
                if (snap?.FeatureImportanceScores is { Length: > 0 })
                {
                    importanceMatrix.Add(snap.FeatureImportanceScores);
                    // Capture feature names from the first valid snapshot encountered.
                    featureNames ??= snap.Features.Length > 0 ? snap.Features : null;
                }
            }
            catch { /* skip corrupt snapshots */ }
        }

        if (importanceMatrix.Count < minGenerations) return;

        // Use the minimum feature count across all generations to avoid index-out-of-range
        // when a retrain added or removed features between generations.
        int featureCount = importanceMatrix.Min(x => x.Length);
        if (featureCount == 0) return;

        var dyingFeatures = new List<(int Index, string Name, double LatestImportance)>();

        for (int j = 0; j < featureCount; j++)
        {
            // Check strict monotone decrease across generations.
            // importanceMatrix[g][j] must be strictly less than importanceMatrix[g-1][j]
            // for every consecutive pair of generations. Any plateau or increase resets
            // the flag, preventing noisy features from triggering false positives.
            bool isDecreasing = true;
            for (int g = 1; g < importanceMatrix.Count; g++)
            {
                if (importanceMatrix[g][j] >= importanceMatrix[g - 1][j])
                {
                    isDecreasing = false;
                    break;
                }
            }

            // The absolute value guard (< decayThreshold) ensures we only report features
            // that have decayed to near-zero importance, not merely slightly declining ones.
            double latestImportance = importanceMatrix[^1][j];
            if (isDecreasing && latestImportance < decayThreshold)
            {
                string name = featureNames?.Length > j ? featureNames[j] : $"Feature[{j}]";
                dyingFeatures.Add((j, name, latestImportance));
            }
        }

        if (dyingFeatures.Count == 0)
        {
            _logger.LogDebug(
                "FeatureImpTrend: {Symbol}/{Tf} — no dying features detected across {N} generations.",
                symbol, timeframe, importanceMatrix.Count);
            return;
        }

        _logger.LogWarning(
            "FeatureImpTrend: {Symbol}/{Tf} — {Count} dying feature(s) detected: [{Features}]. " +
            "Consider pruning or replacing these features.",
            symbol, timeframe, dyingFeatures.Count,
            string.Join(", ", dyingFeatures.Select(f => $"{f.Name}({f.LatestImportance:F4})")));

        // Deduplicate alert: only raise a new alert if no active MLModelDegraded alert
        // already exists for this symbol. This avoids spamming the alert channel on
        // each daily cycle when the operator has not yet acted.
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                       &&
                           a.AlertType == AlertType.MLModelDegraded    &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (!alertExists)
        {
            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                Symbol        = symbol,
                Channel       = AlertChannel.Webhook,
                Destination   = "ml-ops",
                ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reason           = "feature_importance_monotone_decay",
                    // Full list of dying features with their latest importance value
                    // so the ML-ops team can decide which to prune in the next retrain.
                    dyingFeatures    = dyingFeatures.Select(f => new
                    {
                        index            = f.Index,
                        name             = f.Name,
                        latestImportance = f.LatestImportance,
                    }),
                    generationsChecked = importanceMatrix.Count,
                    symbol,
                    timeframe          = timeframe.ToString(),
                }),
                IsActive = true,
            });

            await writeCtx.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key is absent or cannot be converted.
    /// </summary>
    /// <typeparam name="T">Target type (e.g. <see cref="int"/>, <see cref="double"/>).</typeparam>
    /// <param name="ctx">EF Core DbContext to query.</param>
    /// <param name="key">Configuration key.</param>
    /// <param name="defaultValue">Fallback value.</param>
    /// <param name="ct">Cancellation token.</param>
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
