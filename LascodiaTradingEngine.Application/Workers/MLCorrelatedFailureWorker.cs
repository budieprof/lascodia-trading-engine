using System.Text.Json;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects correlated failure across active ML models and activates a system-wide training
/// pause when a significant fraction of models degrade simultaneously.
///
/// <para>
/// Correlated model failure is a strong signal that a systemic market structure shift has
/// occurred (e.g. central bank intervention, liquidity shock, regime change) rather than
/// isolated per-symbol degradation. In such scenarios, retraining individual models is
/// wasteful because they will immediately degrade again on the shifted data.
/// </para>
///
/// <para>
/// Every poll cycle the worker:
/// <list type="number">
///   <item>Loads all active <see cref="MLModel"/> records.</item>
///   <item>Computes rolling direction accuracy for each model from its
///         <see cref="MLModelPredictionLog"/> records within the drift window.</item>
///   <item>Classifies a model as "failing" when it has enough resolved predictions and its
///         accuracy falls below <c>MLTraining:DriftAccuracyThreshold</c>.</item>
///   <item>If the failure ratio exceeds <c>MLCorrelated:AlarmRatio</c>, activates
///         <c>MLTraining:SystemicPauseActive</c> and creates an
///         <see cref="MLCorrelatedFailureLog"/> record plus an <see cref="Alert"/>.</item>
///   <item>If the failure ratio drops below <c>MLCorrelated:RecoveryRatio</c> while a pause
///         is active, lifts the pause and logs the recovery.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MLCorrelatedFailureWorker : BackgroundService
{
    private const string WorkerName = nameof(MLCorrelatedFailureWorker);
    private const string DistributedLockKey = "ml:correlated-failure:cycle";

    private const string CK_SystemicPause      = "MLTraining:SystemicPauseActive";

    private const int AlertPayloadSchemaVersion = 1;

    private readonly IServiceScopeFactory                  _scopeFactory;
    private readonly ILogger<MLCorrelatedFailureWorker>    _logger;
    private readonly IDistributedLock?                     _distributedLock;
    private readonly TimeProvider                          _timeProvider;
    private readonly IWorkerHealthMonitor?                 _healthMonitor;
    private readonly TradingMetrics?                       _metrics;
    private readonly MLCorrelatedFailureOptions            _options;
    private readonly MLCorrelatedFailureConfigReader       _configReader;

    private static class EventIds
    {
        public static readonly EventId PauseActivated = new(4101, nameof(PauseActivated));
        public static readonly EventId PauseRecovered = new(4102, nameof(PauseRecovered));
        public static readonly EventId LockSkipped = new(4103, nameof(LockSkipped));
        public static readonly EventId StateChangeCooldown = new(4104, nameof(StateChangeCooldown));
    }

    private sealed record ActiveModelSnapshot(long Id, string Symbol, Timeframe Timeframe);

    private sealed record ModelPredictionStats(
        long MLModelId,
        int DirectionTotal,
        int CorrectCount,
        int ProfitTotal,
        int ProfitableCount);

    private sealed record FailingModelSnapshot(
        long ModelId,
        string Symbol,
        Timeframe Timeframe,
        int PredictionCount,
        double Accuracy);

    private sealed record CorrelatedFailureEvaluation(
        int ActiveModelCount,
        int EvaluatedModelCount,
        int FailingModelCount,
        int ModelsWithoutPredictions,
        int ModelsBelowMinPredictions,
        double FailureRatio,
        IReadOnlyList<string> AffectedSymbols,
        IReadOnlyList<FailingModelSnapshot> FailingModels);

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per poll cycle, giving each iteration fresh
    /// <see cref="IReadApplicationDbContext"/> / <see cref="IWriteApplicationDbContext"/>
    /// instances and preventing long-lived DbContext connection leaks.
    /// </param>
    /// <param name="logger">Structured logger for correlated failure events.</param>
    public MLCorrelatedFailureWorker(
        IServiceScopeFactory                scopeFactory,
        ILogger<MLCorrelatedFailureWorker>  logger,
        IDistributedLock?                   distributedLock = null,
        TimeProvider?                       timeProvider = null,
        IWorkerHealthMonitor?               healthMonitor = null,
        TradingMetrics?                     metrics = null,
        MLCorrelatedFailureOptions?         options = null,
        MLCorrelatedFailureConfigReader?    configReader = null)
    {
        _scopeFactory    = scopeFactory;
        _logger          = logger;
        _distributedLock = distributedLock;
        _timeProvider    = timeProvider ?? TimeProvider.System;
        _healthMonitor   = healthMonitor;
        _metrics         = metrics;
        _options         = options ?? new MLCorrelatedFailureOptions();
        _configReader    = configReader ?? new MLCorrelatedFailureConfigReader(_options);
    }

    /// <summary>
    /// Main background loop. Runs indefinitely at <c>MLCorrelated:PollIntervalSeconds</c>
    /// intervals (default 600 s / 10 min) until the host requests shutdown.
    ///
    /// Each cycle acquires the <see cref="WorkerBulkhead.MLMonitoring"/> semaphore to avoid
    /// connection-pool exhaustion from concurrent ML monitoring workers, then evaluates all
    /// active models for correlated failure.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLCorrelatedFailureWorker started.");
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Detects simultaneous live ML model degradation and toggles systemic training pause.",
            TimeSpan.FromSeconds(_options.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = _options.PollIntervalSeconds; // default 10 min
            var cycleStart = Stopwatch.GetTimestamp();

            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                pollSecs = await RunCycleAsync(stoppingToken);
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
                _logger.LogError(ex, "MLCorrelatedFailureWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        _logger.LogInformation("MLCorrelatedFailureWorker stopping.");
    }

    internal async Task<int> RunCycleAsync(CancellationToken ct)
    {
        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is not null)
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(5),
                ct);
            if (cycleLock is null)
            {
                _metrics?.MLCorrelatedFailureLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _logger.LogDebug(EventIds.LockSkipped, "MLCorrelatedFailureWorker: cycle skipped because distributed lock is held elsewhere.");
                return _options.PollIntervalSeconds;
            }

            _metrics?.MLCorrelatedFailureLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));
        }
        else
        {
            _metrics?.MLCorrelatedFailureLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var readCtx  = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                var config = await _configReader.LoadAsync(readCtx, ct);

                await EvaluateCorrelatedFailureAsync(
                    readCtx,
                    writeCtx,
                    config,
                    ct);

                return config.PollSeconds;
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    // ── Core evaluation logic ──────────────────────────────────────────────────

    /// <summary>
    /// Evaluates all active ML models for correlated failure by computing per-model rolling
    /// accuracy in a single batch query, then comparing the failure ratio against the
    /// configured alarm and recovery thresholds.
    /// </summary>
    /// <param name="readCtx">EF read context for SELECT queries.</param>
    /// <param name="writeCtx">EF write context for EngineConfig updates and log inserts.</param>
    /// <param name="alarmRatio">Fraction of failing models that triggers the systemic pause.</param>
    /// <param name="recoveryRatio">Fraction below which the systemic pause is lifted.</param>
    /// <param name="accThreshold">Accuracy below which a model is considered failing.</param>
    /// <param name="windowDays">Rolling window (days) for prediction accuracy evaluation.</param>
    /// <param name="minPredictions">Minimum resolved predictions required to classify a model.</param>
    /// <param name="stateChangeCooldownMinutes">Minimum minutes between pause state changes.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EvaluateCorrelatedFailureAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        MLCorrelatedFailureRuntimeConfig        config,
        CancellationToken                       ct)
    {
        // ── 1. Load all active models ──────────────────────────────────────────
        var activeModelRows = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer
                        && !m.IsSuppressed)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);
        var activeModels = activeModelRows
            .Select(m => new ActiveModelSnapshot(m.Id, m.Symbol, m.Timeframe))
            .ToList();

        if (activeModels.Count == 0)
        {
            _logger.LogDebug("MLCorrelatedFailureWorker: no active models — skipping cycle.");
            return;
        }

        // ── 2. Batch-fetch prediction accuracy per model ───────────────────────
        // Grouped batch queries avoid N+1 while keeping SQL parameter lists bounded.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var windowStart = now.AddDays(-config.WindowDays);
        var modelIds    = activeModels.Select(m => m.Id).ToList();

        var predictionStats = await LoadPredictionStatsAsync(
            readCtx,
            modelIds,
            windowStart,
            config.ModelStatsBatchSize,
            ct);

        var evaluation = EvaluateModelStats(
            activeModels,
            predictionStats,
            config.MinPredictions,
            config.FailureThreshold,
            config.FailureMetric);

        _metrics?.MLCorrelatedFailureModelsEvaluated.Add(evaluation.EvaluatedModelCount);
        _metrics?.MLCorrelatedFailureModelsFailing.Add(evaluation.FailingModelCount);
        _metrics?.MLCorrelatedFailureAffectedSymbols.Record(evaluation.AffectedSymbols.Count);
        _metrics?.MLCorrelatedFailureRatio.Record(evaluation.FailureRatio);
        RecordSkippedModels(evaluation);

        if (evaluation.EvaluatedModelCount == 0)
        {
            _logger.LogDebug(
                "MLCorrelatedFailureWorker: no models have >= {Min} predictions in window — skipping.",
                config.MinPredictions);
            return;
        }

        // ── Guard: require a minimum sample of evaluated models ────────────
        // Without this, a single degenerate model (1/1 = 100% failure ratio)
        // can trigger systemic pause and create a deadlock: pause blocks
        // training → no new models accumulate predictions → ratio stays 100%
        // → pause never lifts. Requiring ≥ N evaluated models ensures the
        // alarm only fires on genuinely correlated failure, not sampling noise.
        // Observed 2026-04-15: GBPUSD/M15 (model 26, 13% accuracy) was the
        // sole evaluated model, triggering 100% ratio and blocking all training
        // for the entire queue of 23 runs.
        if (evaluation.EvaluatedModelCount < config.MinModelsForAlarm)
        {
            _logger.LogInformation(
                "MLCorrelatedFailureWorker: only {Evaluated}/{Required} models evaluated " +
                "(need {Required} for alarm). Skipping alarm check. Failing: {Failing} ({FailSymbols}).",
                evaluation.EvaluatedModelCount, config.MinModelsForAlarm, config.MinModelsForAlarm,
                evaluation.FailingModelCount, string.Join(", ", evaluation.AffectedSymbols));
            return;
        }

        int    failingCount  = evaluation.FailingModelCount;
        double failureRatio  = evaluation.FailureRatio;

        _logger.LogDebug(
            "MLCorrelatedFailureWorker: {Failing}/{Total} models failing (ratio={Ratio:P1}, alarm={Alarm:P1}, recovery={Recovery:P1}).",
            failingCount, evaluation.EvaluatedModelCount, failureRatio, config.AlarmRatio, config.RecoveryRatio);

        // ── 4. Read current pause state ────────────────────────────────────────
        bool currentlyPaused = await GetConfigAsync(writeCtx, CK_SystemicPause, false, ct);

        // ── 5. Alarm: activate systemic pause ──────────────────────────────────
        if (failureRatio >= config.AlarmRatio)
        {
            if (!currentlyPaused)
            {
                if (await HasRecentStateChangeAsync(writeCtx, now, config.StateChangeCooldownMinutes, ct))
                {
                    _metrics?.MLCorrelatedFailureCooldownSkips.Add(
                        1,
                        new KeyValuePair<string, object?>("transition", "activate"));
                    _logger.LogInformation(
                        EventIds.StateChangeCooldown,
                        "MLCorrelatedFailureWorker: activation skipped because state-change cooldown is active.");
                    return;
                }

                var strategy = writeCtx.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async token =>
                {
                    await using var tx = await writeCtx.Database.BeginTransactionAsync(token);
                    bool pauseUnderLock = await GetConfigAsync(writeCtx, CK_SystemicPause, false, token);
                    if (pauseUnderLock)
                    {
                        await tx.CommitAsync(token);
                        return;
                    }

                    await UpsertConfigAsync(writeCtx, CK_SystemicPause, "true", token);

                    var symbolsJson = JsonSerializer.Serialize(evaluation.AffectedSymbols);
                    var failureDetailsJson = CreateFailureDetailsJson(evaluation);

                    writeCtx.Set<MLCorrelatedFailureLog>().Add(new MLCorrelatedFailureLog
                    {
                        DetectedAt          = now,
                        FailingModelCount   = failingCount,
                        TotalModelCount     = evaluation.EvaluatedModelCount,
                        ActiveModelCount    = evaluation.ActiveModelCount,
                        EvaluatedModelCount = evaluation.EvaluatedModelCount,
                        FailureRatio        = failureRatio,
                        SymbolsAffectedJson = symbolsJson,
                        FailureDetailsJson  = failureDetailsJson,
                        PauseActivated      = true,
                    });

                    writeCtx.Set<Alert>().Add(new Alert
                    {
                        AlertType      = AlertType.SystemicMLDegradation,
                        Severity       = AlertSeverity.High,
                        Symbol         = "SYSTEM",
                        DeduplicationKey = "MLCorrelatedFailure:SystemicPause:Global",
                        CooldownSeconds = 3600,
                        ConditionJson  = JsonSerializer.Serialize(new
                        {
                            SchemaVersion = AlertPayloadSchemaVersion,
                            Message  = $"Systemic ML degradation detected: {failingCount}/{evaluation.EvaluatedModelCount} models failing ({failureRatio:P1}). Training pause activated.",
                            Symbols  = evaluation.AffectedSymbols,
                            FailingModels = evaluation.FailingModels.Select(m => new
                            {
                                m.ModelId,
                                m.Symbol,
                                Timeframe = m.Timeframe.ToString(),
                                m.PredictionCount,
                                Accuracy = m.Accuracy
                            }).ToArray(),
                            Ratio    = failureRatio,
                            AlarmRatio = config.AlarmRatio,
                            FailureMetric = config.FailureMetric.ToString(),
                            EvaluatedModels = evaluation.EvaluatedModelCount,
                            ActiveModels = evaluation.ActiveModelCount,
                            Severity = "high",
                        }),
                        IsActive = true,
                    });

                    await writeCtx.SaveChangesAsync(token);
                    await tx.CommitAsync(token);
                }, ct);

                _metrics?.MLCorrelatedFailurePauseActivations.Add(1);
                _logger.LogWarning(
                    EventIds.PauseActivated,
                    "Systemic ML degradation: {Failing}/{Total} models failing ({Ratio:P1} >= alarm {Alarm:P1}). " +
                    "Training pause ACTIVATED. Affected symbols: {Symbols}.",
                    failingCount, evaluation.EvaluatedModelCount, failureRatio, config.AlarmRatio,
                    string.Join(", ", evaluation.AffectedSymbols));
            }
            else
            {
                _logger.LogDebug(
                    "MLCorrelatedFailureWorker: systemic pause still active ({Failing}/{Total} failing, ratio={Ratio:P1}).",
                    failingCount, evaluation.EvaluatedModelCount, failureRatio);
            }

            return;
        }

        // ── 6. Recovery: lift systemic pause ───────────────────────────────────
        if (failureRatio < config.RecoveryRatio && currentlyPaused)
        {
            if (await HasRecentStateChangeAsync(writeCtx, now, config.StateChangeCooldownMinutes, ct))
            {
                _metrics?.MLCorrelatedFailureCooldownSkips.Add(
                    1,
                    new KeyValuePair<string, object?>("transition", "recover"));
                _logger.LogInformation(
                    EventIds.StateChangeCooldown,
                    "MLCorrelatedFailureWorker: recovery skipped because state-change cooldown is active.");
                return;
            }

            var strategy = writeCtx.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async token =>
            {
                await using var tx = await writeCtx.Database.BeginTransactionAsync(token);
                bool pauseUnderLock = await GetConfigAsync(writeCtx, CK_SystemicPause, false, token);
                if (!pauseUnderLock)
                {
                    await tx.CommitAsync(token);
                    return;
                }

                await UpsertConfigAsync(writeCtx, CK_SystemicPause, "false", token);

                writeCtx.Set<MLCorrelatedFailureLog>().Add(new MLCorrelatedFailureLog
                {
                    DetectedAt          = now,
                    FailingModelCount   = failingCount,
                    TotalModelCount     = evaluation.EvaluatedModelCount,
                    ActiveModelCount    = evaluation.ActiveModelCount,
                    EvaluatedModelCount = evaluation.EvaluatedModelCount,
                    FailureRatio        = failureRatio,
                    SymbolsAffectedJson = JsonSerializer.Serialize(evaluation.AffectedSymbols),
                    FailureDetailsJson  = CreateFailureDetailsJson(evaluation),
                    PauseActivated      = false,
                });

                await writeCtx.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }, ct);

            _metrics?.MLCorrelatedFailurePauseRecoveries.Add(1);
            _logger.LogInformation(
                EventIds.PauseRecovered,
                "Systemic ML recovery: failure ratio {Ratio:P1} < recovery threshold {Recovery:P1}. " +
                "Training pause LIFTED.",
                failureRatio, config.RecoveryRatio);
        }
    }

    private static async Task<List<ModelPredictionStats>> LoadPredictionStatsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        IReadOnlyList<long> modelIds,
        DateTime windowStart,
        int batchSize,
        CancellationToken ct)
    {
        var predictionStats = new List<ModelPredictionStats>();

        foreach (var batch in modelIds.Chunk(batchSize))
        {
            var batchIds = batch.ToArray();
            var rows = await readCtx.Set<MLModelPredictionLog>()
                .Where(l => batchIds.Contains(l.MLModelId) &&
                            !l.IsDeleted                   &&
                            ((l.DirectionCorrect != null) || (l.WasProfitable != null)) &&
                            ((l.OutcomeRecordedAt != null && l.OutcomeRecordedAt >= windowStart)
                             || (l.OutcomeRecordedAt == null && l.PredictedAt >= windowStart)))
                .GroupBy(l => l.MLModelId)
                .Select(g => new
                {
                    MLModelId = g.Key,
                    DirectionTotal = g.Count(l => l.DirectionCorrect != null),
                    CorrectCount = g.Count(l => l.DirectionCorrect == true),
                    ProfitTotal = g.Count(l => l.WasProfitable != null),
                    ProfitableCount = g.Count(l => l.WasProfitable == true)
                })
                .ToListAsync(ct);

            predictionStats.AddRange(rows.Select(s => new ModelPredictionStats(
                s.MLModelId,
                s.DirectionTotal,
                s.CorrectCount,
                s.ProfitTotal,
                s.ProfitableCount)));
        }

        return predictionStats;
    }

    private static CorrelatedFailureEvaluation EvaluateModelStats(
        IReadOnlyCollection<ActiveModelSnapshot> activeModels,
        IReadOnlyCollection<ModelPredictionStats> predictionStats,
        int minPredictions,
        double failureThreshold,
        MLCorrelatedFailureMetric failureMetric)
    {
        var statsLookup = predictionStats.ToDictionary(s => s.MLModelId);
        var affectedSymbols = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var failingModels = new List<FailingModelSnapshot>();
        int evaluatedModelCount = 0;
        int modelsWithoutPredictions = 0;
        int modelsBelowMinPredictions = 0;

        foreach (var model in activeModels)
        {
            if (!statsLookup.TryGetValue(model.Id, out var stats))
            {
                modelsWithoutPredictions++;
                continue;
            }

            int sampleCount = GetMetricSampleCount(stats, failureMetric);
            if (sampleCount < minPredictions)
            {
                modelsBelowMinPredictions++;
                continue;
            }

            evaluatedModelCount++;

            double healthScore = GetMetricHealthScore(stats, failureMetric);
            if (healthScore >= failureThreshold)
                continue;

            failingModels.Add(new FailingModelSnapshot(
                model.Id,
                model.Symbol,
                model.Timeframe,
                sampleCount,
                healthScore));
            affectedSymbols.Add(model.Symbol);
        }

        double failureRatio = evaluatedModelCount == 0
            ? 0.0
            : (double)failingModels.Count / evaluatedModelCount;

        return new CorrelatedFailureEvaluation(
            activeModels.Count,
            evaluatedModelCount,
            failingModels.Count,
            modelsWithoutPredictions,
            modelsBelowMinPredictions,
            failureRatio,
            affectedSymbols.ToArray(),
            failingModels);
    }

    private static int GetMetricSampleCount(
        ModelPredictionStats stats,
        MLCorrelatedFailureMetric failureMetric)
        => failureMetric switch
        {
            MLCorrelatedFailureMetric.Profitability => stats.ProfitTotal,
            MLCorrelatedFailureMetric.Composite => Math.Min(stats.DirectionTotal, stats.ProfitTotal),
            _ => stats.DirectionTotal
        };

    private static double GetMetricHealthScore(
        ModelPredictionStats stats,
        MLCorrelatedFailureMetric failureMetric)
    {
        double directionAccuracy = stats.DirectionTotal == 0
            ? 0.0
            : (double)stats.CorrectCount / stats.DirectionTotal;
        double profitability = stats.ProfitTotal == 0
            ? 0.0
            : (double)stats.ProfitableCount / stats.ProfitTotal;

        return failureMetric switch
        {
            MLCorrelatedFailureMetric.Profitability => profitability,
            MLCorrelatedFailureMetric.Composite => (directionAccuracy + profitability) / 2.0,
            _ => directionAccuracy
        };
    }

    private void RecordSkippedModels(CorrelatedFailureEvaluation evaluation)
    {
        if (_metrics is null)
            return;

        if (evaluation.ModelsWithoutPredictions > 0)
        {
            _metrics.MLCorrelatedFailureModelsSkipped.Add(
                evaluation.ModelsWithoutPredictions,
                new KeyValuePair<string, object?>("reason", "no_predictions"));
        }

        if (evaluation.ModelsBelowMinPredictions > 0)
        {
            _metrics.MLCorrelatedFailureModelsSkipped.Add(
                evaluation.ModelsBelowMinPredictions,
                new KeyValuePair<string, object?>("reason", "below_min_predictions"));
        }
    }

    private async Task<bool> HasRecentStateChangeAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        DateTime nowUtc,
        int cooldownMinutes,
        CancellationToken ct)
    {
        if (cooldownMinutes <= 0)
            return false;

        var cutoff = nowUtc.AddMinutes(-cooldownMinutes);
        return await ctx.Set<MLCorrelatedFailureLog>()
            .AsNoTracking()
            .AnyAsync(l => l.DetectedAt >= cutoff, ct);
    }

    private static string CreateFailureDetailsJson(CorrelatedFailureEvaluation evaluation)
        => JsonSerializer.Serialize(new
        {
            SchemaVersion = AlertPayloadSchemaVersion,
            evaluation.ActiveModelCount,
            evaluation.EvaluatedModelCount,
            evaluation.FailingModelCount,
            evaluation.ModelsWithoutPredictions,
            evaluation.ModelsBelowMinPredictions,
            evaluation.FailureRatio,
            AffectedSymbols = evaluation.AffectedSymbols,
            FailingModels = evaluation.FailingModels.Select(m => new
            {
                m.ModelId,
                m.Symbol,
                Timeframe = m.Timeframe.ToString(),
                m.PredictionCount,
                Accuracy = m.Accuracy
            }).ToArray()
        });

    // ── Config helpers ─────────────────────────────────────────────────────────

    private static async Task<bool> GetConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        bool                                    defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key && !c.IsDeleted, ct);

        if (entry?.Value is null) return defaultValue;

        return bool.TryParse(entry.Value, out var parsed)
            ? parsed
            : defaultValue;
    }

    /// <summary>
    /// Creates or updates an <see cref="EngineConfig"/> entry. If the key already exists,
    /// updates its value and <see cref="EngineConfig.LastUpdatedAt"/> timestamp. Otherwise
    /// creates a new boolean record with hot-reload enabled. Soft-deleted rows are revived
    /// because <see cref="EngineConfig.Key"/> is unique across the table.
    /// </summary>
    /// <param name="writeCtx">EF write context — must be a tracked context.</param>
    /// <param name="key">The configuration key to upsert.</param>
    /// <param name="value">The new string value.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        var entry = await writeCtx.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry is null)
        {
            writeCtx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key = key,
                Value = value,
                DataType = ConfigDataType.Bool,
                IsHotReloadable = true,
                LastUpdatedAt = _timeProvider.GetUtcNow().UtcDateTime
            });
            return;
        }

        entry.Value = value;
        entry.DataType = ConfigDataType.Bool;
        entry.IsHotReloadable = true;
        entry.IsDeleted = false;
        entry.LastUpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
    }
}
