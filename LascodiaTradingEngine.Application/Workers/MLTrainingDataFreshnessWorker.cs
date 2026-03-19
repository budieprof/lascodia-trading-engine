using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors how stale the training data is for each active ML model and
/// automatically enqueues a retraining job when the data window end-date
/// falls too far in the past.
///
/// <b>Problem:</b> A model trained on data ending 90 days ago has never seen
/// recent market microstructure, volatility regimes, or spread changes. Its
/// predictions are anchored to a stale statistical distribution, which
/// systematically degrades accuracy over time regardless of what the direction
/// or EWMA accuracy workers report.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>For each active <see cref="MLModel"/>, find its most recent
///         <see cref="MLTrainingRun"/> with <c>Status = Completed</c>.</item>
///   <item>Compute <c>daysSinceTrainingDataEnd = (UtcNow − run.ToDate).TotalDays</c>.</item>
///   <item>Write <c>MLTraining:{Symbol}:{Tf}:DaysSinceTrainingDataEnd</c> to
///         <see cref="EngineConfig"/> for observability.</item>
///   <item>If <c>daysSinceTrainingDataEnd</c> exceeds the staleness threshold
///         <b>and</b> no <c>Queued</c> training run already exists for the
///         symbol/timeframe, insert a new <see cref="MLTrainingRun"/> with
///         <c>TriggerType = AutoDegrading</c> and a training window of
///         [UtcNow − TrainingWindowDays … UtcNow].</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLFreshness:PollIntervalSeconds</c>   — default 3600 (1 h)</item>
///   <item><c>MLFreshness:StalenessDays</c>         — trigger threshold, default 60</item>
///   <item><c>MLFreshness:TrainingWindowDays</c>    — new run's data window, default 180</item>
/// </list>
/// </summary>
public sealed class MLTrainingDataFreshnessWorker : BackgroundService
{
    private const string CK_PollSecs     = "MLFreshness:PollIntervalSeconds";
    private const string CK_Staleness    = "MLFreshness:StalenessDays";
    private const string CK_TrainWindow  = "MLFreshness:TrainingWindowDays";
    private const string KeyPrefix       = "MLTraining:";
    private const string KeySuffix       = ":DaysSinceTrainingDataEnd";

    private readonly IServiceScopeFactory                    _scopeFactory;
    private readonly ILogger<MLTrainingDataFreshnessWorker>  _logger;

    public MLTrainingDataFreshnessWorker(
        IServiceScopeFactory                     scopeFactory,
        ILogger<MLTrainingDataFreshnessWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLTrainingDataFreshnessWorker started.");

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

                await CheckFreshnessAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLTrainingDataFreshnessWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLTrainingDataFreshnessWorker stopping.");
    }

    // ── Freshness check core ──────────────────────────────────────────────────

    private async Task CheckFreshnessAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int stalenessDays   = await GetConfigAsync<int>(readCtx, CK_Staleness,   60,  ct);
        int trainWindowDays = await GetConfigAsync<int>(readCtx, CK_TrainWindow, 180, ct);

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
                await CheckModelFreshnessAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    stalenessDays, trainWindowDays,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Freshness: check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task CheckModelFreshnessAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        int                                     stalenessDays,
        int                                     trainWindowDays,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Find the most recent completed training run for this model
        var latestRun = await readCtx.Set<MLTrainingRun>()
            .Where(r => r.Symbol    == symbol        &&
                        r.Timeframe == timeframe      &&
                        r.Status    == RunStatus.Completed &&
                        !r.IsDeleted)
            .OrderByDescending(r => r.ToDate)
            .AsNoTracking()
            .Select(r => new { r.ToDate })
            .FirstOrDefaultAsync(ct);

        if (latestRun is null)
        {
            _logger.LogDebug(
                "Freshness: no completed training run found for {Symbol}/{Tf} — skipping.",
                symbol, timeframe);
            return;
        }

        var now = DateTime.UtcNow;
        int daysSince = (int)(now - latestRun.ToDate).TotalDays;

        // Write observability key to EngineConfig
        string configKey = $"{KeyPrefix}{symbol}:{timeframe}{KeySuffix}";
        await UpsertConfigAsync(writeCtx, configKey, daysSince.ToString(), ct);

        _logger.LogDebug(
            "Freshness: {Symbol}/{Tf} — {Days} day(s) since training data end ({ToDate:yyyy-MM-dd}).",
            symbol, timeframe, daysSince, latestRun.ToDate);

        if (daysSince <= stalenessDays) return;

        // Check whether a queued run already exists to avoid duplicate scheduling
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == symbol        &&
                           r.Timeframe == timeframe      &&
                           r.Status    == RunStatus.Queued &&
                           !r.IsDeleted, ct);

        if (alreadyQueued)
        {
            _logger.LogDebug(
                "Freshness: {Symbol}/{Tf} — stale ({Days} days) but a Queued run already exists.",
                symbol, timeframe, daysSince);
            return;
        }

        // Enqueue a new training run
        var fromDate = now.AddDays(-trainWindowDays);

        writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol      = symbol,
            Timeframe   = timeframe,
            TriggerType = TriggerType.AutoDegrading,
            Status      = RunStatus.Queued,
            FromDate    = fromDate,
            ToDate      = now,
        });
        await writeCtx.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Freshness: {Symbol}/{Tf} — training data is {Days} days old (threshold {Thr}). " +
            "AutoDegrading training run queued (window {From:yyyy-MM-dd} → {To:yyyy-MM-dd}).",
            symbol, timeframe, daysSince, stalenessDays, fromDate, now);
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
                Description     = "Days since training data end for this symbol/timeframe. Written by MLTrainingDataFreshnessWorker.",
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
