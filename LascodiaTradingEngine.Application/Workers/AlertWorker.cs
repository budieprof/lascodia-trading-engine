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
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in AlertWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
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

        // Load all active alerts — IsActive is controlled by the operator via the API.
        var alerts = await readContext.GetDbContext()
            .Set<Alert>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        if (alerts.Count == 0) return;

        // Track whether any alert was triggered so we can batch the timestamp update.
        bool anyUpdated = false;

        foreach (var alert in alerts)
        {
            try
            {
                // Dispatch to the appropriate type-specific evaluator.
                // The switch expression returns a (triggered, message) tuple.
                var (triggered, message) = alert.AlertType switch
                {
                    AlertType.PriceLevel       => EvaluatePriceLevel(alert),
                    AlertType.DrawdownBreached => await EvaluateDrawdownAsync(alert, readContext, ct),
                    AlertType.SignalGenerated  => await EvaluateSignalGeneratedAsync(alert, readContext, ct),
                    AlertType.OrderFilled      => await EvaluateOrderFilledAsync(alert, readContext, ct),
                    AlertType.PositionClosed   => await EvaluatePositionClosedAsync(alert, readContext, ct),
                    _                          => (false, string.Empty)
                };

                if (!triggered) continue;

                // Load the entity via the write context so EF tracks the property change.
                // Using the read context here would not mark the entity as Modified.
                var tracked = await writeContext.GetDbContext()
                    .Set<Alert>()
                    .FirstOrDefaultAsync(x => x.Id == alert.Id, ct);

                if (tracked is null) continue;

                // Update the trigger timestamp — prevents this alert from firing again
                // within the cooldown window (level types) or on the same event (event types).
                tracked.LastTriggeredAt = DateTime.UtcNow;
                anyUpdated = true;

                // Deliver the notification through all configured channels
                // (Webhook, Email, Telegram) via the alert dispatcher.
                await dispatcher.DispatchAsync(alert, message, ct);

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
    /// <returns>A tuple indicating whether the alert triggered and the notification message.</returns>
    private (bool Triggered, string Message) EvaluatePriceLevel(Alert alert)
    {
        // Enforce the 60-minute cooldown to prevent repeated firings while price stays above/below threshold.
        if (InCooldown(alert)) return (false, string.Empty);

        var price = _priceCache.Get(alert.Symbol);
        if (price is null) return (false, string.Empty); // Symbol not in cache — skip silently

        // Mid-price is the standard reference for level-based alerts;
        // using the spread mid avoids false triggers from wide bid/ask spreads.
        decimal mid = (price.Value.Bid + price.Value.Ask) / 2m;

        JsonElement condition;
        try { condition = JsonDocument.Parse(alert.ConditionJson).RootElement; }
        catch { return (false, string.Empty); } // Malformed JSON — skip; operator must fix the alert definition

        if (!condition.TryGetProperty("Price",     out var priceEl))     return (false, string.Empty);
        if (!condition.TryGetProperty("Direction", out var directionEl)) return (false, string.Empty);

        decimal threshold = priceEl.GetDecimal();
        string  direction = directionEl.GetString() ?? string.Empty;

        // Case-insensitive comparison so "above", "Above", and "ABOVE" all match.
        bool hit = direction.Equals("Above", StringComparison.OrdinalIgnoreCase)
            ? mid >= threshold
            : mid <= threshold;

        return hit
            ? (true, $"{alert.Symbol} mid price {mid:F5} is {direction.ToLower()} threshold {threshold:F5}")
            : (false, string.Empty);
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
    private async Task<(bool Triggered, string Message)> EvaluateDrawdownAsync(
        Alert alert, IReadApplicationDbContext ctx, CancellationToken ct)
    {
        if (InCooldown(alert)) return (false, string.Empty);

        // The most recent snapshot is the authoritative current drawdown level.
        var snapshot = await ctx.GetDbContext()
            .Set<DrawdownSnapshot>()
            .OrderByDescending(x => x.RecordedAt)
            .FirstOrDefaultAsync(ct);

        if (snapshot is null) return (false, string.Empty);

        JsonElement condition;
        try { condition = JsonDocument.Parse(alert.ConditionJson).RootElement; }
        catch { return (false, string.Empty); }

        if (!condition.TryGetProperty("ThresholdPct", out var thresholdEl)) return (false, string.Empty);

        decimal threshold = thresholdEl.GetDecimal();

        return snapshot.DrawdownPct >= threshold
            ? (true, $"Drawdown {snapshot.DrawdownPct:F2}% breached threshold {threshold:F2}%")
            : (false, string.Empty);
    }

    /// <summary>
    /// Evaluates a <see cref="AlertType.SignalGenerated"/> alert by checking for a new
    /// <see cref="TradeSignal"/> for the alert's symbol created after the last trigger time.
    /// Takes the earliest qualifying signal (oldest-first) so signals are not skipped if
    /// multiple arrive between polling cycles.
    /// </summary>
    private async Task<(bool Triggered, string Message)> EvaluateSignalGeneratedAsync(
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

        if (signal is null) return (false, string.Empty);

        return (true, $"New trade signal generated for {alert.Symbol} at {signal.GeneratedAt:u}");
    }

    /// <summary>
    /// Evaluates a <see cref="AlertType.OrderFilled"/> alert by checking for a new filled
    /// <see cref="Order"/> for the alert's symbol with a fill timestamp after the last trigger.
    /// </summary>
    private async Task<(bool Triggered, string Message)> EvaluateOrderFilledAsync(
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

        if (order is null) return (false, string.Empty);

        return (true, $"Order {order.Id} filled for {alert.Symbol} at {order.FilledAt:u}");
    }

    /// <summary>
    /// Evaluates a <see cref="AlertType.PositionClosed"/> alert by checking for a
    /// <see cref="Position"/> closed for the alert's symbol after the last trigger time.
    /// </summary>
    private async Task<(bool Triggered, string Message)> EvaluatePositionClosedAsync(
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

        if (position is null) return (false, string.Empty);

        return (true, $"Position {position.Id} closed for {alert.Symbol} at {position.ClosedAt:u}");
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
}
