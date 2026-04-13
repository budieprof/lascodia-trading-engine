using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that evaluates all active <see cref="Alert"/> conditions every
/// 30 seconds and dispatches notifications via <see cref="IAlertDispatcher"/> when
/// conditions are met.
/// </summary>
/// <remarks>
/// <para>
/// <b>Polling interval:</b> 30 seconds (<see cref="PollingInterval"/>). This gives
/// near-real-time notification for price-level breaches while keeping the DB query
/// rate reasonable even with many active alerts.
/// </para>
///
/// <para>
/// <b>Supported alert types:</b>
/// <list type="bullet">
///   <item>
///     <term>PriceLevel</term>
///     <description>
///       Fires when the live mid-price crosses a threshold. ConditionJson must be
///       <c>{"Price":1.0850,"Direction":"Above"}</c> or <c>"Below"</c>. Prices are
///       sourced from <see cref="ILivePriceCache"/> which is populated by
///       <c>MarketDataWorker</c>.
///     </description>
///   </item>
///   <item>
///     <term>DrawdownBreached</term>
///     <description>
///       Fires when the latest <see cref="DrawdownSnapshot.DrawdownPct"/> exceeds a
///       threshold. ConditionJson: <c>{"ThresholdPct":5.0}</c>.
///     </description>
///   </item>
///   <item>
///     <term>SignalGenerated</term>
///     <description>
///       Fires once per new <see cref="TradeSignal"/> for the alert's symbol, created
///       after <see cref="Alert.LastTriggeredAt"/>. No cooldown — each new signal is
///       a distinct event.
///     </description>
///   </item>
///   <item>
///     <term>OrderFilled</term>
///     <description>
///       Fires once per order filled for the alert's symbol after
///       <see cref="Alert.LastTriggeredAt"/>.
///     </description>
///   </item>
///   <item>
///     <term>PositionClosed</term>
///     <description>
///       Fires once per position closed for the alert's symbol after
///       <see cref="Alert.LastTriggeredAt"/>.
///     </description>
///   </item>
/// </list>
/// </para>
///
/// <para>
/// <b>Cooldown mechanism:</b>
/// Level-based alert types (<c>PriceLevel</c>, <c>DrawdownBreached</c>) impose a
/// 60-minute cooldown (<see cref="LevelCooldown"/>) after firing to prevent alert
/// fatigue when a condition persists across many polling cycles. Event-based types
/// (<c>SignalGenerated</c>, <c>OrderFilled</c>, <c>PositionClosed</c>) use
/// <see cref="Alert.LastTriggeredAt"/> as a cursor instead — each fire updates the
/// cursor to the event timestamp so only new events trigger the next notification.
/// </para>
///
/// <para>
/// <b>Auto-resolution:</b>
/// Level-based alerts (PriceLevel, DrawdownBreached) support auto-resolution. When a
/// previously triggered alert's condition clears (price drops back below threshold,
/// drawdown recovers), a "resolved" notification is dispatched and
/// <see cref="Alert.AutoResolvedAt"/> is set. This auto-resets when the condition
/// triggers again.
/// </para>
///
/// <para>
/// <b>Severity-based routing:</b>
/// Alerts are dispatched via <see cref="IAlertDispatcher.DispatchAsync"/>
/// which routes notifications to channels based on the alert's <see cref="AlertSeverity"/>:
/// Critical/High → Telegram + Webhook, Medium/Info → Webhook.
/// </para>
///
/// <para>
/// <b>Pipeline position:</b> AlertWorker sits at the end of the monitoring pipeline.
/// It does not generate events itself; it reacts to data written by other workers
/// (price cache, <see cref="DrawdownMonitorWorker"/>, strategy/order workers) and
/// delivers notifications to operators via the channels configured on each alert
/// (Webhook, Email, Telegram).
/// </para>
/// </remarks>
public class AlertWorker : BackgroundService
{
    /// <summary>How often the worker scans active alerts (30 seconds).</summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum time that must elapse between two firings of the same level-based alert
    /// (PriceLevel, DrawdownBreached). Prevents alert storms when a condition persists
    /// for multiple polling cycles.
    /// </summary>
    private static readonly TimeSpan LevelCooldown   = TimeSpan.FromMinutes(60);

    /// <summary>Max backoff delay on consecutive failures (5 minutes).</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private int _consecutiveFailures;

    /// <summary>
    /// Alert types that the polling-based worker evaluates. Other types (MLModelDegraded,
    /// DataQualityIssue, etc.) are dispatched directly by their respective specialized
    /// workers and do not need polling.
    /// </summary>
    private static readonly HashSet<AlertType> PolledAlertTypes = new()
    {
        AlertType.PriceLevel,
        AlertType.DrawdownBreached,
        AlertType.SignalGenerated,
        AlertType.OrderFilled,
        AlertType.PositionClosed
    };

    private readonly ILogger<AlertWorker>  _logger;
    private readonly IServiceScopeFactory  _scopeFactory;

    /// <summary>
    /// Singleton price cache used by <see cref="EvaluatePriceLevel"/>. Injected directly
    /// (not via scope) because it is registered as a Singleton and is safe to call from
    /// multiple threads concurrently.
    /// </summary>
    private readonly ILivePriceCache       _priceCache;

    /// <summary>
    /// Initialises the worker.
    /// </summary>
    /// <param name="logger">Structured logger.</param>
    /// <param name="scopeFactory">Factory for creating per-cycle DI scopes.</param>
    /// <param name="priceCache">Singleton live price cache used for PriceLevel evaluation.</param>
    public AlertWorker(
        ILogger<AlertWorker>  logger,
        IServiceScopeFactory  scopeFactory,
        ILivePriceCache       priceCache)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _priceCache   = priceCache;
    }

    /// <summary>
    /// Entry point for the hosted service. Evaluates all active alerts on each polling
    /// cycle and dispatches notifications for those that have triggered. Errors inside a
    /// single cycle are caught and logged so the loop continues on the next cycle.
    /// </summary>
    /// <param name="stoppingToken">Signalled by the host on shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AlertWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAlertsAsync(stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex,
                    "Unexpected error in AlertWorker polling loop (consecutive={Count})",
                    _consecutiveFailures);
            }

            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    PollingInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : PollingInterval;

            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("AlertWorker stopped");
    }

    /// <summary>
    /// Core alert evaluation method. Loads all active, non-deleted alerts from the DB,
    /// evaluates each one against its type-specific condition, and dispatches notifications
    /// for those that have triggered.
    ///
    /// <para>
    /// The write context is used <em>only</em> to update <see cref="Alert.LastTriggeredAt"/>
    /// on triggered alerts, preventing duplicate firings on the next polling cycle. All
    /// condition reads use the read context.
    /// </para>
    ///
    /// <para>
    /// A single <c>SaveChangesAsync</c> is called at the end if any alert was triggered,
    /// batching all timestamp updates into one DB round-trip.
    /// </para>
    /// </summary>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task EvaluateAlertsAsync(CancellationToken ct)
    {
        using var scope      = _scopeFactory.CreateScope();
        var readContext      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeContext     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var dispatcher       = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();

        // Load only alert types that this worker handles. Other types (MLModelDegraded,
        // DataQualityIssue, etc.) are dispatched by their respective specialized workers.
        var alerts = await readContext.GetDbContext()
            .Set<Alert>()
            .Where(x => x.IsActive && !x.IsDeleted && PolledAlertTypes.Contains(x.AlertType))
            .ToListAsync(ct);

        if (alerts.Count == 0) return;

        // Track whether any alert was triggered so we can batch the timestamp update.
        bool anyUpdated = false;

        foreach (var alert in alerts)
        {
            try
            {
                // Dispatch to the appropriate type-specific evaluator.
                // Returns (triggered, message, eventTimestamp) where eventTimestamp is the
                // timestamp of the event that caused the trigger (for event-based types) or
                // null (for level-based types that use UtcNow).
                var (triggered, message, eventTimestamp) = alert.AlertType switch
                {
                    AlertType.PriceLevel       => EvaluatePriceLevel(alert),
                    AlertType.DrawdownBreached => await EvaluateDrawdownAsync(alert, readContext, ct),
                    AlertType.SignalGenerated  => await EvaluateSignalGeneratedAsync(alert, readContext, ct),
                    AlertType.OrderFilled      => await EvaluateOrderFilledAsync(alert, readContext, ct),
                    AlertType.PositionClosed   => await EvaluatePositionClosedAsync(alert, readContext, ct),
                    _                          => (false, string.Empty, (DateTime?)null)
                };

                // Auto-resolution for level-based alerts: if the condition has cleared
                // since the last trigger, send a "resolved" notification.
                if (!triggered && IsLevelBased(alert.AlertType) && alert.LastTriggeredAt.HasValue)
                {
                    if (await HandleAutoResolutionAsync(alert, writeContext, dispatcher, ct))
                        anyUpdated = true;
                    continue;
                }

                if (!triggered) continue;

                // Clear any previous auto-resolution since the condition is active again.
                // Load the entity via the write context so EF tracks the property change.
                var tracked = await writeContext.GetDbContext()
                    .Set<Alert>()
                    .FirstOrDefaultAsync(x => x.Id == alert.Id, ct);

                if (tracked is null) continue;

                // Dispatch FIRST, then update the cursor. If dispatch throws, the cursor
                // does not advance and the event will be retried on the next cycle.
                await dispatcher.DispatchAsync(alert, message, ct);

                // For event-based types, set cursor to the event's own timestamp so
                // subsequent events in the same batch are picked up on the next cycle.
                // For level-based types, use UtcNow (cooldown prevents re-triggering).
                tracked.LastTriggeredAt = eventTimestamp ?? DateTime.UtcNow;
                tracked.AutoResolvedAt  = null; // Reset auto-resolution on re-trigger
                anyUpdated = true;

                _logger.LogInformation(
                    "AlertWorker: fired alert {AlertId} ({Type}/{Symbol}) — {Message}",
                    alert.Id, alert.AlertType, alert.Symbol, message);
            }
            catch (Exception ex)
            {
                // Per-alert errors are isolated so one bad alert does not block others.
                _logger.LogError(ex,
                    "AlertWorker: error evaluating alert {AlertId} ({Type}/{Symbol})",
                    alert.Id, alert.AlertType, alert.Symbol);
            }
        }

        // Batch all LastTriggeredAt updates into a single SaveChanges call.
        if (anyUpdated)
            await writeContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Checks whether a previously triggered level-based alert should auto-resolve
    /// (condition has cleared). Dispatches a "resolved" notification and sets
    /// <see cref="Alert.AutoResolvedAt"/>.
    /// </summary>
    /// <returns><c>true</c> if the alert was auto-resolved and needs saving.</returns>
    private async Task<bool> HandleAutoResolutionAsync(
        Alert alert, IWriteApplicationDbContext writeContext,
        IAlertDispatcher dispatcher, CancellationToken ct)
    {
        // Already resolved — nothing to do until the condition triggers again.
        if (alert.AutoResolvedAt.HasValue) return false;

        var tracked = await writeContext.GetDbContext()
            .Set<Alert>()
            .FirstOrDefaultAsync(x => x.Id == alert.Id, ct);

        if (tracked is null) return false;

        // Dispatch the auto-resolve notification. The dispatcher sets AutoResolvedAt
        // and sends a "resolved" message through the severity-based channels.
        await dispatcher.TryAutoResolveAsync(tracked, conditionStillActive: false, ct);
        return true;
    }

    // ── Evaluators ────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates a <see cref="AlertType.PriceLevel"/> alert using the live price cache.
    /// The mid-price is computed as <c>(Bid + Ask) / 2</c> and compared against the
    /// configured threshold and direction.
    ///
    /// <para>
    /// Short-circuits immediately if the alert is within its cooldown window or if no
    /// live price is available for the symbol (e.g. market is closed or cache is stale).
    /// </para>
    ///
    /// <para>
    /// ConditionJson schema: <c>{"Price": 1.0850, "Direction": "Above"|"Below"}</c>
    /// </para>
    /// </summary>
    /// <param name="alert">The alert to evaluate.</param>
    /// <returns>A tuple indicating whether the alert triggered, the notification message,
    /// and null event timestamp (level-based uses UtcNow).</returns>
    private (bool Triggered, string Message, DateTime? EventTimestamp) EvaluatePriceLevel(Alert alert)
    {
        // Enforce the 60-minute cooldown to prevent repeated firings while price stays above/below threshold.
        if (InCooldown(alert)) return (false, string.Empty, null);

        var price = _priceCache.Get(alert.Symbol);
        if (price is null) return (false, string.Empty, null); // Symbol not in cache — skip silently

        // Mid-price is the standard reference for level-based alerts;
        // using the spread mid avoids false triggers from wide bid/ask spreads.
        decimal mid = (price.Value.Bid + price.Value.Ask) / 2m;

        JsonElement condition;
        try
        {
            using var doc = JsonDocument.Parse(alert.ConditionJson);
            condition = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "AlertWorker: malformed ConditionJson for PriceLevel alert {AlertId} ({Symbol}) — skipping until operator fixes the definition",
                alert.Id, alert.Symbol);
            return (false, string.Empty, null);
        }

        if (!condition.TryGetProperty("Price",     out var priceEl))     return (false, string.Empty, null);
        if (!condition.TryGetProperty("Direction", out var directionEl)) return (false, string.Empty, null);

        decimal threshold = priceEl.GetDecimal();
        string  direction = directionEl.GetString() ?? string.Empty;

        // Case-insensitive comparison so "above", "Above", and "ABOVE" all match.
        bool hit = direction.Equals("Above", StringComparison.OrdinalIgnoreCase)
            ? mid >= threshold
            : mid <= threshold;

        return hit
            ? (true, $"{alert.Symbol} mid price {mid:F5} is {direction.ToLower()} threshold {threshold:F5}", null)
            : (false, string.Empty, null);
    }

    /// <summary>
    /// Evaluates a <see cref="AlertType.DrawdownBreached"/> alert by comparing the latest
    /// <see cref="DrawdownSnapshot.DrawdownPct"/> against the configured threshold.
    /// Subject to the 60-minute cooldown to prevent repeated firings during a sustained
    /// drawdown period.
    ///
    /// <para>
    /// ConditionJson schema: <c>{"ThresholdPct": 5.0}</c>
    /// </para>
    /// </summary>
    private async Task<(bool Triggered, string Message, DateTime? EventTimestamp)> EvaluateDrawdownAsync(
        Alert alert, IReadApplicationDbContext ctx, CancellationToken ct)
    {
        if (InCooldown(alert)) return (false, string.Empty, null);

        // The most recent snapshot is the authoritative current drawdown level.
        var snapshot = await ctx.GetDbContext()
            .Set<DrawdownSnapshot>()
            .OrderByDescending(x => x.RecordedAt)
            .FirstOrDefaultAsync(ct);

        if (snapshot is null) return (false, string.Empty, null);

        JsonElement condition;
        try
        {
            using var doc = JsonDocument.Parse(alert.ConditionJson);
            condition = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "AlertWorker: malformed ConditionJson for DrawdownBreached alert {AlertId} — skipping until operator fixes the definition",
                alert.Id);
            return (false, string.Empty, null);
        }

        if (!condition.TryGetProperty("ThresholdPct", out var thresholdEl)) return (false, string.Empty, null);

        decimal threshold = thresholdEl.GetDecimal();

        return snapshot.DrawdownPct >= threshold
            ? (true, $"Drawdown {snapshot.DrawdownPct:F2}% breached threshold {threshold:F2}%", null)
            : (false, string.Empty, null);
    }

    /// <summary>
    /// Evaluates a <see cref="AlertType.SignalGenerated"/> alert by checking for a new
    /// <see cref="TradeSignal"/> for the alert's symbol created after the last trigger time.
    /// Takes the earliest qualifying signal (oldest-first) so signals are not skipped if
    /// multiple arrive between polling cycles. Returns the signal's own timestamp as the
    /// event cursor so subsequent signals are picked up on the next cycle.
    /// </summary>
    private async Task<(bool Triggered, string Message, DateTime? EventTimestamp)> EvaluateSignalGeneratedAsync(
        Alert alert, IReadApplicationDbContext ctx, CancellationToken ct)
    {
        // Use DateTime.MinValue as the starting cursor when the alert has never fired,
        // ensuring all signals ever created for the symbol are considered on first run.
        var since = alert.LastTriggeredAt ?? DateTime.MinValue;

        var signal = await ctx.GetDbContext()
            .Set<TradeSignal>()
            .Where(x => x.Symbol       == alert.Symbol
                     && x.GeneratedAt  > since
                     && !x.IsDeleted)
            .OrderBy(x => x.GeneratedAt) // Oldest first — process events in chronological order
            .FirstOrDefaultAsync(ct);

        if (signal is null) return (false, string.Empty, null);

        return (true,
            $"New trade signal generated for {alert.Symbol} at {signal.GeneratedAt:u}",
            signal.GeneratedAt);
    }

    /// <summary>
    /// Evaluates a <see cref="AlertType.OrderFilled"/> alert by checking for a new filled
    /// <see cref="Order"/> for the alert's symbol with a fill timestamp after the last trigger.
    /// Returns the order's fill timestamp as the event cursor.
    /// </summary>
    private async Task<(bool Triggered, string Message, DateTime? EventTimestamp)> EvaluateOrderFilledAsync(
        Alert alert, IReadApplicationDbContext ctx, CancellationToken ct)
    {
        var since = alert.LastTriggeredAt ?? DateTime.MinValue;

        var order = await ctx.GetDbContext()
            .Set<Order>()
            .Where(x => x.Symbol   == alert.Symbol
                     && x.Status   == OrderStatus.Filled
                     && x.FilledAt > since
                     && !x.IsDeleted)
            .OrderBy(x => x.FilledAt) // Oldest first — advance cursor one event at a time
            .FirstOrDefaultAsync(ct);

        if (order is null) return (false, string.Empty, null);

        return (true,
            $"Order {order.Id} filled for {alert.Symbol} at {order.FilledAt:u}",
            order.FilledAt);
    }

    /// <summary>
    /// Evaluates a <see cref="AlertType.PositionClosed"/> alert by checking for a
    /// <see cref="Position"/> closed for the alert's symbol after the last trigger time.
    /// Returns the position's close timestamp as the event cursor.
    /// </summary>
    private async Task<(bool Triggered, string Message, DateTime? EventTimestamp)> EvaluatePositionClosedAsync(
        Alert alert, IReadApplicationDbContext ctx, CancellationToken ct)
    {
        var since = alert.LastTriggeredAt ?? DateTime.MinValue;

        var position = await ctx.GetDbContext()
            .Set<Position>()
            .Where(x => x.Symbol   == alert.Symbol
                     && x.Status   == PositionStatus.Closed
                     && x.ClosedAt > since
                     && !x.IsDeleted)
            .OrderBy(x => x.ClosedAt) // Oldest first
            .FirstOrDefaultAsync(ct);

        if (position is null) return (false, string.Empty, null);

        return (true,
            $"Position {position.Id} closed for {alert.Symbol} at {position.ClosedAt:u}",
            position.ClosedAt);
    }

    /// <summary>
    /// Returns <c>true</c> if the alert has fired recently enough that the 60-minute
    /// cooldown window has not yet elapsed. Used exclusively by level-based alert types
    /// (PriceLevel, DrawdownBreached) to suppress repeated notifications while a
    /// condition persists across multiple polling cycles.
    /// </summary>
    /// <param name="alert">The alert to check.</param>
    private static bool InCooldown(Alert alert) =>
        alert.LastTriggeredAt.HasValue
        && DateTime.UtcNow - alert.LastTriggeredAt.Value < LevelCooldown;

    /// <summary>
    /// Returns <c>true</c> for alert types that represent a sustained condition
    /// (PriceLevel, DrawdownBreached) as opposed to discrete events (SignalGenerated, etc.).
    /// Level-based alerts support cooldowns and auto-resolution.
    /// </summary>
    private static bool IsLevelBased(AlertType type) =>
        type is AlertType.PriceLevel or AlertType.DrawdownBreached;
}
