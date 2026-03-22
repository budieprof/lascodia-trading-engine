using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects conflicting ML predictions for strongly correlated currency pairs and raises
/// an alert, preventing the trading engine from simultaneously taking opposing positions
/// that would partially cancel each other out or expose hidden correlated risk.
///
/// <b>Problem:</b> EURUSD and GBPUSD are typically 70–85% correlated. If the EURUSD model
/// predicts Buy while the GBPUSD model predicts Sell within the same evaluation window,
/// at least one prediction is almost certainly wrong. Acting on both signals without
/// investigation compounds directional risk.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>Read the pair correlation map from <c>EngineConfig</c> key
///         <c>MLCorrelation:PairMap</c> — a JSON object where each key is a base symbol
///         and the value is an array of correlated symbols,
///         e.g. <c>{"EURUSD":["GBPUSD","AUDUSD"]}</c>.</item>
///   <item>For each base symbol, load <see cref="TradeSignal"/> records with
///         <c>Status = Approved</c> created within the conflict detection window.</item>
///   <item>For each correlated peer, load its approved signals in the same window.</item>
///   <item>If the most recent approved base signal and the most recent approved peer signal
///         have opposing <c>MLPredictedDirection</c> values, fire an alert.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLCorrelation:PollIntervalSeconds</c>   — default 300 (5 min)</item>
///   <item><c>MLCorrelation:WindowMinutes</c>         — conflict window, default 60</item>
///   <item><c>MLCorrelation:PairMap</c>               — JSON correlation map, default empty</item>
///   <item><c>MLCorrelation:AlertDestination</c>      — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLCorrelatedSignalConflictWorker : BackgroundService
{
    // ── EngineConfig key constants ─────────────────────────────────────────────

    /// <summary>Seconds between conflict detection cycles (default 300 = 5 min).
    /// Five minutes is short enough to catch intra-session conflicts before both signals
    /// are executed, but long enough to avoid excessive DB load from frequent signal queries.</summary>
    private const string CK_PollSecs   = "MLCorrelation:PollIntervalSeconds";

    /// <summary>Width (in minutes) of the conflict detection window (default 60 min).
    /// Only approved signals generated within this window are compared for directional conflict.
    /// A 60-minute window covers most intra-session signal windows without reaching back so far
    /// that conflicts from a previous session are incorrectly flagged.</summary>
    private const string CK_Window     = "MLCorrelation:WindowMinutes";

    /// <summary>JSON object defining the correlation map between currency pairs.
    /// Format: <c>{"EURUSD":["GBPUSD","AUDUSD"],"USDJPY":["USDCHF"]}</c>.
    /// Each key is a base symbol; the value is an array of correlated peers to check against.
    /// The map is one-directional — EURUSD checks GBPUSD, but GBPUSD does not automatically
    /// check EURUSD unless explicitly listed. Configure both directions if bidirectional checking
    /// is required. An empty map (<c>{}</c>) disables all conflict detection.</summary>
    private const string CK_PairMap    = "MLCorrelation:PairMap";

    /// <summary>Alert destination identifier for conflict alerts (default "ml-ops").</summary>
    private const string CK_AlertDest  = "MLCorrelation:AlertDestination";

    private readonly IServiceScopeFactory                          _scopeFactory;
    private readonly ILogger<MLCorrelatedSignalConflictWorker>     _logger;

    /// <summary>
    /// Initialises the worker with scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">Used to create per-iteration DI scopes for safe scoped service access.</param>
    /// <param name="logger">Structured logger for correlation conflict detection and alert events.</param>
    public MLCorrelatedSignalConflictWorker(
        IServiceScopeFactory                           scopeFactory,
        ILogger<MLCorrelatedSignalConflictWorker>      logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Main background loop. Runs indefinitely until the host signals cancellation.
    /// On each iteration:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope to obtain scoped read/write DbContexts.</item>
    ///   <item>Reads the poll interval from <see cref="EngineConfig"/>.</item>
    ///   <item>Delegates to <see cref="DetectConflictsAsync"/> to evaluate all configured pair correlations.</item>
    ///   <item>Sleeps for the configured poll interval (default 5 min) before the next cycle.</item>
    /// </list>
    /// The 5-minute default allows conflicts to be detected and alerted before both signals
    /// advance through the <see cref="SignalOrderBridgeWorker"/> to order placement.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLCorrelatedSignalConflictWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 300;

            try
            {
                // New DI scope per iteration — DbContexts are scoped and must not outlive a single tick.
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Hot-reload: poll interval read from DB each cycle.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 300, stoppingToken);

                await DetectConflictsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLCorrelatedSignalConflictWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLCorrelatedSignalConflictWorker stopping.");
    }

    // ── Detection core ────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the correlation pair map from <see cref="EngineConfig"/> and checks each
    /// configured base symbol against its correlated peers for directional conflicts.
    ///
    /// <b>Batch-load optimisation:</b> Rather than querying signals per symbol, all relevant
    /// symbols (base + all peers) are collected into a single list and fetched in one DB query.
    /// The results are then grouped into an in-memory direction map for O(1) peer lookups.
    ///
    /// <b>Conflict definition:</b> A conflict exists when the most recent approved ML signal
    /// for the base symbol has an opposing <c>MLPredictedDirection</c> to the most recent
    /// approved ML signal for a correlated peer symbol, within the configured time window.
    /// "Most recent" is determined by <c>GeneratedAt DESC</c>.
    ///
    /// <b>Why only approved signals?</b> Pending signals may still be rejected by risk checks;
    /// comparing them would generate premature conflict alerts. Executed signals are already
    /// in the market and cannot be recalled, so detecting their conflicts has limited actionable value.
    /// Approved signals are the most dangerous — they are about to be executed and can still be
    /// stopped if the conflict is flagged in time.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for signal queries and alert deduplication.</param>
    /// <param name="writeCtx">Write DbContext for alert creation.</param>
    /// <param name="ct">Cancellation token from the host.</param>
    private async Task DetectConflictsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowMinutes = await GetConfigAsync<int>   (readCtx, CK_Window,    60,      ct);
        string pairMapJson   = await GetConfigAsync<string>(readCtx, CK_PairMap,   "{}",    ct);
        string alertDest     = await GetConfigAsync<string>(readCtx, CK_AlertDest, "ml-ops", ct);

        // Parse the pair correlation map from JSON.
        // Example: {"EURUSD":["GBPUSD","AUDUSD"],"USDJPY":["USDCHF"]}
        // PropertyNameCaseInsensitive allows both "EURUSD" and "eurusd" keys to match.
        Dictionary<string, List<string>> pairMap;
        try
        {
            pairMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                pairMapJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new();
        }
        catch (Exception ex)
        {
            // Malformed JSON in EngineConfig — log the error and skip this cycle entirely.
            _logger.LogWarning(ex, "MLCorrelation: failed to parse PairMap JSON — skipping.");
            return;
        }

        if (pairMap.Count == 0)
        {
            // No pairs configured — correlation detection is disabled. This is normal during
            // initial deployment before the ops team configures the pair map.
            _logger.LogDebug("MLCorrelation: PairMap is empty, nothing to check.");
            return;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-windowMinutes);

        // Collect all unique symbols that appear in the pair map (both base keys and peer values).
        // This allows a single DB query to load all relevant signals instead of one query per symbol.
        var allSymbols = pairMap.Keys
            .Concat(pairMap.Values.SelectMany(v => v))
            .Distinct()
            .ToList();

        // Batch-load the latest approved ML-predicted signals for all relevant symbols.
        // Only signals with a non-null MLPredictedDirection are included — signals lacking a
        // direction prediction cannot participate in directional conflict detection.
        var latestSignals = await readCtx.Set<TradeSignal>()
            .Where(ts => allSymbols.Contains(ts.Symbol)            &&
                         ts.Status == TradeSignalStatus.Approved    &&
                         ts.GeneratedAt >= cutoff                   &&
                         ts.MLPredictedDirection.HasValue           &&
                         !ts.IsDeleted)
            .AsNoTracking()
            .Select(ts => new
            {
                ts.Symbol,
                ts.MLPredictedDirection,
                ts.GeneratedAt,
            })
            .ToListAsync(ct);

        // Build symbol → latest predicted direction map.
        // For each symbol, take the most recently generated approved signal's direction.
        // This handles the case where a symbol has multiple approved signals in the window
        // (e.g. from different strategies) — we compare the freshest prediction only.
        var directionMap = latestSignals
            .GroupBy(s => s.Symbol)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.GeneratedAt).First().MLPredictedDirection!.Value);

        foreach (var (baseSymbol, peers) in pairMap)
        {
            ct.ThrowIfCancellationRequested();

            // Skip base symbols with no approved signal in the window — no conflict possible.
            if (!directionMap.TryGetValue(baseSymbol, out var baseDir)) continue;

            foreach (var peer in peers)
            {
                // Skip peers with no approved signal in the window — no conflict possible.
                if (!directionMap.TryGetValue(peer, out var peerDir)) continue;

                // Same direction = correlated pairs agree — no conflict, no alert.
                // Example: EURUSD Buy + GBPUSD Buy is expected behaviour for USD-selling regimes.
                if (baseDir == peerDir) continue;

                // Opposing directions on correlated pairs = conflict.
                // Example: EURUSD Buy + GBPUSD Sell is contradictory — at least one model
                // is miscalibrated for the current market regime.
                _logger.LogWarning(
                    "MLCorrelation: conflicting signals — {Base} predicts {BaseDir} " +
                    "but correlated pair {Peer} predicts {PeerDir} within {Window} min window.",
                    baseSymbol, baseDir, peer, peerDir, windowMinutes);

                // Avoid duplicating an already-active conflict alert for either symbol in the pair.
                // The check covers both the base and peer symbol to prevent double-alerting when
                // a symmetric pair map lists both directions (e.g. EURUSD→GBPUSD and GBPUSD→EURUSD).
                bool alertExists = await readCtx.Set<Alert>()
                    .AnyAsync(a => (a.Symbol == baseSymbol || a.Symbol == peer) &&
                                   a.AlertType == AlertType.MLModelDegraded     &&
                                   a.IsActive  && !a.IsDeleted, ct);

                if (alertExists) continue;

                // Create a conflict alert with full directional context in ConditionJson.
                // The alert is keyed to the baseSymbol so the AlertWorker can route it correctly.
                writeCtx.Set<Alert>().Add(new Alert
                {
                    AlertType     = AlertType.MLModelDegraded,
                    Symbol        = baseSymbol,
                    Channel       = AlertChannel.Webhook,
                    Destination   = alertDest,
                    ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        reason         = "correlated_signal_conflict",
                        severity       = "warning",
                        baseSymbol,
                        peerSymbol     = peer,
                        baseDirection  = baseDir.ToString(),
                        peerDirection  = peerDir.ToString(),
                        windowMinutes,
                    }),
                    IsActive = true,
                });
            }
        }

        // Single SaveChangesAsync call after processing all pairs — batches all alert inserts
        // into one DB round trip rather than one per conflict.
        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key does not exist or the stored
    /// string cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target type — typically <c>int</c> or <c>string</c>.</typeparam>
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
