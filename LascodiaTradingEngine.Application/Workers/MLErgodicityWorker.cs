using System.Diagnostics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
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

    private readonly IServiceScopeFactory        _scopeFactory;
    private readonly ILogger<MLErgodicityWorker> _logger;
    private readonly IDistributedLock?           _distributedLock;
    private readonly TimeProvider                _timeProvider;
    private readonly IWorkerHealthMonitor?       _healthMonitor;
    private readonly TradingMetrics?             _metrics;
    private readonly MLErgodicityOptions         _options;
    private readonly MLErgodicityConfigReader    _configReader;

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

    private sealed record ErgodicityCycleResult(
        int ActiveModelCount,
        int EvaluatedModelCount,
        int SkippedModelCount,
        int LogsWritten);

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
        _logger.LogInformation("MLErgodicityWorker started.");
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Computes model ergodicity economics and Kelly sizing diagnostics.",
            TimeSpan.FromHours(_options.PollIntervalHours));

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollHours = _options.PollIntervalHours;
            var cycleStart = Stopwatch.GetTimestamp();

            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                pollHours = await RunCycleAsync(stoppingToken);
                _healthMonitor?.RecordCycleSuccess(
                    WorkerName,
                    (long)Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName));
                _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                _logger.LogError(ex, "MLErgodicityWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromHours(pollHours), stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        _logger.LogInformation("MLErgodicityWorker stopping.");
    }

    internal async Task<int> RunCycleAsync(CancellationToken ct)
    {
        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is not null)
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(_options.LockTimeoutSeconds),
                ct);
            if (cycleLock is null)
            {
                _metrics?.MLErgodicityLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _logger.LogDebug(
                    EventIds.LockSkipped,
                    "MLErgodicityWorker: cycle skipped because distributed lock is held elsewhere.");
                return _options.PollIntervalHours;
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
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readCtx = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                var config = await _configReader.LoadAsync(readCtx, ct);
                var result = await RunErgodicityAsync(readCtx, writeCtx, config, ct);

                _logger.LogInformation(
                    EventIds.CycleCompleted,
                    "MLErgodicityWorker: evaluated {Evaluated}/{Active} active models, skipped {Skipped}, wrote {Logs} logs.",
                    result.EvaluatedModelCount,
                    result.ActiveModelCount,
                    result.SkippedModelCount,
                    result.LogsWritten);

                return config.PollIntervalHours;
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

        var activeModels = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer
                        && !m.IsSuppressed)
            .OrderBy(m => m.Id)
            .Take(config.MaxCycleModels)
            .Select(m => new ActiveModelSnapshot(m.Id, m.Symbol))
            .ToListAsync(ct);

        if (activeModels.Count == 0)
            return new ErgodicityCycleResult(0, 0, 0, 0);

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

        var logsToWrite = new List<MLErgodicityLog>(activeModels.Count);
        int skipped = 0;

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

            logsToWrite.Add(new MLErgodicityLog
            {
                MLModelId = model.Id,
                Symbol = model.Symbol,
                EnsembleGrowthRate = ToMetricDecimal(metrics.EnsembleGrowthRate),
                TimeAverageGrowthRate = ToMetricDecimal(metrics.TimeAverageGrowthRate),
                ErgodicityGap = ToMetricDecimal(metrics.ErgodicityGap),
                NaiveKellyFraction = ToMetricDecimal(metrics.NaiveKellyFraction),
                ErgodicityAdjustedKelly = ToMetricDecimal(metrics.ErgodicityAdjustedKelly),
                GrowthRateVariance = ToMetricDecimal(metrics.GrowthRateVariance),
                ComputedAt = now,
            });

            _metrics?.MLErgodicityGap.Record(metrics.ErgodicityGap);
            _metrics?.MLErgodicityAdjustedKelly.Record(metrics.ErgodicityAdjustedKelly);
            _metrics?.MLErgodicityGrowthVariance.Record(metrics.GrowthRateVariance);
        }

        if (logsToWrite.Count > 0)
        {
            var strategy = writeCtx.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async token =>
            {
                await using var tx = await writeCtx.Database.BeginTransactionAsync(token);
                writeCtx.Set<MLErgodicityLog>().AddRange(logsToWrite);
                await writeCtx.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }, ct);

            _metrics?.MLErgodicityLogsWritten.Add(logsToWrite.Count);
        }

        _metrics?.MLErgodicityModelsEvaluated.Add(logsToWrite.Count);

        return new ErgodicityCycleResult(
            activeModels.Count,
            logsToWrite.Count,
            skipped,
            logsToWrite.Count);
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
        }

        double mu = returns.Average();
        double timeAverage = returns.Average(v => Math.Log(1.0 + Math.Max(v, -0.999999)));
        double gap = mu - timeAverage;
        double variance = returns.Sum(v => (v - mu) * (v - mu)) / Math.Max(returns.Length - 1, 1);
        double safeVariance = Math.Max(variance, MinVariance);
        double naiveKelly = Math.Clamp(mu / safeVariance, -config.MaxKellyAbs, config.MaxKellyAbs);
        double adjustedKelly = Math.Clamp(
            naiveKelly * (1.0 - gap / safeVariance),
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
            double scaled = (double)outcome.ActualMagnitudePips.Value / config.ReturnPipScale;
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
}
