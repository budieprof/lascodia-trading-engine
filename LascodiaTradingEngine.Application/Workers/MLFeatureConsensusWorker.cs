using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes cross-architecture feature importance consensus for each active (symbol, timeframe) pair.
///
/// <para>
/// When multiple ML architectures (bagged logistic, TCN, hybrid, etc.) are trained on the same
/// symbol/timeframe, their individual feature importance vectors may diverge — some features
/// are universally important while others are architecture-specific artifacts. This worker
/// aggregates importance across all active models to produce a consensus snapshot.
/// </para>
///
/// <para>
/// Every poll cycle the worker:
/// <list type="number">
///   <item>Loads all distinct (symbol, timeframe) pairs from active <see cref="MLModel"/> records
///         that have serialised <c>ModelBytes</c>.</item>
///   <item>For each pair, deserialises every model's <see cref="ModelSnapshot"/> to extract
///         <c>FeatureImportance</c> vectors.</item>
///   <item>If enough models contribute (≥ <c>MLFeatureConsensus:MinModelsForConsensus</c>),
///         computes per-feature mean importance, standard deviation, and agreement score.</item>
///   <item>Computes pairwise Kendall's tau rank correlation across all contributing models
///         and averages to produce a single rank-agreement metric.</item>
///   <item>Persists a new <see cref="MLFeatureConsensusSnapshot"/> with the consensus JSON,
///         contributing model count, and mean Kendall's tau.</item>
/// </list>
/// </para>
///
/// <para>Downstream consumers:</para>
/// <list type="bullet">
///   <item><c>TrainerSelector</c>: boost architectures whose top features align with consensus.</item>
///   <item><c>MLCovariateShiftWorker</c>: skip retraining if drift is in low-consensus features.</item>
///   <item><c>MLTrainingWorker</c>: optionally initialise feature masks from consensus.</item>
/// </list>
/// </summary>
public sealed class MLFeatureConsensusWorker : BackgroundService
{
    // ── Config keys ────────────────────────────────────────────────────────────
    private const string CK_PollSecs           = "MLFeatureConsensus:PollIntervalSeconds";
    private const string CK_MinModels          = "MLFeatureConsensus:MinModelsForConsensus";

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceScopeFactory                _scopeFactory;
    private readonly ILogger<MLFeatureConsensusWorker>    _logger;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per poll cycle, providing fresh
    /// <see cref="IReadApplicationDbContext"/> / <see cref="IWriteApplicationDbContext"/>
    /// instances and preventing long-lived DbContext connection leaks.
    /// </param>
    /// <param name="logger">Structured logger for consensus computation events and diagnostics.</param>
    public MLFeatureConsensusWorker(
        IServiceScopeFactory              scopeFactory,
        ILogger<MLFeatureConsensusWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Main background loop. Runs indefinitely at <c>MLFeatureConsensus:PollIntervalSeconds</c>
    /// intervals (default 3600 s / 1 hour) until the host requests shutdown.
    ///
    /// Each cycle creates a fresh DI scope, loads config, and processes every active
    /// (symbol, timeframe) pair one at a time for efficient memory usage.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureConsensusWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600; // default hourly

            try
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(stoppingToken);
                try
                {
                    await RunCycleAsync(stoppingToken);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLFeatureConsensusWorker loop error");
            }

            // Re-read poll interval inside the delay so config changes take effect next cycle
            try
            {
                await using var configScope = _scopeFactory.CreateAsyncScope();
                var configReadDb = configScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var configCtx    = configReadDb.GetDbContext();
                pollSecs = await GetConfigAsync<int>(configCtx, CK_PollSecs, 3600, stoppingToken);
            }
            catch
            {
                // Fall back to default if config read fails
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLFeatureConsensusWorker stopping.");
    }

    // ── Core cycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a single consensus computation cycle: loads all active (symbol, timeframe)
    /// pairs and computes feature consensus for each.
    /// </summary>
    private async Task RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx  = readDb.GetDbContext();
        var writeCtx = writeDb.GetDbContext();

        int minModels = await GetConfigAsync<int>(readCtx, CK_MinModels, 3, ct);

        // Load distinct (symbol, timeframe) pairs from active models with serialised weights
        var pairs = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .AsNoTracking()
            .ToListAsync(ct);

        _logger.LogDebug(
            "Feature consensus cycle: {PairCount} distinct (symbol, timeframe) pair(s), minModels={Min}.",
            pairs.Count, minModels);

        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessPairAsync(readCtx, writeCtx, pair.Symbol, pair.Timeframe, minModels, ct);
        }
    }

    // ── Per-pair consensus computation ─────────────────────────────────────────

    /// <summary>
    /// Computes feature importance consensus for a single (symbol, timeframe) pair.
    /// Loads all active models, extracts their feature importance vectors, computes
    /// per-feature statistics and pairwise Kendall's tau, then persists the result.
    /// </summary>
    /// <param name="readCtx">EF read context for loading models.</param>
    /// <param name="writeCtx">EF write context for persisting the snapshot.</param>
    /// <param name="symbol">Currency pair symbol.</param>
    /// <param name="timeframe">Chart timeframe.</param>
    /// <param name="minModels">Minimum models required to compute consensus.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ProcessPairAsync(
        DbContext         readCtx,
        DbContext         writeCtx,
        string            symbol,
        Timeframe         timeframe,
        int               minModels,
        CancellationToken ct)
    {
        // Step (a): Load all active models with their ModelBytes
        var models = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null
                     && m.Symbol == symbol && m.Timeframe == timeframe)
            .AsNoTracking()
            .ToListAsync(ct);

        // Step (b): Check minimum model count
        if (models.Count < minModels)
        {
            _logger.LogDebug(
                "Pair {Symbol}/{Tf}: only {Count} active model(s) — need {Min}, skipping consensus.",
                symbol, timeframe, models.Count, minModels);
            return;
        }

        // Steps (c)-(d): Deserialise each model's snapshot and extract FeatureImportance
        var importanceVectors = new List<float[]>();
        foreach (var model in models)
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!, JsonOptions);
                if (snapshot?.FeatureImportance is { Length: > 0 })
                {
                    importanceVectors.Add(snapshot.FeatureImportance);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex, "Pair {Symbol}/{Tf}: failed to deserialise model {Id} — skipping.",
                    symbol, timeframe, model.Id);
            }
        }

        // Step (e): Re-check minimum after filtering
        if (importanceVectors.Count < minModels)
        {
            _logger.LogDebug(
                "Pair {Symbol}/{Tf}: only {Count} model(s) with valid FeatureImportance — need {Min}, skipping.",
                symbol, timeframe, importanceVectors.Count, minModels);
            return;
        }

        // Determine the maximum feature count across all vectors
        int featureCount = importanceVectors.Max(v => v.Length);

        // Step (f): Compute per-feature consensus statistics
        var consensusEntries = new List<FeatureConsensusEntry>(featureCount);
        for (int i = 0; i < featureCount; i++)
        {
            // Collect abs(importance[i]) from each model (0 if model's vector is shorter)
            var absValues = new double[importanceVectors.Count];
            for (int m = 0; m < importanceVectors.Count; m++)
            {
                absValues[m] = i < importanceVectors[m].Length
                    ? Math.Abs(importanceVectors[m][i])
                    : 0.0;
            }

            double meanImportance = absValues.Average();
            double stdImportance  = ComputeStdDev(absValues);

            // AgreementScore = 1 - (Std / Mean), clamped to [0, 1]
            double agreementScore = meanImportance > 1e-12
                ? Math.Clamp(1.0 - (stdImportance / meanImportance), 0.0, 1.0)
                : 0.0;

            consensusEntries.Add(new FeatureConsensusEntry
            {
                Feature         = $"Feature_{i}",
                MeanImportance  = Math.Round(meanImportance, 6),
                StdImportance   = Math.Round(stdImportance, 6),
                AgreementScore  = Math.Round(agreementScore, 4),
            });
        }

        // Step (g): Compute pairwise Kendall's tau rank correlation and average
        double meanKendallTau = ComputeMeanKendallTau(importanceVectors, featureCount);

        // Step (h): Serialise consensus JSON
        string consensusJson = JsonSerializer.Serialize(consensusEntries, JsonOptions);

        // Step (i): Persist snapshot
        var snapshot2 = new MLFeatureConsensusSnapshot
        {
            Symbol                 = symbol,
            Timeframe              = timeframe,
            FeatureConsensusJson   = consensusJson,
            ContributingModelCount = importanceVectors.Count,
            MeanKendallTau         = Math.Round(meanKendallTau, 6),
            DetectedAt             = DateTime.UtcNow,
        };

        writeCtx.Set<MLFeatureConsensusSnapshot>().Add(snapshot2);
        await writeCtx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Feature consensus computed for {Symbol}/{Tf}: {ModelCount} models, " +
            "meanKendallTau={Tau:F4}, {FeatureCount} features.",
            symbol, timeframe, importanceVectors.Count, meanKendallTau, featureCount);
    }

    // ── Statistical helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Computes the population standard deviation of the given values.
    /// </summary>
    private static double ComputeStdDev(double[] values)
    {
        if (values.Length <= 1) return 0.0;

        double mean     = values.Average();
        double variance = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double diff = values[i] - mean;
            variance += diff * diff;
        }
        variance /= values.Length;
        return Math.Sqrt(variance);
    }

    /// <summary>
    /// Computes the average pairwise Kendall's tau rank correlation across all pairs
    /// of feature importance vectors. Each vector is padded with zeros to
    /// <paramref name="featureCount"/> length before ranking.
    /// </summary>
    /// <param name="vectors">Feature importance vectors from each contributing model.</param>
    /// <param name="featureCount">Maximum feature count (vectors shorter than this are zero-padded).</param>
    /// <returns>Mean Kendall's tau across all (n choose 2) pairs, in range [-1, 1].</returns>
    private static double ComputeMeanKendallTau(List<float[]> vectors, int featureCount)
    {
        int n = vectors.Count;
        if (n < 2 || featureCount < 2) return 0.0;

        // Build padded abs-importance arrays for ranking
        var padded = new double[n][];
        for (int m = 0; m < n; m++)
        {
            padded[m] = new double[featureCount];
            for (int j = 0; j < featureCount; j++)
            {
                padded[m][j] = j < vectors[m].Length
                    ? Math.Abs(vectors[m][j])
                    : 0.0;
            }
        }

        double tauSum   = 0;
        int    pairCount = 0;

        for (int a = 0; a < n; a++)
        {
            for (int b = a + 1; b < n; b++)
            {
                tauSum += KendallTau(padded[a], padded[b]);
                pairCount++;
            }
        }

        return pairCount > 0 ? tauSum / pairCount : 0.0;
    }

    /// <summary>
    /// Computes Kendall's tau rank correlation coefficient between two vectors.
    ///
    /// For two vectors of length n, counts concordant and discordant pairs.
    /// tau = (concordant - discordant) / (n * (n - 1) / 2).
    /// </summary>
    /// <param name="x">First importance vector.</param>
    /// <param name="y">Second importance vector.</param>
    /// <returns>Kendall's tau in range [-1, 1].</returns>
    private static double KendallTau(double[] x, double[] y)
    {
        int n = x.Length;
        if (n < 2) return 0.0;

        long concordant = 0;
        long discordant = 0;

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double xDiff = x[i] - x[j];
                double yDiff = y[i] - y[j];
                double product = xDiff * yDiff;

                if (product > 0)
                    concordant++;
                else if (product < 0)
                    discordant++;
                // Ties (product == 0) are ignored
            }
        }

        long totalPairs = (long)n * (n - 1) / 2;
        return totalPairs > 0
            ? (double)(concordant - discordant) / totalPairs
            : 0.0;
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/> or returns <paramref name="defaultValue"/>
    /// when the key is absent or its stored string cannot be parsed into <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        DbContext         ctx,
        string            key,
        T                 defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    // ── DTO for JSON serialisation ────────────────────────────────────────────

    /// <summary>
    /// Represents a single feature's consensus statistics in the serialised JSON array.
    /// </summary>
    private sealed class FeatureConsensusEntry
    {
        public string Feature        { get; set; } = string.Empty;
        public double MeanImportance { get; set; }
        public double StdImportance  { get; set; }
        public double AgreementScore { get; set; }
    }
}
