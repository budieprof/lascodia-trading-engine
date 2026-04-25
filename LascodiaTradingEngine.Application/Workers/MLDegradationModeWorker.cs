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
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Maintains per-symbol ML degradation flags when no routable model exists for a symbol.
/// </summary>
public sealed class MLDegradationModeWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLDegradationModeWorker);

    private const string DistributedLockKey = "ml:degradation-mode:cycle";
    private const string AlertDedupPrefix = "ml-degradation-mode:";
    private const int AlertConditionMaxLength = 1_200;

    private const string CK_Enabled = "MLDegradation:Enabled";
    private const string CK_PollSecs = "MLDegradation:PollIntervalSeconds";
    private const string CK_PollJitterSecs = "MLDegradation:PollJitterSeconds";
    private const string CK_MaxSymbols = "MLDegradation:MaxSymbolsPerCycle";
    private const string CK_CriticalAfterMinutes = "MLDegradation:CriticalAfterMinutes";
    private const string CK_EscalateAfterHours = "MLDegradation:EscalateAfterHours";
    private const string CK_AlertCooldown = "MLDegradation:AlertCooldownSeconds";
    private const string CK_LockTimeout = "MLDegradation:LockTimeoutSeconds";
    private const string CK_AlertDest = "MLDegradation:AlertDestination";
    private const string CK_EscalationDest = "MLDegradation:EscalationDestination";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLDegradationModeWorker> _logger;
    private readonly MLDegradationModeOptions _options;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;
    private bool _missingAlertDispatcherWarningEmitted;

    public MLDegradationModeWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLDegradationModeWorker> logger,
        MLDegradationModeOptions? options = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options ?? new MLDegradationModeOptions();
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialSettings = BuildSettings(_options);
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Maintains per-symbol ML degradation flags and alerts when no routable model exists for a symbol.",
            initialSettings.PollInterval);

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName) + initialSettings.InitialDelay;
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var delaySettings = BuildSettings(_options);

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                    var result = await RunCycleAsync(stoppingToken);
                    delaySettings = result.Settings;

                    var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.SymbolsEvaluated);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(durationMs, Tag("worker", WorkerName));

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                    }
                    else if (result.NewlyDegraded > 0 || result.Recovered > 0 || result.AlertsEscalated > 0)
                    {
                        _logger.LogInformation(
                            "{Worker}: evaluated={Evaluated}, degraded={Degraded}, newlyDegraded={NewlyDegraded}, recovered={Recovered}, alertsDispatched={AlertsDispatched}, alertsResolved={AlertsResolved}, escalated={Escalated}.",
                            WorkerName,
                            result.SymbolsEvaluated,
                            result.DegradedSymbols,
                            result.NewlyDegraded,
                            result.Recovered,
                            result.AlertsDispatched,
                            result.AlertsResolved,
                            result.AlertsEscalated);
                    }

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _consecutiveFailures = 0;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _metrics?.WorkerErrors.Add(1, Tag("worker", WorkerName));
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
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
            _logger.LogInformation("{Worker} stopping.", WorkerName);
        }
    }

    internal async Task<MLDegradationModeCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var settings = BuildSettings(_options);

        try
        {
            if (!settings.Enabled)
            {
                RecordCycleSkipped("disabled");
                return MLDegradationModeCycleResult.Skipped(settings, "disabled");
            }

            IAsyncDisposable? cycleLock = null;
            if (_distributedLock is null)
            {
                _metrics?.MLDegradationModeLockAttempts.Add(1, Tag("outcome", "unavailable"));
                if (!_missingDistributedLockWarningEmitted)
                {
                    _logger.LogWarning(
                        "{Worker} running without IDistributedLock; duplicate degradation-mode alerting is possible in multi-instance deployments.",
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
                    _metrics?.MLDegradationModeLockAttempts.Add(1, Tag("outcome", "busy"));
                    RecordCycleSkipped("lock_busy");
                    return MLDegradationModeCycleResult.Skipped(settings, "lock_busy");
                }

                _metrics?.MLDegradationModeLockAttempts.Add(1, Tag("outcome", "acquired"));
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
                            "{Worker} could not resolve IAlertDispatcher; degradation alerts will be persisted but not notified.",
                            WorkerName);
                        _missingAlertDispatcherWarningEmitted = true;
                    }

                    var runtimeSettings = await LoadRuntimeSettingsAsync(db, settings, ct);
                    if (!runtimeSettings.Enabled)
                    {
                        RecordCycleSkipped("disabled");
                        return MLDegradationModeCycleResult.Skipped(runtimeSettings, "disabled");
                    }

                    return await EvaluateDegradationAsync(writeContext, db, dispatcher, runtimeSettings, ct);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
        }
        finally
        {
            _metrics?.MLDegradationModeCycleDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task<MLDegradationModeCycleResult> EvaluateDegradationAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        MLDegradationModeWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var load = await LoadSymbolStatesAsync(db, settings, ct);

        var symbolsEvaluated = 0;
        var degradedSymbols = 0;
        var newlyDegraded = 0;
        var recovered = 0;
        var alertsDispatched = 0;
        var alertsResolved = 0;
        var alertsEscalated = 0;
        var symbolsSkipped = load.InvalidSymbolsSkipped;

        for (var i = 0; i < load.InvalidSymbolsSkipped; i++)
            RecordSymbolSkipped("invalid_symbol");
        if (load.Truncated)
            RecordSymbolSkipped("max_symbols_truncated");

        foreach (var state in load.Symbols)
        {
            ct.ThrowIfCancellationRequested();
            symbolsEvaluated++;
            _metrics?.MLDegradationModeSymbolsEvaluated.Add(1, Tag("symbol", state.Symbol));

            try
            {
                var result = state.IsHealthy
                    ? await ClearDegradationAsync(writeContext, db, dispatcher, settings, state, ct)
                    : await SetOrEscalateDegradationAsync(writeContext, db, dispatcher, settings, state, nowUtc, ct);

                if (!state.IsHealthy)
                {
                    degradedSymbols++;
                    _metrics?.MLDegradationModeSymbolsDegraded.Add(1, Tag("symbol", state.Symbol));
                }

                if (result.NewlyDegraded)
                    newlyDegraded++;
                if (result.Recovered)
                    recovered++;
                alertsDispatched += result.AlertsDispatched;
                if (result.AlertResolved)
                    alertsResolved += result.AlertsResolved;
                alertsEscalated += result.AlertsEscalated;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                symbolsSkipped++;
                RecordSymbolSkipped("symbol_error");
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed to process symbol {Symbol}; skipping.",
                    WorkerName,
                    state.Symbol);
            }
        }

        if (symbolsEvaluated == 0)
        {
            RecordCycleSkipped("no_model_symbols");
            return new MLDegradationModeCycleResult(
                settings,
                "no_model_symbols",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                symbolsSkipped,
                load.Truncated);
        }

        return new MLDegradationModeCycleResult(
            settings,
            null,
            symbolsEvaluated,
            degradedSymbols,
            newlyDegraded,
            recovered,
            alertsDispatched,
            alertsResolved,
            alertsEscalated,
            symbolsSkipped,
            load.Truncated);
    }

    private async Task<DegradationProcessResult> SetOrEscalateDegradationAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        MLDegradationModeWorkerSettings settings,
        SymbolModelState state,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var detectedAt = await GetDetectedAtAsync(db, state.Symbol, ct);
        var active = await IsActiveDegradationAsync(db, state.Symbol, ct);
        var newlyDegraded = !active;

        if (!active)
        {
            detectedAt = nowUtc;
            await UpsertStateAsync(db, state.Symbol, isActive: true, detectedAt, ct);
        }
        else if (!detectedAt.HasValue)
        {
            detectedAt = nowUtc;
            await UpsertDetectedAtAsync(db, state.Symbol, detectedAt.Value, ct);
        }

        await writeContext.SaveChangesAsync(ct);

        var degradedFor = nowUtc - NormalizeUtc(detectedAt!.Value);
        if (degradedFor < TimeSpan.Zero)
            degradedFor = TimeSpan.Zero;

        _metrics?.MLDegradationModeDurationHours.Record(
            degradedFor.TotalHours,
            Tag("symbol", state.Symbol));

        var alertsDispatched = 0;
        var alertsEscalated = 0;

        if (newlyDegraded)
        {
            var activatedDispatched = await UpsertAndDispatchAlertAsync(
                writeContext,
                db,
                dispatcher,
                state,
                DegradationAlertStage.Activated,
                AlertSeverity.High,
                settings.AlertDestination,
                detectedAt.Value,
                degradedFor,
                settings,
                ct);
            if (activatedDispatched)
                alertsDispatched++;

            _metrics?.MLDegradationModeNewlyDegraded.Add(1, Tag("symbol", state.Symbol));
            _logger.LogWarning(
                "{Worker}: symbol {Symbol} entered ML degradation mode; no routable active model exists.",
                WorkerName,
                state.Symbol);
        }

        if (degradedFor >= settings.CriticalAfter)
        {
            var criticalDispatched = await UpsertAndDispatchAlertAsync(
                writeContext,
                db,
                dispatcher,
                state,
                DegradationAlertStage.Critical,
                AlertSeverity.Critical,
                settings.AlertDestination,
                detectedAt.Value,
                degradedFor,
                settings,
                ct);
            if (criticalDispatched)
            {
                alertsDispatched++;
                alertsEscalated++;
            }
        }

        if (degradedFor >= settings.EscalateAfter)
        {
            var escalationDispatched = await UpsertAndDispatchAlertAsync(
                writeContext,
                db,
                dispatcher,
                state,
                DegradationAlertStage.Escalated,
                AlertSeverity.Critical,
                settings.EscalationDestination,
                detectedAt.Value,
                degradedFor,
                settings,
                ct);
            if (escalationDispatched)
            {
                alertsDispatched++;
                alertsEscalated++;
            }
        }

        return new DegradationProcessResult(
            NewlyDegraded: newlyDegraded,
            Recovered: false,
            AlertsDispatched: alertsDispatched,
            AlertResolved: false,
            AlertsResolved: 0,
            AlertsEscalated: alertsEscalated);
    }

    private async Task<DegradationProcessResult> ClearDegradationAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        MLDegradationModeWorkerSettings settings,
        SymbolModelState state,
        CancellationToken ct)
    {
        if (!await IsActiveDegradationAsync(db, state.Symbol, ct))
            return DegradationProcessResult.Noop;

        await UpsertStateAsync(db, state.Symbol, isActive: false, detectedAt: null, ct);
        var alertsResolved = await ResolveWorkerAlertsAsync(writeContext, db, dispatcher, settings, state.Symbol, ct);
        await writeContext.SaveChangesAsync(ct);

        _metrics?.MLDegradationModeSymbolsRecovered.Add(1, Tag("symbol", state.Symbol));
        _logger.LogInformation(
            "{Worker}: symbol {Symbol} recovered from ML degradation mode; routable model count is now {RoutableModels}.",
            WorkerName,
            state.Symbol,
            state.RoutableModelCount);

        return new DegradationProcessResult(
            NewlyDegraded: false,
            Recovered: true,
            AlertsDispatched: 0,
            AlertResolved: alertsResolved > 0,
            AlertsResolved: alertsResolved,
            AlertsEscalated: 0);
    }

    private async Task<bool> UpsertAndDispatchAlertAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        SymbolModelState state,
        DegradationAlertStage stage,
        AlertSeverity severity,
        string destination,
        DateTime detectedAtUtc,
        TimeSpan degradedFor,
        MLDegradationModeWorkerSettings settings,
        CancellationToken ct)
    {
        var deduplicationKey = BuildAlertKey(stage, state.Symbol);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(existing => existing.AlertType == AlertType.MLModelDegraded
                                          && existing.IsActive
                                          && !existing.IsDeleted
                                          && existing.DeduplicationKey == deduplicationKey,
                ct);

        var previousTriggeredAt = alert?.LastTriggeredAt;
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

        alert.AlertType = AlertType.MLModelDegraded;
        alert.Symbol = state.Symbol;
        alert.Severity = severity;
        alert.CooldownSeconds = (int)settings.AlertCooldown.TotalSeconds;
        alert.IsActive = true;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = BuildAlertConditionJson(state, stage, severity, destination, detectedAtUtc, degradedFor);

        await writeContext.SaveChangesAsync(ct);

        if (IsWithinCooldown(previousTriggeredAt, _timeProvider.GetUtcNow().UtcDateTime, settings.AlertCooldown))
            return false;

        if (dispatcher is null)
            return false;

        var lastTriggeredBeforeDispatch = alert.LastTriggeredAt;
        var message = BuildAlertMessage(state, stage, destination, degradedFor);

        try
        {
            await dispatcher.DispatchAsync(alert, message, ct);
            await writeContext.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch {Stage} alert for {Symbol}.",
                WorkerName,
                stage,
                state.Symbol);
            return false;
        }

        var dispatched = alert.LastTriggeredAt.HasValue
                         && alert.LastTriggeredAt != lastTriggeredBeforeDispatch;
        if (dispatched)
        {
            _metrics?.MLDegradationModeAlertsDispatched.Add(
                1,
                Tag("symbol", state.Symbol),
                Tag("stage", stage.ToString().ToLowerInvariant()));
        }

        return dispatched;
    }

    private async Task<int> ResolveWorkerAlertsAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        MLDegradationModeWorkerSettings settings,
        string symbol,
        CancellationToken ct)
    {
        var prefix = BuildSymbolAlertPrefix(symbol);
        var alerts = await db.Set<Alert>()
            .Where(alert => alert.AlertType == AlertType.MLModelDegraded
                         && alert.IsActive
                         && !alert.IsDeleted
                         && alert.DeduplicationKey != null
                         && alert.DeduplicationKey.StartsWith(prefix))
            .ToListAsync(ct);

        if (alerts.Count == 0)
            return 0;

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var alert in alerts)
        {
            alert.CooldownSeconds = (int)settings.AlertCooldown.TotalSeconds;

            if (dispatcher is not null)
            {
                try
                {
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
                        "{Worker}: failed to dispatch degradation recovery for {DeduplicationKey}.",
                        WorkerName,
                        alert.DeduplicationKey);
                }
            }

            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;
        }

        await writeContext.SaveChangesAsync(ct);
        _metrics?.MLDegradationModeAlertsResolved.Add(alerts.Count, Tag("symbol", symbol));
        return alerts.Count;
    }

    private async Task<LoadSymbolStatesResult> LoadSymbolStatesAsync(
        DbContext db,
        MLDegradationModeWorkerSettings settings,
        CancellationToken ct)
    {
        var rows = await db.Set<MLModel>()
            .Where(model => !model.IsDeleted)
            .AsNoTracking()
            .Select(model => new ModelProjection(
                model.Symbol,
                model.Timeframe,
                model.IsActive,
                model.IsSuppressed,
                model.IsFallbackChampion,
                model.Status,
                model.ModelBytes != null && model.ModelBytes.Length > 0))
            .ToListAsync(ct);

        if (rows.Count == 0)
            return new LoadSymbolStatesResult([], 0, false);

        var invalidSymbols = 0;
        var grouped = rows
            .Select(row => row with { Symbol = NormalizeSymbol(row.Symbol) })
            .Where(row =>
            {
                var valid = IsValidSymbol(row.Symbol);
                if (!valid)
                    invalidSymbols++;
                return valid;
            })
            .GroupBy(row => row.Symbol, StringComparer.Ordinal)
            .OrderBy(group => group.Key)
            .ToList();

        var truncated = grouped.Count > settings.MaxSymbolsPerCycle;
        if (truncated)
            grouped = grouped.Take(settings.MaxSymbolsPerCycle).ToList();

        var states = grouped
            .Select(group => BuildSymbolState(group.Key, group.ToList()))
            .ToList();

        return new LoadSymbolStatesResult(states, invalidSymbols, truncated);
    }

    private static SymbolModelState BuildSymbolState(string symbol, IReadOnlyList<ModelProjection> rows)
    {
        var totalModels = rows.Count;
        var activeModels = rows.Count(row => row.IsActive && row.HasModelBytes && row.Status != MLModelStatus.Failed);
        var routableModels = 0;

        foreach (var pair in rows.GroupBy(row => row.Timeframe))
        {
            var pairRows = pair.ToList();
            var primaryRows = pairRows
                .Where(row => row.IsActive
                           && !row.IsFallbackChampion
                           && row.HasModelBytes
                           && row.Status != MLModelStatus.Failed)
                .ToList();

            routableModels += primaryRows.Count(row => !row.IsSuppressed);

            var hasFallback = pairRows.Any(row => row.IsActive
                                               && row.IsFallbackChampion
                                               && !row.IsSuppressed
                                               && row.HasModelBytes
                                               && row.Status != MLModelStatus.Failed);
            if (hasFallback)
                routableModels += primaryRows.Count(row => row.IsSuppressed);
        }

        return new SymbolModelState(
            symbol,
            totalModels,
            activeModels,
            routableModels,
            IsHealthy: routableModels > 0);
    }

    private async Task<MLDegradationModeWorkerSettings> LoadRuntimeSettingsAsync(
        DbContext db,
        MLDegradationModeWorkerSettings defaults,
        CancellationToken ct)
    {
        var keys = new[]
        {
            CK_Enabled,
            CK_PollSecs,
            CK_PollJitterSecs,
            CK_MaxSymbols,
            CK_CriticalAfterMinutes,
            CK_EscalateAfterHours,
            CK_AlertCooldown,
            CK_LockTimeout,
            CK_AlertDest,
            CK_EscalationDest
        };

        var config = await db.Set<EngineConfig>()
            .Where(entry => keys.Contains(entry.Key) && !entry.IsDeleted)
            .AsNoTracking()
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Value, ct);

        var criticalAfter = TimeSpan.FromMinutes(GetInt(config, CK_CriticalAfterMinutes, (int)defaults.CriticalAfter.TotalMinutes, 1, 43_200));
        var escalateAfter = TimeSpan.FromHours(GetInt(config, CK_EscalateAfterHours, (int)defaults.EscalateAfter.TotalHours, 1, 720));
        if (escalateAfter < criticalAfter)
            escalateAfter = criticalAfter;

        return defaults with
        {
            Enabled = GetBool(config, CK_Enabled, defaults.Enabled),
            PollInterval = TimeSpan.FromSeconds(GetInt(config, CK_PollSecs, (int)defaults.PollInterval.TotalSeconds, 30, 86_400)),
            PollJitter = TimeSpan.FromSeconds(GetInt(config, CK_PollJitterSecs, (int)defaults.PollJitter.TotalSeconds, 0, 86_400)),
            MaxSymbolsPerCycle = GetInt(config, CK_MaxSymbols, defaults.MaxSymbolsPerCycle, 1, 100_000),
            CriticalAfter = criticalAfter,
            EscalateAfter = escalateAfter,
            AlertCooldown = TimeSpan.FromSeconds(GetInt(config, CK_AlertCooldown, (int)defaults.AlertCooldown.TotalSeconds, 0, 2_592_000)),
            LockTimeout = TimeSpan.FromSeconds(GetInt(config, CK_LockTimeout, (int)defaults.LockTimeout.TotalSeconds, 0, 300)),
            AlertDestination = GetString(config, CK_AlertDest, defaults.AlertDestination, 100),
            EscalationDestination = GetString(config, CK_EscalationDest, defaults.EscalationDestination, 100)
        };
    }

    private static MLDegradationModeWorkerSettings BuildSettings(MLDegradationModeOptions options)
    {
        var criticalAfter = TimeSpan.FromMinutes(Clamp(options.CriticalAfterMinutes, 1, 43_200));
        var escalateAfter = TimeSpan.FromHours(Clamp(options.EscalateAfterHours, 1, 720));
        if (escalateAfter < criticalAfter)
            escalateAfter = criticalAfter;

        return new MLDegradationModeWorkerSettings
        {
            Enabled = options.Enabled,
            InitialDelay = TimeSpan.FromSeconds(Clamp(options.InitialDelaySeconds, 0, 86_400)),
            PollInterval = TimeSpan.FromSeconds(Clamp(options.PollIntervalSeconds, 30, 86_400)),
            PollJitter = TimeSpan.FromSeconds(Clamp(options.PollJitterSeconds, 0, 86_400)),
            MaxSymbolsPerCycle = Clamp(options.MaxSymbolsPerCycle, 1, 100_000),
            CriticalAfter = criticalAfter,
            EscalateAfter = escalateAfter,
            AlertCooldown = TimeSpan.FromSeconds(Clamp(options.AlertCooldownSeconds, 0, 2_592_000)),
            LockTimeout = TimeSpan.FromSeconds(Clamp(options.LockTimeoutSeconds, 0, 300)),
            AlertDestination = NormalizeDestination(options.AlertDestination, "ml-ops"),
            EscalationDestination = NormalizeDestination(options.EscalationDestination, "ml-ops-escalation")
        };
    }

    private static async Task UpsertStateAsync(
        DbContext db,
        string symbol,
        bool isActive,
        DateTime? detectedAt,
        CancellationToken ct)
    {
        await EngineConfigUpsert.BatchUpsertAsync(
            db,
            new[]
            {
                new EngineConfigUpsertSpec(
                    BuildActiveKey(symbol),
                    isActive ? "true" : "false",
                    ConfigDataType.Bool,
                    $"ML degradation flag for {symbol}; managed by {WorkerName}.",
                    IsHotReloadable: true),
                new EngineConfigUpsertSpec(
                    BuildDetectedAtKey(symbol),
                    detectedAt.HasValue ? NormalizeUtc(detectedAt.Value).ToString("O", CultureInfo.InvariantCulture) : string.Empty,
                    ConfigDataType.String,
                    $"ML degradation detection timestamp for {symbol}; managed by {WorkerName}.",
                    IsHotReloadable: true)
            },
            ct);
    }

    private static Task UpsertDetectedAtAsync(
        DbContext db,
        string symbol,
        DateTime detectedAt,
        CancellationToken ct)
        => EngineConfigUpsert.UpsertAsync(
            db,
            BuildDetectedAtKey(symbol),
            NormalizeUtc(detectedAt).ToString("O", CultureInfo.InvariantCulture),
            ConfigDataType.String,
            $"ML degradation detection timestamp for {symbol}; managed by {WorkerName}.",
            isHotReloadable: true,
            ct: ct);

    private static async Task<bool> IsActiveDegradationAsync(DbContext db, string symbol, CancellationToken ct)
    {
        var value = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(entry => entry.Key == BuildActiveKey(symbol) && !entry.IsDeleted)
            .Select(entry => entry.Value)
            .FirstOrDefaultAsync(ct);

        return TryParseBool(value, defaultValue: false);
    }

    private static async Task<DateTime?> GetDetectedAtAsync(DbContext db, string symbol, CancellationToken ct)
    {
        var value = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(entry => entry.Key == BuildDetectedAtKey(symbol) && !entry.IsDeleted)
            .Select(entry => entry.Value)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string BuildAlertConditionJson(
        SymbolModelState state,
        DegradationAlertStage stage,
        AlertSeverity severity,
        string destination,
        DateTime detectedAtUtc,
        TimeSpan degradedFor)
    {
        var payload = new
        {
            reason = StageReason(stage),
            worker = WorkerName,
            severity = severity.ToString(),
            destination,
            symbol = state.Symbol,
            detectedAt = NormalizeUtc(detectedAtUtc),
            degradedHours = Math.Round(degradedFor.TotalHours, 3),
            totalModels = state.TotalModels,
            activeModels = state.ActiveModels,
            routableModels = state.RoutableModelCount,
            message = stage switch
            {
                DegradationAlertStage.Activated => "No routable ML model is available; ML scoring should be skipped for this symbol.",
                DegradationAlertStage.Critical => "ML degradation has persisted beyond the critical threshold.",
                _ => "ML degradation has persisted beyond the escalation threshold. Escalated operations response required."
            }
        };

        return Truncate(JsonSerializer.Serialize(payload, JsonOptions), AlertConditionMaxLength);
    }

    private static string BuildAlertMessage(
        SymbolModelState state,
        DegradationAlertStage stage,
        string destination,
        TimeSpan degradedFor)
        => stage switch
        {
            DegradationAlertStage.Activated =>
                $"ML degradation mode activated for {state.Symbol}: no routable active model exists. Destination={destination}.",
            DegradationAlertStage.Critical =>
                $"ML degradation mode critical for {state.Symbol}: degraded for {degradedFor.TotalHours:F1}h. Destination={destination}.",
            _ =>
                $"ML degradation mode escalation for {state.Symbol}: degraded for {degradedFor.TotalHours:F1}h. Destination={destination}."
        };

    private static string StageReason(DegradationAlertStage stage)
        => stage switch
        {
            DegradationAlertStage.Activated => "ml_degradation_mode_activated",
            DegradationAlertStage.Critical => "ml_degradation_mode_critical",
            _ => "ml_degradation_mode_escalation"
        };

    private static string BuildActiveKey(string symbol) => $"MLDegradation:{symbol}:Active";

    private static string BuildDetectedAtKey(string symbol) => $"MLDegradation:{symbol}:DetectedAt";

    private static string BuildAlertKey(DegradationAlertStage stage, string symbol)
        => $"{BuildSymbolAlertPrefix(symbol)}{stage.ToString().ToLowerInvariant()}";

    private static string BuildSymbolAlertPrefix(string symbol)
        => $"{AlertDedupPrefix}{symbol}:";

    private static string NormalizeSymbol(string? symbol)
        => string.IsNullOrWhiteSpace(symbol)
            ? string.Empty
            : symbol.Trim().ToUpperInvariant();

    private static bool IsValidSymbol(string symbol)
        => symbol.Length is > 0 and <= 20;

    private static string NormalizeDestination(string? destination, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(destination)
            ? fallback
            : destination.Trim();

        return value.Length > 100 ? value[..100] : value;
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

    private static bool IsWithinCooldown(DateTime? lastTriggeredAt, DateTime nowUtc, TimeSpan cooldown)
    {
        if (!lastTriggeredAt.HasValue || cooldown <= TimeSpan.Zero)
            return false;

        return nowUtc - NormalizeUtc(lastTriggeredAt.Value) < cooldown;
    }

    private static TimeSpan GetIntervalWithJitter(MLDegradationModeWorkerSettings settings)
    {
        if (settings.PollJitter <= TimeSpan.Zero)
            return settings.PollInterval;

        var jitterMs = Random.Shared.NextDouble() * settings.PollJitter.TotalMilliseconds;
        return settings.PollInterval + TimeSpan.FromMilliseconds(jitterMs);
    }

    private static TimeSpan CalculateDelay(TimeSpan baseDelay, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseDelay;

        var multiplier = Math.Min(8, 1 << Math.Min(consecutiveFailures, 3));
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * multiplier);
    }

    private void RecordSymbolSkipped(string reason)
        => _metrics?.MLDegradationModeSymbolsSkipped.Add(1, Tag("reason", reason));

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLDegradationModeCyclesSkipped.Add(1, Tag("reason", reason));

    private static bool GetBool(
        IReadOnlyDictionary<string, string> config,
        string key,
        bool defaultValue)
    {
        if (!config.TryGetValue(key, out var value))
            return defaultValue;

        return TryParseBool(value, defaultValue);
    }

    private static bool TryParseBool(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (bool.TryParse(value, out var parsed))
            return parsed;

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => defaultValue
        };
    }

    private static int GetInt(
        IReadOnlyDictionary<string, string> config,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        if (!config.TryGetValue(key, out var value)
            || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return Clamp(parsed, min, max);
    }

    private static string GetString(
        IReadOnlyDictionary<string, string> config,
        string key,
        string defaultValue,
        int maxLength)
    {
        if (!config.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return defaultValue;

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    private sealed record ModelProjection(
        string Symbol,
        Timeframe Timeframe,
        bool IsActive,
        bool IsSuppressed,
        bool IsFallbackChampion,
        MLModelStatus Status,
        bool HasModelBytes);

    private sealed record SymbolModelState(
        string Symbol,
        int TotalModels,
        int ActiveModels,
        int RoutableModelCount,
        bool IsHealthy);

    private sealed record LoadSymbolStatesResult(
        IReadOnlyList<SymbolModelState> Symbols,
        int InvalidSymbolsSkipped,
        bool Truncated);

    private sealed record DegradationProcessResult(
        bool NewlyDegraded,
        bool Recovered,
        int AlertsDispatched,
        bool AlertResolved,
        int AlertsResolved,
        int AlertsEscalated)
    {
        public static DegradationProcessResult Noop { get; } = new(false, false, 0, false, 0, 0);
    }

    private enum DegradationAlertStage
    {
        Activated,
        Critical,
        Escalated
    }
}

internal sealed record MLDegradationModeWorkerSettings
{
    public bool Enabled { get; init; } = true;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollJitter { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxSymbolsPerCycle { get; init; } = 1_000;
    public TimeSpan CriticalAfter { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan EscalateAfter { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan AlertCooldown { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public string AlertDestination { get; init; } = "ml-ops";
    public string EscalationDestination { get; init; } = "ml-ops-escalation";
}

internal sealed record MLDegradationModeCycleResult(
    MLDegradationModeWorkerSettings Settings,
    string? SkippedReason,
    int SymbolsEvaluated,
    int DegradedSymbols,
    int NewlyDegraded,
    int Recovered,
    int AlertsDispatched,
    int AlertsResolved,
    int AlertsEscalated,
    int SymbolsSkipped,
    bool Truncated)
{
    public static MLDegradationModeCycleResult Skipped(
        MLDegradationModeWorkerSettings settings,
        string reason)
        => new(settings, reason, 0, 0, 0, 0, 0, 0, 0, 0, false);
}
