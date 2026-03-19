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

    public MLFeatureImportanceTrendWorker(
        IServiceScopeFactory                     scopeFactory,
        ILogger<MLFeatureImportanceTrendWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureImportanceTrendWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
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

    private async Task CheckImportanceTrendsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    generationsToCheck = await GetConfigAsync<int>   (readCtx, CK_Generations,    4,     ct);
        int    minGenerations     = await GetConfigAsync<int>   (readCtx, CK_MinGenerations, 3,     ct);
        double decayThreshold     = await GetConfigAsync<double>(readCtx, CK_DecayThreshold, 0.005, ct);

        // Get distinct symbol/timeframe pairs from active models
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
                _logger.LogWarning(ex,
                    "Feature importance trend check failed for {Symbol}/{Tf} — skipping.",
                    pair.Symbol, pair.Timeframe);
            }
        }
    }

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
        // Load the last N model generations (including superseded) ordered oldest → newest
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

        // Re-order oldest → newest for trend computation
        models = models.OrderBy(m => m.TrainedAt).ToList();

        if (models.Count < minGenerations)
        {
            _logger.LogDebug(
                "FeatureImpTrend: {Symbol}/{Tf} only {N} generations available (need {Min}) — skip.",
                symbol, timeframe, models.Count, minGenerations);
            return;
        }

        // Deserialise each snapshot and extract per-feature importances
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
                    featureNames ??= snap.Features.Length > 0 ? snap.Features : null;
                }
            }
            catch { /* skip corrupt snapshots */ }
        }

        if (importanceMatrix.Count < minGenerations) return;

        int featureCount = importanceMatrix.Min(x => x.Length);
        if (featureCount == 0) return;

        var dyingFeatures = new List<(int Index, string Name, double LatestImportance)>();

        for (int j = 0; j < featureCount; j++)
        {
            // Check strict monotone decrease across generations
            bool isDecreasing = true;
            for (int g = 1; g < importanceMatrix.Count; g++)
            {
                if (importanceMatrix[g][j] >= importanceMatrix[g - 1][j])
                {
                    isDecreasing = false;
                    break;
                }
            }

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

        // Deduplicate alert
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
