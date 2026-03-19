using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Imposes a temporary per-symbol/timeframe signal cooldown after a model produces
/// a configurable number of consecutive incorrect predictions, using exponential
/// backoff to avoid thrashing during sustained adverse conditions.
///
/// <b>Distinction from <c>MLSignalSuppressionWorker</c>:</b>
/// Suppression monitors a rolling accuracy window and fires when the long-run
/// average crosses a floor — it requires many observations to activate.
/// Cooldown reacts to rapid consecutive failures (e.g., 5 in a row) which can occur
/// within a single session and cause immediate P&amp;L damage before suppression activates.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>Load the last <c>MaxConsecMisses</c> resolved prediction logs ordered by
///         <c>PredictedAt DESC</c>.</item>
///   <item>Count the leading consecutive miss streak from the most recent prediction
///         backward. Stop at the first correct prediction.</item>
///   <item>If streak ≥ <c>MaxConsecMisses</c>, compute the cooldown duration using
///         exponential backoff: <c>BaseCooldownMinutes × 2^(streak − MaxConsecMisses)</c>,
///         capped at <c>MaxCooldownMinutes</c>.</item>
///   <item>Write <c>MLCooldown:{Symbol}:{Timeframe}:ExpiresAt</c> to
///         <see cref="EngineConfig"/>.</item>
///   <item>When the most-recent prediction is correct, clear any active cooldown by
///         writing a past timestamp.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLCooldown:PollIntervalSeconds</c>   — default 120 (2 min)</item>
///   <item><c>MLCooldown:MaxConsecMisses</c>        — streak that triggers cooldown, default 5</item>
///   <item><c>MLCooldown:BaseCooldownMinutes</c>    — first-trigger cooldown, default 15 min</item>
///   <item><c>MLCooldown:MaxCooldownMinutes</c>     — exponential backoff cap, default 240 min</item>
/// </list>
/// </summary>
public sealed class MLSignalCooldownWorker : BackgroundService
{
    private const string CK_PollSecs  = "MLCooldown:PollIntervalSeconds";
    private const string CK_MaxMisses = "MLCooldown:MaxConsecMisses";
    private const string CK_BaseMin   = "MLCooldown:BaseCooldownMinutes";
    private const string CK_MaxMin    = "MLCooldown:MaxCooldownMinutes";
    private const string KeyPrefix    = "MLCooldown:";
    private const string KeySuffix    = ":ExpiresAt";

    private readonly IServiceScopeFactory             _scopeFactory;
    private readonly ILogger<MLSignalCooldownWorker>  _logger;

    public MLSignalCooldownWorker(
        IServiceScopeFactory              scopeFactory,
        ILogger<MLSignalCooldownWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSignalCooldownWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 120;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 120, stoppingToken);

                await UpdateCooldownsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLSignalCooldownWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSignalCooldownWorker stopping.");
    }

    // ── Cooldown update core ──────────────────────────────────────────────────

    private async Task UpdateCooldownsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int maxMisses  = await GetConfigAsync<int>(readCtx, CK_MaxMisses, 5,   ct);
        int baseMin    = await GetConfigAsync<int>(readCtx, CK_BaseMin,   15,  ct);
        int maxMin     = await GetConfigAsync<int>(readCtx, CK_MaxMin,    240, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ProcessModelCooldownAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    maxMisses, baseMin, maxMin,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Cooldown: update failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task ProcessModelCooldownAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        int                                     maxMisses,
        int                                     baseMinutes,
        int                                     maxMinutes,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load the last N+1 resolved predictions (enough to detect streak)
        var recent = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId  &&
                        l.DirectionCorrect != null      &&
                        !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)
            .Take(maxMisses + 5)
            .AsNoTracking()
            .Select(l => l.DirectionCorrect!.Value)
            .ToListAsync(ct);

        if (recent.Count == 0) return;

        // Count leading consecutive misses from most-recent backward
        int streak = 0;
        foreach (var correct in recent)
        {
            if (!correct) streak++;
            else break;
        }

        string configKey = $"{KeyPrefix}{symbol}:{timeframe}{KeySuffix}";

        if (streak < maxMisses)
        {
            // No active cooldown needed — clear any stale key
            await UpsertConfigAsync(writeCtx, configKey,
                DateTime.UtcNow.AddMinutes(-1).ToString("o"), ct);
            return;
        }

        // Exponential backoff: BaseMins × 2^(streak − maxMisses)
        int extraMisses  = streak - maxMisses;
        double factor    = Math.Pow(2.0, extraMisses);
        int cooldownMins = (int)Math.Min(baseMinutes * factor, maxMinutes);

        var expiresAt = DateTime.UtcNow.AddMinutes(cooldownMins);
        await UpsertConfigAsync(writeCtx, configKey, expiresAt.ToString("o"), ct);

        _logger.LogWarning(
            "Cooldown: model {Id} ({Symbol}/{Tf}) — {Streak} consecutive misses, " +
            "cooldown={Mins} min, expires {Exp:HH:mm} UTC",
            modelId, symbol, timeframe, streak, cooldownMins, expiresAt);
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
                DataType        = ConfigDataType.String,
                Description     = "Cooldown expiry ISO-8601 timestamp for consecutive-miss suppression. " +
                                  "Written by MLSignalCooldownWorker.",
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
