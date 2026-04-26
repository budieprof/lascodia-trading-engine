using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors active ML models for per-feature Population Stability Index (PSI) drift.
/// </summary>
public sealed class MLFeaturePsiWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLFeaturePsiWorker);

    private const string DistributedLockKey = "workers:ml-feature-psi:cycle";
    private const string AlertDeduplicationPrefix = "MLFeaturePsi:";
    private const string DriftTriggerType = "FeaturePSI";

    private const string CK_Enabled = "MLFeaturePsi:Enabled";
    private const string CK_InitialDelaySecs = "MLFeaturePsi:InitialDelaySeconds";
    private const string CK_PollSecs = "MLFeaturePsi:PollIntervalSeconds";
    private const string CK_CandleWindowDays = "MLFeaturePsi:CandleWindowDays";
    private const string CK_MinFeatureSamples = "MLFeaturePsi:MinFeatureSamples";
    private const string CK_PsiAlertThresh = "MLFeaturePsi:PsiAlertThreshold";
    private const string CK_PsiRetrainThresh = "MLFeaturePsi:PsiRetrainThreshold";
    private const string CK_RetrainMajorityFraction = "MLFeaturePsi:RetrainMajorityFraction";
    private const string CK_MaxModelsPerCycle = "MLFeaturePsi:MaxModelsPerCycle";
    private const string CK_MaxFeaturesInAlert = "MLFeaturePsi:MaxFeaturesInAlert";
    private const string CK_TrainingWindowDays = "MLFeaturePsi:TrainingWindowDays";
    private const string CK_RetrainCooldownSecs = "MLFeaturePsi:RetrainCooldownSeconds";
    private const string CK_LockTimeoutSecs = "MLFeaturePsi:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSecs = "MLFeaturePsi:DbCommandTimeoutSeconds";
    private const string CK_AlertCooldownSecs = "MLFeaturePsi:AlertCooldownSeconds";
    private const string CK_AlertDest = "MLFeaturePsi:AlertDestination";

    private const int DefaultInitialDelaySeconds = 0;
    private const int DefaultPollSeconds = 7_200;
    private const int DefaultCandleWindowDays = 14;
    private const int DefaultMinFeatureSamples = 50;
    private const int DefaultMaxModelsPerCycle = 256;
    private const int DefaultMaxFeaturesInAlert = 5;
    private const int DefaultTrainingWindowDays = 365;
    private const int DefaultRetrainCooldownSeconds = 86_400;
    private const int DefaultLockTimeoutSeconds = 0;
    private const int DefaultDbCommandTimeoutSeconds = 30;
    private const double DefaultPsiAlertThreshold = 0.25;
    private const double DefaultPsiRetrainThreshold = 0.40;
    private const double DefaultRetrainMajorityFraction = 0.50;
    private const string DefaultAlertDestination = "ml-ops";
    private const int AlertConditionMaxLength = 1_500;

    private static readonly string[] ConfigKeys =
    [
        CK_Enabled,
        CK_InitialDelaySecs,
        CK_PollSecs,
        CK_CandleWindowDays,
        CK_MinFeatureSamples,
        CK_PsiAlertThresh,
        CK_PsiRetrainThresh,
        CK_RetrainMajorityFraction,
        CK_MaxModelsPerCycle,
        CK_MaxFeaturesInAlert,
        CK_TrainingWindowDays,
        CK_RetrainCooldownSecs,
        CK_LockTimeoutSecs,
        CK_DbCommandTimeoutSecs,
        CK_AlertCooldownSecs,
        CK_AlertDest,
        AlertCooldownDefaults.CK_MLMonitoring
    ];

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeaturePsiWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly MLFeaturePsiOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    public MLFeaturePsiWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLFeaturePsiWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        MLFeaturePsiOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new MLFeaturePsiOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Monitors per-feature PSI drift against training-time feature quantiles.",
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
                    _metrics?.MLFeaturePsiTimeSinceLastSuccessSec.Record((nowUtc - lastSuccessUtc).TotalSeconds);

                if (nowUtc - lastCycleStartUtc >= currentPollInterval)
                {
                    lastCycleStartUtc = nowUtc;
                    var started = Stopwatch.GetTimestamp();

                    try
                    {
                        _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                        var result = await RunCycleAsync(stoppingToken);
                        currentPollInterval = result.Config.PollInterval;

                        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        _healthMonitor?.RecordBacklogDepth(WorkerName, result.ModelsDiscovered);
                        _healthMonitor?.RecordCycleSuccess(WorkerName, elapsedMs);
                        _metrics?.WorkerCycleDurationMs.Record(
                            elapsedMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.MLFeaturePsiCycleDurationMs.Record(elapsedMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: models={Models}, evaluated={Evaluated}, skipped={Skipped}, failed={Failed}, highPsiFeatures={HighPsiFeatures}, alertsUpserted={AlertsUpserted}, alertsResolved={AlertsResolved}, retrainsQueued={RetrainsQueued}.",
                                WorkerName,
                                result.ModelsDiscovered,
                                result.ModelsEvaluated,
                                result.ModelsSkipped,
                                result.ModelsFailed,
                                result.HighPsiFeatureCount,
                                result.AlertsUpserted,
                                result.AlertsResolved,
                                result.RetrainsQueued);
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
                            new KeyValuePair<string, object?>("reason", "ml_feature_psi_cycle"));
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

    internal async Task<FeaturePsiCycleResult> RunCycleAsync(CancellationToken ct)
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
            return FeaturePsiCycleResult.Skipped(config, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLFeaturePsiLockAttempts.Add(1, Tag("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate PSI alerts/retrains are possible in multi-instance deployments.",
                    WorkerName);
            }
        }
        else
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(config.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLFeaturePsiLockAttempts.Add(1, Tag("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                return FeaturePsiCycleResult.Skipped(config, "lock_busy");
            }

            _metrics?.MLFeaturePsiLockAttempts.Add(1, Tag("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunPsiAsync(readCtx, writeCtx, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    internal async Task<FeaturePsiCycleResult> RunPsiAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CancellationToken ct)
    {
        var config = await LoadConfigAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
            return FeaturePsiCycleResult.Skipped(config, "disabled");

        return await RunPsiAsync(readCtx, writeCtx, config, ct);
    }

    private async Task<FeaturePsiCycleResult> RunPsiAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeaturePsiConfig config,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var models = await LoadActiveModelsAsync(readCtx, config.MaxModelsPerCycle, ct);
        if (models.Truncated)
            RecordCycleSkipped("model_limit");

        var activeDedupKeys = models.Items
            .Select(model => BuildDeduplicationKey(model.Id))
            .ToHashSet(StringComparer.Ordinal);

        int evaluated = 0;
        int skipped = 0;
        int failed = 0;
        int highPsiFeatures = 0;
        int alertsUpserted = 0;
        int alertsResolved = 0;
        int retrainsQueued = 0;

        foreach (var model in models.Items)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await EvaluateModelAsync(readCtx, writeCtx, model, config, nowUtc, ct);
                if (result.Evaluated)
                    evaluated++;
                else
                    skipped++;

                highPsiFeatures += result.HighPsiFeatureCount;
                if (result.AlertUpserted)
                    alertsUpserted++;
                if (result.AlertResolved)
                    alertsResolved++;
                if (result.RetrainQueued)
                    retrainsQueued++;

                if (result.Evaluated)
                {
                    _metrics?.MLFeaturePsiMaxPsi.Record(
                        result.MaxPsi,
                        Tag("symbol", model.Symbol),
                        Tag("timeframe", model.Timeframe.ToString()));
                    _metrics?.MLFeaturePsiHighFeatures.Record(
                        result.HighPsiFeatureCount,
                        Tag("symbol", model.Symbol),
                        Tag("timeframe", model.Timeframe.ToString()));
                }
                else
                {
                    _metrics?.MLFeaturePsiModelsSkipped.Add(
                        1,
                        Tag("reason", result.State),
                        Tag("symbol", model.Symbol),
                        Tag("timeframe", model.Timeframe.ToString()));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _metrics?.MLFeaturePsiModelsSkipped.Add(
                    1,
                    Tag("reason", "model_error"),
                    Tag("symbol", model.Symbol),
                    Tag("timeframe", model.Timeframe.ToString()));
                _logger.LogWarning(
                    ex,
                    "{Worker}: PSI evaluation failed for model {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    WorkerName,
                    model.Id,
                    model.Symbol,
                    model.Timeframe);
            }
        }

        if (models.Truncated)
        {
            _logger.LogDebug(
                "{Worker}: skipped stale PSI alert cleanup because active model set was truncated at {MaxModels}.",
                WorkerName,
                config.MaxModelsPerCycle);
        }
        else
        {
            alertsResolved += await ResolveInactiveModelAlertsAsync(writeCtx, activeDedupKeys, nowUtc, ct);
        }

        await writeCtx.SaveChangesAsync(ct);

        _metrics?.MLFeaturePsiModelsEvaluated.Add(evaluated);
        if (skipped > 0)
            _metrics?.MLFeaturePsiModelsSkipped.Add(skipped, Tag("reason", "cycle_total"));
        if (alertsUpserted > 0)
            _metrics?.MLFeaturePsiAlertTransitions.Add(alertsUpserted, Tag("transition", "upserted"));
        if (alertsResolved > 0)
            _metrics?.MLFeaturePsiAlertTransitions.Add(alertsResolved, Tag("transition", "resolved"));
        if (retrainsQueued > 0)
            _metrics?.MLFeaturePsiRetrainsQueued.Add(retrainsQueued);

        return new FeaturePsiCycleResult(
            Config: config,
            ModelsDiscovered: models.Items.Count,
            ModelsEvaluated: evaluated,
            ModelsSkipped: skipped,
            ModelsFailed: failed,
            HighPsiFeatureCount: highPsiFeatures,
            AlertsUpserted: alertsUpserted,
            AlertsResolved: alertsResolved,
            RetrainsQueued: retrainsQueued,
            SkippedReason: null);
    }

    private static async Task<(List<ModelProjection> Items, bool Truncated)> LoadActiveModelsAsync(
        DbContext db,
        int maxModels,
        CancellationToken ct)
    {
        var rows = await db.Set<MLModel>()
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
            .Take(maxModels + 1)
            .Select(m => new ModelProjection(m.Id, m.Symbol, m.Timeframe, m.ModelBytes!))
            .ToListAsync(ct);

        var truncated = rows.Count > maxModels;
        if (truncated)
            rows.RemoveAt(rows.Count - 1);

        return (rows, truncated);
    }

    private async Task<FeaturePsiModelResult> EvaluateModelAsync(
        DbContext readCtx,
        DbContext writeCtx,
        ModelProjection model,
        FeaturePsiConfig config,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (!TryDeserializeSnapshot(model.ModelBytes, out var snapshot))
            return FeaturePsiModelResult.Skipped("invalid_snapshot");

        var featureCount = ResolveComparableFeatureCount(snapshot);
        if (featureCount <= 0)
            return FeaturePsiModelResult.Skipped("missing_snapshot_stats");

        var windowStart = nowUtc.AddDays(-config.CandleWindowDays);
        var candles = await readCtx.Set<Candle>()
            .AsNoTracking()
            .Where(c => c.Symbol == model.Symbol
                        && c.Timeframe == model.Timeframe
                        && c.Timestamp >= windowStart
                        && c.IsClosed
                        && !c.IsDeleted)
            .OrderBy(c => c.Timestamp)
            .ToListAsync(ct);

        var requiredCandles = MLFeatureHelper.LookbackWindow + config.MinFeatureSamples + 1;
        if (candles.Count < requiredCandles)
            return FeaturePsiModelResult.Skipped("insufficient_candles");

        var recentSamples = MLFeatureHelper.BuildTrainingSamples(candles);
        if (recentSamples.Count < config.MinFeatureSamples)
            return FeaturePsiModelResult.Skipped("insufficient_feature_samples");

        if (!TryBuildComparableSamples(
                snapshot,
                recentSamples,
                featureCount,
                config.MinFeatureSamples,
                out var comparableSamples,
                out var activeFeatureIndices,
                out var skipReason))
        {
            return FeaturePsiModelResult.Skipped(skipReason);
        }

        var featurePsi = new List<FeaturePsiValue>(activeFeatureIndices.Count);
        foreach (var featureIndex in activeFeatureIndices)
        {
            var edges = SanitizeBinEdges(snapshot.FeatureQuantileBreakpoints[featureIndex]);
            if (edges.Length == 0)
                continue;

            var recentVals = comparableSamples
                .Select(sample => (double)sample.Features[featureIndex])
                .Where(double.IsFinite)
                .ToArray();

            if (recentVals.Length < config.MinFeatureSamples)
                continue;

            var trainingVals = GenerateUniformFromEdges(edges, recentVals.Length);
            var psi = MLFeatureHelper.ComputeFeaturePsi(edges, trainingVals, recentVals);
            if (!double.IsFinite(psi))
                continue;

            featurePsi.Add(new FeaturePsiValue(
                featureIndex,
                ResolveFeatureName(snapshot, featureIndex),
                psi));
        }

        if (featurePsi.Count == 0)
            return FeaturePsiModelResult.Skipped("no_comparable_features");

        var highPsi = featurePsi
            .Where(f => f.Psi >= config.PsiAlertThreshold)
            .OrderByDescending(f => f.Psi)
            .ToArray();
        var retrainTriggerCount = featurePsi.Count(f => f.Psi >= config.PsiRetrainThreshold);
        var maxPsi = featurePsi.Max(f => f.Psi);
        var avgPsi = featurePsi.Average(f => f.Psi);
        var dedupKey = BuildDeduplicationKey(model.Id);

        var alertUpserted = false;
        var alertResolved = false;
        var retrainQueued = false;

        if (highPsi.Length > 0)
        {
            await UpsertAlertAsync(
                writeCtx,
                model,
                config,
                dedupKey,
                featurePsi.Count,
                highPsi,
                maxPsi,
                avgPsi,
                retrainTriggerCount,
                nowUtc,
                ct);
            alertUpserted = true;

            if (ShouldQueueRetrain(retrainTriggerCount, featurePsi.Count, config.RetrainMajorityFraction))
            {
                var queueResult = await TryQueueRetrainAsync(
                    writeCtx,
                    model,
                    config,
                    highPsi,
                    maxPsi,
                    avgPsi,
                    retrainTriggerCount,
                    featurePsi.Count,
                    nowUtc,
                    ct);
                retrainQueued = queueResult == RetrainQueueResult.Queued;
            }

            _logger.LogWarning(
                "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) has {High}/{Total} feature(s) above PSI threshold {Threshold:F3}; maxPsi={MaxPsi:F4}.",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe,
                highPsi.Length,
                featurePsi.Count,
                config.PsiAlertThreshold,
                maxPsi);
        }
        else
        {
            alertResolved = await ResolveAlertAsync(writeCtx, model.Symbol, dedupKey, nowUtc, ct);
            _logger.LogDebug(
                "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) PSI healthy; maxPsi={MaxPsi:F4}, avgPsi={AvgPsi:F4}.",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe,
                maxPsi,
                avgPsi);
        }

        return new FeaturePsiModelResult(
            Evaluated: true,
            State: highPsi.Length > 0 ? "psi_drift" : "healthy",
            CheckedFeatureCount: featurePsi.Count,
            HighPsiFeatureCount: highPsi.Length,
            MaxPsi: maxPsi,
            AvgPsi: avgPsi,
            AlertUpserted: alertUpserted,
            AlertResolved: alertResolved,
            RetrainQueued: retrainQueued);
    }

    private async Task UpsertAlertAsync(
        DbContext writeCtx,
        ModelProjection model,
        FeaturePsiConfig config,
        string dedupKey,
        int checkedFeatureCount,
        IReadOnlyList<FeaturePsiValue> highPsi,
        double maxPsi,
        double avgPsi,
        int retrainTriggerCount,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alert = await writeCtx.Set<Alert>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == dedupKey && !a.IsDeleted, ct);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                DeduplicationKey = dedupKey,
            };
            writeCtx.Set<Alert>().Add(alert);
        }

        var payload = JsonSerializer.Serialize(new
        {
            detectorType = "FeaturePSI",
            modelId = model.Id,
            model.Symbol,
            timeframe = model.Timeframe.ToString(),
            alertDestination = config.AlertDestination,
            checkedFeatureCount,
            highPsiFeatureCount = highPsi.Count,
            retrainTriggerCount,
            maxPsi,
            avgPsi,
            thresholds = new
            {
                alert = config.PsiAlertThreshold,
                retrain = config.PsiRetrainThreshold,
                config.RetrainMajorityFraction,
            },
            topPsiFeatures = highPsi.Take(config.MaxFeaturesInAlert).Select(f => new
            {
                f.Index,
                f.Name,
                psi = Math.Round(f.Psi, 6),
            }),
            observedAtUtc = nowUtc,
        }, JsonOptions);

        alert.AlertType = AlertType.MLModelDegraded;
        alert.Symbol = model.Symbol;
        alert.ConditionJson = Truncate(payload, AlertConditionMaxLength);
        alert.Severity = ShouldQueueRetrain(retrainTriggerCount, checkedFeatureCount, config.RetrainMajorityFraction)
            ? AlertSeverity.High
            : AlertSeverity.Medium;
        alert.CooldownSeconds = config.AlertCooldownSeconds;
        alert.IsActive = true;
        alert.IsDeleted = false;
        alert.AutoResolvedAt = null;
    }

    private static async Task<bool> ResolveAlertAsync(
        DbContext writeCtx,
        string symbol,
        string dedupKey,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alerts = await writeCtx.Set<Alert>()
            .Where(a => a.IsActive
                        && !a.IsDeleted
                        && a.AlertType == AlertType.MLModelDegraded
                        && a.Symbol == symbol
                        && (a.DeduplicationKey == dedupKey
                            || (a.DeduplicationKey == null
                                && a.ConditionJson.Contains("FeaturePSI"))))
            .ToListAsync(ct);

        foreach (var alert in alerts)
        {
            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;
        }

        return alerts.Count > 0;
    }

    private static async Task<int> ResolveInactiveModelAlertsAsync(
        DbContext writeCtx,
        IReadOnlySet<string> activeDedupKeys,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alerts = await writeCtx.Set<Alert>()
            .Where(a => a.IsActive
                        && !a.IsDeleted
                        && a.DeduplicationKey != null
                        && a.DeduplicationKey.StartsWith(AlertDeduplicationPrefix))
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var alert in alerts)
        {
            if (alert.DeduplicationKey is null || activeDedupKeys.Contains(alert.DeduplicationKey))
                continue;

            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;
            resolved++;
        }

        return resolved;
    }

    private static async Task<RetrainQueueResult> TryQueueRetrainAsync(
        DbContext writeCtx,
        ModelProjection model,
        FeaturePsiConfig config,
        IReadOnlyList<FeaturePsiValue> highPsi,
        double maxPsi,
        double avgPsi,
        int retrainTriggerCount,
        int checkedFeatureCount,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (await HasQueuedOrRunningRetrainAsync(writeCtx, model.Symbol, model.Timeframe, ct))
            return RetrainQueueResult.AlreadyQueued;

        if (config.RetrainCooldown > TimeSpan.Zero)
        {
            var cooldownStart = nowUtc - config.RetrainCooldown;
            var recentlyQueued = await writeCtx.Set<MLTrainingRun>()
                .AsNoTracking()
                .AnyAsync(run => run.Symbol == model.Symbol
                              && run.Timeframe == model.Timeframe
                              && run.DriftTriggerType == DriftTriggerType
                              && run.StartedAt >= cooldownStart
                              && run.Status != RunStatus.Failed,
                    ct);
            if (recentlyQueued)
                return RetrainQueueResult.Cooldown;
        }

        var metadata = JsonSerializer.Serialize(new
        {
            detector = WorkerName,
            driftTriggerType = DriftTriggerType,
            modelId = model.Id,
            maxPsi,
            avgPsi,
            retrainTriggerCount,
            checkedFeatureCount,
            config.PsiAlertThreshold,
            config.PsiRetrainThreshold,
            config.RetrainMajorityFraction,
            topPsiFeatures = highPsi.Take(config.MaxFeaturesInAlert).Select(f => new
            {
                f.Index,
                f.Name,
                psi = f.Psi,
            }),
        }, JsonOptions);

        writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            TriggerType = TriggerType.AutoDegrading,
            Status = RunStatus.Queued,
            FromDate = nowUtc.AddDays(-config.TrainingWindowDays),
            ToDate = nowUtc,
            StartedAt = nowUtc,
            DriftTriggerType = DriftTriggerType,
            DriftMetadataJson = metadata,
            Priority = 1,
        });

        return RetrainQueueResult.Queued;
    }

    private static async Task<bool> HasQueuedOrRunningRetrainAsync(
        DbContext db,
        string symbol,
        Timeframe timeframe,
        CancellationToken ct)
    {
        var localMatch = db.Set<MLTrainingRun>().Local.Any(run =>
            run.Symbol == symbol
            && run.Timeframe == timeframe
            && (run.Status == RunStatus.Queued || run.Status == RunStatus.Running));
        if (localMatch)
            return true;

        return await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(run => run.Symbol == symbol
                          && run.Timeframe == timeframe
                          && (run.Status == RunStatus.Queued || run.Status == RunStatus.Running),
                ct);
    }

    private static bool TryDeserializeSnapshot(byte[] modelBytes, out ModelSnapshot snapshot)
    {
        snapshot = new ModelSnapshot();
        if (modelBytes.Length == 0)
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<ModelSnapshot>(modelBytes, JsonOptions);
            if (parsed is null)
                return false;

            parsed.Features ??= [];
            parsed.Means ??= [];
            parsed.Stds ??= [];
            parsed.FeatureQuantileBreakpoints ??= [];
            parsed.ActiveFeatureMask ??= [];
            snapshot = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int ResolveComparableFeatureCount(ModelSnapshot snapshot)
    {
        var resolved = snapshot.ResolveExpectedInputFeatures();
        var limit = Math.Min(resolved, snapshot.FeatureQuantileBreakpoints.Length);
        limit = Math.Min(limit, snapshot.Means.Length);
        limit = Math.Min(limit, snapshot.Stds.Length);
        return limit is >= 1 and <= MLFeatureHelper.MaxAllowedFeatureCount ? limit : 0;
    }

    private static bool TryBuildComparableSamples(
        ModelSnapshot snapshot,
        IReadOnlyList<TrainingSample> samples,
        int featureCount,
        int minSamples,
        out List<TrainingSample> comparableSamples,
        out IReadOnlyList<int> activeFeatureIndices,
        out string skipReason)
    {
        comparableSamples = [];
        activeFeatureIndices = [];
        skipReason = string.Empty;

        foreach (var sample in samples)
        {
            if (sample.Features.Length < featureCount)
                continue;

            var features = new float[featureCount];
            var finite = true;
            for (var j = 0; j < featureCount; j++)
            {
                var std = Math.Abs(snapshot.Stds[j]) < 1e-6f ? 1f : snapshot.Stds[j];
                var value = (sample.Features[j] - snapshot.Means[j]) / std;
                if (!float.IsFinite(value))
                {
                    finite = false;
                    break;
                }

                features[j] = value;
            }

            if (finite)
                comparableSamples.Add(sample with { Features = features });
        }

        if (comparableSamples.Count < minSamples)
        {
            skipReason = "insufficient_comparable_samples";
            return false;
        }

        if (snapshot.FracDiffD > 0.0)
            comparableSamples = MLFeatureHelper.ApplyFractionalDifferencing(comparableSamples, featureCount, snapshot.FracDiffD);

        var active = Enumerable.Range(0, featureCount).ToArray();
        if (snapshot.ActiveFeatureMask is { Length: > 0 } mask && mask.Length >= featureCount)
        {
            active = active.Where(index => mask[index]).ToArray();
            for (var i = 0; i < comparableSamples.Count; i++)
            {
                var features = (float[])comparableSamples[i].Features.Clone();
                for (var j = 0; j < featureCount; j++)
                {
                    if (!mask[j])
                        features[j] = 0f;
                }

                comparableSamples[i] = comparableSamples[i] with { Features = features };
            }
        }

        if (active.Length == 0)
        {
            skipReason = "no_active_features";
            return false;
        }

        activeFeatureIndices = active;
        return true;
    }

    internal static double[] GenerateUniformFromEdges(double[] edges, int n)
    {
        var sanitized = SanitizeBinEdges(edges);
        if (sanitized.Length == 0 || n <= 0)
            return [];

        var bins = sanitized.Length + 1;
        var result = new double[n];
        var range = sanitized[^1] - sanitized[0];
        if (!double.IsFinite(range) || Math.Abs(range) < 1e-9)
            range = 1.0;

        var leftTail = sanitized[0] - range * 0.5;
        var rightTail = sanitized[^1] + range * 0.5;
        var cursor = 0;
        for (var bin = 0; bin < bins; bin++)
        {
            var count = n / bins + (bin < n % bins ? 1 : 0);
            var lo = bin == 0 ? leftTail : sanitized[bin - 1];
            var hi = bin < sanitized.Length ? sanitized[bin] : rightTail;
            var mid = (lo + hi) / 2.0;
            for (var i = 0; i < count && cursor < result.Length; i++)
                result[cursor++] = mid;
        }

        return result;
    }

    private static double[] SanitizeBinEdges(double[]? edges)
    {
        if (edges is null || edges.Length == 0)
            return [];

        var sorted = edges
            .Where(double.IsFinite)
            .OrderBy(v => v)
            .ToArray();
        if (sorted.Length == 0)
            return [];

        var unique = new List<double>(sorted.Length);
        foreach (var value in sorted)
        {
            if (unique.Count == 0 || Math.Abs(value - unique[^1]) > 1e-9)
                unique.Add(value);
        }

        return unique.ToArray();
    }

    private static string ResolveFeatureName(ModelSnapshot snapshot, int index)
    {
        if (snapshot.Features.Length > index && !string.IsNullOrWhiteSpace(snapshot.Features[index]))
            return snapshot.Features[index];

        var names = MLFeatureHelper.ResolveFeatureNames(Math.Max(index + 1, MLFeatureHelper.FeatureCount));
        return names.Length > index ? names[index] : $"F{index}";
    }

    private static bool ShouldQueueRetrain(int retrainTriggerCount, int checkedFeatureCount, double majorityFraction)
        => checkedFeatureCount > 0 && retrainTriggerCount > checkedFeatureCount * majorityFraction;

    internal static async Task<FeaturePsiConfig> LoadConfigAsync(
        DbContext ctx,
        MLFeaturePsiOptions options,
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

        var pollSeconds = NormalizePollSeconds(GetConfig(values, CK_PollSecs, options.PollIntervalSeconds));
        var alertCooldownDefault = GetConfig(
            values,
            AlertCooldownDefaults.CK_MLMonitoring,
            options.AlertCooldownSeconds);
        var psiAlertThreshold = NormalizePsiThreshold(
            GetConfig(values, CK_PsiAlertThresh, options.PsiAlertThreshold),
            DefaultPsiAlertThreshold);
        var psiRetrainThreshold = NormalizePsiThreshold(
            GetConfig(values, CK_PsiRetrainThresh, options.PsiRetrainThreshold),
            DefaultPsiRetrainThreshold);
        psiRetrainThreshold = Math.Max(psiRetrainThreshold, psiAlertThreshold);

        return new FeaturePsiConfig(
            Enabled: GetConfig(values, CK_Enabled, options.Enabled),
            InitialDelay: TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(
                GetConfig(values, CK_InitialDelaySecs, options.InitialDelaySeconds))),
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            PollSeconds: pollSeconds,
            CandleWindowDays: NormalizeCandleWindowDays(GetConfig(values, CK_CandleWindowDays, options.CandleWindowDays)),
            MinFeatureSamples: NormalizeMinFeatureSamples(
                GetConfig(values, CK_MinFeatureSamples, options.MinFeatureSamples)),
            PsiAlertThreshold: psiAlertThreshold,
            PsiRetrainThreshold: psiRetrainThreshold,
            RetrainMajorityFraction: NormalizeMajorityFraction(
                GetConfig(values, CK_RetrainMajorityFraction, options.RetrainMajorityFraction)),
            MaxModelsPerCycle: NormalizeMaxModelsPerCycle(
                GetConfig(values, CK_MaxModelsPerCycle, options.MaxModelsPerCycle)),
            MaxFeaturesInAlert: NormalizeMaxFeaturesInAlert(
                GetConfig(values, CK_MaxFeaturesInAlert, options.MaxFeaturesInAlert)),
            TrainingWindowDays: NormalizeTrainingWindowDays(
                GetConfig(values, CK_TrainingWindowDays, options.TrainingWindowDays)),
            RetrainCooldown: TimeSpan.FromSeconds(NormalizeRetrainCooldownSeconds(
                GetConfig(values, CK_RetrainCooldownSecs, options.RetrainCooldownSeconds))),
            LockTimeoutSeconds: NormalizeLockTimeoutSeconds(
                GetConfig(values, CK_LockTimeoutSecs, options.LockTimeoutSeconds)),
            DbCommandTimeoutSeconds: NormalizeDbCommandTimeoutSeconds(
                GetConfig(values, CK_DbCommandTimeoutSecs, options.DbCommandTimeoutSeconds)),
            AlertCooldownSeconds: NormalizeAlertCooldownSeconds(
                GetConfig(values, CK_AlertCooldownSecs, alertCooldownDefault)),
            AlertDestination: NormalizeDestination(GetConfig(values, CK_AlertDest, options.AlertDestination)));
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

    internal static int NormalizeCandleWindowDays(int value)
        => value is >= 1 and <= 3_650 ? value : DefaultCandleWindowDays;

    internal static int NormalizeMinFeatureSamples(int value)
        => value is >= 20 and <= 100_000 ? value : DefaultMinFeatureSamples;

    internal static double NormalizePsiThreshold(double value, double fallback)
        => double.IsFinite(value) && value is >= 0.01 and <= 5.0 ? value : fallback;

    internal static double NormalizeMajorityFraction(double value)
        => double.IsFinite(value) && value is >= 0.0 and <= 1.0 ? value : DefaultRetrainMajorityFraction;

    internal static int NormalizeMaxModelsPerCycle(int value)
        => value is >= 1 and <= 10_000 ? value : DefaultMaxModelsPerCycle;

    internal static int NormalizeMaxFeaturesInAlert(int value)
        => value is >= 1 and <= 100 ? value : DefaultMaxFeaturesInAlert;

    internal static int NormalizeTrainingWindowDays(int value)
        => value is >= 1 and <= 3_650 ? value : DefaultTrainingWindowDays;

    internal static int NormalizeRetrainCooldownSeconds(int value)
        => value is >= 0 and <= 2_592_000 ? value : DefaultRetrainCooldownSeconds;

    internal static int NormalizeLockTimeoutSeconds(int value)
        => value is >= 0 and <= 300 ? value : DefaultLockTimeoutSeconds;

    internal static int NormalizeDbCommandTimeoutSeconds(int value)
        => value is >= 1 and <= 600 ? value : DefaultDbCommandTimeoutSeconds;

    internal static int NormalizeAlertCooldownSeconds(int value)
        => value is >= 1 and <= 604_800 ? value : AlertCooldownDefaults.Default_MLMonitoring;

    internal static string NormalizeDestination(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return DefaultAlertDestination;

        return trimmed.Length <= 128 ? trimmed : trimmed[..128];
    }

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
        => _metrics?.MLFeaturePsiCyclesSkipped.Add(1, Tag("reason", reason));

    private static string BuildDeduplicationKey(long modelId)
        => $"{AlertDeduplicationPrefix}{modelId}";

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

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

    internal sealed record FeaturePsiConfig(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int PollSeconds,
        int CandleWindowDays,
        int MinFeatureSamples,
        double PsiAlertThreshold,
        double PsiRetrainThreshold,
        double RetrainMajorityFraction,
        int MaxModelsPerCycle,
        int MaxFeaturesInAlert,
        int TrainingWindowDays,
        TimeSpan RetrainCooldown,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds,
        int AlertCooldownSeconds,
        string AlertDestination);

    internal sealed record FeaturePsiCycleResult(
        FeaturePsiConfig Config,
        int ModelsDiscovered,
        int ModelsEvaluated,
        int ModelsSkipped,
        int ModelsFailed,
        int HighPsiFeatureCount,
        int AlertsUpserted,
        int AlertsResolved,
        int RetrainsQueued,
        string? SkippedReason)
    {
        public static FeaturePsiCycleResult Skipped(FeaturePsiConfig config, string reason)
            => new(
                config,
                ModelsDiscovered: 0,
                ModelsEvaluated: 0,
                ModelsSkipped: 0,
                ModelsFailed: 0,
                HighPsiFeatureCount: 0,
                AlertsUpserted: 0,
                AlertsResolved: 0,
                RetrainsQueued: 0,
                SkippedReason: reason);
    }

    private sealed record ModelProjection(long Id, string Symbol, Timeframe Timeframe, byte[] ModelBytes);

    private sealed record FeaturePsiValue(int Index, string Name, double Psi);

    private sealed record FeaturePsiModelResult(
        bool Evaluated,
        string State,
        int CheckedFeatureCount,
        int HighPsiFeatureCount,
        double MaxPsi,
        double AvgPsi,
        bool AlertUpserted,
        bool AlertResolved,
        bool RetrainQueued)
    {
        public static FeaturePsiModelResult Skipped(string reason)
            => new(
                Evaluated: false,
                State: reason,
                CheckedFeatureCount: 0,
                HighPsiFeatureCount: 0,
                MaxPsi: 0,
                AvgPsi: 0,
                AlertUpserted: false,
                AlertResolved: false,
                RetrainQueued: false);
    }

    private enum RetrainQueueResult
    {
        Queued,
        AlreadyQueued,
        Cooldown
    }
}
