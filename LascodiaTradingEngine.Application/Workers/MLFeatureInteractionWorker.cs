using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
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
/// Detects replayable pairwise feature-product candidates from resolved live prediction logs.
/// </summary>
/// <remarks>
/// The worker prefers persisted raw model feature vectors and falls back to stored
/// SHAP/contribution vectors for legacy prediction logs. For each feature pair it compares a
/// reduced model (<c>ActualDirection ~ a + b</c>) with a full model
/// (<c>ActualDirection ~ a + b + a*b</c>) and ranks the incremental product term by partial
/// F-ratio. The selected pairs are persisted to <see cref="MLFeatureInteractionAudit"/> and
/// can be replayed by training/scoring as explicit product features.
/// </remarks>
public sealed class MLFeatureInteractionWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLFeatureInteractionWorker);
    private const string DistributedLockKey = "ml:feature-interaction:cycle";
    private const string RawFeatureMethod = "RawFeaturePartialF";
    private const string ShapFallbackMethod = "ShapContributionPartialF";

    private const string CK_Enabled = "MLFeatureInteraction:Enabled";
    private const string CK_InitialDelaySecs = "MLFeatureInteraction:InitialDelaySeconds";
    private const string CK_PollSecs = "MLFeatureInteraction:PollIntervalSeconds";
    private const string CK_TopK = "MLFeatureInteraction:TopK";
    private const string CK_IncludedTopN = "MLFeatureInteraction:IncludedTopN";
    private const string CK_MinSamples = "MLFeatureInteraction:MinSamples";
    private const string CK_MaxLogsPerModel = "MLFeatureInteraction:MaxLogsPerModel";
    private const string CK_MaxFeatures = "MLFeatureInteraction:MaxFeatures";
    private const string CK_MaxModelsPerCycle = "MLFeatureInteraction:MaxModelsPerCycle";
    private const string CK_MinEffectSize = "MLFeatureInteraction:MinEffectSize";
    private const string CK_MaxQValue = "MLFeatureInteraction:MaxQValue";
    private const string CK_LockTimeoutSecs = "MLFeatureInteraction:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSecs = "MLFeatureInteraction:DbCommandTimeoutSeconds";

    private const int DefaultPollSeconds = 7 * 24 * 60 * 60;
    private const int DefaultInitialDelaySeconds = 0;
    private const int DefaultTopK = 5;
    private const int DefaultIncludedTopN = 3;
    private const int DefaultMinSamples = 100;
    private const int DefaultMaxLogsPerModel = 1000;
    private const int DefaultMaxFeatures = MLFeatureHelper.FeatureCountV7;
    private const int DefaultMaxModelsPerCycle = 256;
    private const int DefaultLockTimeoutSeconds = 0;
    private const int DefaultDbCommandTimeoutSeconds = 30;
    private const double DefaultMinEffectSize = 0.001;
    private const double DefaultMaxQValue = 0.20;

    private static readonly string[] ConfigKeys =
    [
        CK_Enabled,
        CK_InitialDelaySecs,
        CK_PollSecs,
        CK_TopK,
        CK_IncludedTopN,
        CK_MinSamples,
        CK_MaxLogsPerModel,
        CK_MaxFeatures,
        CK_MaxModelsPerCycle,
        CK_MinEffectSize,
        CK_MaxQValue,
        CK_LockTimeoutSecs,
        CK_DbCommandTimeoutSecs
    ];

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureInteractionWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly MLFeatureInteractionOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    public MLFeatureInteractionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLFeatureInteractionWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        MLFeatureInteractionOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new MLFeatureInteractionOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Ranks schema-aware feature-product candidates from resolved ML prediction logs.",
            TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds)));

        DateTime lastCycleStartUtc = DateTime.MinValue;
        DateTime lastSuccessUtc = DateTime.MinValue;
        TimeSpan currentPollInterval = TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds));

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName)
                               + TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(_options.InitialDelaySeconds));
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                if (lastSuccessUtc != DateTime.MinValue)
                    _metrics?.MLFeatureInteractionTimeSinceLastSuccessSec.Record((nowUtc - lastSuccessUtc).TotalSeconds);

                if (nowUtc - lastCycleStartUtc >= currentPollInterval)
                {
                    lastCycleStartUtc = nowUtc;
                    var cycleStart = Stopwatch.GetTimestamp();
                    try
                    {
                        _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                        var result = await RunCycleAsync(stoppingToken);
                        currentPollInterval = result.Config.PollInterval;

                        long durationMs = (long)Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
                        _healthMonitor?.RecordBacklogDepth(WorkerName, result.ModelsDiscovered);
                        _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                        _metrics?.WorkerCycleDurationMs.Record(
                            durationMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.MLFeatureInteractionCycleDurationMs.Record(durationMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: models={Models}, processed={Processed}, skipped={Skipped}, failed={Failed}, auditsWritten={AuditsWritten}, staleAuditsDeleted={StaleAuditsDeleted}.",
                                WorkerName,
                                result.ModelsDiscovered,
                                result.ModelsProcessed,
                                result.ModelsSkipped,
                                result.ModelsFailed,
                                result.AuditsWritten,
                                result.StaleAuditsDeleted);
                        }

                        var previousFailures = ConsecutiveCycleFailures;
                        if (previousFailures > 0)
                        {
                            _healthMonitor?.RecordRecovery(WorkerName, previousFailures);
                            _logger.LogInformation(
                                "{Worker}: recovered after {Failures} consecutive failure(s).",
                                WorkerName,
                                previousFailures);
                        }

                        ConsecutiveCycleFailures = 0;
                        lastSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _consecutiveCycleFailuresField);
                        _metrics?.WorkerErrors.Add(
                            1,
                            new KeyValuePair<string, object?>("worker", WorkerName),
                            new KeyValuePair<string, object?>("reason", "ml_feature_interaction_cycle"));
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                        _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                    }
                }

                var delay = ConsecutiveCycleFailures > 0
                    ? CalculateBackoffDelay(ConsecutiveCycleFailures)
                    : WakeInterval;
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<FeatureInteractionCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
        var config = await LoadConfigAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
        {
            RecordCycleSkipped("disabled");
            return FeatureInteractionCycleResult.Skipped(config, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is not null)
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(config.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLFeatureInteractionLockAttempts.Add(1, Tag("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                _logger.LogDebug("{Worker}: cycle skipped because distributed lock is held elsewhere.", WorkerName);
                return FeatureInteractionCycleResult.Skipped(config, "lock_busy");
            }

            _metrics?.MLFeatureInteractionLockAttempts.Add(1, Tag("outcome", "acquired"));
        }
        else
        {
            _metrics?.MLFeatureInteractionLockAttempts.Add(1, Tag("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate audit rows are possible in multi-instance deployments.",
                    WorkerName);
            }
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunCycleCoreAsync(readCtx, writeCtx, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    private async Task<FeatureInteractionCycleResult> RunCycleCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureInteractionConfig config,
        CancellationToken ct)
    {
        var models = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && !m.IsSuppressed
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer
                        && m.ModelBytes != null
                        && (m.Status == MLModelStatus.Active || m.IsFallbackChampion))
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

        int processed = 0, skipped = 0, failed = 0, auditsWritten = 0, staleAuditsDeleted = 0;
        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var modelResult = await ProcessModelAsync(
                    readCtx,
                    writeCtx,
                    model.Id,
                    model.Symbol,
                    model.Timeframe,
                    model.ModelBytes!,
                    config,
                    ct);

                if (modelResult.Processed)
                {
                    processed++;
                }
                else
                {
                    skipped++;
                    _metrics?.MLFeatureInteractionModelsSkipped.Add(
                        1,
                        Tag("reason", modelResult.State),
                        Tag("symbol", model.Symbol),
                        Tag("timeframe", model.Timeframe.ToString()));
                }

                auditsWritten += modelResult.AuditsWritten;
                staleAuditsDeleted += modelResult.StaleAuditsDeleted;
                _metrics?.MLFeatureInteractionPredictionRows.Record(
                    modelResult.UsableRows,
                    Tag("method", modelResult.Method ?? "none"),
                    Tag("state", modelResult.State));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _metrics?.MLFeatureInteractionModelsSkipped.Add(
                    1,
                    Tag("reason", "model_error"),
                    Tag("symbol", model.Symbol),
                    Tag("timeframe", model.Timeframe.ToString()));
                _logger.LogWarning(ex,
                    "{Worker}: failed model {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    WorkerName, model.Id, model.Symbol, model.Timeframe);
            }
        }

        _metrics?.MLFeatureInteractionModelsEvaluated.Add(processed);
        if (skipped > 0)
            _metrics?.MLFeatureInteractionModelsSkipped.Add(skipped, Tag("reason", "cycle_total"));
        if (auditsWritten > 0)
            _metrics?.MLFeatureInteractionAuditsWritten.Add(auditsWritten);
        if (staleAuditsDeleted > 0)
            _metrics?.MLFeatureInteractionStaleAuditsDeleted.Add(staleAuditsDeleted);

        return new FeatureInteractionCycleResult(
            config,
            ModelsDiscovered: models.Count,
            ModelsProcessed: processed,
            ModelsSkipped: skipped,
            ModelsFailed: failed,
            AuditsWritten: auditsWritten,
            StaleAuditsDeleted: staleAuditsDeleted,
            SkippedReason: null);
    }

    private async Task<FeatureInteractionModelResult> ProcessModelAsync(
        DbContext readCtx,
        DbContext writeCtx,
        long modelId,
        string symbol,
        Timeframe timeframe,
        byte[] modelBytes,
        FeatureInteractionConfig config,
        CancellationToken ct)
    {
        var snapshot = TryDeserializeSnapshot(modelBytes, modelId);
        if (snapshot is null)
            return FeatureInteractionModelResult.Skipped("invalid_snapshot");

        int resolvedFeatures = snapshot.ResolveExpectedInputFeatures();
        int baseFeatureCount = snapshot.InteractionBaseFeatureCount > 0
            ? snapshot.InteractionBaseFeatureCount
            : resolvedFeatures;
        baseFeatureCount = Math.Min(baseFeatureCount, resolvedFeatures);
        if (baseFeatureCount < 2 || baseFeatureCount > MLFeatureHelper.MaxAllowedFeatureCount)
            return FeatureInteractionModelResult.Skipped("invalid_feature_count");

        int schemaVersion = snapshot.ResolveFeatureSchemaVersion();
        int featureLimit = Math.Clamp(config.MaxFeatures, 2, baseFeatureCount);
        var featureNames = ResolveFeatureNames(snapshot, baseFeatureCount);

        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId == modelId
                     && !l.IsDeleted
                     && l.ActualDirection.HasValue
                     && (l.RawFeaturesJson != null || l.ShapValuesJson != null))
            .OrderByDescending(l => l.PredictedAt)
            .Take(config.MaxLogsPerModel)
            .Select(l => new PredictionFeatureLog(
                l.RawFeaturesJson,
                l.ShapValuesJson,
                l.ActualDirection!.Value))
            .ToListAsync(ct);

        if (logs.Count < config.MinSamples)
        {
            _logger.LogDebug(
                "{Worker}: model {ModelId} has {Rows}/{Min} feature rows before parsing.",
                WorkerName, modelId, logs.Count, config.MinSamples);
            return FeatureInteractionModelResult.Skipped("insufficient_logs", logs.Count);
        }

        int rawRows = logs.Count(l => l.RawFeaturesJson is not null);
        int shapRows = logs.Count - rawRows;
        var rawParse = BuildInteractionRows(
            logs,
            useRawFeatures: true,
            baseFeatureCount,
            featureLimit);
        var selectedParse = rawParse.Rows.Count >= config.MinSamples
            ? rawParse
            : BuildInteractionRows(
                logs,
                useRawFeatures: false,
                baseFeatureCount,
                featureLimit);

        string method = selectedParse.UsedRawFeatures ? RawFeatureMethod : ShapFallbackMethod;
        var rows = selectedParse.Rows;

        if (rows.Count < config.MinSamples)
        {
            _logger.LogDebug(
                "{Worker}: model {ModelId} usable rows {Rows}/{Min}; method={Method}, rawRows={RawRows}, shapRows={ShapRows}, malformed={Malformed}, wrongShape={WrongShape}, nonFinite={NonFinite}.",
                WorkerName, modelId, rows.Count, config.MinSamples, method, rawRows, shapRows,
                selectedParse.Malformed, selectedParse.WrongShape, selectedParse.NonFinite);
            return FeatureInteractionModelResult.Skipped("insufficient_usable_rows", rows.Count, method);
        }

        var candidates = ScorePairs(rows, featureLimit);
        ApplyBenjaminiHochberg(candidates);

        var topK = candidates
            .Where(c => c.Score > 0 && c.EffectSize >= config.MinEffectSize && c.QValue <= config.MaxQValue)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.EffectSize)
            .Take(config.TopK)
            .ToList();

        await using var tx = await writeCtx.Database.BeginTransactionAsync(ct);
        var old = await writeCtx.Set<MLFeatureInteractionAudit>()
            .Where(a => a.Symbol == symbol
                        && a.Timeframe == timeframe
                        && a.BaseFeatureCount == baseFeatureCount
                        && !a.IsDeleted)
            .ToListAsync(ct);
        foreach (var audit in old)
            audit.IsDeleted = true;

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        for (int rank = 0; rank < topK.Count; rank++)
        {
            var candidate = topK[rank];
            writeCtx.Set<MLFeatureInteractionAudit>().Add(new MLFeatureInteractionAudit
            {
                MLModelId = modelId,
                Symbol = symbol,
                Timeframe = timeframe,
                FeatureIndexA = candidate.A,
                FeatureNameA = featureNames[candidate.A],
                FeatureIndexB = candidate.B,
                FeatureNameB = featureNames[candidate.B],
                FeatureSchemaVersion = schemaVersion,
                BaseFeatureCount = baseFeatureCount,
                SampleCount = rows.Count,
                Method = method,
                InteractionScore = candidate.Score,
                EffectSize = candidate.EffectSize,
                PValue = candidate.PValue,
                QValue = candidate.QValue,
                Rank = rank + 1,
                IsIncludedAsFeature = rank < config.IncludedTopN,
                ComputedAt = nowUtc
            });
        }

        await writeCtx.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "{Worker}: {Symbol}/{Timeframe} model={ModelId} method={Method} rows={Rows}, pairsTested={Pairs}, written={Written}, staleDeleted={StaleDeleted}, top={TopA}x{TopB}, score={Score:F3}, q={Q:F4}.",
            WorkerName, symbol, timeframe, modelId, method, rows.Count, candidates.Count, topK.Count, old.Count,
            topK.Count > 0 ? featureNames[topK[0].A] : "?",
            topK.Count > 0 ? featureNames[topK[0].B] : "?",
            topK.Count > 0 ? topK[0].Score : 0.0,
            topK.Count > 0 ? topK[0].QValue : 1.0);

        return new FeatureInteractionModelResult(
            Processed: true,
            State: topK.Count > 0 ? "audits_written" : "no_significant_pairs",
            UsableRows: rows.Count,
            AuditsWritten: topK.Count,
            StaleAuditsDeleted: old.Count,
            Method: method);
    }

    internal static List<InteractionCandidate> ScorePairs(IReadOnlyList<InteractionRow> rows, int featureCount)
    {
        var candidates = new List<InteractionCandidate>(featureCount * (featureCount - 1) / 2);
        for (int a = 0; a < featureCount; a++)
        for (int b = a + 1; b < featureCount; b++)
        {
            var score = ComputePartialF(rows, a, b);
            if (score.Score > 0 && double.IsFinite(score.Score))
                candidates.Add(score);
        }

        return candidates;
    }

    internal static InteractionCandidate ComputePartialF(IReadOnlyList<InteractionRow> rows, int a, int b)
    {
        int n = rows.Count;
        if (n < 8)
            return new InteractionCandidate(a, b, 0, 0, 1, 1);

        double sseReduced = ComputeSse(rows, a, b, includeInteraction: false);
        double sseFull = ComputeSse(rows, a, b, includeInteraction: true);
        if (!double.IsFinite(sseReduced) || !double.IsFinite(sseFull) || sseFull <= 1e-12 || sseReduced <= sseFull)
            return new InteractionCandidate(a, b, 0, 0, 1, 1);

        double yMean = rows.Average(r => r.Label);
        double sst = rows.Sum(r => (r.Label - yMean) * (r.Label - yMean));
        if (sst <= 1e-12)
            return new InteractionCandidate(a, b, 0, 0, 1, 1);

        double f = (sseReduced - sseFull) / (sseFull / (n - 4));
        double r2Reduced = 1.0 - sseReduced / sst;
        double r2Full = 1.0 - sseFull / sst;
        double effectSize = Math.Max(0.0, r2Full - r2Reduced);
        double p = FSurvival(f, 1, n - 4);

        return new InteractionCandidate(a, b, f, effectSize, p, 1);
    }

    private static InteractionRowParseResult BuildInteractionRows(
        IReadOnlyList<PredictionFeatureLog> logs,
        bool useRawFeatures,
        int baseFeatureCount,
        int featureLimit)
    {
        int malformed = 0, wrongShape = 0, nonFinite = 0;
        var rows = new List<InteractionRow>(logs.Count);
        foreach (var log in logs)
        {
            var json = useRawFeatures ? log.RawFeaturesJson : log.ShapValuesJson;
            if (json is null)
                continue;

            double[]? values;
            try
            {
                values = JsonSerializer.Deserialize<double[]>(json, JsonOptions);
            }
            catch (JsonException)
            {
                malformed++;
                continue;
            }

            if (values is null || values.Length < baseFeatureCount)
            {
                wrongShape++;
                continue;
            }

            var row = new double[featureLimit];
            bool finite = true;
            for (int i = 0; i < featureLimit; i++)
            {
                double value = values[i];
                if (!double.IsFinite(value))
                {
                    finite = false;
                    break;
                }
                row[i] = value;
            }

            if (!finite)
            {
                nonFinite++;
                continue;
            }

            rows.Add(new InteractionRow(row, log.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0));
        }

        return new InteractionRowParseResult(rows, useRawFeatures, malformed, wrongShape, nonFinite);
    }

    private static double ComputeSse(IReadOnlyList<InteractionRow> rows, int a, int b, bool includeInteraction)
    {
        int p = includeInteraction ? 4 : 3;
        var xtx = new double[p, p];
        var xty = new double[p];

        Span<double> x = stackalloc double[4];
        for (int r = 0; r < rows.Count; r++)
        {
            double xa = rows[r].Values[a];
            double xb = rows[r].Values[b];
            x[0] = 1.0;
            x[1] = xa;
            x[2] = xb;
            if (includeInteraction)
                x[3] = xa * xb;

            for (int i = 0; i < p; i++)
            {
                xty[i] += x[i] * rows[r].Label;
                for (int j = 0; j < p; j++)
                    xtx[i, j] += x[i] * x[j];
            }
        }

        var beta = SolveLinearSystem(xtx, xty);
        if (beta is null)
            return double.PositiveInfinity;

        double sse = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            double xa = rows[r].Values[a];
            double xb = rows[r].Values[b];
            x[0] = 1.0;
            x[1] = xa;
            x[2] = xb;
            if (includeInteraction)
                x[3] = xa * xb;

            double yHat = 0;
            for (int i = 0; i < p; i++)
                yHat += beta[i] * x[i];
            double e = rows[r].Label - yHat;
            sse += e * e;
        }

        return sse;
    }

    private static double[]? SolveLinearSystem(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                m[i, j] = a[i, j];
            m[i, n] = b[i];
        }

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col]))
                    pivot = row;

            if (Math.Abs(m[pivot, col]) < 1e-10)
                return null;

            if (pivot != col)
                for (int j = col; j <= n; j++)
                    (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);

            double div = m[col, col];
            for (int j = col; j <= n; j++)
                m[col, j] /= div;

            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                double factor = m[row, col];
                for (int j = col; j <= n; j++)
                    m[row, j] -= factor * m[col, j];
            }
        }

        var result = new double[n];
        for (int i = 0; i < n; i++)
            result[i] = m[i, n];
        return result;
    }

    private static void ApplyBenjaminiHochberg(List<InteractionCandidate> candidates)
    {
        if (candidates.Count == 0)
            return;

        var ordered = candidates
            .Select((c, i) => (Candidate: c, OriginalIndex: i))
            .OrderBy(x => x.Candidate.PValue)
            .ToArray();

        double runningMin = 1.0;
        int m = ordered.Length;
        for (int i = m - 1; i >= 0; i--)
        {
            double q = Math.Min(runningMin, ordered[i].Candidate.PValue * m / (i + 1));
            runningMin = Math.Clamp(q, 0.0, 1.0);
            var c = ordered[i].Candidate;
            candidates[ordered[i].OriginalIndex] = c with { QValue = runningMin };
        }
    }

    private static double FSurvival(double f, double df1, double df2)
    {
        if (!double.IsFinite(f) || f <= 0 || df1 <= 0 || df2 <= 0)
            return 1.0;

        double x = df2 / (df2 + df1 * f);
        return RegularizedIncompleteBeta(x, df2 / 2.0, df1 / 2.0);
    }

    private static double RegularizedIncompleteBeta(double x, double a, double b)
    {
        if (x <= 0) return 0.0;
        if (x >= 1) return 1.0;

        double front = Math.Exp(
            LogGamma(a + b) - LogGamma(a) - LogGamma(b)
            + a * Math.Log(x)
            + b * Math.Log(1.0 - x));

        if (x < (a + 1.0) / (a + b + 2.0))
            return Math.Clamp(front * BetaContinuedFraction(a, b, x) / a, 0.0, 1.0);

        double complement = front * BetaContinuedFraction(b, a, 1.0 - x) / b;
        return Math.Clamp(1.0 - complement, 0.0, 1.0);
    }

    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const int maxIterations = 200;
        const double epsilon = 3.0e-14;
        const double fpMin = 1.0e-300;

        double qab = a + b;
        double qap = a + 1.0;
        double qam = a - 1.0;
        double c = 1.0;
        double d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < fpMin)
            d = fpMin;
        d = 1.0 / d;
        double h = d;

        for (int m = 1; m <= maxIterations; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < fpMin)
                d = fpMin;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < fpMin)
                c = fpMin;
            d = 1.0 / d;
            h *= d * c;

            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < fpMin)
                d = fpMin;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < fpMin)
                c = fpMin;
            d = 1.0 / d;
            double delta = d * c;
            h *= delta;

            if (Math.Abs(delta - 1.0) <= epsilon)
                break;
        }

        return h;
    }

    private static double LogGamma(double x)
    {
        double[] coefficients =
        [
            676.5203681218851,
            -1259.1392167224028,
            771.32342877765313,
            -176.61502916214059,
            12.507343278686905,
            -0.13857109526572012,
            9.9843695780195716e-6,
            1.5056327351493116e-7
        ];

        if (x < 0.5)
            return Math.Log(Math.PI) - Math.Log(Math.Sin(Math.PI * x)) - LogGamma(1.0 - x);

        x -= 1.0;
        double a = 0.99999999999980993;
        for (int i = 0; i < coefficients.Length; i++)
            a += coefficients[i] / (x + i + 1.0);

        double t = x + coefficients.Length - 0.5;
        return 0.9189385332046727 + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
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
                "MLFeatureInteractionWorker: failed to deserialize snapshot for model {ModelId}.",
                modelId);
            return null;
        }
    }

    internal static async Task<FeatureInteractionConfig> LoadConfigAsync(
        DbContext ctx,
        MLFeatureInteractionOptions options,
        CancellationToken ct)
    {
        var rows = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => ConfigKeys.Contains(c.Key) && !c.IsDeleted)
            .Select(c => new { c.Id, c.Key, c.Value, c.LastUpdatedAt })
            .ToListAsync(ct);

        var values = rows
            .Where(c => c.Value is not null)
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.LastUpdatedAt).ThenBy(c => c.Id).Last().Value!,
                StringComparer.Ordinal);

        var topK = NormalizeTopK(GetConfig(values, CK_TopK, options.TopK));
        var includedTopN = NormalizeIncludedTopN(
            GetConfig(values, CK_IncludedTopN, options.IncludedTopN),
            topK);
        var pollSeconds = NormalizePollSeconds(GetConfig(values, CK_PollSecs, options.PollIntervalSeconds));

        return new FeatureInteractionConfig(
            Enabled: GetConfig(values, CK_Enabled, options.Enabled),
            InitialDelay: TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(
                GetConfig(values, CK_InitialDelaySecs, options.InitialDelaySeconds))),
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            PollSeconds: pollSeconds,
            TopK: topK,
            IncludedTopN: includedTopN,
            MinSamples: NormalizeMinSamples(GetConfig(values, CK_MinSamples, options.MinSamples)),
            MaxLogsPerModel: NormalizeMaxLogsPerModel(
                GetConfig(values, CK_MaxLogsPerModel, options.MaxLogsPerModel)),
            MaxFeatures: NormalizeMaxFeatures(GetConfig(values, CK_MaxFeatures, options.MaxFeatures)),
            MaxModelsPerCycle: NormalizeMaxModelsPerCycle(
                GetConfig(values, CK_MaxModelsPerCycle, options.MaxModelsPerCycle)),
            MinEffectSize: NormalizeMinEffectSize(GetConfig(values, CK_MinEffectSize, options.MinEffectSize)),
            MaxQValue: NormalizeMaxQValue(GetConfig(values, CK_MaxQValue, options.MaxQValue)),
            LockTimeoutSeconds: NormalizeLockTimeoutSeconds(
                GetConfig(values, CK_LockTimeoutSecs, options.LockTimeoutSeconds)),
            DbCommandTimeoutSeconds: NormalizeDbCommandTimeoutSeconds(
                GetConfig(values, CK_DbCommandTimeoutSecs, options.DbCommandTimeoutSeconds)));
    }

    private static T GetConfig<T>(
        IReadOnlyDictionary<string, string> values,
        string key,
        T defaultValue)
    {
        if (!values.TryGetValue(key, out var raw))
            return defaultValue;

        return TryConvertConfig(raw, out T parsed)
            ? parsed
            : defaultValue;
    }

    private static bool TryConvertConfig<T>(string value, out T result)
    {
        object? parsed = null;
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        var normalized = value.Trim();

        if (targetType == typeof(string))
        {
            parsed = value;
        }
        else if (targetType == typeof(int)
                 && int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            parsed = intValue;
        }
        else if (targetType == typeof(double)
                 && double.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
        {
            parsed = doubleValue;
        }
        else if (targetType == typeof(bool)
                 && TryParseBool(normalized, out var boolValue))
        {
            parsed = boolValue;
        }

        if (parsed is T typed)
        {
            result = typed;
            return true;
        }

        result = default!;
        return false;
    }

    internal static int NormalizeInitialDelaySeconds(int value)
        => value is >= 0 and <= 86_400 ? value : DefaultInitialDelaySeconds;

    internal static int NormalizePollSeconds(int value)
        => value is >= 60 and <= 604_800 ? value : DefaultPollSeconds;

    internal static int NormalizeTopK(int value)
        => value is >= 1 and <= 20 ? value : DefaultTopK;

    internal static int NormalizeIncludedTopN(int value, int topK)
    {
        if (value is < 0 or > 20)
            return Math.Min(DefaultIncludedTopN, topK);

        return Math.Min(value, topK);
    }

    internal static int NormalizeMinSamples(int value)
        => value is >= 50 and <= 100_000 ? value : DefaultMinSamples;

    internal static int NormalizeMaxLogsPerModel(int value)
        => value is >= 100 and <= 100_000 ? value : DefaultMaxLogsPerModel;

    internal static int NormalizeMaxFeatures(int value)
        => value is >= 2 and <= MLFeatureHelper.MaxAllowedFeatureCount ? value : DefaultMaxFeatures;

    internal static int NormalizeMaxModelsPerCycle(int value)
        => value is >= 1 and <= 10_000 ? value : DefaultMaxModelsPerCycle;

    internal static double NormalizeMinEffectSize(double value)
        => double.IsFinite(value) && value is >= 0.0 and <= 1.0 ? value : DefaultMinEffectSize;

    internal static double NormalizeMaxQValue(double value)
        => double.IsFinite(value) && value is >= 0.0 and <= 1.0 ? value : DefaultMaxQValue;

    internal static int NormalizeLockTimeoutSeconds(int value)
        => value is >= 0 and <= 300 ? value : DefaultLockTimeoutSeconds;

    internal static int NormalizeDbCommandTimeoutSeconds(int value)
        => value is >= 1 and <= 600 ? value : DefaultDbCommandTimeoutSeconds;

    private static bool TryParseBool(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;

        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        return false;
    }

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLFeatureInteractionCyclesSkipped.Add(1, Tag("reason", reason));

    private static KeyValuePair<string, object?> Tag(string key, object? value)
        => new(key, value);

    private static TimeSpan CalculateBackoffDelay(int consecutiveFailures)
    {
        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var seconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        try
        {
            if (db.Database.IsRelational())
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(seconds));
        }
        catch (InvalidOperationException)
        {
            // Some providers do not expose relational command timeout configuration.
        }
    }

    public readonly record struct InteractionRow(double[] Values, double Label);

    public readonly record struct InteractionCandidate(
        int A,
        int B,
        double Score,
        double EffectSize,
        double PValue,
        double QValue);

    private sealed record PredictionFeatureLog(
        string? RawFeaturesJson,
        string? ShapValuesJson,
        TradeDirection ActualDirection);

    private sealed record InteractionRowParseResult(
        List<InteractionRow> Rows,
        bool UsedRawFeatures,
        int Malformed,
        int WrongShape,
        int NonFinite);

    internal sealed record FeatureInteractionConfig(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int PollSeconds,
        int TopK,
        int IncludedTopN,
        int MinSamples,
        int MaxLogsPerModel,
        int MaxFeatures,
        int MaxModelsPerCycle,
        double MinEffectSize,
        double MaxQValue,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds);

    internal sealed record FeatureInteractionCycleResult(
        FeatureInteractionConfig Config,
        int ModelsDiscovered,
        int ModelsProcessed,
        int ModelsSkipped,
        int ModelsFailed,
        int AuditsWritten,
        int StaleAuditsDeleted,
        string? SkippedReason)
    {
        public static FeatureInteractionCycleResult Skipped(FeatureInteractionConfig config, string reason)
            => new(
                config,
                ModelsDiscovered: 0,
                ModelsProcessed: 0,
                ModelsSkipped: 0,
                ModelsFailed: 0,
                AuditsWritten: 0,
                StaleAuditsDeleted: 0,
                SkippedReason: reason);
    }

    private sealed record FeatureInteractionModelResult(
        bool Processed,
        string State,
        int UsableRows,
        int AuditsWritten,
        int StaleAuditsDeleted,
        string? Method)
    {
        public static FeatureInteractionModelResult Skipped(
            string reason,
            int usableRows = 0,
            string? method = null)
            => new(
                Processed: false,
                State: reason,
                UsableRows: usableRows,
                AuditsWritten: 0,
                StaleAuditsDeleted: 0,
                Method: method);
    }
}
