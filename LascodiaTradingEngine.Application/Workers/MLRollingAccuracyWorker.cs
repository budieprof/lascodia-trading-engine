using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Tracks the Exponential Moving Average (EMA) of per-prediction direction correctness
/// for each active ML model and alerts when the EMA dips below a configurable accuracy floor.
///
/// The EMA is computed over resolved prediction logs (where <c>DirectionCorrect</c> is not null),
/// ordered chronologically. Using an EMA rather than a simple mean gives recent predictions
/// higher weight, making the metric responsive to sudden accuracy degradation.
///
/// When the EMA drops below <c>MLRollingAccuracy:AccuracyFloor</c> an
/// <see cref="AlertType.MLModelDegraded"/> alert is created (deduplicated per symbol).
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLRollingAccuracy:PollIntervalSeconds</c> — default 7200 (2 h)</item>
///   <item><c>MLRollingAccuracy:WindowDays</c>          — resolved-log look-back, default 30</item>
///   <item><c>MLRollingAccuracy:MinResolved</c>          — skip model if fewer resolved logs, default 30</item>
///   <item><c>MLRollingAccuracy:EmaAlpha</c>             — EMA smoothing factor α ∈ (0,1), default 0.10</item>
///   <item><c>MLRollingAccuracy:AccuracyFloor</c>        — alert when EMA &lt; this value, default 0.50</item>
/// </list>
/// </summary>
public sealed class MLRollingAccuracyWorker : BackgroundService
{
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    private const string CK_PollSecs      = "MLRollingAccuracy:PollIntervalSeconds";
    private const string CK_WindowDays    = "MLRollingAccuracy:WindowDays";
    private const string CK_MinResolved   = "MLRollingAccuracy:MinResolved";
    private const string CK_EmaAlpha      = "MLRollingAccuracy:EmaAlpha";
    private const string CK_AccuracyFloor = "MLRollingAccuracy:AccuracyFloor";

    private readonly IServiceScopeFactory               _scopeFactory;
    private readonly ILogger<MLRollingAccuracyWorker>   _logger;

    public MLRollingAccuracyWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLRollingAccuracyWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLRollingAccuracyWorker started.");

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

                await CheckRollingAccuracyAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLRollingAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLRollingAccuracyWorker stopping.");
    }

    // ── Per-poll accuracy check ───────────────────────────────────────────────

    private async Task CheckRollingAccuracyAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays    = await GetConfigAsync<int>   (readCtx, CK_WindowDays,    30,   ct);
        int    minResolved   = await GetConfigAsync<int>   (readCtx, CK_MinResolved,   30,   ct);
        double emaAlpha      = await GetConfigAsync<double>(readCtx, CK_EmaAlpha,      0.10, ct);
        double accuracyFloor = await GetConfigAsync<double>(readCtx, CK_AccuracyFloor, 0.50, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckModelAccuracyAsync(
                    model, readCtx, writeCtx,
                    windowDays, minResolved, emaAlpha, accuracyFloor, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Rolling accuracy check failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    private async Task CheckModelAccuracyAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minResolved,
        double                                  emaAlpha,
        double                                  accuracyFloor,
        CancellationToken                       ct)
    {
        var since = DateTime.UtcNow.AddDays(-windowDays);

        // Load resolved prediction outcomes ordered oldest-first for chronological EMA
        var outcomes = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.DirectionCorrect != null     &&
                        !l.IsDeleted)
            .OrderBy(l => l.PredictedAt)
            .Select(l => l.DirectionCorrect!.Value)
            .ToListAsync(ct);

        if (outcomes.Count < minResolved)
        {
            _logger.LogDebug(
                "Rolling accuracy: {Symbol}/{Tf} only {N} resolved predictions (need {Min}) — skip.",
                model.Symbol, model.Timeframe, outcomes.Count, minResolved);
            return;
        }

        double ema = ComputeEma(outcomes, emaAlpha);

        _logger.LogDebug(
            "Rolling accuracy: {Symbol}/{Tf} model {Id}: EMA(acc)={Ema:F3} (floor={Floor:F2}, n={N})",
            model.Symbol, model.Timeframe, model.Id, ema, accuracyFloor, outcomes.Count);

        if (ema >= accuracyFloor) return;

        _logger.LogWarning(
            "Rolling accuracy breach for {Symbol}/{Tf} model {Id}: " +
            "EMA(acc)={Ema:F3} < floor={Floor:F2} — alerting.",
            model.Symbol, model.Timeframe, model.Id, ema, accuracyFloor);

        // Deduplicate: skip if an active MLModelDegraded alert already exists for this symbol
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == model.Symbol              &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (!alertExists)
        {
            var alert = new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                Symbol        = model.Symbol,
                Channel       = AlertChannel.Webhook,
                Destination   = "ml-ops",
                ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reason        = "rolling_accuracy_below_floor",
                    ema,
                    accuracyFloor,
                    emaAlpha,
                    symbol        = model.Symbol,
                    timeframe     = model.Timeframe.ToString(),
                    modelId       = model.Id,
                    resolvedCount = outcomes.Count,
                }),
                IsActive = true,
            };
            writeCtx.Set<Alert>().Add(alert);
            await writeCtx.SaveChangesAsync(ct);
        }
    }

    // ── EMA computation ───────────────────────────────────────────────────────

    /// <summary>
    /// Computes an EMA over a boolean outcome sequence (true = 1, false = 0),
    /// oldest-first. Initialises EMA with the first value (warm-up = 1).
    /// </summary>
    private static double ComputeEma(IReadOnlyList<bool> outcomes, double alpha)
    {
        double ema = outcomes[0] ? 1.0 : 0.0;
        for (int i = 1; i < outcomes.Count; i++)
            ema = alpha * (outcomes[i] ? 1.0 : 0.0) + (1.0 - alpha) * ema;
        return ema;
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
