using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Adapts deployed ML decision thresholds from recent resolved live outcomes with walk-forward
/// regression guards, Wilson lower-bound floors, time-decayed weighting, PSI stationarity gating,
/// per-decision audit logging, P&amp;L-aware EV when broker outcomes are available, and
/// anomalous-drift alerts.
/// </summary>
public sealed class MLAdaptiveThresholdWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLAdaptiveThresholdWorker);

    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";
    private const string CycleLockKey = "workers:ml-adaptive-threshold:cycle";
    private const string ModelLockKeyPrefix = "workers:ml-adaptive-threshold:model:";

    private const string CK_Enabled = "MLAdaptiveThreshold:Enabled";
    private const string CK_PollSecs = "MLAdaptiveThreshold:PollIntervalSeconds";
    private const string CK_WindowSize = "MLAdaptiveThreshold:WindowSize";
    private const string CK_MinPredictions = "MLAdaptiveThreshold:MinResolvedPredictions";
    private const string CK_EmaAlpha = "MLAdaptiveThreshold:EmaAlpha";
    private const string CK_MinDrift = "MLAdaptiveThreshold:MinThresholdDrift";
    private const string CK_LookbackDays = "MLAdaptiveThreshold:LookbackDays";
    private const string CK_MinRegimePredictions = "MLAdaptiveThreshold:MinRegimeResolvedPredictions";
    private const string CK_MaxModelsPerCycle = "MLAdaptiveThreshold:MaxModelsPerCycle";
    private const string CK_LockTimeoutSecs = "MLAdaptiveThreshold:LockTimeoutSeconds";
    private const string CK_ModelLockTimeoutSecs = "MLAdaptiveThreshold:ModelLockTimeoutSeconds";
    private const string CK_HoldoutFraction = "MLAdaptiveThreshold:HoldoutFraction";
    private const string CK_MinHoldoutSamples = "MLAdaptiveThreshold:MinHoldoutSamples";
    private const string CK_TimeDecayHalfLifeDays = "MLAdaptiveThreshold:TimeDecayHalfLifeDays";
    private const string CK_MinSamplesForTimeDecay = "MLAdaptiveThreshold:MinSamplesForTimeDecay";
    private const string CK_StationarityPsiThreshold = "MLAdaptiveThreshold:StationarityPsiThreshold";
    private const string CK_MinStationaritySamples = "MLAdaptiveThreshold:MinStationaritySamples";
    private const string CK_PsiHardCapMultiplier = "MLAdaptiveThreshold:PsiHardCapMultiplier";
    private const string CK_WilsonLowerBoundFloor = "MLAdaptiveThreshold:WilsonLowerBoundFloor";
    private const string CK_RegressionGuardK = "MLAdaptiveThreshold:RegressionGuardK";
    private const string CK_AnomalousDriftAlertThreshold = "MLAdaptiveThreshold:AnomalousDriftAlertThreshold";

    private const int DefaultPollSeconds = 3600;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 7 * 24 * 60 * 60;

    private const int DefaultWindowSize = 500;
    private const int MinWindowSize = 2;
    private const int MaxWindowSize = 5000;

    private const int DefaultMinPredictions = 100;
    private const int MinMinPredictions = 2;
    private const int MaxMinPredictions = 5000;

    private const double DefaultEmaAlpha = 0.2;
    private const double MinEmaAlpha = 0.01;
    private const double MaxEmaAlpha = 1.0;

    private const double DefaultMinDrift = 0.01;
    private const double MinMinDrift = 0.0001;
    private const double MaxMinDrift = 0.50;

    private const int DefaultLookbackDays = 30;
    private const int MinLookbackDays = 1;
    private const int MaxLookbackDays = 365;

    private const int DefaultMinRegimePredictions = 20;
    private const int MinMinRegimePredictions = 2;
    private const int MaxMinRegimePredictions = 1000;

    private const int DefaultMaxModelsPerCycle = 256;
    private const int MinMaxModelsPerCycle = 1;
    private const int MaxMaxModelsPerCycle = 4096;

    private const int DefaultLockTimeoutSeconds = 5;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private const int DefaultModelLockTimeoutSeconds = 30;
    private const int MinModelLockTimeoutSeconds = 1;
    private const int MaxModelLockTimeoutSeconds = 600;

    private const double DefaultHoldoutFraction = 0.30;
    private const double MinHoldoutFraction = 0.05;
    private const double MaxHoldoutFraction = 0.50;

    private const int DefaultMinHoldoutSamples = 30;
    private const int MinMinHoldoutSamples = 0;
    private const int MaxMinHoldoutSamples = 500;

    // Default to a real half-life: time decay is auto-disabled below MinSamplesForTimeDecay,
    // so production with sufficient data benefits, while small-sample tests stay flat.
    private const double DefaultTimeDecayHalfLifeDays = 60.0;
    private const double MinTimeDecayHalfLifeDays = 0.0;
    private const double MaxTimeDecayHalfLifeDays = 365.0;

    private const int DefaultMinSamplesForTimeDecay = 200;
    private const int MinMinSamplesForTimeDecay = 0;
    private const int MaxMinSamplesForTimeDecay = 5000;

    private const double DefaultStationarityPsiThreshold = 0.25;
    private const double MinStationarityPsiThreshold = 0.05;
    private const double MaxStationarityPsiThreshold = 1.0;

    private const int DefaultMinStationaritySamples = 40;
    private const int MinMinStationaritySamples = 0;
    private const int MaxMinStationaritySamples = 5000;

    private const double DefaultPsiHardCapMultiplier = 2.0;
    private const double MinPsiHardCapMultiplier = 1.0;
    private const double MaxPsiHardCapMultiplier = 10.0;

    private const double DefaultWilsonLowerBoundFloor = 0.45;
    private const double MinWilsonLowerBoundFloor = 0.0;
    private const double MaxWilsonLowerBoundFloor = 1.0;

    // One-sigma paired-EV improvement bar by default — a meaningful but lenient guard. To
    // get a true Bonferroni correction across the 41-step sweep at α=0.05, set this to ~3.0.
    private const double DefaultRegressionGuardK = 1.0;
    private const double MinRegressionGuardK = 0.0;
    private const double MaxRegressionGuardK = 5.0;

    private const double DefaultAnomalousDriftAlertThreshold = 0.05;
    private const double MinAnomalousDriftAlertThreshold = 0.001;
    private const double MaxAnomalousDriftAlertThreshold = 0.5;

    private const double DataStarvationRatio = 0.9;
    private const int DataStarvationMinModels = 5;

    private const double SearchMinThreshold = 0.30;
    private const double SearchMaxThreshold = 0.70;
    private const int SearchStepBasisPoints = 1;
    private const double DefaultDecisionThreshold = 0.50;
    private const double MinProbabilityThreshold = 0.01;
    private const double MaxProbabilityThreshold = 0.99;
    private const double ThresholdTieTolerance = 1e-9;

    private const int MaxAuditDiagnosticsLength = 4_000;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MLAdaptiveThresholdWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IAlertDispatcher? _alertDispatcher;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    private readonly record struct ActiveModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe);

    private readonly record struct ModelAdaptationOutcome(
        bool Updated,
        int PrunedRegimeThresholds,
        bool DataStarved,
        string? SkipReason);

    private readonly record struct RegimeSlice(
        DateTime DetectedAt,
        MarketRegimeEnum Regime);

    private readonly record struct EvResult(
        double Ev,
        int Wins,
        int Total,
        double MeanPnlPips);

    private readonly record struct PairedEvComparison(
        double EvAtPrev,
        double EvAtNew,
        double PairedStderr,
        int Wins,
        int Total,
        double MeanPnlPips);

    public MLAdaptiveThresholdWorker(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<MLAdaptiveThresholdWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IAlertDispatcher? alertDispatcher = null)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _alertDispatcher = alertDispatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Adapts live ML decision thresholds with walk-forward regression guards, Wilson floors, time-decayed weighting, PSI stationarity gating, P&L-aware EV, persistent stale-data short-circuit, per-decision audit logging, and anomalous-drift alerts.",
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
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.ModelsProcessed);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.MLAdaptiveThresholdCycleDurationMs.Record(durationMs);

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
                            "{Worker}: processed={Processed}, updated={Updated}, skipped={Skipped}, failed={Failed}, prunedRegimes={Pruned}, starved={Starved}.",
                            WorkerName,
                            result.ModelsProcessed,
                            result.ModelsUpdated,
                            result.ModelsSkipped,
                            result.ModelsFailed,
                            result.RegimeThresholdsPruned,
                            result.ModelsStarved);
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
                        new KeyValuePair<string, object?>("reason", "ml_adaptive_threshold_cycle"));
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

    internal async Task<AdaptiveThresholdCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (!settings.Enabled)
        {
            _metrics?.MLAdaptiveThresholdCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "disabled"));
            return AdaptiveThresholdCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.MLAdaptiveThresholdLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate threshold adaptation cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(
                CycleLockKey,
                TimeSpan.FromSeconds(settings.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLAdaptiveThresholdLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.MLAdaptiveThresholdCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return AdaptiveThresholdCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLAdaptiveThresholdLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));

            await using (cycleLock)
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
                try
                {
                    return await RunCycleCoreAsync(writeContext, db, settings, ct);
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
            return await RunCycleCoreAsync(writeContext, db, settings, ct);
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

    private async Task<AdaptiveThresholdCycleResult> RunCycleCoreAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        AdaptiveThresholdWorkerSettings settings,
        CancellationToken ct)
    {
        var models = await db.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                     && !m.IsDeleted
                     && !m.IsMetaLearner
                     && !m.IsMamlInitializer
                     && m.ModelBytes != null)
            .OrderBy(m => m.Symbol)
            .ThenBy(m => m.Timeframe)
            .ThenByDescending(m => m.ActivatedAt ?? m.TrainedAt)
            .Take(settings.MaxModelsPerCycle)
            .Select(m => new ActiveModelCandidate(m.Id, m.Symbol, m.Timeframe))
            .ToListAsync(ct);

        _healthMonitor?.RecordBacklogDepth(WorkerName, models.Count);
        _metrics?.MLAdaptiveThresholdModelsEvaluated.Add(models.Count);

        // Persistent stale-data short-circuit map: per-model latest NewestOutcomeAt across all
        // audit rows. Loaded once per cycle so it survives process restarts and is shared via
        // the audit table across replicas.
        var modelIds = models.Select(m => m.Id).ToList();
        var lastNewestOutcome = await LoadLastNewestOutcomeMapAsync(db, modelIds, ct);

        int updated = 0;
        int skipped = 0;
        int failed = 0;
        int starved = 0;
        int prunedRegimeThresholds = 0;

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                lastNewestOutcome.TryGetValue(model.Id, out var lastSeen);
                var outcome = await AdaptModelWithLockAsync(writeContext, db, model, settings, lastSeen, ct);
                prunedRegimeThresholds += outcome.PrunedRegimeThresholds;

                if (outcome.Updated)
                {
                    updated++;
                    _metrics?.MLAdaptiveThresholdModelsUpdated.Add(1);
                    if (outcome.PrunedRegimeThresholds > 0)
                    {
                        _metrics?.MLAdaptiveThresholdRegimeThresholdsPruned.Add(outcome.PrunedRegimeThresholds);
                    }
                }
                else
                {
                    skipped++;
                    if (outcome.DataStarved) starved++;
                    _metrics?.MLAdaptiveThresholdModelsSkipped.Add(
                        1,
                        new KeyValuePair<string, object?>("reason", outcome.SkipReason ?? "no_change"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed adapting model {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    WorkerName,
                    model.Id,
                    model.Symbol,
                    model.Timeframe);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        if (models.Count >= DataStarvationMinModels && starved >= models.Count * DataStarvationRatio)
            await RaiseDataStarvationAlertAsync(models.Count, starved, ct);

        return new AdaptiveThresholdCycleResult(
            settings,
            SkippedReason: null,
            ModelsProcessed: models.Count,
            ModelsUpdated: updated,
            ModelsSkipped: skipped,
            ModelsFailed: failed,
            ModelsStarved: starved,
            RegimeThresholdsPruned: prunedRegimeThresholds);
    }

    private static async Task<Dictionary<long, DateTime?>> LoadLastNewestOutcomeMapAsync(
        DbContext db, List<long> modelIds, CancellationToken ct)
    {
        if (modelIds.Count == 0) return [];

        // GroupBy + Max gives us per-model latest NewestOutcomeAt across all prior cycles.
        var rows = await db.Set<MLAdaptiveThresholdLog>()
            .AsNoTracking()
            .Where(l => modelIds.Contains(l.MLModelId) && !l.IsDeleted && l.NewestOutcomeAt != null)
            .GroupBy(l => l.MLModelId)
            .Select(g => new { ModelId = g.Key, MaxAt = g.Max(l => l.NewestOutcomeAt) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ModelId, r => r.MaxAt);
    }

    private async Task<ModelAdaptationOutcome> AdaptModelWithLockAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        AdaptiveThresholdWorkerSettings settings,
        DateTime? lastSeenOutcomeAt,
        CancellationToken ct)
    {
        IAsyncDisposable? modelLock = null;
        if (_distributedLock is not null)
        {
            // Per-model lock uses its own configured TTL — the global cycle lock guarantees
            // mutual exclusion across replicas; this finer lock prevents any in-process
            // re-entry for the same model and gives the operator a separate ceiling on how
            // long any single model evaluation may hold the per-model slot.
            modelLock = await _distributedLock.TryAcquireAsync(
                ModelLockKeyPrefix + model.Id.ToString(CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(settings.ModelLockTimeoutSeconds),
                ct);

            if (modelLock is null)
            {
                return new ModelAdaptationOutcome(
                    Updated: false,
                    PrunedRegimeThresholds: 0,
                    DataStarved: false,
                    SkipReason: "model_lock_busy");
            }
        }

        try
        {
            return await AdaptModelAsync(writeContext, db, model, settings, lastSeenOutcomeAt, ct);
        }
        finally
        {
            if (modelLock is not null)
                await modelLock.DisposeAsync();
        }
    }

    private async Task<ModelAdaptationOutcome> AdaptModelAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        AdaptiveThresholdWorkerSettings settings,
        DateTime? lastSeenOutcomeAt,
        CancellationToken ct)
    {
        // All audit rows for this evaluation accumulate locally and flush in a dedicated scope.
        // The try/finally ensures audits flush even if the snapshot save throws something other
        // than DbUpdateConcurrencyException — operators always see the decision trail.
        var pendingAudits = new List<MLAdaptiveThresholdLog>(8);

        try
        {
            return await AdaptModelCoreAsync(writeContext, db, model, settings, lastSeenOutcomeAt, pendingAudits, ct);
        }
        finally
        {
            if (pendingAudits.Count > 0)
                await FlushAuditsAsync(pendingAudits, ct);
        }
    }

    private async Task<ModelAdaptationOutcome> AdaptModelCoreAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        AdaptiveThresholdWorkerSettings settings,
        DateTime? lastSeenOutcomeAt,
        List<MLAdaptiveThresholdLog> pendingAudits,
        CancellationToken ct)
    {
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime lookbackCutoff = nowUtc.AddDays(-settings.LookbackDays);

        var resolvedLogs = await db.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId == model.Id
                     && !l.IsDeleted
                     && l.ActualDirection.HasValue
                     && l.DirectionCorrect.HasValue
                     && l.OutcomeRecordedAt != null
                     && l.OutcomeRecordedAt >= lookbackCutoff)
            .OrderByDescending(l => l.OutcomeRecordedAt)
            .ThenByDescending(l => l.Id)
            .Take(settings.WindowSize)
            .ToListAsync(ct);

        var informativeLogs = resolvedLogs
            .Where(IsThresholdInformative)
            .ToList();

        if (informativeLogs.Count < settings.MinResolvedPredictions)
        {
            EnqueueAudit(pendingAudits, model, regime: null,
                outcome: AuditOutcome.SkippedData,
                reason: "insufficient_informative_logs",
                sweepSize: informativeLogs.Count,
                holdoutSize: 0,
                previousThreshold: 0,
                optimal: 0,
                newThreshold: 0,
                drift: 0,
                holdoutEvNew: 0,
                holdoutEvPrev: 0,
                meanPnlPips: 0,
                psi: 0,
                newestOutcomeAt: null,
                diagnostics: BuildDiagnostics(("availableLogs", informativeLogs.Count), ("required", settings.MinResolvedPredictions)),
                evaluatedAt: nowUtc);
            await FlushAuditsAsync(pendingAudits, ct);

            return new ModelAdaptationOutcome(
                Updated: false,
                PrunedRegimeThresholds: 0,
                DataStarved: true,
                SkipReason: "insufficient_informative_logs");
        }

        DateTime newestOutcomeAt = informativeLogs[0].OutcomeRecordedAt ?? informativeLogs[0].PredictedAt;

        // Persistent stale-data short-circuit: if the newest log we'd evaluate is older than
        // the newest log evaluated by any prior cycle (read from the audit log), the sweep
        // would yield identical results. Bypass the entire evaluation, including audit writes.
        if (lastSeenOutcomeAt.HasValue && newestOutcomeAt <= lastSeenOutcomeAt.Value)
        {
            return new ModelAdaptationOutcome(
                Updated: false,
                PrunedRegimeThresholds: 0,
                DataStarved: false,
                SkipReason: "no_new_outcomes");
        }

        var (writeModel, snapshot) = await MLModelSnapshotWriteHelper.LoadTrackedLatestSnapshotAsync(db, model.Id, ct);
        if (writeModel is null || snapshot is null)
        {
            return new ModelAdaptationOutcome(
                Updated: false,
                PrunedRegimeThresholds: 0,
                DataStarved: false,
                SkipReason: "snapshot_unavailable");
        }

        // Effective half-life: time decay is forced off below MinSamplesForTimeDecay so the
        // tilt cannot dominate floating-point noise on small samples. Operators can drop
        // MinSamplesForTimeDecay to 0 to opt fully into the configured half-life.
        double effectiveHalfLife = informativeLogs.Count >= settings.MinSamplesForTimeDecay
            ? settings.TimeDecayHalfLifeDays
            : 0.0;

        // P&L map: when broker positions are linked through the prediction-log signal, use
        // realized $ P&L instead of the |actualMagnitudePips| heuristic. Falls back per-log.
        var pnlMap = await LoadSignalPnlMapAsync(db, informativeLogs, ct);

        double psi = ComputeStationarityPsi(informativeLogs, settings.MinStationaritySamples);

        double psiHardCap = settings.StationarityPsiThreshold * settings.PsiHardCapMultiplier;
        if (psi > psiHardCap)
        {
            EnqueueAudit(pendingAudits, model, regime: null,
                outcome: AuditOutcome.SkippedStationarity,
                reason: "psi_above_hard_cap",
                sweepSize: informativeLogs.Count,
                holdoutSize: 0,
                previousThreshold: 0,
                optimal: 0,
                newThreshold: 0,
                drift: 0,
                holdoutEvNew: 0,
                holdoutEvPrev: 0,
                meanPnlPips: 0,
                psi: psi,
                newestOutcomeAt: newestOutcomeAt,
                diagnostics: BuildDiagnostics(("psi", psi), ("hardCap", psiHardCap)),
                evaluatedAt: nowUtc);
            await FlushAuditsAsync(pendingAudits, ct);

            return new ModelAdaptationOutcome(
                Updated: false,
                PrunedRegimeThresholds: 0,
                DataStarved: false,
                SkipReason: "non_stationary");
        }

        // PSI soft mode: dampen alpha when PSI is between the threshold and the hard cap.
        // Keeps adaptation moving but more conservatively when distribution shifts loom.
        double psiAlphaScale = ComputePsiAlphaScale(psi, settings.StationarityPsiThreshold, psiHardCap);
        double effectiveAlpha = settings.EmaAlpha * psiAlphaScale;

        bool snapshotChanged = NormalizeSnapshotThresholdState(snapshot, out int invalidThresholdsRemoved);
        int prunedRegimeThresholds = invalidThresholdsRemoved;

        double currentGlobalThreshold = SanitizeThreshold(
            MLFeatureHelper.ResolveEffectiveDecisionThreshold(snapshot),
            DefaultDecisionThreshold);

        var chronological = informativeLogs.OrderBy(l => l.PredictedAt).ThenBy(l => l.Id).ToList();
        var (sweepSlice, holdoutSlice) = SplitWalkForward(chronological, settings.HoldoutFraction, settings.MinHoldoutSamples);
        bool hasRealHoldout = holdoutSlice.Count > 0;

        var (bestThreshold, bestEv) = FindBestThreshold(
            sweepSlice, currentGlobalThreshold, nowUtc, effectiveHalfLife, pnlMap);

        double newGlobalThreshold = BlendThreshold(bestThreshold, currentGlobalThreshold, effectiveAlpha);
        double globalDrift = Math.Abs(newGlobalThreshold - currentGlobalThreshold);

        var holdoutForCheck = hasRealHoldout ? holdoutSlice : sweepSlice;
        var paired = ComputeHoldoutEvComparison(
            holdoutForCheck, currentGlobalThreshold, newGlobalThreshold, currentGlobalThreshold,
            nowUtc, effectiveHalfLife, pnlMap);

        double wilsonLb = WilsonLowerBound(paired.Wins, paired.Total);
        bool driftMeaningful = globalDrift >= settings.MinThresholdDrift;
        // When the holdout is real (has its own slice), apply the K-sigma paired-EV regression
        // guard. Set RegressionGuardK ~= 3.0 to approximate Bonferroni-α=0.05 over 41 candidates.
        // Sweep-as-holdout fallback is tautological — we degrade to a plain >=.
        bool holdoutPassed = hasRealHoldout
            ? paired.EvAtNew > paired.EvAtPrev + settings.RegressionGuardK * paired.PairedStderr
            : paired.EvAtNew >= paired.EvAtPrev - 1e-9;
        bool wilsonPassed = paired.Total < settings.MinHoldoutSamples
                          || wilsonLb >= settings.WilsonLowerBoundFloor;
        bool globalAccepted = driftMeaningful && holdoutPassed && wilsonPassed;

        string globalReason = !driftMeaningful
            ? (hasRealHoldout ? "drift_below_floor" : "drift_below_floor_no_holdout")
            : !holdoutPassed
                ? (hasRealHoldout ? "holdout_regression" : "no_holdout_regression")
                : !wilsonPassed
                    ? "wilson_below_floor"
                    : (hasRealHoldout ? "accepted" : "accepted_no_holdout");

        double pendingGlobal = currentGlobalThreshold;
        var pendingRegime = new Dictionary<string, double>(StringComparer.Ordinal);

        if (globalAccepted)
        {
            pendingGlobal = newGlobalThreshold;
            snapshotChanged = true;
        }

        EnqueueAudit(pendingAudits, model, regime: null,
            outcome: globalAccepted ? AuditOutcome.Updated : AuditOutcome.SkippedDrift,
            reason: globalReason,
            sweepSize: sweepSlice.Count,
            holdoutSize: holdoutSlice.Count,
            previousThreshold: currentGlobalThreshold,
            optimal: bestThreshold,
            newThreshold: globalAccepted ? newGlobalThreshold : currentGlobalThreshold,
            drift: globalDrift,
            holdoutEvNew: paired.EvAtNew,
            holdoutEvPrev: paired.EvAtPrev,
            meanPnlPips: paired.MeanPnlPips,
            psi: psi,
            newestOutcomeAt: newestOutcomeAt,
            diagnostics: BuildDiagnostics(
                ("sweepBestEv", (object)bestEv),
                ("wilsonLb", (object)wilsonLb),
                ("alpha", (object)effectiveAlpha),
                ("psiAlphaScale", (object)psiAlphaScale),
                ("decayHalfLifeDays", (object)effectiveHalfLife),
                ("informativeLogs", (object)informativeLogs.Count),
                ("hasRealHoldout", (object)hasRealHoldout),
                ("pairedStderr", (object)paired.PairedStderr),
                ("regressionGuardK", (object)settings.RegressionGuardK),
                ("pnlMapSize", (object)pnlMap.Count)),
            evaluatedAt: nowUtc);

        if (globalAccepted && globalDrift >= settings.AnomalousDriftAlertThreshold)
        {
            await RaiseAnomalousDriftAlertAsync(model, currentGlobalThreshold, newGlobalThreshold, globalDrift, ct);
        }

        // Phase 2 — regime-conditioned thresholds.
        if (chronological.Count > 0)
        {
            var earliestPredictedAt = chronological[0].PredictedAt;
            var latestPredictedAt = chronological[^1].PredictedAt;

            var regimeTimeline = await db.Set<MarketRegimeSnapshot>()
                .AsNoTracking()
                .Where(r => r.Symbol == model.Symbol
                         && r.Timeframe == model.Timeframe
                         && !r.IsDeleted
                         && r.DetectedAt >= earliestPredictedAt.AddDays(-1)
                         && r.DetectedAt <= latestPredictedAt)
                .OrderBy(r => r.DetectedAt)
                .Select(r => new RegimeSlice(r.DetectedAt, r.Regime))
                .ToListAsync(ct);

            if (regimeTimeline.Count > 0)
            {
                var matchedRegimes = new HashSet<string>(StringComparer.Ordinal);
                var regimeGroups = new Dictionary<string, List<MLModelPredictionLog>>(StringComparer.Ordinal);

                foreach (var log in chronological)
                {
                    var regime = FindRegimeAt(regimeTimeline, log.PredictedAt);
                    if (regime is null) continue;

                    string regimeName = regime.Value.ToString();
                    matchedRegimes.Add(regimeName);

                    if (!regimeGroups.TryGetValue(regimeName, out var group))
                    {
                        group = [];
                        regimeGroups[regimeName] = group;
                    }
                    group.Add(log);
                }

                int stalePruned = PruneMissingRegimeThresholds(snapshot.RegimeThresholds ??= [], matchedRegimes);
                if (stalePruned > 0)
                {
                    snapshotChanged = true;
                    prunedRegimeThresholds += stalePruned;
                }

                double globalAfter = pendingGlobal != currentGlobalThreshold ? pendingGlobal : currentGlobalThreshold;

                foreach (var (regimeName, regimeLogs) in regimeGroups)
                {
                    if (regimeLogs.Count < settings.MinRegimeResolvedPredictions) continue;

                    double currentRegimeThreshold = snapshot.RegimeThresholds.TryGetValue(regimeName, out var existing)
                        ? SanitizeThreshold(existing, globalAfter)
                        : globalAfter;

                    var (rSweep, rHoldout) = SplitWalkForward(regimeLogs, settings.HoldoutFraction,
                        Math.Max(10, settings.MinHoldoutSamples / 3));
                    bool rHasRealHoldout = rHoldout.Count > 0;

                    double rEffectiveHalfLife = regimeLogs.Count >= settings.MinSamplesForTimeDecay
                        ? settings.TimeDecayHalfLifeDays
                        : 0.0;

                    var (rBest, rBestEv) = FindBestThreshold(
                        rSweep, currentRegimeThreshold, nowUtc, rEffectiveHalfLife, pnlMap);
                    double newRegimeThreshold = BlendThreshold(rBest, currentRegimeThreshold, effectiveAlpha);
                    double rDrift = Math.Abs(newRegimeThreshold - currentRegimeThreshold);

                    var rHoldoutForCheck = rHasRealHoldout ? rHoldout : rSweep;
                    var rPaired = ComputeHoldoutEvComparison(
                        rHoldoutForCheck, currentRegimeThreshold, newRegimeThreshold, currentRegimeThreshold,
                        nowUtc, rEffectiveHalfLife, pnlMap);
                    double rWilson = WilsonLowerBound(rPaired.Wins, rPaired.Total);

                    int rMinHoldout = Math.Max(10, settings.MinHoldoutSamples / 3);
                    bool rDriftMeaningful = rDrift >= settings.MinThresholdDrift;
                    bool rHoldoutPassed = rHasRealHoldout
                        ? rPaired.EvAtNew > rPaired.EvAtPrev + settings.RegressionGuardK * rPaired.PairedStderr
                        : rPaired.EvAtNew >= rPaired.EvAtPrev - 1e-9;
                    bool rWilsonPassed = rPaired.Total < rMinHoldout
                                      || rWilson >= settings.WilsonLowerBoundFloor;
                    bool rAccepted = rDriftMeaningful && rHoldoutPassed && rWilsonPassed;

                    string rReason = !rDriftMeaningful
                        ? (rHasRealHoldout ? "drift_below_floor" : "drift_below_floor_no_holdout")
                        : !rHoldoutPassed
                            ? (rHasRealHoldout ? "holdout_regression" : "no_holdout_regression")
                            : !rWilsonPassed
                                ? "wilson_below_floor"
                                : (rHasRealHoldout ? "accepted" : "accepted_no_holdout");

                    if (rAccepted)
                    {
                        pendingRegime[regimeName] = newRegimeThreshold;
                        snapshotChanged = true;
                    }

                    if (Enum.TryParse<MarketRegimeEnum>(regimeName, ignoreCase: true, out var regimeEnum))
                    {
                        EnqueueAudit(pendingAudits, model, regime: regimeEnum,
                            outcome: rAccepted ? AuditOutcome.Updated : AuditOutcome.SkippedDrift,
                            reason: rReason,
                            sweepSize: rSweep.Count,
                            holdoutSize: rHoldout.Count,
                            previousThreshold: currentRegimeThreshold,
                            optimal: rBest,
                            newThreshold: rAccepted ? newRegimeThreshold : currentRegimeThreshold,
                            drift: rDrift,
                            holdoutEvNew: rPaired.EvAtNew,
                            holdoutEvPrev: rPaired.EvAtPrev,
                            meanPnlPips: rPaired.MeanPnlPips,
                            psi: psi,
                            newestOutcomeAt: newestOutcomeAt,
                            diagnostics: BuildDiagnostics(
                                ("regime", regimeName),
                                ("regimeSamples", regimeLogs.Count),
                                ("wilsonLb", rWilson),
                                ("regimeBestEv", rBestEv),
                                ("hasRealHoldout", rHasRealHoldout),
                                ("pairedStderr", rPaired.PairedStderr)),
                            evaluatedAt: nowUtc);
                    }
                }
            }
        }

        if (!snapshotChanged)
        {
            await FlushAuditsAsync(pendingAudits, ct);
            return new ModelAdaptationOutcome(
                Updated: false,
                PrunedRegimeThresholds: 0,
                DataStarved: false,
                SkipReason: "no_change");
        }

        if (pendingGlobal != currentGlobalThreshold)
        {
            snapshot.AdaptiveThreshold = pendingGlobal;
            _metrics?.MLAdaptiveThresholdAppliedDrift.Record(
                globalDrift,
                new KeyValuePair<string, object?>("scope", "global"));
        }

        foreach (var kvp in pendingRegime)
        {
            snapshot.RegimeThresholds ??= [];
            double regimeDrift = snapshot.RegimeThresholds.TryGetValue(kvp.Key, out var prev)
                ? Math.Abs(kvp.Value - prev) : Math.Abs(kvp.Value - currentGlobalThreshold);
            snapshot.RegimeThresholds[kvp.Key] = kvp.Value;
            _metrics?.MLAdaptiveThresholdAppliedDrift.Record(
                regimeDrift,
                new KeyValuePair<string, object?>("scope", "regime"));
        }

        _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");
        writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);

        try
        {
            await writeContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            EnqueueAudit(pendingAudits, model, regime: null,
                outcome: AuditOutcome.SkippedConcurrency,
                reason: "row_version_conflict",
                sweepSize: sweepSlice.Count,
                holdoutSize: holdoutSlice.Count,
                previousThreshold: currentGlobalThreshold,
                optimal: bestThreshold,
                newThreshold: pendingGlobal,
                drift: globalDrift,
                holdoutEvNew: paired.EvAtNew,
                holdoutEvPrev: paired.EvAtPrev,
                meanPnlPips: paired.MeanPnlPips,
                psi: psi,
                newestOutcomeAt: newestOutcomeAt,
                diagnostics: BuildDiagnostics(("conflict", "row_version")),
                evaluatedAt: nowUtc);
            await FlushAuditsAsync(pendingAudits, ct);

            return new ModelAdaptationOutcome(
                Updated: false,
                PrunedRegimeThresholds: prunedRegimeThresholds,
                DataStarved: false,
                SkipReason: "row_version_conflict");
        }

        _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");
        await FlushAuditsAsync(pendingAudits, ct);

        return new ModelAdaptationOutcome(
            Updated: true,
            PrunedRegimeThresholds: prunedRegimeThresholds,
            DataStarved: false,
            SkipReason: null);
    }

    private async Task FlushAuditsAsync(List<MLAdaptiveThresholdLog> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
            await writeCtx.Set<MLAdaptiveThresholdLog>().AddRangeAsync(pending, ct);
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Worker}: failed to persist {Count} audit row(s); rows discarded.",
                WorkerName, pending.Count);
        }
        finally
        {
            pending.Clear();
        }
    }

    private static async Task<Dictionary<long, double>> LoadSignalPnlMapAsync(
        DbContext db, List<MLModelPredictionLog> logs, CancellationToken ct)
    {
        var signalIds = logs
            .Where(l => l.TradeSignalId > 0)
            .Select(l => l.TradeSignalId)
            .Distinct()
            .ToList();
        if (signalIds.Count == 0) return [];

        // signal -> order ids (one signal can in principle map to multiple orders if retries
        // were issued; we sum the realised P&L across all of them).
        var orderRows = await db.Set<Order>()
            .AsNoTracking()
            .Where(o => o.TradeSignalId.HasValue && signalIds.Contains(o.TradeSignalId.Value) && !o.IsDeleted)
            .Select(o => new { o.Id, SignalId = o.TradeSignalId!.Value })
            .ToListAsync(ct);

        if (orderRows.Count == 0) return [];

        var orderIds = orderRows.Select(o => o.Id).Distinct().ToList();

        var positionRows = await db.Set<Position>()
            .AsNoTracking()
            .Where(p => p.OpenOrderId.HasValue
                     && orderIds.Contains(p.OpenOrderId.Value)
                     && p.Status == PositionStatus.Closed
                     && !p.IsDeleted)
            .Select(p => new { OrderId = p.OpenOrderId!.Value, p.RealizedPnL })
            .ToListAsync(ct);

        if (positionRows.Count == 0) return [];

        var orderToSignal = orderRows.ToDictionary(o => o.Id, o => o.SignalId);
        var map = new Dictionary<long, double>(positionRows.Count);
        foreach (var row in positionRows)
        {
            if (!orderToSignal.TryGetValue(row.OrderId, out var sigId)) continue;
            double pnl = (double)row.RealizedPnL;
            if (!double.IsFinite(pnl)) continue;
            map[sigId] = map.TryGetValue(sigId, out var existing) ? existing + pnl : pnl;
        }
        return map;
    }

    private static (List<MLModelPredictionLog> Sweep, List<MLModelPredictionLog> Holdout) SplitWalkForward(
        List<MLModelPredictionLog> chronological, double holdoutFraction, int minHoldoutSamples)
    {
        if (chronological.Count == 0)
            return (chronological, chronological);

        if (minHoldoutSamples > 0 && chronological.Count < minHoldoutSamples * 2)
            return (chronological, new List<MLModelPredictionLog>(0));

        int holdoutCount = Math.Max(
            minHoldoutSamples,
            (int)Math.Round(chronological.Count * Math.Clamp(holdoutFraction, 0.0, 0.5)));
        holdoutCount = Math.Min(holdoutCount, chronological.Count - 1);
        if (holdoutCount <= 0) return (chronological, new List<MLModelPredictionLog>(0));

        int sweepEnd = chronological.Count - holdoutCount;
        return (chronological.Take(sweepEnd).ToList(), chronological.Skip(sweepEnd).ToList());
    }

    private static bool IsThresholdInformative(MLModelPredictionLog log)
    {
        return log.ServedCalibratedProbability.HasValue
            || log.CalibratedProbability.HasValue
            || log.RawProbability.HasValue
            || log.DecisionThresholdUsed.HasValue;
    }

    private static (double BestThreshold, double BestEv) FindBestThreshold(
        IReadOnlyList<MLModelPredictionLog> logs,
        double currentThreshold,
        DateTime nowUtc,
        double halfLifeDays,
        IReadOnlyDictionary<long, double> pnlMap)
    {
        double anchor = Math.Clamp(
            SanitizeThreshold(currentThreshold, DefaultDecisionThreshold),
            SearchMinThreshold,
            SearchMaxThreshold);

        double bestThreshold = anchor;
        double bestEv = double.NegativeInfinity;

        for (int step = (int)Math.Round(SearchMinThreshold * 100);
             step <= (int)Math.Round(SearchMaxThreshold * 100);
             step += SearchStepBasisPoints)
        {
            double threshold = step / 100.0;
            var result = ComputeWeightedEv(logs, threshold, currentThreshold, nowUtc, halfLifeDays, pnlMap);
            double ev = result.Ev;

            if (ev > bestEv + ThresholdTieTolerance)
            {
                bestEv = ev;
                bestThreshold = threshold;
                continue;
            }

            if (Math.Abs(ev - bestEv) <= ThresholdTieTolerance &&
                Math.Abs(threshold - anchor) < Math.Abs(bestThreshold - anchor))
            {
                bestThreshold = threshold;
            }
        }

        return (bestThreshold, double.IsFinite(bestEv) ? bestEv : 0);
    }

    private static EvResult ComputeWeightedEv(
        IReadOnlyList<MLModelPredictionLog> logs,
        double threshold,
        double fallbackThreshold,
        DateTime nowUtc,
        double halfLifeDays,
        IReadOnlyDictionary<long, double> pnlMap)
    {
        if (logs.Count == 0) return new EvResult(0, 0, 0, 0);

        double evSum = 0;
        double weightedPnl = 0;
        double totalWeight = 0;
        int wins = 0;
        int total = 0;

        foreach (var log in logs)
        {
            if (!log.ActualDirection.HasValue) continue;

            double pBuy = MLFeatureHelper.ResolveLoggedServedBuyProbability(log, fallbackThreshold);
            bool predictedBuy = pBuy >= threshold;
            bool actualBuy = log.ActualDirection.Value == TradeDirection.Buy;
            bool correct = predictedBuy == actualBuy;
            double edge = Math.Abs(pBuy - threshold);
            double weight = ComputeTimeDecayWeight(log, nowUtc, halfLifeDays);
            if (!double.IsFinite(weight) || weight <= 0) continue;

            double contribution = ComputeLogContribution(log, predictedBuy, correct, edge, pnlMap);

            evSum += weight * contribution;
            totalWeight += weight;
            total++;
            if (correct) wins++;

            // Mirror the contribution's pnl-vs-magnitude routing in the diagnostic mean P&L.
            bool sameAsHistory = (log.PredictedDirection == TradeDirection.Buy) == predictedBuy;
            if (sameAsHistory && pnlMap.TryGetValue(log.TradeSignalId, out var pnl))
            {
                weightedPnl += weight * pnl;
            }
            else if (log.ActualMagnitudePips.HasValue)
            {
                double mag = Math.Abs((double)log.ActualMagnitudePips.Value);
                weightedPnl += weight * (correct ? mag : -mag);
            }
        }

        if (totalWeight <= 0) return new EvResult(0, 0, 0, 0);

        return new EvResult(evSum / totalWeight, wins, total, weightedPnl / totalWeight);
    }

    /// <summary>
    /// Per-log signed contribution. Real broker P&amp;L only applies when the test threshold
    /// would have predicted the same direction as the historical execution — otherwise the
    /// outcome would be a counterfactual estimate and we fall through to the magnitude
    /// heuristic instead of inverting the historical pnl.
    /// </summary>
    private static double ComputeLogContribution(
        MLModelPredictionLog log,
        bool predictedBuyAtTestThreshold,
        bool correctAtTestThreshold,
        double edge,
        IReadOnlyDictionary<long, double> pnlMap)
    {
        bool historicalPredictedBuy = log.PredictedDirection == TradeDirection.Buy;
        bool sameAsHistory = historicalPredictedBuy == predictedBuyAtTestThreshold;

        if (sameAsHistory && pnlMap.TryGetValue(log.TradeSignalId, out var pnl))
        {
            // Same trade direction as history → broker P&L is already signed by reality.
            return edge * pnl;
        }

        // Counterfactual or no broker outcome — fall back to the magnitude heuristic.
        double mag = log.ActualMagnitudePips.HasValue
            ? Math.Abs((double)log.ActualMagnitudePips.Value)
            : 1.0;
        return (correctAtTestThreshold ? 1.0 : -1.0) * edge * mag;
    }

    private static PairedEvComparison ComputeHoldoutEvComparison(
        IReadOnlyList<MLModelPredictionLog> logs,
        double prevThreshold,
        double newThreshold,
        double fallbackThreshold,
        DateTime nowUtc,
        double halfLifeDays,
        IReadOnlyDictionary<long, double> pnlMap)
    {
        if (logs.Count == 0) return new PairedEvComparison(0, 0, 0, 0, 0, 0);

        double sumPrev = 0, sumNew = 0;
        double totalWeight = 0;
        double weightedPnlNew = 0;
        int wins = 0, total = 0;

        // First pass: compute weighted EVs at both thresholds, paired per log so we can
        // measure stderr of the difference for the Bonferroni-corrected regression test.
        var deltas = new List<(double WeightedDelta, double Weight)>(logs.Count);

        foreach (var log in logs)
        {
            if (!log.ActualDirection.HasValue) continue;

            double pBuy = MLFeatureHelper.ResolveLoggedServedBuyProbability(log, fallbackThreshold);
            bool actualBuy = log.ActualDirection.Value == TradeDirection.Buy;

            bool predictedPrev = pBuy >= prevThreshold;
            bool predictedNew = pBuy >= newThreshold;
            bool correctPrev = predictedPrev == actualBuy;
            bool correctNew = predictedNew == actualBuy;
            double edgePrev = Math.Abs(pBuy - prevThreshold);
            double edgeNew = Math.Abs(pBuy - newThreshold);

            double weight = ComputeTimeDecayWeight(log, nowUtc, halfLifeDays);
            if (!double.IsFinite(weight) || weight <= 0) continue;

            double contribPrev = ComputeLogContribution(log, predictedPrev, correctPrev, edgePrev, pnlMap);
            double contribNew = ComputeLogContribution(log, predictedNew, correctNew, edgeNew, pnlMap);

            sumPrev += weight * contribPrev;
            sumNew += weight * contribNew;
            totalWeight += weight;
            total++;
            if (correctNew) wins++;

            // Mirror the contribution's pnl-vs-magnitude routing: real broker P&L only when
            // the test threshold matches the historical execution direction, otherwise the
            // counterfactual is too uncertain to attribute and we use the magnitude proxy.
            bool sameAsHistoryNew = (log.PredictedDirection == TradeDirection.Buy) == predictedNew;
            if (sameAsHistoryNew && pnlMap.TryGetValue(log.TradeSignalId, out var pnl))
            {
                weightedPnlNew += weight * pnl;
            }
            else if (log.ActualMagnitudePips.HasValue)
            {
                double mag = Math.Abs((double)log.ActualMagnitudePips.Value);
                weightedPnlNew += weight * (correctNew ? mag : -mag);
            }

            deltas.Add((weight * (contribNew - contribPrev), weight));
        }

        if (totalWeight <= 0) return new PairedEvComparison(0, 0, 0, 0, 0, 0);

        double evPrev = sumPrev / totalWeight;
        double evNew = sumNew / totalWeight;

        // Stderr of the paired weighted mean. Uses Kish's effective sample size so non-uniform
        // (e.g. time-decayed) weights produce honest variance estimates. With uniform weights
        // this collapses to the standard sample-mean stderr; with skewed weights it correctly
        // shrinks the effective N.
        double stderr = 0;
        if (deltas.Count >= 2 && totalWeight > 0)
        {
            double sumW = totalWeight;
            double sumW2 = 0;
            foreach (var (_, w) in deltas) sumW2 += w * w;
            double nEff = sumW2 > 0 ? (sumW * sumW) / sumW2 : deltas.Count;

            double meanDelta = (sumNew - sumPrev) / sumW;
            double weightedSquaredDiff = 0;
            foreach (var (wd, w) in deltas)
            {
                double perLogContribution = wd / w;
                double diff = perLogContribution - meanDelta;
                weightedSquaredDiff += w * diff * diff;
            }
            double weightedVariance = weightedSquaredDiff / sumW;
            double denom = Math.Max(nEff - 1, 1);
            stderr = Math.Sqrt(weightedVariance / denom);
            if (!double.IsFinite(stderr)) stderr = 0;
        }

        return new PairedEvComparison(
            EvAtPrev: evPrev,
            EvAtNew: evNew,
            PairedStderr: stderr,
            Wins: wins,
            Total: total,
            MeanPnlPips: weightedPnlNew / totalWeight);
    }

    private static double ComputeTimeDecayWeight(MLModelPredictionLog log, DateTime nowUtc, double halfLifeDays)
    {
        if (halfLifeDays <= 0) return 1.0;
        DateTime anchor = log.OutcomeRecordedAt ?? log.PredictedAt;
        double ageDays = Math.Max(0, (nowUtc - anchor).TotalDays);
        return Math.Pow(0.5, ageDays / halfLifeDays);
    }

    private static double ComputePsiAlphaScale(double psi, double psiThreshold, double psiHardCap)
    {
        if (psi <= psiThreshold) return 1.0;
        if (psi >= psiHardCap) return 0.0;
        // Linear ramp from full alpha at psi==threshold down to zero at psi==hardCap.
        double range = psiHardCap - psiThreshold;
        if (range <= 0) return 0.0;
        return Math.Clamp(1.0 - ((psi - psiThreshold) / range), 0.0, 1.0);
    }

    private static double BlendThreshold(double targetThreshold, double currentThreshold, double emaAlpha)
    {
        double blended = emaAlpha * targetThreshold + (1.0 - emaAlpha) * currentThreshold;
        return Math.Clamp(blended, MinProbabilityThreshold, MaxProbabilityThreshold);
    }

    private static bool NormalizeSnapshotThresholdState(ModelSnapshot snapshot, out int removedRegimeThresholds)
    {
        bool updated = false;
        removedRegimeThresholds = 0;

        if (snapshot.AdaptiveThreshold > 0.0 && !IsFiniteThreshold(snapshot.AdaptiveThreshold))
        {
            snapshot.AdaptiveThreshold = 0.0;
            updated = true;
        }

        if (snapshot.RegimeThresholds is null || snapshot.RegimeThresholds.Count == 0)
            return updated;

        foreach (var key in snapshot.RegimeThresholds.Keys.ToList())
        {
            if (string.IsNullOrWhiteSpace(key) ||
                !IsFiniteThreshold(snapshot.RegimeThresholds[key]))
            {
                snapshot.RegimeThresholds.Remove(key);
                removedRegimeThresholds++;
            }
        }

        return updated || removedRegimeThresholds > 0;
    }

    private static int PruneMissingRegimeThresholds(
        Dictionary<string, double> regimeThresholds, HashSet<string> matchedRegimes)
    {
        if (regimeThresholds.Count == 0) return 0;
        int removed = 0;
        foreach (var key in regimeThresholds.Keys.ToList())
        {
            if (matchedRegimes.Contains(key)) continue;
            regimeThresholds.Remove(key);
            removed++;
        }
        return removed;
    }

    private static MarketRegimeEnum? FindRegimeAt(List<RegimeSlice> timeline, DateTime predictedAt)
    {
        int lo = 0, hi = timeline.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (timeline[mid].DetectedAt <= predictedAt) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found >= 0 ? timeline[found].Regime : null;
    }

    private static double ComputeStationarityPsi(List<MLModelPredictionLog> logs, int minSamples)
    {
        if (minSamples <= 0 || logs.Count < minSamples) return 0;

        var sorted = logs.OrderBy(l => l.PredictedAt).ToList();
        int half = sorted.Count / 2;
        if (half < 2) return 0;

        var older = sorted.Take(half).Select(l => (double)l.ConfidenceScore).ToList();
        var newer = sorted.Skip(half).Select(l => (double)l.ConfidenceScore).ToList();

        const int bins = 10;
        var oldDist = Histogram(older, bins);
        var newDist = Histogram(newer, bins);

        double psi = 0;
        for (int i = 0; i < bins; i++)
        {
            double e = Math.Max(oldDist[i], 1e-4);
            double a = Math.Max(newDist[i], 1e-4);
            psi += (a - e) * Math.Log(a / e);
        }

        return double.IsFinite(psi) ? Math.Max(0, psi) : 0;
    }

    private static double[] Histogram(List<double> values, int bins)
    {
        var counts = new double[bins];
        if (values.Count == 0) return counts;
        foreach (var v in values)
        {
            double clamped = Math.Clamp(v, 0, 1);
            int idx = Math.Min(bins - 1, (int)(clamped * bins));
            counts[idx]++;
        }
        for (int i = 0; i < bins; i++) counts[i] /= values.Count;
        return counts;
    }

    private static double WilsonLowerBound(int wins, int total, double z = 1.96)
    {
        if (total <= 0) return 0;
        double pHat = (double)wins / total;
        double z2 = z * z;
        double denom = 1 + z2 / total;
        double centre = pHat + z2 / (2.0 * total);
        double margin = z * Math.Sqrt((pHat * (1 - pHat) + z2 / (4.0 * total)) / total);
        double lb = (centre - margin) / denom;
        return double.IsFinite(lb) ? Math.Clamp(lb, 0, 1) : 0;
    }

    private void EnqueueAudit(
        List<MLAdaptiveThresholdLog> pending,
        ActiveModelCandidate model,
        MarketRegimeEnum? regime,
        AuditOutcome outcome,
        string reason,
        int sweepSize,
        int holdoutSize,
        double previousThreshold,
        double optimal,
        double newThreshold,
        double drift,
        double holdoutEvNew,
        double holdoutEvPrev,
        double meanPnlPips,
        double psi,
        DateTime? newestOutcomeAt,
        string diagnostics,
        DateTime evaluatedAt)
    {
        pending.Add(new MLAdaptiveThresholdLog
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            Regime = regime,
            EvaluatedAt = evaluatedAt,
            Outcome = OutcomeString(outcome),
            Reason = TrimToLength(reason, 64),
            PreviousThreshold = previousThreshold,
            OptimalThreshold = optimal,
            NewThreshold = newThreshold,
            Drift = drift,
            HoldoutEvAtNewThreshold = holdoutEvNew,
            HoldoutEvAtPreviousThreshold = holdoutEvPrev,
            HoldoutMeanPnlPips = meanPnlPips,
            SweepSampleSize = sweepSize,
            HoldoutSampleSize = holdoutSize,
            StationarityPsi = psi,
            NewestOutcomeAt = newestOutcomeAt,
            DiagnosticsJson = TrimToLength(diagnostics, MaxAuditDiagnosticsLength),
        });
    }

    private async Task RaiseAnomalousDriftAlertAsync(
        ActiveModelCandidate model, double oldThreshold, double newThreshold, double drift, CancellationToken ct)
    {
        if (_alertDispatcher is null) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

            string dedupKey = $"ml-adaptive-threshold-drift:{model.Id}";
            bool exists = await writeCtx.Set<Alert>()
                .AnyAsync(a => a.DeduplicationKey == dedupKey && a.IsActive && !a.IsDeleted, ct);
            if (exists) return;

            int cooldownSec = await AlertCooldownDefaults.GetCooldownAsync(
                writeCtx, AlertCooldownDefaults.CK_MLDrift, AlertCooldownDefaults.Default_MLDrift, ct);

            string conditionJson = JsonSerializer.Serialize(new
            {
                modelId = model.Id,
                symbol = model.Symbol,
                timeframe = model.Timeframe.ToString(),
                oldThreshold,
                newThreshold,
                drift,
                detectedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            });

            var alert = new Alert
            {
                AlertType = AlertType.ConfigurationDrift,
                Severity = drift >= 0.10 ? AlertSeverity.High : AlertSeverity.Medium,
                DeduplicationKey = dedupKey,
                CooldownSeconds = cooldownSec,
                ConditionJson = conditionJson,
                IsActive = true,
            };

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "Adaptive threshold drift {0:F4} on model {1} ({2}/{3}): {4:F4} -> {5:F4}.",
                drift, model.Id, model.Symbol, model.Timeframe, oldThreshold, newThreshold);

            await _alertDispatcher.DispatchAsync(alert, message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Worker}: failed to dispatch anomalous-drift alert for model {ModelId}.",
                WorkerName, model.Id);
        }
    }

    private async Task RaiseDataStarvationAlertAsync(int totalModels, int starvedModels, CancellationToken ct)
    {
        if (_alertDispatcher is null) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

            const string dedupKey = "ml-adaptive-threshold-data-starvation";
            bool exists = await writeCtx.Set<Alert>()
                .AnyAsync(a => a.DeduplicationKey == dedupKey && a.IsActive && !a.IsDeleted, ct);
            if (exists) return;

            int cooldownSec = await AlertCooldownDefaults.GetCooldownAsync(
                writeCtx, AlertCooldownDefaults.CK_MLMonitoring, AlertCooldownDefaults.Default_MLMonitoring, ct);

            string conditionJson = JsonSerializer.Serialize(new
            {
                totalModels,
                starvedModels,
                ratio = totalModels > 0 ? (double)starvedModels / totalModels : 0,
                detectedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            });

            var alert = new Alert
            {
                AlertType = AlertType.DataQualityIssue,
                Severity = AlertSeverity.High,
                DeduplicationKey = dedupKey,
                CooldownSeconds = cooldownSec,
                ConditionJson = conditionJson,
                IsActive = true,
            };

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "MLAdaptiveThresholdWorker: {0}/{1} models lack sufficient resolved prediction logs - outcome resolution may be stalled.",
                starvedModels, totalModels);

            await _alertDispatcher.DispatchAsync(alert, message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Worker}: failed to dispatch data-starvation alert.", WorkerName);
        }
    }

    private async Task<AdaptiveThresholdWorkerSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled, CK_PollSecs, CK_WindowSize, CK_MinPredictions, CK_EmaAlpha,
            CK_MinDrift, CK_LookbackDays, CK_MinRegimePredictions, CK_MaxModelsPerCycle,
            CK_LockTimeoutSecs, CK_ModelLockTimeoutSecs, CK_HoldoutFraction, CK_MinHoldoutSamples,
            CK_TimeDecayHalfLifeDays, CK_MinSamplesForTimeDecay,
            CK_StationarityPsiThreshold, CK_MinStationaritySamples, CK_PsiHardCapMultiplier,
            CK_WilsonLowerBoundFloor, CK_RegressionGuardK, CK_AnomalousDriftAlertThreshold,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, config => config.Value, ct);

        int windowSize = ClampInt(GetInt(values, CK_WindowSize, DefaultWindowSize),
            DefaultWindowSize, MinWindowSize, MaxWindowSize);
        int minResolvedPredictions = Math.Min(
            ClampInt(GetInt(values, CK_MinPredictions, DefaultMinPredictions),
                DefaultMinPredictions, MinMinPredictions, MaxMinPredictions),
            windowSize);
        int minRegimeResolvedPredictions = Math.Min(
            ClampInt(GetInt(values, CK_MinRegimePredictions, DefaultMinRegimePredictions),
                DefaultMinRegimePredictions, MinMinRegimePredictions, MaxMinRegimePredictions),
            windowSize);

        return new AdaptiveThresholdWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, true),
            PollInterval: TimeSpan.FromSeconds(
                ClampInt(GetInt(values, CK_PollSecs, DefaultPollSeconds),
                    DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            WindowSize: windowSize,
            MinResolvedPredictions: minResolvedPredictions,
            EmaAlpha: ClampDouble(GetDouble(values, CK_EmaAlpha, DefaultEmaAlpha),
                DefaultEmaAlpha, MinEmaAlpha, MaxEmaAlpha),
            MinThresholdDrift: ClampDouble(GetDouble(values, CK_MinDrift, DefaultMinDrift),
                DefaultMinDrift, MinMinDrift, MaxMinDrift),
            LookbackDays: ClampInt(GetInt(values, CK_LookbackDays, DefaultLookbackDays),
                DefaultLookbackDays, MinLookbackDays, MaxLookbackDays),
            MinRegimeResolvedPredictions: minRegimeResolvedPredictions,
            MaxModelsPerCycle: ClampInt(GetInt(values, CK_MaxModelsPerCycle, DefaultMaxModelsPerCycle),
                DefaultMaxModelsPerCycle, MinMaxModelsPerCycle, MaxMaxModelsPerCycle),
            LockTimeoutSeconds: ClampInt(GetInt(values, CK_LockTimeoutSecs, DefaultLockTimeoutSeconds),
                DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            ModelLockTimeoutSeconds: ClampInt(GetInt(values, CK_ModelLockTimeoutSecs, DefaultModelLockTimeoutSeconds),
                DefaultModelLockTimeoutSeconds, MinModelLockTimeoutSeconds, MaxModelLockTimeoutSeconds),
            HoldoutFraction: ClampDouble(GetDouble(values, CK_HoldoutFraction, DefaultHoldoutFraction),
                DefaultHoldoutFraction, MinHoldoutFraction, MaxHoldoutFraction),
            MinHoldoutSamples: ClampIntAllowingZero(GetInt(values, CK_MinHoldoutSamples, DefaultMinHoldoutSamples),
                DefaultMinHoldoutSamples, MinMinHoldoutSamples, MaxMinHoldoutSamples),
            TimeDecayHalfLifeDays: ClampDoubleAllowingZero(GetDouble(values, CK_TimeDecayHalfLifeDays, DefaultTimeDecayHalfLifeDays),
                DefaultTimeDecayHalfLifeDays, MinTimeDecayHalfLifeDays, MaxTimeDecayHalfLifeDays),
            MinSamplesForTimeDecay: ClampIntAllowingZero(GetInt(values, CK_MinSamplesForTimeDecay, DefaultMinSamplesForTimeDecay),
                DefaultMinSamplesForTimeDecay, MinMinSamplesForTimeDecay, MaxMinSamplesForTimeDecay),
            StationarityPsiThreshold: ClampDouble(GetDouble(values, CK_StationarityPsiThreshold, DefaultStationarityPsiThreshold),
                DefaultStationarityPsiThreshold, MinStationarityPsiThreshold, MaxStationarityPsiThreshold),
            MinStationaritySamples: ClampIntAllowingZero(GetInt(values, CK_MinStationaritySamples, DefaultMinStationaritySamples),
                DefaultMinStationaritySamples, MinMinStationaritySamples, MaxMinStationaritySamples),
            PsiHardCapMultiplier: ClampDouble(GetDouble(values, CK_PsiHardCapMultiplier, DefaultPsiHardCapMultiplier),
                DefaultPsiHardCapMultiplier, MinPsiHardCapMultiplier, MaxPsiHardCapMultiplier),
            WilsonLowerBoundFloor: ClampDoubleAllowingZero(GetDouble(values, CK_WilsonLowerBoundFloor, DefaultWilsonLowerBoundFloor),
                DefaultWilsonLowerBoundFloor, MinWilsonLowerBoundFloor, MaxWilsonLowerBoundFloor),
            RegressionGuardK: ClampDoubleAllowingZero(GetDouble(values, CK_RegressionGuardK, DefaultRegressionGuardK),
                DefaultRegressionGuardK, MinRegressionGuardK, MaxRegressionGuardK),
            AnomalousDriftAlertThreshold: ClampDouble(GetDouble(values, CK_AnomalousDriftAlertThreshold, DefaultAnomalousDriftAlertThreshold),
                DefaultAnomalousDriftAlertThreshold, MinAnomalousDriftAlertThreshold, MaxAnomalousDriftAlertThreshold));
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (bool.TryParse(raw, out var b)) return b;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i != 0;
        return defaultValue;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
    {
        return values.TryGetValue(key, out var raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double defaultValue)
    {
        return values.TryGetValue(key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static int ClampInt(int value, int fallback, int min, int max)
    {
        if (value <= 0) return fallback;
        return Math.Min(Math.Max(value, min), max);
    }

    private static int ClampIntAllowingZero(int value, int fallback, int min, int max)
    {
        if (value < 0) return fallback;
        return Math.Min(Math.Max(value, min), max);
    }

    private static double ClampDouble(double value, double fallback, double min, double max)
    {
        if (!double.IsFinite(value) || value <= 0.0) return fallback;
        return Math.Min(Math.Max(value, min), max);
    }

    private static double ClampDoubleAllowingZero(double value, double fallback, double min, double max)
    {
        if (!double.IsFinite(value) || value < 0.0) return fallback;
        return Math.Min(Math.Max(value, min), max);
    }

    private static double SanitizeThreshold(double threshold, double fallback)
        => IsFiniteThreshold(threshold) ? threshold : fallback;

    private static bool IsFiniteThreshold(double threshold)
        => double.IsFinite(threshold)
        && threshold >= MinProbabilityThreshold
        && threshold <= MaxProbabilityThreshold;

    private static string OutcomeString(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Updated => "updated",
        AuditOutcome.SkippedDrift => "skipped_drift",
        AuditOutcome.SkippedData => "skipped_data",
        AuditOutcome.SkippedRegression => "skipped_regression",
        AuditOutcome.SkippedStationarity => "skipped_stationarity",
        AuditOutcome.SkippedConcurrency => "skipped_concurrency",
        _ => "error",
    };

    private static string TrimToLength(string s, int max)
        => s.Length <= max ? s : s[..max];

    private static string BuildDiagnostics(params (string Key, object Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => p.Value);
        return JsonSerializer.Serialize(dict);
    }

    internal readonly record struct AdaptiveThresholdWorkerSettings(
        bool Enabled,
        TimeSpan PollInterval,
        int WindowSize,
        int MinResolvedPredictions,
        double EmaAlpha,
        double MinThresholdDrift,
        int LookbackDays,
        int MinRegimeResolvedPredictions,
        int MaxModelsPerCycle,
        int LockTimeoutSeconds,
        int ModelLockTimeoutSeconds,
        double HoldoutFraction,
        int MinHoldoutSamples,
        double TimeDecayHalfLifeDays,
        int MinSamplesForTimeDecay,
        double StationarityPsiThreshold,
        int MinStationaritySamples,
        double PsiHardCapMultiplier,
        double WilsonLowerBoundFloor,
        double RegressionGuardK,
        double AnomalousDriftAlertThreshold)
    {
        // Preserve the older public name so in-flight call sites and diagnostics remain source-compatible.
        public double BonferroniK => RegressionGuardK;
    }

    internal readonly record struct AdaptiveThresholdCycleResult(
        AdaptiveThresholdWorkerSettings Settings,
        string? SkippedReason,
        int ModelsProcessed,
        int ModelsUpdated,
        int ModelsSkipped,
        int ModelsFailed,
        int ModelsStarved,
        int RegimeThresholdsPruned)
    {
        public static AdaptiveThresholdCycleResult Skipped(
            AdaptiveThresholdWorkerSettings settings, string reason)
            => new(settings, reason, 0, 0, 0, 0, 0, 0);
    }

    private enum AuditOutcome
    {
        Updated,
        SkippedDrift,
        SkippedData,
        SkippedRegression,
        SkippedStationarity,
        SkippedConcurrency,
        Error,
    }
}
