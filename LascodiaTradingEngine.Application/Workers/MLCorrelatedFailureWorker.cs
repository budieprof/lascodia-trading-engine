using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
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
    // ── Config keys ────────────────────────────────────────────────────────────
    private const string CK_PollSecs           = "MLCorrelated:PollIntervalSeconds";
    private const string CK_AlarmRatio         = "MLCorrelated:AlarmRatio";
    private const string CK_RecoveryRatio      = "MLCorrelated:RecoveryRatio";
    private const string CK_MinModelsForAlarm  = "MLCorrelated:MinModelsForAlarm";
    private const string CK_AccThreshold       = "MLTraining:DriftAccuracyThreshold";
    private const string CK_WindowDays         = "MLTraining:DriftWindowDays";
    private const string CK_MinPredictions     = "MLTraining:DriftMinPredictions";
    private const string CK_SystemicPause      = "MLTraining:SystemicPauseActive";

    private readonly IServiceScopeFactory                  _scopeFactory;
    private readonly ILogger<MLCorrelatedFailureWorker>    _logger;

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
        ILogger<MLCorrelatedFailureWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
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

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 600; // default 10 min

            try
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(stoppingToken);
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                    var readCtx  = readDb.GetDbContext();
                    var writeCtx = writeDb.GetDbContext();

                    pollSecs = await GetConfigAsync<int>   (readCtx, CK_PollSecs,       600,  stoppingToken);
                    double alarmRatio     = await GetConfigAsync<double>(readCtx, CK_AlarmRatio,     0.40, stoppingToken);
                    double recoveryRatio  = await GetConfigAsync<double>(readCtx, CK_RecoveryRatio,  0.20, stoppingToken);
                    int    minModels      = await GetConfigAsync<int>   (readCtx, CK_MinModelsForAlarm, 3, stoppingToken);
                    double accThreshold   = await GetConfigAsync<double>(readCtx, CK_AccThreshold,   0.50, stoppingToken);
                    int    windowDays     = await GetConfigAsync<int>   (readCtx, CK_WindowDays,     14,   stoppingToken);
                    int    minPredictions = await GetConfigAsync<int>   (readCtx, CK_MinPredictions,  30,   stoppingToken);

                    await EvaluateCorrelatedFailureAsync(
                        readCtx, writeCtx,
                        alarmRatio, recoveryRatio, minModels, accThreshold, windowDays, minPredictions,
                        stoppingToken);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLCorrelatedFailureWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLCorrelatedFailureWorker stopping.");
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
    /// <param name="ct">Cancellation token.</param>
    private async Task EvaluateCorrelatedFailureAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        double                                  alarmRatio,
        double                                  recoveryRatio,
        int                                     minModelsForAlarm,
        double                                  accThreshold,
        int                                     windowDays,
        int                                     minPredictions,
        CancellationToken                       ct)
    {
        // ── 1. Load all active models ──────────────────────────────────────────
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol })
            .ToListAsync(ct);

        if (activeModels.Count == 0)
        {
            _logger.LogDebug("MLCorrelatedFailureWorker: no active models — skipping cycle.");
            return;
        }

        // ── 2. Batch-fetch prediction accuracy per model ───────────────────────
        // Single query with GroupBy to avoid N+1.
        var windowStart = DateTime.UtcNow.AddDays(-windowDays);
        var modelIds    = activeModels.Select(m => m.Id).ToList();

        var predictionStats = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => modelIds.Contains(l.MLModelId) &&
                        !l.IsDeleted                   &&
                        l.DirectionCorrect != null     &&
                        l.PredictedAt >= windowStart)
            .GroupBy(l => l.MLModelId)
            .Select(g => new
            {
                MLModelId    = g.Key,
                Total        = g.Count(),
                CorrectCount = g.Count(l => l.DirectionCorrect == true),
            })
            .ToListAsync(ct);

        var statsLookup = predictionStats.ToDictionary(s => s.MLModelId);

        // ── 3. Classify models ─────────────────────────────────────────────────
        var failingSymbols = new List<string>();
        int modelsWithEnoughPredictions = 0;

        foreach (var model in activeModels)
        {
            if (!statsLookup.TryGetValue(model.Id, out var stats))
                continue; // No predictions at all — skip

            if (stats.Total < minPredictions)
                continue; // Not enough data to classify

            modelsWithEnoughPredictions++;

            double accuracy = (double)stats.CorrectCount / stats.Total;
            if (accuracy < accThreshold)
                failingSymbols.Add(model.Symbol);
        }

        if (modelsWithEnoughPredictions == 0)
        {
            _logger.LogDebug(
                "MLCorrelatedFailureWorker: no models have >= {Min} predictions in window — skipping.",
                minPredictions);
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
        if (modelsWithEnoughPredictions < minModelsForAlarm)
        {
            _logger.LogInformation(
                "MLCorrelatedFailureWorker: only {Evaluated}/{Required} models evaluated " +
                "(need {Required} for alarm). Skipping alarm check. Failing: {Failing} ({FailSymbols}).",
                modelsWithEnoughPredictions, minModelsForAlarm, minModelsForAlarm,
                failingSymbols.Count, string.Join(", ", failingSymbols));
            return;
        }

        int    failingCount  = failingSymbols.Count;
        double failureRatio  = (double)failingCount / modelsWithEnoughPredictions;

        _logger.LogDebug(
            "MLCorrelatedFailureWorker: {Failing}/{Total} models failing (ratio={Ratio:P1}, alarm={Alarm:P1}, recovery={Recovery:P1}).",
            failingCount, modelsWithEnoughPredictions, failureRatio, alarmRatio, recoveryRatio);

        // ── 4. Read current pause state ────────────────────────────────────────
        bool currentlyPaused = await GetConfigAsync<bool>(readCtx, CK_SystemicPause, false, ct);

        // ── 5. Alarm: activate systemic pause ──────────────────────────────────
        if (failureRatio >= alarmRatio)
        {
            if (!currentlyPaused)
            {
                await UpsertConfigAsync(writeCtx, CK_SystemicPause, "true", ct);

                var symbolsJson = JsonSerializer.Serialize(failingSymbols);

                writeCtx.Set<MLCorrelatedFailureLog>().Add(new MLCorrelatedFailureLog
                {
                    DetectedAt          = DateTime.UtcNow,
                    FailingModelCount   = failingCount,
                    TotalModelCount     = modelsWithEnoughPredictions,
                    FailureRatio        = failureRatio,
                    SymbolsAffectedJson = symbolsJson,
                    PauseActivated      = true,
                });

                writeCtx.Set<Alert>().Add(new Alert
                {
                    AlertType      = AlertType.SystemicMLDegradation,
                    ConditionJson  = JsonSerializer.Serialize(new
                    {
                        Message  = $"Systemic ML degradation detected: {failingCount}/{modelsWithEnoughPredictions} models failing ({failureRatio:P1}). Training pause activated.",
                        Symbols  = failingSymbols,
                        Ratio    = failureRatio,
                        Severity = "info",
                    }),
                    IsActive = true,
                });

                await writeCtx.SaveChangesAsync(ct);

                _logger.LogWarning(
                    "Systemic ML degradation: {Failing}/{Total} models failing ({Ratio:P1} >= alarm {Alarm:P1}). " +
                    "Training pause ACTIVATED. Affected symbols: {Symbols}.",
                    failingCount, modelsWithEnoughPredictions, failureRatio, alarmRatio,
                    string.Join(", ", failingSymbols));
            }
            else
            {
                _logger.LogDebug(
                    "MLCorrelatedFailureWorker: systemic pause still active ({Failing}/{Total} failing, ratio={Ratio:P1}).",
                    failingCount, modelsWithEnoughPredictions, failureRatio);
            }

            return;
        }

        // ── 6. Recovery: lift systemic pause ───────────────────────────────────
        if (failureRatio < recoveryRatio && currentlyPaused)
        {
            await UpsertConfigAsync(writeCtx, CK_SystemicPause, "false", ct);

            writeCtx.Set<MLCorrelatedFailureLog>().Add(new MLCorrelatedFailureLog
            {
                DetectedAt          = DateTime.UtcNow,
                FailingModelCount   = failingCount,
                TotalModelCount     = modelsWithEnoughPredictions,
                FailureRatio        = failureRatio,
                SymbolsAffectedJson = JsonSerializer.Serialize(failingSymbols),
                PauseActivated      = false,
            });

            await writeCtx.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Systemic ML recovery: failure ratio {Ratio:P1} < recovery threshold {Recovery:P1}. " +
                "Training pause LIFTED.",
                failureRatio, recoveryRatio);
        }
    }

    // ── Config helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/> or returns <paramref name="defaultValue"/>
    /// when the key is absent or its string value cannot be converted to <typeparamref name="T"/>.
    /// All reads are <c>AsNoTracking</c> to avoid stale-cache issues in long-lived loops.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    /// <summary>
    /// Creates or updates an <see cref="EngineConfig"/> entry. If the key already exists,
    /// updates its value and <see cref="EngineConfig.LastUpdatedAt"/> timestamp. Otherwise
    /// creates a new record with <see cref="ConfigDataType.String"/> and hot-reload enabled.
    /// </summary>
    /// <param name="writeCtx">EF write context — must be a tracked context.</param>
    /// <param name="key">The configuration key to upsert.</param>
    /// <param name="value">The new string value.</param>
    /// <param name="ct">Cancellation token.</param>
    private static Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(writeCtx, key, value, dataType: LascodiaTradingEngine.Domain.Enums.ConfigDataType.Bool, ct: ct);
}
