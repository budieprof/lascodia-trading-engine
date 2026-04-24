using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors per-strategy execution quality and automatically pauses strategies whose recent
/// execution windows show persistently poor fills.
///
/// <para>
/// The worker evaluates only fresh execution-quality evidence and only for strategies that are
/// currently active or already paused by this worker. That prevents stale fills from causing new
/// trips or auto-resumes months later.
/// </para>
///
/// <para>
/// Decision rules are evaluated over the most recent <c>ExecQuality:WindowFills</c> fills inside
/// the configured lookback window:
/// </para>
///
/// <list type="bullet">
///   <item><description>
///     Mean absolute <see cref="ExecutionQualityLog.SlippagePips"/> &gt;
///     <c>ExecQuality:MaxAvgSlippagePips</c> trips the circuit.
///   </description></item>
///   <item><description>
///     Mean positive <see cref="ExecutionQualityLog.SubmitToFillMs"/> values &gt;
///     <c>ExecQuality:MaxAvgLatencyMs</c> trips the circuit. Zero/negative latencies are treated as
///     missing telemetry and excluded from the latency average instead of biasing it downward.
///   </description></item>
///   <item><description>
///     When enabled, mean <see cref="ExecutionQualityLog.FillRate"/> &lt;
///     <c>ExecQuality:MinAvgFillRate</c> also trips the circuit.
///   </description></item>
/// </list>
///
/// <para>
/// Hysteresis is applied on recovery so a strategy paused by this worker is only auto-resumed
/// once its window has moved meaningfully back inside the thresholds.
/// </para>
/// </summary>
public sealed class ExecutionQualityCircuitBreakerWorker : BackgroundService
{
    internal const string WorkerName = nameof(ExecutionQualityCircuitBreakerWorker);

    private const string ExecutionQualityPauseReason = "ExecutionQuality";

    private const string CK_PollMins = "ExecQuality:PollIntervalMinutes";
    private const string CK_WindowFills = "ExecQuality:WindowFills";
    private const string CK_MaxSlippage = "ExecQuality:MaxAvgSlippagePips";
    private const string CK_MaxLatencyMs = "ExecQuality:MaxAvgLatencyMs";
    private const string CK_AutoPause = "ExecQuality:AutoPauseEnabled";
    private const string CK_HysteresisMargin = "ExecQuality:HysteresisMarginPct";
    private const string CK_LookbackDays = "ExecQuality:LookbackDays";
    private const string CK_MinAvgFillRate = "ExecQuality:MinAvgFillRate";
    private const string DistributedLockKey = "workers:execution-quality-circuit-breaker:cycle";

    private const int DefaultPollIntervalMinutes = 15;
    private const int MinPollIntervalMinutes = 1;
    private const int MaxPollIntervalMinutes = 24 * 60;

    private const int DefaultWindowFills = 50;
    private const int MinWindowFills = 3;
    private const int MaxWindowFills = 500;

    private const double DefaultMaxAverageAbsoluteSlippagePips = 3.0;
    private const double MaxAllowedAverageAbsoluteSlippagePips = 50.0;

    private const double DefaultMaxAverageLatencyMs = 2000.0;
    private const double MaxAllowedAverageLatencyMs = 60_000.0;

    private const double DefaultHysteresisMargin = 0.20;
    private const double MinHysteresisMargin = 0.0;
    private const double MaxHysteresisMargin = 0.95;

    private const int DefaultLookbackDays = 30;
    private const int MinLookbackDays = 1;
    private const int MaxLookbackDays = 365;

    private const double DefaultMinAverageFillRate = 0.0;
    private const double MinAverageFillRate = 0.0;
    private const double MaxAverageFillRate = 1.0;

    private static readonly TimeSpan DistributedLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExecutionQualityCircuitBreakerWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    public ExecutionQualityCircuitBreakerWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ExecutionQualityCircuitBreakerWorker> logger,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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
            "Monitors recent execution quality windows, auto-pauses active strategies on sustained slippage/latency/fill-quality breaches, and auto-resumes only worker-owned pauses after hysteresis recovery.",
            TimeSpan.FromMinutes(DefaultPollIntervalMinutes));

        var currentPollInterval = TimeSpan.FromMinutes(DefaultPollIntervalMinutes);

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
                    currentPollInterval = result.Settings.PollInterval;

                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.CandidateStrategyCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.ExecutionQualityCycleDurationMs.Record(durationMs);

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else if (result.PauseCount > 0 || result.ResumeCount > 0 || result.WarningCount > 0)
                    {
                        _logger.LogInformation(
                            "{Worker}: candidates={Candidates}, evaluated={Evaluated}, breaches={Breaches}, paused={Paused}, resumed={Resumed}, warnings={Warnings}, insufficientFreshData={Insufficient}.",
                            WorkerName,
                            result.CandidateStrategyCount,
                            result.EvaluatedStrategyCount,
                            result.BreachCount,
                            result.PauseCount,
                            result.ResumeCount,
                            result.WarningCount,
                            result.InsufficientFreshDataCount);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "{Worker}: candidates={Candidates}, evaluated={Evaluated}, insufficientFreshData={Insufficient}, no state changes.",
                            WorkerName,
                            result.CandidateStrategyCount,
                            result.EvaluatedStrategyCount,
                            result.InsufficientFreshDataCount);
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
                        new KeyValuePair<string, object?>("reason", "execution_quality_circuit_breaker_cycle"));
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                try
                {
                    await Task.Delay(
                        CalculateDelay(currentPollInterval, _consecutiveFailures),
                        _timeProvider,
                        stoppingToken);
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
            _logger.LogInformation("{Worker} stopping.", WorkerName);
        }
    }

    internal async Task<ExecutionQualityCircuitBreakerCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var writeContext = serviceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator = serviceProvider.GetRequiredService<IMediator>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (_distributedLock is null)
        {
            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate multi-instance cycles are possible.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, DistributedLockTimeout, ct);
            if (cycleLock is null)
                return ExecutionQualityCircuitBreakerCycleResult.Skipped(settings, "lock_busy");

            await using (cycleLock)
            {
                return await RunCycleCoreAsync(db, mediator, settings, ct);
            }
        }

        return await RunCycleCoreAsync(db, mediator, settings, ct);
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseInterval <= TimeSpan.Zero
                ? TimeSpan.FromMinutes(DefaultPollIntervalMinutes)
                : baseInterval;

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<ExecutionQualityCircuitBreakerCycleResult> RunCycleCoreAsync(
        DbContext db,
        IMediator mediator,
        ExecutionQualityCircuitBreakerSettings settings,
        CancellationToken ct)
    {
        var freshCutoffUtc = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-settings.LookbackDays);
        var candidates = await LoadCandidateStrategiesAsync(db, freshCutoffUtc, ct);
        if (candidates.Count == 0)
            return ExecutionQualityCircuitBreakerCycleResult.Empty(settings);

        int evaluatedCount = 0;
        int breachCount = 0;
        int pauseCount = 0;
        int resumeCount = 0;
        int warningCount = 0;
        int insufficientFreshDataCount = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var outcome = await EvaluateStrategyAsync(db, mediator, candidate, settings, freshCutoffUtc, ct);
            if (outcome.InsufficientFreshData)
            {
                insufficientFreshDataCount++;
                continue;
            }

            if (!outcome.Evaluated)
                continue;

            evaluatedCount++;
            if (outcome.Breached)
                breachCount++;
            if (outcome.Paused)
                pauseCount++;
            if (outcome.Resumed)
                resumeCount++;
            if (outcome.WarningLogged)
                warningCount++;
        }

        if (evaluatedCount > 0)
            _metrics?.ExecutionQualityStrategiesEvaluated.Add(evaluatedCount);

        if (insufficientFreshDataCount > 0)
            _metrics?.ExecutionQualityInsufficientFreshDataSkips.Add(insufficientFreshDataCount);

        return new ExecutionQualityCircuitBreakerCycleResult(
            settings,
            CandidateStrategyCount: candidates.Count,
            EvaluatedStrategyCount: evaluatedCount,
            BreachCount: breachCount,
            PauseCount: pauseCount,
            ResumeCount: resumeCount,
            WarningCount: warningCount,
            InsufficientFreshDataCount: insufficientFreshDataCount,
            SkippedReason: null);
    }

    private async Task<StrategyEvaluationOutcome> EvaluateStrategyAsync(
        DbContext db,
        IMediator mediator,
        CandidateStrategyInfo candidate,
        ExecutionQualityCircuitBreakerSettings settings,
        DateTime freshCutoffUtc,
        CancellationToken ct)
    {
        var logs = await db.Set<ExecutionQualityLog>()
            .AsNoTracking()
            .Where(log =>
                !log.IsDeleted &&
                log.StrategyId == candidate.Id &&
                log.RecordedAt >= freshCutoffUtc)
            .OrderByDescending(log => log.RecordedAt)
            .Take(settings.WindowFills)
            .ToListAsync(ct);

        if (logs.Count < settings.WindowFills)
        {
            _logger.LogDebug(
                "{Worker}: strategy {StrategyId} has only {Count}/{Window} fresh fills inside the last {LookbackDays} day(s) — skipping.",
                WorkerName,
                candidate.Id,
                logs.Count,
                settings.WindowFills,
                settings.LookbackDays);

            return StrategyEvaluationOutcome.ForInsufficientFreshData();
        }

        var metrics = CalculateWindowMetrics(logs, settings);

        _metrics?.ExecutionQualityAvgAbsoluteSlippagePips.Record(metrics.AvgAbsoluteSlippagePips);
        if (metrics.HasLatencySamples)
            _metrics?.ExecutionQualityAvgLatencyMs.Record(metrics.AvgLatencyMs);
        _metrics?.ExecutionQualityAvgFillRate.Record(metrics.AvgFillRate);

        if (metrics.SlippageBreached)
        {
            _metrics?.ExecutionQualityBreaches.Add(
                1,
                new KeyValuePair<string, object?>("metric", "slippage"));
        }

        if (metrics.LatencyBreached)
        {
            _metrics?.ExecutionQualityBreaches.Add(
                1,
                new KeyValuePair<string, object?>("metric", "latency"));
        }

        if (metrics.FillRateBreached)
        {
            _metrics?.ExecutionQualityBreaches.Add(
                1,
                new KeyValuePair<string, object?>("metric", "fill_rate"));
        }

        if (!metrics.AnyBreach)
        {
            if (candidate.IsExecutionQualityPaused && settings.AutoPauseEnabled && metrics.FullyRecovered)
            {
                int resumed = await db.Set<Strategy>()
                    .Where(strategy =>
                        !strategy.IsDeleted &&
                        strategy.Id == candidate.Id &&
                        strategy.Status == StrategyStatus.Paused &&
                        strategy.PauseReason == ExecutionQualityPauseReason)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(strategy => strategy.Status, StrategyStatus.Active)
                        .SetProperty(strategy => strategy.PauseReason, (string?)null),
                        ct);

                if (resumed > 0)
                {
                    _metrics?.ExecutionQualityResumes.Add(1);
                    string contextJson = BuildDecisionContextJson(
                        "Recovery",
                        settings,
                        metrics,
                        Array.Empty<string>());

                    _logger.LogInformation(
                        "{Worker}: strategy {StrategyId} auto-resumed after execution quality recovery.",
                        WorkerName,
                        candidate.Id);

                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType = "Strategy",
                        EntityId = candidate.Id,
                        DecisionType = "ExecQualityRecovery",
                        Outcome = "Resumed",
                        Reason = BuildRecoveryReason(metrics),
                        ContextJson = contextJson,
                        Source = WorkerName
                    }, ct);

                    return StrategyEvaluationOutcome.ForResume();
                }
            }

            if (candidate.IsExecutionQualityPaused)
            {
                _logger.LogDebug(
                    "{Worker}: strategy {StrategyId} remains paused by execution-quality circuit breaker pending hysteresis recovery.",
                    WorkerName,
                    candidate.Id);
            }
            else
            {
                _logger.LogDebug(
                    "{Worker}: strategy {StrategyId} execution quality healthy (avgAbsSlippage={Slippage:F2}, avgLatency={Latency}, avgFillRate={FillRate:F3}).",
                    WorkerName,
                    candidate.Id,
                    metrics.AvgAbsoluteSlippagePips,
                    metrics.HasLatencySamples ? $"{metrics.AvgLatencyMs:F0} ms" : "n/a",
                    metrics.AvgFillRate);
            }

            return StrategyEvaluationOutcome.ForHealthyEvaluation();
        }

        var breachedMetrics = BuildBreachedMetricNames(metrics);
        string reason = BuildBreachReason(metrics, settings);
        string context = BuildDecisionContextJson("Breach", settings, metrics, breachedMetrics);

        _logger.LogWarning(
            "{Worker}: strategy {StrategyId} breached execution quality thresholds — {Reason}",
            WorkerName,
            candidate.Id,
            reason);

        if (settings.AutoPauseEnabled && candidate.Status == StrategyStatus.Active)
        {
            int paused = await db.Set<Strategy>()
                .Where(strategy =>
                    !strategy.IsDeleted &&
                    strategy.Id == candidate.Id &&
                    strategy.Status == StrategyStatus.Active)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(strategy => strategy.Status, StrategyStatus.Paused)
                    .SetProperty(strategy => strategy.PauseReason, ExecutionQualityPauseReason),
                    ct);

            if (paused > 0)
            {
                _metrics?.ExecutionQualityPauses.Add(1);
                await mediator.Send(new LogDecisionCommand
                {
                    EntityType = "Strategy",
                    EntityId = candidate.Id,
                    DecisionType = "ExecQualityCircuitBreak",
                    Outcome = "Paused",
                    Reason = reason,
                    ContextJson = context,
                    Source = WorkerName
                }, ct);

                return StrategyEvaluationOutcome.ForPause();
            }

            return StrategyEvaluationOutcome.ForBreachWithoutStateChange();
        }

        if (!settings.AutoPauseEnabled && candidate.Status == StrategyStatus.Active)
        {
            _metrics?.ExecutionQualityWarnings.Add(1);
            await mediator.Send(new LogDecisionCommand
            {
                EntityType = "Strategy",
                EntityId = candidate.Id,
                DecisionType = "ExecQualityWarning",
                Outcome = "Warning",
                Reason = reason + " (AutoPause disabled)",
                ContextJson = context,
                Source = WorkerName
            }, ct);

            return StrategyEvaluationOutcome.ForWarning();
        }

        return StrategyEvaluationOutcome.ForBreachWithoutStateChange();
    }

    private static StrategyWindowMetrics CalculateWindowMetrics(
        IReadOnlyList<ExecutionQualityLog> logs,
        ExecutionQualityCircuitBreakerSettings settings)
    {
        double avgAbsoluteSlippagePips = logs.Average(log => (double)Math.Abs(log.SlippagePips));

        var latencySamples = logs
            .Where(log => log.SubmitToFillMs > 0)
            .Select(log => (double)log.SubmitToFillMs)
            .ToList();

        bool hasLatencySamples = latencySamples.Count > 0;
        double avgLatencyMs = hasLatencySamples ? latencySamples.Average() : 0.0;

        double avgFillRate = logs.Average(log => Clamp((double)log.FillRate, MinAverageFillRate, MaxAverageFillRate));
        bool fillRateMonitoringEnabled = settings.MinAverageFillRate > 0.0;

        double slippageRecoveryThresholdPips = settings.MaxAverageAbsoluteSlippagePips * (1.0 - settings.HysteresisMargin);
        double latencyRecoveryThresholdMs = settings.MaxAverageLatencyMs * (1.0 - settings.HysteresisMargin);
        double fillRateRecoveryThreshold = fillRateMonitoringEnabled
            ? Math.Min(1.0, settings.MinAverageFillRate + ((1.0 - settings.MinAverageFillRate) * settings.HysteresisMargin))
            : 0.0;

        return new StrategyWindowMetrics(
            SampleSize: logs.Count,
            AvgAbsoluteSlippagePips: avgAbsoluteSlippagePips,
            AvgLatencyMs: avgLatencyMs,
            HasLatencySamples: hasLatencySamples,
            AvgFillRate: avgFillRate,
            FillRateMonitoringEnabled: fillRateMonitoringEnabled,
            OldestLogRecordedAtUtc: NormalizeUtc(logs[^1].RecordedAt),
            NewestLogRecordedAtUtc: NormalizeUtc(logs[0].RecordedAt),
            SlippageBreached: avgAbsoluteSlippagePips > settings.MaxAverageAbsoluteSlippagePips,
            LatencyBreached: hasLatencySamples && avgLatencyMs > settings.MaxAverageLatencyMs,
            FillRateBreached: fillRateMonitoringEnabled && avgFillRate < settings.MinAverageFillRate,
            SlippageRecoveryThresholdPips: slippageRecoveryThresholdPips,
            LatencyRecoveryThresholdMs: latencyRecoveryThresholdMs,
            FillRateRecoveryThreshold: fillRateRecoveryThreshold);
    }

    private async Task<ExecutionQualityCircuitBreakerSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        int configuredPollMinutes = await GetIntAsync(db, CK_PollMins, DefaultPollIntervalMinutes, ct);
        int configuredWindowFills = await GetIntAsync(db, CK_WindowFills, DefaultWindowFills, ct);
        double configuredMaxSlippage = await GetDoubleAsync(db, CK_MaxSlippage, DefaultMaxAverageAbsoluteSlippagePips, ct);
        double configuredMaxLatencyMs = await GetDoubleAsync(db, CK_MaxLatencyMs, DefaultMaxAverageLatencyMs, ct);
        bool autoPauseEnabled = await GetBoolAsync(db, CK_AutoPause, true, ct);
        double configuredHysteresisMargin = await GetDoubleAsync(db, CK_HysteresisMargin, DefaultHysteresisMargin, ct);
        int configuredLookbackDays = await GetIntAsync(db, CK_LookbackDays, DefaultLookbackDays, ct);
        double configuredMinAvgFillRate = await GetDoubleAsync(db, CK_MinAvgFillRate, DefaultMinAverageFillRate, ct);

        int pollMinutes = Clamp(configuredPollMinutes, MinPollIntervalMinutes, MaxPollIntervalMinutes);
        int windowFills = Clamp(configuredWindowFills, MinWindowFills, MaxWindowFills);
        double maxAverageSlippagePips = NormalizePositiveThreshold(
            configuredMaxSlippage,
            DefaultMaxAverageAbsoluteSlippagePips,
            MaxAllowedAverageAbsoluteSlippagePips);
        double maxAverageLatencyMs = NormalizePositiveThreshold(
            configuredMaxLatencyMs,
            DefaultMaxAverageLatencyMs,
            MaxAllowedAverageLatencyMs);
        double hysteresisMargin = Clamp(
            configuredHysteresisMargin,
            MinHysteresisMargin,
            MaxHysteresisMargin);
        int lookbackDays = configuredLookbackDays <= 0
            ? DefaultLookbackDays
            : Clamp(configuredLookbackDays, MinLookbackDays, MaxLookbackDays);
        double minAverageFillRate = Clamp(
            configuredMinAvgFillRate,
            MinAverageFillRate,
            MaxAverageFillRate);

        LogNormalizedSetting(CK_PollMins, configuredPollMinutes, pollMinutes);
        LogNormalizedSetting(CK_WindowFills, configuredWindowFills, windowFills);
        LogNormalizedSetting(CK_MaxSlippage, configuredMaxSlippage, maxAverageSlippagePips);
        LogNormalizedSetting(CK_MaxLatencyMs, configuredMaxLatencyMs, maxAverageLatencyMs);
        LogNormalizedSetting(CK_HysteresisMargin, configuredHysteresisMargin, hysteresisMargin);
        LogNormalizedSetting(CK_LookbackDays, configuredLookbackDays, lookbackDays);
        LogNormalizedSetting(CK_MinAvgFillRate, configuredMinAvgFillRate, minAverageFillRate);

        return new ExecutionQualityCircuitBreakerSettings(
            PollInterval: TimeSpan.FromMinutes(pollMinutes),
            WindowFills: windowFills,
            MaxAverageAbsoluteSlippagePips: maxAverageSlippagePips,
            MaxAverageLatencyMs: maxAverageLatencyMs,
            AutoPauseEnabled: autoPauseEnabled,
            HysteresisMargin: hysteresisMargin,
            LookbackDays: lookbackDays,
            MinAverageFillRate: minAverageFillRate);
    }

    private void LogNormalizedSetting<T>(string key, T configuredValue, T effectiveValue)
        where T : IEquatable<T>
    {
        if (configuredValue.Equals(effectiveValue))
            return;

        _logger.LogDebug(
            "{Worker}: normalized config {Key} from {Configured} to {Effective}.",
            WorkerName,
            key,
            configuredValue,
            effectiveValue);
    }

    private static async Task<List<CandidateStrategyInfo>> LoadCandidateStrategiesAsync(
        DbContext db,
        DateTime freshCutoffUtc,
        CancellationToken ct)
    {
        return await db.Set<Strategy>()
            .AsNoTracking()
            .Where(strategy =>
                !strategy.IsDeleted &&
                (strategy.Status == StrategyStatus.Active ||
                 (strategy.Status == StrategyStatus.Paused && strategy.PauseReason == ExecutionQualityPauseReason)) &&
                db.Set<ExecutionQualityLog>().Any(log =>
                    !log.IsDeleted &&
                    log.StrategyId == strategy.Id &&
                    log.RecordedAt >= freshCutoffUtc))
            .OrderBy(strategy => strategy.Id)
            .Select(strategy => new CandidateStrategyInfo(
                strategy.Id,
                strategy.Status,
                strategy.PauseReason))
            .ToListAsync(ct);
    }

    private static string BuildBreachReason(
        StrategyWindowMetrics metrics,
        ExecutionQualityCircuitBreakerSettings settings)
    {
        var reasons = new List<string>(3);

        if (metrics.SlippageBreached)
        {
            reasons.Add(
                $"avgAbsSlippage={metrics.AvgAbsoluteSlippagePips:F2} pips > threshold {settings.MaxAverageAbsoluteSlippagePips:F2} pips");
        }

        if (metrics.LatencyBreached)
        {
            reasons.Add(
                $"avgLatency={metrics.AvgLatencyMs:F0} ms > threshold {settings.MaxAverageLatencyMs:F0} ms");
        }

        if (metrics.FillRateBreached)
        {
            reasons.Add(
                $"avgFillRate={metrics.AvgFillRate:F3} < threshold {settings.MinAverageFillRate:F3}");
        }

        return string.Join("; ", reasons) + $" (over last {metrics.SampleSize} fresh fills)";
    }

    private static string BuildRecoveryReason(StrategyWindowMetrics metrics)
    {
        var reasons = new List<string>(3)
        {
            $"avgAbsSlippage={metrics.AvgAbsoluteSlippagePips:F2} pips <= recovery {metrics.SlippageRecoveryThresholdPips:F2} pips"
        };

        if (metrics.HasLatencySamples)
        {
            reasons.Add(
                $"avgLatency={metrics.AvgLatencyMs:F0} ms <= recovery {metrics.LatencyRecoveryThresholdMs:F0} ms");
        }

        if (metrics.FillRateMonitoringEnabled)
        {
            reasons.Add(
                $"avgFillRate={metrics.AvgFillRate:F3} >= recovery {metrics.FillRateRecoveryThreshold:F3}");
        }

        return string.Join("; ", reasons);
    }

    private static string[] BuildBreachedMetricNames(StrategyWindowMetrics metrics)
    {
        var breached = new List<string>(3);
        if (metrics.SlippageBreached)
            breached.Add("slippage");
        if (metrics.LatencyBreached)
            breached.Add("latency");
        if (metrics.FillRateBreached)
            breached.Add("fill_rate");
        return breached.ToArray();
    }

    private static string BuildDecisionContextJson(
        string operation,
        ExecutionQualityCircuitBreakerSettings settings,
        StrategyWindowMetrics metrics,
        IReadOnlyList<string> breachedMetrics)
    {
        return JsonSerializer.Serialize(new
        {
            operation,
            sampleSize = metrics.SampleSize,
            lookbackDays = settings.LookbackDays,
            autoPauseEnabled = settings.AutoPauseEnabled,
            avgAbsoluteSlippagePips = Math.Round(metrics.AvgAbsoluteSlippagePips, 6),
            maxAverageAbsoluteSlippagePips = settings.MaxAverageAbsoluteSlippagePips,
            avgLatencyMs = metrics.HasLatencySamples ? Math.Round(metrics.AvgLatencyMs, 3) : (double?)null,
            maxAverageLatencyMs = settings.MaxAverageLatencyMs,
            avgFillRate = Math.Round(metrics.AvgFillRate, 6),
            minAverageFillRate = settings.MinAverageFillRate > 0.0 ? settings.MinAverageFillRate : (double?)null,
            hysteresisMarginPct = settings.HysteresisMargin,
            slippageRecoveryThresholdPips = Math.Round(metrics.SlippageRecoveryThresholdPips, 6),
            latencyRecoveryThresholdMs = metrics.HasLatencySamples ? Math.Round(metrics.LatencyRecoveryThresholdMs, 3) : (double?)null,
            fillRateRecoveryThreshold = metrics.FillRateMonitoringEnabled ? Math.Round(metrics.FillRateRecoveryThreshold, 6) : (double?)null,
            oldestLogRecordedAtUtc = metrics.OldestLogRecordedAtUtc,
            newestLogRecordedAtUtc = metrics.NewestLogRecordedAtUtc,
            breachedMetrics = breachedMetrics.Count > 0 ? breachedMetrics : null
        });
    }

    private static async Task<int> GetIntAsync(DbContext db, string key, int defaultValue, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => !config.IsDeleted && config.Key == key)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(ct);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    private static async Task<double> GetDoubleAsync(DbContext db, string key, double defaultValue, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => !config.IsDeleted && config.Key == key)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(ct);

        return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    private static async Task<bool> GetBoolAsync(DbContext db, string key, bool defaultValue, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => !config.IsDeleted && config.Key == key)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(ct);

        if (raw is null)
            return defaultValue;

        if (bool.TryParse(raw, out var boolValue))
            return boolValue;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            return intValue != 0;

        return defaultValue;
    }

    private static double NormalizePositiveThreshold(double configuredValue, double defaultValue, double maxAllowedValue)
    {
        if (double.IsNaN(configuredValue) || double.IsInfinity(configuredValue) || configuredValue <= 0.0)
            return defaultValue;

        return Math.Min(configuredValue, maxAllowedValue);
    }

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    private static double Clamp(double value, double min, double max)
        => Math.Min(Math.Max(value, min), max);

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private readonly record struct CandidateStrategyInfo(
        long Id,
        StrategyStatus Status,
        string? PauseReason)
    {
        public bool IsExecutionQualityPaused
            => Status == StrategyStatus.Paused &&
               string.Equals(PauseReason, ExecutionQualityPauseReason, StringComparison.Ordinal);
    }

    private readonly record struct StrategyWindowMetrics(
        int SampleSize,
        double AvgAbsoluteSlippagePips,
        double AvgLatencyMs,
        bool HasLatencySamples,
        double AvgFillRate,
        bool FillRateMonitoringEnabled,
        DateTime OldestLogRecordedAtUtc,
        DateTime NewestLogRecordedAtUtc,
        bool SlippageBreached,
        bool LatencyBreached,
        bool FillRateBreached,
        double SlippageRecoveryThresholdPips,
        double LatencyRecoveryThresholdMs,
        double FillRateRecoveryThreshold)
    {
        public bool AnyBreach => SlippageBreached || LatencyBreached || FillRateBreached;

        public bool FullyRecovered =>
            AvgAbsoluteSlippagePips <= SlippageRecoveryThresholdPips &&
            (!HasLatencySamples || AvgLatencyMs <= LatencyRecoveryThresholdMs) &&
            (!FillRateMonitoringEnabled || AvgFillRate >= FillRateRecoveryThreshold);
    }

    private readonly record struct StrategyEvaluationOutcome(
        bool Evaluated,
        bool Breached,
        bool Paused,
        bool Resumed,
        bool WarningLogged,
        bool InsufficientFreshData)
    {
        public static StrategyEvaluationOutcome ForHealthyEvaluation()
            => new(true, false, false, false, false, false);

        public static StrategyEvaluationOutcome ForPause()
            => new(true, true, true, false, false, false);

        public static StrategyEvaluationOutcome ForResume()
            => new(true, false, false, true, false, false);

        public static StrategyEvaluationOutcome ForWarning()
            => new(true, true, false, false, true, false);

        public static StrategyEvaluationOutcome ForBreachWithoutStateChange()
            => new(true, true, false, false, false, false);

        public static StrategyEvaluationOutcome ForInsufficientFreshData()
            => new(false, false, false, false, false, true);
    }

    internal readonly record struct ExecutionQualityCircuitBreakerSettings(
        TimeSpan PollInterval,
        int WindowFills,
        double MaxAverageAbsoluteSlippagePips,
        double MaxAverageLatencyMs,
        bool AutoPauseEnabled,
        double HysteresisMargin,
        int LookbackDays,
        double MinAverageFillRate);

    internal readonly record struct ExecutionQualityCircuitBreakerCycleResult(
        ExecutionQualityCircuitBreakerSettings Settings,
        int CandidateStrategyCount,
        int EvaluatedStrategyCount,
        int BreachCount,
        int PauseCount,
        int ResumeCount,
        int WarningCount,
        int InsufficientFreshDataCount,
        string? SkippedReason)
    {
        public static ExecutionQualityCircuitBreakerCycleResult Empty(ExecutionQualityCircuitBreakerSettings settings)
            => new(
                settings,
                CandidateStrategyCount: 0,
                EvaluatedStrategyCount: 0,
                BreachCount: 0,
                PauseCount: 0,
                ResumeCount: 0,
                WarningCount: 0,
                InsufficientFreshDataCount: 0,
                SkippedReason: null);

        public static ExecutionQualityCircuitBreakerCycleResult Skipped(
            ExecutionQualityCircuitBreakerSettings settings,
            string reason)
            => new(
                settings,
                CandidateStrategyCount: 0,
                EvaluatedStrategyCount: 0,
                BreachCount: 0,
                PauseCount: 0,
                ResumeCount: 0,
                WarningCount: 0,
                InsufficientFreshDataCount: 0,
                SkippedReason: reason);
    }
}
