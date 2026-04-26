using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Ensures every supported and unblocked <see cref="LearnerArchitecture"/> receives at least
/// a minimum number of recent training opportunities per active symbol/timeframe.
/// </summary>
/// <remarks>
/// The worker measures successful completed runs plus fresh in-flight runs toward the rotation
/// quota. Failed runs do not satisfy the quota, and stale queued/running rows do not block
/// rotation forever. Architectures can be temporarily suppressed after recent infrastructure
/// failures (for example Torch/libtorch bootstrap failures) or after exceeding a non-infra
/// failure budget within the rotation window, without being banned permanently.
/// </remarks>
public sealed partial class MLArchitectureRotationWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLArchitectureRotationWorker);

    private const string DistributedLockKey = "workers:ml-architecture-rotation:cycle";
    private const string StaleContextAlertDeduplicationPrefix = "ml-architecture-rotation-stale:";
    private const int AlertConditionMaxLength = 1000;

    // Knob names that are overridable per (Symbol, Timeframe) context. The override-token
    // validator flags any override key whose final segment isn't in this set so operators
    // see typos like "WidnowDays" instead of having the row silently fall through to the
    // global default.
    private static readonly string[] ValidOverrideKnobs =
    [
        "MinRunsPerWindow",
        "WindowDays",
        "CooldownMinutes",
        "MaxFailuresPerWindow",
        "ActiveRunFreshnessHours",
        "InfraFailureLookbackHours",
        "TrainingDataWindowDays",
    ];

    private const string CK_Enabled = "MLArchitectureRotation:Enabled";
    private const string CK_PollSecs = "MLArchitectureRotation:PollIntervalSeconds";
    private const string CK_MinRuns = "MLArchitectureRotation:MinRunsPerWindow";
    private const string CK_WindowDays = "MLArchitectureRotation:WindowDays";
    private const string CK_CooldownMinutes = "MLArchitectureRotation:CooldownMinutes";
    private const string CK_TrainWindowDays = "MLTraining:TrainingDataWindowDays";
    private const string CK_BlockedArchitectures = "MLTraining:BlockedArchitectures";
    private const string CK_LockTimeoutSeconds = "MLArchitectureRotation:LockTimeoutSeconds";
    private const string CK_MaxContextsPerCycle = "MLArchitectureRotation:MaxContextsPerCycle";
    private const string CK_ActiveRunFreshnessHours = "MLArchitectureRotation:ActiveRunFreshnessHours";
    private const string CK_InfraFailureLookbackHours = "MLArchitectureRotation:InfraFailureLookbackHours";
    private const string CK_MaxPendingScheduledRuns = "MLArchitectureRotation:MaxPendingScheduledRuns";
    private const string CK_MaxFailuresPerWindow = "MLArchitectureRotation:MaxFailuresPerWindow";
    private const string CK_InfraFailurePatterns = "MLArchitectureRotation:InfraFailurePatterns";
    private const string CK_MaxDegreeOfParallelism = "MLArchitectureRotation:MaxDegreeOfParallelism";
    private const string CK_LongCycleWarnSeconds = "MLArchitectureRotation:LongCycleWarnSeconds";
    private const string CK_StaleContextAlertEnabled = "MLArchitectureRotation:StaleContextAlertEnabled";

    private const int DefaultPollSeconds = 2 * 60 * 60;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 7 * 24 * 60 * 60;

    private const int DefaultMinRunsPerWindow = 2;
    private const int MinMinRunsPerWindow = 1;
    private const int MaxMinRunsPerWindow = 100;

    private const int DefaultWindowDays = 7;
    private const int MinWindowDays = 1;
    private const int MaxWindowDays = 365;

    private const int DefaultCooldownMinutes = 60;
    private const int MinCooldownMinutes = 0;
    private const int MaxCooldownMinutes = 7 * 24 * 60;

    private const int DefaultTrainingWindowDays = 365;
    private const int MinTrainingWindowDays = 30;
    private const int MaxTrainingWindowDays = 3650;

    private const int DefaultLockTimeoutSeconds = 5;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private const int DefaultMaxContextsPerCycle = 128;
    private const int MinMaxContextsPerCycle = 1;
    private const int MaxMaxContextsPerCycle = 10_000;

    private const int DefaultActiveRunFreshnessHours = 24;
    private const int MinActiveRunFreshnessHours = 1;
    private const int MaxActiveRunFreshnessHours = 24 * 30;

    private const int DefaultInfraFailureLookbackHours = 24;
    private const int MinInfraFailureLookbackHours = 1;
    private const int MaxInfraFailureLookbackHours = 24 * 30;

    private const int DefaultMaxPendingScheduledRuns = 1000;
    private const int MinMaxPendingScheduledRuns = 10;
    private const int MaxMaxPendingScheduledRuns = 1_000_000;

    private const int DefaultMaxFailuresPerWindow = 3;
    private const int MinMaxFailuresPerWindow = 1;
    private const int MaxMaxFailuresPerWindow = 100;

    // Bounded in-process concurrency for per-context evaluation. Default 1 preserves
    // strictly-sequential semantics; bumping fans out to N concurrent (context, save,
    // metrics) chains, each in its own DI scope. Per-iteration save isolation means
    // a single context's persistence failure no longer rolls back the whole cycle.
    private const int DefaultMaxDegreeOfParallelism = 1;
    private const int MinMaxDegreeOfParallelism = 1;
    private const int MaxMaxDegreeOfParallelism = 16;

    // Wall-clock cycle warning threshold. The cycle-level distributed lock is held for
    // the duration of one cycle; if cycle wall-time approaches the lock TTL the lock
    // can be re-acquired by another replica before this one finishes. The duration
    // histogram with the parallelism tag is the source-of-truth alerting signal; this
    // log is the operator's prompt to verify the IDistributedLock TTL is at least that
    // long.
    private const int DefaultLongCycleWarnSeconds = 300;
    private const int MinLongCycleWarnSeconds = 0;
    private const int MaxLongCycleWarnSeconds = 24 * 60 * 60;

    private const int BasePriority = 5;
    private const int StarvedPriority = 10;

    private static readonly IReadOnlyList<string> DefaultInfraFailurePatterns =
    [
        "torchsharp",
        "libtorch",
        "unable to load shared library",
        "dllnotfoundexception",
        "no service for type",
        "no keyed service",
    ];

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLArchitectureRotationWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;
    private readonly Func<int> _randomSeedFactory;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    // Hashed signature of the unmatched-tokens set last reported by the override-key
    // validator. Same dedup primitive as the calibration / edge workers. 0 = empty.
    private long _lastUnmatchedTokensSignature;

    private readonly record struct ActiveContext(string Symbol, Timeframe Timeframe);

    private readonly record struct TrainingRunProjection(
        string Symbol,
        Timeframe Timeframe,
        LearnerArchitecture Architecture,
        RunStatus Status,
        DateTime StartedAt,
        DateTime? PickedUpAt,
        DateTime? CompletedAt,
        string? ErrorMessage);

    private readonly record struct ContextRotationOutcome(
        int QueuedRuns,
        int SkippedArchitectures,
        bool BackpressureHit,
        bool AllEligibleSuppressed);

    /// <summary>
    /// Mutable per-cycle state shared by every iteration of the parallel context loop.
    /// Wrapped in one heap object so the parallel lambda captures <c>ctx</c> instead of
    /// N individual locals; counters atomic-incremented through <c>ref ctx.Field</c>.
    /// </summary>
    /// <remarks>
    /// Public mutable fields are deliberate — the only way to provide stable addresses
    /// for <c>Interlocked.Increment(ref ctx.Field)</c> from outside. Class is private
    /// and scoped to one in-flight cycle (cycles are serialised by the cycle-level
    /// distributed lock), so the open-mutable shape never escapes.
    /// </remarks>
    private sealed class CycleIteration
    {
        public required MLArchitectureRotationWorkerSettings Settings;
        public DateTime NowUtc;
        public required IReadOnlyList<LearnerArchitecture> EligibleArchitectures;
        public required IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), List<TrainingRunProjection>> RunsByContext;
        public required IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>> OverridesByContext;
        public ConcurrentDictionary<(string Symbol, Timeframe Timeframe), byte> ActiveStaleContextAlertKeys = new();

        // Counters mutated atomically by Interlocked through `ref ctx.Field`.
        public int ContextsProcessed;
        public int QueuedRuns;
        public int SkippedArchitectures;
        public int FailedContexts;
        public int RemainingContexts;
        public int StaleContextAlertsDispatched;
        public int StaleContextAlertsResolved;

        // Per-cycle queue budget. Atomic-decremented per individual queue attempt; on
        // negative result the run is not queued and the budget is restored. 0 = exhausted.
        public int RemainingPendingQueueBudget;
        public bool BackpressureHit;
    }

    public MLArchitectureRotationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLArchitectureRotationWorker> logger,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null,
        Func<int>? randomSeedFactory = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
        _randomSeedFactory = randomSeedFactory ?? (() => unchecked((int)_timeProvider.GetUtcNow().UtcTicks));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Maintains fair architecture exploration by queueing scheduled ML training runs for underrepresented learner architectures per active symbol/timeframe.",
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
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.ContextCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("parallelism", result.Settings.MaxDegreeOfParallelism));
                    _metrics?.MLArchitectureRotationCycleDurationMs.Record(durationMs);
                    _metrics?.MLArchitectureRotationQueuedRunsPerCycle.Record(result.QueuedRunCount);

                    // Long-cycle guard: warn when wall-time approaches the lock TTL window.
                    // The cycle-level distributed lock is held for the entire cycle, so a
                    // long cycle risks the lock expiring and another replica re-acquiring
                    // before this one finishes. The duration histogram with the parallelism
                    // tag is the source-of-truth alerting signal; this log is the operator's
                    // prompt to verify the IDistributedLock TTL is at least this long.
                    int warnSec = result.Settings.LongCycleWarnSeconds;
                    if (warnSec > 0 && durationMs > warnSec * 1000L)
                    {
                        _logger.LogWarning(
                            "{Worker}: cycle wall-time {DurationMs}ms exceeded LongCycleWarnSeconds={WarnSec}s. Verify the IDistributedLock TTL is at least this long; otherwise another replica may re-acquire the cycle lock mid-flight.",
                            WorkerName, durationMs, warnSec);
                    }

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
                            "{Worker}: contexts={Contexts}, processed={Processed}, eligibleArchitectures={EligibleArchitectures}, queuedRuns={Queued}, skippedArchitectures={Skipped}, failedContexts={FailedContexts}, staleAlertsDispatched={StaleDispatched}, staleAlertsResolved={StaleResolved}, backpressureHit={Backpressure}.",
                            WorkerName,
                            result.ContextCount,
                            result.ContextsProcessed,
                            result.EligibleArchitectureCount,
                            result.QueuedRunCount,
                            result.SkippedArchitectureCount,
                            result.FailedContextCount,
                            result.StaleContextAlertsDispatched,
                            result.StaleContextAlertsResolved,
                            result.BackpressureHit);
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
                        new KeyValuePair<string, object?>("reason", "ml_architecture_rotation_cycle"));
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

    internal async Task<MLArchitectureRotationCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var writeContext = serviceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (!settings.Enabled)
        {
            _metrics?.MLArchitectureRotationCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "disabled"));
            return MLArchitectureRotationCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.MLArchitectureRotationLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate architecture rotation cycles are possible in multi-instance deployments.",
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
                _metrics?.MLArchitectureRotationLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.MLArchitectureRotationCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return MLArchitectureRotationCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLArchitectureRotationLockAttempts.Add(
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

    private async Task<MLArchitectureRotationCycleResult> RunCycleCoreAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        MLArchitectureRotationWorkerSettings settings,
        CancellationToken ct)
    {
        var eligibleArchitectures = ResolveEligibleArchitectures(serviceProvider, settings.BlockedArchitectures);
        if (eligibleArchitectures.Count == 0)
        {
            _metrics?.MLArchitectureRotationCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_eligible_architectures"));
            return MLArchitectureRotationCycleResult.Skipped(settings, "no_eligible_architectures");
        }

        var allContexts = await db.Set<MLModel>()
            .AsNoTracking()
            .Where(model => model.IsActive
                         && !model.IsDeleted
                         && model.Symbol != "ALL")
            .Select(model => new { model.Symbol, model.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        if (allContexts.Count == 0)
        {
            _metrics?.MLArchitectureRotationCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_active_contexts"));
            return MLArchitectureRotationCycleResult.Skipped(settings, "no_active_contexts");
        }

        var shuffledContexts = allContexts
            .Select(row => new ActiveContext(row.Symbol, row.Timeframe))
            .ToList();
        ShuffleInPlace(shuffledContexts, _randomSeedFactory());
        var contexts = shuffledContexts.Count <= settings.MaxContextsPerCycle
            ? shuffledContexts
            : shuffledContexts.Take(settings.MaxContextsPerCycle).ToList();

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        int currentPendingScheduled = await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .CountAsync(
                run => !run.IsDeleted
                    && run.TriggerType == TriggerType.Scheduled
                    && run.Status == RunStatus.Queued,
                ct);

        // Single broad-prefix scan over override rows; bucket per (Symbol, Timeframe)
        // in-memory and run the override-token validator over the same list.
        var allOverrideRows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("MLArchitectureRotation:Override:"))
            .Select(c => new KeyValuePair<string, string>(c.Key, c.Value))
            .ToListAsync(ct);
        ValidateOverrideTokens(allOverrideRows);
        var overridesByContext = BucketOverridesByContext(contexts, allOverrideRows);

        var runsByContext = await BatchLoadRunsAsync(db, contexts, settings, nowUtc, ct);
        var activeStaleContextAlertKeys = await BatchLoadActiveStaleContextAlertKeysAsync(db, contexts, ct);

        int initialQueueBudget = Math.Max(0, settings.MaxPendingScheduledRuns - currentPendingScheduled);
        if (initialQueueBudget == 0)
        {
            _metrics?.MLArchitectureRotationCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "queue_backpressure"));
            _logger.LogDebug(
                "{Worker}: queue backpressure hit at cycle start (pending={Pending} >= max={Max}); deferring all contexts.",
                WorkerName,
                currentPendingScheduled,
                settings.MaxPendingScheduledRuns);
            return new MLArchitectureRotationCycleResult(
                settings,
                SkippedReason: null,
                ContextCount: contexts.Count,
                ContextsProcessed: 0,
                EligibleArchitectureCount: eligibleArchitectures.Count,
                QueuedRunCount: 0,
                SkippedArchitectureCount: 0,
                BackpressureHit: true,
                FailedContextCount: 0,
                StaleContextAlertsDispatched: 0,
                StaleContextAlertsResolved: 0);
        }

        var ctx = new CycleIteration
        {
            Settings = settings,
            NowUtc = nowUtc,
            EligibleArchitectures = eligibleArchitectures,
            RunsByContext = runsByContext,
            OverridesByContext = overridesByContext,
            RemainingContexts = contexts.Count,
            RemainingPendingQueueBudget = initialQueueBudget,
        };
        foreach (var key in activeStaleContextAlertKeys)
            ctx.ActiveStaleContextAlertKeys.TryAdd(key, 0);

        // Per-context evaluation counter — emitted up front so the metric reflects total
        // contexts entering the parallel block, not just the ones that completed without
        // throwing.
        if (_metrics is not null)
        {
            for (int i = 0; i < contexts.Count; i++)
                _metrics.MLArchitectureRotationContextsEvaluated.Add(1);
        }

        int parallelism = Math.Clamp(settings.MaxDegreeOfParallelism, 1, MaxMaxDegreeOfParallelism);

        await Parallel.ForEachAsync(
            contexts,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = ct,
            },
            async (context, contextCt) => await EvaluateOneContextAsync(ctx, context, contextCt))
            .ConfigureAwait(false);

        return new MLArchitectureRotationCycleResult(
            settings,
            SkippedReason: null,
            ContextCount: contexts.Count,
            ContextsProcessed: ctx.ContextsProcessed,
            EligibleArchitectureCount: eligibleArchitectures.Count,
            QueuedRunCount: ctx.QueuedRuns,
            SkippedArchitectureCount: ctx.SkippedArchitectures,
            BackpressureHit: ctx.BackpressureHit,
            FailedContextCount: ctx.FailedContexts,
            StaleContextAlertsDispatched: ctx.StaleContextAlertsDispatched,
            StaleContextAlertsResolved: ctx.StaleContextAlertsResolved);
    }

    private static async Task<HashSet<(string Symbol, Timeframe Timeframe)>> BatchLoadActiveStaleContextAlertKeysAsync(
        DbContext db,
        IReadOnlyList<ActiveContext> contexts,
        CancellationToken ct)
    {
        if (contexts.Count == 0)
            return [];

        var dedupKeys = contexts
            .Select(context => StaleContextDeduplicationKey(context.Symbol, context.Timeframe))
            .ToList();

        var matchedKeys = await db.Set<Alert>()
            .AsNoTracking()
            .Where(alert => !alert.IsDeleted
                         && alert.IsActive
                         && alert.DeduplicationKey != null
                         && dedupKeys.Contains(alert.DeduplicationKey))
            .Select(alert => alert.DeduplicationKey!)
            .ToListAsync(ct);

        var result = new HashSet<(string Symbol, Timeframe Timeframe)>();
        foreach (var key in matchedKeys)
        {
            if (TryParseStaleContextDeduplicationKey(key, out var symbol, out var timeframe))
                result.Add((symbol, timeframe));
        }
        return result;
    }

    private static string StaleContextDeduplicationKey(string symbol, Timeframe timeframe)
        => $"{StaleContextAlertDeduplicationPrefix}{symbol}:{timeframe}";

    private static bool TryParseStaleContextDeduplicationKey(string key, out string symbol, out Timeframe timeframe)
    {
        symbol = string.Empty;
        timeframe = default;
        if (!key.StartsWith(StaleContextAlertDeduplicationPrefix, StringComparison.Ordinal))
            return false;

        var rest = key[StaleContextAlertDeduplicationPrefix.Length..];
        int colon = rest.LastIndexOf(':');
        if (colon <= 0 || colon >= rest.Length - 1) return false;

        symbol = rest[..colon];
        return Enum.TryParse(rest[(colon + 1)..], ignoreCase: true, out timeframe);
    }

    private static async Task<Dictionary<(string Symbol, Timeframe Timeframe), List<TrainingRunProjection>>> BatchLoadRunsAsync(
        DbContext db,
        IReadOnlyList<ActiveContext> contexts,
        MLArchitectureRotationWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var contextLookup = new HashSet<(string Symbol, Timeframe Timeframe)>();
        foreach (var context in contexts)
            contextLookup.Add((context.Symbol, context.Timeframe));

        var symbols = contexts.Select(c => c.Symbol).Distinct().ToList();
        var timeframes = contexts.Select(c => c.Timeframe).Distinct().ToList();

        var windowCutoff = nowUtc.AddDays(-settings.WindowDays);
        var cooldownCutoff = nowUtc.AddMinutes(-settings.CooldownMinutes);
        var infraFailureCutoff = nowUtc.AddHours(-settings.InfraFailureLookbackHours);
        var activeRunFreshnessCutoff = nowUtc.AddHours(-settings.ActiveRunFreshnessHours);
        var earliestRelevantCompletionCutoff = MinDate(windowCutoff, cooldownCutoff, infraFailureCutoff);

        var rows = await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .Where(run => !run.IsDeleted
                       && symbols.Contains(run.Symbol)
                       && timeframes.Contains(run.Timeframe)
                       && (((run.Status == RunStatus.Completed || run.Status == RunStatus.Failed)
                            && run.CompletedAt.HasValue
                            && run.CompletedAt >= earliestRelevantCompletionCutoff)
                           || ((run.Status == RunStatus.Queued || run.Status == RunStatus.Running)
                               && (run.PickedUpAt ?? run.StartedAt) >= activeRunFreshnessCutoff)))
            .Select(run => new TrainingRunProjection(
                run.Symbol,
                run.Timeframe,
                run.LearnerArchitecture,
                run.Status,
                run.StartedAt,
                run.PickedUpAt,
                run.CompletedAt,
                run.ErrorMessage))
            .ToListAsync(ct);

        var runsByContext = new Dictionary<(string Symbol, Timeframe Timeframe), List<TrainingRunProjection>>();
        foreach (var row in rows)
        {
            var key = (row.Symbol, row.Timeframe);
            if (!contextLookup.Contains(key))
                continue;

            if (!runsByContext.TryGetValue(key, out var bucket))
            {
                bucket = [];
                runsByContext[key] = bucket;
            }

            bucket.Add(row);
        }

        return runsByContext;
    }

    /// <summary>
    /// Per-iteration entry point invoked by the parallel context loop. Owns one DI scope
    /// per iteration so the per-context queue insert never crosses an EF state boundary,
    /// and re-throws cancellation cleanly so shutdown doesn't masquerade as context failure.
    /// </summary>
    private async ValueTask EvaluateOneContextAsync(
        CycleIteration ctx, ActiveContext context, CancellationToken ctxCt)
    {
        await using var contextScope = _scopeFactory.CreateAsyncScope();
        var contextWriteCtx = contextScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var contextDb = contextWriteCtx.GetDbContext();

        try
        {
            // Refresh the worker heartbeat before each context evaluation. Long cycles
            // (large fleet / DOP=1) would otherwise leave the health monitor without a
            // signal until cycle end.
            _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

            ctx.RunsByContext.TryGetValue((context.Symbol, context.Timeframe), out var contextRuns);
            var outcome = await ProcessContextAsync(
                contextScope.ServiceProvider, contextWriteCtx, contextDb, context, contextRuns ?? [], ctx, ctxCt);

            Interlocked.Increment(ref ctx.ContextsProcessed);
            Interlocked.Add(ref ctx.QueuedRuns, outcome.QueuedRuns);
            Interlocked.Add(ref ctx.SkippedArchitectures, outcome.SkippedArchitectures);
            if (outcome.BackpressureHit) ctx.BackpressureHit = true;

            // Stale-context alert lifecycle. Dispatch when every eligible architecture got
            // suppressed for THIS context (none queued, none recently handled). Auto-resolve
            // when the context successfully queues at least one run after a prior alert.
            if (ctx.Settings.StaleContextAlertEnabled && outcome.AllEligibleSuppressed && outcome.QueuedRuns == 0)
            {
                bool dispatched = await UpsertAndDispatchStaleContextAlertAsync(
                    contextScope.ServiceProvider, contextWriteCtx, contextDb, context, ctx.Settings, ctx.NowUtc, ctxCt);
                if (dispatched)
                {
                    ctx.ActiveStaleContextAlertKeys.TryAdd((context.Symbol, context.Timeframe), 0);
                    Interlocked.Increment(ref ctx.StaleContextAlertsDispatched);
                }
            }
            else if (outcome.QueuedRuns > 0
                  && ctx.ActiveStaleContextAlertKeys.ContainsKey((context.Symbol, context.Timeframe)))
            {
                bool resolved = await ResolveStaleContextAlertAsync(
                    contextScope.ServiceProvider, contextWriteCtx, contextDb, context, ctx.NowUtc, ctxCt);
                if (resolved)
                {
                    ctx.ActiveStaleContextAlertKeys.TryRemove((context.Symbol, context.Timeframe), out _);
                    Interlocked.Increment(ref ctx.StaleContextAlertsResolved);
                }
            }
        }
        catch (OperationCanceledException) when (ctxCt.IsCancellationRequested)
        {
            // Shutdown propagation, not a context failure. Re-throw so Parallel.ForEachAsync
            // surfaces it and the ExecuteAsync loop honours stoppingToken.
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref ctx.FailedContexts);
            _metrics?.MLArchitectureRotationCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "context_error"),
                new KeyValuePair<string, object?>("symbol", context.Symbol),
                new KeyValuePair<string, object?>("timeframe", context.Timeframe.ToString()),
                new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            _logger.LogWarning(
                ex,
                "{Worker}: failed to process context {Symbol}/{Timeframe}.",
                WorkerName,
                context.Symbol,
                context.Timeframe);
        }
        finally
        {
            int remaining = Interlocked.Decrement(ref ctx.RemainingContexts);
            _healthMonitor?.RecordBacklogDepth(WorkerName, remaining);
        }
    }

    private async Task<ContextRotationOutcome> ProcessContextAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveContext context,
        IReadOnlyList<TrainingRunProjection> contextRuns,
        CycleIteration ctx,
        CancellationToken ct)
    {
        // Apply per-context overrides (5-tier hierarchy: Symbol:Timeframe → Symbol:* →
        // *:Timeframe → *:* → defaults). Each context evaluates against its own effective
        // settings; the cycle-wide settings flow through unchanged when no overrides match.
        var overrides = ctx.OverridesByContext.TryGetValue((context.Symbol, context.Timeframe), out var ctxOverrides)
            ? ctxOverrides
            : new Dictionary<string, string>();
        var settings = ApplyPerContextOverrides(ctx.Settings, overrides, context.Symbol, context.Timeframe);
        var nowUtc = ctx.NowUtc;

        var windowCutoff = nowUtc.AddDays(-settings.WindowDays);
        var cooldownCutoff = nowUtc.AddMinutes(-settings.CooldownMinutes);
        var activeRunFreshnessCutoff = nowUtc.AddHours(-settings.ActiveRunFreshnessHours);
        var infraFailureCutoff = nowUtc.AddHours(-settings.InfraFailureLookbackHours);

        var successfulCounts = new Dictionary<LearnerArchitecture, int>();
        var freshInFlightCounts = new Dictionary<LearnerArchitecture, int>();
        var nonInfraFailureCounts = new Dictionary<LearnerArchitecture, int>();
        var recentlyHandled = new HashSet<LearnerArchitecture>();
        var recentInfraFailures = new HashSet<LearnerArchitecture>();

        foreach (var run in contextRuns)
        {
            if (run.Status == RunStatus.Completed
                && run.CompletedAt is { } completedAt
                && completedAt >= windowCutoff)
            {
                successfulCounts[run.Architecture] = successfulCounts.GetValueOrDefault(run.Architecture) + 1;
            }

            if ((run.Status == RunStatus.Queued || run.Status == RunStatus.Running)
                && GetActiveTimestamp(run) >= activeRunFreshnessCutoff)
            {
                freshInFlightCounts[run.Architecture] = freshInFlightCounts.GetValueOrDefault(run.Architecture) + 1;
                recentlyHandled.Add(run.Architecture);
            }

            if (run.CompletedAt is { } handledAt && handledAt >= cooldownCutoff)
                recentlyHandled.Add(run.Architecture);

            if (run.Status == RunStatus.Failed
                && run.CompletedAt is { } failedAt
                && failedAt >= infraFailureCutoff
                && IsInfrastructureFailure(run.ErrorMessage, settings.InfraFailurePatterns))
            {
                recentInfraFailures.Add(run.Architecture);
            }

            if (run.Status == RunStatus.Failed
                && run.CompletedAt is { } nonInfraFailedAt
                && nonInfraFailedAt >= windowCutoff
                && !IsInfrastructureFailure(run.ErrorMessage, settings.InfraFailurePatterns))
            {
                nonInfraFailureCounts[run.Architecture] = nonInfraFailureCounts.GetValueOrDefault(run.Architecture) + 1;
            }
        }

        int queuedRuns = 0;
        int skippedArchitectures = 0;
        bool backpressureHit = false;
        bool anyEligibleQuotaUnsatisfied = false;
        bool anyEligibleQuotaUnsatisfiedNotSuppressed = false;

        foreach (var architecture in ctx.EligibleArchitectures)
        {
            int creditedRuns = successfulCounts.GetValueOrDefault(architecture)
                               + freshInFlightCounts.GetValueOrDefault(architecture);
            if (creditedRuns >= settings.MinRunsPerWindow)
            {
                skippedArchitectures++;
                LogDecision(context, architecture, "skipped", "quota_satisfied");
                _metrics?.MLArchitectureRotationArchitecturesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "quota_satisfied"));
                continue;
            }

            // From this point on the architecture is below quota for this context. Track
            // it so we can tell "fully blocked" (every eligible arch suppressed) from
            // "everything fine, just nothing to queue".
            anyEligibleQuotaUnsatisfied = true;

            if (recentInfraFailures.Contains(architecture))
            {
                skippedArchitectures++;
                LogDecision(context, architecture, "skipped", "recent_infra_failure");
                _metrics?.MLArchitectureRotationArchitecturesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "recent_infra_failure"));
                continue;
            }

            if (nonInfraFailureCounts.GetValueOrDefault(architecture) >= settings.MaxFailuresPerWindow)
            {
                skippedArchitectures++;
                LogDecision(context, architecture, "skipped", "failure_budget_exhausted");
                _metrics?.MLArchitectureRotationArchitecturesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "failure_budget_exhausted"));
                continue;
            }

            // Cooldown and queue-backpressure are transient (not "suppressed"); recording
            // these alongside the queue path means the context is still healthy, just
            // throttled.
            anyEligibleQuotaUnsatisfiedNotSuppressed = true;

            if (recentlyHandled.Contains(architecture))
            {
                skippedArchitectures++;
                LogDecision(context, architecture, "skipped", "cooldown");
                _metrics?.MLArchitectureRotationArchitecturesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "cooldown"));
                continue;
            }

            // Atomic per-cycle queue budget consumption. On negative result (budget
            // exhausted by parallel iterations) restore so other contexts still see
            // accurate capacity, mark backpressure, and skip-without-error.
            if (Interlocked.Decrement(ref ctx.RemainingPendingQueueBudget) < 0)
            {
                Interlocked.Increment(ref ctx.RemainingPendingQueueBudget);
                backpressureHit = true;
                skippedArchitectures++;
                LogDecision(context, architecture, "skipped", "queue_backpressure");
                _metrics?.MLArchitectureRotationArchitecturesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "queue_backpressure"));
                continue;
            }

            int priority = ComputePriority(architecture, successfulCounts);

            db.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol = context.Symbol,
                Timeframe = context.Timeframe,
                TriggerType = TriggerType.Scheduled,
                Status = RunStatus.Queued,
                FromDate = nowUtc.AddDays(-settings.TrainingDataWindowDays),
                ToDate = nowUtc,
                StartedAt = nowUtc,
                LearnerArchitecture = architecture,
                Priority = priority,
                HyperparamConfigJson = JsonSerializer.Serialize(new
                {
                    triggeredBy = WorkerName,
                    rotationWindowDays = settings.WindowDays,
                    minRunsPerWindow = settings.MinRunsPerWindow,
                    cooldownMinutes = settings.CooldownMinutes,
                    queuedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture),
                    priority,
                }),
            });

            queuedRuns++;
            recentlyHandled.Add(architecture);
            freshInFlightCounts[architecture] = freshInFlightCounts.GetValueOrDefault(architecture) + 1;
            LogDecision(context, architecture, "queued", $"priority={priority}");
            _metrics?.MLArchitectureRotationRunsQueued.Add(1);
        }

        // Per-iteration save: each context's queue inserts commit independently, so a
        // single context's persistence failure does not roll back the whole cycle.
        if (queuedRuns > 0)
        {
            try
            {
                await writeContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsLikelyUniqueViolation(ex))
            {
                // Another worker/replica raced this insert. Drop the duplicate, log,
                // and report no queued runs for this context — the budget restoration
                // happened atomically above on the per-architecture path so callers
                // already see accurate budget state.
                db.ChangeTracker.Clear();
                _logger.LogInformation(
                    "{Worker}: training-run insert race for {Symbol}/{Timeframe} resolved by uniqueness; skipping this context's queue this cycle.",
                    WorkerName,
                    context.Symbol,
                    context.Timeframe);
                queuedRuns = 0;
            }
        }

        // "All eligible architectures suppressed" = there is at least one arch below
        // quota AND every below-quota arch hit a hard suppression gate (infra-failure
        // or failure-budget). Cooldown and backpressure are transient — they don't
        // qualify. anyEligibleQuotaUnsatisfiedNotSuppressed flips on as soon as any
        // arch reaches the cooldown/queue-budget/queue path.
        bool allEligibleSuppressed = anyEligibleQuotaUnsatisfied
                                  && !anyEligibleQuotaUnsatisfiedNotSuppressed;

        return new ContextRotationOutcome(
            QueuedRuns: queuedRuns,
            SkippedArchitectures: skippedArchitectures,
            BackpressureHit: backpressureHit,
            AllEligibleSuppressed: allEligibleSuppressed);
    }

    private void LogDecision(ActiveContext context, LearnerArchitecture architecture, string decision, string reason)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
            return;

        _logger.LogDebug(
            "{Worker}: context={Symbol}/{Timeframe} arch={Architecture} decision={Decision} reason={Reason}",
            WorkerName,
            context.Symbol,
            context.Timeframe,
            architecture,
            decision,
            reason);
    }

    private static int ComputePriority(
        LearnerArchitecture architecture,
        IReadOnlyDictionary<LearnerArchitecture, int> successfulCounts)
        => successfulCounts.GetValueOrDefault(architecture) > 0
            ? BasePriority
            : StarvedPriority;

    private async Task<MLArchitectureRotationWorkerSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled,
            CK_PollSecs,
            CK_MinRuns,
            CK_WindowDays,
            CK_CooldownMinutes,
            CK_TrainWindowDays,
            CK_BlockedArchitectures,
            CK_LockTimeoutSeconds,
            CK_MaxContextsPerCycle,
            CK_ActiveRunFreshnessHours,
            CK_InfraFailureLookbackHours,
            CK_MaxPendingScheduledRuns,
            CK_MaxFailuresPerWindow,
            CK_InfraFailurePatterns,
            CK_MaxDegreeOfParallelism,
            CK_LongCycleWarnSeconds,
            CK_StaleContextAlertEnabled,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, config => config.Value, ct);

        return new MLArchitectureRotationWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, true),
            PollInterval: TimeSpan.FromSeconds(
                ClampInt(GetInt(values, CK_PollSecs, DefaultPollSeconds),
                    DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            MinRunsPerWindow: ClampInt(GetInt(values, CK_MinRuns, DefaultMinRunsPerWindow),
                DefaultMinRunsPerWindow, MinMinRunsPerWindow, MaxMinRunsPerWindow),
            WindowDays: ClampInt(GetInt(values, CK_WindowDays, DefaultWindowDays),
                DefaultWindowDays, MinWindowDays, MaxWindowDays),
            CooldownMinutes: ClampIntAllowingZero(GetInt(values, CK_CooldownMinutes, DefaultCooldownMinutes),
                DefaultCooldownMinutes, MinCooldownMinutes, MaxCooldownMinutes),
            TrainingDataWindowDays: ClampInt(GetInt(values, CK_TrainWindowDays, DefaultTrainingWindowDays),
                DefaultTrainingWindowDays, MinTrainingWindowDays, MaxTrainingWindowDays),
            LockTimeoutSeconds: ClampIntAllowingZero(GetInt(values, CK_LockTimeoutSeconds, DefaultLockTimeoutSeconds),
                DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            MaxContextsPerCycle: ClampInt(GetInt(values, CK_MaxContextsPerCycle, DefaultMaxContextsPerCycle),
                DefaultMaxContextsPerCycle, MinMaxContextsPerCycle, MaxMaxContextsPerCycle),
            ActiveRunFreshnessHours: ClampInt(GetInt(values, CK_ActiveRunFreshnessHours, DefaultActiveRunFreshnessHours),
                DefaultActiveRunFreshnessHours, MinActiveRunFreshnessHours, MaxActiveRunFreshnessHours),
            InfraFailureLookbackHours: ClampInt(GetInt(values, CK_InfraFailureLookbackHours, DefaultInfraFailureLookbackHours),
                DefaultInfraFailureLookbackHours, MinInfraFailureLookbackHours, MaxInfraFailureLookbackHours),
            MaxPendingScheduledRuns: ClampInt(GetInt(values, CK_MaxPendingScheduledRuns, DefaultMaxPendingScheduledRuns),
                DefaultMaxPendingScheduledRuns, MinMaxPendingScheduledRuns, MaxMaxPendingScheduledRuns),
            MaxFailuresPerWindow: ClampInt(GetInt(values, CK_MaxFailuresPerWindow, DefaultMaxFailuresPerWindow),
                DefaultMaxFailuresPerWindow, MinMaxFailuresPerWindow, MaxMaxFailuresPerWindow),
            BlockedArchitectures: ParseArchitectures(values.GetValueOrDefault(CK_BlockedArchitectures)),
            InfraFailurePatterns: ParseInfraFailurePatterns(values.GetValueOrDefault(CK_InfraFailurePatterns)),
            MaxDegreeOfParallelism: ClampInt(GetInt(values, CK_MaxDegreeOfParallelism, DefaultMaxDegreeOfParallelism),
                DefaultMaxDegreeOfParallelism, MinMaxDegreeOfParallelism, MaxMaxDegreeOfParallelism),
            LongCycleWarnSeconds: ClampIntAllowingZero(GetInt(values, CK_LongCycleWarnSeconds, DefaultLongCycleWarnSeconds),
                DefaultLongCycleWarnSeconds, MinLongCycleWarnSeconds, MaxLongCycleWarnSeconds),
            StaleContextAlertEnabled: GetBool(values, CK_StaleContextAlertEnabled, true));
    }

    private IReadOnlyList<LearnerArchitecture> ResolveEligibleArchitectures(
        IServiceProvider serviceProvider,
        IReadOnlySet<LearnerArchitecture> blockedArchitectures)
    {
        var eligible = new List<LearnerArchitecture>();

        foreach (var architecture in Enum.GetValues<LearnerArchitecture>())
        {
            if (blockedArchitectures.Contains(architecture))
            {
                _metrics?.MLArchitectureRotationArchitecturesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "blocked"));
                continue;
            }

            try
            {
                bool supported = architecture == LearnerArchitecture.BaggedLogistic
                    ? serviceProvider.GetService<IMLModelTrainer>() is not null
                    : serviceProvider.GetKeyedService<IMLModelTrainer>(architecture) is not null;

                if (!supported)
                {
                    _metrics?.MLArchitectureRotationArchitecturesSkipped.Add(
                        1,
                        new KeyValuePair<string, object?>("reason", "unsupported"));
                    continue;
                }

                eligible.Add(architecture);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed to resolve trainer for {Architecture}; excluding it from rotation this cycle.",
                    WorkerName,
                    architecture);
                _metrics?.MLArchitectureRotationArchitecturesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "resolution_failure"));
            }
        }

        return eligible;
    }

    private static HashSet<LearnerArchitecture> ParseArchitectures(string? raw)
    {
        var result = new HashSet<LearnerArchitecture>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<LearnerArchitecture>(token, ignoreCase: true, out var architecture))
                result.Add(architecture);
        }

        return result;
    }

    private static IReadOnlyList<string> ParseInfraFailurePatterns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultInfraFailurePatterns;

        var patterns = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 0)
            .Select(token => token.ToLowerInvariant())
            .Distinct()
            .ToList();

        return patterns.Count == 0 ? DefaultInfraFailurePatterns : patterns;
    }

    internal static bool IsInfrastructureFailure(string? errorMessage, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(errorMessage) || patterns.Count == 0)
            return false;

        foreach (var pattern in patterns)
        {
            if (errorMessage.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void ShuffleInPlace<T>(IList<T> list, int seed)
    {
        var rng = new Random(seed);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static DateTime GetActiveTimestamp(TrainingRunProjection run)
        => run.PickedUpAt ?? run.StartedAt;

    private async Task<bool> UpsertAndDispatchStaleContextAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveContext context,
        MLArchitectureRotationWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var dispatcher = serviceProvider.GetService<IAlertDispatcher>();
        if (dispatcher is null)
            return false;

        try
        {
            string deduplicationKey = StaleContextDeduplicationKey(context.Symbol, context.Timeframe);
            var alert = await db.Set<Alert>()
                .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                               && candidate.IsActive
                                               && candidate.DeduplicationKey == deduplicationKey, ct);

            int cooldownSeconds = await AlertCooldownDefaults.GetCooldownAsync(
                db,
                AlertCooldownDefaults.CK_MLMonitoring,
                AlertCooldownDefaults.Default_MLMonitoring,
                ct);

            string conditionJson = Truncate(JsonSerializer.Serialize(new
            {
                detector = "MLArchitectureRotation",
                reason = "all_eligible_architectures_suppressed",
                symbol = context.Symbol,
                timeframe = context.Timeframe.ToString(),
                rotationWindowDays = settings.WindowDays,
                infraFailureLookbackHours = settings.InfraFailureLookbackHours,
                maxFailuresPerWindow = settings.MaxFailuresPerWindow,
                detectedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture),
            }), AlertConditionMaxLength);

            DateTime? previousTriggeredAt = alert?.LastTriggeredAt;

            if (alert is null)
            {
                alert = new Alert
                {
                    AlertType = AlertType.MLMonitoringStale,
                    DeduplicationKey = deduplicationKey,
                    IsActive = true,
                };
                db.Set<Alert>().Add(alert);
            }
            else
            {
                alert.AlertType = AlertType.MLMonitoringStale;
            }

            alert.Symbol = context.Symbol;
            alert.Severity = AlertSeverity.High;
            alert.CooldownSeconds = cooldownSeconds;
            alert.AutoResolvedAt = null;
            alert.ConditionJson = conditionJson;

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
                alert.Symbol = context.Symbol;
                alert.Severity = AlertSeverity.High;
                alert.CooldownSeconds = cooldownSeconds;
                alert.AutoResolvedAt = null;
                alert.ConditionJson = conditionJson;
                await writeContext.SaveChangesAsync(ct);
            }

            // Cooldown the dispatch (the row is upserted regardless so dashboards see
            // the latest state).
            if (previousTriggeredAt.HasValue
                && nowUtc - NormalizeUtc(previousTriggeredAt.Value) < TimeSpan.FromSeconds(cooldownSeconds))
            {
                return false;
            }

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "MLArchitectureRotation: every eligible architecture is currently suppressed for context {0}/{1} (recent infra failures or per-architecture failure budget exhausted). Rotation cannot make progress until at least one architecture clears suppression — investigate the recent failures or extend MaxFailuresPerWindow.",
                context.Symbol,
                context.Timeframe);

            await dispatcher.DispatchAsync(alert, message, ct);
            await writeContext.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch stale-context alert for {Symbol}/{Timeframe}.",
                WorkerName,
                context.Symbol,
                context.Timeframe);
            return false;
        }
    }

    private async Task<bool> ResolveStaleContextAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveContext context,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string deduplicationKey = StaleContextDeduplicationKey(context.Symbol, context.Timeframe);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        if (alert is null)
            return false;

        var dispatcher = serviceProvider.GetService<IAlertDispatcher>();
        if (dispatcher is not null && alert.LastTriggeredAt.HasValue)
        {
            try
            {
                await dispatcher.TryAutoResolveAsync(alert, conditionStillActive: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "{Worker}: failed to auto-resolve stale-context alert {DeduplicationKey} for {Symbol}/{Timeframe}.",
                    WorkerName,
                    deduplicationKey,
                    context.Symbol,
                    context.Timeframe);
            }
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await writeContext.SaveChangesAsync(ct);
        return true;
    }

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

    private static bool IsLikelyUniqueViolation(DbUpdateException ex)
    {
        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }

    private static void DetachIfAdded(DbContext db, Alert alert)
    {
        var entry = db.Entry(alert);
        if (entry.State is EntityState.Added or EntityState.Modified)
            entry.State = EntityState.Detached;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static DateTime NormalizeUtc(DateTime timestamp)
        => timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);

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

    private static DateTime MinDate(DateTime a, DateTime b, DateTime c)
        => a <= b && a <= c ? a : b <= c ? b : c;
}

internal sealed record MLArchitectureRotationWorkerSettings(
    bool Enabled,
    TimeSpan PollInterval,
    int MinRunsPerWindow,
    int WindowDays,
    int CooldownMinutes,
    int TrainingDataWindowDays,
    int LockTimeoutSeconds,
    int MaxContextsPerCycle,
    int ActiveRunFreshnessHours,
    int InfraFailureLookbackHours,
    int MaxPendingScheduledRuns,
    int MaxFailuresPerWindow,
    HashSet<LearnerArchitecture> BlockedArchitectures,
    IReadOnlyList<string> InfraFailurePatterns,
    int MaxDegreeOfParallelism,
    int LongCycleWarnSeconds,
    bool StaleContextAlertEnabled);

internal sealed record MLArchitectureRotationCycleResult(
    MLArchitectureRotationWorkerSettings Settings,
    string? SkippedReason,
    int ContextCount,
    int ContextsProcessed,
    int EligibleArchitectureCount,
    int QueuedRunCount,
    int SkippedArchitectureCount,
    bool BackpressureHit,
    int FailedContextCount,
    int StaleContextAlertsDispatched,
    int StaleContextAlertsResolved)
{
    public static MLArchitectureRotationCycleResult Skipped(
        MLArchitectureRotationWorkerSettings settings,
        string reason)
        => new(
            settings,
            reason,
            ContextCount: 0,
            ContextsProcessed: 0,
            EligibleArchitectureCount: 0,
            QueuedRunCount: 0,
            SkippedArchitectureCount: 0,
            BackpressureHit: false,
            FailedContextCount: 0,
            StaleContextAlertsDispatched: 0,
            StaleContextAlertsResolved: 0);
}
