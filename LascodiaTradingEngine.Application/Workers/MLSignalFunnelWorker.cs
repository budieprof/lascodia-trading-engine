using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Tracks the signal lifecycle funnel for ML-tagged trade signals:
/// <c>Pending → Approved → Executed</c> (and separately, the fraction that are
/// <c>Rejected</c> or <c>Expired</c> before execution).
///
/// <b>Problem:</b> A model's backtested performance assumes immediate execution
/// at the signal price. In production, signals that are approved by risk logic
/// but subsequently rejected or expire unfilled cause the live results to diverge
/// from backtested expectations — and this divergence is invisible to accuracy
/// or P&amp;L workers that only look at <i>executed</i> signals.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>For each symbol, count ML-tagged signals (those with a non-null
///         <see cref="TradeSignal.MLModelId"/>) in the look-back window,
///         grouped by final <see cref="TradeSignalStatus"/>.</item>
///   <item>Compute fill rate = <c>Executed / Created</c> and rejection rate
///         = <c>(Rejected + Expired) / Created</c>.</item>
///   <item>Write <c>MLFunnel:{Symbol}:FillRate</c> and
///         <c>MLFunnel:{Symbol}:RejectionRate</c> to <see cref="EngineConfig"/>.</item>
///   <item>Alert when fill rate &lt; <c>FillRateFloor</c> (default 0.50) or
///         rejection rate &gt; <c>RejectionRateCeiling</c> (default 0.40).</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLFunnel:PollIntervalSeconds</c>      — default 3600 (1 h)</item>
///   <item><c>MLFunnel:WindowDays</c>               — look-back, default 7</item>
///   <item><c>MLFunnel:MinSignals</c>               — minimum ML signals in window, default 10</item>
///   <item><c>MLFunnel:FillRateFloor</c>            — fill rate alert, default 0.50</item>
///   <item><c>MLFunnel:RejectionRateCeiling</c>     — rejection rate alert, default 0.40</item>
///   <item><c>MLFunnel:AlertDestination</c>         — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLSignalFunnelWorker : BackgroundService
{
    // ── EngineConfig key constants ─────────────────────────────────────────────

    /// <summary>Seconds between funnel check cycles (default 3600 = 1 h).
    /// Hourly is appropriate because fill/rejection rates are computed over a multi-day window
    /// and do not change dramatically within a single hour.</summary>
    private const string CK_PollSecs    = "MLFunnel:PollIntervalSeconds";

    /// <summary>Number of calendar days to look back when computing funnel metrics (default 7).
    /// A 7-day window smooths out day-of-week effects (e.g. low Monday liquidity) while
    /// remaining responsive to structural changes in execution quality.</summary>
    private const string CK_Window      = "MLFunnel:WindowDays";

    /// <summary>Minimum number of ML-tagged signals in the window before funnel metrics are
    /// computed for a symbol (default 10). Prevents false alerts on rarely-traded symbols.</summary>
    private const string CK_MinSig      = "MLFunnel:MinSignals";

    /// <summary>Fill rate below this value triggers an alert (default 0.50).
    /// Fill rate = Executed / Total. If fewer than half of ML signals result in an executed trade,
    /// the gap between backtested and live performance is likely to be significant.</summary>
    private const string CK_FillFloor   = "MLFunnel:FillRateFloor";

    /// <summary>Rejection rate above this value triggers an alert (default 0.40).
    /// Rejection rate = (Rejected + Expired) / Total. A high rejection rate indicates the
    /// risk layer or execution layer is systematically blocking ML signals, which degrades
    /// the model's effective live coverage even if accuracy is technically sound.</summary>
    private const string CK_RejCeiling  = "MLFunnel:RejectionRateCeiling";

    /// <summary>Alert destination identifier (e.g. Webhook channel name) for funnel alerts.</summary>
    private const string CK_AlertDest   = "MLFunnel:AlertDestination";

    private readonly IServiceScopeFactory             _scopeFactory;
    private readonly ILogger<MLSignalFunnelWorker>    _logger;

    /// <summary>
    /// Initialises the worker with scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">Used to create per-iteration DI scopes for safe scoped service access.</param>
    /// <param name="logger">Structured logger for funnel metric computation and alert events.</param>
    public MLSignalFunnelWorker(
        IServiceScopeFactory              scopeFactory,
        ILogger<MLSignalFunnelWorker>     logger)
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
    ///   <item>Delegates to <see cref="CheckFunnelAsync"/> to compute funnel metrics for all symbols.</item>
    ///   <item>Sleeps for the configured poll interval (default 1 h) before the next cycle.</item>
    /// </list>
    /// Additionally, this worker acts as an observability pipeline by writing per-symbol fill rate
    /// and rejection rate metrics to <see cref="EngineConfig"/> on every cycle — these are readable
    /// by dashboard tooling and other workers without requiring a separate query.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSignalFunnelWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                // New DI scope per iteration to avoid stale DbContext state across ticks.
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Hot-reload poll interval from DB each cycle.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await CheckFunnelAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLSignalFunnelWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSignalFunnelWorker stopping.");
    }

    // ── Funnel check core ─────────────────────────────────────────────────────

    /// <summary>
    /// Loads all ML-tagged signals across all symbols within the configured look-back window,
    /// groups them by symbol, and invokes <see cref="CheckSymbolFunnelAsync"/> for each symbol
    /// that has at least <c>minSignals</c> records.
    ///
    /// Signals are identified as ML-tagged by having a non-null <c>MLModelId</c> on the
    /// <see cref="TradeSignal"/> record — only signals that went through the ML scorer are
    /// relevant for funnel metric computation.
    ///
    /// All data is loaded in a single query and grouped in-memory to avoid N+1 queries
    /// per symbol.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for signal queries.</param>
    /// <param name="writeCtx">Write DbContext for metric upserts and alert creation.</param>
    /// <param name="ct">Cancellation token from the host.</param>
    private async Task CheckFunnelAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Read all funnel thresholds at once — avoids repeated EngineConfig queries per symbol.
        int    windowDays    = await GetConfigAsync<int>   (readCtx, CK_Window,     7,       ct);
        int    minSignals    = await GetConfigAsync<int>   (readCtx, CK_MinSig,     10,      ct);
        double fillFloor     = await GetConfigAsync<double>(readCtx, CK_FillFloor,  0.50,    ct);
        double rejCeiling    = await GetConfigAsync<double>(readCtx, CK_RejCeiling, 0.40,    ct);
        string alertDest     = await GetConfigAsync<string>(readCtx, CK_AlertDest,  "ml-ops", ct);

        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        // Load all ML-tagged signals in the window, grouped by symbol + status.
        // Only signals with a non-null MLModelId are included — signals generated without
        // ML scoring are out of scope for this funnel analysis.
        var signals = await readCtx.Set<TradeSignal>()
            .Where(s => s.MLModelId   != null          &&
                        s.GeneratedAt >= windowStart    &&
                        !s.IsDeleted)
            .AsNoTracking()
            // Project to minimum needed fields to avoid fetching unused columns (e.g. ReasonJson).
            .Select(s => new { s.Symbol, s.Status })
            .ToListAsync(ct);

        if (signals.Count == 0) return;

        // Group in-memory by symbol — the dataset is typically small enough that a DB GROUP BY
        // would not offer meaningful performance improvement over LINQ grouping.
        var bySymbol = signals.GroupBy(s => s.Symbol);

        foreach (var group in bySymbol)
        {
            ct.ThrowIfCancellationRequested();

            string symbol = group.Key;
            var    list   = group.ToList();

            // Skip symbols with too few signals — their funnel metrics would be noisy.
            if (list.Count < minSignals) continue;

            try
            {
                await CheckSymbolFunnelAsync(
                    symbol, list.Select(s => s.Status).ToList(),
                    fillFloor, rejCeiling, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                // Isolate per-symbol failures so one bad symbol doesn't skip all remaining funnel checks.
                _logger.LogWarning(ex,
                    "SignalFunnel: check failed for {Symbol} — skipping.", symbol);
            }
        }
    }

    /// <summary>
    /// Computes funnel metrics for a single symbol, writes them to <see cref="EngineConfig"/>
    /// as observability data, and raises an alert if either the fill rate or rejection rate
    /// crosses its configured threshold.
    ///
    /// <b>Funnel stages tracked:</b>
    /// <list type="bullet">
    ///   <item><b>Pending</b>  — signal created, awaiting risk/approval evaluation</item>
    ///   <item><b>Approved</b> — signal passed risk checks, eligible for execution</item>
    ///   <item><b>Executed</b> — signal converted to an order and submitted to the broker</item>
    ///   <item><b>Rejected</b> — signal blocked by risk filter or manual rejection</item>
    ///   <item><b>Expired</b>  — signal timed out before execution (e.g. market moved too far)</item>
    /// </list>
    ///
    /// <b>Key metrics written to EngineConfig:</b>
    /// <list type="bullet">
    ///   <item><c>MLFunnel:{Symbol}:FillRate</c>      — fraction of total signals that were executed</item>
    ///   <item><c>MLFunnel:{Symbol}:RejectionRate</c> — fraction that were rejected or expired</item>
    /// </list>
    /// These keys are written on every cycle regardless of alert status, providing a live
    /// observability feed that can be scraped by external monitoring tools.
    ///
    /// <b>Alert deduplication:</b> only one active <see cref="AlertType.MLModelDegraded"/> alert
    /// is created per symbol. Once the alert is resolved (deactivated by the ops team), the next
    /// cycle will create a new one if the condition persists.
    /// </summary>
    /// <param name="symbol">Currency pair symbol to compute funnel metrics for.</param>
    /// <param name="statuses">List of <see cref="TradeSignalStatus"/> values for all ML signals in the window.</param>
    /// <param name="fillRateFloor">Fill rate below this value triggers an alert.</param>
    /// <param name="rejectionRateCeiling">Rejection rate above this value triggers an alert.</param>
    /// <param name="alertDest">Alert channel destination identifier.</param>
    /// <param name="readCtx">Read-only DbContext for alert deduplication check.</param>
    /// <param name="writeCtx">Write DbContext for metric upserts and alert creation.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task CheckSymbolFunnelAsync(
        string                                  symbol,
        List<TradeSignalStatus>                 statuses,
        double                                  fillRateFloor,
        double                                  rejectionRateCeiling,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Bucket the status list into counts for each funnel stage.
        int total    = statuses.Count;
        int executed = statuses.Count(s => s == TradeSignalStatus.Executed);
        int rejected = statuses.Count(s => s == TradeSignalStatus.Rejected);
        int expired  = statuses.Count(s => s == TradeSignalStatus.Expired);
        int approved = statuses.Count(s => s == TradeSignalStatus.Approved);
        int pending  = statuses.Count(s => s == TradeSignalStatus.Pending);

        // Fill rate: fraction of all signals that reached the Executed state.
        // Low fill rate indicates a bottleneck at either the approval or execution stage.
        double fillRate      = (double)executed / total;

        // Rejection rate: fraction of all signals that were blocked (Rejected) or timed out (Expired).
        // Rejected + Expired are combined because both represent signals that were created by the ML model
        // but never resulted in a trade — regardless of the reason.
        double rejectionRate = (double)(rejected + expired) / total;

        _logger.LogDebug(
            "SignalFunnel: {Symbol} — total={T} executed={E} approved={A} rejected={R} expired={X} pending={P} " +
            "fillRate={FR:P1} rejRate={RR:P1}",
            symbol, total, executed, approved, rejected, expired, pending, fillRate, rejectionRate);

        // Write observability keys to EngineConfig on every cycle — these serve as live metrics
        // regardless of whether the thresholds are breached, enabling external monitoring.
        await UpsertConfigAsync(writeCtx, $"MLFunnel:{symbol}:FillRate",      fillRate.ToString("F4"),      ct);
        await UpsertConfigAsync(writeCtx, $"MLFunnel:{symbol}:RejectionRate", rejectionRate.ToString("F4"), ct);

        // Check whether either threshold is breached.
        bool fillAlert = fillRate < fillRateFloor;
        bool rejAlert  = rejectionRate > rejectionRateCeiling;

        // Both conditions are within bounds — no alert needed.
        if (!fillAlert && !rejAlert) return;

        // Prefer the fill rate reason when both conditions are true simultaneously,
        // as a low fill rate is the more actionable diagnostic (signals aren't being traded at all).
        string reason = fillAlert ? "signal_fill_rate_low" : "signal_rejection_rate_high";

        _logger.LogWarning(
            "SignalFunnel: {Symbol} — {Reason}: fillRate={FR:P1} (floor {FF:P0}) " +
            "rejRate={RR:P1} (ceiling {RC:P0})",
            symbol, reason, fillRate, fillRateFloor, rejectionRate, rejectionRateCeiling);

        // Deduplicate alerts — skip creation if an active alert already exists for this symbol.
        // Once the ops team resolves (deactivates) the alert, the condition will be rechecked on
        // the next cycle and a new alert created if it still applies.
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                  &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        // Create a comprehensive alert with full funnel breakdown in ConditionJson so
        // the AlertWorker and downstream consumers have all context without querying DB again.
        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = symbol,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason,
                severity             = "warning",
                symbol,
                totalSignals         = total,
                executedSignals      = executed,
                approvedSignals      = approved,
                rejectedSignals      = rejected,
                expiredSignals       = expired,
                fillRate,
                fillRateFloor,
                rejectionRate,
                rejectionRateCeiling,
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── EngineConfig upsert ───────────────────────────────────────────────────

    /// <summary>
    /// Upserts a single key/value pair in the <see cref="EngineConfig"/> table.
    /// Attempts a bulk <c>ExecuteUpdateAsync</c> first (no entity load required), then falls back
    /// to an <c>Add</c> + <c>SaveChangesAsync</c> insert when the key does not yet exist.
    /// This pattern avoids unnecessary entity tracking overhead on the common update path.
    /// </summary>
    /// <param name="writeCtx">Write DbContext for persistence.</param>
    /// <param name="key">The EngineConfig key to create or update (e.g. <c>MLFunnel:EURUSD:FillRate</c>).</param>
    /// <param name="value">The string value to store (numeric metric as fixed-precision string).</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        // Bulk update path — efficient when the key already exists from a previous cycle.
        int rows = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Value,         value)
                .SetProperty(c => c.LastUpdatedAt, DateTime.UtcNow),
                ct);

        if (rows == 0)
        {
            // Key does not exist yet — first cycle or new symbol. Insert a new row.
            writeCtx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key             = key,
                Value           = value,
                DataType        = ConfigDataType.String,
                Description     = "ML signal funnel metric. Written by MLSignalFunnelWorker.",
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
    /// <typeparam name="T">Target type — typically <c>int</c>, <c>double</c>, or <c>string</c>.</typeparam>
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
