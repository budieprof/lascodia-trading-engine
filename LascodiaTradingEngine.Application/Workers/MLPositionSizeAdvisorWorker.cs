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
/// <b>Distinction from existing mechanisms:</b>
/// <list type="bullet">
///   <item>The BSS-based Kelly multiplier in <c>MLSignalScorer</c> step 13b uses the
///         <em>training-time</em> Brier Skill Score — a static quality measure frozen
///         at training time.</item>
///   <item>This worker computes a <em>live</em> multiplier based on the gap between the
///         model's declared <see cref="MLModel.DirectionAccuracy"/> (training accuracy)
///         and its current rolling live accuracy from resolved prediction logs.</item>
/// </list>
///
/// <b>Multiplier formula:</b>
/// <c>multiplier = clamp(liveAccuracy / trainingAccuracy, MinMultiplier, 1.0)</c>
///
/// A model at 90% of its training accuracy gets a 0.90 multiplier; at or above training
/// accuracy the multiplier is 1.0 (no reduction). The floor is <c>MinMultiplier</c>
/// (default 0.50) to prevent near-zero bet sizing.
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

    public MLPositionSizeAdvisorWorker(
        IServiceScopeFactory                  scopeFactory,
        ILogger<MLPositionSizeAdvisorWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

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

    private async Task UpdateMultipliersAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays  = await GetConfigAsync<int>   (readCtx, CK_WindowDays, 14,   ct);
        int    minSamples  = await GetConfigAsync<int>   (readCtx, CK_MinSamples, 20,   ct);
        double minMult     = await GetConfigAsync<double>(readCtx, CK_MinMult,    0.50, ct);

        var cutoff = DateTime.UtcNow.AddDays(-windowDays);

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
                string configKey = $"{KeyPrefix}{model.Symbol}:{model.Timeframe}{KeySuffix}";

                // If no training accuracy stored, use multiplier = 1.0 (no adjustment)
                if (!model.DirectionAccuracy.HasValue || model.DirectionAccuracy.Value <= 0m)
                {
                    await UpsertConfigAsync(writeCtx, configKey, "1.0", ct);
                    continue;
                }

                double trainingAccuracy = (double)model.DirectionAccuracy.Value;

                // Compute live accuracy from resolved prediction logs in the window
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
                    // Not enough data — keep multiplier at 1.0 (benefit of doubt)
                    await UpsertConfigAsync(writeCtx, configKey, "1.0", ct);
                    continue;
                }

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

    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        int rows = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Value,         value)
                .SetProperty(c => c.LastUpdatedAt, DateTime.UtcNow),
                ct);

        if (rows == 0)
        {
            writeCtx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key             = key,
                Value           = value,
                DataType        = ConfigDataType.Decimal,
                Description     = "Live-accuracy-based Kelly fraction multiplier. Written by MLPositionSizeAdvisorWorker.",
                IsHotReloadable = true,
                LastUpdatedAt   = DateTime.UtcNow,
            });
            await writeCtx.SaveChangesAsync(ct);
        }
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
