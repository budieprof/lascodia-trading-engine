using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects two qualitatively different ML degradation patterns by comparing direction
/// accuracy across two simultaneous rolling windows: a fast/short window for sudden
/// drift and a slow/long window for gradual drift.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sudden drift:</b> <c>shortAccuracy − longAccuracy &lt; −ShortLongAccuracyGap</c>
/// (default −0.07). Fires <see cref="AlertSeverity.Critical"/> alerts.<br/>
/// <b>Gradual drift</b> (only if not already sudden): <c>longAccuracy &lt; LongWindowFloor</c>
/// (default 0.50). Fires <see cref="AlertSeverity.High"/> alerts.
/// </para>
/// <para>
/// <b>Hardening:</b> distributed lock with TTL, retrain cooldown (12 h default),
/// unique-violation handling, command timeout, exponential retry backoff, sub-cycle
/// hot-reload polling, dispatched alerts via <see cref="IAlertDispatcher"/>, and tagged
/// metrics. Mirrors <see cref="MLAdwinDriftWorker"/>'s operational pattern.
/// </para>
/// </remarks>
public sealed class MLMultiScaleDriftWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLMultiScaleDriftWorker);
    private const string DriftDetectorName = "MultiSignal";
    private const string DistributedLockKey = "workers:ml-multiscale-drift:cycle";

    private const string CK_Enabled            = "MLMultiScaleDrift:Enabled";
    private const string CK_PollSecs           = "MLMultiScaleDrift:PollIntervalSeconds";
    private const string CK_ShortWindowDays    = "MLMultiScaleDrift:ShortWindowDays";
    private const string CK_LongWindowDays     = "MLMultiScaleDrift:LongWindowDays";
    private const string CK_MinPredictions     = "MLMultiScaleDrift:MinPredictions";
    private const string CK_ShortLongGap       = "MLMultiScaleDrift:ShortLongAccuracyGap";
    private const string CK_LongWindowFloor    = "MLMultiScaleDrift:LongWindowFloor";
    private const string CK_MaxModelsPerCycle  = "MLMultiScaleDrift:MaxModelsPerCycle";
    private const string CK_LockTimeoutSecs    = "MLMultiScaleDrift:LockTimeoutSeconds";
    private const string CK_MinTimeBetweenRetrainsHours = "MLMultiScaleDrift:MinTimeBetweenRetrainsHours";
    private const string CK_TrainingDays       = "MLTraining:TrainingDataWindowDays";
    private const string CK_DbCommandTimeoutSecs = "MLMultiScaleDrift:DbCommandTimeoutSeconds";

    private const int DefaultPollSeconds = 1800;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 24 * 60 * 60;

    private const int DefaultShortWindowDays = 3;
    private const int MinShortWindowDays = 1;
    private const int MaxShortWindowDays = 30;

    private const int DefaultLongWindowDays = 21;
    private const int MinLongWindowDays = 3;
    private const int MaxLongWindowDays = 365;

    private const int DefaultMinPredictions = 20;
    private const int MinMinPredictions = 5;
    private const int MaxMinPredictions = 5000;

    private const double DefaultShortLongGap = 0.07;
    private const double MinShortLongGap = 0.005;
    private const double MaxShortLongGap = 1.0;

    private const double DefaultLongWindowFloor = 0.50;
    private const double MinLongWindowFloor = 0.0;
    private const double MaxLongWindowFloor = 1.0;

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
    private readonly ILogger<MLMultiScaleDriftWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IAlertDispatcher? _alertDispatcher;

    private long _consecutiveFailuresField;
    private int _missingDistributedLockWarningEmitted;

    public MLMultiScaleDriftWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLMultiScaleDriftWorker> logger,
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
            "Detects sudden and gradual ML drift via simultaneous short/long accuracy windows.",
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
                    _metrics?.MLMultiScaleTimeSinceLastSuccessSec.Record(
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
                        _metrics?.MLMultiScaleCycleDurationMs.Record(durationMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: candidates={Candidates}, evaluated={Evaluated}, sudden={Sudden}, gradual={Gradual}, retrain queued={Queued}.",
                                WorkerName, result.CandidateModelCount, result.EvaluatedModelCount,
                                result.SuddenDriftCount, result.GradualDriftCount, result.RetrainingQueued);
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
                            new KeyValuePair<string, object?>("reason", "ml_multiscale_cycle"));
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

    internal async Task<MultiScaleCycleResult> RunCycleAsync(CancellationToken ct)
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
            _metrics?.MLMultiScaleCyclesSkipped.Add(
                1, new KeyValuePair<string, object?>("reason", "disabled"));
            return MultiScaleCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.MLMultiScaleLockAttempts.Add(
                1, new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate multi-scale cycles are possible in multi-instance deployments.",
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
            _metrics?.MLMultiScaleLockAttempts.Add(
                1, new KeyValuePair<string, object?>("outcome", "busy"));
            _metrics?.MLMultiScaleCyclesSkipped.Add(
                1, new KeyValuePair<string, object?>("reason", "lock_busy"));
            return MultiScaleCycleResult.Skipped(settings, "lock_busy");
        }

        _metrics?.MLMultiScaleLockAttempts.Add(
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
            return baseInterval <= TimeSpan.Zero ? WakeInterval : baseInterval;
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
        catch (InvalidOperationException) { /* unsupported */ }
    }

    private async Task<MultiScaleCycleResult> RunCycleCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        MultiScaleWorkerSettings settings,
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

        int evaluated = 0, suddenCount = 0, gradualCount = 0, retrainQueued = 0;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                _metrics?.MLMultiScaleModelsEvaluated.Add(
                    1,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                    new KeyValuePair<string, object?>("learner_architecture", model.LearnerArchitecture.ToString()));

                var outcome = await EvaluateModelAsync(model, readCtx, writeCtx, settings, nowUtc, ct);
                if (outcome.Evaluated) evaluated++;
                if (outcome.SuddenDrift) suddenCount++;
                if (outcome.GradualDrift) gradualCount++;
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
                    new KeyValuePair<string, object?>("reason", "ml_multiscale_model"),
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                _logger.LogWarning(ex,
                    "{Worker}: multi-scale check failed for {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    WorkerName, model.Id, model.Symbol, model.Timeframe);
            }
        }

        return new MultiScaleCycleResult(
            settings,
            SkippedReason: null,
            CandidateModelCount: activeModels.Count,
            EvaluatedModelCount: evaluated,
            SuddenDriftCount: suddenCount,
            GradualDriftCount: gradualCount,
            RetrainingQueued: retrainQueued);
    }

    private async Task<ModelEvalOutcome> EvaluateModelAsync(
        ActiveModelCandidate model,
        DbContext readCtx,
        DbContext writeCtx,
        MultiScaleWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var longSince = nowUtc.AddDays(-settings.LongWindowDays);
        var shortSince = nowUtc.AddDays(-settings.ShortWindowDays);

        var allResolved = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l =>
                l.MLModelId == model.Id &&
                l.PredictedAt >= longSince &&
                l.DirectionCorrect != null &&
                !l.IsDeleted)
            .Select(l => new PredictionPoint(l.PredictedAt, l.DirectionCorrect!.Value))
            .ToListAsync(ct);

        if (allResolved.Count < settings.MinPredictions)
        {
            _metrics?.MLMultiScaleModelsSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "insufficient_long_window"),
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
            return ModelEvalOutcome.Skipped;
        }

        double longAccuracy = allResolved.Count(r => r.DirectionCorrect) / (double)allResolved.Count;
        var shortResolved = allResolved.Where(r => r.PredictedAt >= shortSince).ToList();

        int minShortPredictions = Math.Max(5, settings.MinPredictions / 4);
        if (shortResolved.Count < minShortPredictions)
        {
            _metrics?.MLMultiScaleModelsSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "insufficient_short_window"),
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
            return ModelEvalOutcome.Skipped;
        }

        double shortAccuracy = shortResolved.Count(r => r.DirectionCorrect) / (double)shortResolved.Count;
        double gap = shortAccuracy - longAccuracy;

        bool suddenDrift = gap < -settings.ShortLongAccuracyGap;
        bool gradualDrift = !suddenDrift && longAccuracy < settings.LongWindowFloor;

        if (!suddenDrift && !gradualDrift)
        {
            _logger.LogDebug(
                "{Worker}: {Symbol}/{Timeframe} no drift — short={Short:P1}(n={Ns}) long={Long:P1}(n={Nl}) gap={Gap:+0.0%;-0.0%}.",
                WorkerName, model.Symbol, model.Timeframe,
                shortAccuracy, shortResolved.Count, longAccuracy, allResolved.Count, gap);
            return ModelEvalOutcome.EvaluatedNoDrift;
        }

        string driftType = suddenDrift ? "sudden" : "gradual";

        if (suddenDrift)
            _metrics?.MLMultiScaleSuddenDrifts.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
        else
            _metrics?.MLMultiScaleGradualDrifts.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));

        _logger.LogWarning(
            "{Worker}: {Symbol}/{Timeframe} {DriftType} drift — short={Short:P1} long={Long:P1} gap={Gap:+0.0%;-0.0%} floor={Floor:P1}.",
            WorkerName, model.Symbol, model.Timeframe, driftType,
            shortAccuracy, longAccuracy, gap, settings.LongWindowFloor);

        // Cooldown #1: existing Queued/Running run.
        bool retrainAlreadyActive = await writeCtx.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(r =>
                !r.IsDeleted &&
                r.Symbol == model.Symbol &&
                r.Timeframe == model.Timeframe &&
                (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);

        if (retrainAlreadyActive)
        {
            await DispatchDriftAlertAsync(model, suddenDrift, shortAccuracy, longAccuracy, gap, settings, retrainQueued: false, ct);
            return new ModelEvalOutcome(true, suddenDrift, gradualDrift, false);
        }

        // Cooldown #2: recent completed run within cooldown window.
        if (settings.MinTimeBetweenRetrainsHours > 0)
        {
            var cutoff = nowUtc.AddHours(-settings.MinTimeBetweenRetrainsHours);
            bool recentRun = await writeCtx.Set<MLTrainingRun>()
                .AsNoTracking()
                .AnyAsync(r =>
                    !r.IsDeleted &&
                    r.Symbol == model.Symbol &&
                    r.Timeframe == model.Timeframe &&
                    r.DriftTriggerType == DriftDetectorName &&
                    (r.CompletedAt ?? r.StartedAt) >= cutoff, ct);
            if (recentRun)
            {
                _metrics?.MLMultiScaleRetrainCooldownSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                await DispatchDriftAlertAsync(model, suddenDrift, shortAccuracy, longAccuracy, gap, settings, retrainQueued: false, ct);
                return new ModelEvalOutcome(true, suddenDrift, gradualDrift, false);
            }
        }

        bool queued = await TryQueueRetrainAsync(model, suddenDrift, shortAccuracy, longAccuracy, gap, settings, nowUtc, writeCtx, ct);

        await DispatchDriftAlertAsync(model, suddenDrift, shortAccuracy, longAccuracy, gap, settings, queued, ct);

        return new ModelEvalOutcome(true, suddenDrift, gradualDrift, queued);
    }

    private async Task<bool> TryQueueRetrainAsync(
        ActiveModelCandidate model,
        bool suddenDrift,
        double shortAccuracy,
        double longAccuracy,
        double gap,
        MultiScaleWorkerSettings settings,
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
            DriftTriggerType = DriftDetectorName,
            DriftMetadataJson = JsonSerializer.Serialize(new
            {
                detector = DriftDetectorName,
                driftType = suddenDrift ? "sudden" : "gradual",
                shortAccuracy,
                longAccuracy,
                gap,
                shortWindowDays = settings.ShortWindowDays,
                longWindowDays = settings.LongWindowDays,
                modelId = model.Id,
            }),
            Priority = suddenDrift ? 0 : 1,
            IsDeleted = false,
        });

        try
        {
            await writeCtx.SaveChangesAsync(ct);
            _metrics?.MLMultiScaleRetrainingQueued.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                new KeyValuePair<string, object?>("severity", suddenDrift ? "sudden" : "gradual"));
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            writeCtx.ChangeTracker.Clear();
            _logger.LogInformation(
                "{Worker}: retrain queue race for {Symbol}/{Timeframe} resolved by partial unique index; another worker queued the run.",
                WorkerName, model.Symbol, model.Timeframe);
            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            var sqlStateProp = cur.GetType().GetProperty("SqlState");
            if (sqlStateProp?.GetValue(cur) is string sqlState && sqlState == "23505") return true;
            if (cur.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
                cur.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private async Task DispatchDriftAlertAsync(
        ActiveModelCandidate model,
        bool suddenDrift,
        double shortAccuracy,
        double longAccuracy,
        double gap,
        MultiScaleWorkerSettings settings,
        bool retrainQueued,
        CancellationToken ct)
    {
        if (_alertDispatcher is null) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
            int cooldownSec = await AlertCooldownDefaults.GetCooldownAsync(
                writeCtx, AlertCooldownDefaults.CK_MLDrift, AlertCooldownDefaults.Default_MLDrift, ct);

            string driftType = suddenDrift ? "sudden" : "gradual";

            string conditionJson = JsonSerializer.Serialize(new
            {
                DetectorType = DriftDetectorName,
                DriftType = driftType,
                ModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe.ToString(),
                LearnerArchitecture = model.LearnerArchitecture.ToString(),
                ShortAccuracy = shortAccuracy,
                LongAccuracy = longAccuracy,
                Gap = gap,
                ShortWindowDays = settings.ShortWindowDays,
                LongWindowDays = settings.LongWindowDays,
                RetrainingQueued = retrainQueued,
                DetectedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            });

            var alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                Severity = suddenDrift ? AlertSeverity.Critical : AlertSeverity.High,
                Symbol = model.Symbol,
                DeduplicationKey = $"multiscale-drift:{model.Symbol}:{model.Timeframe}:{driftType}",
                CooldownSeconds = cooldownSec,
                ConditionJson = conditionJson,
                IsActive = true,
            };

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "Multi-scale {0} drift on {1}/{2}: short={3:P1} long={4:P1} gap={5:+0.0%;-0.0%}; retrainQueued={6}.",
                driftType, model.Symbol, model.Timeframe, shortAccuracy, longAccuracy, gap, retrainQueued);

            await _alertDispatcher.DispatchAsync(alert, message, ct);
            _metrics?.MLMultiScaleAlertsDispatched.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                new KeyValuePair<string, object?>("severity", driftType));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Worker}: failed to dispatch multi-scale drift alert for {ModelId} ({Symbol}/{Timeframe}).",
                WorkerName, model.Id, model.Symbol, model.Timeframe);
        }
    }

    private static async Task<MultiScaleWorkerSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled, CK_PollSecs, CK_ShortWindowDays, CK_LongWindowDays,
            CK_MinPredictions, CK_ShortLongGap, CK_LongWindowFloor,
            CK_MaxModelsPerCycle, CK_LockTimeoutSecs, CK_MinTimeBetweenRetrainsHours,
            CK_TrainingDays, CK_DbCommandTimeoutSecs,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => keys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        return new MultiScaleWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, true),
            PollInterval: TimeSpan.FromSeconds(
                ClampInt(GetInt(values, CK_PollSecs, DefaultPollSeconds), DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            ShortWindowDays: ClampInt(GetInt(values, CK_ShortWindowDays, DefaultShortWindowDays), DefaultShortWindowDays, MinShortWindowDays, MaxShortWindowDays),
            LongWindowDays: ClampInt(GetInt(values, CK_LongWindowDays, DefaultLongWindowDays), DefaultLongWindowDays, MinLongWindowDays, MaxLongWindowDays),
            MinPredictions: ClampInt(GetInt(values, CK_MinPredictions, DefaultMinPredictions), DefaultMinPredictions, MinMinPredictions, MaxMinPredictions),
            ShortLongAccuracyGap: ClampDoublePos(GetDouble(values, CK_ShortLongGap, DefaultShortLongGap), DefaultShortLongGap, MinShortLongGap, MaxShortLongGap),
            LongWindowFloor: ClampDoubleRange(GetDouble(values, CK_LongWindowFloor, DefaultLongWindowFloor), DefaultLongWindowFloor, MinLongWindowFloor, MaxLongWindowFloor),
            MaxModelsPerCycle: ClampInt(GetInt(values, CK_MaxModelsPerCycle, DefaultMaxModelsPerCycle), DefaultMaxModelsPerCycle, MinMaxModelsPerCycle, MaxMaxModelsPerCycle),
            LockTimeoutSeconds: ClampInt(GetInt(values, CK_LockTimeoutSecs, DefaultLockTimeoutSeconds), DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            MinTimeBetweenRetrainsHours: ClampNonNegativeInt(
                GetInt(values, CK_MinTimeBetweenRetrainsHours, DefaultMinTimeBetweenRetrainsHours),
                DefaultMinTimeBetweenRetrainsHours, MinMinTimeBetweenRetrainsHours, MaxMinTimeBetweenRetrainsHours),
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

    private static double ClampDoublePos(double value, double fallback, double min, double max)
        => !double.IsFinite(value) || value <= 0.0
            ? fallback
            : Math.Min(Math.Max(value, min), max);

    private static double ClampDoubleRange(double value, double fallback, double min, double max)
        => !double.IsFinite(value) || value < min || value > max
            ? fallback
            : value;

    private readonly record struct ActiveModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        LearnerArchitecture LearnerArchitecture);

    private readonly record struct PredictionPoint(DateTime PredictedAt, bool DirectionCorrect);

    private readonly record struct ModelEvalOutcome(
        bool Evaluated,
        bool SuddenDrift,
        bool GradualDrift,
        bool RetrainQueued)
    {
        public static readonly ModelEvalOutcome Skipped = new(false, false, false, false);
        public static readonly ModelEvalOutcome EvaluatedNoDrift = new(true, false, false, false);
    }

    internal readonly record struct MultiScaleWorkerSettings(
        bool Enabled,
        TimeSpan PollInterval,
        int ShortWindowDays,
        int LongWindowDays,
        int MinPredictions,
        double ShortLongAccuracyGap,
        double LongWindowFloor,
        int MaxModelsPerCycle,
        int LockTimeoutSeconds,
        int MinTimeBetweenRetrainsHours,
        int TrainingDataWindowDays,
        int DbCommandTimeoutSeconds);

    internal readonly record struct MultiScaleCycleResult(
        MultiScaleWorkerSettings Settings,
        string? SkippedReason,
        int CandidateModelCount,
        int EvaluatedModelCount,
        int SuddenDriftCount,
        int GradualDriftCount,
        int RetrainingQueued)
    {
        public static MultiScaleCycleResult Skipped(MultiScaleWorkerSettings settings, string reason)
            => new(settings, reason, 0, 0, 0, 0, 0);
    }
}
