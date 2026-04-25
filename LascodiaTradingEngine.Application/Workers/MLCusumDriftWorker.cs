using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects bidirectional accuracy drift in active ML models using the Cumulative Sum
/// (CUSUM) control chart algorithm — a classical sequential hypothesis test optimised
/// for detecting <em>sudden, step-change</em> shifts in a process mean.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm — two one-sided CUSUM accumulators:</b>
/// <list type="bullet">
///   <item><b>S⁺ (degradation detector):</b> <c>S⁺ₙ = max(0, S⁺ₙ₋₁ + (μ₀ − xₙ) − k)</c>; fires when S⁺ ≥ h.</item>
///   <item><b>S⁻ (improvement detector):</b> <c>S⁻ₙ = max(0, S⁻ₙ₋₁ + (xₙ − μ₀) − k)</c>; logged but does not fire alerts.</item>
/// </list>
/// where <c>μ₀</c> is estimated from the first half of the window, <c>k</c> is the
/// allowable slack, and <c>h</c> is the alarm threshold.
/// </para>
/// <para>
/// <b>Hardening:</b> distributed lock with TTL, retrain cooldown (12 h default),
/// unique-violation handling on retrain insert, command timeout, exponential retry
/// backoff, sub-cycle hot-reload polling, dispatched alerts via
/// <see cref="IAlertDispatcher"/>, and tagged metrics. Mirrors
/// <see cref="MLAdwinDriftWorker"/>'s operational pattern.
/// </para>
/// </remarks>
public sealed class MLCusumDriftWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLCusumDriftWorker);
    private const string DriftDetectorName = "CUSUM";
    private const string DriftTriggerType = "CusumDrift";
    private const string DistributedLockKey = "workers:ml-cusum-drift:cycle";

    private const string CK_Enabled         = "MLCusum:Enabled";
    private const string CK_PollSecs        = "MLCusum:PollIntervalSeconds";
    private const string CK_Window          = "MLCusum:WindowSize";
    private const string CK_K               = "MLCusum:K";
    private const string CK_H               = "MLCusum:H";
    private const string CK_MinResolved     = "MLCusum:MinResolvedPredictions";
    private const string CK_MaxModelsPerCycle = "MLCusum:MaxModelsPerCycle";
    private const string CK_LockTimeoutSecs = "MLCusum:LockTimeoutSeconds";
    private const string CK_MinTimeBetweenRetrainsHours = "MLCusum:MinTimeBetweenRetrainsHours";
    private const string CK_TrainingDays    = "MLTraining:TrainingDataWindowDays";
    private const string CK_DbCommandTimeoutSecs = "MLCusum:DbCommandTimeoutSeconds";

    private const int DefaultPollSeconds = 3600;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 7 * 24 * 60 * 60;

    private const int DefaultWindowSize = 300;
    private const int MinWindowSize = 60;
    private const int MaxWindowSize = 5000;

    private const double DefaultK = 0.005;
    private const double MinK = 0.0;
    private const double MaxK = 0.5;

    private const double DefaultH = 5.0;
    private const double MinH = 0.5;
    private const double MaxH = 100.0;

    private const int DefaultMinResolved = 30;
    private const int MinMinResolved = 30;
    private const int MaxMinResolved = 5000;

    private const int DefaultMaxModelsPerCycle = 256;
    private const int MinMaxModelsPerCycle = 1;
    private const int MaxMaxModelsPerCycle = 4096;

    private const int DefaultLockTimeoutSeconds = 5;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private const int DefaultMinTimeBetweenRetrainsHours = 12;
    private const int MinMinTimeBetweenRetrainsHours = 0;
    private const int MaxMinTimeBetweenRetrainsHours = 24 * 30;

    private const int DefaultTrainingDataWindowDays = 365;
    private const int MinTrainingDataWindowDays = 30;
    private const int MaxTrainingDataWindowDays = 3650;

    private const int DefaultDbCommandTimeoutSeconds = 60;
    private const int MinDbCommandTimeoutSeconds = 5;
    private const int MaxDbCommandTimeoutSeconds = 600;

    private static readonly TimeSpan WakeInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLCusumDriftWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IAlertDispatcher? _alertDispatcher;

    private long _consecutiveFailuresField;
    private int _missingDistributedLockWarningEmitted;

    public MLCusumDriftWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLCusumDriftWorker> logger,
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
            "Detects sudden step-change accuracy drift via the CUSUM control chart and queues retraining on confirmed degradation.",
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
                    _metrics?.MLCusumTimeSinceLastSuccessSec.Record(
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
                        _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                        _metrics?.WorkerCycleDurationMs.Record(
                            durationMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.MLCusumCycleDurationMs.Record(durationMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: candidates={Candidates}, evaluated={Evaluated}, drifts={Drifts}, retrain queued={Queued}.",
                                WorkerName, result.CandidateModelCount, result.EvaluatedModelCount,
                                result.DriftsDetected, result.RetrainingQueued);
                        }

                        var prevFailures = ConsecutiveFailures;
                        if (prevFailures > 0)
                        {
                            _healthMonitor?.RecordRecovery(WorkerName, prevFailures);
                            _logger.LogInformation(
                                "{Worker}: recovered after {Failures} consecutive failure(s).",
                                WorkerName, prevFailures);
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
                            new KeyValuePair<string, object?>("reason", "ml_cusum_cycle"));
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

    internal async Task<CusumCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var ctx = readCtx.GetDbContext();
        var writeDb = writeCtx.GetDbContext();
        var settings = await LoadSettingsAsync(ctx, ct);

        ApplyCommandTimeout(ctx, settings.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeDb, settings.DbCommandTimeoutSeconds);

        if (!settings.Enabled)
        {
            _metrics?.MLCusumCyclesSkipped.Add(
                1, new KeyValuePair<string, object?>("reason", "disabled"));
            return CusumCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.MLCusumLockAttempts.Add(
                1, new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate CUSUM cycles are possible in multi-instance deployments.",
                    WorkerName);
            }
            return await RunCycleCoreAsync(ctx, writeDb, settings, ct);
        }

        var cycleLock = await _distributedLock.TryAcquireAsync(
            DistributedLockKey,
            TimeSpan.FromSeconds(settings.LockTimeoutSeconds),
            ct);

        if (cycleLock is null)
        {
            _metrics?.MLCusumLockAttempts.Add(
                1, new KeyValuePair<string, object?>("outcome", "busy"));
            _metrics?.MLCusumCyclesSkipped.Add(
                1, new KeyValuePair<string, object?>("reason", "lock_busy"));
            return CusumCycleResult.Skipped(settings, "lock_busy");
        }

        _metrics?.MLCusumLockAttempts.Add(
            1, new KeyValuePair<string, object?>("outcome", "acquired"));

        await using (cycleLock)
        {
            return await RunCycleCoreAsync(ctx, writeDb, settings, ct);
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
        try
        {
            if (db.Database.IsRelational())
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(seconds));
        }
        catch (InvalidOperationException) { /* provider lacks support */ }
    }

    private async Task<CusumCycleResult> RunCycleCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CusumWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var activeModels = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted)
            .OrderBy(m => m.Id)
            .Select(m => new ActiveModelCandidate(m.Id, m.Symbol, m.Timeframe, m.LearnerArchitecture))
            .Take(settings.MaxModelsPerCycle)
            .ToListAsync(ct);

        int evaluated = 0, drifts = 0, retrainQueued = 0;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                _metrics?.MLCusumModelsEvaluated.Add(
                    1,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                    new KeyValuePair<string, object?>("learner_architecture", model.LearnerArchitecture.ToString()));

                var outcome = await EvaluateModelAsync(model, readCtx, writeCtx, settings, nowUtc, ct);
                if (outcome.Evaluated) evaluated++;
                if (outcome.DriftDetected) drifts++;
                if (outcome.RetrainQueued) retrainQueued++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                writeCtx.ChangeTracker.Clear();
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName),
                    new KeyValuePair<string, object?>("reason", "ml_cusum_model"),
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                _logger.LogWarning(ex,
                    "{Worker}: CUSUM check failed for model {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    WorkerName, model.Id, model.Symbol, model.Timeframe);
            }
        }

        return new CusumCycleResult(
            settings,
            SkippedReason: null,
            CandidateModelCount: activeModels.Count,
            EvaluatedModelCount: evaluated,
            DriftsDetected: drifts,
            RetrainingQueued: retrainQueued);
    }

    private async Task<ModelEvalOutcome> EvaluateModelAsync(
        ActiveModelCandidate model,
        DbContext readCtx,
        DbContext writeCtx,
        CusumWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l =>
                l.MLModelId == model.Id &&
                l.DirectionCorrect != null &&
                !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)
            .Take(settings.WindowSize)
            .Select(l => l.DirectionCorrect == true)
            .ToListAsync(ct);

        if (logs.Count < settings.MinResolvedPredictions)
        {
            _metrics?.MLCusumModelsSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "insufficient_history"),
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
            return ModelEvalOutcome.Skipped;
        }

        // Reverse to chronological order (oldest → newest).
        logs.Reverse();

        var scan = ComputeCusum(logs, settings.K, settings.H);

        if (!scan.Fired)
        {
            _logger.LogDebug(
                "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) — refAcc={Ref:F4}, S+={SPlus:F2}, S-={SMinus:F2}, h={H:F1} — no drift.",
                WorkerName, model.Id, model.Symbol, model.Timeframe,
                scan.ReferenceAccuracy, scan.SPlus, scan.SMinus, settings.H);
            return ModelEvalOutcome.EvaluatedNoDrift;
        }

        // Cooldown #1: existing Queued/Running run for the pair.
        bool retrainAlreadyActive = await writeCtx.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(r =>
                !r.IsDeleted &&
                r.Symbol == model.Symbol &&
                r.Timeframe == model.Timeframe &&
                (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);

        if (retrainAlreadyActive)
        {
            _logger.LogDebug(
                "{Worker}: drift detected for {Symbol}/{Timeframe} but retrain already queued/running.",
                WorkerName, model.Symbol, model.Timeframe);
            await DispatchDriftAlertAsync(model, scan, settings, retrainQueued: false, ct);
            return new ModelEvalOutcome(true, true, false);
        }

        // Cooldown #2: a recently-completed CUSUM-triggered run is still propagating.
        if (settings.MinTimeBetweenRetrainsHours > 0)
        {
            var cutoff = nowUtc.AddHours(-settings.MinTimeBetweenRetrainsHours);
            bool recentRun = await writeCtx.Set<MLTrainingRun>()
                .AsNoTracking()
                .AnyAsync(r =>
                    !r.IsDeleted &&
                    r.Symbol == model.Symbol &&
                    r.Timeframe == model.Timeframe &&
                    r.DriftTriggerType == DriftTriggerType &&
                    (r.CompletedAt ?? r.StartedAt) >= cutoff, ct);
            if (recentRun)
            {
                _metrics?.MLCusumRetrainCooldownSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                _logger.LogDebug(
                    "{Worker}: drift detected for {Symbol}/{Timeframe} but inside retrain cooldown.",
                    WorkerName, model.Symbol, model.Timeframe);
                await DispatchDriftAlertAsync(model, scan, settings, retrainQueued: false, ct);
                return new ModelEvalOutcome(true, true, false);
            }
        }

        // Queue the retrain. Falls through unique-violation to "another worker won".
        bool queued = await TryQueueRetrainAsync(model, scan, settings, nowUtc, writeCtx, ct);

        _metrics?.MLCusumDriftsDetected.Add(
            1,
            new KeyValuePair<string, object?>("symbol", model.Symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("learner_architecture", model.LearnerArchitecture.ToString()));

        _logger.LogWarning(
            "{Worker}: CUSUM degradation drift for {Symbol}/{Timeframe} — refAcc={Ref:P1}, recentAcc={Recent:P1}, S+={SPlus:F2} ≥ h={H:F1} at step {Step}/{Total}. retrainQueued={Queued}.",
            WorkerName, model.Symbol, model.Timeframe,
            scan.ReferenceAccuracy, scan.RecentAccuracy, scan.SPlus, settings.H,
            scan.FireStep, scan.MonitoringSteps, queued);

        await DispatchDriftAlertAsync(model, scan, settings, retrainQueued: queued, ct);

        return new ModelEvalOutcome(true, true, queued);
    }

    private async Task<bool> TryQueueRetrainAsync(
        ActiveModelCandidate model,
        CusumScan scan,
        CusumWorkerSettings settings,
        DateTime nowUtc,
        DbContext writeCtx,
        CancellationToken ct)
    {
        writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
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
                detector = DriftDetectorName,
                referenceAccuracy = scan.ReferenceAccuracy,
                recentAccuracy = scan.RecentAccuracy,
                sPlus = scan.SPlus,
                sMinus = scan.SMinus,
                k = settings.K,
                h = settings.H,
                fireStep = scan.FireStep,
                monitoringSteps = scan.MonitoringSteps,
            }),
            Priority = 1,
            IsDeleted = false,
        });

        try
        {
            await writeCtx.SaveChangesAsync(ct);
            _metrics?.MLCusumRetrainingQueued.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
            return true;
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            writeCtx.ChangeTracker.Clear();
            _logger.LogInformation(
                "{Worker}: retrain queue race for {Symbol}/{Timeframe} resolved by partial unique index; another worker queued the run.",
                WorkerName, model.Symbol, model.Timeframe);
            return false;
        }
    }


    private async Task DispatchDriftAlertAsync(
        ActiveModelCandidate model,
        CusumScan scan,
        CusumWorkerSettings settings,
        bool retrainQueued,
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

            string conditionJson = JsonSerializer.Serialize(new
            {
                DetectorType = DriftDetectorName,
                ModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe.ToString(),
                LearnerArchitecture = model.LearnerArchitecture.ToString(),
                ReferenceAcc = scan.ReferenceAccuracy,
                RecentAcc = scan.RecentAccuracy,
                CusumSPlus = scan.SPlus,
                DecisionInterval = settings.H,
                SlackK = settings.K,
                FireStep = scan.FireStep,
                MonitoringSteps = scan.MonitoringSteps,
                RetrainingQueued = retrainQueued,
                DetectedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            });

            var alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                Severity = AlertSeverity.Medium,
                Symbol = model.Symbol,
                DeduplicationKey = $"cusum-drift:{model.Symbol}:{model.Timeframe}",
                CooldownSeconds = cooldownSec,
                ConditionJson = conditionJson,
                IsActive = true,
            };

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "CUSUM drift on {0}/{1}: refAcc {2:F4} → recentAcc {3:F4} (S+ {4:F2} ≥ h {5:F1}); retrainQueued={6}.",
                model.Symbol, model.Timeframe, scan.ReferenceAccuracy, scan.RecentAccuracy,
                scan.SPlus, settings.H, retrainQueued);

            await _alertDispatcher.DispatchAsync(alert, message, ct);
            _metrics?.MLCusumAlertsDispatched.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Worker}: failed to dispatch CUSUM drift alert for {ModelId} ({Symbol}/{Timeframe}).",
                WorkerName, model.Id, model.Symbol, model.Timeframe);
        }
    }

    /// <summary>
    /// Pure-CPU CUSUM detector: estimates the reference accuracy from the first half of
    /// <paramref name="outcomes"/> and runs the two one-sided CUSUM accumulators over the
    /// second half. Returns when S⁺ ≥ <paramref name="h"/> (degradation alarm) or when
    /// the monitoring window is exhausted with no alarm.
    /// </summary>
    internal static CusumScan ComputeCusum(IReadOnlyList<bool> outcomes, double k, double h)
    {
        int n = outcomes.Count;
        int refHalf = n / 2;
        int correct = 0;
        for (int i = 0; i < refHalf; i++)
            if (outcomes[i]) correct++;
        double refAcc = correct / (double)refHalf;

        double sPlus = 0, sMinus = 0;
        bool fired = false;
        int fireStep = -1;
        int monitorCorrect = 0;

        int monitoringSteps = n - refHalf;
        for (int i = 0; i < monitoringSteps; i++)
        {
            double x = outcomes[refHalf + i] ? 1.0 : 0.0;
            if (outcomes[refHalf + i]) monitorCorrect++;

            sPlus = Math.Max(0, sPlus + (refAcc - x) - k);
            sMinus = Math.Max(0, sMinus + (x - refAcc) - k);

            if (sPlus >= h && !fired)
            {
                fired = true;
                fireStep = i + 1;
                break;
            }
        }

        double recentAcc = fired
            ? monitorCorrect / (double)fireStep
            : (monitoringSteps == 0 ? 0.0 : monitorCorrect / (double)monitoringSteps);

        return new CusumScan(refAcc, recentAcc, sPlus, sMinus, fired, fireStep, monitoringSteps);
    }

    private static async Task<CusumWorkerSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled, CK_PollSecs, CK_Window, CK_K, CK_H, CK_MinResolved,
            CK_MaxModelsPerCycle, CK_LockTimeoutSecs, CK_MinTimeBetweenRetrainsHours,
            CK_TrainingDays, CK_DbCommandTimeoutSecs,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => keys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        int windowSize = ClampInt(GetInt(values, CK_Window, DefaultWindowSize), DefaultWindowSize, MinWindowSize, MaxWindowSize);
        int minResolved = Math.Min(
            ClampInt(GetInt(values, CK_MinResolved, DefaultMinResolved), DefaultMinResolved, MinMinResolved, MaxMinResolved),
            windowSize);

        return new CusumWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, true),
            PollInterval: TimeSpan.FromSeconds(
                ClampInt(GetInt(values, CK_PollSecs, DefaultPollSeconds), DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            WindowSize: windowSize,
            K: ClampDouble(GetDouble(values, CK_K, DefaultK), DefaultK, MinK, MaxK, allowZero: true),
            H: ClampDouble(GetDouble(values, CK_H, DefaultH), DefaultH, MinH, MaxH, allowZero: false),
            MinResolvedPredictions: minResolved,
            MaxModelsPerCycle: ClampInt(GetInt(values, CK_MaxModelsPerCycle, DefaultMaxModelsPerCycle), DefaultMaxModelsPerCycle, MinMaxModelsPerCycle, MaxMaxModelsPerCycle),
            LockTimeoutSeconds: ClampInt(GetInt(values, CK_LockTimeoutSecs, DefaultLockTimeoutSeconds), DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            MinTimeBetweenRetrainsHours: ClampNonNegativeInt(
                GetInt(values, CK_MinTimeBetweenRetrainsHours, DefaultMinTimeBetweenRetrainsHours),
                DefaultMinTimeBetweenRetrainsHours,
                MinMinTimeBetweenRetrainsHours,
                MaxMinTimeBetweenRetrainsHours),
            TrainingDataWindowDays: ClampInt(GetInt(values, CK_TrainingDays, DefaultTrainingDataWindowDays), DefaultTrainingDataWindowDays, MinTrainingDataWindowDays, MaxTrainingDataWindowDays),
            DbCommandTimeoutSeconds: ClampInt(GetInt(values, CK_DbCommandTimeoutSecs, DefaultDbCommandTimeoutSeconds), DefaultDbCommandTimeoutSeconds, MinDbCommandTimeoutSeconds, MaxDbCommandTimeoutSeconds));
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (bool.TryParse(raw, out var parsedBool)) return parsedBool;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            return parsedInt != 0;
        return defaultValue;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
        => values.TryGetValue(key, out var raw) &&
           int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : defaultValue;

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double defaultValue)
        => values.TryGetValue(key, out var raw) &&
           double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : defaultValue;

    private static int ClampInt(int value, int fallback, int min, int max)
        => value <= 0 ? fallback : Math.Min(Math.Max(value, min), max);

    private static int ClampNonNegativeInt(int value, int fallback, int min, int max)
        => value < 0 ? fallback : Math.Min(Math.Max(value, min), max);

    private static double ClampDouble(double value, double fallback, double min, double max, bool allowZero)
    {
        if (!double.IsFinite(value)) return fallback;
        if (!allowZero && value <= 0.0) return fallback;
        if (allowZero && value < 0.0) return fallback;
        return Math.Min(Math.Max(value, min), max);
    }

    private readonly record struct ActiveModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        LearnerArchitecture LearnerArchitecture);

    private readonly record struct ModelEvalOutcome(bool Evaluated, bool DriftDetected, bool RetrainQueued)
    {
        public static readonly ModelEvalOutcome Skipped = new(false, false, false);
        public static readonly ModelEvalOutcome EvaluatedNoDrift = new(true, false, false);
    }

    internal readonly record struct CusumScan(
        double ReferenceAccuracy,
        double RecentAccuracy,
        double SPlus,
        double SMinus,
        bool Fired,
        int FireStep,
        int MonitoringSteps);

    internal readonly record struct CusumWorkerSettings(
        bool Enabled,
        TimeSpan PollInterval,
        int WindowSize,
        double K,
        double H,
        int MinResolvedPredictions,
        int MaxModelsPerCycle,
        int LockTimeoutSeconds,
        int MinTimeBetweenRetrainsHours,
        int TrainingDataWindowDays,
        int DbCommandTimeoutSeconds);

    internal readonly record struct CusumCycleResult(
        CusumWorkerSettings Settings,
        string? SkippedReason,
        int CandidateModelCount,
        int EvaluatedModelCount,
        int DriftsDetected,
        int RetrainingQueued)
    {
        public static CusumCycleResult Skipped(CusumWorkerSettings settings, string reason)
            => new(settings, reason, 0, 0, 0, 0);
    }
}
