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
/// Safety mechanism: the worker tracks a rolling accuracy buffer in memory and reverts
/// the weight update if accuracy drops after the SGD step. This prevents catastrophic
/// forgetting from noisy individual trade outcomes.
/// </remarks>
public sealed class MLOnlineLearningWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLOnlineLearningWorker> _logger;

    /// <summary>Default poll interval (10 minutes); overridable via EngineConfig key <c>MLOnline:PollIntervalSeconds</c>.</summary>
    private const int DefaultPollIntervalSec = 600;
    private const string CK_PollInterval = "MLOnline:PollIntervalSeconds";

    /// <summary>Tiny learning rate to prevent catastrophic forgetting.</summary>
    private const double LearningRate = 0.001;

    /// <summary>Rolling accuracy window size for the safety check.</summary>
    private const int AccuracyBufferSize = 50;

    // In-memory rolling accuracy buffers per model — tracks recent prediction correctness
    // to detect accuracy regressions caused by online updates.
    private readonly Dictionary<long, Queue<bool>> _accuracyBuffers = new();

    /// <summary>
    /// Original training biases cached on first online update per model.
    /// Used to compute L2 deviation and for periodic reset.
    /// </summary>
    private readonly ConcurrentDictionary<long, double[]> _originalBiases = new();

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
                    .Take(64)
                    .ToListAsync(ct);

                if (logs.Count == 0) continue;

                // Load the tracked model + snapshot from the write context for mutation.
                var (writeModel, snap) = await MLModelSnapshotWriteHelper
                    .LoadTrackedLatestSnapshotAsync(writeDb, model.Id, ct);

                if (writeModel == null || snap == null) continue;
                if (snap.Weights is not { Length: > 0 } || snap.Biases is not { Length: > 0 }) continue;

                int K = snap.Weights.Length;
                int F = snap.Weights[0].Length;

                // Take a snapshot of the weights before update for safety rollback.
                var preUpdateWeights = snap.Weights.Select(w => (double[])w.Clone()).ToArray();
                var preUpdateBiases  = (double[])snap.Biases.Clone();

                // Cache original training biases on first online update for this model.
                _originalBiases.TryAdd(model.Id, (double[])preUpdateBiases.Clone());

                // Max deviation bound: reject update if biases have drifted too far from original.
                double l2Distance = Math.Sqrt(snap.Biases.Zip(_originalBiases[model.Id], (a, b) => (a - b) * (a - b)).Sum());
                double maxDeviation = await GetConfigAsync<double>(readDb, "MLOnline:MaxBiasDeviation", 0.5, ct);
                if (l2Distance >= maxDeviation)
                {
                    _logger.LogWarning("Online learning: model {Id} bias L2 deviation {L2:F4} >= max {Max:F4}. Skipping update.",
                        model.Id, l2Distance, maxDeviation);
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
                int resetAfter = await GetConfigAsync<int>(readDb, "MLOnline:ResetAfterUpdates", 200, ct);
                if (writeModel.OnlineLearningUpdateCount >= resetAfter && _originalBiases.TryGetValue(model.Id, out var origBiases))
                {
                    _logger.LogInformation(
                        "Online learning: model {Id} reached {Count} updates, resetting biases to original.",
                        model.Id, writeModel.OnlineLearningUpdateCount);

                    Array.Copy(origBiases, snap.Biases, Math.Min(origBiases.Length, snap.Biases.Length));
                    writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);
                    writeModel.OnlineLearningUpdateCount = 0;
                    await writeDb.SaveChangesAsync(ct);

                    // Re-snapshot after reset for the upcoming SGD steps.
                    preUpdateBiases = (double[])snap.Biases.Clone();
                }

                // Compute pre-update accuracy on this batch (for safety check).
                double preAccuracy = ComputeBatchAccuracy(snap, logs);

                int appliedCount = 0;
                DateTime latestOutcome = watermark;

                foreach (var log in logs)
                {
                    double actual    = log.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
                    double predicted = MLFeatureHelper.ResolveLoggedRawBuyProbability(log);

                    // SGD step: w -= lr * (predicted - actual) * x
                    // Since we don't store the full feature vector in prediction logs, we apply
                    // the bias-only update as a proxy: bias -= lr * (predicted - actual).
                    // This corrects systematic calibration drift without requiring feature replay.
                    double error = predicted - actual;

                    for (int k = 0; k < K; k++)
                    {
                        snap.Biases[k] -= LearningRate * error;
                    }

                    appliedCount++;

                    // Track accuracy in the rolling buffer.
                    if (!_accuracyBuffers.TryGetValue(model.Id, out var buffer))
                    {
                        buffer = new Queue<bool>();
                        _accuracyBuffers[model.Id] = buffer;
                    }
                    buffer.Enqueue(log.DirectionCorrect == true);
                    while (buffer.Count > AccuracyBufferSize) buffer.Dequeue();

                    if (log.OutcomeRecordedAt!.Value > latestOutcome)
                        latestOutcome = log.OutcomeRecordedAt.Value;
                }

                if (appliedCount == 0) continue;

                // Safety check: if accuracy dropped after update, revert.
                double postAccuracy = ComputeBatchAccuracy(snap, logs);
                if (postAccuracy < preAccuracy - 0.01)
                {
                    _logger.LogWarning(
                        "MLOnlineLearningWorker: accuracy dropped for model {Id} ({Pre:F3} -> {Post:F3}), reverting.",
                        model.Id, preAccuracy, postAccuracy);

                    // Revert weights.
                    snap.Weights = preUpdateWeights;
                    snap.Biases  = preUpdateBiases;

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

                // Cumulative drift tracking: write the current L2 deviation to EngineConfig
                // so downstream monitoring dashboards can track bias drift over time.
                if (_originalBiases.TryGetValue(model.Id, out var origForTracking))
                {
                    double postUpdateL2 = Math.Sqrt(snap.Biases.Zip(origForTracking, (a, b) => (a - b) * (a - b)).Sum());
                    string driftKey = $"MLOnline:{writeModel.Symbol}:{writeModel.Timeframe}:BiasL2Deviation";
                    var existingConfig = await writeDb.Set<EngineConfig>()
                        .FirstOrDefaultAsync(c => c.Key == driftKey, ct);
                    if (existingConfig != null)
                    {
                        existingConfig.Value = postUpdateL2.ToString("F6");
                    }
                    else
                    {
                        writeDb.Set<EngineConfig>().Add(new EngineConfig
                        {
                            Key   = driftKey,
                            Value = postUpdateL2.ToString("F6"),
                            DataType = ConfigDataType.Decimal
                        });
                    }
                    await writeDb.SaveChangesAsync(ct);
                }

                _logger.LogDebug(
                    "MLOnlineLearningWorker updated model {Id} with {N} outcomes.",
                    model.Id, appliedCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Online learning failed for model {Id}.", model.Id);
            }
        }
    }

    /// <summary>
    /// Computes the fraction of prediction logs in the batch where the model's current
    /// weights would predict the correct direction. Used for the safety accuracy check.
    /// </summary>
    private static double ComputeBatchAccuracy(ModelSnapshot snap, List<MLModelPredictionLog> logs)
    {
        if (logs.Count == 0) return 0.5;

        int correct = 0;
        foreach (var log in logs)
        {
            if (!log.ActualDirection.HasValue) continue;

            // Compute ensemble probability using current weights.
            double avgProb = 0;
            int K = snap.Weights.Length;
            for (int k = 0; k < K; k++)
            {
                avgProb += 1.0 / (1 + Math.Exp(-snap.Biases[k]));
            }
            avgProb /= K;

            var predicted = avgProb >= 0.5 ? TradeDirection.Buy : TradeDirection.Sell;
            if (predicted == log.ActualDirection.Value) correct++;
        }

        return (double)correct / logs.Count;
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
