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
/// Tracks cross-detector agreement for ML drift detection. Counts how many independent
/// drift detectors are simultaneously signalling drift for each active model's
/// (Symbol, Timeframe), and dispatches alerts when the consensus threshold is reached
/// or when the model is suppressed without any detector agreement.
/// </summary>
/// <remarks>
/// <para>
/// Detectors monitored: <see cref="MLDriftMonitorWorker"/> (consecutive failure counter),
/// <see cref="MLAdwinDriftWorker"/> (typed <see cref="MLDriftFlag"/> with future expiry),
/// <see cref="MLCusumDriftWorker"/> (recent CUSUM alert), <see cref="MLCovariateShiftWorker"/>
/// (recent covariate-shift training run), and <see cref="MLMultiScaleDriftWorker"/>
/// (recent multi-scale training run).
/// </para>
/// <para>
/// Alerts go through <see cref="IAlertDispatcher"/> with a per-pair dedupe key so the
/// same consensus produces one notification per cooldown window rather than one per
/// cycle. The earlier behaviour of writing <see cref="Alert"/> rows directly is preserved
/// when the dispatcher is unavailable.
/// </para>
/// </remarks>
public sealed class MLDriftAgreementWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLDriftAgreementWorker);

    private const string DistributedLockKey = "workers:ml-drift-agreement:cycle";

    private const string CK_Enabled         = "MLDriftAgreement:Enabled";
    private const string CK_PollSecs        = "MLDriftAgreement:PollIntervalSeconds";
    private const string CK_CusumWindowH    = "MLDriftAgreement:CusumAlertWindowHours";
    private const string CK_ShiftWindowH    = "MLDriftAgreement:ShiftRunWindowHours";
    private const string CK_ConsensusThresh = "MLDriftAgreement:ConsensusThreshold";
    private const string CK_LockTimeoutSecs = "MLDriftAgreement:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSecs = "MLDriftAgreement:DbCommandTimeoutSeconds";

    private const int DefaultPollSeconds = 21600; // 6 hours
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 24 * 60 * 60;

    private const int DefaultCusumWindowHours = 24;
    private const int MinCusumWindowHours = 1;
    private const int MaxCusumWindowHours = 24 * 30;

    private const int DefaultShiftWindowHours = 48;
    private const int MinShiftWindowHours = 1;
    private const int MaxShiftWindowHours = 24 * 30;

    private const int DefaultConsensusThreshold = 4;
    private const int MinConsensusThreshold = 2;
    private const int MaxConsensusThreshold = 5;

    private const int DefaultLockTimeoutSeconds = 5;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private const int DefaultDbCommandTimeoutSeconds = 60;
    private const int MinDbCommandTimeoutSeconds = 5;
    private const int MaxDbCommandTimeoutSeconds = 600;

    private static readonly TimeSpan WakeInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLDriftAgreementWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IAlertDispatcher? _alertDispatcher;

    private long _consecutiveFailuresField;
    private int _missingDistributedLockWarningEmitted;

    public MLDriftAgreementWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLDriftAgreementWorker> logger,
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
            "Tracks cross-detector drift agreement and alerts on consensus or unexplained suppression.",
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
                    _metrics?.MLDriftAgreementTimeSinceLastSuccessSec.Record(
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
                        _metrics?.MLDriftAgreementCycleDurationMs.Record(durationMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: models evaluated={Evaluated}, consensus alerts={ConsensusAlerts}, anomaly alerts={AnomalyAlerts}.",
                                WorkerName, result.ModelsEvaluated, result.ConsensusAlertsRaised, result.AnomalyAlertsRaised);
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
                            new KeyValuePair<string, object?>("reason", "ml_drift_agreement_cycle"));
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

    internal async Task<DriftAgreementCycleResult> RunCycleAsync(CancellationToken ct)
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
            _metrics?.MLDriftAgreementCyclesSkipped.Add(
                1, new KeyValuePair<string, object?>("reason", "disabled"));
            return DriftAgreementCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.MLDriftAgreementLockAttempts.Add(
                1, new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate drift-agreement cycles are possible in multi-instance deployments.",
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
            _metrics?.MLDriftAgreementLockAttempts.Add(
                1, new KeyValuePair<string, object?>("outcome", "busy"));
            _metrics?.MLDriftAgreementCyclesSkipped.Add(
                1, new KeyValuePair<string, object?>("reason", "lock_busy"));
            return DriftAgreementCycleResult.Skipped(settings, "lock_busy");
        }

        _metrics?.MLDriftAgreementLockAttempts.Add(
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

    private async Task<DriftAgreementCycleResult> RunCycleCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        DriftAgreementWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        int alertCooldown = await AlertCooldownDefaults.GetCooldownAsync(
            readCtx, AlertCooldownDefaults.CK_MLDrift, AlertCooldownDefaults.Default_MLDrift, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted)
            .Select(m => new ActiveModelSnapshot(m.Id, m.Symbol, m.Timeframe, m.IsSuppressed))
            .ToListAsync(ct);

        int modelsEvaluated = 0;
        int consensusAlerts = 0;
        int anomalyAlerts = 0;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var (count, total) = await CountAgreeingDetectorsAsync(
                    readCtx, model, nowUtc, settings, ct);

                modelsEvaluated++;
                _metrics?.MLDriftAgreementCounted.Record(
                    count,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));

                // Persist agreement metric for downstream consumers (existing API contract).
                var agreeKey = $"MLDriftAgreement:{model.Symbol}:{model.Timeframe}:AgreeingDetectors";
                var checkedKey = $"MLDriftAgreement:{model.Symbol}:{model.Timeframe}:LastChecked";
                await UpsertConfigAsync(writeCtx, agreeKey, count.ToString(CultureInfo.InvariantCulture), ct);
                await UpsertConfigAsync(writeCtx, checkedKey, nowUtc.ToString("O", CultureInfo.InvariantCulture), ct);

                _logger.LogDebug(
                    "{Worker}: {Symbol}/{Timeframe} — {Count}/{Total} detectors agreeing (suppressed={Suppressed}).",
                    WorkerName, model.Symbol, model.Timeframe, count, total, model.IsSuppressed);

                if (count >= settings.ConsensusThreshold)
                {
                    if (await DispatchConsensusAlertAsync(writeCtx, model, count, total, alertCooldown, nowUtc, ct))
                        consensusAlerts++;
                }
                else if (count == 0 && model.IsSuppressed)
                {
                    if (await DispatchAnomalyAlertAsync(writeCtx, model, total, alertCooldown, nowUtc, ct))
                        anomalyAlerts++;
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
                    new KeyValuePair<string, object?>("worker", WorkerName),
                    new KeyValuePair<string, object?>("reason", "ml_drift_agreement_model"),
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                _logger.LogWarning(ex,
                    "{Worker}: agreement check failed for model {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    WorkerName, model.Id, model.Symbol, model.Timeframe);
            }
        }

        return new DriftAgreementCycleResult(
            settings,
            SkippedReason: null,
            ModelsEvaluated: modelsEvaluated,
            ConsensusAlertsRaised: consensusAlerts,
            AnomalyAlertsRaised: anomalyAlerts);
    }

    /// <summary>
    /// Counts how many of the five drift detectors are simultaneously firing for the
    /// given model. Returns (count, totalDetectors).
    /// </summary>
    private static async Task<(int Count, int Total)> CountAgreeingDetectorsAsync(
        DbContext readCtx,
        ActiveModelSnapshot model,
        DateTime nowUtc,
        DriftAgreementWorkerSettings settings,
        CancellationToken ct)
    {
        const int totalDetectors = 5;
        int count = 0;

        // 1. MLDriftMonitorWorker — consecutive failure counter > 0
        var failKey = $"MLDrift:{model.Symbol}:{model.Timeframe}:ConsecutiveFailures";
        var failEntry = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == failKey, ct);
        if (failEntry?.Value is not null &&
            int.TryParse(failEntry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var failCount) &&
            failCount > 0)
        {
            count++;
        }

        // 2. MLAdwinDriftWorker — typed MLDriftFlag with future expiry
        bool adwinFlagged = await readCtx.Set<MLDriftFlag>()
            .AsNoTracking()
            .AnyAsync(f =>
                f.Symbol == model.Symbol &&
                f.Timeframe == model.Timeframe &&
                f.DetectorType == "AdwinDrift" &&
                f.ExpiresAtUtc > nowUtc, ct);
        if (adwinFlagged) count++;

        // 3. MLCusumDriftWorker — recent CUSUM alert
        var cusumCutoff = nowUtc.AddHours(-settings.CusumWindowHours);
        bool recentCusum = await readCtx.Set<Alert>()
            .AsNoTracking()
            .AnyAsync(a =>
                a.Symbol == model.Symbol &&
                a.AlertType == AlertType.MLModelDegraded &&
                !a.IsDeleted &&
                a.ConditionJson.Contains("\"DetectorType\":\"CUSUM\"") &&
                a.LastTriggeredAt != null &&
                a.LastTriggeredAt >= cusumCutoff, ct);
        if (recentCusum) count++;

        // 4. MLCovariateShiftWorker — recent covariate shift training run
        var shiftCutoff = nowUtc.AddHours(-settings.ShiftWindowHours);
        bool recentCovariateShift = await readCtx.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(r =>
                r.Symbol == model.Symbol &&
                r.Timeframe == model.Timeframe &&
                !r.IsDeleted &&
                r.DriftTriggerType == "CovariateShift" &&
                r.StartedAt >= shiftCutoff, ct);
        if (recentCovariateShift) count++;

        // 5. MLMultiScaleDriftWorker — recent multi-scale drift training run
        bool recentMultiScale = await readCtx.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(r =>
                r.Symbol == model.Symbol &&
                r.Timeframe == model.Timeframe &&
                !r.IsDeleted &&
                r.DriftTriggerType == "MultiSignal" &&
                r.StartedAt >= shiftCutoff, ct);
        if (recentMultiScale) count++;

        return (count, totalDetectors);
    }

    private async Task<bool> DispatchConsensusAlertAsync(
        DbContext writeCtx,
        ActiveModelSnapshot model,
        int agreeingDetectors,
        int totalDetectors,
        int alertCooldown,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string conditionJson = JsonSerializer.Serialize(new
        {
            DetectorType = "DriftAgreement",
            ModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe.ToString(),
            AgreeingDetectors = agreeingDetectors,
            TotalDetectors = totalDetectors,
            DetectedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture),
        });

        var alert = new Alert
        {
            Symbol = model.Symbol,
            AlertType = AlertType.MLModelDegraded,
            Severity = AlertSeverity.Critical,
            IsActive = true,
            ConditionJson = conditionJson,
            DeduplicationKey = $"drift-agreement:{model.Symbol}:{model.Timeframe}",
            CooldownSeconds = alertCooldown,
        };

        string message = string.Format(
            CultureInfo.InvariantCulture,
            "Multi-detector drift consensus on {0}/{1}: {2}/{3} detectors firing simultaneously.",
            model.Symbol, model.Timeframe, agreeingDetectors, totalDetectors);

        return await DispatchOrPersistAlertAsync(writeCtx, alert, message, ct,
            tagSymbol: model.Symbol, tagTimeframe: model.Timeframe, kind: "consensus");
    }

    private async Task<bool> DispatchAnomalyAlertAsync(
        DbContext writeCtx,
        ActiveModelSnapshot model,
        int totalDetectors,
        int alertCooldown,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string conditionJson = JsonSerializer.Serialize(new
        {
            DetectorType = "DriftAgreementAnomaly",
            ModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe.ToString(),
            AgreeingDetectors = 0,
            TotalDetectors = totalDetectors,
            ModelSuppressed = true,
            Message = "Model suppressed but no detectors firing — potential threshold miscalibration",
            DetectedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture),
        });

        var alert = new Alert
        {
            Symbol = model.Symbol,
            AlertType = AlertType.MLModelDegraded,
            Severity = AlertSeverity.High,
            IsActive = true,
            ConditionJson = conditionJson,
            DeduplicationKey = $"drift-agreement-anomaly:{model.Symbol}:{model.Timeframe}",
            CooldownSeconds = alertCooldown * 2,
        };

        string message = string.Format(
            CultureInfo.InvariantCulture,
            "Model suppressed but no detectors firing for {0}/{1} — potential threshold miscalibration.",
            model.Symbol, model.Timeframe);

        return await DispatchOrPersistAlertAsync(writeCtx, alert, message, ct,
            tagSymbol: model.Symbol, tagTimeframe: model.Timeframe, kind: "anomaly");
    }

    private async Task<bool> DispatchOrPersistAlertAsync(
        DbContext writeCtx,
        Alert alert,
        string message,
        CancellationToken ct,
        string tagSymbol,
        Timeframe tagTimeframe,
        string kind)
    {
        try
        {
            if (_alertDispatcher is not null)
            {
                await _alertDispatcher.DispatchAsync(alert, message, ct);
            }
            else
            {
                writeCtx.Set<Alert>().Add(alert);
                await writeCtx.SaveChangesAsync(ct);
            }
            _metrics?.MLDriftAgreementAlertsDispatched.Add(
                1,
                new KeyValuePair<string, object?>("symbol", tagSymbol),
                new KeyValuePair<string, object?>("timeframe", tagTimeframe.ToString()),
                new KeyValuePair<string, object?>("kind", kind));
            _logger.LogWarning(
                "{Worker}: {Kind} alert dispatched for {Symbol}/{Timeframe}: {Message}",
                WorkerName, kind, tagSymbol, tagTimeframe, message);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Worker}: failed to dispatch {Kind} alert for {Symbol}/{Timeframe}.",
                WorkerName, kind, tagSymbol, tagTimeframe);
            return false;
        }
    }

    private static async Task<DriftAgreementWorkerSettings> LoadSettingsAsync(
        DbContext db,
        CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled, CK_PollSecs, CK_CusumWindowH, CK_ShiftWindowH,
            CK_ConsensusThresh, CK_LockTimeoutSecs, CK_DbCommandTimeoutSecs,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => keys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        return new DriftAgreementWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, true),
            PollInterval: TimeSpan.FromSeconds(
                ClampInt(GetInt(values, CK_PollSecs, DefaultPollSeconds), DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            CusumWindowHours: ClampInt(GetInt(values, CK_CusumWindowH, DefaultCusumWindowHours), DefaultCusumWindowHours, MinCusumWindowHours, MaxCusumWindowHours),
            ShiftWindowHours: ClampInt(GetInt(values, CK_ShiftWindowH, DefaultShiftWindowHours), DefaultShiftWindowHours, MinShiftWindowHours, MaxShiftWindowHours),
            ConsensusThreshold: ClampInt(GetInt(values, CK_ConsensusThresh, DefaultConsensusThreshold), DefaultConsensusThreshold, MinConsensusThreshold, MaxConsensusThreshold),
            LockTimeoutSeconds: ClampInt(GetInt(values, CK_LockTimeoutSecs, DefaultLockTimeoutSeconds), DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            DbCommandTimeoutSeconds: ClampInt(GetInt(values, CK_DbCommandTimeoutSecs, DefaultDbCommandTimeoutSeconds), DefaultDbCommandTimeoutSeconds, MinDbCommandTimeoutSeconds, MaxDbCommandTimeoutSeconds));
    }

    private static Task UpsertConfigAsync(
        DbContext ctx,
        string key,
        string value,
        CancellationToken ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(ctx, key, value, ct: ct);

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

    private static int ClampInt(int value, int fallback, int min, int max)
        => value <= 0 ? fallback : Math.Min(Math.Max(value, min), max);

    private readonly record struct ActiveModelSnapshot(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        bool IsSuppressed);

    internal readonly record struct DriftAgreementWorkerSettings(
        bool Enabled,
        TimeSpan PollInterval,
        int CusumWindowHours,
        int ShiftWindowHours,
        int ConsensusThreshold,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds);

    internal readonly record struct DriftAgreementCycleResult(
        DriftAgreementWorkerSettings Settings,
        string? SkippedReason,
        int ModelsEvaluated,
        int ConsensusAlertsRaised,
        int AnomalyAlertsRaised)
    {
        public static DriftAgreementCycleResult Skipped(
            DriftAgreementWorkerSettings settings, string reason)
            => new(settings, reason, 0, 0, 0);
    }
}
