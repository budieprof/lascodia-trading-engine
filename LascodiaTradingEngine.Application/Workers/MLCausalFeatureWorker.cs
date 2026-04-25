using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
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
/// Audits live ML model input features for predictive causality against resolved outcomes.
/// </summary>
/// <remarks>
/// The previous implementation had three major correctness issues:
/// it rebuilt feature rows from recent candles instead of using the served live feature
/// vectors, it ignored the resolved prediction outcomes for the return series, and it reset
/// operator-managed <see cref="MLCausalFeatureAudit.IsMaskedForTraining"/> state on every run.
///
/// This worker uses authoritative resolved prediction logs with persisted
/// <see cref="MLModelPredictionLog.RawFeaturesJson"/> plus signed
/// <see cref="MLModelPredictionLog.ActualMagnitudePips"/> outcomes, preserves masking state
/// across refreshes, and runs on the write side with distributed-lock and worker-health
/// integration so it can be safely deployed in multi-instance production environments.
///
/// Statistical guarantees: per-feature Granger F-tests are corrected for multiple comparisons
/// using the Benjamini-Hochberg procedure at <c>FdrAlpha</c>, so the family-wise false-discovery
/// rate is bounded across the model's feature set rather than expanding linearly with feature
/// count. The unrestricted/restricted regressions are solved via Cholesky decomposition of the
/// ridge-regularised normal equations, which is exact for the SPD design matrix and roughly
/// twice as fast as Gauss elimination.
///
/// Operational alarms: a <see cref="AlertType.MLMonitoringStale"/> alert fires when a model is
/// skipped for more than <c>ConsecutiveSkipAlertThreshold</c> consecutive cycles (typically a
/// broken prediction-logging pipeline). A <see cref="AlertType.MLModelDegraded"/> alert fires
/// when a model's causal-feature ratio drops by more than <c>RegressionThreshold</c> from the
/// previous cycle (typically a regime shift or a feature schema break). Both alert paths are
/// gated by <c>MaxAlertsPerCycle</c>.
///
/// <para><b>Winsorization semantics:</b> when <c>WinsorizePercentile</c> &gt; 0, the worker
/// clips both the realised-return series and each feature series at the
/// <c>[p, 1-p]</c> empirical quantiles before the F-test. The resulting q-values are
/// statements about Granger causality on the <em>winsorized</em> series, not the raw series —
/// i.e. "does this feature predict the bulk of the return distribution after the most
/// extreme tails are damped." The mean-edge contract used by the trainer is unchanged
/// because the trainer never sees the winsorized values; this is purely a robustness knob
/// for the audit, opt-in via config.</para>
///
/// <para><b>Operator playbook — when to flip which knob:</b></para>
/// <list type="bullet">
///   <item><c>FdrProcedure</c>: keep <see cref="FdrProcedure.BenjaminiHochberg"/> for the
///         typical case (features roughly independent or with positive regression
///         dependence). Switch to <see cref="FdrProcedure.BenjaminiYekutieli"/> when the
///         feature set is known to have heavy collinearity or shared regression bases
///         (e.g. macro V3 features built off the same underlying series), accepting
///         ~<c>Σ(1/i)</c>-times less power in exchange for FDR control under arbitrary
///         dependence.</item>
///   <item><c>InformationCriterion</c>: <see cref="InformationCriterion.Aic"/> at small
///         samples (n &lt; ~150). <see cref="InformationCriterion.Bic"/> when n is large
///         and residuals are heavy-tailed (favours smaller lags, more conservative).
///         <see cref="InformationCriterion.Hqic"/> is a middle ground often preferred
///         for time-series lag selection on financial data.</item>
///   <item><c>WinsorizePercentile</c>: keep at <c>0.0</c> (off) by default. Bump to
///         <c>0.01</c>–<c>0.05</c> when the realised-return series is contaminated by
///         gap fills, weekend re-pricing, or whale losses that visibly dominate
///         regressions. Higher percentiles (e.g. <c>0.10</c>) are appropriate only for
///         low-quality data streams — they materially change the test's interpretation.</item>
/// </list>
/// </remarks>
public sealed class MLCausalFeatureWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLCausalFeatureWorker);

    private const string DistributedLockKey = "workers:ml-causal-feature:cycle";
    private const string StaleMonitoringDeduplicationPrefix = "ml-causal-stale:";
    private const string RegressionDeduplicationPrefix = "ml-causal-regression:";
    private const int AlertConditionMaxLength = 1000;

    private const string CK_Enabled = "MLCausal:Enabled";
    private const string CK_PollSecs = "MLCausal:PollIntervalSeconds";
    private const string CK_WindowDays = "MLCausal:WindowDays";
    private const string CK_MinSamples = "MLCausal:MinSamples";
    private const string CK_MaxLogsPerModel = "MLCausal:MaxLogsPerModel";
    private const string CK_MaxModelsPerCycle = "MLCausal:MaxModelsPerCycle";
    private const string CK_MaxLag = "MLCausal:MaxLag";
    private const string CK_PValueThreshold = "MLCausal:PValueThreshold";
    private const string CK_LockTimeoutSeconds = "MLCausal:LockTimeoutSeconds";
    private const string CK_FdrAlpha = "MLCausal:FdrAlpha";
    private const string CK_FdrProcedure = "MLCausal:FdrProcedure";
    private const string CK_InformationCriterion = "MLCausal:InformationCriterion";
    private const string CK_WinsorizePercentile = "MLCausal:WinsorizePercentile";
    private const string CK_ConsecutiveSkipAlertThreshold = "MLCausal:ConsecutiveSkipAlertThreshold";
    private const string CK_RegressionThreshold = "MLCausal:RegressionThreshold";
    private const string CK_MinPriorCausalForRegression = "MLCausal:MinPriorCausalForRegression";
    private const string CK_MaxAlertsPerCycle = "MLCausal:MaxAlertsPerCycle";

    private const int DefaultPollSeconds = 7 * 24 * 60 * 60;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 30 * 24 * 60 * 60;

    private const int DefaultWindowDays = 180;
    private const int MinWindowDays = 1;
    private const int MaxWindowDays = 3650;

    private const int DefaultMinSamples = 80;
    private const int MinMinSamples = 20;
    private const int MaxMinSamples = 100_000;

    private const int DefaultMaxLogsPerModel = 512;
    private const int MinMaxLogsPerModel = 20;
    private const int MaxMaxLogsPerModel = 100_000;

    private const int DefaultMaxModelsPerCycle = 256;
    private const int MinMaxModelsPerCycle = 1;
    private const int MaxMaxModelsPerCycle = 10_000;

    private const int DefaultMaxLag = 10;
    private const int MinMaxLag = 1;
    private const int MaxMaxLag = 30;

    private const double DefaultPValueThreshold = 0.05;
    private const double MinPValueThreshold = 0.000001;
    private const double MaxPValueThreshold = 0.50;

    private const int DefaultLockTimeoutSeconds = 0;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private const double DefaultFdrAlpha = 0.05;
    private const double MinFdrAlpha = 0.000001;
    private const double MaxFdrAlpha = 0.50;

    private const int DefaultConsecutiveSkipAlertThreshold = 2;
    private const int MinConsecutiveSkipAlertThreshold = 1;
    private const int MaxConsecutiveSkipAlertThreshold = 100;

    private const double DefaultRegressionThreshold = 0.5;
    private const double MinRegressionThreshold = 0.0;
    private const double MaxRegressionThreshold = 1.0;

    private const int DefaultMinPriorCausalForRegression = 5;
    private const int MinMinPriorCausalForRegression = 1;
    private const int MaxMinPriorCausalForRegression = 1000;

    private const int DefaultMaxAlertsPerCycle = 10;
    private const int MinMaxAlertsPerCycle = 0;
    private const int MaxMaxAlertsPerCycle = 1000;

    private const FdrProcedure DefaultFdrProcedure = FdrProcedure.BenjaminiHochberg;

    private const InformationCriterion DefaultInformationCriterion = InformationCriterion.Aic;

    private const double DefaultWinsorizePercentile = 0.0;
    private const double MinWinsorizePercentile = 0.0;
    private const double MaxWinsorizePercentile = 0.20;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLCausalFeatureWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    private readonly record struct ActiveModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        byte[] ModelBytes);

    private readonly record struct PredictionFeatureLog(
        long Id,
        long MLModelId,
        DateTime PredictedAt,
        DateTime OutcomeRecordedAt,
        decimal ActualMagnitudePips,
        string RawFeaturesJson);

    private readonly record struct CausalObservation(
        double[] Features,
        double RealisedReturn,
        DateTime PredictedAt,
        DateTime OutcomeRecordedAt);

    private readonly record struct ParsedObservationSet(
        List<CausalObservation> Observations,
        int Malformed,
        int WrongShape,
        int NonFinite);

    private readonly record struct ModelAuditOutcome(
        bool Evaluated,
        int SamplesUsed,
        int AuditsWritten,
        int CausalFeatures,
        int PreservedMasks,
        bool RegressionAlertDispatched,
        bool StaleMonitoringAlertDispatched,
        bool AlertBackpressureSkipped,
        string? SkipReason)
    {
        public static ModelAuditOutcome Skipped(
            string reason,
            bool staleMonitoringAlertDispatched = false,
            bool alertBackpressureSkipped = false)
            => new(false, 0, 0, 0, 0, false, staleMonitoringAlertDispatched, alertBackpressureSkipped, reason);
    }

    private sealed class CycleContext
    {
        public required Dictionary<long, List<PredictionFeatureLog>> LogsByModelId { get; init; }
        public required Dictionary<long, int> SkipStreaksByModelId { get; init; }
        public required Dictionary<long, double> PriorCausalRatioByModelId { get; init; }
        public required Dictionary<long, int> PriorCausalCountByModelId { get; init; }
        public required HashSet<long> ActiveStaleAlertModelIds { get; init; }
        public required HashSet<long> ActiveRegressionAlertModelIds { get; init; }
        public required AlertBudget AlertBudget { get; init; }
    }

    private sealed class AlertBudget
    {
        private int _remaining;

        public AlertBudget(int capacity)
        {
            _remaining = capacity;
        }

        public bool HasCapacity => _remaining > 0;

        public bool TryConsume()
        {
            if (_remaining <= 0)
                return false;

            _remaining--;
            return true;
        }
    }

    public MLCausalFeatureWorker(
        ILogger<MLCausalFeatureWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Runs schema-aware Granger causality audits with Benjamini-Hochberg FDR control over authoritative live ML prediction logs while preserving operator-approved feature masking state.",
            TimeSpan.FromSeconds(DefaultPollSeconds));

        var currentDelay = TimeSpan.FromSeconds(DefaultPollSeconds);

        try
        {
            try
            {
                var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName);
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                long cycleStarted = Stopwatch.GetTimestamp();

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                    var result = await RunCycleAsync(stoppingToken);
                    currentDelay = result.Settings.PollInterval;

                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.CandidateModelCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.MLCausalFeatureCycleDurationMs.Record(durationMs);

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "{Worker}: candidates={Candidates}, evaluated={Evaluated}, skipped={Skipped}, failed={Failed}, auditsWritten={AuditsWritten}, causalFeatures={CausalFeatures}, preservedMasks={PreservedMasks}, regressionAlerts={RegressionAlerts}, staleMonitoringAlerts={StaleAlerts}, alertBackpressureSkipped={AlertBackpressureSkipped}.",
                            WorkerName,
                            result.CandidateModelCount,
                            result.EvaluatedModelCount,
                            result.SkippedModelCount,
                            result.FailedModelCount,
                            result.AuditsWrittenCount,
                            result.CausalFeatureCount,
                            result.PreservedMaskCount,
                            result.RegressionAlertCount,
                            result.StaleMonitoringAlertCount,
                            result.AlertBackpressureSkippedCount);
                    }

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _logger.LogInformation(
                            "{Worker}: recovered after {Failures} consecutive failure(s).",
                            WorkerName,
                            _consecutiveFailures);
                    }

                    _consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _metrics?.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("reason", "ml_causal_feature_cycle"));
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                try
                {
                    await Task.Delay(CalculateDelay(currentDelay, _consecutiveFailures), _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<MLCausalFeatureCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var writeContext = serviceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (!settings.Enabled)
        {
            _metrics?.MLCausalFeatureCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "disabled"));
            return MLCausalFeatureCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.MLCausalFeatureLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate causal-feature cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(settings.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLCausalFeatureLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.MLCausalFeatureCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return MLCausalFeatureCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLCausalFeatureLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));

            await using (cycleLock)
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
                try
                {
                    return await RunCycleCoreAsync(serviceProvider, writeContext, db, settings, ct);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
        }

        await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
        try
        {
            return await RunCycleCoreAsync(serviceProvider, writeContext, db, settings, ct);
        }
        finally
        {
            WorkerBulkhead.MLMonitoring.Release();
        }
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return baseInterval <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(DefaultPollSeconds)
                : baseInterval;
        }

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<MLCausalFeatureCycleResult> RunCycleCoreAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        MLCausalFeatureWorkerSettings settings,
        CancellationToken ct)
    {
        var models = await db.Set<MLModel>()
            .AsNoTracking()
            .Where(model =>
                model.IsActive &&
                !model.IsDeleted &&
                !model.IsMetaLearner &&
                !model.IsMamlInitializer &&
                model.ModelBytes != null)
            .OrderBy(model => model.Symbol)
            .ThenBy(model => model.Timeframe)
            .ThenByDescending(model => model.TrainedAt)
            .Take(settings.MaxModelsPerCycle)
            .Select(model => new ActiveModelCandidate(
                model.Id,
                model.Symbol,
                model.Timeframe,
                model.ModelBytes!))
            .ToListAsync(ct);

        _healthMonitor?.RecordBacklogDepth(WorkerName, models.Count);

        if (models.Count == 0)
        {
            _metrics?.MLCausalFeatureCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_active_models"));
            return MLCausalFeatureCycleResult.Skipped(settings, "no_active_models");
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var logsByModelId = await BatchLoadResolvedLogsAsync(db, models, settings, nowUtc, ct);
        var perModelEngineConfig = await BatchLoadPerModelEngineConfigAsync(db, models, ct);
        var activeStaleAlertIds = await BatchLoadActiveAlertModelIdsAsync(
            db, models, StaleMonitoringDeduplicationPrefix, ct);
        var activeRegressionAlertIds = await BatchLoadActiveAlertModelIdsAsync(
            db, models, RegressionDeduplicationPrefix, ct);

        var cycleCtx = new CycleContext
        {
            LogsByModelId = logsByModelId,
            SkipStreaksByModelId = perModelEngineConfig.SkipStreaks,
            PriorCausalRatioByModelId = perModelEngineConfig.CausalRatios,
            PriorCausalCountByModelId = perModelEngineConfig.CausalCounts,
            ActiveStaleAlertModelIds = activeStaleAlertIds,
            ActiveRegressionAlertModelIds = activeRegressionAlertIds,
            AlertBudget = new AlertBudget(settings.MaxAlertsPerCycle),
        };

        int evaluatedModels = 0;
        int skippedModels = 0;
        int failedModels = 0;
        int auditsWritten = 0;
        int causalFeatures = 0;
        int preservedMasks = 0;
        int regressionAlerts = 0;
        int staleAlerts = 0;
        int backpressureSkipped = 0;

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var outcome = await AuditModelAsync(
                    serviceProvider, writeContext, db, model, settings, cycleCtx, nowUtc, ct);

                if (!outcome.Evaluated)
                {
                    skippedModels++;
                    if (outcome.StaleMonitoringAlertDispatched) staleAlerts++;
                    if (outcome.AlertBackpressureSkipped) backpressureSkipped++;
                    _metrics?.MLCausalFeatureModelsSkipped.Add(
                        1,
                        new KeyValuePair<string, object?>("reason", outcome.SkipReason ?? "skipped"),
                        new KeyValuePair<string, object?>("symbol", model.Symbol),
                        new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                    continue;
                }

                evaluatedModels++;
                auditsWritten += outcome.AuditsWritten;
                causalFeatures += outcome.CausalFeatures;
                preservedMasks += outcome.PreservedMasks;
                if (outcome.RegressionAlertDispatched) regressionAlerts++;
                if (outcome.AlertBackpressureSkipped) backpressureSkipped++;

                _metrics?.MLCausalFeatureModelsEvaluated.Add(
                    1,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                _metrics?.MLCausalFeatureAuditsWritten.Add(
                    outcome.AuditsWritten,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                _metrics?.MLCausalFeatureResolvedSamples.Record(
                    outcome.SamplesUsed,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                _metrics?.MLCausalFeatureCausalFeatures.Record(
                    outcome.CausalFeatures,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedModels++;
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName),
                    new KeyValuePair<string, object?>("reason", "ml_causal_feature_model"));
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed to audit model {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    WorkerName,
                    model.Id,
                    model.Symbol,
                    model.Timeframe);
            }
        }

        return new MLCausalFeatureCycleResult(
            settings,
            SkippedReason: null,
            CandidateModelCount: models.Count,
            EvaluatedModelCount: evaluatedModels,
            SkippedModelCount: skippedModels,
            FailedModelCount: failedModels,
            AuditsWrittenCount: auditsWritten,
            CausalFeatureCount: causalFeatures,
            PreservedMaskCount: preservedMasks,
            RegressionAlertCount: regressionAlerts,
            StaleMonitoringAlertCount: staleAlerts,
            AlertBackpressureSkippedCount: backpressureSkipped);
    }

    private static async Task<Dictionary<long, List<PredictionFeatureLog>>> BatchLoadResolvedLogsAsync(
        DbContext db,
        IReadOnlyList<ActiveModelCandidate> models,
        MLCausalFeatureWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var modelIds = models.Select(model => model.Id).ToList();
        var lookbackCutoff = nowUtc.AddDays(-settings.WindowDays);

        var rows = await db.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(log =>
                modelIds.Contains(log.MLModelId) &&
                !log.IsDeleted &&
                log.RawFeaturesJson != null &&
                log.ActualMagnitudePips != null &&
                log.OutcomeRecordedAt != null &&
                log.OutcomeRecordedAt >= lookbackCutoff)
            .Select(log => new PredictionFeatureLog(
                log.Id,
                log.MLModelId,
                log.PredictedAt,
                log.OutcomeRecordedAt!.Value,
                log.ActualMagnitudePips!.Value,
                log.RawFeaturesJson!))
            .ToListAsync(ct);

        var byModel = new Dictionary<long, List<PredictionFeatureLog>>();
        foreach (var row in rows)
        {
            if (!byModel.TryGetValue(row.MLModelId, out var bucket))
            {
                bucket = [];
                byModel[row.MLModelId] = bucket;
            }
            bucket.Add(row);
        }

        foreach (var bucket in byModel.Values)
        {
            bucket.Sort((a, b) =>
            {
                int cmp = b.OutcomeRecordedAt.CompareTo(a.OutcomeRecordedAt);
                return cmp != 0 ? cmp : b.Id.CompareTo(a.Id);
            });
            if (bucket.Count > settings.MaxLogsPerModel)
                bucket.RemoveRange(settings.MaxLogsPerModel, bucket.Count - settings.MaxLogsPerModel);
        }

        return byModel;
    }

    private readonly record struct PerModelEngineConfig(
        Dictionary<long, int> SkipStreaks,
        Dictionary<long, double> CausalRatios,
        Dictionary<long, int> CausalCounts);

    private static async Task<PerModelEngineConfig> BatchLoadPerModelEngineConfigAsync(
        DbContext db,
        IReadOnlyList<ActiveModelCandidate> models,
        CancellationToken ct)
    {
        var keys = new List<string>(models.Count * 3);
        foreach (var model in models)
        {
            keys.Add(SkipStreakKey(model.Id));
            keys.Add(CausalRatioKey(model.Id));
            keys.Add(CausalCountKey(model.Id));
        }

        var rows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(config => keys.Contains(config.Key))
            .Select(config => new { config.Key, config.Value })
            .ToListAsync(ct);

        var skipStreaks = new Dictionary<long, int>();
        var causalRatios = new Dictionary<long, double>();
        var causalCounts = new Dictionary<long, int>();

        foreach (var row in rows)
        {
            if (TryParseModelIdFromConfigKey(row.Key, ":ConsecutiveSkips", out var skipModelId)
                && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var skip))
            {
                skipStreaks[skipModelId] = skip;
            }
            else if (TryParseModelIdFromConfigKey(row.Key, ":CausalRatio", out var ratioModelId)
                && double.TryParse(row.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)
                && double.IsFinite(ratio))
            {
                causalRatios[ratioModelId] = ratio;
            }
            else if (TryParseModelIdFromConfigKey(row.Key, ":CausalCount", out var countModelId)
                && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                causalCounts[countModelId] = count;
            }
        }

        return new PerModelEngineConfig(skipStreaks, causalRatios, causalCounts);
    }

    private static async Task<HashSet<long>> BatchLoadActiveAlertModelIdsAsync(
        DbContext db,
        IReadOnlyList<ActiveModelCandidate> models,
        string deduplicationPrefix,
        CancellationToken ct)
    {
        var dedupKeys = models
            .Select(model => deduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture))
            .ToList();

        var rows = await db.Set<Alert>()
            .AsNoTracking()
            .Where(alert => !alert.IsDeleted
                         && alert.IsActive
                         && alert.DeduplicationKey != null
                         && dedupKeys.Contains(alert.DeduplicationKey))
            .Select(alert => alert.DeduplicationKey!)
            .ToListAsync(ct);

        var modelIds = new HashSet<long>();
        foreach (var key in rows)
        {
            if (key.Length <= deduplicationPrefix.Length)
                continue;
            var span = key.AsSpan(deduplicationPrefix.Length);
            if (long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                modelIds.Add(id);
        }
        return modelIds;
    }

    private async Task<ModelAuditOutcome> AuditModelAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCausalFeatureWorkerSettings settings,
        CycleContext cycleCtx,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var snapshot = TryDeserializeSnapshot(model.ModelBytes);
        if (snapshot is null)
            return await HandleSkipAsync(
                serviceProvider, writeContext, db, model, settings, cycleCtx, "snapshot_deserialize_failed", nowUtc, ct);

        int auditedFeatureCount = ResolveAuditedFeatureCount(snapshot);
        if (auditedFeatureCount < 1 || auditedFeatureCount > MLFeatureHelper.MaxAllowedFeatureCount)
            return await HandleSkipAsync(
                serviceProvider, writeContext, db, model, settings, cycleCtx, "unsupported_feature_schema", nowUtc, ct);

        string[] featureNames = ResolveFeatureNames(snapshot, auditedFeatureCount);
        if (featureNames.Length < auditedFeatureCount)
            return await HandleSkipAsync(
                serviceProvider, writeContext, db, model, settings, cycleCtx, "feature_names_unavailable", nowUtc, ct);

        cycleCtx.LogsByModelId.TryGetValue(model.Id, out var resolvedLogs);
        if (resolvedLogs is null || resolvedLogs.Count < settings.MinSamples)
            return await HandleSkipAsync(
                serviceProvider, writeContext, db, model, settings, cycleCtx, "insufficient_resolved_logs", nowUtc, ct);

        var parsed = BuildObservationSet(resolvedLogs, auditedFeatureCount);
        if (parsed.Observations.Count < settings.MinSamples)
        {
            _logger.LogDebug(
                "{Worker}: skipping model {ModelId} ({Symbol}/{Timeframe}) due to insufficient usable causal rows. usable={Usable}, malformed={Malformed}, wrongShape={WrongShape}, nonFinite={NonFinite}.",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe,
                parsed.Observations.Count,
                parsed.Malformed,
                parsed.WrongShape,
                parsed.NonFinite);
            return await HandleSkipAsync(
                serviceProvider, writeContext, db, model, settings, cycleCtx, "insufficient_usable_feature_rows", nowUtc, ct);
        }

        // Sort by predicted-event time so the F-test interprets lag in the model's own
        // event-stream order. Note: lag here is array-index lag (1 = previous prediction),
        // not clock-time lag. For irregularly sampled streams the two diverge; the audit
        // result remains the right "does this feature improve the next event's prediction"
        // signal even though it isn't a calendar-time test.
        parsed.Observations.Sort(static (left, right) =>
        {
            int predictedCompare = left.PredictedAt.CompareTo(right.PredictedAt);
            if (predictedCompare != 0)
                return predictedCompare;
            return left.OutcomeRecordedAt.CompareTo(right.OutcomeRecordedAt);
        });

        double[] realisedReturns = parsed.Observations.Select(o => o.RealisedReturn).ToArray();
        if (realisedReturns.Length < settings.MinSamples)
            return await HandleSkipAsync(
                serviceProvider, writeContext, db, model, settings, cycleCtx, "insufficient_realised_returns", nowUtc, ct);

        // Winsorize the dependent series first so the y-tails can't dominate the F-test.
        // When WinsorizePercentile is 0, the call is a no-op.
        Winsorize(realisedReturns, settings.WinsorizePercentile);

        // Compute per-feature F-stat and raw p-value; defer the IsCausal decision until
        // after multiple-comparison correction across the full feature set.
        var perFeature = new (double FStat, double PValue, int Lag)[auditedFeatureCount];
        for (int featureIndex = 0; featureIndex < auditedFeatureCount; featureIndex++)
        {
            ct.ThrowIfCancellationRequested();

            double[] featureSeries = new double[parsed.Observations.Count];
            for (int row = 0; row < parsed.Observations.Count; row++)
                featureSeries[row] = parsed.Observations[row].Features[featureIndex];
            Winsorize(featureSeries, settings.WinsorizePercentile);

            int bestLag = SelectLagByInformationCriterion(
                realisedReturns, featureSeries, settings.MaxLag, settings.InformationCriterion);
            var (fStat, pValue) = GrangerFTest(realisedReturns, featureSeries, bestLag);
            perFeature[featureIndex] = (fStat, pValue, bestLag);
        }

        var (isCausal, qValues) = ApplyFdrCorrection(
            perFeature.Select(x => x.PValue).ToArray(), settings.FdrAlpha, settings.FdrProcedure);

        // Existing audits keyed by (MLModelId, FeatureIndex). Tracked because we'll mutate
        // them in place.
        var existingAudits = await db.Set<MLCausalFeatureAudit>()
            .Where(audit => audit.MLModelId == model.Id && !audit.IsDeleted)
            .ToListAsync(ct);
        var existingByIndex = existingAudits.ToDictionary(audit => audit.FeatureIndex);
        var auditSet = db.Set<MLCausalFeatureAudit>();

        int causalCount = 0;
        int preservedMasks = 0;

        for (int featureIndex = 0; featureIndex < auditedFeatureCount; featureIndex++)
        {
            var (fStat, pValue, bestLag) = perFeature[featureIndex];
            bool causal = isCausal[featureIndex];
            double qValue = qValues[featureIndex];
            if (causal) causalCount++;

            if (existingByIndex.TryGetValue(featureIndex, out var existing))
            {
                bool maskPreserved = existing.IsMaskedForTraining;
                if (maskPreserved) preservedMasks++;

                existing.FeatureName = featureNames[featureIndex];
                existing.GrangerFStat = (decimal)fStat;
                existing.GrangerPValue = pValue;
                existing.GrangerQValue = qValue;
                existing.LagOrder = bestLag;
                existing.IsCausal = causal;
                existing.ComputedAt = nowUtc;
                existing.IsDeleted = false;
                existingByIndex.Remove(featureIndex);
            }
            else
            {
                auditSet.Add(new MLCausalFeatureAudit
                {
                    MLModelId = model.Id,
                    Symbol = model.Symbol,
                    Timeframe = model.Timeframe,
                    FeatureIndex = featureIndex,
                    FeatureName = featureNames[featureIndex],
                    GrangerFStat = (decimal)fStat,
                    GrangerPValue = pValue,
                    GrangerQValue = qValue,
                    LagOrder = bestLag,
                    IsCausal = causal,
                    IsMaskedForTraining = false,
                    ComputedAt = nowUtc,
                    IsDeleted = false
                });
            }
        }

        // Indices that were audited last cycle but no longer fit the schema (e.g. the
        // feature set shrank). Soft-delete them so the table doesn't carry stale rows.
        foreach (var staleAudit in existingByIndex.Values)
            staleAudit.IsDeleted = true;

        // Persist the new causal-ratio + skip-streak reset alongside the audits in one
        // commit so dashboards never observe a half-updated state.
        double newRatio = auditedFeatureCount > 0 ? (double)causalCount / auditedFeatureCount : 0.0;
        await EngineConfigUpsert.BatchUpsertAsync(
            db,
            new List<EngineConfigUpsertSpec>
            {
                new(SkipStreakKey(model.Id), "0", ConfigDataType.Int,
                    "Consecutive cycles in which the causal-feature worker could not evaluate this model.",
                    false),
                new(CausalRatioKey(model.Id),
                    newRatio.ToString("F4", CultureInfo.InvariantCulture),
                    ConfigDataType.Decimal,
                    "Latest causal-feature ratio (BH-FDR corrected) for this model.",
                    false),
                new(CausalCountKey(model.Id),
                    causalCount.ToString(CultureInfo.InvariantCulture),
                    ConfigDataType.Int,
                    "Latest causal-feature count (BH-FDR corrected) for this model.",
                    false),
                new($"MLCausal:Model:{model.Id}:LastEvaluatedAt",
                    nowUtc.ToString("O", CultureInfo.InvariantCulture),
                    ConfigDataType.String,
                    "UTC timestamp of the latest MLCausalFeatureWorker evaluation for this model.",
                    false),
            },
            ct);

        await writeContext.SaveChangesAsync(ct);

        // Resolve any active stale-monitoring alert on successful evaluation.
        if (cycleCtx.ActiveStaleAlertModelIds.Contains(model.Id))
        {
            var resolved = await ResolveAlertAsync(
                serviceProvider, writeContext, db, model, StaleMonitoringDeduplicationPrefix, nowUtc, ct);
            if (resolved)
                cycleCtx.ActiveStaleAlertModelIds.Remove(model.Id);
        }

        // Regression detection: fire when prior evaluation reported a non-trivial number of
        // causal features and the new ratio dropped by more than RegressionThreshold.
        bool regressionAlertDispatched = false;
        bool alertBackpressureSkipped = false;
        if (cycleCtx.PriorCausalRatioByModelId.TryGetValue(model.Id, out var priorRatio)
            && cycleCtx.PriorCausalCountByModelId.TryGetValue(model.Id, out var priorCausalCount)
            && priorCausalCount >= settings.MinPriorCausalForRegression
            && priorRatio > 0
            && newRatio < priorRatio * (1.0 - settings.RegressionThreshold))
        {
            if (cycleCtx.AlertBudget.HasCapacity)
            {
                cycleCtx.AlertBudget.TryConsume();
                regressionAlertDispatched = await UpsertAndDispatchRegressionAlertAsync(
                    serviceProvider, writeContext, db, model, settings,
                    priorRatio, newRatio, priorCausalCount, causalCount, nowUtc, ct);
                if (regressionAlertDispatched)
                    cycleCtx.ActiveRegressionAlertModelIds.Add(model.Id);
            }
            else
            {
                alertBackpressureSkipped = true;
            }
        }
        else if (cycleCtx.ActiveRegressionAlertModelIds.Contains(model.Id))
        {
            // Causal ratio recovered; auto-resolve the active regression alert.
            var resolved = await ResolveAlertAsync(
                serviceProvider, writeContext, db, model, RegressionDeduplicationPrefix, nowUtc, ct);
            if (resolved)
                cycleCtx.ActiveRegressionAlertModelIds.Remove(model.Id);
        }

        _logger.LogInformation(
            "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) audited {Features} features from {Samples} resolved live rows; causal={Causal} ({Ratio:P1}), preservedMasks={PreservedMasks}, regression={Regression}.",
            WorkerName,
            model.Id,
            model.Symbol,
            model.Timeframe,
            auditedFeatureCount,
            parsed.Observations.Count,
            causalCount,
            newRatio,
            preservedMasks,
            regressionAlertDispatched);

        return new ModelAuditOutcome(
            Evaluated: true,
            SamplesUsed: parsed.Observations.Count,
            AuditsWritten: auditedFeatureCount,
            CausalFeatures: causalCount,
            PreservedMasks: preservedMasks,
            RegressionAlertDispatched: regressionAlertDispatched,
            StaleMonitoringAlertDispatched: false,
            AlertBackpressureSkipped: alertBackpressureSkipped,
            SkipReason: null);
    }

    private async Task<ModelAuditOutcome> HandleSkipAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCausalFeatureWorkerSettings settings,
        CycleContext cycleCtx,
        string skipReason,
        DateTime nowUtc,
        CancellationToken ct)
    {
        int newStreak = cycleCtx.SkipStreaksByModelId.GetValueOrDefault(model.Id) + 1;
        cycleCtx.SkipStreaksByModelId[model.Id] = newStreak;

        await EngineConfigUpsert.BatchUpsertAsync(
            db,
            new List<EngineConfigUpsertSpec>
            {
                new(SkipStreakKey(model.Id),
                    newStreak.ToString(CultureInfo.InvariantCulture),
                    ConfigDataType.Int,
                    "Consecutive cycles in which the causal-feature worker could not evaluate this model.",
                    false),
                new($"MLCausal:Model:{model.Id}:LastSkipReason",
                    skipReason,
                    ConfigDataType.String,
                    "Reason the causal-feature worker last skipped this model.",
                    false),
                new($"MLCausal:Model:{model.Id}:LastEvaluatedAt",
                    nowUtc.ToString("O", CultureInfo.InvariantCulture),
                    ConfigDataType.String,
                    "UTC timestamp of the latest MLCausalFeatureWorker evaluation attempt for this model.",
                    false),
            },
            ct);

        bool staleAlertDispatched = false;
        bool alertBackpressureSkipped = false;
        if (newStreak >= settings.ConsecutiveSkipAlertThreshold)
        {
            if (cycleCtx.AlertBudget.HasCapacity)
            {
                cycleCtx.AlertBudget.TryConsume();
                staleAlertDispatched = await UpsertAndDispatchStaleMonitoringAlertAsync(
                    serviceProvider, writeContext, db, model, settings, skipReason, newStreak, nowUtc, ct);
                if (staleAlertDispatched)
                    cycleCtx.ActiveStaleAlertModelIds.Add(model.Id);
            }
            else
            {
                alertBackpressureSkipped = true;
            }
        }

        return ModelAuditOutcome.Skipped(skipReason, staleAlertDispatched, alertBackpressureSkipped);
    }

    private async Task<bool> UpsertAndDispatchStaleMonitoringAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCausalFeatureWorkerSettings settings,
        string skipReason,
        int consecutiveSkips,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string deduplicationKey = StaleMonitoringDeduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        DateTime? previousTriggeredAt = alert?.LastTriggeredAt;
        string conditionJson = Truncate(JsonSerializer.Serialize(new
        {
            detector = "MLCausalFeature",
            reason = "stale_monitoring",
            modelId = model.Id,
            symbol = model.Symbol,
            timeframe = model.Timeframe.ToString(),
            consecutiveSkips,
            lastSkipReason = skipReason,
            consecutiveSkipAlertThreshold = settings.ConsecutiveSkipAlertThreshold,
            evaluatedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
        }), AlertConditionMaxLength);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.MLMonitoringStale,
                DeduplicationKey = deduplicationKey,
                IsActive = true
            };
            db.Set<Alert>().Add(alert);
        }
        else
        {
            alert.AlertType = AlertType.MLMonitoringStale;
        }

        ApplyAlertFields(alert, model.Symbol, AlertSeverity.High, settings.AlertCooldownSeconds, conditionJson);

        try
        {
            await writeContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsLikelyAlertDeduplicationRace(serviceProvider, ex))
        {
            DetachIfAdded(db, alert);
            alert = await db.Set<Alert>()
                .FirstAsync(candidate => !candidate.IsDeleted
                                      && candidate.IsActive
                                      && candidate.DeduplicationKey == deduplicationKey, ct);
            previousTriggeredAt ??= alert.LastTriggeredAt;
            alert.AlertType = AlertType.MLMonitoringStale;
            ApplyAlertFields(alert, model.Symbol, AlertSeverity.High, settings.AlertCooldownSeconds, conditionJson);
            await writeContext.SaveChangesAsync(ct);
        }

        if (previousTriggeredAt.HasValue &&
            nowUtc - NormalizeUtc(previousTriggeredAt.Value) < TimeSpan.FromSeconds(settings.AlertCooldownSeconds))
        {
            return false;
        }

        string message = $"ML causal-feature monitoring stale for model {model.Id} ({model.Symbol}/{model.Timeframe}): {consecutiveSkips} consecutive skipped cycles ({skipReason}). The prediction-logging pipeline may be broken or the model is no longer being served.";
        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        try
        {
            await dispatcher.DispatchAsync(alert, message, ct);
            await writeContext.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch causal-feature stale-monitoring alert for model {ModelId}.",
                WorkerName,
                model.Id);
            return false;
        }
    }

    private async Task<bool> UpsertAndDispatchRegressionAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCausalFeatureWorkerSettings settings,
        double priorRatio,
        double newRatio,
        int priorCausalCount,
        int newCausalCount,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string deduplicationKey = RegressionDeduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        DateTime? previousTriggeredAt = alert?.LastTriggeredAt;
        string conditionJson = Truncate(JsonSerializer.Serialize(new
        {
            detector = "MLCausalFeature",
            reason = "causal_ratio_regression",
            modelId = model.Id,
            symbol = model.Symbol,
            timeframe = model.Timeframe.ToString(),
            priorCausalRatio = Math.Round(priorRatio, 6),
            newCausalRatio = Math.Round(newRatio, 6),
            priorCausalCount,
            newCausalCount,
            regressionThreshold = Math.Round(settings.RegressionThreshold, 6),
            evaluatedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
        }), AlertConditionMaxLength);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                DeduplicationKey = deduplicationKey,
                IsActive = true
            };
            db.Set<Alert>().Add(alert);
        }
        else
        {
            alert.AlertType = AlertType.MLModelDegraded;
        }

        ApplyAlertFields(alert, model.Symbol, AlertSeverity.High, settings.AlertCooldownSeconds, conditionJson);

        try
        {
            await writeContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsLikelyAlertDeduplicationRace(serviceProvider, ex))
        {
            DetachIfAdded(db, alert);
            alert = await db.Set<Alert>()
                .FirstAsync(candidate => !candidate.IsDeleted
                                      && candidate.IsActive
                                      && candidate.DeduplicationKey == deduplicationKey, ct);
            previousTriggeredAt ??= alert.LastTriggeredAt;
            alert.AlertType = AlertType.MLModelDegraded;
            ApplyAlertFields(alert, model.Symbol, AlertSeverity.High, settings.AlertCooldownSeconds, conditionJson);
            await writeContext.SaveChangesAsync(ct);
        }

        if (previousTriggeredAt.HasValue &&
            nowUtc - NormalizeUtc(previousTriggeredAt.Value) < TimeSpan.FromSeconds(settings.AlertCooldownSeconds))
        {
            return false;
        }

        string message = $"ML causal-feature ratio regressed for model {model.Id} ({model.Symbol}/{model.Timeframe}): {priorRatio:P1} ({priorCausalCount} features) → {newRatio:P1} ({newCausalCount} features). Possible regime shift, broken feature, or upstream data drift.";
        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        try
        {
            await dispatcher.DispatchAsync(alert, message, ct);
            await writeContext.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch causal-feature regression alert for model {ModelId}.",
                WorkerName,
                model.Id);
            return false;
        }
    }

    private async Task<bool> ResolveAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        string deduplicationPrefix,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string deduplicationKey = deduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        if (alert is null)
            return false;

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        if (alert.LastTriggeredAt.HasValue)
        {
            try
            {
                await dispatcher.TryAutoResolveAsync(alert, conditionStillActive: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "{Worker}: failed to auto-resolve alert {DeduplicationKey} for model {ModelId}.",
                    WorkerName,
                    deduplicationKey,
                    model.Id);
            }
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await writeContext.SaveChangesAsync(ct);
        return true;
    }

    private static void ApplyAlertFields(
        Alert alert,
        string symbol,
        AlertSeverity severity,
        int cooldownSeconds,
        string conditionJson)
    {
        alert.Symbol = symbol;
        alert.Severity = severity;
        alert.CooldownSeconds = cooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = conditionJson;
    }

    private static ParsedObservationSet BuildObservationSet(
        IReadOnlyList<PredictionFeatureLog> logs,
        int featureCount)
    {
        int malformed = 0;
        int wrongShape = 0;
        int nonFinite = 0;
        var observations = new List<CausalObservation>(logs.Count);

        foreach (var log in logs)
        {
            double[]? features;
            try
            {
                features = JsonSerializer.Deserialize<double[]>(log.RawFeaturesJson, JsonOptions);
            }
            catch (JsonException)
            {
                malformed++;
                continue;
            }

            if (features is null || features.Length < featureCount)
            {
                wrongShape++;
                continue;
            }

            var row = new double[featureCount];
            bool finite = double.IsFinite((double)log.ActualMagnitudePips);
            for (int i = 0; finite && i < featureCount; i++)
            {
                double value = features[i];
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

            observations.Add(new CausalObservation(
                row,
                (double)log.ActualMagnitudePips,
                log.PredictedAt,
                log.OutcomeRecordedAt));
        }

        return new ParsedObservationSet(observations, malformed, wrongShape, nonFinite);
    }

    internal static int ResolveAuditedFeatureCount(ModelSnapshot snapshot)
    {
        int resolvedFeatureCount = snapshot.ResolveExpectedInputFeatures();
        int auditedFeatureCount = snapshot.InteractionBaseFeatureCount > 0
            ? snapshot.InteractionBaseFeatureCount
            : resolvedFeatureCount;

        int schemaVersion = snapshot.ResolveFeatureSchemaVersion();
        if (schemaVersion >= 7 && snapshot.InteractionBaseFeatureCount <= 0)
            auditedFeatureCount = Math.Min(auditedFeatureCount, MLFeatureHelper.FeatureCountV6);

        auditedFeatureCount = Math.Min(auditedFeatureCount, resolvedFeatureCount);
        return auditedFeatureCount;
    }

    /// <summary>
    /// Benjamini-Hochberg step-up procedure controlling the false-discovery rate at
    /// <paramref name="alpha"/>. Equivalent to <see cref="ApplyFdrCorrection"/> with
    /// <see cref="FdrProcedure.BenjaminiHochberg"/>; retained for backwards-compatibility
    /// of unit tests.
    /// </summary>
    internal static bool[] ApplyBenjaminiHochberg(double[] pValues, double alpha)
        => ApplyFdrCorrection(pValues, alpha, FdrProcedure.BenjaminiHochberg).IsCausal;

    /// <summary>
    /// FDR-controlling step-up procedure. Returns both per-feature rejection flags and
    /// adjusted q-values (monotone min-from-the-right of <c>m·c·p_(j)/j</c> on the sorted
    /// p-values, then unsorted back to input order). The constant <c>c</c> is 1 for
    /// Benjamini-Hochberg (independence / PRDS) and <c>Σ_{i=1}^m 1/i</c> for
    /// Benjamini-Yekutieli (arbitrary dependence). A feature is rejected iff its q-value
    /// is at most <paramref name="alpha"/>.
    /// </summary>
    internal static (bool[] IsCausal, double[] QValues) ApplyFdrCorrection(
        double[] pValues, double alpha, FdrProcedure procedure)
    {
        int m = pValues.Length;
        var isCausal = new bool[m];
        var qValues = new double[m];
        if (m == 0) return (isCausal, qValues);

        // BY scale factor c(m) = 1 + 1/2 + ... + 1/m. BH uses c=1.
        double scale = 1.0;
        if (procedure == FdrProcedure.BenjaminiYekutieli)
        {
            double harmonic = 0.0;
            for (int i = 1; i <= m; i++) harmonic += 1.0 / i;
            scale = harmonic;
        }

        var indexed = new (int Index, double PValue)[m];
        for (int i = 0; i < m; i++)
            indexed[i] = (i, double.IsFinite(pValues[i]) ? pValues[i] : 1.0);
        Array.Sort(indexed, static (a, b) => a.PValue.CompareTo(b.PValue));

        // Adjusted q-values via monotone min-from-the-right pass on m·c·p_(j)/j.
        var sortedQ = new double[m];
        double runningMin = 1.0;
        for (int rank = m - 1; rank >= 0; rank--)
        {
            double raw = indexed[rank].PValue * m * scale / (rank + 1);
            if (raw < runningMin) runningMin = raw;
            sortedQ[rank] = Math.Clamp(runningMin, 0.0, 1.0);
        }

        for (int rank = 0; rank < m; rank++)
        {
            int originalIndex = indexed[rank].Index;
            qValues[originalIndex] = sortedQ[rank];
            isCausal[originalIndex] = sortedQ[rank] <= alpha;
        }

        return (isCausal, qValues);
    }

    internal static (double fStat, double pValue) GrangerFTest(double[] y, double[] x, int lag)
    {
        int n = Math.Min(y.Length, x.Length) - lag;
        if (lag <= 0 || n <= (2 * lag + 1))
            return (0.0, 1.0);

        double rssRestricted = ComputeRss(y, x, lag, includeX: false);
        double rssUnrestricted = ComputeRss(y, x, lag, includeX: true);
        if (!double.IsFinite(rssRestricted) || !double.IsFinite(rssUnrestricted) || rssRestricted <= 0.0 || rssUnrestricted <= 0.0)
            return (0.0, 1.0);

        double numerator = (rssRestricted - rssUnrestricted) / lag;
        double denominator = rssUnrestricted / (n - 2.0 * lag - 1.0);
        if (!double.IsFinite(numerator) || !double.IsFinite(denominator) || denominator <= 0.0)
            return (0.0, 1.0);

        double fStat = Math.Max(0.0, numerator / denominator);
        double pValue = FSurvival(fStat, lag, n - 2.0 * lag - 1.0);
        return (fStat, Math.Clamp(pValue, 0.0, 1.0));
    }

    /// <summary>
    /// AIC-based lag selection. Equivalent to <see cref="SelectLagByInformationCriterion"/>
    /// with <see cref="InformationCriterion.Aic"/>; retained for backwards-compatibility
    /// of unit tests.
    /// </summary>
    internal static int SelectLagByAic(double[] y, double[] x, int maxLag)
        => SelectLagByInformationCriterion(y, x, maxLag, InformationCriterion.Aic);

    /// <summary>
    /// Picks the lag (1..<paramref name="maxLag"/>) that minimises the chosen
    /// information criterion on the unrestricted regression. AIC is light-handed and
    /// good with small samples; BIC penalises complexity more (better for heavy-tailed
    /// residuals); HQIC sits between the two.
    /// <list type="bullet">
    ///   <item><c>AIC = n·log(rss/n) + 2·k</c></item>
    ///   <item><c>BIC = n·log(rss/n) + k·log(n)</c></item>
    ///   <item><c>HQIC = n·log(rss/n) + 2·k·log(log(n))</c></item>
    /// </list>
    /// </summary>
    internal static int SelectLagByInformationCriterion(
        double[] y, double[] x, int maxLag, InformationCriterion criterion)
    {
        int bestLag = 1;
        double bestScore = double.PositiveInfinity;

        for (int lag = 1; lag <= maxLag; lag++)
        {
            int n = Math.Min(y.Length, x.Length) - lag;
            if (n <= (2 * lag + 1))
                break;

            double rss = ComputeRss(y, x, lag, includeX: true);
            if (!double.IsFinite(rss) || rss <= 0.0)
                continue;

            int parameterCount = 1 + (2 * lag);
            double score = ScoreInformationCriterion(criterion, n, rss, parameterCount);
            if (score < bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        return bestLag;
    }

    private static double ScoreInformationCriterion(
        InformationCriterion criterion, int n, double rss, int parameterCount)
    {
        double logLikelihoodTerm = n * Math.Log(rss / n);
        return criterion switch
        {
            InformationCriterion.Bic =>
                logLikelihoodTerm + parameterCount * Math.Log(n),
            // log(log(n)) is undefined for n<=1; clamp via Max so HQIC stays finite at the
            // edge of the supported sample range.
            InformationCriterion.Hqic =>
                logLikelihoodTerm + 2.0 * parameterCount * Math.Log(Math.Max(2.0, Math.Log(n))),
            _ /* AIC */ =>
                logLikelihoodTerm + 2.0 * parameterCount,
        };
    }

    /// <summary>
    /// In-place winsorization: clips values outside the
    /// [<paramref name="percentile"/>, 1 − <paramref name="percentile"/>] quantile range
    /// to the boundary values. A no-op when <paramref name="percentile"/> ≤ 0. Used to
    /// damp the influence of return-tail outliers (whale losses, gap fills) on the F-test
    /// without changing the underlying mean-edge contract that the offline trainer
    /// optimizes against.
    /// </summary>
    internal static void Winsorize(double[] series, double percentile)
    {
        if (percentile <= 0.0 || series.Length == 0) return;
        if (percentile >= 0.5) percentile = 0.49;

        // Borrow a sorting buffer from the shared pool. ArrayPool may hand back a
        // larger array than requested, so the sort and quantile lookup must use only
        // the first series.Length slots.
        double[] sorted = ArrayPool<double>.Shared.Rent(series.Length);
        try
        {
            Array.Copy(series, sorted, series.Length);
            Array.Sort(sorted, 0, series.Length);

            int lowerIndex = (int)Math.Floor(percentile * (series.Length - 1));
            int upperIndex = (int)Math.Ceiling((1.0 - percentile) * (series.Length - 1));
            double lower = sorted[lowerIndex];
            double upper = sorted[upperIndex];

            for (int i = 0; i < series.Length; i++)
            {
                if (series[i] < lower) series[i] = lower;
                else if (series[i] > upper) series[i] = upper;
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(sorted);
        }
    }

    private static double ComputeRss(double[] y, double[] x, int lag, bool includeX)
    {
        int n = Math.Min(y.Length, x.Length) - lag;
        if (n <= 0)
            return double.PositiveInfinity;

        int columnCount = 1 + lag + (includeX ? lag : 0);
        var xtx = new double[columnCount, columnCount];
        var xty = new double[columnCount];

        Span<double> designRow = stackalloc double[1 + (2 * MaxMaxLag)];
        for (int row = 0; row < n; row++)
        {
            int t = row + lag;
            designRow[0] = 1.0;

            for (int l = 1; l <= lag; l++)
                designRow[l] = y[t - l];

            if (includeX)
            {
                for (int l = 1; l <= lag; l++)
                    designRow[lag + l] = x[t - l];
            }

            for (int i = 0; i < columnCount; i++)
            {
                xty[i] += designRow[i] * y[t];
                for (int j = 0; j < columnCount; j++)
                    xtx[i, j] += designRow[i] * designRow[j];
            }
        }

        // Ridge regularisation: XᵀX + λI is strictly positive definite, which is the
        // precondition Cholesky needs. λ is small enough not to bias the F-test
        // meaningfully but large enough to absorb floating-point noise on near-singular
        // designs (e.g. constant features or perfect collinearity between lags).
        for (int i = 0; i < columnCount; i++)
            xtx[i, i] += 1e-6;

        var beta = SolveSpdLinearSystem(xtx, xty);
        if (beta is null)
            return double.PositiveInfinity;

        double rss = 0.0;
        for (int row = 0; row < n; row++)
        {
            int t = row + lag;
            designRow[0] = 1.0;
            for (int l = 1; l <= lag; l++)
                designRow[l] = y[t - l];

            if (includeX)
            {
                for (int l = 1; l <= lag; l++)
                    designRow[lag + l] = x[t - l];
            }

            double prediction = 0.0;
            for (int i = 0; i < columnCount; i++)
                prediction += designRow[i] * beta[i];

            double residual = y[t] - prediction;
            rss += residual * residual;
        }

        return rss;
    }

    /// <summary>
    /// Solves the symmetric positive-definite system <c>A β = b</c> via Cholesky
    /// decomposition (<c>A = L Lᵀ</c>) followed by forward and back substitution.
    /// Roughly twice as fast as Gaussian elimination and numerically stable for the
    /// ridge-regularised normal equations <c>XᵀX + λI</c>. Returns <c>null</c> if the
    /// matrix is not positive-definite (which should not happen with the ridge in place,
    /// but is handled defensively).
    /// </summary>
    internal static double[]? SolveSpdLinearSystem(double[,] a, double[] b)
    {
        int n = b.Length;
        var l = new double[n, n];

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = a[i, j];
                for (int k = 0; k < j; k++)
                    sum -= l[i, k] * l[j, k];

                if (i == j)
                {
                    if (sum <= 0.0 || !double.IsFinite(sum))
                        return null;
                    l[i, j] = Math.Sqrt(sum);
                }
                else
                {
                    l[i, j] = sum / l[j, j];
                }
            }
        }

        // Forward substitution: L y = b.
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = b[i];
            for (int k = 0; k < i; k++)
                sum -= l[i, k] * y[k];
            y[i] = sum / l[i, i];
        }

        // Back substitution: Lᵀ β = y.
        var beta = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = y[i];
            for (int k = i + 1; k < n; k++)
                sum -= l[k, i] * beta[k];
            beta[i] = sum / l[i, i];
        }

        return beta;
    }

    private static double FSurvival(double f, double df1, double df2)
    {
        if (!double.IsFinite(f) || f <= 0.0 || df1 <= 0.0 || df2 <= 0.0)
            return 1.0;

        double x = df2 / (df2 + df1 * f);
        return RegularizedIncompleteBeta(x, df2 / 2.0, df1 / 2.0);
    }

    private static double RegularizedIncompleteBeta(double x, double a, double b)
    {
        if (x <= 0.0)
            return 0.0;
        if (x >= 1.0)
            return 1.0;

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
        double sum = 0.99999999999980993;
        for (int i = 0; i < coefficients.Length; i++)
            sum += coefficients[i] / (x + i + 1.0);

        double t = x + coefficients.Length - 0.5;
        return 0.9189385332046727 + ((x + 0.5) * Math.Log(t)) - t + Math.Log(sum);
    }

    private async Task<MLCausalFeatureWorkerSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled,
            CK_PollSecs,
            CK_WindowDays,
            CK_MinSamples,
            CK_MaxLogsPerModel,
            CK_MaxModelsPerCycle,
            CK_MaxLag,
            CK_PValueThreshold,
            CK_LockTimeoutSeconds,
            CK_FdrAlpha,
            CK_FdrProcedure,
            CK_InformationCriterion,
            CK_WinsorizePercentile,
            CK_ConsecutiveSkipAlertThreshold,
            CK_RegressionThreshold,
            CK_MinPriorCausalForRegression,
            CK_MaxAlertsPerCycle,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, config => config.Value, ct);

        int alertCooldownSeconds = await AlertCooldownDefaults.GetCooldownAsync(
            db,
            AlertCooldownDefaults.CK_MLMonitoring,
            AlertCooldownDefaults.Default_MLMonitoring,
            ct);

        return new MLCausalFeatureWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, true),
            PollInterval: TimeSpan.FromSeconds(ClampInt(
                GetInt(values, CK_PollSecs, DefaultPollSeconds),
                DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            WindowDays: ClampInt(
                GetInt(values, CK_WindowDays, DefaultWindowDays),
                DefaultWindowDays, MinWindowDays, MaxWindowDays),
            MinSamples: ClampInt(
                GetInt(values, CK_MinSamples, DefaultMinSamples),
                DefaultMinSamples, MinMinSamples, MaxMinSamples),
            MaxLogsPerModel: ClampInt(
                GetInt(values, CK_MaxLogsPerModel, DefaultMaxLogsPerModel),
                DefaultMaxLogsPerModel, MinMaxLogsPerModel, MaxMaxLogsPerModel),
            MaxModelsPerCycle: ClampInt(
                GetInt(values, CK_MaxModelsPerCycle, DefaultMaxModelsPerCycle),
                DefaultMaxModelsPerCycle, MinMaxModelsPerCycle, MaxMaxModelsPerCycle),
            MaxLag: ClampInt(
                GetInt(values, CK_MaxLag, DefaultMaxLag),
                DefaultMaxLag, MinMaxLag, MaxMaxLag),
            PValueThreshold: ClampDouble(
                GetDouble(values, CK_PValueThreshold, DefaultPValueThreshold),
                DefaultPValueThreshold, MinPValueThreshold, MaxPValueThreshold),
            LockTimeoutSeconds: ClampIntAllowingZero(
                GetInt(values, CK_LockTimeoutSeconds, DefaultLockTimeoutSeconds),
                DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            FdrAlpha: ClampDouble(
                GetDouble(values, CK_FdrAlpha, DefaultFdrAlpha),
                DefaultFdrAlpha, MinFdrAlpha, MaxFdrAlpha),
            FdrProcedure: ParseEnum(values, CK_FdrProcedure, DefaultFdrProcedure),
            InformationCriterion: ParseEnum(values, CK_InformationCriterion, DefaultInformationCriterion),
            WinsorizePercentile: ClampDoubleAllowingZero(
                GetDouble(values, CK_WinsorizePercentile, DefaultWinsorizePercentile),
                DefaultWinsorizePercentile, MinWinsorizePercentile, MaxWinsorizePercentile),
            ConsecutiveSkipAlertThreshold: ClampInt(
                GetInt(values, CK_ConsecutiveSkipAlertThreshold, DefaultConsecutiveSkipAlertThreshold),
                DefaultConsecutiveSkipAlertThreshold,
                MinConsecutiveSkipAlertThreshold,
                MaxConsecutiveSkipAlertThreshold),
            RegressionThreshold: ClampDoubleAllowingZero(
                GetDouble(values, CK_RegressionThreshold, DefaultRegressionThreshold),
                DefaultRegressionThreshold, MinRegressionThreshold, MaxRegressionThreshold),
            MinPriorCausalForRegression: ClampInt(
                GetInt(values, CK_MinPriorCausalForRegression, DefaultMinPriorCausalForRegression),
                DefaultMinPriorCausalForRegression,
                MinMinPriorCausalForRegression,
                MaxMinPriorCausalForRegression),
            MaxAlertsPerCycle: ClampIntAllowingZero(
                GetInt(values, CK_MaxAlertsPerCycle, DefaultMaxAlertsPerCycle),
                DefaultMaxAlertsPerCycle, MinMaxAlertsPerCycle, MaxMaxAlertsPerCycle),
            AlertCooldownSeconds: Math.Max(1, alertCooldownSeconds));
    }

    private static ModelSnapshot? TryDeserializeSnapshot(byte[] modelBytes)
    {
        try
        {
            return JsonSerializer.Deserialize<ModelSnapshot>(modelBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] ResolveFeatureNames(ModelSnapshot snapshot, int featureCount)
    {
        if (snapshot.Features.Length >= featureCount)
            return snapshot.Features.Take(featureCount).ToArray();

        return MLFeatureHelper.ResolveFeatureNames(featureCount);
    }

    private static string SkipStreakKey(long modelId)
        => $"MLCausal:Model:{modelId.ToString(CultureInfo.InvariantCulture)}:ConsecutiveSkips";

    private static string CausalRatioKey(long modelId)
        => $"MLCausal:Model:{modelId.ToString(CultureInfo.InvariantCulture)}:CausalRatio";

    private static string CausalCountKey(long modelId)
        => $"MLCausal:Model:{modelId.ToString(CultureInfo.InvariantCulture)}:CausalCount";

    private static bool TryParseModelIdFromConfigKey(string key, string suffix, out long modelId)
    {
        modelId = 0;
        const string prefix = "MLCausal:Model:";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)
            || !key.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        int idStart = prefix.Length;
        int idEnd = key.Length - suffix.Length;
        if (idEnd <= idStart)
            return false;
        return long.TryParse(
            key.AsSpan(idStart, idEnd - idStart),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out modelId);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static DateTime NormalizeUtc(DateTime timestamp)
        => timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);

    private static bool IsLikelyAlertDeduplicationRace(IServiceProvider serviceProvider, DbUpdateException ex)
    {
        var classifier = serviceProvider.GetService<IDatabaseExceptionClassifier>();
        if (classifier?.IsUniqueConstraintViolation(ex) == true)
            return true;

        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("DeduplicationKey", StringComparison.OrdinalIgnoreCase) &&
               (message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    private static void DetachIfAdded(DbContext db, Alert alert)
    {
        var entry = db.Entry(alert);
        if (entry.State is EntityState.Added or EntityState.Modified)
            entry.State = EntityState.Detached;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (bool.TryParse(raw, out var parsedBool))
            return parsedBool;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            return parsedInt != 0;

        return defaultValue;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
    {
        return values.TryGetValue(key, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double defaultValue)
    {
        return values.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static T ParseEnum<T>(IReadOnlyDictionary<string, string> values, string key, T defaultValue) where T : struct, Enum
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        return Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) ? parsed : defaultValue;
    }

    private static int ClampInt(int value, int fallback, int min, int max)
    {
        if (value <= 0)
            return fallback;

        return Math.Min(Math.Max(value, min), max);
    }

    private static int ClampIntAllowingZero(int value, int fallback, int min, int max)
    {
        if (value < 0)
            return fallback;

        return Math.Min(Math.Max(value, min), max);
    }

    private static double ClampDouble(double value, double fallback, double min, double max)
    {
        if (!double.IsFinite(value) || value <= 0.0)
            return fallback;

        return Math.Min(Math.Max(value, min), max);
    }

    private static double ClampDoubleAllowingZero(double value, double fallback, double min, double max)
    {
        if (!double.IsFinite(value) || value < 0.0)
            return fallback;

        return Math.Min(Math.Max(value, min), max);
    }
}

internal enum FdrProcedure
{
    /// <summary>
    /// Benjamini-Hochberg step-up. Controls the false-discovery rate at α under
    /// independence or positive regression dependence (PRDS). Standard practical default.
    /// </summary>
    BenjaminiHochberg = 0,

    /// <summary>
    /// Benjamini-Yekutieli step-up. Controls the false-discovery rate at α under
    /// arbitrary dependence between test statistics, at the cost of being roughly
    /// <c>Σ(1/i)</c>-times more conservative than BH (~5× for 73 features).
    /// </summary>
    BenjaminiYekutieli = 1,
}

internal enum InformationCriterion
{
    /// <summary>
    /// Akaike Information Criterion: <c>n·log(rss/n) + 2·k</c>. Light penalty;
    /// assumes Gaussian residuals. Good with smaller samples.
    /// </summary>
    Aic = 0,

    /// <summary>
    /// Bayesian Information Criterion: <c>n·log(rss/n) + k·log(n)</c>. Stronger
    /// complexity penalty; consistent (picks the true model in the limit) and tends
    /// to pick smaller lags than AIC, useful when residuals are heavy-tailed.
    /// </summary>
    Bic = 1,

    /// <summary>
    /// Hannan-Quinn IC: <c>n·log(rss/n) + 2·k·log(log(n))</c>. Penalty between AIC
    /// and BIC. Often preferred for time-series lag selection on financial data.
    /// </summary>
    Hqic = 2,
}

internal sealed record MLCausalFeatureWorkerSettings(
    bool Enabled,
    TimeSpan PollInterval,
    int WindowDays,
    int MinSamples,
    int MaxLogsPerModel,
    int MaxModelsPerCycle,
    int MaxLag,
    double PValueThreshold,
    int LockTimeoutSeconds,
    double FdrAlpha,
    FdrProcedure FdrProcedure,
    InformationCriterion InformationCriterion,
    double WinsorizePercentile,
    int ConsecutiveSkipAlertThreshold,
    double RegressionThreshold,
    int MinPriorCausalForRegression,
    int MaxAlertsPerCycle,
    int AlertCooldownSeconds);

internal sealed record MLCausalFeatureCycleResult(
    MLCausalFeatureWorkerSettings Settings,
    string? SkippedReason,
    int CandidateModelCount,
    int EvaluatedModelCount,
    int SkippedModelCount,
    int FailedModelCount,
    int AuditsWrittenCount,
    int CausalFeatureCount,
    int PreservedMaskCount,
    int RegressionAlertCount,
    int StaleMonitoringAlertCount,
    int AlertBackpressureSkippedCount)
{
    public static MLCausalFeatureCycleResult Skipped(MLCausalFeatureWorkerSettings settings, string reason)
        => new(
            settings,
            reason,
            CandidateModelCount: 0,
            EvaluatedModelCount: 0,
            SkippedModelCount: 0,
            FailedModelCount: 0,
            AuditsWrittenCount: 0,
            CausalFeatureCount: 0,
            PreservedMaskCount: 0,
            RegressionAlertCount: 0,
            StaleMonitoringAlertCount: 0,
            AlertBackpressureSkippedCount: 0);
}
