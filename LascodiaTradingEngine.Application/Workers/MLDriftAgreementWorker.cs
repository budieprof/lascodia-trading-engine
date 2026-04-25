using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Tracks cross-detector agreement for ML drift detection and raises durable,
/// deduplicated alerts when independent detectors converge on the same model pair.
/// </summary>
/// <remarks>
/// Monitored signals:
/// <see cref="MLDriftMonitorWorker"/>, <see cref="MLAdwinDriftWorker"/>,
/// <see cref="MLCusumDriftWorker"/>, <see cref="MLCovariateShiftWorker"/>, and
/// <see cref="MLMultiScaleDriftWorker"/>.
/// </remarks>
public sealed class MLDriftAgreementWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLDriftAgreementWorker);

    private const string DistributedLockKey = "workers:ml-drift-agreement:cycle";
    private const string ConsensusAlertPrefix = "drift-agreement:";
    private const string AnomalyAlertPrefix = "drift-agreement-anomaly:";
    private const int AlertConditionMaxLength = 2_500;

    private const string CK_Enabled = "MLDriftAgreement:Enabled";
    private const string CK_InitialDelaySecs = "MLDriftAgreement:InitialDelaySeconds";
    private const string CK_PollSecs = "MLDriftAgreement:PollIntervalSeconds";
    private const string CK_PollJitterSecs = "MLDriftAgreement:PollJitterSeconds";
    private const string CK_CusumWindowH = "MLDriftAgreement:CusumAlertWindowHours";
    private const string CK_ShiftWindowH = "MLDriftAgreement:ShiftRunWindowHours";
    private const string CK_ConsensusThresh = "MLDriftAgreement:ConsensusThreshold";
    private const string CK_MaxModelsPerCycle = "MLDriftAgreement:MaxModelsPerCycle";
    private const string CK_AlertCooldownSecs = "MLDriftAgreement:AlertCooldownSeconds";
    private const string CK_AlertDestination = "MLDriftAgreement:AlertDestination";
    private const string CK_LockTimeoutSecs = "MLDriftAgreement:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSecs = "MLDriftAgreement:DbCommandTimeoutSeconds";

    private const string DriftMonitorFailuresSuffix = "ConsecutiveFailures";
    private const string AdwinDetectorType = "AdwinDrift";
    private const string CusumDetectorType = "CUSUM";
    private const string CusumDriftTriggerType = "CusumDrift";
    private const string CovariateShiftTriggerType = "CovariateShift";
    private const string MultiScaleTriggerType = "MultiSignal";

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLDriftAgreementWorker> _logger;
    private readonly MLDriftAgreementOptions _options;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;
    private bool _missingAlertDispatcherWarningEmitted;

    public MLDriftAgreementWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLDriftAgreementWorker> logger,
        MLDriftAgreementOptions? options = null,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options ?? new MLDriftAgreementOptions();
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialSettings = BuildSettings(_options);
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Tracks cross-detector ML drift agreement and raises durable consensus alerts.",
            initialSettings.PollInterval);

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName) + initialSettings.InitialDelay;
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            DateTime lastSuccessUtc = DateTime.MinValue;

            while (!stoppingToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var delaySettings = BuildSettings(_options);

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    if (lastSuccessUtc != DateTime.MinValue)
                    {
                        _metrics?.MLDriftAgreementTimeSinceLastSuccessSec.Record(
                            (nowUtc - lastSuccessUtc).TotalSeconds);
                    }

                    var result = await RunCycleAsync(stoppingToken);
                    delaySettings = result.Settings;

                    var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.ModelsEvaluated);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(durationMs, Tag("worker", WorkerName));

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                    }
                    else if (result.ConsensusAlertsRaised > 0 ||
                             result.AnomalyAlertsRaised > 0 ||
                             result.AlertsResolved > 0)
                    {
                        _logger.LogInformation(
                            "{Worker}: evaluated={Evaluated}, skipped={Skipped}, consensus={Consensus}, anomalies={Anomalies}, dispatched={Dispatched}, suppressed={Suppressed}, resolved={Resolved}.",
                            WorkerName,
                            result.ModelsEvaluated,
                            result.ModelsSkipped,
                            result.ConsensusAlertsRaised,
                            result.AnomalyAlertsRaised,
                            result.AlertsDispatched,
                            result.AlertsSuppressedByCooldown,
                            result.AlertsResolved);
                    }

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _consecutiveFailures = 0;
                    }

                    lastSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
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
                        Tag("worker", WorkerName),
                        Tag("reason", "ml_drift_agreement_cycle"));
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                await Task.Delay(
                    CalculateDelay(GetIntervalWithJitter(delaySettings), _consecutiveFailures),
                    _timeProvider,
                    stoppingToken);
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

    internal async Task<DriftAgreementCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var settings = BuildSettings(_options);

        try
        {
            if (!settings.Enabled)
            {
                RecordCycleSkipped("disabled");
                return DriftAgreementCycleResult.Skipped(settings, "disabled");
            }

            IAsyncDisposable? cycleLock = null;
            if (_distributedLock is null)
            {
                _metrics?.MLDriftAgreementLockAttempts.Add(1, Tag("outcome", "unavailable"));
                if (!_missingDistributedLockWarningEmitted)
                {
                    _logger.LogWarning(
                        "{Worker} running without IDistributedLock; duplicate drift-agreement cycles are possible in multi-instance deployments.",
                        WorkerName);
                    _missingDistributedLockWarningEmitted = true;
                }
            }
            else
            {
                cycleLock = await _distributedLock.TryAcquireAsync(
                    DistributedLockKey,
                    settings.LockTimeout,
                    ct);

                if (cycleLock is null)
                {
                    _metrics?.MLDriftAgreementLockAttempts.Add(1, Tag("outcome", "busy"));
                    RecordCycleSkipped("lock_busy");
                    return DriftAgreementCycleResult.Skipped(settings, "lock_busy");
                }

                _metrics?.MLDriftAgreementLockAttempts.Add(1, Tag("outcome", "acquired"));
            }

            await using (cycleLock)
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var db = writeContext.GetDbContext();
                    var dispatcher = scope.ServiceProvider.GetService<IAlertDispatcher>();

                    if (dispatcher is null && !_missingAlertDispatcherWarningEmitted)
                    {
                        _logger.LogWarning(
                            "{Worker} could not resolve IAlertDispatcher; drift-agreement alerts will be persisted but not notified.",
                            WorkerName);
                        _missingAlertDispatcherWarningEmitted = true;
                    }

                    ApplyCommandTimeout(db, settings.DbCommandTimeoutSeconds);

                    var runtimeSettings = await LoadRuntimeSettingsAsync(db, settings, ct);
                    if (!runtimeSettings.Enabled)
                    {
                        RecordCycleSkipped("disabled");
                        return DriftAgreementCycleResult.Skipped(runtimeSettings, "disabled");
                    }

                    return await RunCycleCoreAsync(db, dispatcher, runtimeSettings, ct);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
        }
        finally
        {
            _metrics?.MLDriftAgreementCycleDurationMs.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : baseInterval;

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<DriftAgreementCycleResult> RunCycleCoreAsync(
        DbContext db,
        IAlertDispatcher? dispatcher,
        DriftAgreementWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var modelLoad = await LoadActiveModelPairsAsync(db, settings, ct);
        var activeAlertKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int modelsEvaluated = 0;
        int modelsSkipped = modelLoad.ModelsSkipped;
        int consensusAlerts = 0;
        int anomalyAlerts = 0;
        int alertsDispatched = 0;
        int alertsSuppressedByCooldown = 0;
        int alertsResolved = 0;

        if (modelLoad.Models.Count == 0)
        {
            RecordCycleSkipped("no_active_models");

            alertsResolved += await ResolveStaleWorkerAlertsAsync(
                db,
                dispatcher,
                activeAlertKeys,
                allowFullStaleResolution: !modelLoad.LimitedByMaxModels,
                nowUtc,
                ct);

            return new DriftAgreementCycleResult(
                settings,
                SkippedReason: "no_active_models",
                ModelsEvaluated: 0,
                ModelsSkipped: modelsSkipped,
                ConsensusAlertsRaised: 0,
                AnomalyAlertsRaised: 0,
                AlertsDispatched: 0,
                AlertsSuppressedByCooldown: 0,
                AlertsResolved: alertsResolved);
        }

        foreach (var model in modelLoad.Models)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var agreement = await CountAgreeingDetectorsAsync(db, model, nowUtc, settings, ct);
                modelsEvaluated++;

                _metrics?.MLDriftAgreementModelsEvaluated.Add(
                    1,
                    Tag("symbol", model.Symbol),
                    Tag("timeframe", model.Timeframe.ToString()));
                _metrics?.MLDriftAgreementCounted.Record(
                    agreement.Count,
                    Tag("symbol", model.Symbol),
                    Tag("timeframe", model.Timeframe.ToString()));

                await PersistAgreementStateAsync(db, model, agreement, nowUtc, ct);

                _logger.LogDebug(
                    "{Worker}: {Symbol}/{Timeframe} has {Count}/{Total} agreeing detectors ({Detectors}); suppressed={Suppressed}.",
                    WorkerName,
                    model.Symbol,
                    model.Timeframe,
                    agreement.Count,
                    agreement.Total,
                    string.Join(",", agreement.ActiveDetectors),
                    model.IsSuppressed);

                if (agreement.Count >= settings.ConsensusThreshold)
                {
                    var action = await UpsertAndMaybeDispatchAlertAsync(
                        db,
                        dispatcher,
                        model,
                        agreement,
                        DriftAgreementAlertKind.Consensus,
                        settings,
                        nowUtc,
                        ct);

                    activeAlertKeys.Add(action.DeduplicationKey);
                    consensusAlerts++;
                    if (action.Dispatched) alertsDispatched++;
                    if (action.SuppressedByCooldown) alertsSuppressedByCooldown++;
                    alertsResolved += await ResolveAlertByKeyAsync(
                        db,
                        dispatcher,
                        BuildAnomalyDedupKey(model.Symbol, model.Timeframe),
                        nowUtc,
                        ct);
                }
                else if (agreement.Count == 0 && model.IsSuppressed)
                {
                    var action = await UpsertAndMaybeDispatchAlertAsync(
                        db,
                        dispatcher,
                        model,
                        agreement,
                        DriftAgreementAlertKind.SuppressionAnomaly,
                        settings,
                        nowUtc,
                        ct);

                    activeAlertKeys.Add(action.DeduplicationKey);
                    anomalyAlerts++;
                    if (action.Dispatched) alertsDispatched++;
                    if (action.SuppressedByCooldown) alertsSuppressedByCooldown++;
                    alertsResolved += await ResolveAlertByKeyAsync(
                        db,
                        dispatcher,
                        BuildConsensusDedupKey(model.Symbol, model.Timeframe),
                        nowUtc,
                        ct);
                }
                else
                {
                    alertsResolved += await ResolveAlertByKeyAsync(
                        db,
                        dispatcher,
                        BuildConsensusDedupKey(model.Symbol, model.Timeframe),
                        nowUtc,
                        ct);
                    alertsResolved += await ResolveAlertByKeyAsync(
                        db,
                        dispatcher,
                        BuildAnomalyDedupKey(model.Symbol, model.Timeframe),
                        nowUtc,
                        ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _metrics?.WorkerErrors.Add(
                    1,
                    Tag("worker", WorkerName),
                    Tag("reason", "ml_drift_agreement_model"),
                    Tag("symbol", model.Symbol),
                    Tag("timeframe", model.Timeframe.ToString()));
                _logger.LogWarning(
                    ex,
                    "{Worker}: agreement check failed for model pair {Symbol}/{Timeframe}; continuing.",
                    WorkerName,
                    model.Symbol,
                    model.Timeframe);
            }
        }

        alertsResolved += await ResolveStaleWorkerAlertsAsync(
            db,
            dispatcher,
            activeAlertKeys,
            allowFullStaleResolution: !modelLoad.LimitedByMaxModels,
            nowUtc,
            ct);

        if (modelsSkipped > 0)
        {
            _metrics?.MLDriftAgreementModelsSkipped.Add(
                modelsSkipped,
                Tag("reason", modelLoad.LimitedByMaxModels ? "max_models_per_cycle" : "invalid_model"));
        }

        return new DriftAgreementCycleResult(
            settings,
            SkippedReason: null,
            ModelsEvaluated: modelsEvaluated,
            ModelsSkipped: modelsSkipped,
            ConsensusAlertsRaised: consensusAlerts,
            AnomalyAlertsRaised: anomalyAlerts,
            AlertsDispatched: alertsDispatched,
            AlertsSuppressedByCooldown: alertsSuppressedByCooldown,
            AlertsResolved: alertsResolved);
    }

    private static async Task<ActiveModelLoadResult> LoadActiveModelPairsAsync(
        DbContext db,
        DriftAgreementWorkerSettings settings,
        CancellationToken ct)
    {
        var take = settings.MaxModelsPerCycle + 1;
        var candidates = db.Set<MLModel>()
            .AsNoTracking()
            .Where(model =>
                model.IsActive &&
                !model.IsDeleted &&
                (model.Status == MLModelStatus.Active || model.IsFallbackChampion));

        var rows = await candidates
            .OrderBy(model => model.Symbol)
            .ThenBy(model => model.Timeframe)
            .ThenByDescending(model => model.ActivatedAt ?? model.TrainedAt)
            .Take(take)
            .Select(model => new ActiveModelRow(
                model.Id,
                model.Symbol,
                model.Timeframe,
                model.IsSuppressed,
                model.IsFallbackChampion,
                model.ActivatedAt ?? model.TrainedAt))
            .ToListAsync(ct);

        var limited = rows.Count > settings.MaxModelsPerCycle;
        if (limited)
            rows.RemoveAt(rows.Count - 1);

        var skipped = limited
            ? Math.Max(0, await candidates.CountAsync(ct) - settings.MaxModelsPerCycle)
            : 0;
        var grouped = new Dictionary<(string Symbol, Timeframe Timeframe), ActiveModelSnapshot>();

        foreach (var row in rows)
        {
            var symbol = NormalizeSymbol(row.Symbol);
            if (symbol is null)
            {
                skipped++;
                continue;
            }

            var key = (symbol, row.Timeframe);
            if (!grouped.TryGetValue(key, out var existing))
            {
                grouped[key] = new ActiveModelSnapshot(
                    PrimaryModelId: row.Id,
                    Symbol: symbol,
                    Timeframe: row.Timeframe,
                    IsSuppressed: row.IsSuppressed,
                    ActiveModelCount: 1,
                    HasFallbackChampion: row.IsFallbackChampion,
                    MostRecentActivationUtc: row.ActivatedAt);
                continue;
            }

            grouped[key] = existing with
            {
                IsSuppressed = existing.IsSuppressed || row.IsSuppressed,
                ActiveModelCount = existing.ActiveModelCount + 1,
                HasFallbackChampion = existing.HasFallbackChampion || row.IsFallbackChampion,
            };
        }

        return new ActiveModelLoadResult(
            grouped.Values
                .OrderBy(model => model.Symbol, StringComparer.Ordinal)
                .ThenBy(model => model.Timeframe)
                .ToList(),
            skipped,
            limited);
    }

    private static async Task<DriftDetectorAgreement> CountAgreeingDetectorsAsync(
        DbContext db,
        ActiveModelSnapshot model,
        DateTime nowUtc,
        DriftAgreementWorkerSettings settings,
        CancellationToken ct)
    {
        var failKey = $"MLDrift:{model.Symbol}:{model.Timeframe}:{DriftMonitorFailuresSuffix}";
        var failEntry = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(config => config.Key == failKey, ct);

        var driftMonitorCounter = failEntry?.Value is not null &&
                                  int.TryParse(failEntry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var failCount) &&
                                  failCount > 0;

        var driftMonitorFlag = await db.Set<MLDriftFlag>()
            .AsNoTracking()
            .AnyAsync(flag =>
                !flag.IsDeleted &&
                flag.Symbol == model.Symbol &&
                flag.Timeframe == model.Timeframe &&
                flag.DetectorType == "DriftMonitor" &&
                flag.ExpiresAtUtc > nowUtc,
                ct);

        var driftMonitor = driftMonitorCounter || driftMonitorFlag;

        var adwin = await db.Set<MLDriftFlag>()
            .AsNoTracking()
            .AnyAsync(flag =>
                !flag.IsDeleted &&
                flag.Symbol == model.Symbol &&
                flag.Timeframe == model.Timeframe &&
                flag.DetectorType == AdwinDetectorType &&
                flag.ExpiresAtUtc > nowUtc,
                ct);

        var cusumCutoff = nowUtc.AddHours(-settings.CusumAlertWindowHours);
        var timeframeToken = $"\"Timeframe\":\"{model.Timeframe}\"";
        var recentCusumAlert = await db.Set<Alert>()
            .AsNoTracking()
            .AnyAsync(alert =>
                !alert.IsDeleted &&
                alert.Symbol == model.Symbol &&
                alert.AlertType == AlertType.MLModelDegraded &&
                alert.ConditionJson.Contains("\"DetectorType\":\"" + CusumDetectorType + "\"") &&
                alert.ConditionJson.Contains(timeframeToken) &&
                alert.LastTriggeredAt != null &&
                alert.LastTriggeredAt >= cusumCutoff,
                ct);

        var shiftCutoff = nowUtc.AddHours(-settings.ShiftRunWindowHours);
        var recentCusumRun = await HasRecentTrainingRunAsync(
            db,
            model,
            CusumDriftTriggerType,
            shiftCutoff,
            ct);
        var cusum = recentCusumAlert || recentCusumRun;

        var covariateShift = await HasRecentTrainingRunAsync(
            db,
            model,
            CovariateShiftTriggerType,
            shiftCutoff,
            ct);

        var multiScale = await HasRecentTrainingRunAsync(
            db,
            model,
            MultiScaleTriggerType,
            shiftCutoff,
            ct);

        var activeDetectors = new List<string>(5);
        if (driftMonitor) activeDetectors.Add("DriftMonitor");
        if (adwin) activeDetectors.Add(AdwinDetectorType);
        if (cusum) activeDetectors.Add(CusumDetectorType);
        if (covariateShift) activeDetectors.Add(CovariateShiftTriggerType);
        if (multiScale) activeDetectors.Add(MultiScaleTriggerType);

        return new DriftDetectorAgreement(
            Count: activeDetectors.Count,
            Total: 5,
            DriftMonitor: driftMonitor,
            Adwin: adwin,
            Cusum: cusum,
            CovariateShift: covariateShift,
            MultiScale: multiScale,
            ActiveDetectors: activeDetectors);
    }

    private static Task<bool> HasRecentTrainingRunAsync(
        DbContext db,
        ActiveModelSnapshot model,
        string driftTriggerType,
        DateTime cutoffUtc,
        CancellationToken ct)
        => db.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(run =>
                !run.IsDeleted &&
                run.Symbol == model.Symbol &&
                run.Timeframe == model.Timeframe &&
                run.DriftTriggerType == driftTriggerType &&
                run.StartedAt >= cutoffUtc,
                ct);

    private static Task PersistAgreementStateAsync(
        DbContext db,
        ActiveModelSnapshot model,
        DriftDetectorAgreement agreement,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var prefix = $"MLDriftAgreement:{model.Symbol}:{model.Timeframe}";
        var activeDetectorJson = JsonSerializer.Serialize(agreement.ActiveDetectors, JsonOptions);

        EngineConfigUpsertSpec[] specs =
        [
            new(
                $"{prefix}:AgreeingDetectors",
                agreement.Count.ToString(CultureInfo.InvariantCulture),
                ConfigDataType.Int,
                "Number of active drift detectors agreeing for this symbol/timeframe."),
            new(
                $"{prefix}:TotalDetectors",
                agreement.Total.ToString(CultureInfo.InvariantCulture),
                ConfigDataType.Int,
                "Total drift detectors checked by MLDriftAgreementWorker."),
            new(
                $"{prefix}:ActiveDetectors",
                activeDetectorJson,
                ConfigDataType.Json,
                "JSON array of active detector names from the latest agreement cycle."),
            new(
                $"{prefix}:LastChecked",
                nowUtc.ToString("O", CultureInfo.InvariantCulture),
                ConfigDataType.String,
                "UTC timestamp of the latest MLDriftAgreementWorker evaluation.")
        ];

        return EngineConfigUpsert.BatchUpsertAsync(db, specs, ct);
    }

    private async Task<DriftAgreementAlertAction> UpsertAndMaybeDispatchAlertAsync(
        DbContext db,
        IAlertDispatcher? dispatcher,
        ActiveModelSnapshot model,
        DriftDetectorAgreement agreement,
        DriftAgreementAlertKind kind,
        DriftAgreementWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var dedupKey = kind == DriftAgreementAlertKind.Consensus
            ? BuildConsensusDedupKey(model.Symbol, model.Timeframe)
            : BuildAnomalyDedupKey(model.Symbol, model.Timeframe);

        var alerts = await db.Set<Alert>()
            .IgnoreQueryFilters()
            .Where(alert =>
                !alert.IsDeleted &&
                alert.AlertType == AlertType.MLModelDegraded &&
                alert.DeduplicationKey == dedupKey)
            .OrderByDescending(alert => alert.IsActive)
            .ThenByDescending(alert => alert.LastTriggeredAt ?? DateTime.MinValue)
            .ThenByDescending(alert => alert.Id)
            .ToListAsync(ct);

        var alert = alerts.FirstOrDefault();
        var wasActive = alert is { IsActive: true, AutoResolvedAt: null };
        var lastTriggeredAt = alert?.LastTriggeredAt;

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                Symbol = model.Symbol,
                DeduplicationKey = dedupKey,
            };
            db.Set<Alert>().Add(alert);
        }

        foreach (var duplicate in alerts.Skip(1))
        {
            duplicate.IsActive = false;
            duplicate.AutoResolvedAt ??= nowUtc;
        }

        var severity = kind == DriftAgreementAlertKind.Consensus
            ? AlertSeverity.Critical
            : AlertSeverity.High;

        alert.Symbol = model.Symbol;
        alert.AlertType = AlertType.MLModelDegraded;
        alert.Severity = severity;
        alert.IsActive = true;
        alert.AutoResolvedAt = null;
        alert.CooldownSeconds = settings.AlertCooldownSeconds;
        alert.ConditionJson = BuildConditionJson(model, agreement, kind, settings, nowUtc);

        var cooldownElapsed = lastTriggeredAt is null ||
                              settings.AlertCooldownSeconds <= 0 ||
                              nowUtc - lastTriggeredAt.Value >= TimeSpan.FromSeconds(settings.AlertCooldownSeconds);
        var shouldDispatch = !wasActive || cooldownElapsed;

        await db.SaveChangesAsync(ct);

        if (!shouldDispatch)
        {
            _metrics?.MLDriftAgreementAlertsSuppressed.Add(
                1,
                Tag("kind", GetAlertKindTag(kind)),
                Tag("reason", "cooldown"));

            return new DriftAgreementAlertAction(
                dedupKey,
                Dispatched: false,
                SuppressedByCooldown: true,
                PersistedWithoutDispatcher: false);
        }

        if (dispatcher is null)
        {
            alert.LastTriggeredAt = nowUtc;
            await db.SaveChangesAsync(ct);
            return new DriftAgreementAlertAction(
                dedupKey,
                Dispatched: false,
                SuppressedByCooldown: false,
                PersistedWithoutDispatcher: true);
        }

        try
        {
            await dispatcher.DispatchAsync(alert, BuildAlertMessage(model, agreement, kind), ct);
            alert.LastTriggeredAt = nowUtc;
            await db.SaveChangesAsync(ct);

            _metrics?.MLDriftAgreementAlertsDispatched.Add(
                1,
                Tag("symbol", model.Symbol),
                Tag("timeframe", model.Timeframe.ToString()),
                Tag("kind", GetAlertKindTag(kind)));

            return new DriftAgreementAlertAction(
                dedupKey,
                Dispatched: true,
                SuppressedByCooldown: false,
                PersistedWithoutDispatcher: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch {Kind} alert for {Symbol}/{Timeframe}.",
                WorkerName,
                GetAlertKindTag(kind),
                model.Symbol,
                model.Timeframe);

            return new DriftAgreementAlertAction(
                dedupKey,
                Dispatched: false,
                SuppressedByCooldown: false,
                PersistedWithoutDispatcher: false);
        }
    }

    private async Task<int> ResolveAlertByKeyAsync(
        DbContext db,
        IAlertDispatcher? dispatcher,
        string dedupKey,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alerts = await db.Set<Alert>()
            .IgnoreQueryFilters()
            .Where(alert =>
                !alert.IsDeleted &&
                alert.AlertType == AlertType.MLModelDegraded &&
                alert.DeduplicationKey == dedupKey &&
                (alert.IsActive || alert.AutoResolvedAt == null))
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var alert in alerts)
            resolved += await ResolveLoadedAlertAsync(db, dispatcher, alert, nowUtc, ct);

        return resolved;
    }

    private async Task<int> ResolveStaleWorkerAlertsAsync(
        DbContext db,
        IAlertDispatcher? dispatcher,
        HashSet<string> activeAlertKeys,
        bool allowFullStaleResolution,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (!allowFullStaleResolution)
            return 0;

        var alerts = await db.Set<Alert>()
            .IgnoreQueryFilters()
            .Where(alert =>
                !alert.IsDeleted &&
                alert.AlertType == AlertType.MLModelDegraded &&
                alert.IsActive &&
                alert.DeduplicationKey != null &&
                (alert.DeduplicationKey.StartsWith(ConsensusAlertPrefix) ||
                 alert.DeduplicationKey.StartsWith(AnomalyAlertPrefix)))
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var alert in alerts)
        {
            if (alert.DeduplicationKey is null || activeAlertKeys.Contains(alert.DeduplicationKey))
                continue;

            resolved += await ResolveLoadedAlertAsync(db, dispatcher, alert, nowUtc, ct);
        }

        return resolved;
    }

    private async Task<int> ResolveLoadedAlertAsync(
        DbContext db,
        IAlertDispatcher? dispatcher,
        Alert alert,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (!alert.IsActive && alert.AutoResolvedAt.HasValue)
            return 0;

        try
        {
            if (dispatcher is not null)
                await dispatcher.TryAutoResolveAsync(alert, conditionStillActive: false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch resolved notification for alert {AlertId}.",
                WorkerName,
                alert.Id);
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await db.SaveChangesAsync(ct);

        _metrics?.MLDriftAgreementAlertsResolved.Add(
            1,
            Tag("symbol", alert.Symbol ?? "unknown"));

        return 1;
    }

    private static string BuildConditionJson(
        ActiveModelSnapshot model,
        DriftDetectorAgreement agreement,
        DriftAgreementAlertKind kind,
        DriftAgreementWorkerSettings settings,
        DateTime nowUtc)
    {
        var payload = new
        {
            reason = kind == DriftAgreementAlertKind.Consensus
                ? "drift_detector_consensus"
                : "suppressed_without_detector_agreement",
            severity = kind == DriftAgreementAlertKind.Consensus ? "critical" : "high",
            destination = settings.AlertDestination,
            worker = WorkerName,
            modelId = model.PrimaryModelId,
            activeModelCount = model.ActiveModelCount,
            hasFallbackChampion = model.HasFallbackChampion,
            symbol = model.Symbol,
            timeframe = model.Timeframe.ToString(),
            modelSuppressed = model.IsSuppressed,
            agreeingDetectors = agreement.Count,
            totalDetectors = agreement.Total,
            consensusThreshold = settings.ConsensusThreshold,
            activeDetectors = agreement.ActiveDetectors,
            detectorStates = new
            {
                driftMonitor = agreement.DriftMonitor,
                adwin = agreement.Adwin,
                cusum = agreement.Cusum,
                covariateShift = agreement.CovariateShift,
                multiScale = agreement.MultiScale,
            },
            detectedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture),
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return json.Length <= AlertConditionMaxLength
            ? json
            : json[..AlertConditionMaxLength];
    }

    private static string BuildAlertMessage(
        ActiveModelSnapshot model,
        DriftDetectorAgreement agreement,
        DriftAgreementAlertKind kind)
    {
        if (kind == DriftAgreementAlertKind.SuppressionAnomaly)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Model {0}/{1} is suppressed, but no drift detectors are active. Check suppression thresholds and detector health.",
                model.Symbol,
                model.Timeframe);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Multi-detector drift consensus on {0}/{1}: {2}/{3} detectors active ({4}).",
            model.Symbol,
            model.Timeframe,
            agreement.Count,
            agreement.Total,
            agreement.ActiveDetectors.Count == 0 ? "none" : string.Join(", ", agreement.ActiveDetectors));
    }

    private static async Task<DriftAgreementWorkerSettings> LoadRuntimeSettingsAsync(
        DbContext db,
        DriftAgreementWorkerSettings current,
        CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled,
            CK_InitialDelaySecs,
            CK_PollSecs,
            CK_PollJitterSecs,
            CK_CusumWindowH,
            CK_ShiftWindowH,
            CK_ConsensusThresh,
            CK_MaxModelsPerCycle,
            CK_AlertCooldownSecs,
            CK_AlertDestination,
            CK_LockTimeoutSecs,
            CK_DbCommandTimeoutSecs,
            AlertCooldownDefaults.CK_MLDrift,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, config => config.Value, ct);

        var alertCooldown = GetBoundedInt(
            values,
            CK_AlertCooldownSecs,
            GetBoundedInt(
                values,
                AlertCooldownDefaults.CK_MLDrift,
                current.AlertCooldownSeconds,
                min: 0,
                max: 2_592_000,
                allowZero: true),
            min: 0,
            max: 2_592_000,
            allowZero: true);

        var alertDestination = values.TryGetValue(CK_AlertDestination, out var rawDestination) &&
                               !string.IsNullOrWhiteSpace(rawDestination)
            ? rawDestination.Trim()
            : current.AlertDestination;

        if (alertDestination.Length > 100)
            alertDestination = alertDestination[..100];

        return current with
        {
            Enabled = GetBool(values, CK_Enabled, current.Enabled),
            InitialDelay = TimeSpan.FromSeconds(GetBoundedInt(values, CK_InitialDelaySecs, (int)current.InitialDelay.TotalSeconds, 0, 86_400, allowZero: true)),
            PollInterval = TimeSpan.FromSeconds(GetBoundedInt(values, CK_PollSecs, (int)current.PollInterval.TotalSeconds, 60, 86_400)),
            PollJitter = TimeSpan.FromSeconds(GetBoundedInt(values, CK_PollJitterSecs, (int)current.PollJitter.TotalSeconds, 0, 86_400, allowZero: true)),
            CusumAlertWindowHours = GetBoundedInt(values, CK_CusumWindowH, current.CusumAlertWindowHours, 1, 720),
            ShiftRunWindowHours = GetBoundedInt(values, CK_ShiftWindowH, current.ShiftRunWindowHours, 1, 720),
            ConsensusThreshold = GetBoundedInt(values, CK_ConsensusThresh, current.ConsensusThreshold, 2, 5),
            MaxModelsPerCycle = GetBoundedInt(values, CK_MaxModelsPerCycle, current.MaxModelsPerCycle, 1, 100_000),
            AlertCooldownSeconds = alertCooldown,
            AlertDestination = alertDestination,
            LockTimeout = TimeSpan.FromSeconds(GetBoundedInt(values, CK_LockTimeoutSecs, (int)current.LockTimeout.TotalSeconds, 0, 300, allowZero: true)),
            DbCommandTimeoutSeconds = GetBoundedInt(values, CK_DbCommandTimeoutSecs, current.DbCommandTimeoutSeconds, 5, 600),
        };
    }

    private static DriftAgreementWorkerSettings BuildSettings(MLDriftAgreementOptions options)
        => new(
            Enabled: options.Enabled,
            InitialDelay: TimeSpan.FromSeconds(ClampRange(options.InitialDelaySeconds, 0, 86_400)),
            PollInterval: TimeSpan.FromSeconds(ClampRange(options.PollIntervalSeconds, 60, 86_400)),
            PollJitter: TimeSpan.FromSeconds(ClampRange(options.PollJitterSeconds, 0, 86_400)),
            CusumAlertWindowHours: ClampRange(options.CusumAlertWindowHours, 1, 720),
            ShiftRunWindowHours: ClampRange(options.ShiftRunWindowHours, 1, 720),
            ConsensusThreshold: ClampRange(options.ConsensusThreshold, 2, 5),
            MaxModelsPerCycle: ClampRange(options.MaxModelsPerCycle, 1, 100_000),
            AlertCooldownSeconds: ClampRange(options.AlertCooldownSeconds, 0, 2_592_000),
            AlertDestination: string.IsNullOrWhiteSpace(options.AlertDestination)
                ? "ml-ops"
                : options.AlertDestination.Trim().Length > 100
                    ? options.AlertDestination.Trim()[..100]
                    : options.AlertDestination.Trim(),
            LockTimeout: TimeSpan.FromSeconds(ClampRange(options.LockTimeoutSeconds, 0, 300)),
            DbCommandTimeoutSeconds: ClampRange(options.DbCommandTimeoutSeconds, 5, 600));

    private static TimeSpan GetIntervalWithJitter(DriftAgreementWorkerSettings settings)
    {
        if (settings.PollJitter <= TimeSpan.Zero)
            return settings.PollInterval;

        var jitterMs = Random.Shared.NextDouble() * settings.PollJitter.TotalMilliseconds;
        return settings.PollInterval + TimeSpan.FromMilliseconds(jitterMs);
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
        }
    }

    private static string BuildConsensusDedupKey(string symbol, Timeframe timeframe)
        => $"{ConsensusAlertPrefix}{symbol}:{timeframe}";

    private static string BuildAnomalyDedupKey(string symbol, Timeframe timeframe)
        => $"{AnomalyAlertPrefix}{symbol}:{timeframe}";

    private static string GetAlertKindTag(DriftAgreementAlertKind kind)
        => kind == DriftAgreementAlertKind.Consensus ? "consensus" : "anomaly";

    private static string? NormalizeSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return null;

        var normalized = symbol.Trim().ToUpperInvariant();
        return normalized.Length is >= 1 and <= 20 ? normalized : null;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (bool.TryParse(raw, out var parsedBool))
            return parsedBool;

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt)
            ? parsedInt != 0
            : defaultValue;
    }

    private static int GetBoundedInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int currentValue,
        int min,
        int max,
        bool allowZero = false)
    {
        if (!values.TryGetValue(key, out var raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return currentValue;
        }

        if (allowZero && parsed == 0)
            return 0;

        if (!allowZero && parsed <= 0)
            return currentValue;

        return ClampRange(parsed, min, max);
    }

    private static int ClampRange(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLDriftAgreementCyclesSkipped.Add(1, Tag("reason", reason));

    private static KeyValuePair<string, object?> Tag(string key, object? value)
        => new(key, value);

    private readonly record struct ActiveModelRow(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        bool IsSuppressed,
        bool IsFallbackChampion,
        DateTime? ActivatedAt);

    private sealed record ActiveModelLoadResult(
        IReadOnlyList<ActiveModelSnapshot> Models,
        int ModelsSkipped,
        bool LimitedByMaxModels);

    private readonly record struct ActiveModelSnapshot(
        long PrimaryModelId,
        string Symbol,
        Timeframe Timeframe,
        bool IsSuppressed,
        int ActiveModelCount,
        bool HasFallbackChampion,
        DateTime? MostRecentActivationUtc);

    private sealed record DriftDetectorAgreement(
        int Count,
        int Total,
        bool DriftMonitor,
        bool Adwin,
        bool Cusum,
        bool CovariateShift,
        bool MultiScale,
        IReadOnlyList<string> ActiveDetectors);

    private enum DriftAgreementAlertKind
    {
        Consensus,
        SuppressionAnomaly
    }

    private sealed record DriftAgreementAlertAction(
        string DeduplicationKey,
        bool Dispatched,
        bool SuppressedByCooldown,
        bool PersistedWithoutDispatcher);

    internal sealed record DriftAgreementWorkerSettings(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        TimeSpan PollJitter,
        int CusumAlertWindowHours,
        int ShiftRunWindowHours,
        int ConsensusThreshold,
        int MaxModelsPerCycle,
        int AlertCooldownSeconds,
        string AlertDestination,
        TimeSpan LockTimeout,
        int DbCommandTimeoutSeconds);

    internal readonly record struct DriftAgreementCycleResult(
        DriftAgreementWorkerSettings Settings,
        string? SkippedReason,
        int ModelsEvaluated,
        int ModelsSkipped,
        int ConsensusAlertsRaised,
        int AnomalyAlertsRaised,
        int AlertsDispatched,
        int AlertsSuppressedByCooldown,
        int AlertsResolved)
    {
        public static DriftAgreementCycleResult Skipped(
            DriftAgreementWorkerSettings settings,
            string reason)
            => new(settings, reason, 0, 0, 0, 0, 0, 0, 0);
    }
}
