using System.Diagnostics;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors ML feature freshness by measuring lag-1 autocorrelation for each model feature.
/// </summary>
/// <remarks>
/// The worker prefers persisted raw prediction feature vectors, which match the exact
/// deployed model schema including CPC and interaction features. It falls back to recent
/// candle-derived V1 feature vectors for legacy models without raw prediction feature logs.
/// A feature is stale when it is constant/near-constant or when consecutive observations are
/// too strongly autocorrelated. One active row is maintained per <c>(MLModelId, FeatureName)</c>.
/// </remarks>
public sealed class MLFeatureStalenessWorker : BackgroundService
{
    private const string WorkerName = nameof(MLFeatureStalenessWorker);
    private const string DistributedLockKey = "ml:feature-staleness:cycle";
    private const string RawPredictionMethod = "RawPredictionFeatureLag1";
    private const string CandleFallbackMethod = "CandleFeatureLag1Fallback";

    private const string CK_PollSecs = "MLFeatureStaleness:PollIntervalSeconds";
    private const string CK_MinSamples = "MLFeatureStaleness:MinSamples";
    private const string CK_MaxRowsPerModel = "MLFeatureStaleness:MaxRowsPerModel";
    private const string CK_MaxCandlesPerModel = "MLFeatureStaleness:MaxCandlesPerModel";
    private const string CK_MaxFeatures = "MLFeatureStaleness:MaxFeatures";
    private const string CK_MaxModelsPerCycle = "MLFeatureStaleness:MaxModelsPerCycle";
    private const string CK_AbsAutocorrThreshold = "MLFeatureStaleness:AbsAutocorrThreshold";
    private const string CK_ConstantVarianceEpsilon = "MLFeatureStaleness:ConstantVarianceEpsilon";
    private const string CK_MaxStaleFeatureFraction = "MLFeatureStaleness:MaxStaleFeatureFraction";
    private const string CK_RetentionDays = "MLFeatureStaleness:RetentionDays";
    private const string CK_LockTimeoutSecs = "MLFeatureStaleness:LockTimeoutSeconds";

    private const int DefaultPollSeconds = 7 * 24 * 60 * 60;
    private const int DefaultMinSamples = 50;
    private const int DefaultMaxRowsPerModel = 1000;
    private const int DefaultMaxCandlesPerModel = 300;
    private const int DefaultMaxFeatures = MLFeatureHelper.FeatureCountV7;
    private const int DefaultMaxModelsPerCycle = 256;
    private const int DefaultRetentionDays = 90;
    private const int DefaultLockTimeoutSeconds = 0;
    private const double DefaultAbsAutocorrThreshold = 0.95;
    private const double DefaultConstantVarianceEpsilon = 1.0e-9;
    private const double DefaultMaxStaleFeatureFraction = 0.25;

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureStalenessWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;

    public MLFeatureStalenessWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLFeatureStalenessWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureStalenessWorker started.");
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Ranks stale, highly repetitive model features from raw prediction vectors.",
            TimeSpan.FromSeconds(DefaultPollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSeconds;
            var cycleStart = Stopwatch.GetTimestamp();
            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                pollSecs = await RunCycleAsync(stoppingToken);

                long durationMs = (long)Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
                _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                _metrics?.WorkerCycleDurationMs.Record(
                    durationMs,
                    new KeyValuePair<string, object?>("worker", WorkerName));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName));
                _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                _logger.LogError(ex, "MLFeatureStalenessWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(pollSecs, 60, 7 * 24 * 60 * 60)), stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        _logger.LogInformation("MLFeatureStalenessWorker stopping.");
    }

    internal async Task<int> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
        var config = await LoadConfigAsync(readCtx, ct);

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is not null)
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(config.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _logger.LogDebug("MLFeatureStalenessWorker: cycle skipped because distributed lock is held elsewhere.");
                return config.PollSeconds;
            }
        }
        else
        {
            _logger.LogWarning(
                "MLFeatureStalenessWorker running without IDistributedLock; duplicate active rows are possible in multi-instance deployments.");
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                await RunCycleCoreAsync(readCtx, writeCtx, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }

        return config.PollSeconds;
    }

    private async Task RunCycleCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureStalenessConfig config,
        CancellationToken ct)
    {
        var models = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                     && !m.IsDeleted
                     && !m.IsMetaLearner
                     && !m.IsMamlInitializer
                     && m.ModelBytes != null)
            .OrderBy(m => m.Symbol)
            .ThenBy(m => m.Timeframe)
            .ThenByDescending(m => m.TrainedAt)
            .Take(config.MaxModelsPerCycle)
            .Select(m => new
            {
                m.Id,
                m.Symbol,
                m.Timeframe,
                m.ModelBytes
            })
            .ToListAsync(ct);

        _healthMonitor?.RecordBacklogDepth(WorkerName, models.Count);

        int written = 0, skipped = 0, failed = 0;
        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bool didWrite = await ProcessModelAsync(
                    readCtx,
                    writeCtx,
                    model.Id,
                    model.Symbol,
                    model.Timeframe,
                    model.ModelBytes!,
                    config,
                    ct);

                if (didWrite) written++;
                else skipped++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex,
                    "MLFeatureStalenessWorker: failed model {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }

        await PruneExpiredRowsAsync(writeCtx, config, ct);

        _logger.LogInformation(
            "MLFeatureStalenessWorker cycle complete: written={Written}, skipped={Skipped}, failed={Failed}, models={Total}.",
            written, skipped, failed, models.Count);
    }

    private async Task<bool> ProcessModelAsync(
        DbContext readCtx,
        DbContext writeCtx,
        long modelId,
        string symbol,
        Timeframe timeframe,
        byte[] modelBytes,
        FeatureStalenessConfig config,
        CancellationToken ct)
    {
        var snapshot = TryDeserializeSnapshot(modelBytes, modelId);
        if (snapshot is null)
            return false;

        int resolvedFeatureCount = snapshot.ResolveExpectedInputFeatures();
        if (resolvedFeatureCount < 1 || resolvedFeatureCount > MLFeatureHelper.MaxAllowedFeatureCount)
            return false;

        int featureLimit = Math.Clamp(config.MaxFeatures, 1, resolvedFeatureCount);
        var featureNames = ResolveFeatureNames(snapshot, resolvedFeatureCount);

        var rows = await LoadRawPredictionRowsAsync(
            readCtx,
            modelId,
            resolvedFeatureCount,
            featureLimit,
            config,
            ct);
        string method = RawPredictionMethod;

        if (rows.Count < config.MinSamples)
        {
            rows = await LoadCandleFallbackRowsAsync(
                readCtx,
                symbol,
                timeframe,
                resolvedFeatureCount,
                featureLimit,
                config,
                ct);
            method = CandleFallbackMethod;
        }

        if (rows.Count < config.MinSamples)
        {
            _logger.LogDebug(
                "MLFeatureStalenessWorker: model {ModelId} has usable rows {Rows}/{Min}; featureCount={FeatureCount}.",
                modelId, rows.Count, config.MinSamples, resolvedFeatureCount);
            return false;
        }

        var scores = ScoreFeatures(rows, featureLimit, config).ToList();
        ApplyStaleCap(scores, config.MaxStaleFeatureFraction);

        await using var tx = await writeCtx.Database.BeginTransactionAsync(ct);
        var existingRows = await writeCtx.Set<MLFeatureStalenessLog>()
            .Where(l => l.MLModelId == modelId && !l.IsDeleted)
            .ToListAsync(ct);

        var groupedExisting = existingRows
            .GroupBy(l => l.FeatureName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Id).ToList(), StringComparer.OrdinalIgnoreCase);

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var currentNames = featureNames.Take(featureLimit).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleRow in existingRows.Where(row => !currentNames.Contains(row.FeatureName)))
            staleRow.IsDeleted = true;

        int staleCount = 0;
        foreach (var score in scores)
        {
            string featureName = featureNames[score.FeatureIndex];
            if (score.IsStale)
                staleCount++;

            if (groupedExisting.TryGetValue(featureName, out var matches) && matches.Count > 0)
            {
                var existing = matches[0];
                existing.Symbol = symbol;
                existing.Timeframe = timeframe;
                existing.Lag1Autocorr = score.Lag1Autocorr;
                existing.IsStale = score.IsStale;
                existing.ComputedAt = nowUtc;

                foreach (var duplicate in matches.Skip(1))
                    duplicate.IsDeleted = true;
            }
            else
            {
                writeCtx.Set<MLFeatureStalenessLog>().Add(new MLFeatureStalenessLog
                {
                    MLModelId = modelId,
                    Symbol = symbol,
                    Timeframe = timeframe,
                    FeatureName = featureName,
                    Lag1Autocorr = score.Lag1Autocorr,
                    IsStale = score.IsStale,
                    ComputedAt = nowUtc
                });
            }
        }

        await writeCtx.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "MLFeatureStalenessWorker: {Symbol}/{Timeframe} model={ModelId} method={Method} rows={Rows}, stale={Stale}/{Features}, threshold={Threshold:F3}.",
            symbol, timeframe, modelId, method, rows.Count, staleCount, featureLimit, config.AbsAutocorrThreshold);

        return true;
    }

    private static async Task<List<double[]>> LoadRawPredictionRowsAsync(
        DbContext readCtx,
        long modelId,
        int resolvedFeatureCount,
        int featureLimit,
        FeatureStalenessConfig config,
        CancellationToken ct)
    {
        var jsonRows = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId == modelId
                     && !l.IsDeleted
                     && l.RawFeaturesJson != null)
            .OrderByDescending(l => l.PredictedAt)
            .Take(config.MaxRowsPerModel)
            .Select(l => l.RawFeaturesJson!)
            .ToListAsync(ct);

        var rows = new List<double[]>(jsonRows.Count);
        foreach (string json in jsonRows)
        {
            double[]? values;
            try
            {
                values = JsonSerializer.Deserialize<double[]>(json, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (values is null || values.Length < resolvedFeatureCount)
                continue;

            var row = new double[featureLimit];
            bool finite = true;
            for (int i = 0; i < featureLimit; i++)
            {
                if (!double.IsFinite(values[i]))
                {
                    finite = false;
                    break;
                }

                row[i] = values[i];
            }

            if (finite)
                rows.Add(row);
        }

        rows.Reverse();
        return rows;
    }

    private static async Task<List<double[]>> LoadCandleFallbackRowsAsync(
        DbContext readCtx,
        string symbol,
        Timeframe timeframe,
        int resolvedFeatureCount,
        int featureLimit,
        FeatureStalenessConfig config,
        CancellationToken ct)
    {
        var candles = await readCtx.Set<Candle>()
            .AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == timeframe && !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .Take(config.MaxCandlesPerModel)
            .ToListAsync(ct);

        candles.Reverse();
        if (candles.Count < MLFeatureHelper.LookbackWindow + 2)
            return [];

        var samples = MLFeatureHelper.BuildTrainingSamples(candles);
        if (samples.Count == 0 || samples[0].Features.Length < resolvedFeatureCount)
            return [];

        var rows = new List<double[]>(samples.Count);
        foreach (var sample in samples)
        {
            if (sample.Features.Length < resolvedFeatureCount)
                continue;

            var row = new double[featureLimit];
            bool finite = true;
            for (int i = 0; i < featureLimit; i++)
            {
                double value = sample.Features[i];
                if (!double.IsFinite(value))
                {
                    finite = false;
                    break;
                }

                row[i] = value;
            }

            if (finite)
                rows.Add(row);
        }

        return rows;
    }

    internal static IEnumerable<FeatureStalenessScore> ScoreFeatures(
        IReadOnlyList<double[]> rows,
        int featureCount,
        FeatureStalenessConfig config)
    {
        for (int featureIndex = 0; featureIndex < featureCount; featureIndex++)
        {
            var values = new double[rows.Count];
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                values[rowIndex] = rows[rowIndex][featureIndex];

            var autocorr = ComputeLag1Autocorr(values, config.ConstantVarianceEpsilon);
            bool stale = autocorr.IsDegenerate || Math.Abs(autocorr.Correlation) >= config.AbsAutocorrThreshold;
            yield return new FeatureStalenessScore(
                featureIndex,
                autocorr.Correlation,
                stale,
                autocorr.IsDegenerate);
        }
    }

    internal static Lag1AutocorrResult ComputeLag1Autocorr(
        IReadOnlyList<double> values,
        double constantVarianceEpsilon = DefaultConstantVarianceEpsilon)
    {
        if (values.Count < 3)
            return new Lag1AutocorrResult(0.0, IsDegenerate: true);

        double mean = values.Average();
        double variance = 0.0;
        for (int i = 0; i < values.Count; i++)
        {
            double delta = values[i] - mean;
            variance += delta * delta;
        }

        variance /= values.Count;
        if (variance <= constantVarianceEpsilon)
            return new Lag1AutocorrResult(1.0, IsDegenerate: true);

        int pairCount = values.Count - 1;
        double mean0 = 0.0, mean1 = 0.0;
        for (int i = 0; i < pairCount; i++)
        {
            mean0 += values[i];
            mean1 += values[i + 1];
        }

        mean0 /= pairCount;
        mean1 /= pairCount;

        double covariance = 0.0, variance0 = 0.0, variance1 = 0.0;
        for (int i = 0; i < pairCount; i++)
        {
            double d0 = values[i] - mean0;
            double d1 = values[i + 1] - mean1;
            covariance += d0 * d1;
            variance0 += d0 * d0;
            variance1 += d1 * d1;
        }

        double denominator = Math.Sqrt(variance0) * Math.Sqrt(variance1);
        if (denominator <= constantVarianceEpsilon)
            return new Lag1AutocorrResult(1.0, IsDegenerate: true);

        return new Lag1AutocorrResult(Math.Clamp(covariance / denominator, -1.0, 1.0), IsDegenerate: false);
    }

    internal static void ApplyStaleCap(List<FeatureStalenessScore> scores, double maxStaleFeatureFraction)
    {
        if (scores.Count == 0)
            return;

        int maxStale = (int)Math.Floor(scores.Count * Math.Clamp(maxStaleFeatureFraction, 0.0, 1.0));
        if (maxStaleFeatureFraction > 0 && maxStale == 0)
            maxStale = 1;

        var allowed = scores
            .Where(s => s.IsStale)
            .OrderByDescending(s => s.IsDegenerate)
            .ThenByDescending(s => Math.Abs(s.Lag1Autocorr))
            .Take(maxStale)
            .Select(s => s.FeatureIndex)
            .ToHashSet();

        for (int i = 0; i < scores.Count; i++)
        {
            var score = scores[i];
            if (score.IsStale && !allowed.Contains(score.FeatureIndex))
                scores[i] = score with { IsStale = false };
        }
    }

    private async Task PruneExpiredRowsAsync(
        DbContext writeCtx,
        FeatureStalenessConfig config,
        CancellationToken ct)
    {
        if (config.RetentionDays <= 0)
            return;

        var retentionCutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-config.RetentionDays);
        var expiredRows = await writeCtx.Set<MLFeatureStalenessLog>()
            .Where(l => l.ComputedAt < retentionCutoff && !l.IsDeleted)
            .ToListAsync(ct);
        foreach (var row in expiredRows)
            row.IsDeleted = true;

        if (expiredRows.Count > 0)
        {
            await writeCtx.SaveChangesAsync(ct);
            _logger.LogInformation("MLFeatureStalenessWorker: soft-pruned {Count} staleness logs older than {Days} days.", expiredRows.Count, config.RetentionDays);
        }
    }

    private static string[] ResolveFeatureNames(ModelSnapshot snapshot, int featureCount)
    {
        if (snapshot.Features.Length >= featureCount)
            return snapshot.Features.Take(featureCount).ToArray();

        return MLFeatureHelper.ResolveFeatureNames(featureCount);
    }

    private ModelSnapshot? TryDeserializeSnapshot(byte[] modelBytes, long modelId)
    {
        try
        {
            return JsonSerializer.Deserialize<ModelSnapshot>(modelBytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "MLFeatureStalenessWorker: failed to deserialize snapshot for model {ModelId}.",
                modelId);
            return null;
        }
    }

    private static async Task<FeatureStalenessConfig> LoadConfigAsync(DbContext ctx, CancellationToken ct)
    {
        var values = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("MLFeatureStaleness:"))
            .Select(c => new { c.Key, c.Value })
            .ToDictionaryAsync(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase, ct);

        return new FeatureStalenessConfig(
            PollSeconds: GetInt(values, CK_PollSecs, DefaultPollSeconds, 60, 7 * 24 * 60 * 60),
            MinSamples: GetInt(values, CK_MinSamples, DefaultMinSamples, 20, 100_000),
            MaxRowsPerModel: GetInt(values, CK_MaxRowsPerModel, DefaultMaxRowsPerModel, 50, 100_000),
            MaxCandlesPerModel: GetInt(values, CK_MaxCandlesPerModel, DefaultMaxCandlesPerModel, MLFeatureHelper.LookbackWindow + 2, 100_000),
            MaxFeatures: GetInt(values, CK_MaxFeatures, DefaultMaxFeatures, 1, MLFeatureHelper.MaxAllowedFeatureCount),
            MaxModelsPerCycle: GetInt(values, CK_MaxModelsPerCycle, DefaultMaxModelsPerCycle, 1, 10_000),
            AbsAutocorrThreshold: GetDouble(values, CK_AbsAutocorrThreshold, DefaultAbsAutocorrThreshold, 0.50, 0.9999),
            ConstantVarianceEpsilon: GetDouble(values, CK_ConstantVarianceEpsilon, DefaultConstantVarianceEpsilon, 1.0e-12, 1.0),
            MaxStaleFeatureFraction: GetDouble(values, CK_MaxStaleFeatureFraction, DefaultMaxStaleFeatureFraction, 0.0, 1.0),
            RetentionDays: GetInt(values, CK_RetentionDays, DefaultRetentionDays, 1, 3650),
            LockTimeoutSeconds: GetInt(values, CK_LockTimeoutSecs, DefaultLockTimeoutSeconds, 0, 300));
    }

    private static int GetInt(Dictionary<string, string> values, string key, int fallback, int min, int max)
        => values.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    private static double GetDouble(Dictionary<string, string> values, string key, double fallback, double min, double max)
        => values.TryGetValue(key, out var raw) && double.TryParse(raw, out var parsed) && double.IsFinite(parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    internal readonly record struct Lag1AutocorrResult(double Correlation, bool IsDegenerate);

    internal readonly record struct FeatureStalenessScore(
        int FeatureIndex,
        double Lag1Autocorr,
        bool IsStale,
        bool IsDegenerate);

    internal sealed record FeatureStalenessConfig(
        int PollSeconds,
        int MinSamples,
        int MaxRowsPerModel,
        int MaxCandlesPerModel,
        int MaxFeatures,
        int MaxModelsPerCycle,
        double AbsAutocorrThreshold,
        double ConstantVarianceEpsilon,
        double MaxStaleFeatureFraction,
        int RetentionDays,
        int LockTimeoutSeconds);
}
