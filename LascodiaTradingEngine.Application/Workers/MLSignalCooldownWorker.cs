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
    // ── EngineConfig key constants ─────────────────────────────────────────────

    /// <summary>Seconds between cooldown evaluation cycles (default 120 = 2 min).
    /// Short interval ensures a new consecutive-miss streak is detected and cooldown applied
    /// within a single candle period on most timeframes.</summary>
    private const string CK_PollSecs  = "MLCooldown:PollIntervalSeconds";

    /// <summary>Number of consecutive incorrect predictions required to trigger a cooldown
    /// (default 5). Five in a row is extremely unlikely by chance for a model operating at
    /// even 50 % accuracy, so it reliably indicates a regime shift or feature failure.</summary>
    private const string CK_MaxMisses = "MLCooldown:MaxConsecMisses";

    /// <summary>Duration (minutes) of the first cooldown when the streak reaches MaxConsecMisses
    /// (default 15 min). Subsequent additional misses beyond the threshold double this value
    /// via exponential backoff.</summary>
    private const string CK_BaseMin   = "MLCooldown:BaseCooldownMinutes";

    /// <summary>Hard upper cap (minutes) on any single cooldown period (default 240 = 4 h).
    /// Without a cap, exponential backoff could produce absurdly long cooldowns during extreme
    /// losing streaks, preventing recovery even after market conditions normalise.</summary>
    private const string CK_MaxMin    = "MLCooldown:MaxCooldownMinutes";

    /// <summary>Prefix for per-model cooldown expiry keys written to EngineConfig.
    /// The full key pattern is: <c>MLCooldown:{Symbol}:{Timeframe}:ExpiresAt</c>.</summary>
    private const string KeyPrefix    = "MLCooldown:";

    /// <summary>Suffix appended to the per-model cooldown key.</summary>
    private const string KeySuffix    = ":ExpiresAt";

    private readonly IServiceScopeFactory             _scopeFactory;
    private readonly ILogger<MLSignalCooldownWorker>  _logger;

    /// <summary>
    /// Initialises the worker with scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">Used to create per-iteration DI scopes so EF DbContexts
    /// are properly disposed after each poll cycle.</param>
    /// <param name="logger">Structured logger for cooldown activation and expiry events.</param>
    public MLSignalCooldownWorker(
        IServiceScopeFactory              scopeFactory,
        ILogger<MLSignalCooldownWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Main background loop. Runs indefinitely until the host signals cancellation.
    /// On each iteration:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope for scoped DbContext access.</item>
    ///   <item>Reads the poll interval from <see cref="EngineConfig"/>.</item>
    ///   <item>Delegates to <see cref="UpdateCooldownsAsync"/> to evaluate every active model.</item>
    ///   <item>Sleeps for the configured poll interval (default 2 min) before the next cycle.</item>
    /// </list>
    /// The short default interval (2 min) means cooldowns activate quickly after consecutive misses,
    /// minimising the window during which a poorly-performing model continues to emit signals.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSignalCooldownWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval — overridden by EngineConfig on each cycle.
            int pollSecs = 120;

            try
            {
                // New scope per iteration — DbContexts are scoped and must not be reused
                // across loop ticks that may be minutes apart.
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Hot-reload: read interval from DB on every cycle.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 120, stoppingToken);

                await UpdateCooldownsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown — exit cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Log and continue — transient errors should not permanently disable cooldown enforcement.
                _logger.LogError(ex, "MLSignalCooldownWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSignalCooldownWorker stopping.");
    }

    // ── Cooldown update core ──────────────────────────────────────────────────

    /// <summary>
    /// Reads all active ML models and updates the cooldown expiry key in <see cref="EngineConfig"/>
    /// for each one based on the current consecutive-miss streak in <see cref="MLModelPredictionLog"/>.
    /// All three cooldown parameters are read from EngineConfig upfront to avoid N+1 queries.
    /// Per-model failures are isolated so one model's error cannot skip cooldown evaluation for others.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for model and prediction log queries.</param>
    /// <param name="writeCtx">Write DbContext for upserting EngineConfig cooldown keys.</param>
    /// <param name="ct">Cancellation token propagated from the host.</param>
    private async Task UpdateCooldownsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Read all cooldown thresholds from config once — avoids repeated DB hits per model.
        int maxMisses  = await GetConfigAsync<int>(readCtx, CK_MaxMisses, 5,   ct);
        int baseMin    = await GetConfigAsync<int>(readCtx, CK_BaseMin,   15,  ct);
        int maxMin     = await GetConfigAsync<int>(readCtx, CK_MaxMin,    240, ct);

        // Project to a lightweight anonymous type — we don't need ModelBytes or other heavy fields.
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
                // Isolate per-model failures so one bad model doesn't skip cooldown updates for all others.
                _logger.LogWarning(ex,
                    "Cooldown: update failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Evaluates the consecutive-miss streak for a single model and writes (or clears)
    /// the cooldown expiry timestamp in <see cref="EngineConfig"/>.
    ///
    /// <b>Streak detection:</b> Loads the most recent <c>maxMisses + 5</c> resolved predictions
    /// (the extra 5 provide a buffer to detect streaks that just crossed the threshold) ordered
    /// newest-first, then counts the leading run of incorrect predictions. The loop stops at the
    /// first correct prediction — meaning only the unbroken tail of misses counts.
    ///
    /// <b>Exponential backoff formula:</b>
    /// <c>cooldownMins = Min(baseMins × 2^(streak − maxMisses), maxMins)</c>
    /// <list type="bullet">
    ///   <item>At exactly maxMisses (e.g. 5): 15 × 2^0 = 15 min</item>
    ///   <item>At maxMisses + 1 (6):           15 × 2^1 = 30 min</item>
    ///   <item>At maxMisses + 2 (7):           15 × 2^2 = 60 min</item>
    ///   <item>At maxMisses + 4 (9):           15 × 2^4 = 240 min (capped)</item>
    /// </list>
    ///
    /// <b>How cooldowns are consumed:</b> Downstream signal generators (e.g. <see cref="StrategyWorker"/>
    /// or <see cref="SignalOrderBridgeWorker"/>) read the
    /// <c>MLCooldown:{Symbol}:{Timeframe}:ExpiresAt</c> key from EngineConfig and skip signal
    /// emission when the current UTC time is before the stored expiry timestamp.
    ///
    /// <b>Clearing a cooldown:</b> When the model produces at least one correct prediction the
    /// streak breaks. This writes a past timestamp (1 minute ago) to the expiry key, immediately
    /// re-enabling signals without deleting the key (deletion would require a separate migration path).
    /// </summary>
    /// <param name="modelId">Database ID of the ML model.</param>
    /// <param name="symbol">Currency pair symbol for the cooldown key and log query filter.</param>
    /// <param name="timeframe">Timeframe used in the cooldown key name.</param>
    /// <param name="maxMisses">Number of consecutive misses that trigger the first cooldown.</param>
    /// <param name="baseMinutes">Base cooldown duration in minutes (before backoff multiplier).</param>
    /// <param name="maxMinutes">Hard upper cap on cooldown duration in minutes.</param>
    /// <param name="readCtx">Read-only DbContext for prediction log queries.</param>
    /// <param name="writeCtx">Write DbContext for EngineConfig upserts.</param>
    /// <param name="ct">Cancellation token.</param>
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
        // Load the last N+1 resolved predictions (enough to detect streak).
        // The +5 buffer ensures we can detect streaks that slightly exceed maxMisses without
        // an off-by-one boundary. Only resolved logs (DirectionCorrect != null) are loaded.
        var recent = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId  &&
                        l.DirectionCorrect != null      &&
                        !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)  // newest first — streak is counted from the head
            .Take(maxMisses + 5)
            .AsNoTracking()
            .Select(l => l.DirectionCorrect!.Value)  // project to bool to avoid loading full entities
            .ToListAsync(ct);

        // No predictions yet for this model — skip; do not write a cooldown or clear an existing one.
        if (recent.Count == 0) return;

        // Count leading consecutive misses from most-recent prediction backward.
        // A 'miss' is DirectionCorrect == false. The loop stops at the first correct prediction.
        int streak = 0;
        foreach (var correct in recent)
        {
            if (!correct) streak++;
            else break;  // streak is broken — stop counting
        }

        // The EngineConfig key that downstream workers read to check cooldown expiry.
        // Pattern: "MLCooldown:EURUSD:H1:ExpiresAt"
        string configKey = $"{KeyPrefix}{symbol}:{timeframe}{KeySuffix}";

        if (streak < maxMisses)
        {
            // Streak is below the threshold — ensure no active cooldown is blocking signals.
            // Write a past timestamp (1 minute ago) so downstream readers see the cooldown as expired.
            // This effectively clears a previously-set cooldown without deleting the key.
            await UpsertConfigAsync(writeCtx, configKey,
                DateTime.UtcNow.AddMinutes(-1).ToString("o"), ct);
            return;
        }

        // Streak has reached or exceeded the threshold — apply exponential backoff cooldown.
        // extraMisses = how many misses beyond the minimum threshold (used as the backoff exponent).
        int extraMisses  = streak - maxMisses;
        double factor    = Math.Pow(2.0, extraMisses);  // doubles with each additional miss beyond threshold
        int cooldownMins = (int)Math.Min(baseMinutes * factor, maxMinutes);  // cap at maxMinutes

        var expiresAt = DateTime.UtcNow.AddMinutes(cooldownMins);

        // Write the expiry timestamp so downstream workers know when signals can resume.
        await UpsertConfigAsync(writeCtx, configKey, expiresAt.ToString("o"), ct);

        _logger.LogWarning(
            "Cooldown: model {Id} ({Symbol}/{Tf}) — {Streak} consecutive misses, " +
            "cooldown={Mins} min, expires {Exp:HH:mm} UTC",
            modelId, symbol, timeframe, streak, cooldownMins, expiresAt);
    }

    // ── EngineConfig upsert ───────────────────────────────────────────────────

    /// <summary>
    /// Upserts a single key/value pair in the <see cref="EngineConfig"/> table.
    /// Uses <c>ExecuteUpdateAsync</c> first (bulk update without loading the entity) for efficiency.
    /// Falls back to <c>Add</c> + <c>SaveChangesAsync</c> when the key does not yet exist.
    /// This two-step approach avoids the overhead of a SELECT + UPDATE roundtrip on the common
    /// path (the key already exists from a previous cooldown evaluation cycle).
    /// </summary>
    /// <param name="writeCtx">Write DbContext for persistence.</param>
    /// <param name="key">The EngineConfig key to create or update.</param>
    /// <param name="value">The new value to store (ISO-8601 timestamp string for cooldown expiry keys).</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        // Attempt bulk update — returns 0 if the key doesn't exist yet.
        int rows = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Value,         value)
                .SetProperty(c => c.LastUpdatedAt, DateTime.UtcNow),
                ct);

        if (rows == 0)
        {
            // Key does not exist — insert a new EngineConfig row.
            // IsHotReloadable = true allows the cooldown expiry to be manually overridden
            // via the EngineConfiguration API without restarting the worker.
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

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key does not exist or the stored
    /// string cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target type — typically <c>int</c> or <c>double</c>.</typeparam>
    /// <param name="ctx">Any DbContext with access to the EngineConfig set.</param>
    /// <param name="key">The EngineConfig key to look up.</param>
    /// <param name="defaultValue">Fallback returned when the key is absent or unparseable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed configuration value, or <paramref name="defaultValue"/>.</returns>
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
