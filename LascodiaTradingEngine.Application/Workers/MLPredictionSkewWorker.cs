using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors the BUY/SELL prediction ratio for each active ML model over a rolling window
/// of recent <see cref="MLModelPredictionLog"/> records.
///
/// A healthy model should produce a roughly balanced mix of BUY and SELL predictions.
/// When one direction dominates beyond a configurable threshold (e.g. 80%), it indicates
/// one of two problems:
/// <list type="bullet">
///   <item>The training set had a severe class imbalance that propagated into the model.</item>
///   <item>The model's decision boundary has collapsed to a near-constant prediction
///         ("stuck predictor"), which destroys its value as a signal.</item>
/// </list>
///
/// On detection the worker creates a deduplicated <see cref="AlertType.MLModelDegraded"/>
/// alert and queues an <see cref="MLTrainingRun"/> so the model is retrained.
///
/// Algorithm (per model):
/// <list type="number">
///   <item>Load the most recent <c>N</c> prediction logs within the look-back window.</item>
///   <item>Count BUY predictions (where <c>PredictedDirection == TradeDirection.Buy</c>).</item>
///   <item>Compute BUY fraction = buyCount / total.</item>
///   <item>If BUY fraction &gt; <c>MaxSkewFraction</c> or BUY fraction &lt; (1 − MaxSkewFraction),
///         the model is skewed.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLPredictionSkew:PollIntervalSeconds</c> — default 7200 (2 h)</item>
///   <item><c>MLPredictionSkew:WindowDays</c>          — look-back window, default 14</item>
///   <item><c>MLPredictionSkew:MinPredictions</c>      — skip if fewer records, default 30</item>
///   <item><c>MLPredictionSkew:MaxSkewFraction</c>     — skew threshold, default 0.80</item>
/// </list>
/// </summary>
public sealed class MLPredictionSkewWorker : BackgroundService
{
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    private const string CK_PollSecs        = "MLPredictionSkew:PollIntervalSeconds";
    private const string CK_WindowDays      = "MLPredictionSkew:WindowDays";
    private const string CK_MinPredictions  = "MLPredictionSkew:MinPredictions";
    private const string CK_MaxSkewFraction = "MLPredictionSkew:MaxSkewFraction";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLPredictionSkewWorker>   _logger;

    public MLPredictionSkewWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<MLPredictionSkewWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLPredictionSkewWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 7200;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 7200, stoppingToken);

                await CheckSkewAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLPredictionSkewWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLPredictionSkewWorker stopping.");
    }

    // ── Per-poll skew check ───────────────────────────────────────────────────

    private async Task CheckSkewAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays      = await GetConfigAsync<int>   (readCtx, CK_WindowDays,      14,   ct);
        int    minPredictions  = await GetConfigAsync<int>   (readCtx, CK_MinPredictions,  30,   ct);
        double maxSkewFraction = await GetConfigAsync<double>(readCtx, CK_MaxSkewFraction, 0.80, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckModelSkewAsync(
                    model, readCtx, writeCtx,
                    windowDays, minPredictions, maxSkewFraction, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Skew check failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    private async Task CheckModelSkewAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minPredictions,
        double                                  maxSkewFraction,
        CancellationToken                       ct)
    {
        var since = DateTime.UtcNow.AddDays(-windowDays);

        var predictions = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId == model.Id &&
                        l.PredictedAt >= since   &&
                        !l.IsDeleted)
            .Select(l => l.PredictedDirection)
            .ToListAsync(ct);

        if (predictions.Count < minPredictions)
        {
            _logger.LogDebug(
                "Skew: {Symbol}/{Tf} model {Id} only {N} predictions (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, predictions.Count, minPredictions);
            return;
        }

        int    buyCount   = predictions.Count(d => d == TradeDirection.Buy);
        double buyFraction = (double)buyCount / predictions.Count;

        _logger.LogDebug(
            "Skew: {Symbol}/{Tf} model {Id}: BUY={BuyFrac:P1} ({Buy}/{Total}, threshold={Thr:P0})",
            model.Symbol, model.Timeframe, model.Id,
            buyFraction, buyCount, predictions.Count, maxSkewFraction);

        bool isSkewed = buyFraction > maxSkewFraction || buyFraction < (1.0 - maxSkewFraction);
        if (!isSkewed) return;

        string dominantSide = buyFraction > maxSkewFraction ? "BUY" : "SELL";
        double dominantFraction = buyFraction > maxSkewFraction ? buyFraction : 1.0 - buyFraction;

        _logger.LogWarning(
            "Prediction skew detected for {Symbol}/{Tf} model {Id}: " +
            "{Side} = {Frac:P1} of {N} predictions (threshold={Thr:P0}) — model may be stuck.",
            model.Symbol, model.Timeframe, model.Id,
            dominantSide, dominantFraction, predictions.Count, maxSkewFraction);

        // Deduplicate alert
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == model.Symbol              &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (!alertExists)
        {
            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                Symbol        = model.Symbol,
                Channel       = AlertChannel.Webhook,
                Destination   = "ml-ops",
                ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reason          = "prediction_direction_skew",
                    dominantSide,
                    dominantFraction,
                    maxSkewFraction,
                    totalPredictions = predictions.Count,
                    symbol          = model.Symbol,
                    timeframe       = model.Timeframe.ToString(),
                    modelId         = model.Id,
                }),
                IsActive = true,
            });
        }

        // Queue retrain if none already queued/running
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);

        if (!alreadyQueued)
        {
            writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol    = model.Symbol,
                Timeframe = model.Timeframe,
                Status    = RunStatus.Queued,
                HyperparamConfigJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    triggeredBy     = "MLPredictionSkewWorker",
                    dominantSide,
                    dominantFraction,
                    modelId         = model.Id,
                }),
            });
        }

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

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
