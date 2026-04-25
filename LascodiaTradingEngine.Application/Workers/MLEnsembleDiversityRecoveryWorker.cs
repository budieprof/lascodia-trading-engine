using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects ensemble diversity collapse in active ML model snapshots and queues a
/// full recovery retrain with elevated diversity regularisation.
/// </summary>
/// <remarks>
/// The persisted <see cref="ModelSnapshot.EnsembleDiversity"/> field is not perfectly
/// uniform across trainers. Bagged logistic, SMOTE, and quantile RF snapshots store a
/// correlation/collapse score where higher is worse. ELM snapshots store directional
/// disagreement where lower is worse. This worker evaluates only those known semantics
/// so a genuinely diverse ELM ensemble is not misclassified as collapsed.
/// </remarks>
public sealed class MLEnsembleDiversityRecoveryWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLEnsembleDiversityRecoveryWorker);

    private const string DistributedLockKey = "workers:ml-ensemble-diversity-recovery:cycle";
    private const string DriftTriggerType = "EnsembleDiversityRecovery";

    private const string CK_Enabled = "MLDiversityRecovery:Enabled";
    private const string CK_PollSecs = "MLDiversityRecovery:PollIntervalSeconds";
    private const string CK_MaxDiversity = "MLDiversityRecovery:MaxEnsembleDiversity";
    private const string CK_MaxCorrelation = "MLDiversityRecovery:MaxEnsembleCorrelation";
    private const string CK_MinDisagreement = "MLDiversityRecovery:MinDisagreementDiversity";
    private const string CK_TreatZeroAsMissing = "MLDiversityRecovery:TreatZeroAsMissing";
    private const string CK_ForcedNclLambda = "MLDiversityRecovery:ForcedNclLambda";
    private const string CK_ForcedDivLambda = "MLDiversityRecovery:ForcedDiversityLambda";
    private const string CK_TrainingDays = "MLTraining:TrainingDataWindowDays";
    private const string CK_MaxModelsPerCycle = "MLDiversityRecovery:MaxModelsPerCycle";
    private const string CK_LockTimeoutSecs = "MLDiversityRecovery:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSecs = "MLDiversityRecovery:DbCommandTimeoutSeconds";
    private const string CK_MinTimeBetweenRetrainsHours = "MLDiversityRecovery:MinTimeBetweenRetrainsHours";
    private const string CK_MaxQueueDepth = "MLDiversityRecovery:MaxQueueDepth";
    private const string CK_RetrainPriority = "MLDiversityRecovery:RetrainPriority";

    private const int DefaultPollSeconds = 21_600;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 7 * 24 * 60 * 60;

    private const double DefaultMaxCorrelationScore = 0.75;
    private const double MinMaxCorrelationScore = 0.01;
    private const double MaxMaxCorrelationScore = 0.999;

    private const double DefaultMinDisagreementDiversity = 0.05;
    private const double MinMinDisagreementDiversity = 0.0;
    private const double MaxMinDisagreementDiversity = 1.0;

    private const double DefaultForcedNclLambda = 0.30;
    private const double DefaultForcedDiversityLambda = 0.15;
    private const double MinForcedLambda = 0.0;
    private const double MaxForcedLambda = 5.0;

    private const int DefaultTrainingDataWindowDays = 365;
    private const int MinTrainingDataWindowDays = 30;
    private const int MaxTrainingDataWindowDays = 3650;

    private const int DefaultMaxModelsPerCycle = 512;
    private const int MinMaxModelsPerCycle = 1;
    private const int MaxMaxModelsPerCycle = 10_000;

    private const int DefaultLockTimeoutSeconds = 5;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private const int DefaultDbCommandTimeoutSeconds = 60;
    private const int MinDbCommandTimeoutSeconds = 5;
    private const int MaxDbCommandTimeoutSeconds = 600;

    private const int DefaultMinTimeBetweenRetrainsHours = 12;
    private const int MinMinTimeBetweenRetrainsHours = 0;
    private const int MaxMinTimeBetweenRetrainsHours = 24 * 30;

    private const int DefaultMaxQueueDepth = int.MaxValue;
    private const int MinMaxQueueDepth = 1;
    private const int MaxMaxQueueDepth = 100_000;

    private const int DefaultRetrainPriority = 2;
    private const int MinRetrainPriority = 0;
    private const int MaxRetrainPriority = 10;

    private static readonly TimeSpan WakeInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLEnsembleDiversityRecoveryWorker> _logger;
    private readonly MLEnsembleDiversityRecoveryOptions _options;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;

    private long _consecutiveFailuresField;
    private int _missingDistributedLockWarningEmitted;

    internal readonly record struct DiversityRecoveryWorkerSettings(
        bool Enabled,
        TimeSpan PollInterval,
        double MaxCorrelationScore,
        double MinDisagreementDiversity,
        bool TreatZeroAsMissing,
        double ForcedNclLambda,
        double ForcedDiversityLambda,
        int TrainingDataWindowDays,
        int MaxModelsPerCycle,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds,
        int MinTimeBetweenRetrainsHours,
        int MaxQueueDepth,
        int RetrainPriority);

    internal readonly record struct DiversityRecoveryCycleResult(
        DiversityRecoveryWorkerSettings Settings,
        string? SkippedReason,
        int ModelsEvaluated,
        int ModelsSkipped,
        int CollapsesDetected,
        int RetrainingQueued,
        int TrainingBacklogDepth)
    {
        public static DiversityRecoveryCycleResult Skipped(
            DiversityRecoveryWorkerSettings settings,
            string reason)
            => new(settings, reason, 0, 0, 0, 0, 0);
    }

    private readonly record struct ModelOutcome(
        bool Evaluated,
        bool CollapseDetected,
        bool RetrainingQueued,
        string? SkipReason);

    private enum DiversityMetricMode
    {
        Unsupported = 0,
        HighCorrelationIsBad = 1,
        LowDisagreementIsBad = 2,
    }

    public MLEnsembleDiversityRecoveryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLEnsembleDiversityRecoveryWorker> logger,
        MLEnsembleDiversityRecoveryOptions? options = null,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options ?? new MLEnsembleDiversityRecoveryOptions();
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private int ConsecutiveFailures
    {
        get => (int)Interlocked.Read(ref _consecutiveFailuresField);
        set => Interlocked.Exchange(ref _consecutiveFailuresField, value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var defaults = BuildSettings(_options);
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Detects ML ensemble diversity collapse and queues diversity-regularised recovery retrains.",
            defaults.PollInterval);

        DateTime lastCycleStartUtc = DateTime.MinValue;
        DateTime lastSuccessUtc = DateTime.MinValue;
        TimeSpan currentPollInterval = defaults.PollInterval;

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName);
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                if (lastSuccessUtc != DateTime.MinValue)
                {
                    _metrics?.MLEnsembleDiversityRecoveryTimeSinceLastSuccessSec.Record(
                        (nowUtc - lastSuccessUtc).TotalSeconds);
                }

                if (nowUtc - lastCycleStartUtc >= currentPollInterval)
                {
                    lastCycleStartUtc = nowUtc;
                    var started = Stopwatch.GetTimestamp();

                    try
                    {
                        _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                        var result = await RunCycleAsync(stoppingToken);
                        currentPollInterval = result.Settings.PollInterval;

                        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        _metrics?.WorkerCycleDurationMs.Record(
                            elapsedMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.MLEnsembleDiversityRecoveryCycleDurationMs.Record(elapsedMs);
                        _healthMonitor?.RecordBacklogDepth(WorkerName, result.TrainingBacklogDepth);
                        _healthMonitor?.RecordCycleSuccess(WorkerName, elapsedMs);

                        if (ConsecutiveFailures > 0)
                        {
                            _healthMonitor?.RecordRecovery(WorkerName, ConsecutiveFailures);
                            ConsecutiveFailures = 0;
                        }

                        lastSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _consecutiveFailuresField);
                        _metrics?.WorkerErrors.Add(
                            1,
                            new KeyValuePair<string, object?>("worker", WorkerName),
                            new KeyValuePair<string, object?>("reason", "ml_ensemble_diversity_recovery_cycle"));
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                        _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                    }
                }

                var delay = ConsecutiveFailures > 0
                    ? CalculateBackoffDelay(ConsecutiveFailures)
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

    internal async Task<DiversityRecoveryCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeDb.GetDbContext();

        var settings = await LoadSettingsAsync(db, _options, ct);
        ApplyCommandTimeout(db, settings.DbCommandTimeoutSeconds);

        if (!settings.Enabled)
        {
            RecordCycleSkipped("disabled");
            return DiversityRecoveryCycleResult.Skipped(settings, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLEnsembleDiversityRecoveryLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate diversity recovery runs are possible in multi-instance deployments.",
                    WorkerName);
            }
        }
        else
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(settings.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLEnsembleDiversityRecoveryLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                return DiversityRecoveryCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLEnsembleDiversityRecoveryLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await CheckAllModelsAsync(db, settings, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    private async Task<DiversityRecoveryCycleResult> CheckAllModelsAsync(
        DbContext db,
        DiversityRecoveryWorkerSettings settings,
        CancellationToken ct)
    {
        var query = db.Set<MLModel>()
            .AsNoTracking()
            .Where(model =>
                model.IsActive &&
                !model.IsDeleted &&
                (model.Status == MLModelStatus.Active || model.IsFallbackChampion) &&
                model.ModelBytes != null);

        var models = await query
            .OrderBy(model => model.Symbol)
            .ThenBy(model => model.Timeframe)
            .ThenBy(model => model.Id)
            .Take(settings.MaxModelsPerCycle + 1)
            .ToListAsync(ct);

        var truncated = models.Count > settings.MaxModelsPerCycle;
        if (truncated)
            models.RemoveAt(models.Count - 1);

        var skippedByLimit = truncated
            ? Math.Max(0, await query.CountAsync(ct) - settings.MaxModelsPerCycle)
            : 0;

        var backlogDepth = await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .CountAsync(run => run.Status == RunStatus.Queued, ct);

        var evaluated = 0;
        var skipped = skippedByLimit;
        var collapses = 0;
        var queued = 0;

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var outcome = await CheckModelDiversityAsync(db, model, settings, ct);
                if (outcome.Evaluated)
                    evaluated++;
                if (outcome.CollapseDetected)
                    collapses++;
                if (outcome.RetrainingQueued)
                    queued++;
                if (outcome.SkipReason is not null)
                    skipped++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                skipped++;
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName),
                    new KeyValuePair<string, object?>("reason", "ml_ensemble_diversity_recovery_model"),
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                _logger.LogWarning(
                    ex,
                    "{Worker}: diversity recovery check failed for model {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    WorkerName,
                    model.Id,
                    model.Symbol,
                    model.Timeframe);
            }
        }

        if (evaluated > 0)
            _metrics?.MLEnsembleDiversityRecoveryModelsEvaluated.Add(evaluated);
        if (skipped > 0)
            _metrics?.MLEnsembleDiversityRecoveryModelsSkipped.Add(skipped);

        return new DiversityRecoveryCycleResult(
            settings,
            SkippedReason: null,
            ModelsEvaluated: evaluated,
            ModelsSkipped: skipped,
            CollapsesDetected: collapses,
            RetrainingQueued: queued,
            TrainingBacklogDepth: backlogDepth);
    }

    private async Task<ModelOutcome> CheckModelDiversityAsync(
        DbContext db,
        MLModel model,
        DiversityRecoveryWorkerSettings settings,
        CancellationToken ct)
    {
        var mode = ResolveMetricMode(model.LearnerArchitecture);
        if (mode == DiversityMetricMode.Unsupported)
            return new ModelOutcome(false, false, false, "unsupported_architecture");

        if (model.ModelBytes is not { Length: > 0 })
            return new ModelOutcome(false, false, false, "missing_snapshot");

        ModelSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(
                ex,
                "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) has unreadable snapshot JSON.",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe);
            return new ModelOutcome(false, false, false, "invalid_snapshot");
        }

        if (snapshot is null)
            return new ModelOutcome(false, false, false, "missing_snapshot");

        var score = snapshot.EnsembleDiversity;
        if (!double.IsFinite(score) || score < 0.0 || score > 1.0)
            return new ModelOutcome(false, false, false, "invalid_diversity_score");

        if (settings.TreatZeroAsMissing && score == 0.0)
            return new ModelOutcome(false, false, false, "missing_diversity_score");

        var symbol = NormalizeSymbol(model.Symbol);
        var collapsed = mode switch
        {
            DiversityMetricMode.HighCorrelationIsBad => score > settings.MaxCorrelationScore,
            DiversityMetricMode.LowDisagreementIsBad => score < settings.MinDisagreementDiversity,
            _ => false,
        };

        _metrics?.MLEnsembleDiversityRecoveryScore.Record(
            score,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("architecture", model.LearnerArchitecture.ToString()),
            new KeyValuePair<string, object?>("mode", mode.ToString()));

        if (!collapsed)
            return new ModelOutcome(true, false, false, null);

        _metrics?.MLEnsembleDiversityRecoveryCollapsesDetected.Add(
            1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("architecture", model.LearnerArchitecture.ToString()));

        var alreadyQueued = await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(run =>
                !run.IsDeleted &&
                run.Symbol.ToUpper() == symbol &&
                run.Timeframe == model.Timeframe &&
                (run.Status == RunStatus.Queued || run.Status == RunStatus.Running),
                ct);

        if (alreadyQueued)
        {
            _metrics?.MLEnsembleDiversityRecoveryRetrainingSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "already_queued"));
            return new ModelOutcome(true, true, false, "already_queued");
        }

        if (settings.MinTimeBetweenRetrainsHours > 0)
        {
            var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-settings.MinTimeBetweenRetrainsHours);
            var recentRecovery = await db.Set<MLTrainingRun>()
                .AsNoTracking()
                .AnyAsync(run =>
                    !run.IsDeleted &&
                    run.Symbol.ToUpper() == symbol &&
                    run.Timeframe == model.Timeframe &&
                    run.TriggerType == TriggerType.AutoDegrading &&
                    run.DriftTriggerType == DriftTriggerType &&
                    (run.CompletedAt ?? run.StartedAt) >= cutoff,
                    ct);

            if (recentRecovery)
            {
                _metrics?.MLEnsembleDiversityRecoveryRetrainingSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "cooldown"));
                return new ModelOutcome(true, true, false, "cooldown");
            }
        }

        var queueDepth = await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .CountAsync(run => run.Status == RunStatus.Queued, ct);

        if (queueDepth >= settings.MaxQueueDepth)
        {
            _metrics?.MLEnsembleDiversityRecoveryRetrainingSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "queue_depth"));
            return new ModelOutcome(true, true, false, "queue_depth");
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var metadata = new
        {
            detector = WorkerName,
            modelId = model.Id,
            symbol,
            timeframe = model.Timeframe.ToString(),
            architecture = model.LearnerArchitecture.ToString(),
            metricMode = mode.ToString(),
            ensembleDiversity = score,
            maxCorrelationScore = settings.MaxCorrelationScore,
            minDisagreementDiversity = settings.MinDisagreementDiversity,
            detectedAtUtc = nowUtc.ToString("O", CultureInfo.InvariantCulture),
        };

        var run = new MLTrainingRun
        {
            Symbol = symbol,
            Timeframe = model.Timeframe,
            LearnerArchitecture = model.LearnerArchitecture,
            TriggerType = TriggerType.AutoDegrading,
            Status = RunStatus.Queued,
            FromDate = nowUtc.AddDays(-settings.TrainingDataWindowDays),
            ToDate = nowUtc,
            StartedAt = nowUtc,
            DriftTriggerType = DriftTriggerType,
            DriftMetadataJson = JsonSerializer.Serialize(metadata, JsonOptions),
            Priority = settings.RetrainPriority,
            HyperparamConfigJson = JsonSerializer.Serialize(new
            {
                TriggeredBy = WorkerName,
                NclLambda = settings.ForcedNclLambda,
                DiversityLambda = settings.ForcedDiversityLambda,
                MaxEnsembleDiversity = settings.MaxCorrelationScore,
                EnsembleDiversity = score,
                MetricMode = mode.ToString(),
                SourceModelId = model.Id,
            }, JsonOptions),
        };

        db.Set<MLTrainingRun>().Add(run);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            _metrics?.MLEnsembleDiversityRecoveryRetrainingSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "unique_race"));
            _logger.LogInformation(
                "{Worker}: retraining queue race for {Symbol}/{Timeframe} resolved by unique index.",
                WorkerName,
                symbol,
                model.Timeframe);
            return new ModelOutcome(true, true, false, "unique_race");
        }

        _metrics?.MLEnsembleDiversityRecoveryRetrainingQueued.Add(
            1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("architecture", model.LearnerArchitecture.ToString()));

        _logger.LogWarning(
            "{Worker}: ensemble diversity collapse detected for model {ModelId} ({Symbol}/{Timeframe}, {Architecture}) score={Score:F4}, mode={Mode}. Queued recovery run {RunId}.",
            WorkerName,
            model.Id,
            symbol,
            model.Timeframe,
            model.LearnerArchitecture,
            score,
            mode,
            run.Id);

        return new ModelOutcome(true, true, true, null);
    }

    internal static async Task<DiversityRecoveryWorkerSettings> LoadSettingsAsync(
        DbContext db,
        CancellationToken ct)
        => await LoadSettingsAsync(db, new MLEnsembleDiversityRecoveryOptions(), ct);

    private static async Task<DiversityRecoveryWorkerSettings> LoadSettingsAsync(
        DbContext db,
        MLEnsembleDiversityRecoveryOptions options,
        CancellationToken ct)
    {
        var defaults = BuildSettings(options);
        string[] keys =
        [
            CK_Enabled,
            CK_PollSecs,
            CK_MaxDiversity,
            CK_MaxCorrelation,
            CK_MinDisagreement,
            CK_TreatZeroAsMissing,
            CK_ForcedNclLambda,
            CK_ForcedDivLambda,
            CK_TrainingDays,
            CK_MaxModelsPerCycle,
            CK_LockTimeoutSecs,
            CK_DbCommandTimeoutSecs,
            CK_MinTimeBetweenRetrainsHours,
            CK_MaxQueueDepth,
            CK_RetrainPriority,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, config => config.Value, ct);

        var maxCorrelationDefault = defaults.MaxCorrelationScore;
        var maxCorrelation = GetDouble(values, CK_MaxCorrelation,
            GetDouble(values, CK_MaxDiversity, maxCorrelationDefault));

        return new DiversityRecoveryWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, defaults.Enabled),
            PollInterval: TimeSpan.FromSeconds(
                ClampInt(GetInt(values, CK_PollSecs, (int)defaults.PollInterval.TotalSeconds), DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            MaxCorrelationScore: ClampDoubleRange(maxCorrelation, DefaultMaxCorrelationScore, MinMaxCorrelationScore, MaxMaxCorrelationScore),
            MinDisagreementDiversity: ClampDoubleRange(GetDouble(values, CK_MinDisagreement, defaults.MinDisagreementDiversity), DefaultMinDisagreementDiversity, MinMinDisagreementDiversity, MaxMinDisagreementDiversity),
            TreatZeroAsMissing: GetBool(values, CK_TreatZeroAsMissing, defaults.TreatZeroAsMissing),
            ForcedNclLambda: ClampDoubleRange(GetDouble(values, CK_ForcedNclLambda, defaults.ForcedNclLambda), DefaultForcedNclLambda, MinForcedLambda, MaxForcedLambda),
            ForcedDiversityLambda: ClampDoubleRange(GetDouble(values, CK_ForcedDivLambda, defaults.ForcedDiversityLambda), DefaultForcedDiversityLambda, MinForcedLambda, MaxForcedLambda),
            TrainingDataWindowDays: ClampInt(GetInt(values, CK_TrainingDays, defaults.TrainingDataWindowDays), DefaultTrainingDataWindowDays, MinTrainingDataWindowDays, MaxTrainingDataWindowDays),
            MaxModelsPerCycle: ClampInt(GetInt(values, CK_MaxModelsPerCycle, defaults.MaxModelsPerCycle), DefaultMaxModelsPerCycle, MinMaxModelsPerCycle, MaxMaxModelsPerCycle),
            LockTimeoutSeconds: ClampNonNegativeInt(GetInt(values, CK_LockTimeoutSecs, defaults.LockTimeoutSeconds), DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            DbCommandTimeoutSeconds: ClampInt(GetInt(values, CK_DbCommandTimeoutSecs, defaults.DbCommandTimeoutSeconds), DefaultDbCommandTimeoutSeconds, MinDbCommandTimeoutSeconds, MaxDbCommandTimeoutSeconds),
            MinTimeBetweenRetrainsHours: ClampNonNegativeInt(GetInt(values, CK_MinTimeBetweenRetrainsHours, defaults.MinTimeBetweenRetrainsHours), DefaultMinTimeBetweenRetrainsHours, MinMinTimeBetweenRetrainsHours, MaxMinTimeBetweenRetrainsHours),
            MaxQueueDepth: ClampIntAllowMax(GetInt(values, CK_MaxQueueDepth, defaults.MaxQueueDepth), DefaultMaxQueueDepth, MinMaxQueueDepth, MaxMaxQueueDepth),
            RetrainPriority: ClampNonNegativeInt(GetInt(values, CK_RetrainPriority, defaults.RetrainPriority), DefaultRetrainPriority, MinRetrainPriority, MaxRetrainPriority));
    }

    private static DiversityRecoveryWorkerSettings BuildSettings(MLEnsembleDiversityRecoveryOptions options)
        => new(
            Enabled: options.Enabled,
            PollInterval: TimeSpan.FromSeconds(ClampInt(options.PollIntervalSeconds, DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            MaxCorrelationScore: ClampDoubleRange(options.MaxEnsembleDiversity, DefaultMaxCorrelationScore, MinMaxCorrelationScore, MaxMaxCorrelationScore),
            MinDisagreementDiversity: ClampDoubleRange(options.MinDisagreementDiversity, DefaultMinDisagreementDiversity, MinMinDisagreementDiversity, MaxMinDisagreementDiversity),
            TreatZeroAsMissing: options.TreatZeroAsMissing,
            ForcedNclLambda: ClampDoubleRange(options.ForcedNclLambda, DefaultForcedNclLambda, MinForcedLambda, MaxForcedLambda),
            ForcedDiversityLambda: ClampDoubleRange(options.ForcedDiversityLambda, DefaultForcedDiversityLambda, MinForcedLambda, MaxForcedLambda),
            TrainingDataWindowDays: ClampInt(options.TrainingDataWindowDays, DefaultTrainingDataWindowDays, MinTrainingDataWindowDays, MaxTrainingDataWindowDays),
            MaxModelsPerCycle: ClampInt(options.MaxModelsPerCycle, DefaultMaxModelsPerCycle, MinMaxModelsPerCycle, MaxMaxModelsPerCycle),
            LockTimeoutSeconds: ClampNonNegativeInt(options.LockTimeoutSeconds, DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            DbCommandTimeoutSeconds: ClampInt(options.DbCommandTimeoutSeconds, DefaultDbCommandTimeoutSeconds, MinDbCommandTimeoutSeconds, MaxDbCommandTimeoutSeconds),
            MinTimeBetweenRetrainsHours: ClampNonNegativeInt(options.MinTimeBetweenRetrainsHours, DefaultMinTimeBetweenRetrainsHours, MinMinTimeBetweenRetrainsHours, MaxMinTimeBetweenRetrainsHours),
            MaxQueueDepth: ClampIntAllowMax(options.MaxQueueDepth, DefaultMaxQueueDepth, MinMaxQueueDepth, MaxMaxQueueDepth),
            RetrainPriority: ClampNonNegativeInt(options.RetrainPriority, DefaultRetrainPriority, MinRetrainPriority, MaxRetrainPriority));

    private static DiversityMetricMode ResolveMetricMode(LearnerArchitecture architecture)
        => architecture switch
        {
            LearnerArchitecture.BaggedLogistic => DiversityMetricMode.HighCorrelationIsBad,
            LearnerArchitecture.Smote => DiversityMetricMode.HighCorrelationIsBad,
            LearnerArchitecture.QuantileRf => DiversityMetricMode.HighCorrelationIsBad,
            LearnerArchitecture.Elm => DiversityMetricMode.LowDisagreementIsBad,
            _ => DiversityMetricMode.Unsupported,
        };

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        try
        {
            if (db.Database.IsRelational())
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(seconds));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLEnsembleDiversityRecoveryCyclesSkipped.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason));

    private static TimeSpan CalculateBackoffDelay(int consecutiveFailures)
    {
        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var seconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }

    private static string NormalizeSymbol(string symbol)
        => string.IsNullOrWhiteSpace(symbol) ? string.Empty : symbol.Trim().ToUpperInvariant();

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
        => values.TryGetValue(key, out var raw) &&
           int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double defaultValue)
        => values.TryGetValue(key, out var raw) &&
           double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;

    private static int ClampInt(int value, int fallback, int min, int max)
        => value <= 0 ? fallback : Math.Min(Math.Max(value, min), max);

    private static int ClampIntAllowMax(int value, int fallback, int min, int max)
        => value <= 0 ? fallback : value >= max ? max : Math.Min(Math.Max(value, min), max);

    private static int ClampNonNegativeInt(int value, int fallback, int min, int max)
        => value < 0 ? fallback : Math.Min(Math.Max(value, min), max);

    private static double ClampDoubleRange(double value, double fallback, double min, double max)
        => !double.IsFinite(value) || value < min || value > max ? fallback : value;
}
