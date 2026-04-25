using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
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
public sealed class MLArchitectureRotationWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLArchitectureRotationWorker);

    private const string DistributedLockKey = "workers:ml-architecture-rotation:cycle";

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
        bool BackpressureHit);

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
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.MLArchitectureRotationCycleDurationMs.Record(durationMs);
                    _metrics?.MLArchitectureRotationQueuedRunsPerCycle.Record(result.QueuedRunCount);

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
                            "{Worker}: contexts={Contexts}, processed={Processed}, eligibleArchitectures={EligibleArchitectures}, queuedRuns={Queued}, skippedArchitectures={Skipped}, backpressureHit={Backpressure}.",
                            WorkerName,
                            result.ContextCount,
                            result.ContextsProcessed,
                            result.EligibleArchitectureCount,
                            result.QueuedRunCount,
                            result.SkippedArchitectureCount,
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

        var runsByContext = await BatchLoadRunsAsync(db, contexts, settings, nowUtc, ct);

        int queuedRuns = 0;
        int skippedArchitectures = 0;
        bool backpressureHit = false;

        foreach (var context in contexts)
        {
            _metrics?.MLArchitectureRotationContextsEvaluated.Add(1);

            int remainingQueueBudget = Math.Max(0, settings.MaxPendingScheduledRuns - currentPendingScheduled);
            if (remainingQueueBudget == 0)
            {
                backpressureHit = true;
                _logger.LogDebug(
                    "{Worker}: queue backpressure hit (pending={Pending} >= max={Max}); deferring remaining contexts.",
                    WorkerName,
                    currentPendingScheduled,
                    settings.MaxPendingScheduledRuns);
                _metrics?.MLArchitectureRotationCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "queue_backpressure"));
                break;
            }

            runsByContext.TryGetValue((context.Symbol, context.Timeframe), out var contextRuns);
            var outcome = ProcessContext(
                db,
                context,
                contextRuns ?? [],
                eligibleArchitectures,
                settings,
                nowUtc,
                remainingQueueBudget);

            queuedRuns += outcome.QueuedRuns;
            skippedArchitectures += outcome.SkippedArchitectures;
            currentPendingScheduled += outcome.QueuedRuns;
            backpressureHit |= outcome.BackpressureHit;
        }

        if (queuedRuns > 0)
            await writeContext.SaveChangesAsync(ct);

        return new MLArchitectureRotationCycleResult(
            settings,
            SkippedReason: null,
            ContextCount: contexts.Count,
            ContextsProcessed: contexts.Count,
            EligibleArchitectureCount: eligibleArchitectures.Count,
            QueuedRunCount: queuedRuns,
            SkippedArchitectureCount: skippedArchitectures,
            BackpressureHit: backpressureHit);
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

    private ContextRotationOutcome ProcessContext(
        DbContext db,
        ActiveContext context,
        IReadOnlyList<TrainingRunProjection> contextRuns,
        IReadOnlyList<LearnerArchitecture> eligibleArchitectures,
        MLArchitectureRotationWorkerSettings settings,
        DateTime nowUtc,
        int remainingQueueBudget)
    {
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
        int budgetRemaining = remainingQueueBudget;

        foreach (var architecture in eligibleArchitectures)
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

            if (recentlyHandled.Contains(architecture))
            {
                skippedArchitectures++;
                LogDecision(context, architecture, "skipped", "cooldown");
                _metrics?.MLArchitectureRotationArchitecturesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "cooldown"));
                continue;
            }

            if (budgetRemaining <= 0)
            {
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
            budgetRemaining--;
            recentlyHandled.Add(architecture);
            freshInFlightCounts[architecture] = freshInFlightCounts.GetValueOrDefault(architecture) + 1;
            LogDecision(context, architecture, "queued", $"priority={priority}");
            _metrics?.MLArchitectureRotationRunsQueued.Add(1);
        }

        return new ContextRotationOutcome(queuedRuns, skippedArchitectures, backpressureHit);
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
            InfraFailurePatterns: ParseInfraFailurePatterns(values.GetValueOrDefault(CK_InfraFailurePatterns)));
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
    IReadOnlyList<string> InfraFailurePatterns);

internal sealed record MLArchitectureRotationCycleResult(
    MLArchitectureRotationWorkerSettings Settings,
    string? SkippedReason,
    int ContextCount,
    int ContextsProcessed,
    int EligibleArchitectureCount,
    int QueuedRunCount,
    int SkippedArchitectureCount,
    bool BackpressureHit)
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
            BackpressureHit: false);
}
