using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Refits the isotonic (PAVA) calibration curve for active production ML models using
/// recent resolved prediction logs, then patches the serialized snapshot in-place when
/// the candidate curve improves or preserves deployed ECE.
/// </summary>
public sealed class MLIsotonicRecalibrationWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLIsotonicRecalibrationWorker);

    private const string DistributedLockKey = "workers:ml-isotonic-recalibration:cycle";
    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";
    private const string ConfigPrefixUpper = "MLISOTONICRECAL:";

    private const string CK_Enabled = "MLIsotonicRecal:Enabled";
    private const string CK_InitialDelaySeconds = "MLIsotonicRecal:InitialDelaySeconds";
    private const string CK_PollSecs = "MLIsotonicRecal:PollIntervalSeconds";
    private const string CK_WindowDays = "MLIsotonicRecal:WindowDays";
    private const string CK_MinResolved = "MLIsotonicRecal:MinResolved";
    private const string CK_MaxModelsPerCycle = "MLIsotonicRecal:MaxModelsPerCycle";
    private const string CK_MaxPredictionLogsPerModel = "MLIsotonicRecal:MaxPredictionLogsPerModel";
    private const string CK_MinPavaSegments = "MLIsotonicRecal:MinPavaSegments";
    private const string CK_MaxBreakpoints = "MLIsotonicRecal:MaxBreakpoints";
    private const string CK_MinimumEceImprovement = "MLIsotonicRecal:MinimumEceImprovement";
    private const string CK_LockTimeoutSeconds = "MLIsotonicRecal:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSeconds = "MLIsotonicRecal:DbCommandTimeoutSeconds";
    private const double ProbabilityGroupingTolerance = 1e-12;

    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MLIsotonicRecalibrationWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly MLIsotonicRecalibrationOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    /// <summary>Initialises the worker with its required dependencies.</summary>
    public MLIsotonicRecalibrationWorker(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<MLIsotonicRecalibrationWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        MLIsotonicRecalibrationOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new MLIsotonicRecalibrationOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Refits active ML model isotonic calibration curves from recent resolved outcomes.",
            TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds)));

        DateTime lastCycleStartUtc = DateTime.MinValue;
        DateTime lastSuccessUtc = DateTime.MinValue;
        TimeSpan currentPollInterval = TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds));

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName)
                               + TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(_options.InitialDelaySeconds));
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                if (lastSuccessUtc != DateTime.MinValue)
                    _metrics?.MLIsotonicRecalibrationTimeSinceLastSuccessSec.Record((nowUtc - lastSuccessUtc).TotalSeconds);

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
                        _healthMonitor?.RecordCycleSuccess(WorkerName, elapsedMs);
                        _metrics?.WorkerCycleDurationMs.Record(elapsedMs, Tag("worker", WorkerName));
                        _metrics?.MLIsotonicRecalibrationCycleDurationMs.Record(elapsedMs);

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
                                "{Worker}: candidates={Candidates}, evaluated={Evaluated}, updated={Updated}, skipped={Skipped}, samples={Samples}.",
                                WorkerName,
                                result.CandidateModelCount,
                                result.ModelsEvaluated,
                                result.SnapshotsUpdated,
                                result.ModelsSkipped,
                                result.ResolvedSamplesUsed);
                        }

                        var previousFailures = ConsecutiveCycleFailures;
                        if (previousFailures > 0)
                        {
                            _healthMonitor?.RecordRecovery(WorkerName, previousFailures);
                            _logger.LogInformation(
                                "{Worker}: recovered after {Failures} consecutive failure(s).",
                                WorkerName,
                                previousFailures);
                        }

                        ConsecutiveCycleFailures = 0;
                        lastSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _consecutiveCycleFailuresField);
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                        _metrics?.WorkerErrors.Add(1, Tag("worker", WorkerName), Tag("reason", "ml_isotonic_recalibration_cycle"));
                        _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                    }
                }

                var delay = ConsecutiveCycleFailures > 0
                    ? CalculateBackoffDelay(ConsecutiveCycleFailures)
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

    internal async Task<IsotonicCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx = readDb.GetDbContext();
        var writeCtx = writeDb.GetDbContext();

        var settings = await LoadSettingsAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, settings.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, settings.DbCommandTimeoutSeconds);

        if (!settings.Enabled)
        {
            RecordCycleSkipped("disabled");
            return IsotonicCycleResult.Skipped(settings, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLIsotonicRecalibrationLockAttempts.Add(1, Tag("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate isotonic recalibration cycles are possible in multi-instance deployments.",
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
                _metrics?.MLIsotonicRecalibrationLockAttempts.Add(1, Tag("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                return IsotonicCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLIsotonicRecalibrationLockAttempts.Add(1, Tag("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RecalibrateModelsCoreAsync(readCtx, writeCtx, settings, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    internal async Task RecalibrateModelsAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(readCtx, _options, ct);
        if (!settings.Enabled)
            return;

        ApplyCommandTimeout(readCtx, settings.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, settings.DbCommandTimeoutSeconds);
        await RecalibrateModelsCoreAsync(readCtx, writeCtx, settings, ct);
    }

    private async Task<IsotonicCycleResult> RecalibrateModelsCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        IsotonicSettings settings,
        CancellationToken ct)
    {
        var candidates = await LoadActiveModelCandidatesAsync(readCtx, settings.MaxModelsPerCycle, ct);
        _healthMonitor?.RecordBacklogDepth(WorkerName, candidates.Selected.Count);

        if (candidates.Selected.Count == 0)
        {
            RecordCycleSkipped("no_active_models");
            return new IsotonicCycleResult(settings, 0, 0, 0, 0, 0, "no_active_models");
        }

        var evaluated = 0;
        var skipped = candidates.SkippedByLimit + candidates.SkippedInvalidModel;
        var updated = 0;
        var sampleCount = 0;

        if (candidates.SkippedByLimit > 0)
            _metrics?.MLIsotonicRecalibrationModelsSkipped.Add(candidates.SkippedByLimit, Tag("reason", "cycle_limit"));
        if (candidates.SkippedInvalidModel > 0)
            _metrics?.MLIsotonicRecalibrationModelsSkipped.Add(candidates.SkippedInvalidModel, Tag("reason", "invalid_model"));

        foreach (var candidate in candidates.Selected)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var outcome = await RecalibrateModelAsync(candidate, readCtx, writeCtx, settings, ct);
                if (outcome.Evaluated)
                    evaluated++;
                if (outcome.Updated)
                    updated++;
                if (outcome.SkipReason is { Length: > 0 })
                {
                    skipped++;
                    _metrics?.MLIsotonicRecalibrationModelsSkipped.Add(1, Tag("reason", outcome.SkipReason));
                }
                if (outcome.Samples > 0)
                {
                    sampleCount += outcome.Samples;
                    _metrics?.MLIsotonicRecalibrationResolvedSamples.Record(outcome.Samples);
                }
                if (outcome.BreakpointSegments > 0)
                    _metrics?.MLIsotonicRecalibrationBreakpoints.Record(outcome.BreakpointSegments);
                if (outcome.CurrentEce.HasValue && outcome.NewEce.HasValue)
                    _metrics?.MLIsotonicRecalibrationEceDelta.Record(outcome.CurrentEce.Value - outcome.NewEce.Value);

                writeCtx.ChangeTracker.Clear();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                skipped++;
                _metrics?.MLIsotonicRecalibrationModelsSkipped.Add(1, Tag("reason", "model_error"));
                _logger.LogWarning(
                    ex,
                    "{Worker}: recalibration failed for model {ModelId} ({Symbol}/{Timeframe}); skipping.",
                    WorkerName,
                    candidate.Id,
                    candidate.Symbol,
                    candidate.Timeframe);
                writeCtx.ChangeTracker.Clear();
            }
        }

        if (evaluated > 0)
            _metrics?.MLIsotonicRecalibrationModelsEvaluated.Add(evaluated);
        if (updated > 0)
            _metrics?.MLIsotonicRecalibrationSnapshotsUpdated.Add(updated);

        return new IsotonicCycleResult(
            settings,
            candidates.Selected.Count + candidates.SkippedByLimit + candidates.SkippedInvalidModel,
            evaluated,
            skipped,
            updated,
            sampleCount,
            null);
    }

    private async Task<ModelRecalibrationOutcome> RecalibrateModelAsync(
        ActiveModelCandidate model,
        DbContext readCtx,
        DbContext writeCtx,
        IsotonicSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var (writeModel, snapshot) = await MLModelSnapshotWriteHelper.LoadTrackedLatestSnapshotAsync(writeCtx, model.Id, ct);
        if (writeModel is null || snapshot is null)
            return ModelRecalibrationOutcome.Skipped("invalid_snapshot");

        if (!IsRoutableModel(writeModel))
            return ModelRecalibrationOutcome.Skipped("not_routable");

        var decisionThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snapshot);
        var since = nowUtc.AddDays(-settings.WindowDays);
        var logs = await LoadResolvedLogsAsync(readCtx, model.Id, since, settings.MaxPredictionLogsPerModel, ct);
        if (logs.Count < settings.MinResolved)
            return ModelRecalibrationOutcome.Skipped("insufficient_samples", logs.Count);

        var pairs = BuildCalibrationPairs(logs, snapshot, decisionThreshold, nowUtc);
        if (pairs.Count < settings.MinResolved)
            return ModelRecalibrationOutcome.Skipped("invalid_probability_samples", pairs.Count);

        var pavaInput = pairs
            .OrderBy(p => p.PreIsotonicProbability)
            .Select(p => (P: p.PreIsotonicProbability, Y: p.Label))
            .ToList();
        var newBreakpoints = SanitizeBreakpoints(FitPAVA(pavaInput), settings.MaxBreakpoints);
        var breakpointSegments = newBreakpoints.Length / 2;
        if (breakpointSegments < settings.MinPavaSegments)
            return ModelRecalibrationOutcome.EvaluatedSkip("pava_too_small", pairs.Count, breakpointSegments);

        var currentEce = ComputeEce(pairs.Select(p => (P: p.CurrentProbability, p.Label)).ToList());
        var newEce = ComputeEce(pairs
            .Select(p => (P: ApplyFullCalibration(p.RawProbability, snapshot, newBreakpoints, nowUtc), p.Label))
            .ToList());

        if (newEce > currentEce - settings.MinimumEceImprovement)
        {
            _logger.LogDebug(
                "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) skipped because isotonic candidate did not improve ECE enough ({Current:F6}->{New:F6}, minDelta={MinDelta:F6}).",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe,
                currentEce,
                newEce,
                settings.MinimumEceImprovement);
            return ModelRecalibrationOutcome.EvaluatedSkip("no_ece_improvement", pairs.Count, breakpointSegments, currentEce, newEce);
        }

        var reliability = ComputeReliabilityBins(
            pairs.Select(p => (P: ApplyFullCalibration(p.RawProbability, snapshot, newBreakpoints, nowUtc), p.Label)).ToList());

        snapshot.IsotonicBreakpoints = newBreakpoints;
        snapshot.Ece = newEce;
        snapshot.ReliabilityBinConfidence = reliability.Confidence;
        snapshot.ReliabilityBinAccuracy = reliability.Accuracy;
        snapshot.ReliabilityBinCounts = reliability.Counts;
        UpdateTcnCalibrationArtifact(snapshot, newBreakpoints, pairs.Count);

        writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        await writeCtx.SaveChangesAsync(ct);
        _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");

        _logger.LogInformation(
            "{Worker}: updated isotonic breakpoints for model {ModelId} ({Symbol}/{Timeframe}) using {Samples} samples: ECE {Current:F6}->{New:F6}, segments={Segments}.",
            WorkerName,
            model.Id,
            model.Symbol,
            model.Timeframe,
            pairs.Count,
            currentEce,
            newEce,
            breakpointSegments);

        return ModelRecalibrationOutcome.Applied(pairs.Count, breakpointSegments, currentEce, newEce);
    }

    private static async Task<CandidateSelection> LoadActiveModelCandidatesAsync(
        DbContext readCtx,
        int maxModelsPerCycle,
        CancellationToken ct)
    {
        var query = readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && !m.IsSuppressed
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer
                        && (m.Status == MLModelStatus.Active || m.IsFallbackChampion)
                        && m.ModelBytes != null
                        && m.ModelBytes.Length > 0);

        var rows = await query
            .OrderByDescending(m => m.ActivatedAt ?? m.TrainedAt)
            .ThenBy(m => m.Id)
            .Take(maxModelsPerCycle + 1)
            .Select(m => new ActiveModelCandidate(
                m.Id,
                NormalizeSymbol(m.Symbol),
                m.Timeframe,
                m.ActivatedAt ?? m.TrainedAt))
            .ToListAsync(ct);

        var invalid = rows.Count(r => string.IsNullOrWhiteSpace(r.Symbol));
        rows = rows.Where(r => !string.IsNullOrWhiteSpace(r.Symbol)).ToList();

        var truncated = rows.Count > maxModelsPerCycle;
        var skippedByLimit = 0;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
            var totalActive = await query.CountAsync(ct);
            skippedByLimit = Math.Max(0, totalActive - maxModelsPerCycle - invalid);
        }

        return new CandidateSelection(rows, skippedByLimit, invalid);
    }

    private static async Task<List<ResolvedPredictionLog>> LoadResolvedLogsAsync(
        DbContext readCtx,
        long modelId,
        DateTime sinceUtc,
        int maxPredictionLogsPerModel,
        CancellationToken ct)
    {
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId == modelId
                        && l.ModelRole == ModelRole.Champion
                        && l.OutcomeRecordedAt != null
                        && l.OutcomeRecordedAt >= sinceUtc
                        && l.ActualDirection != null
                        && l.DirectionCorrect != null
                        && !l.IsDeleted)
            .OrderByDescending(l => l.OutcomeRecordedAt)
            .ThenByDescending(l => l.Id)
            .Take(maxPredictionLogsPerModel)
            .Select(l => new ResolvedPredictionLog(
                l.Id,
                l.PredictedDirection,
                l.ConfidenceScore,
                l.RawProbability,
                l.CalibratedProbability,
                l.DecisionThresholdUsed,
                l.EnsembleDisagreement,
                l.ActualDirection!.Value,
                l.OutcomeRecordedAt!.Value))
            .ToListAsync(ct);

        logs.Sort(static (a, b) =>
        {
            var byTime = a.OutcomeRecordedAt.CompareTo(b.OutcomeRecordedAt);
            return byTime != 0 ? byTime : a.Id.CompareTo(b.Id);
        });
        return logs;
    }

    private static List<CalibrationPair> BuildCalibrationPairs(
        IReadOnlyList<ResolvedPredictionLog> logs,
        ModelSnapshot snapshot,
        double decisionThreshold,
        DateTime nowUtc)
    {
        var pairs = new List<CalibrationPair>(logs.Count);
        foreach (var log in logs)
        {
            var shim = new MLModelPredictionLog
            {
                PredictedDirection = log.PredictedDirection,
                ConfidenceScore = log.ConfidenceScore,
                RawProbability = log.RawProbability,
                CalibratedProbability = log.CalibratedProbability,
                DecisionThresholdUsed = log.DecisionThresholdUsed,
                EnsembleDisagreement = log.EnsembleDisagreement,
                ActualDirection = log.ActualDirection
            };

            var rawP = MLFeatureHelper.ResolveLoggedRawBuyProbability(shim, decisionThreshold);
            var preIsoP = ApplyPreIsotonicCalibration(rawP, snapshot, nowUtc);
            var currentP = ApplyFullCalibration(rawP, snapshot, null, nowUtc);
            if (!double.IsFinite(rawP) || !double.IsFinite(preIsoP) || !double.IsFinite(currentP))
                continue;

            pairs.Add(new CalibrationPair(
                Math.Clamp(rawP, 0.0, 1.0),
                Math.Clamp(preIsoP, 0.0, 1.0),
                Math.Clamp(currentP, 0.0, 1.0),
                log.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0));
        }

        return pairs;
    }

    private static double ApplyPreIsotonicCalibration(
        double rawP,
        ModelSnapshot snapshot,
        DateTime nowUtc)
    {
        var stack = ResolveCalibrationStack(snapshot);
        return InferenceHelpers.ApplyDeployedCalibration(
            rawP,
            stack.PlattA,
            stack.PlattB,
            stack.TemperatureScale,
            stack.PlattABuy,
            stack.PlattBBuy,
            stack.PlattASell,
            stack.PlattBSell,
            stack.RoutingThreshold,
            [],
            snapshot.AgeDecayLambda,
            snapshot.TrainedAtUtc,
            applyAgeDecay: false,
            nowUtc);
    }

    private static double ApplyFullCalibration(
        double rawP,
        ModelSnapshot snapshot,
        double[]? isotonicOverride,
        DateTime nowUtc)
    {
        var stack = ResolveCalibrationStack(snapshot);
        return InferenceHelpers.ApplyDeployedCalibration(
            rawP,
            stack.PlattA,
            stack.PlattB,
            stack.TemperatureScale,
            stack.PlattABuy,
            stack.PlattBBuy,
            stack.PlattASell,
            stack.PlattBSell,
            stack.RoutingThreshold,
            isotonicOverride ?? stack.IsotonicBreakpoints,
            snapshot.AgeDecayLambda,
            snapshot.TrainedAtUtc,
            applyAgeDecay: true,
            nowUtc);
    }

    private static CalibrationStack ResolveCalibrationStack(ModelSnapshot snapshot)
    {
        if (string.Equals(snapshot.Type, "TCN", StringComparison.OrdinalIgnoreCase))
        {
            var tcn = TcnSnapshotSupport.ResolveCalibrationArtifact(snapshot);
            return new CalibrationStack(
                tcn.GlobalPlattA,
                tcn.GlobalPlattB,
                tcn.TemperatureScale,
                tcn.BuyBranchPlattA,
                tcn.BuyBranchPlattB,
                tcn.SellBranchPlattA,
                tcn.SellBranchPlattB,
                tcn.ConditionalRoutingThreshold,
                tcn.IsotonicBreakpoints ?? []);
        }

        return new CalibrationStack(
            snapshot.PlattA,
            snapshot.PlattB,
            snapshot.TemperatureScale,
            snapshot.PlattABuy,
            snapshot.PlattBBuy,
            snapshot.PlattASell,
            snapshot.PlattBSell,
            snapshot.ConditionalCalibrationRoutingThreshold,
            snapshot.IsotonicBreakpoints ?? []);
    }

    private static void UpdateTcnCalibrationArtifact(
        ModelSnapshot snapshot,
        double[] breakpoints,
        int samples)
    {
        if (!string.Equals(snapshot.Type, "TCN", StringComparison.OrdinalIgnoreCase) ||
            snapshot.TcnCalibrationArtifact is null)
        {
            return;
        }

        snapshot.TcnCalibrationArtifact.IsotonicBreakpoints = breakpoints;
        snapshot.TcnCalibrationArtifact.IsotonicSampleCount = samples;
        snapshot.TcnCalibrationArtifact.IsotonicBreakpointCount = breakpoints.Length / 2;
        snapshot.TcnCalibrationArtifact.DiagnosticsSampleCount = Math.Max(
            snapshot.TcnCalibrationArtifact.DiagnosticsSampleCount,
            samples);
    }

    private static double[] FitPAVA(List<(double P, double Y)> pairs)
    {
        if (pairs.Count == 0)
            return [];

        var stack = new List<(double SumY, double SumP, int Count)>(pairs.Count);
        foreach (var block in CoalesceSortedProbabilityBlocks(pairs))
        {
            stack.Add((block.SumY, block.SumP, block.Count));
            while (stack.Count >= 2)
            {
                var last = stack[^1];
                var previous = stack[^2];
                if (previous.SumY / previous.Count <= last.SumY / last.Count)
                    break;

                stack.RemoveAt(stack.Count - 1);
                stack[^1] = (
                    previous.SumY + last.SumY,
                    previous.SumP + last.SumP,
                    previous.Count + last.Count);
            }
        }

        var breakpoints = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            breakpoints[i * 2] = stack[i].SumP / stack[i].Count;
            breakpoints[i * 2 + 1] = stack[i].SumY / stack[i].Count;
        }

        return breakpoints;
    }

    private static List<(double SumY, double SumP, int Count)> CoalesceSortedProbabilityBlocks(
        List<(double P, double Y)> pairs)
    {
        var blocks = new List<(double SumY, double SumP, int Count)>(pairs.Count);
        var currentProbability = pairs[0].P;
        var sumY = 0.0;
        var sumP = 0.0;
        var count = 0;

        foreach (var (probability, label) in pairs)
        {
            if (count > 0 && Math.Abs(probability - currentProbability) > ProbabilityGroupingTolerance)
            {
                blocks.Add((sumY, sumP, count));
                currentProbability = probability;
                sumY = 0.0;
                sumP = 0.0;
                count = 0;
            }

            sumY += label;
            sumP += probability;
            count++;
        }

        if (count > 0)
            blocks.Add((sumY, sumP, count));

        return blocks;
    }

    private static double[] SanitizeBreakpoints(double[] breakpoints, int maxSegments)
    {
        if (breakpoints.Length < 4)
            return [];

        var points = new List<(double X, double Y)>(breakpoints.Length / 2);
        for (int i = 0; i + 1 < breakpoints.Length; i += 2)
        {
            var x = breakpoints[i];
            var y = breakpoints[i + 1];
            if (!double.IsFinite(x) || !double.IsFinite(y))
                continue;

            x = Math.Clamp(x, 0.0, 1.0);
            y = Math.Clamp(y, 0.0, 1.0);
            if (points.Count > 0)
            {
                x = Math.Max(x, points[^1].X);
                y = Math.Max(y, points[^1].Y);
            }
            points.Add((x, y));
        }

        if (points.Count < 2)
            return [];

        if (points.Count > maxSegments)
        {
            var compressed = new List<(double X, double Y)>(maxSegments);
            var lastIndex = points.Count - 1;
            var lastSelected = -1;
            for (int i = 0; i < maxSegments; i++)
            {
                var index = (int)Math.Round(i * lastIndex / (double)(maxSegments - 1), MidpointRounding.AwayFromZero);
                if (index == lastSelected && index < lastIndex)
                    index++;
                compressed.Add(points[index]);
                lastSelected = index;
            }
            points = compressed;
        }

        var sanitized = new double[points.Count * 2];
        for (int i = 0; i < points.Count; i++)
        {
            sanitized[i * 2] = points[i].X;
            sanitized[i * 2 + 1] = points[i].Y;
        }

        return sanitized;
    }

    private static double ComputeEce(IReadOnlyList<(double P, double Label)> pairs)
    {
        if (pairs.Count == 0)
            return 0.0;

        const int bins = 10;
        var binConf = new double[bins];
        var binLabel = new double[bins];
        var binN = new int[bins];

        foreach (var (probability, label) in pairs)
        {
            var p = Math.Clamp(probability, 0.0, 1.0);
            var bin = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[bin] += p;
            binLabel[bin] += label;
            binN[bin]++;
        }

        var ece = 0.0;
        for (int i = 0; i < bins; i++)
        {
            if (binN[i] == 0)
                continue;

            ece += (double)binN[i] / pairs.Count * Math.Abs(binLabel[i] / binN[i] - binConf[i] / binN[i]);
        }

        return Math.Clamp(ece, 0.0, 1.0);
    }

    private static (double[] Confidence, double[] Accuracy, int[] Counts) ComputeReliabilityBins(
        IReadOnlyList<(double P, double Label)> pairs)
    {
        const int bins = 10;
        var confidence = new double[bins];
        var accuracy = new double[bins];
        var counts = new int[bins];

        foreach (var (probability, label) in pairs)
        {
            var p = Math.Clamp(probability, 0.0, 1.0);
            var bin = Math.Clamp((int)(p * bins), 0, bins - 1);
            confidence[bin] += p;
            accuracy[bin] += label;
            counts[bin]++;
        }

        for (int i = 0; i < bins; i++)
        {
            if (counts[i] == 0)
                continue;

            confidence[i] /= counts[i];
            accuracy[i] /= counts[i];
        }

        return (confidence, accuracy, counts);
    }

    private async Task<IsotonicSettings> LoadSettingsAsync(
        DbContext readCtx,
        MLIsotonicRecalibrationOptions options,
        CancellationToken ct)
    {
        var fallback = IsotonicSettings.FromOptions(options);

        Dictionary<string, string?> values;
        try
        {
            var rows = await readCtx.Set<EngineConfig>()
                .AsNoTracking()
                .Where(c => c.Key.ToUpper().StartsWith(ConfigPrefixUpper) && !c.IsDeleted)
                .Select(c => new { c.Id, c.Key, Value = (string?)c.Value, c.LastUpdatedAt })
                .ToListAsync(ct);

            values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.OrderBy(r => r.LastUpdatedAt).ThenBy(r => r.Id))
            {
                if (!string.IsNullOrWhiteSpace(row.Key))
                    values[row.Key.Trim()] = row.Value;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "{Worker}: failed to read EngineConfig isotonic recalibration settings; using options/defaults.",
                WorkerName);
            values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        var maxLogs = GetInt(values, CK_MaxPredictionLogsPerModel, fallback.MaxPredictionLogsPerModel, 10, 1_000_000);
        var minResolved = GetInt(values, CK_MinResolved, fallback.MinResolved, 10, 1_000_000);
        maxLogs = Math.Max(maxLogs, minResolved);

        var maxBreakpoints = GetInt(values, CK_MaxBreakpoints, fallback.MaxBreakpoints, 2, 10_000);
        var minPavaSegments = GetInt(values, CK_MinPavaSegments, fallback.MinPavaSegments, 2, 1_000);
        maxBreakpoints = Math.Max(maxBreakpoints, minPavaSegments);

        return new IsotonicSettings(
            Enabled: GetBool(values, CK_Enabled, fallback.Enabled),
            InitialDelay: TimeSpan.FromSeconds(GetInt(values, CK_InitialDelaySeconds, (int)fallback.InitialDelay.TotalSeconds, 0, 86_400)),
            PollInterval: TimeSpan.FromSeconds(GetInt(values, CK_PollSecs, (int)fallback.PollInterval.TotalSeconds, 1, 86_400)),
            WindowDays: GetInt(values, CK_WindowDays, fallback.WindowDays, 1, 3650),
            MinResolved: minResolved,
            MaxModelsPerCycle: GetInt(values, CK_MaxModelsPerCycle, fallback.MaxModelsPerCycle, 1, 250_000),
            MaxPredictionLogsPerModel: maxLogs,
            MinPavaSegments: minPavaSegments,
            MaxBreakpoints: maxBreakpoints,
            MinimumEceImprovement: GetDouble(values, CK_MinimumEceImprovement, fallback.MinimumEceImprovement, 0.0, 1.0),
            LockTimeoutSeconds: GetInt(values, CK_LockTimeoutSeconds, fallback.LockTimeoutSeconds, 0, 300),
            DbCommandTimeoutSeconds: GetInt(values, CK_DbCommandTimeoutSeconds, fallback.DbCommandTimeoutSeconds, 1, 600));
    }

    private static bool IsRoutableModel(MLModel model)
        => model.IsActive
           && !model.IsDeleted
           && !model.IsSuppressed
           && !model.IsMetaLearner
           && !model.IsMamlInitializer
           && (model.Status == MLModelStatus.Active || model.IsFallbackChampion)
           && model.ModelBytes is { Length: > 0 };

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLIsotonicRecalibrationCyclesSkipped.Add(1, Tag("reason", reason));

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        if (db.Database.IsRelational())
            db.Database.SetCommandTimeout(seconds);
    }

    private TimeSpan CalculateBackoffDelay(int consecutiveFailures)
    {
        var exponent = Math.Min(6, Math.Max(0, consecutiveFailures - 1));
        var delay = TimeSpan.FromTicks(InitialRetryDelay.Ticks * (1L << exponent));
        return delay <= MaxRetryDelay ? delay : MaxRetryDelay;
    }

    private static int GetInt(
        IReadOnlyDictionary<string, string?> values,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        return values.TryGetValue(key, out var raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : Math.Clamp(defaultValue, min, max);
    }

    private static double GetDouble(
        IReadOnlyDictionary<string, string?> values,
        string key,
        double defaultValue,
        double min,
        double max)
    {
        return values.TryGetValue(key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
               double.IsFinite(parsed)
            ? Math.Clamp(parsed, min, max)
            : Math.Clamp(defaultValue, min, max);
    }

    private static bool GetBool(
        IReadOnlyDictionary<string, string?> values,
        string key,
        bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (bool.TryParse(raw, out var parsed))
            return parsed;

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            ? true
            : raw.Equals("0", StringComparison.OrdinalIgnoreCase) ||
              raw.Equals("no", StringComparison.OrdinalIgnoreCase)
                ? false
                : defaultValue;
    }

    private static int NormalizeInitialDelaySeconds(int value)
        => value is >= 0 and <= 86_400 ? value : 0;

    private static int NormalizePollSeconds(int value)
        => value is >= 1 and <= 86_400 ? value : 28_800;

    private static string NormalizeSymbol(string? symbol)
        => string.IsNullOrWhiteSpace(symbol) ? string.Empty : symbol.Trim().ToUpperInvariant();

    private static KeyValuePair<string, object?> Tag(string name, object? value)
        => new(name, value);

    internal sealed record IsotonicCycleResult(
        IsotonicSettings Settings,
        int CandidateModelCount,
        int ModelsEvaluated,
        int ModelsSkipped,
        int SnapshotsUpdated,
        int ResolvedSamplesUsed,
        string? SkippedReason)
    {
        public static IsotonicCycleResult Skipped(IsotonicSettings settings, string reason)
            => new(settings, 0, 0, 0, 0, 0, reason);
    }

    internal sealed record IsotonicSettings(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int WindowDays,
        int MinResolved,
        int MaxModelsPerCycle,
        int MaxPredictionLogsPerModel,
        int MinPavaSegments,
        int MaxBreakpoints,
        double MinimumEceImprovement,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds)
    {
        public static IsotonicSettings FromOptions(MLIsotonicRecalibrationOptions options)
        {
            var minResolved = Math.Clamp(options.MinResolved, 10, 1_000_000);
            var maxLogs = Math.Max(Math.Clamp(options.MaxPredictionLogsPerModel, 10, 1_000_000), minResolved);
            var minSegments = Math.Clamp(options.MinPavaSegments, 2, 1_000);
            var maxBreakpoints = Math.Max(Math.Clamp(options.MaxBreakpoints, 2, 10_000), minSegments);

            return new IsotonicSettings(
                options.Enabled,
                TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(options.InitialDelaySeconds)),
                TimeSpan.FromSeconds(NormalizePollSeconds(options.PollIntervalSeconds)),
                Math.Clamp(options.WindowDays, 1, 3650),
                minResolved,
                Math.Clamp(options.MaxModelsPerCycle, 1, 250_000),
                maxLogs,
                minSegments,
                maxBreakpoints,
                double.IsFinite(options.MinimumEceImprovement)
                    ? Math.Clamp(options.MinimumEceImprovement, 0.0, 1.0)
                    : 0.0,
                Math.Clamp(options.LockTimeoutSeconds, 0, 300),
                Math.Clamp(options.DbCommandTimeoutSeconds, 1, 600));
        }
    }

    private sealed record CandidateSelection(
        List<ActiveModelCandidate> Selected,
        int SkippedByLimit,
        int SkippedInvalidModel);

    private sealed record ActiveModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        DateTime ActivatedAt);

    private sealed record ResolvedPredictionLog(
        long Id,
        TradeDirection PredictedDirection,
        decimal ConfidenceScore,
        decimal? RawProbability,
        decimal? CalibratedProbability,
        decimal? DecisionThresholdUsed,
        decimal? EnsembleDisagreement,
        TradeDirection ActualDirection,
        DateTime OutcomeRecordedAt);

    private sealed record CalibrationPair(
        double RawProbability,
        double PreIsotonicProbability,
        double CurrentProbability,
        double Label);

    private readonly record struct CalibrationStack(
        double PlattA,
        double PlattB,
        double TemperatureScale,
        double PlattABuy,
        double PlattBBuy,
        double PlattASell,
        double PlattBSell,
        double RoutingThreshold,
        double[] IsotonicBreakpoints);

    private sealed record ModelRecalibrationOutcome(
        bool Evaluated,
        bool Updated,
        int Samples,
        int BreakpointSegments,
        double? CurrentEce,
        double? NewEce,
        string? SkipReason)
    {
        public static ModelRecalibrationOutcome Skipped(
            string reason,
            int samples = 0,
            int breakpointSegments = 0,
            double? currentEce = null,
            double? newEce = null)
            => new(false, false, samples, breakpointSegments, currentEce, newEce, reason);

        public static ModelRecalibrationOutcome EvaluatedSkip(
            string reason,
            int samples,
            int breakpointSegments,
            double? currentEce = null,
            double? newEce = null)
            => new(true, false, samples, breakpointSegments, currentEce, newEce, reason);

        public static ModelRecalibrationOutcome Applied(
            int samples,
            int breakpointSegments,
            double currentEce,
            double newEce)
            => new(true, true, samples, breakpointSegments, currentEce, newEce, null);
    }
}
