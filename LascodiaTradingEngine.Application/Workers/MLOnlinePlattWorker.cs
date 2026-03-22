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
/// Performs daily online Platt scaling updates for active ML models using a sliding window
/// of the most recent 100 resolved prediction logs (Rec #45).
///
/// <b>Platt scaling</b> (Platt, 1999) fits a sigmoid function on top of a classifier's raw
/// scores to produce calibrated probabilities. The sigmoid is parameterised by two scalars
/// (A, B):
///
///   calibP = σ(A × logit(rawScore) + B)
///
/// where logit(p) = log(p / (1 − p)) and σ is the sigmoid function.
/// A = 1, B = 0 is the identity (no correction). A &gt; 1 compresses probabilities toward 0.5;
/// A &lt; 1 stretches them toward the extremes.
///
/// <b>Online SGD update:</b> this worker applies one pass of stochastic gradient descent over
/// the recent log window using a fixed learning rate of 0.01. Each step is:
///
///   err   = calibP − y               (prediction error)
///   A ← A − lr × err × logit(rawP)
///   B ← B − lr × err
///
/// The updated A and B are written directly to <see cref="MLModel.PlattA"/> and
/// <see cref="MLModel.PlattB"/> (columns on the model entity, not in ModelBytes).
///
/// <b>Drift tracking:</b> an Exponential Moving Average (EMA) of |A − A_original| is
/// maintained in <see cref="MLModel.PlattCalibrationDrift"/>:
///
///   newDrift = α × |currentDrift| + (1 − α) × previousEma   (α = 0.1)
///
/// A rising drift EMA indicates the model's calibration is deteriorating and may warrant
/// a full recalibration pass by <see cref="MLRecalibrationWorker"/>.
///
/// The worker updates <see cref="MLModel.OnlineLearningUpdateCount"/> and
/// <see cref="MLModel.LastOnlineLearningAt"/> for audit and monitoring purposes.
/// Runs every 24 hours.
/// </summary>
public sealed class MLOnlinePlattWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLOnlinePlattWorker> _logger;

    // Number of most-recent resolved prediction logs to include in each SGD pass.
    private const int WindowSize = 100;

    // SGD learning rate for the online Platt parameter update.
    // Low enough to prevent large jumps from a single noisy batch.
    private const double Lr = 0.01;

    // EMA smoothing factor for the Platt A-parameter drift signal.
    // α = 0.1 gives a smoothed drift estimate with a half-life of ≈ 6.5 updates.
    private const double EmaAlpha = 0.1;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">Creates scoped DI scopes per run cycle.</param>
    /// <param name="logger">Structured logger for Platt update events.</param>
    public MLOnlinePlattWorker(IServiceScopeFactory scopeFactory, ILogger<MLOnlinePlattWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Main hosted-service loop. Runs every 24 hours, delegating to <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="stoppingToken">Signals graceful shutdown requested by the host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLOnlinePlattWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLOnlinePlattWorker error"); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    /// <summary>
    /// Core online Platt update routine. For each active model with serialised model bytes:
    /// <list type="number">
    ///   <item>Reads current Platt A and B from the deserialized <see cref="ModelSnapshot"/>.</item>
    ///   <item>Loads the last <see cref="WindowSize"/> resolved prediction logs (requires ≥ 30).</item>
    ///   <item>Runs a single SGD pass over the logs, updating A and B in-place.</item>
    ///   <item>Computes the absolute A-parameter drift and updates the EMA drift tracker.</item>
    ///   <item>Persists the new A, B, drift, update count, and timestamp to the model record.</item>
    /// </list>
    ///
    /// Note: the updated A and B are stored on the <see cref="MLModel"/> entity columns
    /// (<c>PlattA</c>, <c>PlattB</c>), not inside <c>ModelBytes</c>. This is intentional:
    /// online Platt updates are lightweight corrections applied between full retrains.
    /// The scoring pipeline reads from these columns with higher priority than the snapshot.
    /// </summary>
    /// <param name="ct">Cooperative cancellation token.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Skip models without serialised weights — no snapshot to read A, B from.
            if (model.ModelBytes == null) continue;

            // Deserialise snapshot to read the current Platt A and B baseline values.
            ModelSnapshot? snap;
            try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes); }
            catch { continue; }
            if (snap == null) continue;

            // Load the most recent resolved prediction logs, ordered newest first.
            var recentLogs = await readDb.Set<MLModelPredictionLog>()
                .Where(p => p.MLModelId == model.Id
                         && p.DirectionCorrect != null
                         && !p.IsDeleted)
                .OrderByDescending(p => p.PredictedAt)
                .Take(WindowSize)
                .ToListAsync(ct);

            // Require at least 30 resolved logs for a statistically meaningful SGD update.
            if (recentLogs.Count < 30) continue;

            // Initialise A and B from the current snapshot values.
            double a = snap.PlattA, b = snap.PlattB;
            double originalA = a; // Save pre-update A for drift computation.

            // Single-pass online SGD over the resolved prediction logs.
            // Processes logs in reverse-chronological order (most recent first).
            foreach (var log in recentLogs)
            {
                double rawP  = (double)log.ConfidenceScore;
                // Compute logit, guarding against edge cases (rawP = 0 or 1 would be ±∞).
                double logit = rawP > 0 && rawP < 1 ? Math.Log(rawP / (1 - rawP)) : 0;
                // Apply current Platt parameters: calibP = σ(A × logit + B)
                double p     = 1.0 / (1 + Math.Exp(-(a * logit + b)));
                double y     = log.DirectionCorrect!.Value ? 1.0 : 0.0;
                // Gradient of cross-entropy loss: ∂L/∂A = err × logit, ∂L/∂B = err
                double err   = p - y;
                a -= Lr * err * logit; // update A: scale parameter
                b -= Lr * err;         // update B: bias parameter
            }

            // Drift = absolute change in A parameter from before to after the SGD pass.
            // Large drift indicates the model's raw outputs have shifted significantly.
            double drift = Math.Abs(a - originalA);

            // EMA drift tracker: smoothed exponential moving average of per-update drift.
            // newDrift = α × currentDrift + (1 − α) × previousEma
            double newDrift = model.PlattCalibrationDrift.HasValue
                ? EmaAlpha * drift + (1 - EmaAlpha) * model.PlattCalibrationDrift.Value
                : drift; // first-time initialisation: use raw drift

            // Persist the updated Platt parameters and drift signal on the model entity.
            var writeModel = await writeDb.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == model.Id && !m.IsDeleted, ct);
            if (writeModel == null) continue;

            writeModel.PlattA                = (decimal)a;
            writeModel.PlattB                = (decimal)b;
            writeModel.PlattCalibrationDrift = newDrift;
            writeModel.OnlineLearningUpdateCount++;      // audit counter
            writeModel.LastOnlineLearningAt  = DateTime.UtcNow;

            _logger.LogDebug("MLOnlinePlattWorker: {S}/{T} PlattA={A:F4} PlattB={B:F4} Drift={D:F4}",
                model.Symbol, model.Timeframe, a, b, newDrift);
        }
        await writeDb.SaveChangesAsync(ct);
    }
}
