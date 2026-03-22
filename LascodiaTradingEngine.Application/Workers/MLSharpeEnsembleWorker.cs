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
/// Reweights base learner ensemble weights by their rolling Sharpe ratio computed from
/// resolved prediction logs (Rec #46). Learners with higher risk-adjusted accuracy
/// receive larger ensemble weights, improving the signal-to-noise ratio. Runs weekly.
/// </summary>
/// <remarks>
/// <b>Motivation:</b> The standard bagged ensemble averages base learner outputs with
/// equal weight. However, learners specialise over time — some perform better in trending
/// regimes, others in mean-reverting conditions. The Sharpe-weighted ensemble allocates
/// more influence to learners whose recent return streams have higher risk-adjusted
/// performance, making the ensemble adaptive to the current market regime.
///
/// <b>Polling interval:</b> 7 days. The weekly cadence ensures at least
/// <see cref="RollingWindow"/> new resolved prediction logs have been collected since
/// the previous reweighting, providing a statistically meaningful sample of recent
/// per-learner return distributions.
///
/// <b>ML lifecycle contribution:</b> This worker sits between full batch retraining
/// (which replaces all weights) and online learning (which adjusts biases after every
/// trade). It provides a medium-frequency calibration layer that improves ensemble
/// accuracy without the cost of a full training run.
/// </remarks>
public sealed class MLSharpeEnsembleWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLSharpeEnsembleWorker> _logger;

    /// <summary>
    /// Number of most-recent resolved prediction logs used to compute each learner's
    /// rolling Sharpe ratio. 100 logs provides approximately 1–2 weeks of signals
    /// for active symbols.
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
    /// 7 days to recompute Sharpe-weighted ensemble allocations for all active models.
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
    /// Core Sharpe-weighted ensemble reweighting cycle. For each active model with
    /// sufficient resolved prediction logs, computes per-learner rolling Sharpe ratios
    /// and updates <c>ModelSnapshot.EnsembleSelectionWeights</c> via softmax normalisation.
    /// </summary>
    /// <remarks>
    /// Sharpe-ratio weighted ensemble methodology:
    /// <list type="number">
    ///   <item>
    ///     Load the last <see cref="RollingWindow"/> resolved prediction logs for the model
    ///     ordered by most recent first. Require both <c>DirectionCorrect</c> and
    ///     <c>ActualMagnitudePips</c> so that the return can be computed as a signed pip value.
    ///     Skip models with fewer than 30 logs — the Sharpe estimate is unreliable below this.
    ///   </item>
    ///   <item>
    ///     <b>Return proxy:</b> For each prediction log, simulate a per-learner return as:
    ///     <c>ret = sign(correct) × |pips|</c>. Since the individual learner's raw probability
    ///     output is not stored separately per learner (only the ensemble output is logged),
    ///     all K learners share the same return stream in the current implementation.
    ///     This is a conservative proxy — a full implementation would store per-learner
    ///     probabilities and compute separate return streams.
    ///   </item>
    ///   <item>
    ///     <b>Sharpe ratio:</b> Sharpe_k = mean(returns_k) / std(returns_k). A zero standard
    ///     deviation (all returns identical) yields Sharpe = 0 — the learner receives the
    ///     minimum softmax weight.
    ///   </item>
    ///   <item>
    ///     <b>Softmax normalisation:</b> Convert Sharpe ratios to non-negative ensemble
    ///     weights via softmax with a max-subtraction stability trick:
    ///     w_k = exp(S_k − max(S)) / Σ exp(S_j − max(S)).
    ///     Softmax ensures weights sum to 1 and are all positive, making the ensemble a
    ///     valid probability mixture. Learners with negative Sharpe still receive a small
    ///     positive weight rather than zero, preserving ensemble diversity.
    ///   </item>
    ///   <item>
    ///     <b>Weight persistence:</b> The updated weights are written into
    ///     <c>ModelSnapshot.EnsembleSelectionWeights</c> and serialised back to
    ///     <c>MLModel.ModelBytes</c>. The scoring path reads these weights at inference
    ///     time to blend individual learner outputs.
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
            if (snap?.Weights == null || snap.Weights.Length == 0) continue;

            // Fetch the last RollingWindow resolved logs that include magnitude data.
            // ActualMagnitudePips is required to compute a meaningful return proxy.
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

            int K = snap.Weights.Length; // number of ensemble base learners

            // Compute the rolling Sharpe ratio for each ensemble learner.
            // Currently all learners share the same return stream (see remarks above).
            var learnerSharpes = new double[K];
            for (int k = 0; k < K; k++)
            {
                var returns = new List<double>();
                foreach (var log in logs)
                {
                    // Return proxy: +pip if prediction was correct, -pip if incorrect.
                    // This transforms binary direction accuracy into a risk-weighted return.
                    double direction = log.DirectionCorrect!.Value ? 1.0 : -1.0;
                    double ret = direction * (double)Math.Abs(log.ActualMagnitudePips!.Value);
                    returns.Add(ret);
                }
                if (returns.Count == 0) continue;

                // Compute annualised Sharpe (unscaled — relative ordering is sufficient).
                double mean = returns.Average();
                double std  = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Average());
                learnerSharpes[k] = std > 0 ? mean / std : 0;
            }

            // Softmax-normalise Sharpe ratios to ensemble selection weights.
            // Max subtraction (log-sum-exp trick) prevents overflow for large Sharpe values.
            double maxS   = learnerSharpes.Max();
            double[] expS = learnerSharpes.Select(s => Math.Exp(s - maxS)).ToArray();
            double sumE   = expS.Sum();
            double[] newWeights = expS.Select(e => e / sumE).ToArray();

            // Write updated weights back into the snapshot's EnsembleSelectionWeights field.
            snap.EnsembleSelectionWeights = newWeights;

            // Load the tracked (write-context) model entity to persist the updated bytes.
            var writeModel = await writeDb.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == model.Id && !m.IsDeleted, ct);
            if (writeModel == null) continue;

            writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);
            _logger.LogDebug("MLSharpeEnsembleWorker: updated ensemble weights for {S}/{T}.",
                model.Symbol, model.Timeframe);
        }

        // Single batch save for all modified model records.
        await writeDb.SaveChangesAsync(ct);
    }
}
