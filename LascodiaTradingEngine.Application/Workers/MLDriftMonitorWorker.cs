using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors live ML model prediction accuracy and automatically queues a retraining run
/// when a deployed model's accuracy degrades below the configured threshold.
///
/// <para>
/// Every poll cycle the worker:
/// <list type="number">
///   <item>Finds all active <see cref="MLModel"/> records.</item>
///   <item>Looks up <see cref="MLModelPredictionLog"/> records with known outcomes within
///         the rolling drift window (<c>MLTraining:DriftWindowDays</c>).</item>
///   <item>Computes the rolling direction accuracy over the window.</item>
///   <item>If accuracy &lt; <c>MLTraining:DriftAccuracyThreshold</c> AND enough predictions
///         exist, queues a new <see cref="MLTrainingRun"/> with
///         <see cref="TriggerType.AutoDegrading"/>.</item>
///   <item>Skips models that already have a queued/running retraining run to avoid
///         duplicate queue entries.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MLDriftMonitorWorker : BackgroundService
{
    private const string CK_PollSecs            = "MLDrift:PollIntervalSeconds";
    private const string CK_WindowDays          = "MLTraining:DriftWindowDays";
    private const string CK_MinPredictions      = "MLTraining:DriftMinPredictions";
    private const string CK_AccThreshold        = "MLTraining:DriftAccuracyThreshold";
    private const string CK_TrainingDays        = "MLTraining:TrainingDataWindowDays";
    // Calibration drift — triggers retraining when Brier score exceeds threshold
    private const string CK_MaxBrierDrift       = "MLDrift:MaxBrierScore";
    // Disagreement drift — triggers retraining when mean ensemble std rises above threshold
    private const string CK_MaxDisagreement     = "MLDrift:MaxEnsembleDisagreement";
    // Relative degradation — triggers when live accuracy drops below this fraction of training accuracy
    private const string CK_RelativeDegradation = "MLDrift:RelativeDegradationRatio";
    // Consecutive window failures — how many consecutive poll windows must fail before triggering
    private const string CK_ConsecutiveFailures = "MLDrift:ConsecutiveFailuresBeforeRetrain";
    // P&L feedback — triggers when rolling live Sharpe drops below this fraction of training Sharpe
    private const string CK_SharpeDegradation   = "MLDrift:SharpeDegradationRatio";
    // P&L feedback — minimum closed trades in window before Sharpe comparison is active
    private const string CK_MinClosedTrades     = "MLDrift:MinClosedTradesForSharpe";

    /// <summary>
    /// Tracks how many consecutive poll windows each active model has been in a degraded state.
    /// Key = MLModel.Id, Value = consecutive failure count.
    /// Reset to 0 when the model is healthy.
    /// </summary>
    private readonly Dictionary<long, int> _consecutiveFailures = new();

    private readonly IServiceScopeFactory       _scopeFactory;
    private readonly ILogger<MLDriftMonitorWorker> _logger;

    public MLDriftMonitorWorker(
        IServiceScopeFactory            scopeFactory,
        ILogger<MLDriftMonitorWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLDriftMonitorWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 300; // default 5 min

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs,           300,  stoppingToken);
                int windowDays        = await GetConfigAsync<int>   (ctx, CK_WindowDays,        14,   stoppingToken);
                int minPredictions    = await GetConfigAsync<int>   (ctx, CK_MinPredictions,    30,   stoppingToken);
                double threshold      = await GetConfigAsync<double>(ctx, CK_AccThreshold,      0.50, stoppingToken);
                int trainingDays      = await GetConfigAsync<int>   (ctx, CK_TrainingDays,      365,  stoppingToken);
                double maxBrier       = await GetConfigAsync<double>(ctx, CK_MaxBrierDrift,     0.30, stoppingToken);
                double maxDisagreement = await GetConfigAsync<double>(ctx, CK_MaxDisagreement,  0.35, stoppingToken);
                double relDegradation = await GetConfigAsync<double>(ctx, CK_RelativeDegradation, 0.85, stoppingToken);
                int    requiredConsecutiveFailures = await GetConfigAsync<int>(ctx, CK_ConsecutiveFailures, 3, stoppingToken);
                double sharpeDegradation = await GetConfigAsync<double>(ctx, CK_SharpeDegradation, 0.60, stoppingToken);
                int    minClosedTrades   = await GetConfigAsync<int>   (ctx, CK_MinClosedTrades,   20,   stoppingToken);

                var windowStart = DateTime.UtcNow.AddDays(-windowDays);

                // ── Load all active models ───────────────────────────────────
                var activeModels = await ctx.Set<MLModel>()
                    .Where(m => m.IsActive && !m.IsDeleted)
                    .AsNoTracking()
                    .ToListAsync(stoppingToken);

                _logger.LogDebug(
                    "Drift monitor checking {Count} active models (window={Days}d threshold={Thr:P1} relDeg={Rel:P0})",
                    activeModels.Count, windowDays, threshold, relDegradation);

                // Clean up consecutive failure tracking for models no longer active
                var activeIds = new HashSet<long>(activeModels.Select(m => m.Id));
                foreach (var key in _consecutiveFailures.Keys.Where(k => !activeIds.Contains(k)).ToList())
                    _consecutiveFailures.Remove(key);

                foreach (var model in activeModels)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await CheckModelDriftAsync(
                        model, writeCtx, ctx,
                        windowStart, minPredictions, threshold, trainingDays,
                        maxBrier, maxDisagreement,
                        relDegradation, requiredConsecutiveFailures,
                        sharpeDegradation, minClosedTrades,
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLDriftMonitorWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLDriftMonitorWorker stopping.");
    }

    // ── Per-model drift check ─────────────────────────────────────────────────

    private async Task CheckModelDriftAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        DateTime                                windowStart,
        int                                     minPredictions,
        double                                  threshold,
        int                                     trainingDays,
        double                                  maxBrierScore,
        double                                  maxEnsembleDisagreement,
        double                                  relativeDegradationRatio,
        int                                     requiredConsecutiveFailures,
        double                                  sharpeDegradationRatio,
        int                                     minClosedTradesForSharpe,
        CancellationToken                       ct)
    {
        // Fetch resolved predictions within the rolling window
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        !l.IsDeleted                   &&
                        l.DirectionCorrect != null     &&
                        l.PredictedAt      >= windowStart)
            .AsNoTracking()
            .ToListAsync(ct);

        // Also fetch all predictions (including unresolved) for disagreement monitoring
        var allLogs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId  == model.Id &&
                        !l.IsDeleted             &&
                        l.PredictedAt >= windowStart)
            .AsNoTracking()
            .ToListAsync(ct);

        if (logs.Count < minPredictions)
        {
            _logger.LogDebug(
                "Model {Id} ({Symbol}/{Tf}): only {N} resolved predictions in window — skipping drift check",
                model.Id, model.Symbol, model.Timeframe, logs.Count);
            return;
        }

        int    correct  = logs.Count(l => l.DirectionCorrect == true);
        double accuracy = (double)correct / logs.Count;

        // ── Calibration: rolling Brier score ─────────────────────────────────
        double brierScore = ComputeRollingBrierScore(logs);

        // ── Ensemble disagreement: mean inter-learner std ─────────────────────
        var disagLogs = allLogs.Where(l => l.EnsembleDisagreement.HasValue).ToList();
        double meanDisagreement = disagLogs.Count > 0
            ? (double)disagLogs.Average(l => l.EnsembleDisagreement!.Value)
            : 0;

        _logger.LogDebug(
            "Model {Id} ({Symbol}/{Tf}): acc={Acc:P1} brier={Brier:F4} disagree={Dis:F4} N={N}",
            model.Id, model.Symbol, model.Timeframe, accuracy, brierScore, meanDisagreement, logs.Count);

        bool accuracyDrift     = accuracy < threshold;
        bool calibrationDrift  = brierScore > maxBrierScore;
        bool disagreementDrift = disagLogs.Count >= minPredictions && meanDisagreement > maxEnsembleDisagreement;

        // ── Relative degradation: compare live accuracy against model's own training accuracy ──
        bool relativeDrift = false;
        if (model.DirectionAccuracy.HasValue && model.DirectionAccuracy.Value > 0)
        {
            double trainingAcc = (double)model.DirectionAccuracy.Value;
            double degradationThreshold = trainingAcc * relativeDegradationRatio;
            relativeDrift = accuracy < degradationThreshold;
        }

        // ── P&L feedback: rolling live Sharpe vs model's training Sharpe ───────
        bool sharpeDrift = false;
        if (model.SharpeRatio.HasValue && model.SharpeRatio.Value > 0)
        {
            // Compute rolling Sharpe from resolved predictions' P&L proxy (magnitude × direction correctness)
            var pnlReturns = logs
                .Where(l => l.ActualMagnitudePips.HasValue)
                .Select(l => (double)l.ActualMagnitudePips!.Value * (l.DirectionCorrect == true ? 1.0 : -1.0))
                .ToList();

            if (pnlReturns.Count >= minClosedTradesForSharpe)
            {
                double mean = pnlReturns.Average();
                double variance = pnlReturns.Sum(r => (r - mean) * (r - mean)) / pnlReturns.Count;
                double std = Math.Sqrt(variance);
                double liveSharpe = std > 1e-10 ? mean / std * Math.Sqrt(252) : 0;
                double trainSharpe = (double)model.SharpeRatio.Value;
                double sharpeThreshold = trainSharpe * sharpeDegradationRatio;

                sharpeDrift = liveSharpe < sharpeThreshold;

                _logger.LogDebug(
                    "Model {Id}: live Sharpe={Live:F2} vs train={Train:F2} (threshold={Thr:F2})",
                    model.Id, liveSharpe, trainSharpe, sharpeThreshold);
            }
        }

        bool anyDrift = accuracyDrift || calibrationDrift || disagreementDrift || relativeDrift || sharpeDrift;

        if (!anyDrift)
        {
            // Model is healthy — reset consecutive failure counter
            _consecutiveFailures[model.Id] = 0;
            return;
        }

        // ── Consecutive window tracking ─────────────────────────────────────
        _consecutiveFailures.TryGetValue(model.Id, out int failCount);
        failCount++;
        _consecutiveFailures[model.Id] = failCount;

        string driftReason = string.Join(", ", new[]
        {
            accuracyDrift     ? $"accuracy={accuracy:P1}<{threshold:P1}" : null,
            relativeDrift     ? $"relDeg={accuracy:P1}<{(double)model.DirectionAccuracy!.Value * relativeDegradationRatio:P1}({relativeDegradationRatio:P0}×train)" : null,
            calibrationDrift  ? $"brier={brierScore:F4}>{maxBrierScore:F4}" : null,
            disagreementDrift ? $"disagreement={meanDisagreement:F4}>{maxEnsembleDisagreement:F4}" : null,
            sharpeDrift       ? $"sharpeDeg=live<{sharpeDegradationRatio:P0}×trainSharpe" : null,
        }.Where(s => s is not null));

        if (failCount < requiredConsecutiveFailures)
        {
            _logger.LogInformation(
                "Model {Id} ({Symbol}/{Tf}): drift detected [{Reason}] — window {N}/{Required} before retrain",
                model.Id, model.Symbol, model.Timeframe, driftReason, failCount, requiredConsecutiveFailures);
            return;
        }

        // Threshold met — reset counter and proceed to queue retraining
        _consecutiveFailures[model.Id] = 0;

        // ── Check whether a retraining run is already queued or running ──────
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running),
                      ct);

        if (alreadyQueued)
        {
            _logger.LogDebug(
                "Model {Id} ({Symbol}/{Tf}): drift detected [{Reason}] but retraining already queued",
                model.Id, model.Symbol, model.Timeframe, driftReason);
            return;
        }

        // ── Queue a new AutoDegrading training run ────────────────────────────
        var now = DateTime.UtcNow;
        var run = new MLTrainingRun
        {
            Symbol      = model.Symbol,
            Timeframe   = model.Timeframe,
            TriggerType = TriggerType.AutoDegrading,
            Status      = RunStatus.Queued,
            FromDate    = now.AddDays(-trainingDays),
            ToDate      = now,
            StartedAt   = now,
        };

        writeCtx.Set<MLTrainingRun>().Add(run);
        await writeCtx.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Drift detected for model {Id} ({Symbol}/{Tf}): [{Reason}] over {N} predictions. " +
            "Queued retraining run {RunId}.",
            model.Id, model.Symbol, model.Timeframe, driftReason, logs.Count, run.Id);
    }

    // ── Metric helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Brier score using ConfidenceScore as the probability of the predicted direction.
    /// Remaps confidence [0,1] → probability space: p = 0.5 + conf/2 when correct direction,
    /// 0.5 − conf/2 when wrong. Tracks calibration drift independently of accuracy.
    /// </summary>
    private static double ComputeRollingBrierScore(List<MLModelPredictionLog> logs)
    {
        double sum = 0;
        int    n   = 0;
        foreach (var l in logs)
        {
            if (l.DirectionCorrect is null) continue;
            double conf = (double)l.ConfidenceScore;
            double pBuy = l.PredictedDirection == TradeDirection.Buy
                ? 0.5 + conf / 2.0
                : 0.5 - conf / 2.0;
            double y   = l.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
            sum += (pBuy - y) * (pBuy - y);
            n++;
        }
        return n > 0 ? sum / n : 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
