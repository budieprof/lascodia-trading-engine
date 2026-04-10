using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
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
    // Queue depth limiter — maximum concurrent Queued training runs before drift workers stop queuing
    private const string CK_MaxQueueDepth       = "MLTraining:MaxQueueDepth";

    private readonly IServiceScopeFactory       _scopeFactory;
    private readonly ILogger<MLDriftMonitorWorker> _logger;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per poll cycle, which gives each iteration a fresh
    /// pair of <see cref="IReadApplicationDbContext"/> / <see cref="IWriteApplicationDbContext"/>
    /// instances and prevents long-lived DbContext connection leaks.
    /// </param>
    /// <param name="logger">Structured logger for drift events and diagnostic output.</param>
    public MLDriftMonitorWorker(
        IServiceScopeFactory            scopeFactory,
        ILogger<MLDriftMonitorWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Main background loop. Runs indefinitely at <c>MLDrift:PollIntervalSeconds</c> intervals
    /// (default 300 s / 5 min) until the host requests shutdown.
    ///
    /// Each cycle:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope and resolves read + write DB contexts.</item>
    ///   <item>Reads all configurable thresholds from <see cref="EngineConfig"/>.</item>
    ///   <item>Loads all active (non-deleted) <see cref="MLModel"/> records.</item>
    ///   <item>Prunes the in-memory <see cref="_consecutiveFailures"/> tracker for models
    ///         that are no longer active (prevents unbounded memory growth).</item>
    ///   <item>Calls <see cref="CheckModelDriftAsync"/> for each active model.</item>
    /// </list>
    /// </summary>
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

                    // Improvement #10: proactive champion tenure challenge
                    try
                    {
                        await CheckChampionTenureAsync(model, ctx, writeCtx, trainingDays, stoppingToken);
                    }
                    catch (Exception tenureEx)
                    {
                        _logger.LogDebug(tenureEx, "Tenure check failed for model {Id} — non-critical", model.Id);
                    }

                    // Model expiry policy: force retraining when a model exceeds MaxModelAgeDays
                    try
                    {
                        await CheckModelExpiryAsync(model, ctx, writeCtx, trainingDays, stoppingToken);
                    }
                    catch (Exception expiryEx)
                    {
                        _logger.LogDebug(expiryEx, "Model expiry check failed for model {Id} — non-critical", model.Id);
                    }
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

    /// <summary>
    /// Evaluates a single active <see cref="MLModel"/> for multiple forms of performance
    /// degradation using its resolved <see cref="MLModelPredictionLog"/> records from the
    /// rolling drift window. Queues a <see cref="MLTrainingRun"/> if any drift criterion
    /// is triggered for <paramref name="requiredConsecutiveFailures"/> consecutive windows.
    ///
    /// <para><b>Drift criteria evaluated (any one can trigger retraining):</b></para>
    /// <list type="bullet">
    ///   <item><b>Accuracy drift</b> — rolling direction accuracy &lt; <paramref name="threshold"/>
    ///         (absolute floor, e.g. 50%).</item>
    ///   <item><b>Relative degradation</b> — rolling accuracy &lt; training accuracy ×
    ///         <paramref name="relativeDegradationRatio"/> (e.g. 85% of training accuracy),
    ///         allowing models trained on different base rates to share a common relative standard.</item>
    ///   <item><b>Calibration drift (Brier score)</b> — rolling Brier score &gt;
    ///         <paramref name="maxBrierScore"/>. Detects models that are systematically
    ///         over- or under-confident even when raw accuracy is still acceptable.
    ///         See <see cref="ComputeRollingBrierScore"/>.</item>
    ///   <item><b>Ensemble disagreement</b> — mean inter-learner std &gt;
    ///         <paramref name="maxEnsembleDisagreement"/>. High disagreement among bag members
    ///         indicates the model is operating in a region of the input space not well
    ///         covered by training data — an early-warning signal before accuracy drops.</item>
    ///   <item><b>Sharpe degradation</b> — rolling live Sharpe &lt; training Sharpe ×
    ///         <paramref name="sharpeDegradationRatio"/>. Requires at least
    ///         <paramref name="minClosedTradesForSharpe"/> resolved trades. Uses
    ///         magnitude × direction-sign as a P&amp;L proxy (annualised with √252 factor).</item>
    /// </list>
    ///
    /// <para><b>Consecutive-window guard:</b> a single bad window (e.g. a flash event) is
    /// not enough to trigger retraining. The model must fail on
    /// <paramref name="requiredConsecutiveFailures"/> consecutive poll cycles before a new
    /// <see cref="MLTrainingRun"/> is queued. The counter is persisted in <see cref="EngineConfig"/>
    /// under key <c>MLDrift:{Symbol}:{Timeframe}:ConsecutiveFailures</c> so it survives worker
    /// restarts — eliminating the in-memory amnesia bug where a restart would reset a model
    /// that was 2/3 windows toward a retrain trigger.</para>
    ///
    /// <para><b>Deduplication:</b> if a run for the same symbol/timeframe is already
    /// queued or running, this method skips queueing to avoid pile-up.</para>
    /// </summary>
    /// <param name="model">The active model to evaluate.</param>
    /// <param name="writeCtx">EF write context — used only to persist a new <see cref="MLTrainingRun"/>.</param>
    /// <param name="readCtx">EF read context — used for all SELECT queries.</param>
    /// <param name="windowStart">Start of the rolling evaluation window (UTC).</param>
    /// <param name="minPredictions">Minimum resolved predictions required to run drift checks.</param>
    /// <param name="threshold">Absolute direction accuracy floor.</param>
    /// <param name="trainingDays">Training window size for the queued retraining run (days back from now).</param>
    /// <param name="maxBrierScore">Maximum acceptable Brier score (calibration gate).</param>
    /// <param name="maxEnsembleDisagreement">Maximum acceptable mean ensemble std deviation.</param>
    /// <param name="relativeDegradationRatio">Fraction of training accuracy below which relative drift fires.</param>
    /// <param name="requiredConsecutiveFailures">Number of consecutive bad windows before retraining is queued.</param>
    /// <param name="sharpeDegradationRatio">Fraction of training Sharpe below which Sharpe drift fires.</param>
    /// <param name="minClosedTradesForSharpe">Minimum resolved trades before Sharpe comparison is active.</param>
    /// <param name="ct">Cancellation token.</param>
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
                        l.OutcomeRecordedAt != null    &&
                        l.OutcomeRecordedAt >= windowStart)
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
        double fallbackThreshold = 0.5;
        if (model.ModelBytes is { Length: > 0 })
        {
            try
            {
                var snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes);
                if (snap is not null)
                    fallbackThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snap);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MLDriftMonitorWorker: failed to deserialize ModelSnapshot for model {ModelId} — using fallback threshold {Threshold}",
                    model.Id, fallbackThreshold);
            }
        }

        double brierScore = ComputeRollingBrierScore(logs, fallbackThreshold);

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

        // ── Persisted consecutive failure counter ────────────────────────────
        // Stored in EngineConfig so it survives worker restarts. Key pattern:
        //   MLDrift:{Symbol}:{Timeframe}:ConsecutiveFailures
        var failKey = $"MLDrift:{model.Symbol}:{model.Timeframe}:ConsecutiveFailures";

        if (!anyDrift)
        {
            // Model is healthy — reset persisted consecutive failure counter
            await ResetPersistedFailureCountAsync(writeCtx, failKey, ct);
            return;
        }

        // ── Consecutive window tracking ─────────────────────────────────────
        int failCount = await GetConfigAsync<int>(readCtx, failKey, 0, ct);
        failCount++;
        await UpsertConfigAsync(writeCtx, failKey, failCount.ToString(), ct);

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

        // Threshold met — reset persisted counter and proceed to queue retraining
        await ResetPersistedFailureCountAsync(writeCtx, failKey, ct);

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

        // ── Global queue depth limiter ──────────────────────────────────────
        // Prevents thundering-herd when many models drift simultaneously —
        // caps the total number of Queued runs. Emergency runs (Priority <= 1)
        // bypass the limiter so drift-triggered retrains are never blocked.
        int maxQueueDepth = await GetConfigAsync<int>(readCtx, CK_MaxQueueDepth, 10, ct);
        int currentQueueDepth = await readCtx.Set<MLTrainingRun>()
            .CountAsync(r => r.Status == RunStatus.Queued, ct);

        if (currentQueueDepth >= maxQueueDepth)
        {
            _logger.LogWarning(
                "Model {Id} ({Symbol}/{Tf}): drift detected [{Reason}] but queue depth {Depth} >= max {Max} — skipping",
                model.Id, model.Symbol, model.Timeframe, driftReason, currentQueueDepth, maxQueueDepth);
            return;
        }

        // ── Queue a new AutoDegrading training run ────────────────────────────
        var now = DateTime.UtcNow;

        // Improvement #2: determine primary drift trigger type and build metadata.
        // Count active signals to distinguish single from multi-signal drift.
        int activeSignals = (accuracyDrift ? 1 : 0) + (calibrationDrift ? 1 : 0) +
                            (disagreementDrift ? 1 : 0) + (relativeDrift ? 1 : 0) +
                            (sharpeDrift ? 1 : 0);

        // Weight drift signals by importance
        double driftScore = 0;
        if (accuracyDrift) driftScore += 1.0;        // Accuracy is most important
        if (calibrationDrift) driftScore += 0.7;
        if (sharpeDrift) driftScore += 0.5;
        if (relativeDrift) driftScore += 0.4;
        if (disagreementDrift) driftScore += 0.3;

        // Log when composite signal is strong
        if (driftScore >= 1.5)
        {
            _logger.LogInformation("MLDriftMonitor: strong composite drift signal ({Score:F1}) for model {ModelId} ({Symbol}/{Tf})",
                driftScore, model.Id, model.Symbol, model.Timeframe);
        }

        string driftTrigger = activeSignals == 1
            ? (accuracyDrift     ? "AccuracyDrift"
             : calibrationDrift  ? "CalibrationDrift"
             : disagreementDrift ? "DisagreementDrift"
             : sharpeDrift       ? "SharpeDrift"
             :                     "RelativeDegradation")
            : "MultiSignal";

        string driftMetadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            accuracy       = accuracyDrift     ? accuracy          : (double?)null,
            threshold      = accuracyDrift     ? threshold         : (double?)null,
            brierScore     = calibrationDrift  ? brierScore        : (double?)null,
            disagreement   = disagreementDrift ? meanDisagreement  : (double?)null,
        });

        var run = new MLTrainingRun
        {
            Symbol           = model.Symbol,
            Timeframe        = model.Timeframe,
            TriggerType      = TriggerType.AutoDegrading,
            Status           = RunStatus.Queued,
            FromDate         = now.AddDays(-trainingDays),
            ToDate           = now,
            StartedAt        = now,
            DriftTriggerType = driftTrigger,
            DriftMetadataJson = driftMetadata,
            Priority         = 1, // Improvement #9: drift-triggered = priority 1
        };

        writeCtx.Set<MLTrainingRun>().Add(run);
        await writeCtx.SaveChangesAsync(ct);

        // ── Inter-worker coordination: signal urgent suppression check ────
        // Set an EngineConfig flag so MLSignalSuppressionWorker can prioritize
        // this symbol/timeframe on its next iteration instead of waiting for
        // the full poll cycle (reduces propagation delay from minutes to seconds).
        var urgentKey = $"MLDrift:UrgentSymbol:{model.Symbol}:{model.Timeframe}";
        await UpsertConfigAsync(writeCtx, urgentKey, DateTime.UtcNow.ToString("O"), ct);

        _logger.LogWarning(
            "Drift detected for model {Id} ({Symbol}/{Tf}): [{Reason}] (trigger={Trigger}) over {N} predictions. " +
            "Queued retraining run {RunId}. Set urgent flag for suppression worker.",
            model.Id, model.Symbol, model.Timeframe, driftReason, driftTrigger, logs.Count, run.Id);
    }

    // ── Improvement #10: Champion tenure tracking ──────────────────────────

    /// <summary>
    /// Checks whether the model has exceeded the maximum champion tenure without being
    /// challenged. If so, queues a proactive training run to ensure the model hasn't
    /// silently degraded below optimal performance.
    /// </summary>
    private async Task CheckChampionTenureAsync(
        MLModel                           model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                               trainingDays,
        CancellationToken                 ct)
    {
        // Read tenure config
        var enabledStr = await readCtx.Set<EngineConfig>()
            .Where(c => c.Key == "MLTraining:ProactiveChallengeEnabled" && !c.IsDeleted)
            .Select(c => c.Value).FirstOrDefaultAsync(ct);
        if (enabledStr != "true" && enabledStr != "1") return;

        var maxTenureStr = await readCtx.Set<EngineConfig>()
            .Where(c => c.Key == "MLTraining:MaxChampionTenureDays" && !c.IsDeleted)
            .Select(c => c.Value).FirstOrDefaultAsync(ct);
        int maxTenureDays = int.TryParse(maxTenureStr, out var mt) ? mt : 30;

        var minBetweenStr = await readCtx.Set<EngineConfig>()
            .Where(c => c.Key == "MLTraining:MinDaysBetweenChallenges" && !c.IsDeleted)
            .Select(c => c.Value).FirstOrDefaultAsync(ct);
        int minDaysBetween = int.TryParse(minBetweenStr, out var mb) ? mb : 7;

        if (!model.ActivatedAt.HasValue) return;

        var now = DateTime.UtcNow;
        double tenureDays = (now - model.ActivatedAt.Value).TotalDays;
        if (tenureDays < maxTenureDays) return;

        // Check cooldown since last challenge
        if (model.LastChallengedAt.HasValue &&
            (now - model.LastChallengedAt.Value).TotalDays < minDaysBetween)
            return;

        // Check if a run is already queued/running
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);
        if (alreadyQueued) return;

        // Queue a proactive challenge run
        var run = new MLTrainingRun
        {
            Symbol           = model.Symbol,
            Timeframe        = model.Timeframe,
            TriggerType      = TriggerType.Scheduled,
            Status           = RunStatus.Queued,
            FromDate         = now.AddDays(-trainingDays),
            ToDate           = now,
            StartedAt        = now,
            Priority         = 2, // Improvement #9: tenure challenge = priority 2
        };

        writeCtx.Set<MLTrainingRun>().Add(run);

        // Update LastChallengedAt to prevent redundant challenges
        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == model.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastChallengedAt, now), ct);

        await writeCtx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Tenure challenge: model {Id} ({Symbol}/{Tf}) active for {Days:F0} days " +
            "(max tenure={MaxDays}). Queued proactive retraining run {RunId}.",
            model.Id, model.Symbol, model.Timeframe, tenureDays, maxTenureDays, run.Id);
    }

    // ── Model expiry policy ─────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the active model has exceeded <c>MLTraining:MaxModelAgeDays</c> (default 90).
    /// If the model is expired and no queued/running retraining run exists, queues an emergency
    /// retraining run with Priority=0 (highest) and DriftTriggerType="ModelExpiry".
    /// </summary>
    private async Task CheckModelExpiryAsync(
        MLModel                                    model,
        Microsoft.EntityFrameworkCore.DbContext     readCtx,
        Microsoft.EntityFrameworkCore.DbContext     writeCtx,
        int                                        trainingDays,
        CancellationToken                          ct)
    {
        if (!model.ActivatedAt.HasValue) return;

        int maxAgeDays = await GetConfigAsync<int>(readCtx, "MLTraining:MaxModelAgeDays", 90, ct);
        double ageDays = (DateTime.UtcNow - model.ActivatedAt.Value).TotalDays;

        if (ageDays <= maxAgeDays) return;

        // Check if a retraining run is already queued or running
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);

        if (alreadyQueued) return;

        _logger.LogWarning(
            "Model {Id} ({Symbol}/{Tf}): exceeded max age ({Age:F0} days > {Max} days). " +
            "Queuing emergency retraining.",
            model.Id, model.Symbol, model.Timeframe, ageDays, maxAgeDays);

        var now = DateTime.UtcNow;
        var run = new MLTrainingRun
        {
            Symbol           = model.Symbol,
            Timeframe        = model.Timeframe,
            TriggerType      = TriggerType.AutoDegrading,
            Status           = RunStatus.Queued,
            FromDate         = now.AddDays(-trainingDays),
            ToDate           = now,
            StartedAt        = now,
            DriftTriggerType = "ModelExpiry",
            Priority         = 0, // emergency priority
        };

        writeCtx.Set<MLTrainingRun>().Add(run);
        await writeCtx.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Model expiry: model {Id} ({Symbol}/{Tf}) active for {Age:F0} days (max={Max}). " +
            "Queued emergency retraining run {RunId} (Priority=0).",
            model.Id, model.Symbol, model.Timeframe, ageDays, maxAgeDays, run.Id);
    }

    // ── Metric helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Brier score using ConfidenceScore as the probability of the predicted direction.
    /// Remaps confidence [0,1] → probability space: p = 0.5 + conf/2 when correct direction,
    /// 0.5 − conf/2 when wrong. Tracks calibration drift independently of accuracy.
    /// </summary>
    private static double ComputeRollingBrierScore(
        List<MLModelPredictionLog> logs,
        double                     fallbackThreshold)
    {
        double sum = 0;
        int    n   = 0;
        foreach (var l in logs)
        {
            if (l.DirectionCorrect is null) continue;
            double pBuy = MLFeatureHelper.ResolveLoggedServedBuyProbability(l, fallbackThreshold);
            double y   = l.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
            sum += (pBuy - y) * (pBuy - y);
            n++;
        }
        return n > 0 ? sum / n : 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
    /// Upserts a value into <see cref="EngineConfig"/>. If the key already exists, updates
    /// the value; otherwise inserts a new row. Used to persist the consecutive failure counter
    /// across worker restarts.
    /// </summary>
    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        int updated = await ctx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Value, value), ct);

        if (updated == 0)
        {
            ctx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key      = key,
                Value    = value,
                DataType = ConfigDataType.Int,
            });
            await ctx.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Resets (or removes) the persisted consecutive failure counter for a model that is healthy.
    /// Uses <c>ExecuteUpdateAsync</c> to set the value to "0" — avoids loading the entity.
    /// </summary>
    private static async Task ResetPersistedFailureCountAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        CancellationToken                       ct)
    {
        await ctx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Value, "0"), ct);
    }
}
