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
/// Background service that evaluates all active alert conditions every 30 seconds
/// and dispatches notifications via <see cref="IAlertDispatcher"/> when conditions are met.
/// </summary>
/// <remarks>
/// Supported alert types:
/// <list type="bullet">
///   <item><term>PriceLevel</term><description>Fires when the live mid-price crosses a threshold. ConditionJson: <c>{"Price":1.0850,"Direction":"Above"}</c> or <c>"Below"</c>.</description></item>
///   <item><term>DrawdownBreached</term><description>Fires when the latest drawdown snapshot exceeds the configured threshold. ConditionJson: <c>{"ThresholdPct":5.0}</c>.</description></item>
///   <item><term>SignalGenerated</term><description>Fires once per new <see cref="TradeSignal"/> for the symbol created after <see cref="Alert.LastTriggeredAt"/>.</description></item>
///   <item><term>OrderFilled</term><description>Fires once per order filled for the symbol after <see cref="Alert.LastTriggeredAt"/>.</description></item>
///   <item><term>PositionClosed</term><description>Fires once per position closed for the symbol after <see cref="Alert.LastTriggeredAt"/>.</description></item>
/// </list>
/// A 60-minute cooldown prevents duplicate firings for sustained level-based breaches
/// (PriceLevel, DrawdownBreached). Event-based types (Signal, Order, Position) fire once
/// per event and update <see cref="Alert.LastTriggeredAt"/> to the event timestamp.
/// </remarks>
public class AlertWorker : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LevelCooldown   = TimeSpan.FromMinutes(60);

    private readonly ILogger<AlertWorker>  _logger;
    private readonly IServiceScopeFactory  _scopeFactory;
    private readonly ILivePriceCache       _priceCache;

    public AlertWorker(
        ILogger<AlertWorker>  logger,
        IServiceScopeFactory  scopeFactory,
        ILivePriceCache       priceCache)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _priceCache   = priceCache;
    }

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

    private async Task EvaluateAlertsAsync(CancellationToken ct)
    {
        using var scope      = _scopeFactory.CreateScope();
        var readContext      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeContext     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var dispatcher       = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();

        var alerts = await readContext.GetDbContext()
            .Set<Alert>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        if (alerts.Count == 0) return;

        bool anyUpdated = false;

        foreach (var alert in alerts)
        {
            try
            {
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

                // Fetch the tracked entity from the write context so EF tracks the update
                var tracked = await writeContext.GetDbContext()
                    .Set<Alert>()
                    .FirstOrDefaultAsync(x => x.Id == alert.Id, ct);

                if (tracked is null) continue;

                tracked.LastTriggeredAt = DateTime.UtcNow;
                anyUpdated = true;

                await dispatcher.DispatchAsync(alert, message, ct);

                _logger.LogInformation(
                    "AlertWorker: fired alert {AlertId} ({Type}/{Symbol}) — {Message}",
                    alert.Id, alert.AlertType, alert.Symbol, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AlertWorker: error evaluating alert {AlertId} ({Type}/{Symbol})",
                    alert.Id, alert.AlertType, alert.Symbol);
            }
        }

        if (anyUpdated)
            await writeContext.SaveChangesAsync(ct);
    }

    // ── Evaluators ────────────────────────────────────────────────────────────

    private (bool Triggered, string Message) EvaluatePriceLevel(Alert alert)
    {
        if (InCooldown(alert)) return (false, string.Empty);

        var price = _priceCache.Get(alert.Symbol);
        if (price is null) return (false, string.Empty);

        decimal mid = (price.Value.Bid + price.Value.Ask) / 2m;

        JsonElement condition;
        try { condition = JsonDocument.Parse(alert.ConditionJson).RootElement; }
        catch { return (false, string.Empty); }

        if (!condition.TryGetProperty("Price",     out var priceEl))     return (false, string.Empty);
        if (!condition.TryGetProperty("Direction", out var directionEl)) return (false, string.Empty);

        decimal threshold = priceEl.GetDecimal();
        string  direction = directionEl.GetString() ?? string.Empty;

        bool hit = direction.Equals("Above", StringComparison.OrdinalIgnoreCase)
            ? mid >= threshold
            : mid <= threshold;

        return hit
            ? (true, $"{alert.Symbol} mid price {mid:F5} is {direction.ToLower()} threshold {threshold:F5}")
            : (false, string.Empty);
    }

    private async Task<(bool Triggered, string Message)> EvaluateDrawdownAsync(
        Alert alert, IReadApplicationDbContext ctx, CancellationToken ct)
    {
        if (InCooldown(alert)) return (false, string.Empty);

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

    private async Task<(bool Triggered, string Message)> EvaluateSignalGeneratedAsync(
        Alert alert, IReadApplicationDbContext ctx, CancellationToken ct)
    {
        var since = alert.LastTriggeredAt ?? DateTime.MinValue;

        var signal = await ctx.GetDbContext()
            .Set<TradeSignal>()
            .Where(x => x.Symbol       == alert.Symbol
                     && x.GeneratedAt  > since
                     && !x.IsDeleted)
            .OrderBy(x => x.GeneratedAt)
            .FirstOrDefaultAsync(ct);

        if (signal is null) return (false, string.Empty);

        return (true, $"New trade signal generated for {alert.Symbol} at {signal.GeneratedAt:u}");
    }

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
            .OrderBy(x => x.FilledAt)
            .FirstOrDefaultAsync(ct);

        if (order is null) return (false, string.Empty);

        return (true, $"Order {order.Id} filled for {alert.Symbol} at {order.FilledAt:u}");
    }

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
            .OrderBy(x => x.ClosedAt)
            .FirstOrDefaultAsync(ct);

        if (position is null) return (false, string.Empty);

        return (true, $"Position {position.Id} closed for {alert.Symbol} at {position.ClosedAt:u}");
    }

    private static bool InCooldown(Alert alert) =>
        alert.LastTriggeredAt.HasValue
        && DateTime.UtcNow - alert.LastTriggeredAt.Value < LevelCooldown;
}
