using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Post-trade online SGD weight update worker (Rec #17).
/// Polls for recently resolved <see cref="MLModelPredictionLog"/> records where the
/// trade outcome is now known, and applies a single mini-batch SGD gradient step
/// to the active model's weights — enabling continuous learning between full retrains.
/// </summary>
/// <remarks>
/// Each update:
///   1. Loads newly resolved prediction logs since the model's last online-update timestamp.
///   2. Deserialises the active model's <see cref="ModelSnapshot"/>.
///   3. Runs a single forward + backward pass computing the cross-entropy gradient.
///   4. Updates each ensemble learner's bias term by −lr × gradient.
///   5. Re-serialises the snapshot and patches <c>MLModel.ModelBytes</c>.
///   6. Increments <c>MLModel.OnlineLearningUpdateCount</c>.
///
/// The learning rate is intentionally small (default 1e-4) to prevent catastrophic
/// forgetting while still allowing the model to adapt to recent distributional shifts.
/// </remarks>
public sealed class MLOnlineLearningWorker : BackgroundService
{
    private readonly IServiceScopeFactory   _scopeFactory;
    private readonly ILogger<MLOnlineLearningWorker> _logger;

    private const double OnlineLr        = 1e-4;
    private const int    BatchSize       = 32;
    private const int    PollIntervalSec = 60;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per poll cycle so EF Core scoped contexts
    /// are correctly disposed after each batch.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLOnlineLearningWorker(IServiceScopeFactory scopeFactory, ILogger<MLOnlineLearningWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Polls every <see cref="PollIntervalSec"/> seconds (60 s)
    /// and invokes <see cref="RunBatchAsync"/> to apply incremental SGD updates from
    /// recently resolved trade outcomes.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLOnlineLearningWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunBatchAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLOnlineLearningWorker error"); }
            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSec), stoppingToken);
        }
    }

    /// <summary>
    /// Processes a mini-batch of recently resolved prediction outcomes and applies
    /// a single online SGD gradient step to each affected model's ensemble biases.
    /// </summary>
    /// <remarks>
    /// Online / incremental learning update methodology:
    /// <list type="number">
    ///   <item>
    ///     <b>Outcome discovery:</b> For each active model, query resolved
    ///     <see cref="MLModelPredictionLog"/> rows whose <c>OutcomeRecordedAt</c> is newer
    ///     than the model's <c>LastOnlineLearningAt</c> watermark (or, on first run, newer
    ///     than 2 hours ago). Take at most <see cref="BatchSize"/> records ordered oldest-first
    ///     to process in chronological sequence.
    ///   </item>
    ///   <item>
    ///     <b>Model grouping:</b> Group logs by <c>MLModelId</c> so each model's
    ///     weights are updated exactly once per batch call (one SaveChanges per model),
    ///     preventing partial writes if an exception interrupts the loop mid-batch.
    ///   </item>
    ///   <item>
    ///     <b>Gradient computation:</b> The raw feature vector is not stored in the
    ///     prediction log (storing it would double DB size per prediction). Instead, a
    ///     scalar pseudo-gradient is derived solely from the probability prediction error:
    ///     <c>err = pHat − targetLabel</c>. This is applied as a bias correction:
    ///     <c>bias[k] -= OnlineLr × err</c>. While less precise than a full gradient
    ///     step, it nudges the model's calibration in the correct direction without
    ///     requiring feature replay.
    ///   </item>
    ///   <item>
    ///     <b>Catastrophic forgetting prevention:</b> The learning rate <see cref="OnlineLr"/>
    ///     (1e-4) is intentionally tiny relative to the batch training rate (typically
    ///     0.01–0.1). This ensures that thousands of online updates are needed to
    ///     meaningfully shift the decision boundary, preventing any single noisy trade
    ///     outcome from destabilising the model.
    ///   </item>
    ///   <item>
    ///     <b>Bookkeeping:</b> After saving, <c>MLModel.OnlineLearningUpdateCount</c>
    ///     and <c>LastOnlineLearningAt</c> are updated so monitoring workers can track
    ///     how aggressively a model has drifted from its batch-trained state.
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task RunBatchAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        var activeModels = await writeDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            if (model.ModelBytes == null) continue;

            try
            {
                var snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes);
                if (snap == null) continue;

                DateTime outcomeCutoff = model.LastOnlineLearningAt ?? DateTime.UtcNow.AddHours(-2);
                var logs = await readDb.Set<MLModelPredictionLog>()
                    .Where(l => !l.IsDeleted
                             && l.MLModelId == model.Id
                             && l.DirectionCorrect.HasValue
                             && l.OutcomeRecordedAt != null
                             && l.OutcomeRecordedAt > outcomeCutoff)
                    .OrderBy(l => l.OutcomeRecordedAt)
                    .Take(BatchSize)
                    .ToListAsync(ct);

                if (logs.Count == 0) continue;

                // GBM models store their structure in GbmTreesJson rather than the
                // Weights/Biases arrays used by logistic/ELM architectures.
                // For GBM, apply the online bias correction to GbmBaseLogOdds instead.
                bool isGbm = !string.IsNullOrEmpty(snap.GbmTreesJson);

                // For non-GBM models, require valid Weights and Biases arrays.
                if (!isGbm && (snap.Weights == null || snap.Biases == null
                            || snap.Weights.Length == 0 || snap.Biases.Length == 0))
                    continue;

                int K = isGbm ? 0 : snap.Weights!.Length;

                int appliedCount = 0;
                DateTime latestOutcomeRecordedAt = outcomeCutoff;
                foreach (var log in logs)
                {
                    double targetLabel = (log.ActualDirection == log.PredictedDirection) ? 1.0 : 0.0;
                    double pBuy        = MLFeatureHelper.ResolveLoggedCalibratedBuyProbability(log);
                    double pHat        = log.PredictedDirection == TradeDirection.Buy ? pBuy : 1.0 - pBuy;

                    // Cross-entropy gradient w.r.t. the logit: σ(z) − y.
                    // This is the standard logistic regression gradient; subtracting it
                    // (multiplied by lr) from the bias moves the model's output toward
                    // the correct label.
                    double err = pHat - targetLabel;

                    if (isGbm)
                    {
                        // GBM models use GbmBaseLogOdds as the intercept term.
                        // Adjusting it shifts the base probability for all predictions,
                        // correcting systematic over- or under-confidence.
                        snap.GbmBaseLogOdds -= OnlineLr * err;
                    }
                    else
                    {
                        // Apply error-scaled bias correction to all K learners in the ensemble.
                        // Without the stored feature vector we cannot update feature weights w,
                        // only the intercept (bias) term. This still corrects for systematic
                        // over- or under-confidence without distorting the decision hyperplane.
                        for (int k = 0; k < K && k < snap.Biases!.Length; k++)
                            snap.Biases[k] -= OnlineLr * err;
                    }

                    appliedCount++;
                    if (log.OutcomeRecordedAt!.Value > latestOutcomeRecordedAt)
                        latestOutcomeRecordedAt = log.OutcomeRecordedAt.Value;
                }

                if (appliedCount == 0) continue;

                // Re-serialise the updated snapshot back into the model blob.
                model.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);

                // Track cumulative online updates so monitoring workers can flag models
                // that have diverged significantly from their batch-trained baseline.
                model.OnlineLearningUpdateCount += appliedCount;
                model.LastOnlineLearningAt       = latestOutcomeRecordedAt;

                await writeDb.SaveChangesAsync(ct);
                _logger.LogDebug(
                    "MLOnlineLearningWorker updated model {Id} with {N} outcomes.",
                    model.Id, appliedCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Online learning failed for model {Id}", model.Id);
            }
        }
    }
}
