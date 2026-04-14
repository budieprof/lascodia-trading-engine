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

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new async DI scope per poll cycle so scoped EF Core
    /// contexts are correctly disposed after each freshness check pass.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLTrainingDataFreshnessWorker(
        IServiceScopeFactory                     scopeFactory,
        ILogger<MLTrainingDataFreshnessWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Polls every <c>MLFreshness:PollIntervalSeconds</c>
    /// seconds (default 3600 = 1 hour), reading the interval from <see cref="EngineConfig"/>
    /// on each cycle so it can be hot-reloaded without a restart.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLTrainingDataFreshnessWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default 1-hour poll interval; refreshed from DB on every cycle.
            int pollSecs = 3600;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Refresh poll interval from DB each cycle to support hot-reload.
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

    /// <summary>
    /// Iterates over all active models and delegates per-model freshness checks to
    /// <see cref="CheckModelFreshnessAsync"/>. Reads staleness thresholds once per
    /// cycle to avoid redundant per-model DB round trips.
    /// </summary>
    /// <param name="readCtx">Read-only EF context for models and training run history.</param>
    /// <param name="writeCtx">Write EF context for inserting training runs and updating EngineConfig.</param>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task CheckFreshnessAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Read staleness policy values once per cycle.
        int stalenessDays   = await GetConfigAsync<int>(readCtx, CK_Staleness,   60,  ct);
        int trainWindowDays = await GetConfigAsync<int>(readCtx, CK_TrainWindow, 180, ct);

        // Load all active models — project to minimal fields to reduce bandwidth.
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
                // Per-model exceptions are non-fatal — log and continue with remaining models.
                _logger.LogWarning(ex,
                    "Freshness: check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }

        // Cold-start bootstrap: if an active strategy has no active model and no in-flight
        // training run, queue one. Every other ML queueing worker only reacts to existing
        // models, so without this hook the pipeline deadlocks when the initial
        // DatabaseSeeder bootstrap fails (e.g. insufficient candles at first boot) because
        // nothing ever re-attempts first training.
        try
        {
            await BootstrapMissingModelsAsync(trainWindowDays, readCtx, writeCtx, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Freshness: bootstrap scan for missing models failed");
        }
    }

    /// <summary>
    /// Finds active strategies whose (symbol, timeframe) has no active ML model and no
    /// in-flight training run, and queues a fresh run for each. Runs at most one bootstrap
    /// per (symbol, timeframe) per cycle.
    /// </summary>
    private async Task BootstrapMissingModelsAsync(
        int trainWindowDays,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken ct)
    {
        var strategyKeys = await readCtx.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .Select(s => new { s.Symbol, s.Timeframe })
            .Distinct()
            .ToListAsync(ct);
        if (strategyKeys.Count == 0) return;

        var activeModelKeys = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);
        var activeSet = new HashSet<(string, Timeframe)>(
            activeModelKeys.Select(k => (k.Symbol, k.Timeframe)));

        var inFlightKeys = await readCtx.Set<MLTrainingRun>()
            .Where(r => (r.Status == RunStatus.Queued || r.Status == RunStatus.Running) && !r.IsDeleted)
            .Select(r => new { r.Symbol, r.Timeframe })
            .Distinct()
            .ToListAsync(ct);
        var inFlightSet = new HashSet<(string, Timeframe)>(
            inFlightKeys.Select(k => (k.Symbol, k.Timeframe)));

        int queued = 0;
        var now = DateTime.UtcNow;
        var fromDate = now.AddDays(-trainWindowDays);

        foreach (var key in strategyKeys)
        {
            var tuple = (key.Symbol, key.Timeframe);
            if (activeSet.Contains(tuple)) continue;  // already has a live model
            if (inFlightSet.Contains(tuple)) continue; // already queued / running

            writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol      = key.Symbol,
                Timeframe   = key.Timeframe,
                TriggerType = TriggerType.Manual,
                Status      = RunStatus.Queued,
                FromDate    = fromDate,
                ToDate      = now,
                StartedAt   = now,
            });
            queued++;
        }

        if (queued > 0)
        {
            await writeCtx.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Freshness: bootstrapped {Count} cold-start training run(s) for strategies without an active model",
                queued);
        }
    }

    /// <summary>
    /// Checks whether the training data for a single active model has exceeded the
    /// staleness threshold and, if so, enqueues an <c>AutoDegrading</c> retraining run
    /// while also publishing an observability metric to <see cref="EngineConfig"/>.
    /// </summary>
    /// <remarks>
    /// Training data freshness monitoring:
    ///
    /// A model trained on data ending 60+ days ago has never observed recent market
    /// microstructure, volatility regimes, spread changes, or central bank policy shifts.
    /// Even if the model's accuracy workers report acceptable performance today, its
    /// predictions are structurally anchored to a stale distribution. Over time this
    /// produces "slow-rot" degradation that is harder to detect than sudden distributional
    /// shift because the model's confidence scores may remain stable while its predictions
    /// become quietly misaligned with current price dynamics.
    ///
    /// <b>Observability:</b> The worker writes
    /// <c>MLTraining:{Symbol}:{Tf}:DaysSinceTrainingDataEnd</c> to <see cref="EngineConfig"/>
    /// on every cycle regardless of whether a retrain is triggered. This provides a live
    /// dashboard metric visible to operators without querying the raw training run table.
    ///
    /// <b>Trigger condition:</b> daysSince &gt; stalenessDays AND no queued run exists.
    /// The new run uses <c>TriggerType.AutoDegrading</c> to distinguish it from manually
    /// requested retrains, enabling downstream analysis of how often staleness triggers fire.
    /// </remarks>
    /// <param name="modelId">ID of the model being checked (for log context).</param>
    /// <param name="symbol">Trading symbol (e.g. "EUR_USD").</param>
    /// <param name="timeframe">Candle timeframe for this model.</param>
    /// <param name="stalenessDays">Number of days after which training data is considered stale.</param>
    /// <param name="trainWindowDays">Size of the data window for the auto-triggered retraining run.</param>
    /// <param name="readCtx">Read-only EF context for training run history.</param>
    /// <param name="writeCtx">Write EF context for inserting runs and upserting EngineConfig.</param>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
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
        // Find the most recent completed training run for this symbol/timeframe.
        // ToDate is the end of the training data window — this is what determines staleness.
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

        // Compute days since the training data window ended.
        // This is the primary freshness metric: how far behind the model's "knowledge" is.
        int daysSince = (int)(now - latestRun.ToDate).TotalDays;

        // Write the freshness metric to EngineConfig for live operator visibility.
        // The key format is "MLTraining:{Symbol}:{Tf}:DaysSinceTrainingDataEnd".
        string configKey = $"{KeyPrefix}{symbol}:{timeframe}{KeySuffix}";
        await UpsertConfigAsync(writeCtx, configKey, daysSince.ToString(), ct);

        _logger.LogDebug(
            "Freshness: {Symbol}/{Tf} — {Days} day(s) since training data end ({ToDate:yyyy-MM-dd}).",
            symbol, timeframe, daysSince, latestRun.ToDate);

        // Model is still fresh — no action required.
        if (daysSince <= stalenessDays) return;

        // Check whether a queued run already exists to avoid scheduling duplicates.
        // Another trigger (PSI, diversity, explicit API call) may have already scheduled a retrain.
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

        // Enqueue an AutoDegrading training run with a fresh data window.
        // The window starts trainWindowDays in the past and ends now, ensuring the new
        // model is trained on the most recent available market data.
        var fromDate = now.AddDays(-trainWindowDays);

        writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol      = symbol,
            Timeframe   = timeframe,
            TriggerType = TriggerType.AutoDegrading, // distinct from manual/PSI/diversity triggers
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

    /// <summary>
    /// Upserts a key-value pair in the <see cref="EngineConfig"/> table.
    /// Attempts an in-place update first (one DB round trip for existing keys);
    /// inserts a new row only when no existing row is found (cold-start or new symbol).
    /// </summary>
    /// <param name="writeCtx">Write EF context for the update/insert operations.</param>
    /// <param name="key">The EngineConfig key to upsert.</param>
    /// <param name="value">The string value to write.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        // Attempt bulk update — returns 0 rows if key does not yet exist.
        int rows = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Value,         value)
                .SetProperty(c => c.LastUpdatedAt, DateTime.UtcNow),
                ct);

        // Key did not exist — insert it for the first time.
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

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table,
    /// falling back to <paramref name="defaultValue"/> if the key is absent or
    /// the stored value cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target value type (int, double, string, etc.).</typeparam>
    /// <param name="ctx">EF Core context to query against.</param>
    /// <param name="key">The EngineConfig key to look up.</param>
    /// <param name="defaultValue">Value to return when the key is missing or invalid.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed config value or <paramref name="defaultValue"/>.</returns>
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
