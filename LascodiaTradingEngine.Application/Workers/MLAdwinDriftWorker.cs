using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Drift;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using DomainMarketRegime = LascodiaTradingEngine.Domain.Enums.MarketRegime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Applies ADWIN-style adaptive-window drift detection to resolved live ML outcomes and
/// queues retraining only for statistically significant degradation.
/// </summary>
/// <remarks>
/// <para>
/// The worker evaluates recent binary directional-accuracy outcomes per active model. It
/// records a daily audit row for every sufficiently fresh model, but only raises the
/// retirement/retraining flag when the newer sub-window is meaningfully worse than the
/// older one. Statistically significant improvements are logged for auditability without
/// being misclassified as degradation.
/// </para>
/// <para>
/// Drift flags are persisted as typed <see cref="MLDriftFlag"/> rows (one per
/// <c>(Symbol, Timeframe, DetectorType)</c>) — replacing the earlier pattern of stuffing
/// expiry timestamps into <c>EngineConfig</c>. Drift detection also fires an alert via
/// <see cref="IAlertDispatcher"/> with a flag-key dedupe so operators see one event per
/// flag-TTL window rather than one per cycle.
/// </para>
/// <para>
/// Retrain queueing is guarded by (a) a 12-hour cooldown that suppresses re-queueing
/// while an earlier auto-degrading run is still propagating through the SPRT shadow
/// tournament and (b) a Postgres partial-unique index on
/// <c>MLTrainingRun(Symbol, Timeframe) WHERE Status IN (Queued, Running)</c> that closes
/// the TOCTOU race between read and insert.
/// </para>
/// </remarks>
public sealed class MLAdwinDriftWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLAdwinDriftWorker);

    internal const string DriftDetectorType = "AdwinDrift";

    private const string DistributedLockKey = "workers:ml-adwin-drift:cycle";
    private const string DriftTriggerType = DriftDetectorType;

    private const string CK_Enabled = "MLAdwinDrift:Enabled";
    private const string CK_PollSecs = "MLAdwinDrift:PollIntervalSeconds";
    private const string CK_WindowSize = "MLAdwinDrift:WindowSize";
    private const string CK_MinResolvedPredictions = "MLAdwinDrift:MinResolvedPredictions";
    private const string CK_Delta = "MLAdwinDrift:Delta";
    private const string CK_LookbackDays = "MLAdwinDrift:LookbackDays";
    private const string CK_FlagTtlHours = "MLAdwinDrift:FlagTtlHours";
    private const string CK_MaxModelsPerCycle = "MLAdwinDrift:MaxModelsPerCycle";
    private const string CK_LockTimeoutSecs = "MLAdwinDrift:LockTimeoutSeconds";
    private const string CK_TrainingDays = "MLTraining:TrainingDataWindowDays";
    private const string CK_MinTimeBetweenRetrainsHours = "MLAdwinDrift:MinTimeBetweenRetrainsHours";
    private const string CK_SnapshotOutcomeSeries = "MLAdwinDrift:SnapshotOutcomeSeries";
    private const string CK_DbCommandTimeoutSecs = "MLAdwinDrift:DbCommandTimeoutSeconds";
    private const string CK_SaveBatchSize = "MLAdwinDrift:SaveBatchSize";

    // Per-(symbol, timeframe) override key prefix:
    //   MLAdwinDrift:Override:{Symbol}:{Timeframe}:Delta
    //   MLAdwinDrift:Override:{Symbol}:{Timeframe}:WindowSize
    private const string CK_OverridePrefix = "MLAdwinDrift:Override:";

    private const int DefaultPollSeconds = 24 * 60 * 60;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 7 * 24 * 60 * 60;

    private const int DefaultWindowSize = 100;
    private const int MinWindowSize = AdwinDetector.MinRequiredObservations;
    private const int MaxWindowSize = 5000;

    private const int DefaultMinResolvedPredictions = AdwinDetector.MinRequiredObservations;
    private const int MinMinResolvedPredictions = AdwinDetector.MinRequiredObservations;
    private const int MaxMinResolvedPredictions = 5000;

    private const double DefaultDelta = 0.002;
    private const double MinDelta = 0.000001;
    private const double MaxDelta = 0.25;

    private const int DefaultLookbackDays = 180;
    private const int MinLookbackDays = 1;
    private const int MaxLookbackDays = 3650;

    private const int DefaultFlagTtlHours = 48;
    private const int MinFlagTtlHours = 1;
    private const int MaxFlagTtlHours = 24 * 30;

    private const int DefaultMaxModelsPerCycle = 256;
    private const int MinMaxModelsPerCycle = 1;
    private const int MaxMaxModelsPerCycle = 4096;

    private const int DefaultLockTimeoutSeconds = 5;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private const int DefaultTrainingDataWindowDays = 365;
    private const int MinTrainingDataWindowDays = 30;
    private const int MaxTrainingDataWindowDays = 3650;

    private const int DefaultMinTimeBetweenRetrainsHours = 12;
    private const int MinMinTimeBetweenRetrainsHours = 0;
    private const int MaxMinTimeBetweenRetrainsHours = 24 * 30;

    private const int DefaultDbCommandTimeoutSeconds = 60;
    private const int MinDbCommandTimeoutSeconds = 5;
    private const int MaxDbCommandTimeoutSeconds = 600;

    private const int DefaultSaveBatchSize = 32;
    private const int MinSaveBatchSize = 1;
    private const int MaxSaveBatchSize = 256;

    /// <summary>
    /// Outer-loop wake cadence. Each wake re-reads <see cref="EngineConfig"/> so operator
    /// changes to <c>PollIntervalSeconds</c> propagate within a minute, regardless of how
    /// long the configured cycle interval is.
    /// </summary>
    private static readonly TimeSpan WakeInterval = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLAdwinDriftWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IAlertDispatcher? _alertDispatcher;

    // Mutated only from ExecuteAsync's single thread today, but flagged volatile/Interlocked
    // so a future refactor that runs cycles concurrently does not silently break.
    private long _consecutiveFailuresField;
    private int _missingDistributedLockWarningEmitted;

    private readonly record struct ActiveModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        LearnerArchitecture LearnerArchitecture);

    private readonly record struct ResolvedOutcome(DateTime PredictedAt, bool DirectionCorrect);

    private readonly record struct ModelCycleOutcome(
        bool Evaluated,
        bool DriftDetected,
        bool RetrainingQueued,
        bool FlagCleared,
        string? SkipReason)
    {
        public static ModelCycleOutcome Skipped(string reason) => new(false, false, false, false, reason);
    }

    /// <summary>
    /// Result of pure-CPU evaluation per model (no DB writes). Carried into the batched
    /// save phase so all DB mutation happens together rather than once per model.
    /// </summary>
    private sealed record PreparedOutcome(
        ActiveModelCandidate Model,
        bool Evaluated,
        AdwinScanResult? Scan,
        bool[]? Outcomes,
        int Window1Size,
        int Window2Size,
        double EffectiveDelta,
        DomainMarketRegime? Regime,
        string? SkipReason);

    /// <summary>Materialization shape for the batched <c>ROW_NUMBER OVER PARTITION BY</c> query.</summary>
    private sealed class BatchedOutcomeRow
    {
        public long MLModelId { get; set; }
        public DateTime PredictedAt { get; set; }
        public int DirectionCorrectInt { get; set; }
    }

    public MLAdwinDriftWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLAdwinDriftWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IAlertDispatcher? alertDispatcher = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _alertDispatcher = alertDispatcher;
    }

    private int ConsecutiveFailures
    {
        get => (int)Interlocked.Read(ref _consecutiveFailuresField);
        set => Interlocked.Exchange(ref _consecutiveFailuresField, value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Applies ADWIN-style adaptive drift detection to resolved live ML outcomes and queues retraining only on statistically significant degradation.",
            TimeSpan.FromSeconds(DefaultPollSeconds));

        DateTime lastCycleStartUtc = DateTime.MinValue;
        DateTime lastSuccessUtc = DateTime.MinValue;
        TimeSpan currentPollInterval = TimeSpan.FromSeconds(DefaultPollSeconds);

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
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

                if (lastSuccessUtc != DateTime.MinValue)
                {
                    _metrics?.MLAdwinTimeSinceLastSuccessSec.Record(
                        (nowUtc - lastSuccessUtc).TotalSeconds);
                }

                bool dueForCycle = nowUtc - lastCycleStartUtc >= currentPollInterval;

                if (dueForCycle)
                {
                    long cycleStarted = Stopwatch.GetTimestamp();
                    lastCycleStartUtc = nowUtc;

                    try
                    {
                        _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                        var result = await RunCycleAsync(stoppingToken);
                        currentPollInterval = result.Settings.PollInterval;

                        long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                        _healthMonitor?.RecordBacklogDepth(WorkerName, result.CandidateModelCount);
                        _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                        _metrics?.WorkerCycleDurationMs.Record(
                            durationMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.MLAdwinCycleDurationMs.Record(durationMs);

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
                                "{Worker}: candidates={Candidates}, evaluated={Evaluated}, drifts={Drifts}, retrainingQueued={Queued}, flagsCleared={FlagsCleared}, failed={Failed}.",
                                WorkerName,
                                result.CandidateModelCount,
                                result.EvaluatedModelCount,
                                result.DriftCount,
                                result.RetrainingQueuedCount,
                                result.FlagClearCount,
                                result.FailedModelCount);
                        }

                        var prevFailures = ConsecutiveFailures;
                        if (prevFailures > 0)
                        {
                            _healthMonitor?.RecordRecovery(WorkerName, prevFailures);
                            _logger.LogInformation(
                                "{Worker}: recovered after {Failures} consecutive failure(s).",
                                WorkerName,
                                prevFailures);
                        }

                        ConsecutiveFailures = 0;
                        lastSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _consecutiveFailuresField);
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                        _metrics?.WorkerErrors.Add(
                            1,
                            new KeyValuePair<string, object?>("worker", WorkerName),
                            new KeyValuePair<string, object?>("reason", "ml_adwin_cycle"));
                        _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                    }
                }

                try
                {
                    await Task.Delay(CalculateDelay(WakeInterval, ConsecutiveFailures), _timeProvider, stoppingToken);
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

    internal async Task<AdwinCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        ApplyCommandTimeout(db, settings.DbCommandTimeoutSeconds);

        if (!settings.Enabled)
        {
            _metrics?.MLAdwinCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "disabled"));
            return AdwinCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.MLAdwinLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate ADWIN cycles are possible in multi-instance deployments.",
                    WorkerName);
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
                _metrics?.MLAdwinLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.MLAdwinCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return AdwinCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLAdwinLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));

            await using (cycleLock)
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
                try
                {
                    return await RunCycleCoreAsync(db, settings, ct);
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
            return await RunCycleCoreAsync(db, settings, ct);
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
                ? WakeInterval
                : baseInterval;
        }

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        // Only apply on relational providers; in-memory test providers throw on this call.
        try
        {
            if (db.Database.IsRelational())
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(seconds));
        }
        catch (InvalidOperationException)
        {
            // Provider does not support timeouts — safe to skip.
        }
    }

    private async Task<AdwinCycleResult> RunCycleCoreAsync(
        DbContext db,
        AdwinWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var recentPredictionCutoff = nowUtc.AddDays(-settings.LookbackDays);

        // Phase A: load active models (with the dormant-model filter pushed into SQL).
        var activeModels = await LoadActiveModelsAsync(db, settings, recentPredictionCutoff, ct);

        if (activeModels.Count == 0)
        {
            return new AdwinCycleResult(
                settings,
                SkippedReason: null,
                CandidateModelCount: 0,
                EvaluatedModelCount: 0,
                DriftCount: 0,
                RetrainingQueuedCount: 0,
                FlagClearCount: 0,
                FailedModelCount: 0);
        }

        // Phase B: batch-load all per-pair data (overrides + outcomes + flags + regimes).
        // Each is a single query — no per-model round-trips.
        var overrides = await LoadAllPerPairOverridesAsync(db, ct);
        int maxBatchedWindowSize = ComputeMaxBatchedWindowSize(activeModels, settings, overrides);

        var modelIds = activeModels.Select(m => m.Id).ToList();
        var outcomesByModel = await LoadOutcomesBatchedAsync(
            db, modelIds, maxBatchedWindowSize, recentPredictionCutoff, ct);
        var flagsByPair = await LoadFlagsBatchedAsync(db, activeModels, ct);
        var regimesByPair = await LoadDominantRegimesBatchedAsync(
            db, activeModels, recentPredictionCutoff, ct);

        // Phase C: pure-CPU evaluation per model (no DB writes, no exceptions from queries).
        var prepared = new List<PreparedOutcome>(activeModels.Count);
        int failedModelCount = 0;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            _metrics?.MLAdwinModelsEvaluated.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                new KeyValuePair<string, object?>("learner_architecture", model.LearnerArchitecture.ToString()));

            try
            {
                var outcome = PrepareOutcome(model, settings, overrides, outcomesByModel, regimesByPair);
                prepared.Add(outcome);

                if (!outcome.Evaluated && outcome.SkipReason is { Length: > 0 })
                {
                    _metrics?.MLAdwinModelsSkipped.Add(
                        1,
                        new KeyValuePair<string, object?>("reason", outcome.SkipReason),
                        new KeyValuePair<string, object?>("symbol", model.Symbol),
                        new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                }
                else if (outcome.Evaluated && outcome.Scan!.Value.DriftDetected)
                {
                    _metrics?.MLAdwinDriftsDetected.Add(
                        1,
                        new KeyValuePair<string, object?>("symbol", model.Symbol),
                        new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                        new KeyValuePair<string, object?>("learner_architecture", model.LearnerArchitecture.ToString()));
                    _metrics?.MLAdwinDetectedAccuracyDrop.Record(
                        outcome.Scan.Value.AccuracyDrop,
                        new KeyValuePair<string, object?>("symbol", model.Symbol),
                        new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));

                    _logger.LogWarning(
                        "{Worker}: degrading ADWIN drift detected for {Symbol}/{Timeframe} (mu1={Window1Mean:F4}, mu2={Window2Mean:F4}, eps={EpsilonCut:F4}, split={SplitIndex}, n={WindowSize}, drop={AccuracyDrop:F4}, regime={Regime}).",
                        WorkerName,
                        model.Symbol,
                        model.Timeframe,
                        outcome.Scan.Value.SelectedEvidence.Window1Mean,
                        outcome.Scan.Value.SelectedEvidence.Window2Mean,
                        outcome.Scan.Value.SelectedEvidence.EpsilonCut,
                        outcome.Scan.Value.SelectedEvidence.SplitIndex,
                        outcome.Window1Size + outcome.Window2Size,
                        outcome.Scan.Value.AccuracyDrop,
                        outcome.Regime?.ToString() ?? "Unknown");
                }
            }
            catch (Exception ex)
            {
                failedModelCount++;
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName),
                    new KeyValuePair<string, object?>("reason", "ml_adwin_prepare"),
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                _logger.LogWarning(ex,
                    "{Worker}: prepare failed for model {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    WorkerName, model.Id, model.Symbol, model.Timeframe);
            }
        }

        // Phase D: batched audit log + flag mutation saves. One commit per batch (default 32),
        // with per-model fallback on DbUpdateException so a single bad row doesn't poison
        // the others. Saves audit+flag changes only — retrain inserts (which need their own
        // unique-violation handling) happen in phase E.
        int flagClearCount = 0;
        var evaluated = prepared.Where(p => p.Evaluated).ToList();
        foreach (var batch in Chunk(evaluated, settings.SaveBatchSize))
        {
            flagClearCount += await SaveAuditAndFlagsBatchAsync(
                db, batch, flagsByPair, settings, nowUtc, ct);
        }

        // Phase E: per-drift retrain queue + alert dispatch. Audit row is already committed,
        // so drift evidence is durable even if retrain or alert dispatch fails downstream.
        int evaluatedModelCount = evaluated.Count;
        int driftCount = evaluated.Count(p => p.Scan!.Value.DriftDetected);
        int retrainingQueuedCount = 0;

        foreach (var p in evaluated.Where(o => o.Scan!.Value.DriftDetected))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                bool queued = await QueueRetrainingIfNeededAsync(
                    db,
                    p.Model,
                    settings,
                    p.EffectiveDelta,
                    nowUtc,
                    p.Window1Size + p.Window2Size,
                    p.Scan!.Value.SelectedEvidence,
                    p.Scan.Value.AccuracyDrop,
                    p.Regime,
                    ct);

                if (queued)
                    retrainingQueuedCount++;

                _logger.LogInformation(
                    "{Worker}: drift retrain decision for {Symbol}/{Timeframe}: queued={Queued}.",
                    WorkerName, p.Model.Symbol, p.Model.Timeframe, queued);

                await DispatchDriftAlertAsync(
                    p.Model,
                    p.Scan.Value,
                    p.EffectiveDelta,
                    p.Window1Size + p.Window2Size,
                    p.Regime,
                    queued,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedModelCount++;
                db.ChangeTracker.Clear();
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName),
                    new KeyValuePair<string, object?>("reason", "ml_adwin_post_save"),
                    new KeyValuePair<string, object?>("symbol", p.Model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", p.Model.Timeframe.ToString()));
                _logger.LogWarning(ex,
                    "{Worker}: post-save (retrain/alert) failed for model {ModelId} ({Symbol}/{Timeframe}); audit row already committed.",
                    WorkerName, p.Model.Id, p.Model.Symbol, p.Model.Timeframe);
            }
        }

        return new AdwinCycleResult(
            settings,
            SkippedReason: null,
            CandidateModelCount: activeModels.Count,
            EvaluatedModelCount: evaluatedModelCount,
            DriftCount: driftCount,
            RetrainingQueuedCount: retrainingQueuedCount,
            FlagClearCount: flagClearCount,
            FailedModelCount: failedModelCount);
    }

    private static async Task<List<ActiveModelCandidate>> LoadActiveModelsAsync(
        DbContext db,
        AdwinWorkerSettings settings,
        DateTime recentPredictionCutoff,
        CancellationToken ct)
    {
        // Push the "has ≥ MinResolvedPredictions resolved logs in lookback" filter into SQL
        // so dormant models never enter the per-model evaluation loop.
        return await db.Set<MLModel>()
            .AsNoTracking()
            .Where(model => model.IsActive && !model.IsDeleted && !model.IsMetaLearner && !model.IsMamlInitializer)
            .Where(model => db.Set<MLModelPredictionLog>()
                .Count(log =>
                    log.MLModelId == model.Id &&
                    !log.IsDeleted &&
                    log.DirectionCorrect.HasValue &&
                    log.PredictedAt >= recentPredictionCutoff) >= settings.MinResolvedPredictions)
            .OrderBy(model => model.Id)
            .Select(model => new ActiveModelCandidate(
                model.Id,
                model.Symbol,
                model.Timeframe,
                model.LearnerArchitecture))
            .Take(settings.MaxModelsPerCycle)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Pure-CPU per-model evaluation. Builds a <see cref="PreparedOutcome"/> from already-loaded
    /// outcomes, overrides, and regime data; performs no DB I/O.
    /// </summary>
    private static PreparedOutcome PrepareOutcome(
        ActiveModelCandidate model,
        AdwinWorkerSettings settings,
        IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), (double? Delta, int? WindowSize)> overrides,
        IReadOnlyDictionary<long, List<ResolvedOutcome>> outcomesByModel,
        IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), DomainMarketRegime> regimesByPair)
    {
        var pair = (model.Symbol, model.Timeframe);
        overrides.TryGetValue(pair, out var perPair);
        int effectiveWindowSize = perPair.WindowSize ?? settings.WindowSize;
        double effectiveDelta = perPair.Delta ?? settings.Delta;

        if (!outcomesByModel.TryGetValue(model.Id, out var allOutcomes) || allOutcomes.Count == 0)
            return new PreparedOutcome(model, false, null, null, 0, 0, effectiveDelta, null, "no_recent_resolved_predictions");

        // Outcomes come back from the batched query in DESC-by-PredictedAt order, possibly
        // longer than the model's effective window. Trim to that window, then reverse to
        // chronological order for ADWIN.
        var trimmed = allOutcomes.Count > effectiveWindowSize
            ? allOutcomes.GetRange(0, effectiveWindowSize)
            : allOutcomes;

        if (trimmed.Count < settings.MinResolvedPredictions)
            return new PreparedOutcome(model, false, null, null, 0, 0, effectiveDelta, null, "insufficient_history");

        var outcomesArr = new bool[trimmed.Count];
        for (int i = 0; i < trimmed.Count; i++)
            outcomesArr[i] = trimmed[trimmed.Count - 1 - i].DirectionCorrect;

        var scan = AdwinDetector.Evaluate(outcomesArr, effectiveDelta);
        if (scan is null)
            return new PreparedOutcome(model, false, null, null, 0, 0, effectiveDelta, null, "insufficient_history");

        regimesByPair.TryGetValue(pair, out var regime);
        DomainMarketRegime? regimeOrNull = regimesByPair.ContainsKey(pair) ? regime : null;

        int w1 = scan.Value.SelectedEvidence.SplitIndex;
        int w2 = trimmed.Count - w1;

        return new PreparedOutcome(
            model,
            true,
            scan,
            outcomesArr,
            w1,
            w2,
            effectiveDelta,
            regimeOrNull,
            null);
    }

    /// <summary>
    /// Batched audit + flag persistence. Adds all in-memory mutations to the change tracker,
    /// commits in one round-trip, and falls back to per-model save on <see cref="DbUpdateException"/>
    /// so a single bad row doesn't poison the rest of the batch.
    /// </summary>
    private async Task<int> SaveAuditAndFlagsBatchAsync(
        DbContext db,
        IReadOnlyList<PreparedOutcome> batch,
        Dictionary<(string Symbol, Timeframe Timeframe), MLDriftFlag> flagsByPair,
        AdwinWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (batch.Count == 0)
            return 0;

        int flagCleared = 0;

        foreach (var p in batch)
        {
            AddAuditLogToChangeTracker(db, p, nowUtc, settings.SnapshotOutcomeSeries);

            if (p.Scan!.Value.DriftDetected)
                ApplyFlagSetOrRefresh(db, flagsByPair, p.Model, nowUtc, nowUtc.AddHours(settings.FlagTtlHours));
            else if (ApplyFlagExpire(flagsByPair, p.Model, nowUtc))
                flagCleared++;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            return flagCleared;
        }
        catch (DbUpdateException ex) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            _logger.LogWarning(ex,
                "{Worker}: batched audit/flag save failed; retrying per-model. Affected batch size = {Size}.",
                WorkerName, batch.Count);

            // Reload flags for fallback so we don't reapply stale entity references.
            flagsByPair.Clear();
            foreach (var f in await LoadAllFlagsAsync(db, ct))
                flagsByPair[(f.Symbol, f.Timeframe)] = f;

            int fallbackFlagCleared = 0;
            foreach (var p in batch)
            {
                try
                {
                    AddAuditLogToChangeTracker(db, p, nowUtc, settings.SnapshotOutcomeSeries);
                    if (p.Scan!.Value.DriftDetected)
                        ApplyFlagSetOrRefresh(db, flagsByPair, p.Model, nowUtc, nowUtc.AddHours(settings.FlagTtlHours));
                    else if (ApplyFlagExpire(flagsByPair, p.Model, nowUtc))
                        fallbackFlagCleared++;

                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                }
                catch (Exception perEx) when (!ct.IsCancellationRequested)
                {
                    db.ChangeTracker.Clear();
                    _logger.LogWarning(perEx,
                        "{Worker}: per-model fallback save failed for {Symbol}/{Timeframe}.",
                        WorkerName, p.Model.Symbol, p.Model.Timeframe);
                }
            }
            return fallbackFlagCleared;
        }
    }

    private static void AddAuditLogToChangeTracker(
        DbContext db,
        PreparedOutcome p,
        DateTime nowUtc,
        bool snapshotOutcomes)
    {
        var scan = p.Scan!.Value;
        db.Set<MLAdwinDriftLog>().Add(new MLAdwinDriftLog
        {
            MLModelId = p.Model.Id,
            Symbol = p.Model.Symbol,
            Timeframe = p.Model.Timeframe,
            DriftDetected = scan.DriftDetected,
            Window1Mean = scan.SelectedEvidence.Window1Mean,
            Window2Mean = scan.SelectedEvidence.Window2Mean,
            EpsilonCut = scan.SelectedEvidence.EpsilonCut,
            Window1Size = p.Window1Size,
            Window2Size = p.Window2Size,
            DetectedAt = nowUtc,
            AccuracyDrop = scan.AccuracyDrop,
            DeltaUsed = p.EffectiveDelta,
            DominantRegime = p.Regime,
            OutcomeSeriesCompressed = snapshotOutcomes && p.Outcomes is not null
                ? CompressOutcomeSeries(p.Outcomes)
                : null,
        });
    }

    private static void ApplyFlagSetOrRefresh(
        DbContext db,
        Dictionary<(string Symbol, Timeframe Timeframe), MLDriftFlag> flagsByPair,
        ActiveModelCandidate model,
        DateTime nowUtc,
        DateTime expiresAtUtc)
    {
        var key = (model.Symbol, model.Timeframe);
        if (!flagsByPair.TryGetValue(key, out var flag))
        {
            var newFlag = new MLDriftFlag
            {
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                DetectorType = DriftDetectorType,
                ExpiresAtUtc = expiresAtUtc,
                FirstDetectedAtUtc = nowUtc,
                LastRefreshedAtUtc = nowUtc,
                ConsecutiveDetections = 1,
                IsDeleted = false,
            };
            db.Set<MLDriftFlag>().Add(newFlag);
            flagsByPair[key] = newFlag;
            return;
        }

        bool wasActive = !flag.IsDeleted && flag.ExpiresAtUtc > nowUtc;
        flag.ExpiresAtUtc = expiresAtUtc;
        flag.LastRefreshedAtUtc = nowUtc;
        flag.ConsecutiveDetections = wasActive ? flag.ConsecutiveDetections + 1 : 1;
        flag.IsDeleted = false;

        if (!wasActive)
            flag.FirstDetectedAtUtc = nowUtc;
    }

    private bool ApplyFlagExpire(
        Dictionary<(string Symbol, Timeframe Timeframe), MLDriftFlag> flagsByPair,
        ActiveModelCandidate model,
        DateTime nowUtc)
    {
        var key = (model.Symbol, model.Timeframe);
        if (!flagsByPair.TryGetValue(key, out var flag) || flag.ExpiresAtUtc <= nowUtc)
            return false;

        flag.ExpiresAtUtc = nowUtc.AddMinutes(-1);
        flag.LastRefreshedAtUtc = nowUtc;
        flag.ConsecutiveDetections = 0;
        _metrics?.MLAdwinFlagsCleared.Add(
            1,
            new KeyValuePair<string, object?>("symbol", model.Symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
        return true;
    }

    private static int ComputeMaxBatchedWindowSize(
        IReadOnlyList<ActiveModelCandidate> models,
        AdwinWorkerSettings settings,
        IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), (double? Delta, int? WindowSize)> overrides)
    {
        int max = settings.WindowSize;
        foreach (var m in models)
        {
            if (overrides.TryGetValue((m.Symbol, m.Timeframe), out var p) && p.WindowSize is int w && w > max)
                max = w;
        }
        return max;
    }

    private static IEnumerable<IReadOnlyList<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        if (size <= 0) size = 1;
        for (int i = 0; i < source.Count; i += size)
        {
            int len = Math.Min(size, source.Count - i);
            var chunk = new List<T>(len);
            for (int j = 0; j < len; j++) chunk.Add(source[i + j]);
            yield return chunk;
        }
    }

    private static byte[] CompressOutcomeSeries(bool[] outcomes)
    {
        var raw = new byte[outcomes.Length];
        for (int i = 0; i < outcomes.Length; i++)
            raw[i] = outcomes[i] ? (byte)1 : (byte)0;

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }
        return output.ToArray();
    }

    private async Task<bool> QueueRetrainingIfNeededAsync(
        DbContext db,
        ActiveModelCandidate model,
        AdwinWorkerSettings settings,
        double effectiveDelta,
        DateTime nowUtc,
        int windowSize,
        AdwinEvidence evidence,
        double accuracyDrop,
        DomainMarketRegime? dominantRegime,
        CancellationToken ct)
    {
        // Cooldown #1: an existing Queued/Running run for this pair already covers us.
        bool retrainingAlreadyActive = await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(run =>
                !run.IsDeleted &&
                run.Symbol == model.Symbol &&
                run.Timeframe == model.Timeframe &&
                (run.Status == RunStatus.Queued || run.Status == RunStatus.Running), ct);

        if (retrainingAlreadyActive)
            return false;

        // Cooldown #2: a recently completed (or failed) auto-degrading run is still
        // propagating through SPRT shadow evaluation. Re-queueing now would queue
        // duplicate work before we know whether the prior fix landed.
        if (settings.MinTimeBetweenRetrainsHours > 0)
        {
            var cooldownCutoff = nowUtc.AddHours(-settings.MinTimeBetweenRetrainsHours);
            bool recentRun = await db.Set<MLTrainingRun>()
                .AsNoTracking()
                .AnyAsync(run =>
                    !run.IsDeleted &&
                    run.Symbol == model.Symbol &&
                    run.Timeframe == model.Timeframe &&
                    run.DriftTriggerType == DriftTriggerType &&
                    (run.CompletedAt ?? run.StartedAt) >= cooldownCutoff, ct);

            if (recentRun)
            {
                _metrics?.MLAdwinRetrainCooldownSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                return false;
            }
        }

        db.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            TriggerType = TriggerType.AutoDegrading,
            Status = RunStatus.Queued,
            FromDate = nowUtc.AddDays(-settings.TrainingDataWindowDays),
            ToDate = nowUtc,
            StartedAt = nowUtc,
            LearnerArchitecture = model.LearnerArchitecture,
            DriftTriggerType = DriftTriggerType,
            DriftMetadataJson = JsonSerializer.Serialize(new
            {
                detector = "ADWIN",
                direction = "degradation",
                window1Mean = evidence.Window1Mean,
                window2Mean = evidence.Window2Mean,
                epsilonCut = evidence.EpsilonCut,
                splitPoint = evidence.SplitIndex,
                windowSize,
                delta = effectiveDelta,
                accuracyDrop,
                dominantRegime = dominantRegime?.ToString(),
            }),
            Priority = 1,
            IsDeleted = false
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Concurrent worker (or another auto-retrain trigger) won the race and inserted
            // its own Queued/Running row through the partial unique index. Treat as a
            // successful no-op: the retraining is happening, just not from us.
            db.ChangeTracker.Clear();
            _logger.LogInformation(
                "{Worker}: retrain queue race for {Symbol}/{Timeframe} resolved by partial unique index; another worker queued the run.",
                WorkerName, model.Symbol, model.Timeframe);
            return false;
        }

        _metrics?.MLAdwinRetrainingQueued.Add(
            1,
            new KeyValuePair<string, object?>("symbol", model.Symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("learner_architecture", model.LearnerArchitecture.ToString()));
        return true;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // Postgres SQLSTATE 23505 — unique_violation. Avoid a hard reference to Npgsql by
        // sniffing the SqlState property reflectively, falling back to a string match.
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            var sqlStateProp = cur.GetType().GetProperty("SqlState");
            if (sqlStateProp?.GetValue(cur) is string sqlState && sqlState == "23505")
                return true;
            if (cur.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
                cur.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Single-query batched fetch of the top-N resolved outcomes per model using
    /// <c>ROW_NUMBER() OVER (PARTITION BY MLModelId ORDER BY PredictedAt DESC)</c>.
    /// Returns one list per model in DESC-by-PredictedAt order. Falls back to per-model
    /// queries on non-relational providers (in-memory tests).
    /// </summary>
    private async Task<Dictionary<long, List<ResolvedOutcome>>> LoadOutcomesBatchedAsync(
        DbContext db,
        IReadOnlyList<long> modelIds,
        int maxWindowSize,
        DateTime cutoffUtc,
        CancellationToken ct)
    {
        if (modelIds.Count == 0)
            return new Dictionary<long, List<ResolvedOutcome>>();

        if (db.Database.IsRelational())
        {
            try
            {
                // IDs are typed long — safe to interpolate directly. Cutoff and N are bound
                // as parameters via {0}/{1}.
                var idsCsv = string.Join(",", modelIds);
                var sql = $@"
SELECT t.""MLModelId"" AS ""MLModelId"",
       t.""PredictedAt"" AS ""PredictedAt"",
       t.""DirectionCorrectInt"" AS ""DirectionCorrectInt""
FROM (
    SELECT ""MLModelId"",
           ""PredictedAt"",
           CASE WHEN ""DirectionCorrect"" THEN 1 ELSE 0 END AS ""DirectionCorrectInt"",
           ROW_NUMBER() OVER (PARTITION BY ""MLModelId"" ORDER BY ""PredictedAt"" DESC) AS rn
    FROM ""MLModelPredictionLog""
    WHERE ""MLModelId"" IN ({idsCsv})
      AND NOT ""IsDeleted""
      AND ""DirectionCorrect"" IS NOT NULL
      AND ""PredictedAt"" >= {{0}}
) t
WHERE t.rn <= {{1}}
ORDER BY t.""MLModelId"", t.""PredictedAt"" DESC";

                var rows = await db.Database
                    .SqlQueryRaw<BatchedOutcomeRow>(sql, cutoffUtc, maxWindowSize)
                    .ToListAsync(ct);

                var byModel = new Dictionary<long, List<ResolvedOutcome>>(modelIds.Count);
                foreach (var row in rows)
                {
                    if (!byModel.TryGetValue(row.MLModelId, out var list))
                    {
                        list = new List<ResolvedOutcome>();
                        byModel[row.MLModelId] = list;
                    }
                    list.Add(new ResolvedOutcome(row.PredictedAt, row.DirectionCorrectInt != 0));
                }
                return byModel;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex,
                    "{Worker}: batched outcome query failed; falling back to per-model fetch.",
                    WorkerName);
                // Fall through to per-model.
            }
        }

        var fallback = new Dictionary<long, List<ResolvedOutcome>>(modelIds.Count);
        foreach (var id in modelIds)
        {
            var logs = await db.Set<MLModelPredictionLog>()
                .AsNoTracking()
                .Where(log =>
                    log.MLModelId == id &&
                    !log.IsDeleted &&
                    log.DirectionCorrect.HasValue &&
                    log.PredictedAt >= cutoffUtc)
                .OrderByDescending(log => log.PredictedAt)
                .Take(maxWindowSize)
                .Select(log => new ResolvedOutcome(log.PredictedAt, log.DirectionCorrect == true))
                .ToListAsync(ct);
            fallback[id] = logs;
        }
        return fallback;
    }

    /// <summary>
    /// Single-query fetch of all <see cref="MLDriftFlag"/> rows (including soft-deleted) for
    /// the AdwinDrift detector, then in-memory filter to the active models' pairs.
    /// </summary>
    private static async Task<Dictionary<(string Symbol, Timeframe Timeframe), MLDriftFlag>> LoadFlagsBatchedAsync(
        DbContext db,
        IReadOnlyList<ActiveModelCandidate> models,
        CancellationToken ct)
    {
        var pairSet = new HashSet<(string, Timeframe)>();
        foreach (var m in models)
            pairSet.Add((m.Symbol, m.Timeframe));

        var allFlags = await LoadAllFlagsAsync(db, ct);

        var dict = new Dictionary<(string Symbol, Timeframe Timeframe), MLDriftFlag>();
        foreach (var f in allFlags)
        {
            if (pairSet.Contains((f.Symbol, f.Timeframe)))
                dict[(f.Symbol, f.Timeframe)] = f;
        }
        return dict;
    }

    private static Task<List<MLDriftFlag>> LoadAllFlagsAsync(DbContext db, CancellationToken ct)
        => db.Set<MLDriftFlag>()
            .IgnoreQueryFilters()
            .Where(f => f.DetectorType == DriftDetectorType)
            .ToListAsync(ct);

    /// <summary>
    /// Single-query fetch of recent <see cref="MarketRegimeSnapshot"/> rows for the active
    /// models, deduped in-memory to "latest per (Symbol, Timeframe)". Provider-agnostic.
    /// </summary>
    private static async Task<Dictionary<(string Symbol, Timeframe Timeframe), DomainMarketRegime>> LoadDominantRegimesBatchedAsync(
        DbContext db,
        IReadOnlyList<ActiveModelCandidate> models,
        DateTime cutoffUtc,
        CancellationToken ct)
    {
        var distinctSymbols = models.Select(m => m.Symbol).Distinct().ToList();
        if (distinctSymbols.Count == 0)
            return new Dictionary<(string, Timeframe), DomainMarketRegime>();

        var pairSet = new HashSet<(string, Timeframe)>();
        foreach (var m in models)
            pairSet.Add((m.Symbol, m.Timeframe));

        var snapshots = await db.Set<MarketRegimeSnapshot>()
            .AsNoTracking()
            .Where(s =>
                distinctSymbols.Contains(s.Symbol) &&
                !s.IsDeleted &&
                s.DetectedAt >= cutoffUtc)
            .OrderByDescending(s => s.DetectedAt)
            .Select(s => new { s.Symbol, s.Timeframe, s.Regime })
            .ToListAsync(ct);

        var dict = new Dictionary<(string Symbol, Timeframe Timeframe), DomainMarketRegime>();
        foreach (var s in snapshots)
        {
            var key = (s.Symbol, s.Timeframe);
            if (!pairSet.Contains(key))
                continue;
            // First match wins because we sorted DESC by DetectedAt.
            dict.TryAdd(key, s.Regime);
        }
        return dict;
    }

    private async Task DispatchDriftAlertAsync(
        ActiveModelCandidate model,
        AdwinScanResult scan,
        double delta,
        int windowSize,
        DomainMarketRegime? dominantRegime,
        bool retrainingQueued,
        CancellationToken ct)
    {
        if (_alertDispatcher is null)
            return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

            int cooldownSec = await AlertCooldownDefaults.GetCooldownAsync(
                writeCtx, AlertCooldownDefaults.CK_MLDrift, AlertCooldownDefaults.Default_MLDrift, ct);

            string dedupKey = $"adwin-drift:{model.Symbol}:{model.Timeframe}:{DriftDetectorType}";

            string conditionJson = JsonSerializer.Serialize(new
            {
                detector = "ADWIN",
                modelId = model.Id,
                symbol = model.Symbol,
                timeframe = model.Timeframe.ToString(),
                learnerArchitecture = model.LearnerArchitecture.ToString(),
                window1Mean = scan.SelectedEvidence.Window1Mean,
                window2Mean = scan.SelectedEvidence.Window2Mean,
                epsilonCut = scan.SelectedEvidence.EpsilonCut,
                accuracyDrop = scan.AccuracyDrop,
                splitPoint = scan.SelectedEvidence.SplitIndex,
                windowSize,
                delta,
                dominantRegime = dominantRegime?.ToString(),
                retrainingQueued,
                detectedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            });

            var alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                Severity = AlertSeverity.Medium,
                Symbol = model.Symbol,
                DeduplicationKey = dedupKey,
                CooldownSeconds = cooldownSec,
                ConditionJson = conditionJson,
                IsActive = true,
            };

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "ADWIN drift on {0}/{1} (model {2}, {3}): mu1={4:F4} -> mu2={5:F4} (drop {6:F4}, eps {7:F4}, n {8}); retrainQueued={9}.",
                model.Symbol,
                model.Timeframe,
                model.Id,
                model.LearnerArchitecture,
                scan.SelectedEvidence.Window1Mean,
                scan.SelectedEvidence.Window2Mean,
                scan.AccuracyDrop,
                scan.SelectedEvidence.EpsilonCut,
                windowSize,
                retrainingQueued);

            await _alertDispatcher.DispatchAsync(alert, message, ct);

            _metrics?.MLAdwinAlertsDispatched.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Worker}: failed to dispatch ADWIN drift alert for model {ModelId} ({Symbol}/{Timeframe}).",
                WorkerName, model.Id, model.Symbol, model.Timeframe);
        }
    }

    private async Task<AdwinWorkerSettings> LoadSettingsAsync(
        DbContext db,
        CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled,
            CK_PollSecs,
            CK_WindowSize,
            CK_MinResolvedPredictions,
            CK_Delta,
            CK_LookbackDays,
            CK_FlagTtlHours,
            CK_MaxModelsPerCycle,
            CK_LockTimeoutSecs,
            CK_TrainingDays,
            CK_MinTimeBetweenRetrainsHours,
            CK_SnapshotOutcomeSeries,
            CK_DbCommandTimeoutSecs,
            CK_SaveBatchSize,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, config => config.Value, ct);

        int windowSize = ClampInt(
            GetInt(values, CK_WindowSize, DefaultWindowSize),
            DefaultWindowSize,
            MinWindowSize,
            MaxWindowSize);

        int minResolvedPredictions = Math.Min(
            ClampInt(
                GetInt(values, CK_MinResolvedPredictions, DefaultMinResolvedPredictions),
                DefaultMinResolvedPredictions,
                MinMinResolvedPredictions,
                MaxMinResolvedPredictions),
            windowSize);

        return new AdwinWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, true),
            PollInterval: TimeSpan.FromSeconds(
                ClampInt(GetInt(values, CK_PollSecs, DefaultPollSeconds), DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            WindowSize: windowSize,
            MinResolvedPredictions: minResolvedPredictions,
            Delta: ClampDouble(GetDouble(values, CK_Delta, DefaultDelta), DefaultDelta, MinDelta, MaxDelta),
            LookbackDays: ClampInt(GetInt(values, CK_LookbackDays, DefaultLookbackDays), DefaultLookbackDays, MinLookbackDays, MaxLookbackDays),
            FlagTtlHours: ClampInt(GetInt(values, CK_FlagTtlHours, DefaultFlagTtlHours), DefaultFlagTtlHours, MinFlagTtlHours, MaxFlagTtlHours),
            MaxModelsPerCycle: ClampInt(GetInt(values, CK_MaxModelsPerCycle, DefaultMaxModelsPerCycle), DefaultMaxModelsPerCycle, MinMaxModelsPerCycle, MaxMaxModelsPerCycle),
            LockTimeoutSeconds: ClampInt(GetInt(values, CK_LockTimeoutSecs, DefaultLockTimeoutSeconds), DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            TrainingDataWindowDays: ClampInt(GetInt(values, CK_TrainingDays, DefaultTrainingDataWindowDays), DefaultTrainingDataWindowDays, MinTrainingDataWindowDays, MaxTrainingDataWindowDays),
            MinTimeBetweenRetrainsHours: ClampNonNegativeInt(
                GetInt(values, CK_MinTimeBetweenRetrainsHours, DefaultMinTimeBetweenRetrainsHours),
                DefaultMinTimeBetweenRetrainsHours,
                MinMinTimeBetweenRetrainsHours,
                MaxMinTimeBetweenRetrainsHours),
            SnapshotOutcomeSeries: GetBool(values, CK_SnapshotOutcomeSeries, true),
            DbCommandTimeoutSeconds: ClampInt(
                GetInt(values, CK_DbCommandTimeoutSecs, DefaultDbCommandTimeoutSeconds),
                DefaultDbCommandTimeoutSeconds,
                MinDbCommandTimeoutSeconds,
                MaxDbCommandTimeoutSeconds),
            SaveBatchSize: ClampInt(
                GetInt(values, CK_SaveBatchSize, DefaultSaveBatchSize),
                DefaultSaveBatchSize,
                MinSaveBatchSize,
                MaxSaveBatchSize));
    }

    /// <summary>
    /// Single-query fetch of all per-pair overrides keyed under <c>MLAdwinDrift:Override:</c>,
    /// parsed and clamped to safe ranges. Replaces the previous per-model overrides query.
    /// </summary>
    private static async Task<Dictionary<(string Symbol, Timeframe Timeframe), (double? Delta, int? WindowSize)>> LoadAllPerPairOverridesAsync(
        DbContext db,
        CancellationToken ct)
    {
        var rows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith(CK_OverridePrefix))
            .Select(c => new { c.Key, c.Value })
            .ToListAsync(ct);

        var dict = new Dictionary<(string Symbol, Timeframe Timeframe), (double? Delta, int? WindowSize)>();
        foreach (var row in rows)
        {
            // Format: MLAdwinDrift:Override:{Symbol}:{Timeframe}:{Delta|WindowSize}
            var rest = row.Key.Substring(CK_OverridePrefix.Length);
            var parts = rest.Split(':');
            if (parts.Length != 3)
                continue;

            string symbol = parts[0];
            if (!Enum.TryParse<Timeframe>(parts[1], ignoreCase: false, out var timeframe))
                continue;

            string field = parts[2];
            var key = (symbol, timeframe);
            dict.TryGetValue(key, out var current);

            if (field == "Delta" &&
                double.TryParse(row.Value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var delta) &&
                double.IsFinite(delta) && delta >= MinDelta && delta <= MaxDelta)
            {
                current.Delta = delta;
            }
            else if (field == "WindowSize" &&
                int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var windowSize) &&
                windowSize >= MinWindowSize && windowSize <= MaxWindowSize)
            {
                current.WindowSize = windowSize;
            }
            else
            {
                continue;
            }

            dict[key] = current;
        }
        return dict;
    }

    private static bool GetBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (bool.TryParse(raw, out var parsedBool))
            return parsedBool;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            return parsedInt != 0;

        return defaultValue;
    }

    private static int GetInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int defaultValue)
    {
        return values.TryGetValue(key, out var raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static double GetDouble(
        IReadOnlyDictionary<string, string> values,
        string key,
        double defaultValue)
    {
        return values.TryGetValue(key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static int ClampInt(int value, int fallback, int min, int max)
    {
        if (value <= 0)
            return fallback;

        return Math.Min(Math.Max(value, min), max);
    }

    private static int ClampNonNegativeInt(int value, int fallback, int min, int max)
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

    internal readonly record struct AdwinWorkerSettings(
        bool Enabled,
        TimeSpan PollInterval,
        int WindowSize,
        int MinResolvedPredictions,
        double Delta,
        int LookbackDays,
        int FlagTtlHours,
        int MaxModelsPerCycle,
        int LockTimeoutSeconds,
        int TrainingDataWindowDays,
        int MinTimeBetweenRetrainsHours,
        bool SnapshotOutcomeSeries,
        int DbCommandTimeoutSeconds,
        int SaveBatchSize);

    internal readonly record struct AdwinCycleResult(
        AdwinWorkerSettings Settings,
        string? SkippedReason,
        int CandidateModelCount,
        int EvaluatedModelCount,
        int DriftCount,
        int RetrainingQueuedCount,
        int FlagClearCount,
        int FailedModelCount)
    {
        public static AdwinCycleResult Skipped(
            AdwinWorkerSettings settings,
            string reason)
            => new(
                settings,
                reason,
                CandidateModelCount: 0,
                EvaluatedModelCount: 0,
                DriftCount: 0,
                RetrainingQueuedCount: 0,
                FlagClearCount: 0,
                FailedModelCount: 0);
    }
}
