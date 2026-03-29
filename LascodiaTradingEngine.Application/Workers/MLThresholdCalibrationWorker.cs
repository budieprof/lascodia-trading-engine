using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically re-derives the optimal decision threshold for each active ML model
/// from its resolved <see cref="MLModelPredictionLog"/> records and writes the
/// updated value back into <see cref="ModelSnapshot.OptimalThreshold"/>.
///
/// The threshold stored in the snapshot at training time maximises expected value on
/// the hold-out test set. In live trading the optimal threshold drifts as the market
/// regime changes. This worker keeps it aligned between full retrains by using the
/// Youden-J balanced precision strategy:
///
///   Δ = (sellPrecision − buyPrecision) × LearningRate
///   new_threshold = clamp(current_threshold + Δ, MinThreshold, MaxThreshold)
///
/// Rationale: when buyPrecision &lt; sellPrecision the model is too eager to predict Buy;
/// raising the threshold forces it to be more selective, restoring balance.
///
/// The update is skipped when:
/// <list type="bullet">
///   <item>Fewer than <c>MLThresholdCal:MinResolved</c> records exist.</item>
///   <item>Both buy and sell precision already exceed <c>MLThresholdCal:TargetPrecision</c>.</item>
///   <item>The computed delta is below <c>1e-4</c> (negligible movement).</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLThresholdCal:PollIntervalSeconds</c> — default 21600 (6 h)</item>
///   <item><c>MLThresholdCal:WindowDays</c>          — resolved-log look-back, default 21</item>
///   <item><c>MLThresholdCal:MinResolved</c>          — skip model if fewer records, default 40</item>
///   <item><c>MLThresholdCal:LearningRate</c>         — nudge scaling factor, default 0.10</item>
///   <item><c>MLThresholdCal:MinThreshold</c>         — lower clamp, default 0.35</item>
///   <item><c>MLThresholdCal:MaxThreshold</c>         — upper clamp, default 0.65</item>
/// </list>
/// </summary>
public sealed class MLThresholdCalibrationWorker : BackgroundService
{
    private const string CK_PollSecs      = "MLThresholdCal:PollIntervalSeconds";
    private const string CK_WindowDays    = "MLThresholdCal:WindowDays";
    private const string CK_MinResolved   = "MLThresholdCal:MinResolved";
    private const string CK_LearningRate  = "MLThresholdCal:LearningRate";
    private const string CK_MinThreshold  = "MLThresholdCal:MinThreshold";
    private const string CK_MaxThreshold  = "MLThresholdCal:MaxThreshold";

    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";

    private readonly IServiceScopeFactory                    _scopeFactory;
    private readonly IMemoryCache                            _cache;
    private readonly ILogger<MLThresholdCalibrationWorker>   _logger;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">Creates scoped DI scopes per polling cycle.</param>
    /// <param name="cache">
    /// Shared in-memory cache used to evict the <c>MLSnapshot:{modelId}</c> entry
    /// so <c>MLSignalScorer</c> picks up the updated threshold on its next call.
    /// </param>
    /// <param name="logger">Structured logger.</param>
    public MLThresholdCalibrationWorker(
        IServiceScopeFactory                   scopeFactory,
        IMemoryCache                           cache,
        ILogger<MLThresholdCalibrationWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        _logger       = logger;
    }

    /// <summary>
    /// Main hosted-service loop. Polls every <c>MLThresholdCal:PollIntervalSeconds</c>
    /// (default 21600 s / 6 h), creating a fresh DI scope per cycle and delegating
    /// to <see cref="CalibrateThresholdsAsync"/>.
    /// </summary>
    /// <param name="stoppingToken">Signals graceful shutdown requested by the host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLThresholdCalibrationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval refreshed from live EngineConfig each cycle.
            int pollSecs = 21600;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 21600, stoppingToken);

                await CalibrateThresholdsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLThresholdCalibrationWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLThresholdCalibrationWorker stopping.");
    }

    /// <summary>
    /// Loads global threshold-calibration parameters from <see cref="EngineConfig"/>
    /// then iterates over every active model that has serialised <c>ModelBytes</c>,
    /// delegating to <see cref="CalibrateModelThresholdAsync"/> per model.
    /// Errors for individual models are caught so a bad model cannot stall the cycle.
    /// </summary>
    private async Task CalibrateThresholdsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // All configuration values are live — changing them in EngineConfig takes effect
        // on the next poll without a service restart.
        int    windowDays   = await GetConfigAsync<int>   (readCtx, CK_WindowDays,   21,   ct);
        int    minResolved  = await GetConfigAsync<int>   (readCtx, CK_MinResolved,  40,   ct);
        double learningRate = await GetConfigAsync<double>(readCtx, CK_LearningRate, 0.10, ct);
        double minThreshold = await GetConfigAsync<double>(readCtx, CK_MinThreshold, 0.35, ct);
        double maxThreshold = await GetConfigAsync<double>(readCtx, CK_MaxThreshold, 0.65, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CalibrateModelThresholdAsync(
                    model, readCtx, writeCtx,
                    windowDays, minResolved, learningRate, minThreshold, maxThreshold, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Threshold calibration failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    /// <summary>
    /// Adjusts the decision threshold for a single model using the Youden-J balanced
    /// precision strategy applied to its recent resolved prediction logs.
    ///
    /// <b>Decision logic:</b>
    /// <list type="number">
    ///   <item>Load resolved prediction logs within the look-back window.</item>
    ///   <item>Build a binary confusion matrix partitioned by predicted direction (Buy/Sell).</item>
    ///   <item>Compute per-direction precision:
    ///         buyPrecision  = TP / (TP + FP)  (fraction of Buy predictions that were correct)
    ///         sellPrecision = TN / (TN + FN)  (fraction of Sell predictions that were correct)
    ///   </item>
    ///   <item>Compute the threshold nudge:
    ///         Δ = (sellPrecision − buyPrecision) × learningRate
    ///         If buyPrecision &lt; sellPrecision → Δ > 0 → raise threshold → model predicts Buy less often.
    ///         If buyPrecision > sellPrecision → Δ &lt; 0 → lower threshold → model predicts Buy more often.
    ///   </item>
    ///   <item>Apply the nudge clamped to [minThreshold, maxThreshold] and persist.</item>
    /// </list>
    ///
    /// Skips the update when |Δ| &lt; 1e-4 to avoid constant noisy micro-adjustments.
    /// </summary>
    /// <param name="model">Active model entity to adjust the threshold for.</param>
    /// <param name="readCtx">Read-only EF context.</param>
    /// <param name="writeCtx">Write EF context for persisting the updated snapshot.</param>
    /// <param name="windowDays">Look-back window in days for resolved logs.</param>
    /// <param name="minResolved">Minimum resolved-log count required to proceed.</param>
    /// <param name="learningRate">Scales how aggressively the threshold is nudged per cycle.</param>
    /// <param name="minThreshold">Hard lower bound for the decision threshold (e.g. 0.35).</param>
    /// <param name="maxThreshold">Hard upper bound for the decision threshold (e.g. 0.65).</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    private async Task CalibrateModelThresholdAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minResolved,
        double                                  learningRate,
        double                                  minThreshold,
        double                                  maxThreshold,
        CancellationToken                       ct)
    {
        var since = DateTime.UtcNow.AddDays(-windowDays);

        // Project only the fields needed for the confusion matrix to minimise data transfer.
        var resolved = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.DirectionCorrect != null     &&
                        !l.IsDeleted)
            .Select(l => new
            {
                l.PredictedDirection,
                DirectionCorrect = l.DirectionCorrect!.Value,
            })
            .ToListAsync(ct);

        if (resolved.Count < minResolved)
        {
            _logger.LogDebug(
                "ThresholdCal: {Symbol}/{Tf} model {Id} only {N} resolved (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, resolved.Count, minResolved);
            return;
        }

        // Confusion matrix partitioned by predicted direction.
        // TP = correctly predicted Buy, FP = incorrectly predicted Buy (was Sell)
        // TN = correctly predicted Sell, FN = incorrectly predicted Sell (was Buy)
        int tp = resolved.Count(r => r.PredictedDirection == TradeDirection.Buy  &&  r.DirectionCorrect);
        int fp = resolved.Count(r => r.PredictedDirection == TradeDirection.Buy  && !r.DirectionCorrect);
        int tn = resolved.Count(r => r.PredictedDirection == TradeDirection.Sell &&  r.DirectionCorrect);
        int fn = resolved.Count(r => r.PredictedDirection == TradeDirection.Sell && !r.DirectionCorrect);

        // Per-direction precision. Defaults to 0.5 (neutral) when no predictions exist for that class.
        double buyPrecision  = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0.5;
        double sellPrecision = (tn + fn) > 0 ? (double)tn / (tn + fn) : 0.5;

        _logger.LogDebug(
            "ThresholdCal: {Symbol}/{Tf} model {Id}: buyPrec={Buy:P1} sellPrec={Sell:P1} (n={N})",
            model.Symbol, model.Timeframe, model.Id, buyPrecision, sellPrecision, resolved.Count);

        // Youden-J balanced precision nudge:
        // Δ = (sellPrecision − buyPrecision) × learningRate
        // Positive Δ → raise threshold (make Buy predictions rarer).
        // Negative Δ → lower threshold (make Buy predictions more frequent).
        double delta = (sellPrecision - buyPrecision) * learningRate;

        // Skip negligible movements to avoid endless micro-updates.
        if (Math.Abs(delta) < 1e-4)
        {
            _logger.LogDebug(
                "ThresholdCal: {Symbol}/{Tf} model {Id} delta={D:F5} negligible — no update.",
                model.Symbol, model.Timeframe, model.Id, delta);
            return;
        }

        var (writeModel, snap) = await MLModelSnapshotWriteHelper
            .LoadTrackedLatestSnapshotAsync(writeCtx, model.Id, ct);
        if (writeModel == null || snap == null)
            return;

        // Resolve the authoritative current threshold in priority order:
        // 1. OptimalThreshold (set by training or a previous update)
        // 2. AdaptiveThreshold (set by the adaptive threshold worker)
        // 3. 0.5 (symmetrical default for binary classification)
        double currentThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snap);

        // Apply nudge, clamped to the configured safety bounds to prevent runaway drift.
        double newThreshold = Math.Clamp(currentThreshold + delta, minThreshold, maxThreshold);

        _logger.LogInformation(
            "ThresholdCal: {Symbol}/{Tf} model {Id}: threshold {Old:F4} → {New:F4} (Δ={D:F4})",
            model.Symbol, model.Timeframe, model.Id, currentThreshold, newThreshold, delta);

        snap.OptimalThreshold = newThreshold;
        writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);
        await writeCtx.SaveChangesAsync(ct);

        // Evict the scorer cache so the new threshold is used on the next signal-scoring call.
        _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");
    }

    /// <summary>
    /// Reads a typed value from the <see cref="EngineConfig"/> table by key.
    /// Falls back to <paramref name="defaultValue"/> when the key is missing or unparseable.
    /// </summary>
    /// <typeparam name="T">Target CLR type.</typeparam>
    /// <param name="ctx">EF Core context to query.</param>
    /// <param name="key">Configuration key stored in <c>EngineConfig.Key</c>.</param>
    /// <param name="defaultValue">Fallback value.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
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
}
