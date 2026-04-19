using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes a live-performance-based Kelly fraction multiplier for each active ML model
/// and writes it to <see cref="EngineConfig"/>, allowing <c>MLSignalScorer</c> to
/// proportionally scale down bet sizing when a model is underperforming its training
/// accuracy — without triggering full suppression.
///
/// <para>
/// <b>Position sizing algorithm (Kelly-inspired adaptive multiplier):</b>
/// The classical Kelly criterion sizes bets as f = (bp − q) / b, where p is win probability,
/// q = 1 − p, and b is the payout ratio. This worker applies a simplified, monotone
/// approximation: if the model's live accuracy has drifted below training accuracy, the
/// position size is proportionally reduced. The formula is:
/// <code>
///   multiplier = clamp(liveAccuracy / trainingAccuracy, MinMultiplier, 1.0)
/// </code>
/// A model scoring 90% of its training accuracy gets a 0.90 multiplier applied to all
/// downstream bet sizes. At or above training accuracy the multiplier is 1.0 (no reduction).
/// The <c>MinMultiplier</c> floor (default 0.50) prevents near-zero sizing that would
/// make signals economically meaningless.
/// </para>
///
/// <para>
/// <b>Distinction from existing mechanisms:</b>
/// <list type="bullet">
///   <item>The BSS-based Kelly multiplier in <c>MLSignalScorer</c> step 13b uses the
///         <em>training-time</em> Brier Skill Score — a static quality measure frozen
///         at training time that does not adapt to live performance.</item>
///   <item>This worker computes a <em>live</em> multiplier from resolved prediction logs
///         in the rolling window, so it responds dynamically to in-market model drift.</item>
///   <item>When insufficient data is available (fewer than <c>MinSamples</c> resolved logs),
///         the multiplier defaults to 1.0 (benefit of the doubt) rather than 0 — the model
///         is not penalized for having been recently deployed.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Polling interval:</b> 3600 seconds (1 hour) by default. The hourly update ensures
/// position sizing reflects recent performance while avoiding excessive write pressure on
/// the EngineConfig table.
/// </para>
///
/// <para>
/// <b>ML lifecycle contribution:</b> Writes <c>MLKelly:{Symbol}:{Timeframe}:LiveMultiplier</c>
/// to <see cref="EngineConfig"/> for each active model. The scorer reads this key to
/// proportionally reduce the computed lot size before submitting the signal to the
/// order bridge. This creates a smooth, continuous degradation path between a fully
/// performing model (multiplier 1.0) and the suppression threshold handled by
/// <see cref="MLModelRetirementWorker"/>.
/// </para>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLKelly:PollIntervalSeconds</c>  — default 3600 (1 h)</item>
///   <item><c>MLKelly:WindowDays</c>           — live accuracy look-back, default 14</item>
///   <item><c>MLKelly:MinSamples</c>           — minimum resolved logs, default 20</item>
///   <item><c>MLKelly:MinMultiplier</c>        — floor for the multiplier, default 0.50</item>
/// </list>
/// </summary>
public sealed class MLPositionSizeAdvisorWorker : BackgroundService
{
    private const string CK_PollSecs    = "MLKelly:PollIntervalSeconds";
    private const string CK_WindowDays  = "MLKelly:WindowDays";
    private const string CK_MinSamples  = "MLKelly:MinSamples";
    private const string CK_MinMult     = "MLKelly:MinMultiplier";
    private const string KeyPrefix      = "MLKelly:";
    private const string KeySuffix      = ":LiveMultiplier";

    private readonly IServiceScopeFactory                 _scopeFactory;
    private readonly ILogger<MLPositionSizeAdvisorWorker> _logger;

    /// <summary>
    /// Initializes the worker.
    /// </summary>
    /// <param name="scopeFactory">Per-iteration DI scope factory.</param>
    /// <param name="logger">Structured logger.</param>
    public MLPositionSizeAdvisorWorker(
        IServiceScopeFactory                  scopeFactory,
        ILogger<MLPositionSizeAdvisorWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Background service main loop. Each iteration creates a fresh scope, reads the
    /// poll interval, and calls <see cref="UpdateMultipliersAsync"/> to refresh the
    /// Kelly multipliers for all active models.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLPositionSizeAdvisorWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await UpdateMultipliersAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLPositionSizeAdvisorWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLPositionSizeAdvisorWorker stopping.");
    }

    // ── Multiplier computation core ───────────────────────────────────────────

    /// <summary>
    /// Iterates all active models and writes an updated live Kelly multiplier to
    /// <see cref="EngineConfig"/> for each one. Models with no stored training
    /// accuracy or insufficient live data default to multiplier = 1.0.
    /// </summary>
    private async Task UpdateMultipliersAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays  = await GetConfigAsync<int>   (readCtx, CK_WindowDays, 14,   ct);
        int    minSamples  = await GetConfigAsync<int>   (readCtx, CK_MinSamples, 20,   ct);
        double minMult     = await GetConfigAsync<double>(readCtx, CK_MinMult,    0.50, ct);

        var cutoff = DateTime.UtcNow.AddDays(-windowDays);

        // Fetch only the fields needed for the Kelly calculation — avoids loading
        // heavy JSON columns (e.g. HyperparamConfigJson) unnecessarily.
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe, m.DirectionAccuracy })
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Scope the live multiplier to the concrete model id so coexisting active,
                // fallback, or shadow-adjacent models cannot overwrite one another's sizing.
                string configKey = $"{KeyPrefix}{model.Symbol}:{model.Timeframe}:{model.Id}{KeySuffix}";

                // If the model has no stored training accuracy (e.g. imported externally),
                // we cannot compute a meaningful ratio — fall back to no-op multiplier.
                if (!model.DirectionAccuracy.HasValue || model.DirectionAccuracy.Value <= 0m)
                {
                    await UpsertConfigAsync(writeCtx, configKey, "1.0", ct);
                    continue;
                }

                double trainingAccuracy = (double)model.DirectionAccuracy.Value;

                // ── Live accuracy from resolved prediction logs ────────────────
                // Only include logs with DirectionCorrect populated (i.e., the outcome
                // has been recorded by MLPredictionOutcomeWorker).
                var liveOutcomes = await readCtx.Set<MLModelPredictionLog>()
                    .Where(l => l.MLModelId        == model.Id   &&
                                l.DirectionCorrect != null        &&
                                l.PredictedAt      >= cutoff      &&
                                !l.IsDeleted)
                    .AsNoTracking()
                    .Select(l => l.DirectionCorrect!.Value)
                    .ToListAsync(ct);

                if (liveOutcomes.Count < minSamples)
                {
                    // Insufficient resolved outcomes — give the model benefit of the doubt
                    // rather than applying an arbitrary penalty on small samples.
                    await UpsertConfigAsync(writeCtx, configKey, "1.0", ct);
                    continue;
                }

                // ── Kelly multiplier computation ──────────────────────────────
                // liveAccuracy = fraction of resolved predictions that were correct
                // multiplier   = liveAccuracy / trainingAccuracy, clamped to [minMult, 1.0]
                //
                // The upper clamp at 1.0 ensures the model never receives a bonus above
                // its training-time capability estimate — only reductions are applied.
                // The lower clamp at minMult ensures the signal is not reduced to near-zero
                // (which would make it economically useless even if technically correct).
                double liveAccuracy = liveOutcomes.Count(x => x) / (double)liveOutcomes.Count;
                double multiplier   = Math.Clamp(liveAccuracy / trainingAccuracy, minMult, 1.0);

                string multStr = multiplier.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                await UpsertConfigAsync(writeCtx, configKey, multStr, ct);

                _logger.LogDebug(
                    "KellyAdvisor: model {Id} ({Symbol}/{Tf}) — " +
                    "trainAcc={Train:P1} liveAcc={Live:P1} n={N} multiplier={Mult:F3}",
                    model.Id, model.Symbol, model.Timeframe,
                    trainingAccuracy, liveAccuracy, liveOutcomes.Count, multiplier);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "KellyAdvisor: update failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    // ── EngineConfig upsert ───────────────────────────────────────────────────

    /// <summary>
    /// Inserts or updates a <see cref="EngineConfig"/> row for the Kelly multiplier key.
    /// Uses <c>ExecuteUpdateAsync</c> to avoid a full entity load; falls back to an
    /// insert if the key does not yet exist (first run for a new model).
    /// </summary>
    /// <param name="writeCtx">Write DbContext.</param>
    /// <param name="key">Config key (e.g. <c>MLKelly:EURUSD:H1:LiveMultiplier</c>).</param>
    /// <param name="value">Four-decimal string representation of the multiplier.</param>
    /// <param name="ct">Cancellation token.</param>
    private static Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(writeCtx, key, value, dataType: LascodiaTradingEngine.Domain.Enums.ConfigDataType.Decimal, ct: ct);

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/>. Returns
    /// <paramref name="defaultValue"/> if the key is absent or unparseable.
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
}
