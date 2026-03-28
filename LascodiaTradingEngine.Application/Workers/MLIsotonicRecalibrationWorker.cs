using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Refits the isotonic (PAVA) calibration curve for each active ML model using recent
/// resolved <see cref="MLModelPredictionLog"/> records, then writes the updated
/// <see cref="ModelSnapshot.IsotonicBreakpoints"/> back to the database without requiring
/// a full retrain.
///
/// At training time, isotonic breakpoints are fitted on a held-out calibration set whose
/// distribution matches the training period. In live trading the predicted-probability
/// distribution can drift, causing the calibrated probabilities to over- or under-estimate
/// the true empirical accuracy. This worker corrects that drift between retrains.
///
/// Algorithm:
/// <list type="number">
///   <item>Reconstruct approximate calibP for each resolved prediction:
///         <list type="bullet">
///           <item>Buy:  calibP ≈ threshold + ConfidenceScore × (1 − threshold)</item>
///           <item>Sell: calibP ≈ threshold − ConfidenceScore × threshold</item>
///         </list>
///         where <c>threshold</c> = snapshot's OptimalThreshold (or 0.5 if unset).
///   </item>
///   <item>Sort (calibP, label) pairs by calibP ascending.</item>
///   <item>Apply the Pool Adjacent Violators Algorithm (PAVA) to fit a non-decreasing
///         calibration curve that maps calibP → empirical accuracy.</item>
///   <item>Store the resulting breakpoints as the new
///         <see cref="ModelSnapshot.IsotonicBreakpoints"/>.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLIsotonicRecal:PollIntervalSeconds</c> — default 28800 (8 h)</item>
///   <item><c>MLIsotonicRecal:WindowDays</c>          — look-back window, default 30</item>
///   <item><c>MLIsotonicRecal:MinResolved</c>          — skip if fewer records, default 50</item>
/// </list>
/// </summary>
public sealed class MLIsotonicRecalibrationWorker : BackgroundService
{
    private const string CK_PollSecs    = "MLIsotonicRecal:PollIntervalSeconds";
    private const string CK_WindowDays  = "MLIsotonicRecal:WindowDays";
    private const string CK_MinResolved = "MLIsotonicRecal:MinResolved";

    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";

    private readonly IServiceScopeFactory                     _scopeFactory;
    private readonly IMemoryCache                             _cache;
    private readonly ILogger<MLIsotonicRecalibrationWorker>   _logger;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">Creates scoped DI scopes per polling cycle.</param>
    /// <param name="cache">
    /// Shared in-memory cache. The <c>MLSnapshot:{modelId}</c> entry is evicted after
    /// a successful update so the scorer reloads the patched breakpoints immediately.
    /// </param>
    /// <param name="logger">Structured logger.</param>
    public MLIsotonicRecalibrationWorker(
        IServiceScopeFactory                    scopeFactory,
        IMemoryCache                            cache,
        ILogger<MLIsotonicRecalibrationWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        _logger       = logger;
    }

    /// <summary>
    /// Main hosted-service loop. Polls every <c>MLIsotonicRecal:PollIntervalSeconds</c>
    /// (default 28800 s / 8 h). Creates a fresh DI scope per cycle and delegates to
    /// <see cref="RecalibrateModelsAsync"/>.
    /// </summary>
    /// <param name="stoppingToken">Signals graceful shutdown requested by the host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLIsotonicRecalibrationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval — refreshed from live EngineConfig each cycle.
            int pollSecs = 28800;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 28800, stoppingToken);

                await RecalibrateModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLIsotonicRecalibrationWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLIsotonicRecalibrationWorker stopping.");
    }

    /// <summary>
    /// Loads window/sample configuration from <see cref="EngineConfig"/> and iterates
    /// over every active model with serialised model bytes, delegating per-model work to
    /// <see cref="RecalibrateModelAsync"/>. Errors for individual models are swallowed so
    /// one bad model cannot block the remaining models.
    /// </summary>
    private async Task RecalibrateModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int windowDays  = await GetConfigAsync<int>(readCtx, CK_WindowDays,  30, ct);
        int minResolved = await GetConfigAsync<int>(readCtx, CK_MinResolved, 50, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await RecalibrateModelAsync(model, readCtx, writeCtx, windowDays, minResolved, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Isotonic recalibration failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    /// <summary>
    /// Refits the isotonic calibration breakpoints for a single model using the PAVA
    /// algorithm applied to recent resolved prediction logs.
    ///
    /// <b>Probability reconstruction:</b> Because raw logits are not stored in prediction logs,
    /// the calibrated probability is approximated from <c>ConfidenceScore</c>,
    /// <c>PredictedDirection</c>, and the model's current decision threshold T:
    /// <list type="bullet">
    ///   <item>Buy:  calibP = T + conf × (1 − T)  → maps conf ∈ [0,1] to calibP ∈ [T, 1]</item>
    ///   <item>Sell: calibP = T − conf × T          → maps conf ∈ [0,1] to calibP ∈ [0, T]</item>
    /// </list>
    ///
    /// This ensures the reconstructed probability is consistent with the model's threshold and
    /// maps confidence scores to the correct side of the probability space.
    ///
    /// The resulting (calibP, label) pairs are sorted and fed into <see cref="FitPAVA"/>.
    /// The new breakpoints replace the existing ones in <c>ModelSnapshot.IsotonicBreakpoints</c>
    /// and are written back to <c>MLModel.ModelBytes</c>.
    /// </summary>
    /// <param name="model">Active model entity to refine isotonic breakpoints for.</param>
    /// <param name="readCtx">Read-only EF context.</param>
    /// <param name="writeCtx">Write EF context for persisting the patched snapshot.</param>
    /// <param name="windowDays">Look-back window in days for resolved logs.</param>
    /// <param name="minResolved">Minimum resolved-log count required to proceed (skip if fewer).</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    private async Task RecalibrateModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minResolved,
        CancellationToken                       ct)
    {
        // Deserialise the current snapshot to extract the model's active decision threshold.
        ModelSnapshot? snap;
        try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }

        if (snap is null) return;

        double threshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snap);

        var since = DateTime.UtcNow.AddDays(-windowDays);

        // Load only the fields needed for PAVA fitting to minimise data transfer.
        var resolved = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.ActualDirection  != null     &&
                        l.DirectionCorrect != null     &&
                        !l.IsDeleted)
            .Select(l => new
            {
                l.PredictedDirection,
                l.ConfidenceScore,
                l.RawProbability,
                l.CalibratedProbability,
                l.DecisionThresholdUsed,
                l.EnsembleDisagreement,
                l.ActualDirection,
            })
            .ToListAsync(ct);

        if (resolved.Count < minResolved)
        {
            _logger.LogDebug(
                "IsotonicRecal: {Symbol}/{Tf} model {Id} only {N} resolved (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, resolved.Count, minResolved);
            return;
        }

        // Reconstruct the logged buy-probability using the same threshold-relative
        // confidence contract as the live scorer, then sort ascending for PAVA.
        var pairs = resolved
            .Select(r =>
            {
                double calibP = MLFeatureHelper.ResolveLoggedCalibratedBuyProbability(
                    new MLModelPredictionLog
                    {
                        PredictedDirection = r.PredictedDirection,
                        ConfidenceScore = r.ConfidenceScore,
                        RawProbability = r.RawProbability,
                        CalibratedProbability = r.CalibratedProbability,
                        DecisionThresholdUsed = r.DecisionThresholdUsed,
                        EnsembleDisagreement = r.EnsembleDisagreement,
                        ActualDirection = r.ActualDirection,
                    },
                    threshold);
                double label  = r.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
                return (P: calibP, Y: label);
            })
            .OrderBy(p => p.P)  // PAVA requires sorted input
            .ToList();

        double[] newBreakpoints = FitPAVA(pairs);

        // Require at least 2 breakpoint segments (4 values) to be useful.
        if (newBreakpoints.Length < 4)
        {
            _logger.LogDebug(
                "IsotonicRecal: {Symbol}/{Tf} model {Id} PAVA produced < 2 segments — skip.",
                model.Symbol, model.Timeframe, model.Id);
            return;
        }

        _logger.LogInformation(
            "IsotonicRecal: {Symbol}/{Tf} model {Id}: fitted {N} PAVA breakpoints from {Count} samples.",
            model.Symbol, model.Timeframe, model.Id, newBreakpoints.Length / 2, resolved.Count);

        // Overwrite the existing isotonic breakpoints with the freshly fitted ones.
        snap.IsotonicBreakpoints = newBreakpoints;
        byte[] updatedBytes = JsonSerializer.SerializeToUtf8Bytes(snap);

        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == model.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ModelBytes, updatedBytes), ct);

        // Evict the scorer cache so the updated breakpoints are used immediately.
        _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");
    }

    // ── PAVA (Pool Adjacent Violators Algorithm) ──────────────────────────────

    /// <summary>
    /// Fits a monotone non-decreasing calibration curve via PAVA.
    /// Returns interleaved [x₀,y₀,x₁,y₁,…] breakpoints compatible with
    /// <see cref="BaggedLogisticTrainer.ApplyIsotonicCalibration"/>.
    ///
    /// <b>PAVA algorithm:</b> processes the sorted (P, Y) pairs left to right.
    /// Each pair is added as a new block. Whenever the new block's mean Y (accuracy)
    /// is less than the previous block's mean Y, the two blocks are merged into one
    /// combined block with pooled statistics. This merging is repeated until the
    /// non-decreasing invariant is restored. The result is a staircase function from
    /// probability to accuracy that is guaranteed monotone non-decreasing.
    ///
    /// <b>Output format:</b> the interleaved array [x₀,y₀, x₁,y₁, …] matches the
    /// format expected by <c>BaggedLogisticTrainer.ApplyIsotonicCalibration</c>, where
    /// x_i is the mean calibrated probability for block i and y_i is the mean accuracy
    /// (empirical label rate) for that block.
    /// </summary>
    /// <param name="pairs">
    /// (calibP, label) pairs sorted ascending by calibP.
    /// Returns empty array when fewer than 10 pairs are provided.
    /// </param>
    /// <returns>
    /// Interleaved [x, y] breakpoints where x = mean calibP, y = mean empirical accuracy.
    /// </returns>
    private static double[] FitPAVA(List<(double P, double Y)> pairs)
    {
        if (pairs.Count < 10) return [];

        // Stack-based PAVA: each entry = (sumY, sumP, count).
        // The mean accuracy of a block is sumY / count.
        // The mean probability of a block is sumP / count.
        var stack = new List<(double SumY, double SumP, int Count)>(pairs.Count);
        foreach (var (P, Y) in pairs)
        {
            // Push each new point as a singleton block.
            stack.Add((Y, P, 1));
            // Merge back while the monotone non-decreasing invariant is violated.
            while (stack.Count >= 2)
            {
                var last = stack[^1];
                var prev = stack[^2];
                // Violation: previous block has higher mean accuracy than the current block.
                if (prev.SumY / prev.Count > last.SumY / last.Count)
                {
                    // Pool the two blocks: combine their sums and counts.
                    stack.RemoveAt(stack.Count - 1);
                    stack[^1] = (prev.SumY + last.SumY,
                                 prev.SumP + last.SumP,
                                 prev.Count + last.Count);
                }
                else break; // Non-decreasing invariant satisfied — stop merging.
            }
        }

        // Build interleaved [x,y] breakpoints: x = mean calibP, y = mean label.
        var bp = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            bp[i * 2]     = stack[i].SumP / stack[i].Count; // x: mean probability for block i
            bp[i * 2 + 1] = stack[i].SumY / stack[i].Count; // y: mean accuracy for block i
        }
        return bp;
    }

    // ── Config helper ─────────────────────────────────────────────────────────

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
