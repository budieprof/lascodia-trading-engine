using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Maintains meaningful ensemble-selection weights for active models without overwriting
/// them from learner-agnostic live returns. Runs weekly.
/// </summary>
/// <remarks>
/// <b>Motivation:</b> The live prediction log currently stores only ensemble-level outcomes,
/// not per-learner returns. Recomputing weekly Sharpe ratios from that shared return stream
/// collapses every learner to the same score and can overwrite informative training-time
/// weights with a meaningless uniform vector.
///
/// Until learner-level production attribution is persisted, this worker takes the safe path:
/// it preserves existing ensemble weights and only backfills
/// <see cref="ModelSnapshot.EnsembleSelectionWeights"/> from
/// <see cref="ModelSnapshot.LearnerAccuracyWeights"/> when the deployed snapshot is missing
/// a valid selection-weight vector.
///
/// <b>ML lifecycle contribution:</b> This worker sits between full batch retraining
/// (which replaces all weights) and online learning (which adjusts biases after every
/// trade). In the current schema it acts as a guardrail against harmful weekly weight
/// rewrites rather than a true learner-level Sharpe optimizer.
/// </remarks>
public sealed class MLSharpeEnsembleWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLSharpeEnsembleWorker> _logger;

    /// <summary>
    /// Number of most-recent resolved prediction logs that would be required for future
    /// learner-level attribution. Retained here as the minimum evidence threshold before
    /// any backfill is considered.
    /// </summary>
    private const int RollingWindow = 100;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per weekly reweighting cycle so scoped EF Core
    /// contexts are correctly disposed after each run.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLSharpeEnsembleWorker(IServiceScopeFactory scopeFactory, ILogger<MLSharpeEnsembleWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Executes immediately on startup then re-runs every
    /// 7 days to maintain safe ensemble-selection weights for all active models.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSharpeEnsembleWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLSharpeEnsembleWorker error"); }
            await Task.Delay(TimeSpan.FromDays(7), stoppingToken);
        }
    }

    /// <summary>
    /// Core weekly maintenance cycle. For each active model with enough resolved logs,
    /// preserves any valid existing selection weights and, when absent, backfills them
    /// from stored learner-accuracy weights rather than fabricating pseudo-Sharpe weights
    /// from learner-agnostic returns.
    /// </summary>
    /// <remarks>
    /// Safe maintenance methodology:
    /// <list type="number">
    ///   <item>
    ///     Load the last <see cref="RollingWindow"/> resolved prediction logs for the model
    ///     ordered by most recent first. Require at least 30 logs before changing anything.
    ///   </item>
    ///   <item>
    ///     If <c>EnsembleSelectionWeights</c> is already present with the correct length
    ///     and a positive weight sum, keep it unchanged.
    ///   </item>
    ///   <item>
    ///     Otherwise, if <c>LearnerAccuracyWeights</c> is present with the correct length,
    ///     normalise it to sum to 1 and persist it as the ensemble-selection vector.
    ///     This keeps deployed weights aligned with the best learner-specific information
    ///     the snapshot currently contains.
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Load all active models that have a serialised ModelSnapshot.
        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            if (model.ModelBytes == null) continue;

            // Deserialise the snapshot to access current weights and ensemble structure.
            ModelSnapshot? snap;
            try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes); }
            catch { continue; }
            if (snap?.Weights == null || snap.Weights.Length < 2) continue;

            var logs = await readDb.Set<MLModelPredictionLog>()
                .Where(p => p.MLModelId == model.Id
                         && p.DirectionCorrect != null
                         && p.ActualMagnitudePips != null
                         && !p.IsDeleted)
                .OrderByDescending(p => p.PredictedAt)
                .Take(RollingWindow)
                .ToListAsync(ct);

            // Require at least 30 samples for a statistically reliable Sharpe estimate.
            if (logs.Count < 30) continue;

            int K = snap.Weights.Length;

            if (HasValidWeightVector(snap.EnsembleSelectionWeights, K))
            {
                _logger.LogDebug(
                    "MLSharpeEnsembleWorker: preserving existing ensemble weights for {S}/{T}; live logs do not provide learner-level returns.",
                    model.Symbol, model.Timeframe);
                continue;
            }

            if (!HasValidWeightVector(snap.LearnerAccuracyWeights, K))
            {
                _logger.LogDebug(
                    "MLSharpeEnsembleWorker: skipped {S}/{T}; no learner-level live attribution and no stored learner-accuracy weights to backfill.",
                    model.Symbol, model.Timeframe);
                continue;
            }

            double sum = snap.LearnerAccuracyWeights.Sum();
            snap.EnsembleSelectionWeights = snap.LearnerAccuracyWeights
                .Select(w => w / sum)
                .ToArray();

            var writeModel = await writeDb.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == model.Id && !m.IsDeleted, ct);
            if (writeModel == null) continue;

            writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);
            _logger.LogDebug("MLSharpeEnsembleWorker: backfilled ensemble weights from learner-accuracy weights for {S}/{T}.",
                model.Symbol, model.Timeframe);
        }

        // Single batch save for all modified model records.
        await writeDb.SaveChangesAsync(ct);
    }

    private static bool HasValidWeightVector(double[]? weights, int expectedLength) =>
        weights is { Length: > 0 } &&
        weights.Length == expectedLength &&
        weights.Sum() > 1e-10;
}
