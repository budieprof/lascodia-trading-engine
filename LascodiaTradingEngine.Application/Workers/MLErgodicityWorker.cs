using System.Diagnostics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes ergodicity economics metrics for active production ML models.
/// </summary>
/// <remarks>
/// Ergodicity economics distinguishes arithmetic ensemble-average growth from geometric
/// time-average growth. The gap between them is used to temper Kelly sizing so downstream
/// allocation favours long-run compounded wealth rather than one-step expected value.
/// </remarks>
public sealed class MLErgodicityWorker : BackgroundService
{
    private const string WorkerName = nameof(MLErgodicityWorker);
    private const string DistributedLockKey = "ml:ergodicity:cycle";
    private const double MinVariance = 1e-10;
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory        _scopeFactory;
    private readonly ILogger<MLErgodicityWorker> _logger;
    private readonly IDistributedLock?           _distributedLock;
    private readonly TimeProvider                _timeProvider;
    private readonly IWorkerHealthMonitor?       _healthMonitor;
    private readonly TradingMetrics?             _metrics;
    private readonly MLErgodicityOptions         _options;
    private readonly MLErgodicityConfigReader    _configReader;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    private static class EventIds
    {
        public static readonly EventId LockSkipped = new(4201, nameof(LockSkipped));
        public static readonly EventId CycleCompleted = new(4202, nameof(CycleCompleted));
        public static readonly EventId ModelSkipped = new(4203, nameof(ModelSkipped));
    }

    private sealed record ActiveModelSnapshot(long Id, string Symbol);

    private sealed record PredictionOutcomeSnapshot(
        long Id,
        long MLModelId,
        string Symbol,
        TradeDirection PredictedDirection,
        bool DirectionCorrect,
        DateTime OutcomeRecordedAt,
        decimal? ServedCalibratedProbability,
        decimal? CalibratedProbability,
        decimal? RawProbability,
        decimal? DecisionThresholdUsed,
        decimal ConfidenceScore,
        decimal? ActualMagnitudePips,
        bool? WasProfitable);

    private sealed record ErgodicityMetrics(
        double EnsembleGrowthRate,
        double TimeAverageGrowthRate,
        double ErgodicityGap,
        double NaiveKellyFraction,
        double ErgodicityAdjustedKelly,
        double GrowthRateVariance);

    internal sealed record ErgodicityCycleResult(
        int PollIntervalHours,
        int ActiveModelCount,
        int EvaluatedModelCount,
        int SkippedModelCount,
        int LogsWritten,
        string? SkippedReason)
    {
        public static ErgodicityCycleResult Skipped(
            MLErgodicityRuntimeConfig config,
            string reason)
            => new(
                config.PollIntervalHours,
                0,
                0,
                0,
                0,
                reason);
    }

    /// <summary>
    /// Initialises the worker with its DI dependencies.
    /// </summary>
    public MLErgodicityWorker(
        IServiceScopeFactory        scopeFactory,
        ILogger<MLErgodicityWorker> logger,
        IDistributedLock?           distributedLock = null,
        TimeProvider?               timeProvider = null,
        IWorkerHealthMonitor?       healthMonitor = null,
        TradingMetrics?             metrics = null,
        MLErgodicityOptions?        options = null,
        MLErgodicityConfigReader?   configReader = null)
    {
        _scopeFactory    = scopeFactory;
        _logger          = logger;
        _distributedLock = distributedLock;
        _timeProvider    = timeProvider ?? TimeProvider.System;
        _healthMonitor   = healthMonitor;
        _metrics         = metrics;
        _options         = options ?? new MLErgodicityOptions();
        _configReader    = configReader ?? new MLErgodicityConfigReader(_options);
    }

    /// <summary>
    /// Hosted-service entry point. Runs a bounded ergodicity cycle at the configured interval.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Computes model ergodicity economics and Kelly sizing diagnostics.",
            TimeSpan.FromHours(_options.PollIntervalHours));

        DateTime lastCycleStartUtc = DateTime.MinValue;
        DateTime lastSuccessUtc = DateTime.MinValue;
        TimeSpan currentPollInterval = TimeSpan.FromHours(Math.Clamp(_options.PollIntervalHours, 1, 168));

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName)
                               + TimeSpan.FromSeconds(Math.Clamp(_options.InitialDelaySeconds, 0, 86_400));
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                if (lastSuccessUtc != DateTime.MinValue)
                {
                    _metrics?.MLErgodicityTimeSinceLastSuccessSec.Record(
                        (nowUtc - lastSuccessUtc).TotalSeconds);
                }

                if (nowUtc - lastCycleStartUtc >= currentPollInterval)
                {
                    lastCycleStartUtc = nowUtc;
                    var cycleStart = Stopwatch.GetTimestamp();

                    try
                    {
                        _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                        var result = await RunCycleDetailedAsync(stoppingToken);
                        currentPollInterval = TimeSpan.FromHours(result.PollIntervalHours);

                        var durationMs = (long)Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
                        _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                        _metrics?.WorkerCycleDurationMs.Record(
                            durationMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.MLErgodicityCycleDurationMs.Record(durationMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug(
                                "{Worker}: cycle skipped ({Reason}).",
                                WorkerName,
                                result.SkippedReason);
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
                        _metrics?.WorkerErrors.Add(
                            1,
                            new KeyValuePair<string, object?>("worker", WorkerName),
                            new KeyValuePair<string, object?>("reason", "ml_ergodicity_cycle"));
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
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

    internal async Task<int> RunCycleAsync(CancellationToken ct)
        => (await RunCycleDetailedAsync(ct)).PollIntervalHours;

    internal async Task<ErgodicityCycleResult> RunCycleDetailedAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx = readDb.GetDbContext();
        var writeCtx = writeDb.GetDbContext();

        var config = await _configReader.LoadAsync(readCtx, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
        {
            RecordCycleSkipped("disabled");
            return ErgodicityCycleResult.Skipped(config, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is not null)
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(config.LockTimeoutSeconds),
                ct);
            if (cycleLock is null)
            {
                _metrics?.MLErgodicityLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                _logger.LogDebug(
                    EventIds.LockSkipped,
                    "MLErgodicityWorker: cycle skipped because distributed lock is held elsewhere.");
                return ErgodicityCycleResult.Skipped(config, "lock_busy");
            }

            _metrics?.MLErgodicityLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));
        }
        else
        {
            _metrics?.MLErgodicityLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate ergodicity cycles are possible in multi-instance deployments.",
                    WorkerName);
            }
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                var result = await RunErgodicityAsync(readCtx, writeCtx, config, ct);

                _logger.LogInformation(
                    EventIds.CycleCompleted,
                    "MLErgodicityWorker: evaluated {Evaluated}/{Active} active models, skipped {Skipped}, wrote {Logs} logs.",
                    result.EvaluatedModelCount,
                    result.ActiveModelCount,
                    result.SkippedModelCount,
                    result.LogsWritten);

                return result;
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    private async Task<ErgodicityCycleResult> RunErgodicityAsync(
        DbContext readCtx,
        DbContext writeCtx,
        MLErgodicityRuntimeConfig config,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-config.WindowDays);

        var activeModelQuery = readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && (m.Status == MLModelStatus.Active || m.IsFallbackChampion)
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer
                        && !m.IsSuppressed);

        var activeModels = await activeModelQuery
            .OrderBy(m => m.Id)
            .Take(config.MaxCycleModels + 1)
            .Select(m => new ActiveModelSnapshot(m.Id, m.Symbol))
            .ToListAsync(ct);

        var skippedByLimit = 0;
        if (activeModels.Count > config.MaxCycleModels)
        {
            activeModels.RemoveAt(activeModels.Count - 1);
            var totalActiveModels = await activeModelQuery.CountAsync(ct);
            skippedByLimit = Math.Max(0, totalActiveModels - config.MaxCycleModels);
            if (skippedByLimit > 0)
            {
                _metrics?.MLErgodicityModelsSkipped.Add(
                    skippedByLimit,
                    new KeyValuePair<string, object?>("reason", "cycle_limit"));
            }
        }

        if (activeModels.Count == 0)
            return new ErgodicityCycleResult(config.PollIntervalHours, 0, 0, 0, 0, null);

        var outcomes = await LoadPredictionOutcomesAsync(
            readCtx,
            activeModels.Select(m => m.Id).ToArray(),
            cutoff,
            config,
            ct);

        var outcomesByModel = outcomes
            .GroupBy(l => l.MLModelId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PredictionOutcomeSnapshot>)g
                .OrderByDescending(l => l.OutcomeRecordedAt)
                .ThenByDescending(l => l.Id)
                .Take(config.MaxLogsPerModel)
                .ToArray());

        var calculatedLogs = new List<MLErgodicityLog>(activeModels.Count);
        int skipped = skippedByLimit;

        foreach (var model in activeModels)
        {
            if (!outcomesByModel.TryGetValue(model.Id, out var modelOutcomes))
            {
                skipped++;
                _metrics?.MLErgodicityModelsSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "no_outcomes"));
                _logger.LogDebug(
                    EventIds.ModelSkipped,
                    "MLErgodicityWorker: model {ModelId} ({Symbol}) skipped with no resolved outcomes.",
                    model.Id,
                    model.Symbol);
                continue;
            }

            if (modelOutcomes.Count < config.MinSamples)
            {
                skipped++;
                _metrics?.MLErgodicityModelsSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "below_min_samples"));
                _logger.LogDebug(
                    EventIds.ModelSkipped,
                    "MLErgodicityWorker: model {ModelId} ({Symbol}) skipped with {Samples}/{Required} samples.",
                    model.Id,
                    model.Symbol,
                    modelOutcomes?.Count ?? 0,
                    config.MinSamples);
                continue;
            }

            if (!TryComputeMetrics(modelOutcomes, config, out var metrics))
            {
                skipped++;
                _metrics?.MLErgodicityModelsSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "non_finite_metrics"));
                _logger.LogWarning(
                    "MLErgodicityWorker: model {ModelId} ({Symbol}) skipped because computed metrics were non-finite.",
                    model.Id,
                    model.Symbol);
                continue;
            }

            var log = new MLErgodicityLog
            {
                MLModelId = model.Id,
                Symbol = model.Symbol,
            };
            ApplyMetrics(log, model.Symbol, metrics, now);
            calculatedLogs.Add(log);

            _metrics?.MLErgodicityGap.Record(metrics.ErgodicityGap);
            _metrics?.MLErgodicityAdjustedKelly.Record(metrics.ErgodicityAdjustedKelly);
            _metrics?.MLErgodicityGrowthVariance.Record(metrics.GrowthRateVariance);
        }

        int logsPersisted = 0;
        if (calculatedLogs.Count > 0)
        {
            var modelIds = calculatedLogs.Select(l => l.MLModelId).ToArray();
            var dedupeCutoff = now.AddHours(-Math.Max(1, config.PollIntervalHours));
            var recentLogsByModel = await LoadRecentLogsByModelAsync(
                writeCtx,
                modelIds,
                dedupeCutoff,
                config.ModelBatchSize,
                ct);

            var logsToInsert = new List<MLErgodicityLog>(calculatedLogs.Count);
            int logsUpdated = 0;
            foreach (var calculatedLog in calculatedLogs)
            {
                if (recentLogsByModel.TryGetValue(calculatedLog.MLModelId, out var existingLog))
                {
                    CopyMetrics(existingLog, calculatedLog);
                    logsUpdated++;
                }
                else
                {
                    logsToInsert.Add(calculatedLog);
                }
            }

            var strategy = writeCtx.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async token =>
            {
                await using var tx = await writeCtx.Database.BeginTransactionAsync(token);
                if (logsToInsert.Count > 0)
                    writeCtx.Set<MLErgodicityLog>().AddRange(logsToInsert);

                await writeCtx.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }, ct);

            if (logsToInsert.Count > 0)
            {
                _metrics?.MLErgodicityLogsWritten.Add(
                    logsToInsert.Count,
                    new KeyValuePair<string, object?>("operation", "insert"));
            }

            if (logsUpdated > 0)
            {
                _metrics?.MLErgodicityLogsWritten.Add(
                    logsUpdated,
                    new KeyValuePair<string, object?>("operation", "update"));
            }

            logsPersisted = logsToInsert.Count + logsUpdated;
        }

        _metrics?.MLErgodicityModelsEvaluated.Add(calculatedLogs.Count);

        return new ErgodicityCycleResult(
            config.PollIntervalHours,
            activeModels.Count + skippedByLimit,
            calculatedLogs.Count,
            skipped,
            logsPersisted,
            null);
    }

    private static async Task<Dictionary<long, MLErgodicityLog>> LoadRecentLogsByModelAsync(
        DbContext writeCtx,
        IReadOnlyList<long> modelIds,
        DateTime dedupeCutoff,
        int batchSize,
        CancellationToken ct)
    {
        var recentLogsByModel = new Dictionary<long, MLErgodicityLog>();

        foreach (var batch in modelIds.Chunk(batchSize))
        {
            var batchIds = batch.ToArray();
            var rows = await writeCtx.Set<MLErgodicityLog>()
                .Where(l => batchIds.Contains(l.MLModelId)
                            && l.ComputedAt >= dedupeCutoff)
                .OrderByDescending(l => l.ComputedAt)
                .ThenByDescending(l => l.Id)
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                recentLogsByModel.TryAdd(row.MLModelId, row);
            }
        }

        return recentLogsByModel;
    }

    private static void ApplyMetrics(
        MLErgodicityLog log,
        string symbol,
        ErgodicityMetrics metrics,
        DateTime computedAt)
    {
        log.Symbol = symbol;
        log.EnsembleGrowthRate = ToMetricDecimal(metrics.EnsembleGrowthRate);
        log.TimeAverageGrowthRate = ToMetricDecimal(metrics.TimeAverageGrowthRate);
        log.ErgodicityGap = ToMetricDecimal(metrics.ErgodicityGap);
        log.NaiveKellyFraction = ToMetricDecimal(metrics.NaiveKellyFraction);
        log.ErgodicityAdjustedKelly = ToMetricDecimal(metrics.ErgodicityAdjustedKelly);
        log.GrowthRateVariance = ToMetricDecimal(metrics.GrowthRateVariance);
        log.ComputedAt = computedAt;
        log.IsDeleted = false;
    }

    private static void CopyMetrics(MLErgodicityLog target, MLErgodicityLog source)
    {
        target.Symbol = source.Symbol;
        target.EnsembleGrowthRate = source.EnsembleGrowthRate;
        target.TimeAverageGrowthRate = source.TimeAverageGrowthRate;
        target.ErgodicityGap = source.ErgodicityGap;
        target.NaiveKellyFraction = source.NaiveKellyFraction;
        target.ErgodicityAdjustedKelly = source.ErgodicityAdjustedKelly;
        target.GrowthRateVariance = source.GrowthRateVariance;
        target.ComputedAt = source.ComputedAt;
        target.IsDeleted = false;
    }

    private static async Task<List<PredictionOutcomeSnapshot>> LoadPredictionOutcomesAsync(
        DbContext readCtx,
        IReadOnlyList<long> modelIds,
        DateTime cutoff,
        MLErgodicityRuntimeConfig config,
        CancellationToken ct)
    {
        var outcomes = new List<PredictionOutcomeSnapshot>();

        foreach (var batch in modelIds.Chunk(config.ModelBatchSize))
        {
            var batchIds = batch.ToArray();
            var rows = await readCtx.Set<MLModelPredictionLog>()
                .AsNoTracking()
                .Where(l => batchIds.Contains(l.MLModelId)
                            && !l.IsDeleted
                            && l.ModelRole == ModelRole.Champion
                            && l.DirectionCorrect.HasValue
                            && l.OutcomeRecordedAt != null
                            && l.OutcomeRecordedAt >= cutoff)
                .OrderByDescending(l => l.OutcomeRecordedAt)
                .ThenByDescending(l => l.Id)
                .Select(l => new PredictionOutcomeSnapshot(
                    l.Id,
                    l.MLModelId,
                    l.Symbol,
                    l.PredictedDirection,
                    l.DirectionCorrect!.Value,
                    l.OutcomeRecordedAt!.Value,
                    l.ServedCalibratedProbability,
                    l.CalibratedProbability,
                    l.RawProbability,
                    l.DecisionThresholdUsed,
                    l.ConfidenceScore,
                    l.ActualMagnitudePips,
                    l.WasProfitable))
                .ToListAsync(ct);

            outcomes.AddRange(rows);
        }

        return outcomes;
    }

    private static bool TryComputeMetrics(
        IReadOnlyList<PredictionOutcomeSnapshot> outcomes,
        MLErgodicityRuntimeConfig config,
        out ErgodicityMetrics metrics)
    {
        var returns = new double[outcomes.Count];
        for (int i = 0; i < outcomes.Count; i++)
        {
            returns[i] = ResolveReturnProxy(outcomes[i], config);
            if (!double.IsFinite(returns[i]))
            {
                metrics = new ErgodicityMetrics(0, 0, 0, 0, 0, 0);
                return false;
            }
        }

        double mu = returns.Average();
        double timeAverage = returns.Average(v =>
            Math.Log(1.0 + Math.Clamp(v, -config.MaxReturnAbs, config.MaxReturnAbs)));
        double gap = mu - timeAverage;
        double variance = returns.Sum(v => (v - mu) * (v - mu)) / Math.Max(returns.Length - 1, 1);
        double safeVariance = Math.Max(variance, MinVariance);
        double naiveKelly = Math.Clamp(mu / safeVariance, -config.MaxKellyAbs, config.MaxKellyAbs);
        double ergodicityPenalty = Math.Clamp(gap / safeVariance, 0.0, 1.0);
        double adjustedKelly = Math.Clamp(
            naiveKelly * (1.0 - ergodicityPenalty),
            -config.MaxKellyAbs,
            config.MaxKellyAbs);

        metrics = new ErgodicityMetrics(mu, timeAverage, gap, naiveKelly, adjustedKelly, variance);
        return double.IsFinite(metrics.EnsembleGrowthRate)
               && double.IsFinite(metrics.TimeAverageGrowthRate)
               && double.IsFinite(metrics.ErgodicityGap)
               && double.IsFinite(metrics.NaiveKellyFraction)
               && double.IsFinite(metrics.ErgodicityAdjustedKelly)
               && double.IsFinite(metrics.GrowthRateVariance);
    }

    private static double ResolveReturnProxy(
        PredictionOutcomeSnapshot outcome,
        MLErgodicityRuntimeConfig config)
    {
        if (outcome.ActualMagnitudePips.HasValue)
        {
            double magnitude = Math.Abs((double)outcome.ActualMagnitudePips.Value);
            double sign = outcome.WasProfitable.HasValue
                ? (outcome.WasProfitable.Value ? 1.0 : -1.0)
                : (outcome.DirectionCorrect ? 1.0 : -1.0);
            double scaled = sign * magnitude / config.ReturnPipScale;
            if (double.IsFinite(scaled))
                return Math.Clamp(scaled, -config.MaxReturnAbs, config.MaxReturnAbs);
        }

        if (outcome.WasProfitable.HasValue)
        {
            double confidence = ResolveServedPredictionConfidence(outcome);
            double edge = Math.Clamp(confidence - 0.5, 0.0, config.MaxReturnAbs);
            return outcome.WasProfitable.Value ? edge : -edge;
        }

        double fallbackConfidence = ResolveServedPredictionConfidence(outcome);
        double fallbackEdge = Math.Clamp(fallbackConfidence - 0.5, 0.0, config.MaxReturnAbs);
        return outcome.DirectionCorrect ? fallbackEdge : -fallbackEdge;
    }

    private static double ResolveServedPredictionConfidence(PredictionOutcomeSnapshot outcome)
    {
        var proxyLog = new MLModelPredictionLog
        {
            PredictedDirection = outcome.PredictedDirection,
            ServedCalibratedProbability = outcome.ServedCalibratedProbability,
            CalibratedProbability = outcome.CalibratedProbability,
            RawProbability = outcome.RawProbability,
            DecisionThresholdUsed = outcome.DecisionThresholdUsed,
            ConfidenceScore = outcome.ConfidenceScore
        };

        double pBuy = MLFeatureHelper.ResolveLoggedServedBuyProbability(proxyLog);
        double confidence = outcome.PredictedDirection == TradeDirection.Buy
            ? pBuy
            : 1.0 - pBuy;

        if (double.IsFinite(confidence))
            return Math.Clamp(confidence, 0.0, 1.0);

        return Math.Clamp((double)outcome.ConfidenceScore, 0.0, 1.0);
    }

    private static decimal ToMetricDecimal(double value)
    {
        if (!double.IsFinite(value))
            return 0m;

        const double max = 99_999_999.99999999;
        const double min = -99_999_999.99999999;
        return (decimal)Math.Clamp(value, min, max);
    }

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLErgodicityCyclesSkipped.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason));

    private static TimeSpan CalculateBackoffDelay(int consecutiveFailures)
    {
        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var seconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
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
            // Some providers do not expose relational command timeout configuration.
        }
    }
}
