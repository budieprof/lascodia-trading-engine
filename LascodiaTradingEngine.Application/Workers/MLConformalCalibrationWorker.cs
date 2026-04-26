using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes live split-conformal calibration records for active ML models whose serving
/// snapshots do not yet have a usable persisted calibration row.
/// </summary>
/// <remarks>
/// The worker runs on the authoritative write side so calibration existence, prediction
/// logs, and snapshot writes are observed consistently. It calibrates from resolved logs
/// produced after model activation, writes the same threshold to the global and per-class
/// snapshot fields consumed by the scorer, and keeps the persisted calibration row aligned
/// with that snapshot threshold.
/// </remarks>
public sealed partial class MLConformalCalibrationWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLConformalCalibrationWorker);

    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";
    private const string DistributedLockKey = "workers:ml-conformal-calibration:cycle";
    private const string StaleCalibrationAlertDeduplicationPrefix = "ml-conformal-calibration-stale:";
    private const string SkipStreakKeyPrefix = "MLConformal:Model:";
    private const string SkipStreakKeySuffix = ":ConsecutiveSkips";
    private const int AlertConditionMaxLength = 1000;
    private const int MaxMaxDegreeOfParallelism = 16;
    private const double ProbabilityEpsilon = 1e-9;

    // Knob names that are overridable per (Model:{id} | Symbol:Timeframe | Symbol:* |
    // *:Timeframe | *:*) tier. The override-token validator flags any override key whose
    // final segment isn't in this set so operators see typos like "MnLogs" instead of
    // having the row silently fall through to the global default.
    private static readonly string[] ValidOverrideKnobs =
    [
        "MinLogs",
        "MaxLogs",
        "MaxLogAgeDays",
        "MaxCalibrationAgeDays",
        "TargetCoverage",
        "RequirePostActivationLogs",
    ];

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MLConformalCalibrationWorker> _logger;
    private readonly MLConformalCalibrationOptions _options;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    // Hashed signature of the unmatched-tokens set last reported by the override-key
    // validator. Same dedup primitive as the calibration / edge / rotation workers.
    private long _lastUnmatchedTokensSignature;

    internal sealed record MLConformalCalibrationWorkerSettings(
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int PollJitterSeconds,
        int MaxLogs,
        int MinLogs,
        int MaxLogAgeDays,
        int MaxCalibrationAgeDays,
        double TargetCoverage,
        int ModelBatchSize,
        int MaxCycleModels,
        int LockTimeoutSeconds,
        bool RequirePostActivationLogs,
        int MaxDegreeOfParallelism,
        int LongCycleWarnSeconds,
        bool StaleAlertEnabled,
        int StaleSkipAlertThreshold);

    internal readonly record struct MLConformalCalibrationCycleResult(
        MLConformalCalibrationWorkerSettings Settings,
        string? SkippedReason,
        int CandidateModelCount,
        int EvaluatedModelCount,
        int CalibrationsWritten,
        int SkippedAlreadyCalibratedCount,
        int SkippedInvalidSnapshotCount,
        int SkippedInsufficientLogsCount,
        int SkippedPersistenceRaceCount,
        int FailedModelCount,
        int StaleAlertsDispatched,
        int StaleAlertsResolved)
    {
        public static MLConformalCalibrationCycleResult Skipped(
            MLConformalCalibrationWorkerSettings settings,
            string reason)
            => new(settings, reason, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Mutable per-cycle state shared by every iteration of the parallel model loop.
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
        public required MLConformalCalibrationWorkerSettings Settings;
        public DateTime NowUtc;
        public required IReadOnlyDictionary<long, MLConformalCalibration[]> ExistingByModelId;
        public required IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>> OverridesByContext;
        public required IReadOnlyDictionary<long, int> SkipStreaksByModelId;
        public ConcurrentDictionary<long, byte> ActiveStaleAlertModelIds = new();

        // Counters mutated atomically by Interlocked through `ref ctx.Field`.
        public int Evaluated;
        public int Written;
        public int SkippedAlreadyCalibrated;
        public int SkippedInvalidSnapshot;
        public int SkippedInsufficientLogs;
        public int SkippedPersistenceRace;
        public int FailedModels;
        public int RemainingModels;
        public int StaleAlertsDispatched;
        public int StaleAlertsResolved;
    }

    private readonly record struct ActiveModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        DateTime TrainedAt,
        DateTime? ActivatedAt,
        byte[]? ModelBytes);

    private readonly record struct CalibrationObservation(
        double Score,
        double BuyProbability,
        TradeDirection ActualDirection,
        DateTime OutcomeRecordedAt);

    private readonly record struct CalibrationComputation(
        IReadOnlyList<double> SortedScores,
        int SampleCount,
        double Threshold,
        double TargetCoverage,
        double EmpiricalCoverage,
        double AmbiguousRate);

    public MLConformalCalibrationWorker(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<MLConformalCalibrationWorker> logger,
        MLConformalCalibrationOptions? options = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
        _options = options ?? new MLConformalCalibrationOptions();
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);

        var initialSettings = BuildSettings(_options);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Builds persisted conformal calibration records and aligns serving snapshots for active ML models.",
            initialSettings.PollInterval);

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName) + initialSettings.InitialDelay;
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                    var result = await RunCycleAsync(stoppingToken);

                    long durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.CandidateModelCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("parallelism", result.Settings.MaxDegreeOfParallelism));

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
                    _metrics?.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                var currentSettings = BuildSettings(_options);
                await Task.Delay(
                    CalculateDelay(GetIntervalWithJitter(currentSettings), _consecutiveFailures),
                    _timeProvider,
                    stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("{Worker} stopping.", WorkerName);
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
        }
    }

    internal Task<MLConformalCalibrationCycleResult> RunAsync(CancellationToken ct)
        => RunCycleAsync(ct);

    internal async Task<MLConformalCalibrationCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var settings = BuildSettings(_options);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var writeDb = writeContext.GetDbContext();

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLConformalCalibrationLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate calibration cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
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
                _metrics?.MLConformalCalibrationLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.MLConformalCalibrationCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return MLConformalCalibrationCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLConformalCalibrationLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunCycleCoreAsync(writeDb, settings, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : baseInterval;

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<MLConformalCalibrationCycleResult> RunCycleCoreAsync(
        DbContext writeDb,
        MLConformalCalibrationWorkerSettings settings,
        CancellationToken ct)
    {
        var cycleStart = Stopwatch.GetTimestamp();
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var candidates = await writeDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer
                        && m.ModelBytes != null)
            .OrderBy(m => m.Id)
            .Take(settings.MaxCycleModels)
            .Select(m => new ActiveModelCandidate(
                m.Id,
                m.Symbol,
                m.Timeframe,
                m.TrainedAt,
                m.ActivatedAt,
                m.ModelBytes))
            .ToListAsync(ct);

        _healthMonitor?.RecordBacklogDepth(WorkerName, candidates.Count);

        if (candidates.Count == 0)
        {
            _metrics?.MLConformalCalibrationCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_candidate_models"));
            return MLConformalCalibrationCycleResult.Skipped(settings, "no_candidate_models");
        }

        // Batch-load existing calibrations for skip-check (read-only on the cycle scope).
        var allModelIds = candidates.Select(m => m.Id).ToArray();
        var existingByModelId = new Dictionary<long, MLConformalCalibration[]>();
        foreach (var batch in candidates.Chunk(settings.ModelBatchSize))
        {
            var batchIds = batch.Select(m => m.Id).ToArray();
            var rows = await writeDb.Set<MLConformalCalibration>()
                .AsNoTracking()
                .Where(c => batchIds.Contains(c.MLModelId) && !c.IsDeleted)
                .OrderByDescending(c => c.CalibratedAt)
                .ThenByDescending(c => c.Id)
                .ToListAsync(ct);
            foreach (var group in rows.GroupBy(c => c.MLModelId))
                existingByModelId[group.Key] = group.ToArray();
        }

        // Single broad-prefix scan over override rows; bucket per (Symbol, Timeframe)
        // in-memory and run the override-token validator over the same list.
        var allOverrideRows = await writeDb.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("MLConformalCalibration:Override:"))
            .Select(c => new KeyValuePair<string, string>(c.Key, c.Value))
            .ToListAsync(ct);
        ValidateOverrideTokens(allOverrideRows);
        var overridesByContext = BucketOverridesByContext(candidates, allOverrideRows);

        // Batch-load per-model skip-streak counters and active stale-alert state so the
        // parallel iterations don't hit EngineConfig / Alert tables N×.
        var skipStreaksByModelId = await BatchLoadSkipStreaksAsync(writeDb, allModelIds, ct);
        var activeStaleAlertModelIds = await BatchLoadActiveStaleAlertModelIdsAsync(writeDb, allModelIds, ct);

        var ctx = new CycleIteration
        {
            Settings = settings,
            NowUtc = nowUtc,
            ExistingByModelId = existingByModelId,
            OverridesByContext = overridesByContext,
            SkipStreaksByModelId = skipStreaksByModelId,
            RemainingModels = candidates.Count,
        };
        foreach (var id in activeStaleAlertModelIds) ctx.ActiveStaleAlertModelIds.TryAdd(id, 0);

        int parallelism = Math.Clamp(settings.MaxDegreeOfParallelism, 1, MaxMaxDegreeOfParallelism);

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = ct,
            },
            async (model, modelCt) => await EvaluateOneModelIterationAsync(ctx, model, modelCt))
            .ConfigureAwait(false);

        double durationMs = Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
        _metrics?.MLConformalCalibrationCycleDurationMs.Record(durationMs);

        _logger.LogInformation(
            "{Worker}: cycle complete. candidates={Candidates} evaluated={Evaluated} written={Written} skippedAlready={SkippedAlready} skippedInvalidSnapshot={SkippedInvalidSnapshot} skippedInsufficient={SkippedInsufficient} skippedRace={SkippedRace} failed={Failed} staleAlertsDispatched={StaleDispatched} staleAlertsResolved={StaleResolved}",
            WorkerName,
            candidates.Count,
            ctx.Evaluated,
            ctx.Written,
            ctx.SkippedAlreadyCalibrated,
            ctx.SkippedInvalidSnapshot,
            ctx.SkippedInsufficientLogs,
            ctx.SkippedPersistenceRace,
            ctx.FailedModels,
            ctx.StaleAlertsDispatched,
            ctx.StaleAlertsResolved);

        return new MLConformalCalibrationCycleResult(
            settings,
            SkippedReason: null,
            CandidateModelCount: candidates.Count,
            EvaluatedModelCount: ctx.Evaluated,
            CalibrationsWritten: ctx.Written,
            SkippedAlreadyCalibratedCount: ctx.SkippedAlreadyCalibrated,
            SkippedInvalidSnapshotCount: ctx.SkippedInvalidSnapshot,
            SkippedInsufficientLogsCount: ctx.SkippedInsufficientLogs,
            SkippedPersistenceRaceCount: ctx.SkippedPersistenceRace,
            FailedModelCount: ctx.FailedModels,
            StaleAlertsDispatched: ctx.StaleAlertsDispatched,
            StaleAlertsResolved: ctx.StaleAlertsResolved);
    }

    /// <summary>
    /// Per-iteration entry point invoked by the parallel model loop. Owns one DI scope
    /// per iteration so the per-model log query + transactional persist never cross an
    /// EF state boundary, and re-throws cancellation cleanly so shutdown doesn't
    /// masquerade as model failure.
    /// </summary>
    private async ValueTask EvaluateOneModelIterationAsync(
        CycleIteration ctx, ActiveModelCandidate model, CancellationToken modelCt)
    {
        await using var modelScope = _scopeFactory.CreateAsyncScope();
        var modelWriteCtx = modelScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var modelDb = modelWriteCtx.GetDbContext();

        try
        {
            // Refresh the worker heartbeat before each model evaluation. Long cycles
            // (large fleet / DOP=1) would otherwise leave the health monitor without
            // a signal until cycle end.
            _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

            await EvaluateModelAsync(modelScope.ServiceProvider, modelDb, ctx, model, modelCt);
        }
        catch (OperationCanceledException) when (modelCt.IsCancellationRequested)
        {
            // Shutdown propagation, not a model failure. Re-throw so Parallel.ForEachAsync
            // surfaces it and the ExecuteAsync loop honours stoppingToken.
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref ctx.FailedModels);
            _metrics?.MLConformalCalibrationModelsSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "model_error"),
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            _logger.LogWarning(
                ex,
                "{Worker}: failed to evaluate calibration for model {ModelId} ({Symbol}/{Timeframe}).",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe);
        }
        finally
        {
            int remaining = Interlocked.Decrement(ref ctx.RemainingModels);
            _healthMonitor?.RecordBacklogDepth(WorkerName, remaining);
        }
    }

    private async Task EvaluateModelAsync(
        IServiceProvider serviceProvider,
        DbContext modelDb,
        CycleIteration ctx,
        ActiveModelCandidate model,
        CancellationToken ct)
    {
        // Apply per-context overrides (6-tier: Model:{id} → Symbol:Timeframe → Symbol:* →
        // *:Timeframe → *:* → defaults). Each model evaluates against its own effective
        // settings; cycle-wide settings flow through unchanged when no overrides match.
        var overrides = ctx.OverridesByContext.TryGetValue((model.Symbol, model.Timeframe), out var ctxOverrides)
            ? ctxOverrides
            : new Dictionary<string, string>();
        var settings = ApplyPerContextOverrides(ctx.Settings, overrides, model.Symbol, model.Timeframe, model.Id);
        var nowUtc = ctx.NowUtc;

        ctx.ExistingByModelId.TryGetValue(model.Id, out var modelCalibrations);
        if (modelCalibrations is not null
            && modelCalibrations.Any(c => IsUsableCalibration(c, model, settings, nowUtc)))
        {
            Interlocked.Increment(ref ctx.SkippedAlreadyCalibrated);
            RecordSkip("already_calibrated", model);
            await TryAutoResolveStaleAlertAsync(serviceProvider, modelDb, ctx, model, ct);
            await ResetSkipStreakAsync(modelDb, model, ct);
            return;
        }

        if (!TryDeserializeSnapshot(model.ModelBytes, out var snapshot) || !HasModelWeights(snapshot))
        {
            Interlocked.Increment(ref ctx.SkippedInvalidSnapshot);
            RecordSkip("invalid_snapshot", model);
            await IncrementSkipStreakAndMaybeAlertAsync(serviceProvider, modelDb, ctx, model, "invalid_snapshot", ct);
            return;
        }

        double decisionThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snapshot);
        DateTime evidenceCutoff = GetEvidenceCutoff(model, settings, nowUtc);
        var modelLogs = await LoadRecentResolvedLogsAsync(modelDb, model, settings, nowUtc, ct);
        var observations = BuildObservations(modelLogs, model, evidenceCutoff, decisionThreshold);

        if (observations.Count < settings.MinLogs)
        {
            Interlocked.Increment(ref ctx.SkippedInsufficientLogs);
            RecordSkip("insufficient_logs", model);
            await IncrementSkipStreakAndMaybeAlertAsync(serviceProvider, modelDb, ctx, model, "insufficient_logs", ct);
            return;
        }

        Interlocked.Increment(ref ctx.Evaluated);
        _metrics?.MLConformalCalibrationModelsEvaluated.Add(
            1,
            new("symbol", model.Symbol),
            new("timeframe", model.Timeframe.ToString()));

        var calibration = ComputeCalibration(observations, settings.TargetCoverage);
        bool persisted = await PersistCalibrationAsync(
            modelDb,
            model,
            calibration,
            settings,
            ct);

        if (!persisted)
        {
            Interlocked.Increment(ref ctx.SkippedPersistenceRace);
            RecordSkip("already_calibrated_after_recheck", model);
            return;
        }

        Interlocked.Increment(ref ctx.Written);
        _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");

        _metrics?.MLConformalCalibrationWritten.Add(
            1,
            new("symbol", model.Symbol),
            new("timeframe", model.Timeframe.ToString()));
        _metrics?.MLConformalCalibrationSamples.Record(
            calibration.SampleCount,
            new("symbol", model.Symbol),
            new("timeframe", model.Timeframe.ToString()));
        _metrics?.MLConformalCalibrationEmpiricalCoverage.Record(
            calibration.EmpiricalCoverage,
            new("symbol", model.Symbol),
            new("timeframe", model.Timeframe.ToString()));
        _metrics?.MLConformalCalibrationAmbiguousRate.Record(
            calibration.AmbiguousRate,
            new("symbol", model.Symbol),
            new("timeframe", model.Timeframe.ToString()));

        _logger.LogInformation(
            "{Worker}: calibrated model {ModelId} {Symbol}/{Timeframe} qHat={Threshold:F4} coverage={Coverage:P1} ambiguous={Ambiguous:P1} samples={Samples}.",
            WorkerName,
            model.Id,
            model.Symbol,
            model.Timeframe,
            calibration.Threshold,
            calibration.EmpiricalCoverage,
            calibration.AmbiguousRate,
            calibration.SampleCount);

        // Successful calibration: reset the skip streak and auto-resolve any active
        // stale-calibration alert for this model.
        await ResetSkipStreakAsync(modelDb, model, ct);
        await TryAutoResolveStaleAlertAsync(serviceProvider, modelDb, ctx, model, ct);
    }

    private async Task<bool> PersistCalibrationAsync(
        DbContext writeDb,
        ActiveModelCandidate model,
        CalibrationComputation calibration,
        MLConformalCalibrationWorkerSettings settings,
        CancellationToken ct)
    {
        bool persisted = false;
        var strategy = writeDb.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async token =>
        {
            await using var transaction = await writeDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

            var latestCalibration = await writeDb.Set<MLConformalCalibration>()
                .Where(c => c.MLModelId == model.Id && !c.IsDeleted)
                .OrderByDescending(c => c.CalibratedAt)
                .ThenByDescending(c => c.Id)
                .FirstOrDefaultAsync(token);

            DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            if (latestCalibration is not null && IsUsableCalibration(latestCalibration, model, settings, nowUtc))
            {
                await transaction.CommitAsync(token);
                persisted = false;
                return;
            }

            var (writeModel, latestSnapshot) = await MLModelSnapshotWriteHelper
                .LoadTrackedLatestSnapshotAsync(writeDb, model.Id, token);
            if (writeModel is null || latestSnapshot is null || !HasModelWeights(latestSnapshot))
            {
                await transaction.CommitAsync(token);
                persisted = false;
                return;
            }

            latestSnapshot.ConformalQHat = calibration.Threshold;
            latestSnapshot.ConformalQHatBuy = calibration.Threshold;
            latestSnapshot.ConformalQHatSell = calibration.Threshold;
            latestSnapshot.ConformalCoverage = calibration.TargetCoverage;
            writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(latestSnapshot);

            writeDb.Set<MLConformalCalibration>().Add(new MLConformalCalibration
            {
                MLModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                NonConformityScoresJson = JsonSerializer.Serialize(calibration.SortedScores),
                CalibrationSamples = calibration.SampleCount,
                TargetCoverage = calibration.TargetCoverage,
                CoverageThreshold = calibration.Threshold,
                EmpiricalCoverage = calibration.EmpiricalCoverage,
                AmbiguousRate = calibration.AmbiguousRate,
                CalibratedAt = nowUtc
            });

            await writeDb.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            writeDb.ChangeTracker.Clear();
            persisted = true;
        }, ct);

        return persisted;
    }

    private static async Task<List<MLModelPredictionLog>> LoadRecentResolvedLogsAsync(
        DbContext db,
        ActiveModelCandidate model,
        MLConformalCalibrationWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        DateTime oldestAllowed = nowUtc.AddDays(-settings.MaxLogAgeDays);
        return await db.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId == model.Id
                        && !l.IsDeleted
                        && l.Symbol == model.Symbol
                        && l.Timeframe == model.Timeframe
                        && l.ActualDirection.HasValue
                        && l.OutcomeRecordedAt.HasValue
                        && l.OutcomeRecordedAt >= oldestAllowed
                        && (l.ServedCalibratedProbability.HasValue
                            || l.CalibratedProbability.HasValue
                            || l.RawProbability.HasValue
                            || l.ConfidenceScore > 0m))
            .OrderByDescending(l => l.OutcomeRecordedAt)
            .ThenByDescending(l => l.Id)
            .Take(settings.MaxLogs)
            .ToListAsync(ct);
    }

    private static List<CalibrationObservation> BuildObservations(
        IReadOnlyCollection<MLModelPredictionLog> logs,
        ActiveModelCandidate model,
        DateTime evidenceCutoff,
        double decisionThreshold)
    {
        var observations = new List<CalibrationObservation>(logs.Count);
        foreach (var log in logs)
        {
            if (!string.Equals(log.Symbol?.Trim(), model.Symbol?.Trim(), StringComparison.OrdinalIgnoreCase)
                || log.Timeframe != model.Timeframe
                || !log.ActualDirection.HasValue
                || !log.OutcomeRecordedAt.HasValue)
            {
                continue;
            }

            DateTime outcomeAt = NormalizeUtc(log.OutcomeRecordedAt.Value);
            if (outcomeAt < evidenceCutoff)
                continue;

            double buyProbability = MLFeatureHelper.ResolveLoggedServedBuyProbability(log, decisionThreshold);
            if (!IsFiniteProbability(buyProbability))
                continue;

            double trueProbability = log.ActualDirection.Value == TradeDirection.Buy
                ? buyProbability
                : 1.0 - buyProbability;
            double score = 1.0 - trueProbability;
            if (!IsFiniteProbability(score))
                continue;

            observations.Add(new CalibrationObservation(
                score,
                buyProbability,
                log.ActualDirection.Value,
                outcomeAt));
        }

        return observations;
    }

    private static CalibrationComputation ComputeCalibration(
        IReadOnlyCollection<CalibrationObservation> observations,
        double targetCoverage)
    {
        var scores = observations
            .Select(o => o.Score)
            .OrderBy(s => s)
            .ToArray();

        double threshold = ComputeConformalQuantile(scores, targetCoverage);
        int covered = observations.Count(o => o.Score <= threshold + ProbabilityEpsilon);
        int ambiguous = observations.Count(o =>
            (1.0 - o.BuyProbability) <= threshold + ProbabilityEpsilon
            && o.BuyProbability <= threshold + ProbabilityEpsilon);

        return new CalibrationComputation(
            scores,
            scores.Length,
            threshold,
            targetCoverage,
            covered / (double)scores.Length,
            ambiguous / (double)scores.Length);
    }

    internal static double ComputeConformalQuantile(
        IReadOnlyList<double> sortedScores,
        double targetCoverage)
    {
        if (sortedScores.Count == 0)
            return 0.5;

        int index = (int)Math.Ceiling(targetCoverage * (sortedScores.Count + 1)) - 1;
        index = Math.Clamp(index, 0, sortedScores.Count - 1);
        return Math.Clamp(sortedScores[index], 0.0, 1.0);
    }

    private static bool IsUsableCalibration(
        MLConformalCalibration calibration,
        ActiveModelCandidate model,
        MLConformalCalibrationWorkerSettings settings,
        DateTime nowUtc)
    {
        if (calibration.IsDeleted
            || calibration.MLModelId != model.Id
            || calibration.CalibrationSamples < settings.MinLogs
            || !IsStrictProbability(calibration.TargetCoverage)
            || !IsFiniteProbability(calibration.CoverageThreshold)
            || Math.Abs(calibration.TargetCoverage - settings.TargetCoverage) > 0.000001
            || !string.Equals(calibration.Symbol?.Trim(), model.Symbol?.Trim(), StringComparison.OrdinalIgnoreCase)
            || calibration.Timeframe != model.Timeframe)
        {
            return false;
        }

        if (calibration.CalibratedAt < nowUtc.AddDays(-settings.MaxCalibrationAgeDays))
            return false;

        if (settings.RequirePostActivationLogs && calibration.CalibratedAt < GetEvidenceCutoff(model, settings, calibration.CalibratedAt))
            return false;

        return true;
    }

    private void RecordSkip(string reason, ActiveModelCandidate model)
    {
        _metrics?.MLConformalCalibrationModelsSkipped.Add(
            1,
            new("reason", reason),
            new("symbol", model.Symbol),
            new("timeframe", model.Timeframe.ToString()));
    }

    private static string SkipStreakKey(long modelId)
        => $"{SkipStreakKeyPrefix}{modelId.ToString(CultureInfo.InvariantCulture)}{SkipStreakKeySuffix}";

    private static bool TryParseModelIdFromSkipKey(string key, out long modelId)
    {
        modelId = 0;
        if (!key.StartsWith(SkipStreakKeyPrefix, StringComparison.Ordinal)
            || !key.EndsWith(SkipStreakKeySuffix, StringComparison.Ordinal))
            return false;

        int idStart = SkipStreakKeyPrefix.Length;
        int idEnd = key.Length - SkipStreakKeySuffix.Length;
        return long.TryParse(
            key.AsSpan(idStart, idEnd - idStart),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out modelId);
    }

    private static string StaleAlertDeduplicationKey(long modelId)
        => StaleCalibrationAlertDeduplicationPrefix + modelId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Batch-load per-model consecutive-skip counters from <see cref="EngineConfig"/>
    /// at cycle start so the parallel iteration doesn't query EngineConfig N×.
    /// </summary>
    private static async Task<Dictionary<long, int>> BatchLoadSkipStreaksAsync(
        DbContext db,
        IReadOnlyList<long> modelIds,
        CancellationToken ct)
    {
        var keys = modelIds.Select(SkipStreakKey).ToList();
        var rows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(config => keys.Contains(config.Key))
            .Select(config => new { config.Key, config.Value })
            .ToListAsync(ct);

        var streaks = new Dictionary<long, int>();
        foreach (var row in rows)
        {
            if (TryParseModelIdFromSkipKey(row.Key, out var modelId)
                && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var streak))
            {
                streaks[modelId] = streak;
            }
        }
        return streaks;
    }

    /// <summary>
    /// Batch-load currently-active stale-calibration alert dedup keys at cycle start so
    /// parallel iterations can decide whether to dispatch / auto-resolve without an
    /// extra round-trip per model.
    /// </summary>
    private static async Task<HashSet<long>> BatchLoadActiveStaleAlertModelIdsAsync(
        DbContext db,
        IReadOnlyList<long> modelIds,
        CancellationToken ct)
    {
        if (modelIds.Count == 0)
            return [];

        var dedupKeys = modelIds
            .Select(StaleAlertDeduplicationKey)
            .ToList();

        var rows = await db.Set<Alert>()
            .AsNoTracking()
            .Where(alert => !alert.IsDeleted
                         && alert.IsActive
                         && alert.DeduplicationKey != null
                         && dedupKeys.Contains(alert.DeduplicationKey))
            .Select(alert => alert.DeduplicationKey!)
            .ToListAsync(ct);

        var result = new HashSet<long>();
        foreach (var key in rows)
        {
            if (key.Length <= StaleCalibrationAlertDeduplicationPrefix.Length)
                continue;
            var span = key.AsSpan(StaleCalibrationAlertDeduplicationPrefix.Length);
            if (long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// Increment the per-model skip streak counter and dispatch a stale-calibration
    /// alert when the streak reaches the configured threshold. The streak is durable
    /// (persisted in <see cref="EngineConfig"/>) so it survives worker restarts.
    /// </summary>
    private async Task IncrementSkipStreakAndMaybeAlertAsync(
        IServiceProvider serviceProvider,
        DbContext db,
        CycleIteration ctx,
        ActiveModelCandidate model,
        string skipReason,
        CancellationToken ct)
    {
        if (!ctx.Settings.StaleAlertEnabled)
            return;

        int previous = ctx.SkipStreaksByModelId.TryGetValue(model.Id, out var existing) ? existing : 0;
        int next = previous + 1;

        await EngineConfigUpsert.UpsertAsync(
            db,
            SkipStreakKey(model.Id),
            next.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Consecutive cycles in which MLConformalCalibrationWorker could not calibrate this model.",
            isHotReloadable: false,
            ct);

        if (previous < ctx.Settings.StaleSkipAlertThreshold && next >= ctx.Settings.StaleSkipAlertThreshold)
        {
            bool dispatched = await UpsertAndDispatchStaleCalibrationAlertAsync(
                serviceProvider, db, model, ctx.Settings, skipReason, next, ctx.NowUtc, ct);
            if (dispatched)
            {
                ctx.ActiveStaleAlertModelIds.TryAdd(model.Id, 0);
                Interlocked.Increment(ref ctx.StaleAlertsDispatched);
            }
        }
    }

    /// <summary>
    /// Reset the per-model skip streak counter when calibration succeeds (or an
    /// existing usable calibration is observed).
    /// </summary>
    private async Task ResetSkipStreakAsync(
        DbContext db,
        ActiveModelCandidate model,
        CancellationToken ct)
    {
        await EngineConfigUpsert.UpsertAsync(
            db,
            SkipStreakKey(model.Id),
            "0",
            ConfigDataType.Int,
            "Consecutive cycles in which MLConformalCalibrationWorker could not calibrate this model.",
            isHotReloadable: false,
            ct);
    }

    private async Task TryAutoResolveStaleAlertAsync(
        IServiceProvider serviceProvider,
        DbContext db,
        CycleIteration ctx,
        ActiveModelCandidate model,
        CancellationToken ct)
    {
        if (!ctx.ActiveStaleAlertModelIds.ContainsKey(model.Id))
            return;

        bool resolved = await ResolveStaleCalibrationAlertAsync(serviceProvider, db, model, ctx.NowUtc, ct);
        if (resolved)
        {
            ctx.ActiveStaleAlertModelIds.TryRemove(model.Id, out _);
            Interlocked.Increment(ref ctx.StaleAlertsResolved);
        }
    }

    private async Task<bool> UpsertAndDispatchStaleCalibrationAlertAsync(
        IServiceProvider serviceProvider,
        DbContext db,
        ActiveModelCandidate model,
        MLConformalCalibrationWorkerSettings settings,
        string skipReason,
        int consecutiveSkips,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var dispatcher = serviceProvider.GetService<IAlertDispatcher>();
        if (dispatcher is null)
            return false;

        try
        {
            string deduplicationKey = StaleAlertDeduplicationKey(model.Id);
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
                detector = "MLConformalCalibration",
                reason = "stale_calibration",
                modelId = model.Id,
                symbol = model.Symbol,
                timeframe = model.Timeframe.ToString(),
                consecutiveSkips,
                lastSkipReason = skipReason,
                staleSkipAlertThreshold = settings.StaleSkipAlertThreshold,
                evaluatedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture),
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

            alert.Symbol = model.Symbol;
            alert.Severity = AlertSeverity.High;
            alert.CooldownSeconds = cooldownSeconds;
            alert.AutoResolvedAt = null;
            alert.ConditionJson = conditionJson;

            try
            {
                await db.SaveChangesAsync(ct);
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
                alert.Symbol = model.Symbol;
                alert.Severity = AlertSeverity.High;
                alert.CooldownSeconds = cooldownSeconds;
                alert.AutoResolvedAt = null;
                alert.ConditionJson = conditionJson;
                await db.SaveChangesAsync(ct);
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
                "MLConformalCalibration: model {0} ({1}/{2}) has been skipped for {3} consecutive cycles ({4}). The calibration pipeline cannot make progress until prediction logging is restored or the snapshot is repaired.",
                model.Id,
                model.Symbol,
                model.Timeframe,
                consecutiveSkips,
                skipReason);

            await dispatcher.DispatchAsync(alert, message, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch stale-calibration alert for model {ModelId} ({Symbol}/{Timeframe}).",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe);
            return false;
        }
    }

    private async Task<bool> ResolveStaleCalibrationAlertAsync(
        IServiceProvider serviceProvider,
        DbContext db,
        ActiveModelCandidate model,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string deduplicationKey = StaleAlertDeduplicationKey(model.Id);
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
                    "{Worker}: failed to auto-resolve stale-calibration alert {DeduplicationKey} for model {ModelId}.",
                    WorkerName,
                    deduplicationKey,
                    model.Id);
            }
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await db.SaveChangesAsync(ct);
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

    private static void DetachIfAdded(DbContext db, Alert alert)
    {
        var entry = db.Entry(alert);
        if (entry.State is EntityState.Added or EntityState.Modified)
            entry.State = EntityState.Detached;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static bool TryDeserializeSnapshot(byte[]? modelBytes, out ModelSnapshot snapshot)
    {
        snapshot = new ModelSnapshot();
        if (modelBytes is null || modelBytes.Length == 0)
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<ModelSnapshot>(modelBytes);
            if (parsed is null)
                return false;

            snapshot = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasModelWeights(ModelSnapshot snap) =>
        snap.Weights.Length > 0 ||
        !string.IsNullOrEmpty(snap.ConvWeightsJson) ||
        !string.IsNullOrEmpty(snap.GbmTreesJson) ||
        !string.IsNullOrEmpty(snap.TabNetAttentionJson) ||
        !string.IsNullOrEmpty(snap.FtTransformerAdditionalLayersJson) ||
        snap.FtTransformerAdditionalLayersBytes is { Length: > 0 } ||
        !string.IsNullOrEmpty(snap.RotationForestJson);

    private static DateTime GetEvidenceCutoff(
        ActiveModelCandidate model,
        MLConformalCalibrationWorkerSettings settings,
        DateTime nowUtc)
    {
        DateTime oldestAllowed = nowUtc.AddDays(-settings.MaxLogAgeDays);
        if (!settings.RequirePostActivationLogs)
            return oldestAllowed;

        DateTime servingStart = NormalizeUtc(model.ActivatedAt ?? model.TrainedAt);
        return servingStart > oldestAllowed ? servingStart : oldestAllowed;
    }

    private static bool IsStrictProbability(double value)
        => double.IsFinite(value) && value > 0.0 && value < 1.0;

    private static bool IsFiniteProbability(double value)
        => double.IsFinite(value) && value >= 0.0 && value <= 1.0;

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static MLConformalCalibrationWorkerSettings BuildSettings(MLConformalCalibrationOptions options)
    {
        int minLogs = ClampInt(options.MinLogs, 50, 10, 100_000);
        int modelBatchSize = ClampInt(options.ModelBatchSize, 100, 1, 10_000);

        return new MLConformalCalibrationWorkerSettings(
            InitialDelay: TimeSpan.FromMinutes(ClampInt(options.InitialDelayMinutes, 20, 0, 24 * 60)),
            PollInterval: TimeSpan.FromMinutes(ClampInt(options.PollIntervalMinutes, 30, 1, 7 * 24 * 60)),
            PollJitterSeconds: ClampInt(options.PollJitterSeconds, 300, 0, 24 * 60 * 60),
            MaxLogs: Math.Max(minLogs, ClampInt(options.MaxLogs, 500, minLogs, 100_000)),
            MinLogs: minLogs,
            MaxLogAgeDays: ClampInt(options.MaxLogAgeDays, 30, 1, 3650),
            MaxCalibrationAgeDays: ClampInt(options.MaxCalibrationAgeDays, 30, 1, 3650),
            TargetCoverage: ClampDouble(options.TargetCoverage, 0.90, 0.50, 0.999999),
            ModelBatchSize: modelBatchSize,
            MaxCycleModels: Math.Max(modelBatchSize, ClampInt(options.MaxCycleModels, 10_000, modelBatchSize, 100_000)),
            LockTimeoutSeconds: ClampInt(options.LockTimeoutSeconds, 5, 0, 300),
            RequirePostActivationLogs: options.RequirePostActivationLogs,
            MaxDegreeOfParallelism: ClampInt(options.MaxDegreeOfParallelism, 1, 1, MaxMaxDegreeOfParallelism),
            LongCycleWarnSeconds: ClampIntAllowingZero(options.LongCycleWarnSeconds, 300, 0, 24 * 60 * 60),
            StaleAlertEnabled: options.StaleAlertEnabled,
            StaleSkipAlertThreshold: ClampInt(options.StaleSkipAlertThreshold, 5, 1, 1000));
    }

    private static int ClampInt(int value, int defaultValue, int min, int max)
        => value < min || value > max ? defaultValue : value;

    private static int ClampIntAllowingZero(int value, int defaultValue, int min, int max)
    {
        if (value < 0)
            return defaultValue;

        return Math.Min(Math.Max(value, min), max);
    }

    private static double ClampDouble(double value, double defaultValue, double min, double max)
        => !double.IsFinite(value) || value < min || value > max ? defaultValue : value;

    private static TimeSpan GetIntervalWithJitter(MLConformalCalibrationWorkerSettings settings)
        => settings.PollJitterSeconds == 0
            ? settings.PollInterval
            : settings.PollInterval + TimeSpan.FromSeconds(Random.Shared.Next(0, settings.PollJitterSeconds + 1));
}
