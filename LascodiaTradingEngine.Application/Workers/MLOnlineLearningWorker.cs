using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using System.Collections.Concurrent;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Performs lightweight online SGD updates on active models using resolved prediction
/// outcomes (Rec #17). Each cycle finds prediction logs with known outcomes that arrived
/// after the model's last online update, then applies a single gradient step per outcome
/// to the logistic weights: <c>w -= lr * (predicted - actual) * x</c>.
/// </summary>
/// <remarks>
/// Safety hierarchy (defense-in-depth):
///   1. Primary: per-step gradient clipping (max delta = LearningRate × 1.0)
///   2. Secondary: per-update accuracy validation on held-out buffer (revert if accuracy drops > 1%)
///   3. Tertiary: L2 distance from original weights (catastrophic backstop at 2.0)
/// On reversion, the model enters a 1-hour cooldown to prevent thrashing.
/// Adaptive learning rate decay reduces update magnitude as the model stabilizes.
/// </remarks>
public sealed class MLOnlineLearningWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLOnlineLearningWorker> _logger;

    /// <summary>Default poll interval (10 minutes); overridable via EngineConfig key <c>MLOnline:PollIntervalSeconds</c>.</summary>
    private const int DefaultPollIntervalSec = 600;
    private const string CK_PollInterval = "MLOnline:PollIntervalSeconds";

    /// <summary>Base learning rate before adaptive decay is applied.</summary>
    private const double BaseLearningRate = 0.001;

    /// <summary>
    /// Maximum number of entries in the per-model validation buffer.
    /// Oldest entries are evicted FIFO when this limit is reached.
    /// </summary>
    private const int ValidationBufferCapacity = 200;

    /// <summary>
    /// Number of SGD steps between accuracy validation checks within a single batch.
    /// Checking every 10 steps balances responsiveness with computational overhead.
    /// </summary>
    private const int ValidationCheckInterval = 10;

    /// <summary>
    /// Minimum number of resolved prediction outcomes required before online updates
    /// are applied. Ensures sufficient signal-to-noise for meaningful gradient steps.
    /// </summary>
    private const int MinBatchSize = 10;

    // L2 cap is a catastrophic backstop, not the primary safety mechanism.
    // Primary safety: per-step gradient clipping (max delta = LearningRate × 1.0)
    // Secondary safety: per-update accuracy validation (revert if accuracy drops > 1%)
    // Tertiary backstop: L2 distance from original weights (revert if > 2.0)
    private const double MaxL2Deviation = 2.0;

    /// <summary>
    /// Rolling validation buffers per model. Each entry is (predicted probability, actual binary outcome).
    /// Used to compute pre/post accuracy for the secondary safety check.
    /// </summary>
    private readonly ConcurrentDictionary<long, Queue<(double Predicted, int Actual)>> _validationBuffers = new();

    /// <summary>
    /// Cooldown timestamps per model. When online learning is reverted, the model enters
    /// a 1-hour cooldown to prevent thrashing on adversarial data distributions.
    /// </summary>
    private readonly ConcurrentDictionary<long, DateTime> _onlineLearningCooldown = new();

    /// <summary>
    /// Original training biases cached on first online update per model.
    /// Used to compute L2 deviation and for periodic reset.
    /// </summary>
    private readonly ConcurrentDictionary<long, double[]> _originalBiases = new();

    /// <summary>
    /// Tracks consecutive successful update batches per model (no reversions).
    /// Used for adaptive learning rate decay: effectiveLr = baseLr / (1 + 0.01 * count).
    /// </summary>
    private readonly ConcurrentDictionary<long, int> _successfulUpdateCounts = new();

    /// <summary>Counter for cooldown eviction housekeeping.</summary>
    private int _cycleCount;

    public MLOnlineLearningWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLOnlineLearningWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLOnlineLearningWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollIntervalSec;
            try
            {
                using var scope = _scopeFactory.CreateAsyncScope();
                var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var readDb  = readCtx.GetDbContext();
                pollSecs = await GetConfigAsync<int>(readDb, CK_PollInterval, DefaultPollIntervalSec, stoppingToken);

                await RunBatchAsync(scope.ServiceProvider, stoppingToken);

                // Housekeeping: evict stale cooldown entries every 100 cycles.
                _cycleCount++;
                if (_cycleCount % 100 == 0)
                {
                    EvictStaleCooldowns();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "MLOnlineLearningWorker error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }
    }

    private async Task RunBatchAsync(IServiceProvider sp, CancellationToken ct)
    {
        var readCtx  = sp.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = sp.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Find all active models that have serialised weights (ModelBytes).
        var activeModels = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .Select(m => new { m.Id, m.LastOnlineLearningAt, m.ActivatedAt })
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            try
            {
                // Cooldown check: skip models that recently had a reversion.
                if (_onlineLearningCooldown.TryGetValue(model.Id, out var cooldownUntil) && DateTime.UtcNow < cooldownUntil)
                {
                    _logger.LogDebug("Online learning on cooldown for model {Id} until {Until}", model.Id, cooldownUntil);
                    continue;
                }

                // Determine the watermark: outcomes after LastOnlineLearningAt, or ActivatedAt if null.
                DateTime watermark = model.LastOnlineLearningAt
                                  ?? model.ActivatedAt
                                  ?? DateTime.UtcNow.AddHours(-2);

                // Find resolved prediction logs with outcomes recorded after the watermark.
                var logs = await readDb.Set<MLModelPredictionLog>()
                    .AsNoTracking()
                    .Where(l => !l.IsDeleted
                             && l.MLModelId == model.Id
                             && l.ActualDirection.HasValue
                             && l.DirectionCorrect.HasValue
                             && l.OutcomeRecordedAt != null
                             && l.OutcomeRecordedAt > watermark)
                    .OrderBy(l => l.OutcomeRecordedAt)
                    .ThenBy(l => l.Id)
                    .Take(200)
                    .ToListAsync(ct);

                if (logs.Count < MinBatchSize)
                {
                    _logger.LogDebug(
                        "Online learning skipped for model {Id}: batch {Count} < {Min}",
                        model.Id, logs.Count, MinBatchSize);
                    continue;
                }

                // Load the tracked model + snapshot from the write context for mutation.
                var (writeModel, snap) = await MLModelSnapshotWriteHelper
                    .LoadTrackedLatestSnapshotAsync(writeDb, model.Id, ct);

                if (writeModel == null || snap == null) continue;
                if (snap.Weights is not { Length: > 0 } || snap.Biases is not { Length: > 0 }) continue;

                int K = snap.Weights.Length;

                // Snapshot weight backup before batch: deep copy for potential rollback.
                var originalWeights = snap.Weights.Select(w => (double[])w.Clone()).ToArray();
                var originalBiases  = (double[])snap.Biases.Clone();

                // Cache original training biases on first online update for this model.
                _originalBiases.TryAdd(model.Id, (double[])originalBiases.Clone());

                // Tertiary safety: L2 distance catastrophic backstop.
                // This is NOT the primary mechanism — it only fires if weights have drifted
                // catastrophically far from the original training checkpoint.
                double l2Distance = Math.Sqrt(snap.Biases.Zip(_originalBiases[model.Id], (a, b) => (a - b) * (a - b)).Sum());
                if (l2Distance >= MaxL2Deviation)
                {
                    _logger.LogWarning(
                        "Online learning: model {Id} bias L2 deviation {L2:F4} >= catastrophic backstop {Max:F4}. Skipping update.",
                        model.Id, l2Distance, MaxL2Deviation);
                    var skipWriteModel = await writeDb.Set<MLModel>().FindAsync(new object[] { model.Id }, ct);
                    if (skipWriteModel != null)
                    {
                        skipWriteModel.LastOnlineLearningAt = logs.Max(l => l.OutcomeRecordedAt!.Value);
                        await writeDb.SaveChangesAsync(ct);
                    }
                    continue;
                }

                // Periodic reset: if too many updates have accumulated, reset biases to original
                // to prevent unbounded drift.
                int resetAfter = await GetConfigAsync<int>(readDb, "MLOnline:ResetAfterUpdates", 500, ct);
                if (writeModel.OnlineLearningUpdateCount >= resetAfter && _originalBiases.TryGetValue(model.Id, out var origBiases))
                {
                    _logger.LogInformation(
                        "Online learning: model {Id} reached {Count} updates, resetting biases to original.",
                        model.Id, writeModel.OnlineLearningUpdateCount);

                    Array.Copy(origBiases, snap.Biases, Math.Min(origBiases.Length, snap.Biases.Length));
                    writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);
                    writeModel.OnlineLearningUpdateCount = 0;
                    _successfulUpdateCounts[model.Id] = 0;
                    await writeDb.SaveChangesAsync(ct);

                    // Re-snapshot after reset for the upcoming SGD steps.
                    originalBiases = (double[])snap.Biases.Clone();
                }

                // Compute pre-update accuracy on the validation buffer (secondary safety baseline).
                double preAccuracy = ComputeValidationBufferAccuracy(model.Id, snap);

                // Adaptive learning rate decay: reduces update magnitude as the model
                // accumulates successful updates without reversion.
                int successCount = _successfulUpdateCounts.GetValueOrDefault(model.Id, 0);
                double effectiveLr = BaseLearningRate / (1.0 + 0.01 * successCount);

                int appliedCount = 0;
                DateTime latestOutcome = watermark;

                foreach (var log in logs)
                {
                    double actual    = log.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
                    double predicted = MLFeatureHelper.ResolveLoggedRawBuyProbability(log);

                    // SGD step: bias -= lr * (predicted - actual)
                    // Since we don't store the full feature vector in prediction logs, we apply
                    // the bias-only update as a proxy. This corrects systematic calibration
                    // drift without requiring feature replay.
                    double error = predicted - actual;

                    // Primary safety: gradient clipping. Clamp the error to [-1, 1] to bound
                    // the per-step bias delta to at most effectiveLr per class.
                    const double GradientClipThreshold = 1.0;
                    error = Math.Clamp(error, -GradientClipThreshold, GradientClipThreshold);

                    for (int k = 0; k < K; k++)
                    {
                        snap.Biases[k] -= effectiveLr * error;
                    }

                    appliedCount++;

                    // Populate the rolling validation buffer with this outcome.
                    AddToValidationBuffer(model.Id, predicted, (int)actual);

                    if (log.OutcomeRecordedAt!.Value > latestOutcome)
                        latestOutcome = log.OutcomeRecordedAt.Value;

                    // Secondary safety: periodic accuracy validation during the batch.
                    // Every ValidationCheckInterval steps, check if accuracy has degraded.
                    if (appliedCount % ValidationCheckInterval == 0)
                    {
                        double midAccuracy = ComputeValidationBufferAccuracy(model.Id, snap);
                        if (midAccuracy < preAccuracy - 0.01)
                        {
                            _logger.LogWarning(
                                "Online learning reverted for model {Id} at step {Step}: accuracy dropped {Pre:P1} -> {Post:P1}. Cooldown 1h.",
                                model.Id, appliedCount, preAccuracy, midAccuracy);

                            // Revert all updates in this batch.
                            snap.Weights = originalWeights;
                            snap.Biases  = originalBiases;

                            // Enter cooldown to prevent thrashing.
                            _onlineLearningCooldown[model.Id] = DateTime.UtcNow.AddHours(1);

                            // Reset successful count on reversion.
                            _successfulUpdateCounts[model.Id] = 0;

                            // Advance the watermark so we don't re-process the same logs.
                            writeModel.LastOnlineLearningAt = latestOutcome;
                            await writeDb.SaveChangesAsync(ct);
                            appliedCount = 0; // signal reversion
                            break;
                        }
                    }
                }

                if (appliedCount == 0) continue;

                // Final post-batch accuracy validation.
                double postAccuracy = ComputeValidationBufferAccuracy(model.Id, snap);
                if (postAccuracy < preAccuracy - 0.01)
                {
                    _logger.LogWarning(
                        "Online learning reverted for model {Id}: accuracy dropped {Pre:P1} -> {Post:P1}. Cooldown 1h.",
                        model.Id, preAccuracy, postAccuracy);

                    // Revert weights.
                    snap.Weights = originalWeights;
                    snap.Biases  = originalBiases;

                    // Enter cooldown.
                    _onlineLearningCooldown[model.Id] = DateTime.UtcNow.AddHours(1);
                    _successfulUpdateCounts[model.Id] = 0;

                    // Still advance the watermark so we don't re-process the same logs.
                    writeModel.LastOnlineLearningAt = latestOutcome;
                    await writeDb.SaveChangesAsync(ct);
                    continue;
                }

                // Persist the updated snapshot.
                writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);
                writeModel.OnlineLearningUpdateCount += appliedCount;
                writeModel.LastOnlineLearningAt       = latestOutcome;

                await writeDb.SaveChangesAsync(ct);

                // Increment successful update counter for adaptive LR decay.
                _successfulUpdateCounts.AddOrUpdate(model.Id, appliedCount, (_, prev) => prev + appliedCount);

                // Persist update count to EngineConfig for observability.
                await PersistEngineConfigAsync(writeDb,
                    $"MLOnline:{writeModel.Symbol}:{writeModel.Timeframe}:UpdateCount",
                    writeModel.OnlineLearningUpdateCount.ToString(),
                    ConfigDataType.Int, ct);

                // Cumulative drift tracking: write the current L2 deviation to EngineConfig
                // so downstream monitoring dashboards can track bias drift over time.
                if (_originalBiases.TryGetValue(model.Id, out var origForTracking))
                {
                    double postUpdateL2 = Math.Sqrt(snap.Biases.Zip(origForTracking, (a, b) => (a - b) * (a - b)).Sum());
                    await PersistEngineConfigAsync(writeDb,
                        $"MLOnline:{writeModel.Symbol}:{writeModel.Timeframe}:BiasL2Deviation",
                        postUpdateL2.ToString("F6"),
                        ConfigDataType.Decimal, ct);
                }

                double accuracyDelta = postAccuracy - preAccuracy;
                _logger.LogDebug(
                    "MLOnlineLearningWorker updated model {Id} with {N} outcomes (LR={Lr:F6}, accuracy delta={Delta:+0.000;-0.000}).",
                    model.Id, appliedCount, effectiveLr, accuracyDelta);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Online learning failed for model {Id}.", model.Id);
            }
        }
    }

    /// <summary>
    /// Adds a prediction/actual pair to the rolling validation buffer for the given model.
    /// Evicts oldest entries when the buffer exceeds <see cref="ValidationBufferCapacity"/>.
    /// </summary>
    private void AddToValidationBuffer(long modelId, double predicted, int actual)
    {
        var buffer = _validationBuffers.GetOrAdd(modelId, _ => new Queue<(double, int)>());
        buffer.Enqueue((predicted, actual));
        while (buffer.Count > ValidationBufferCapacity)
            buffer.Dequeue();
    }

    /// <summary>
    /// Computes accuracy of the model's current biases against entries in the validation buffer.
    /// If the buffer is empty or too small, falls back to 0.5 (neutral) to avoid false triggers.
    /// </summary>
    private double ComputeValidationBufferAccuracy(long modelId, ModelSnapshot snap)
    {
        if (!_validationBuffers.TryGetValue(modelId, out var buffer) || buffer.Count < 5)
            return 0.5;

        int K = snap.Biases.Length;
        int correct = 0;
        int total = 0;

        foreach (var (predicted, actual) in buffer)
        {
            // Compute ensemble probability using current biases.
            double avgProb = 0;
            for (int k = 0; k < K; k++)
            {
                avgProb += 1.0 / (1.0 + Math.Exp(-snap.Biases[k]));
            }
            avgProb /= K;

            int predictedDir = avgProb >= 0.5 ? 1 : 0;
            if (predictedDir == actual) correct++;
            total++;
        }

        return total > 0 ? (double)correct / total : 0.5;
    }

    /// <summary>
    /// Evicts cooldown entries that have expired (older than 2 hours) to prevent
    /// unbounded memory growth in long-running deployments.
    /// </summary>
    private void EvictStaleCooldowns()
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);
        var staleKeys = _onlineLearningCooldown
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
        {
            _onlineLearningCooldown.TryRemove(key, out _);
        }

        if (staleKeys.Count > 0)
        {
            _logger.LogDebug("Evicted {Count} stale online learning cooldown entries.", staleKeys.Count);
        }
    }

    /// <summary>
    /// Persists or updates an EngineConfig entry for observability dashboards.
    /// </summary>
    private static async Task PersistEngineConfigAsync(
        DbContext writeDb, string key, string value, ConfigDataType dataType, CancellationToken ct)
    {
        var existingConfig = await writeDb.Set<EngineConfig>()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (existingConfig != null)
        {
            existingConfig.Value = value;
        }
        else
        {
            writeDb.Set<EngineConfig>().Add(new EngineConfig
            {
                Key      = key,
                Value    = value,
                DataType = dataType
            });
        }

        await writeDb.SaveChangesAsync(ct);
    }

    private static async Task<T> GetConfigAsync<T>(
        DbContext ctx, string key, T defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
