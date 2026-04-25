using System.Data;
using System.Diagnostics;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
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
public sealed class MLConformalCalibrationWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLConformalCalibrationWorker);

    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";
    private const string DistributedLockKey = "workers:ml-conformal-calibration:cycle";
    private const double ProbabilityEpsilon = 1e-9;

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

    internal readonly record struct MLConformalCalibrationWorkerSettings(
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
        bool RequirePostActivationLogs);

    internal readonly record struct MLConformalCalibrationCycleResult(
        MLConformalCalibrationWorkerSettings Settings,
        string? SkippedReason,
        int CandidateModelCount,
        int EvaluatedModelCount,
        int CalibrationsWritten,
        int SkippedAlreadyCalibratedCount,
        int SkippedInvalidSnapshotCount,
        int SkippedInsufficientLogsCount,
        int SkippedPersistenceRaceCount)
    {
        public static MLConformalCalibrationCycleResult Skipped(
            MLConformalCalibrationWorkerSettings settings,
            string reason)
            => new(settings, reason, 0, 0, 0, 0, 0, 0, 0);
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

        int evaluated = 0;
        int written = 0;
        int skippedAlreadyCalibrated = 0;
        int skippedInvalidSnapshot = 0;
        int skippedInsufficientLogs = 0;
        int skippedPersistenceRace = 0;

        foreach (var batch in candidates.Chunk(settings.ModelBatchSize))
        {
            var batchCandidates = batch.ToArray();
            var modelIds = batchCandidates.Select(m => m.Id).ToArray();

            var existingCalibrations = await writeDb.Set<MLConformalCalibration>()
                .AsNoTracking()
                .Where(c => modelIds.Contains(c.MLModelId) && !c.IsDeleted)
                .OrderByDescending(c => c.CalibratedAt)
                .ThenByDescending(c => c.Id)
                .ToListAsync(ct);

            var existingByModelId = existingCalibrations
                .GroupBy(c => c.MLModelId)
                .ToDictionary(g => g.Key, g => g.ToArray());

            foreach (var model in batchCandidates)
            {
                ct.ThrowIfCancellationRequested();

                existingByModelId.TryGetValue(model.Id, out var modelCalibrations);
                if (modelCalibrations is not null
                    && modelCalibrations.Any(c => IsUsableCalibration(c, model, settings, nowUtc)))
                {
                    skippedAlreadyCalibrated++;
                    RecordSkip("already_calibrated", model);
                    continue;
                }

                if (!TryDeserializeSnapshot(model.ModelBytes, out var snapshot) || !HasModelWeights(snapshot))
                {
                    skippedInvalidSnapshot++;
                    RecordSkip("invalid_snapshot", model);
                    continue;
                }

                double decisionThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snapshot);
                DateTime evidenceCutoff = GetEvidenceCutoff(model, settings, nowUtc);
                var modelLogs = await LoadRecentResolvedLogsAsync(writeDb, model, settings, nowUtc, ct);
                var observations = BuildObservations(modelLogs, model, evidenceCutoff, decisionThreshold);

                if (observations.Count < settings.MinLogs)
                {
                    skippedInsufficientLogs++;
                    RecordSkip("insufficient_logs", model);
                    continue;
                }

                evaluated++;
                _metrics?.MLConformalCalibrationModelsEvaluated.Add(
                    1,
                    new("symbol", model.Symbol),
                    new("timeframe", model.Timeframe.ToString()));

                var calibration = ComputeCalibration(observations, settings.TargetCoverage);
                bool persisted = await PersistCalibrationAsync(
                    writeDb,
                    model,
                    calibration,
                    settings,
                    ct);

                if (!persisted)
                {
                    skippedPersistenceRace++;
                    RecordSkip("already_calibrated_after_recheck", model);
                    continue;
                }

                written++;
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
            }
        }

        double durationMs = Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
        _metrics?.MLConformalCalibrationCycleDurationMs.Record(durationMs);
        _metrics?.WorkerCycleDurationMs.Record(
            durationMs,
            new KeyValuePair<string, object?>("worker", WorkerName));

        _logger.LogInformation(
            "{Worker}: cycle complete. candidates={Candidates} evaluated={Evaluated} written={Written} skippedAlready={SkippedAlready} skippedInvalidSnapshot={SkippedInvalidSnapshot} skippedInsufficient={SkippedInsufficient} skippedRace={SkippedRace}",
            WorkerName,
            candidates.Count,
            evaluated,
            written,
            skippedAlreadyCalibrated,
            skippedInvalidSnapshot,
            skippedInsufficientLogs,
            skippedPersistenceRace);

        return new MLConformalCalibrationCycleResult(
            settings,
            SkippedReason: null,
            CandidateModelCount: candidates.Count,
            EvaluatedModelCount: evaluated,
            CalibrationsWritten: written,
            SkippedAlreadyCalibratedCount: skippedAlreadyCalibrated,
            SkippedInvalidSnapshotCount: skippedInvalidSnapshot,
            SkippedInsufficientLogsCount: skippedInsufficientLogs,
            SkippedPersistenceRaceCount: skippedPersistenceRace);
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
            RequirePostActivationLogs: options.RequirePostActivationLogs);
    }

    private static int ClampInt(int value, int defaultValue, int min, int max)
        => value < min || value > max ? defaultValue : value;

    private static double ClampDouble(double value, double defaultValue, double min, double max)
        => !double.IsFinite(value) || value < min || value > max ? defaultValue : value;

    private static TimeSpan GetIntervalWithJitter(MLConformalCalibrationWorkerSettings settings)
        => settings.PollJitterSeconds == 0
            ? settings.PollInterval
            : settings.PollInterval + TimeSpan.FromSeconds(Random.Shared.Next(0, settings.PollJitterSeconds + 1));
}
