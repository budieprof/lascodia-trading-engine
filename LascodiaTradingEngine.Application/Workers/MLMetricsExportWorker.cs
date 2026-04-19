using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes and persists ML model performance metrics to <see cref="EngineConfig"/> for
/// dashboard consumption. Provides a single source of truth for any observability UI to query.
///
/// <para>
/// Every poll cycle the worker:
/// <list type="number">
///   <item>Loads all active (non-deleted) <see cref="MLModel"/> records.</item>
///   <item>For each model, loads resolved <see cref="MLModelPredictionLog"/> records from
///         the rolling 14-day window.</item>
///   <item>Computes direction accuracy, Brier score, ensemble disagreement, inference latency,
///         prediction count, and model age.</item>
///   <item>Writes each metric to <see cref="EngineConfig"/> under a hierarchical key pattern
///         <c>MLMetrics:{Symbol}:{Timeframe}:*</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Polling interval:</b> configurable via <c>MLMetrics:PollIntervalSeconds</c> (default 300 s / 5 min).
/// </para>
/// </summary>
public sealed class MLMetricsExportWorker : BackgroundService
{
    private const string CK_PollSecs   = "MLMetrics:PollIntervalSeconds";
    private const string CK_WindowDays = "MLMetrics:WindowDays";

    private readonly IServiceScopeFactory          _scopeFactory;
    private readonly ILogger<MLMetricsExportWorker> _logger;

    public MLMetricsExportWorker(
        IServiceScopeFactory            scopeFactory,
        ILogger<MLMetricsExportWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLMetricsExportWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 300;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 300, stoppingToken);
                int windowDays = await GetConfigAsync<int>(ctx, CK_WindowDays, 14, stoppingToken);

                var windowStart = DateTime.UtcNow.AddDays(-windowDays);

                var activeModels = await ctx.Set<MLModel>()
                    .Where(m => m.IsActive && !m.IsDeleted)
                    .AsNoTracking()
                    .ToListAsync(stoppingToken);

                _logger.LogDebug(
                    "MLMetricsExport: computing metrics for {Count} active models (window={Days}d)",
                    activeModels.Count, windowDays);

                foreach (var model in activeModels)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await ExportModelMetricsAsync(model, writeCtx, ctx, windowStart, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLMetricsExportWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLMetricsExportWorker stopping.");
    }

    /// <summary>
    /// Computes and persists all observability metrics for a single active model.
    /// </summary>
    private async Task ExportModelMetricsAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        DateTime                                windowStart,
        CancellationToken                       ct)
    {
        var prefix = $"MLMetrics:{model.Symbol}:{model.Timeframe}";

        // ── Resolved predictions (with known outcomes) ──────────────────────
        var resolvedLogs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        !l.IsDeleted                   &&
                        l.DirectionCorrect != null     &&
                        l.OutcomeRecordedAt != null    &&
                        l.OutcomeRecordedAt >= windowStart)
            .AsNoTracking()
            .ToListAsync(ct);

        // ── All predictions (including unresolved) for disagreement & latency ─
        var allLogs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId  == model.Id &&
                        !l.IsDeleted             &&
                        l.PredictedAt >= windowStart)
            .AsNoTracking()
            .ToListAsync(ct);

        // ── Direction accuracy ──────────────────────────────────────────────
        double directionAccuracy = 0;
        if (resolvedLogs.Count > 0)
        {
            int correct = resolvedLogs.Count(l => l.DirectionCorrect == true);
            directionAccuracy = (double)correct / resolvedLogs.Count;
        }

        // ── Brier score ─────────────────────────────────────────────────────
        double brierScore = 0;
        if (resolvedLogs.Count > 0)
        {
            double sum = 0;
            int n = 0;
            foreach (var l in resolvedLogs)
            {
                if (l.DirectionCorrect is null) continue;
                // Use served probability if available, fallback to confidence
                double pBuy = l.ServedCalibratedProbability.HasValue
                    ? (double)l.ServedCalibratedProbability.Value
                    : l.CalibratedProbability.HasValue
                        ? (double)l.CalibratedProbability.Value
                        : (double)l.ConfidenceScore;
                double y = l.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
                sum += (pBuy - y) * (pBuy - y);
                n++;
            }
            brierScore = n > 0 ? sum / n : 0;
        }

        // ── Ensemble disagreement ───────────────────────────────────────────
        var disagLogs = allLogs.Where(l => l.EnsembleDisagreement.HasValue).ToList();
        double meanDisagreement = disagLogs.Count > 0
            ? (double)disagLogs.Average(l => l.EnsembleDisagreement!.Value)
            : 0;

        // ── Inference latency ───────────────────────────────────────────────
        var latencyLogs = allLogs.Where(l => l.LatencyMs.HasValue).ToList();
        double avgLatencyMs = latencyLogs.Count > 0
            ? latencyLogs.Average(l => l.LatencyMs!.Value)
            : 0;

        // ── Prediction count ────────────────────────────────────────────────
        int predictionCount = allLogs.Count;

        // ── Model age ───────────────────────────────────────────────────────
        double modelAgeDays = model.ActivatedAt.HasValue
            ? (DateTime.UtcNow - model.ActivatedAt.Value).TotalDays
            : 0;

        // ── Persist metrics ─────────────────────────────────────────────────
        var now = DateTime.UtcNow;

        await UpsertConfigAsync(writeCtx, $"{prefix}:DirectionAccuracy",    directionAccuracy.ToString("F4"),    ct);
        await UpsertConfigAsync(writeCtx, $"{prefix}:BrierScore",           brierScore.ToString("F4"),           ct);
        await UpsertConfigAsync(writeCtx, $"{prefix}:EnsembleDisagreement", meanDisagreement.ToString("F4"),     ct);
        await UpsertConfigAsync(writeCtx, $"{prefix}:InferenceLatencyMs",   avgLatencyMs.ToString("F1"),         ct);
        await UpsertConfigAsync(writeCtx, $"{prefix}:PredictionCount",      predictionCount.ToString(),          ct);
        await UpsertConfigAsync(writeCtx, $"{prefix}:ModelAge",             modelAgeDays.ToString("F1"),         ct);
        await UpsertConfigAsync(writeCtx, $"{prefix}:LastUpdated",          now.ToString("O"),                   ct);

        _logger.LogInformation(
            "MLMetricsExport {Symbol}/{Tf}: acc={Acc:P1} brier={Brier:F4} disagree={Dis:F4} " +
            "latency={Lat:F1}ms predictions={N} age={Age:F0}d",
            model.Symbol, model.Timeframe, directionAccuracy, brierScore, meanDisagreement,
            avgLatencyMs, predictionCount, modelAgeDays);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(ctx, key, value, ct: ct);
}
