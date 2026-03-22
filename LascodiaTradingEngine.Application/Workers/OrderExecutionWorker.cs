using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Orders.Commands.SubmitOrder;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

// Rate-limit key used to guard all outgoing broker order-submission calls.
// The budget (default 30 per minute) should be kept well below the broker's
// published API rate limit to leave headroom for other API calls (pricing, account).
// Adjust via EngineConfig key "RateLimit:BrokerOrdersPerMinute".

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that polls for Pending orders every 5 seconds, submits them to the
/// broker via <c>SubmitOrderCommand</c>, and — for any order that fills immediately —
/// automatically creates an <c>ExecutionQualityLog</c> and publishes an
/// <c>OrderFilledIntegrationEvent</c> so downstream consumers can react without polling.
/// </summary>
public class OrderExecutionWorker : BackgroundService
{
    /// <summary>Token-bucket key for broker order submission rate limiting.</summary>
    private const string RateLimitKey      = "broker:orders";
    /// <summary>Default order submissions per minute when no EngineConfig override is set.</summary>
    private const int    DefaultMaxPerMin  = 30;
    /// <summary>Number of consecutive failures before the circuit-breaker engages exponential backoff.</summary>
    private const int    MaxConsecutiveFailures = 5;

    private readonly ILogger<OrderExecutionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRateLimiter _rateLimiter;
    private readonly TradingMetrics _metrics;

    /// <summary>Rolling count of consecutive submit failures — drives the circuit-breaker.</summary>
    private int _consecutiveFailures;

    public OrderExecutionWorker(
        ILogger<OrderExecutionWorker> logger,
        IServiceScopeFactory scopeFactory,
        IRateLimiter rateLimiter,
        TradingMetrics metrics)
    {
        _logger      = logger;
        _scopeFactory = scopeFactory;
        _rateLimiter = rateLimiter;
        _metrics     = metrics;
    }

    /// <summary>
    /// Main polling loop that processes pending orders every 5 seconds. Incorporates a
    /// circuit-breaker pattern: after <see cref="MaxConsecutiveFailures"/> consecutive
    /// broker submission failures, the worker backs off exponentially (up to 5 minutes)
    /// to avoid hammering a degraded broker API.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OrderExecutionWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Circuit-breaker: exponential backoff after consecutive failures
            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                int backoffSeconds = Math.Min(60 * (int)Math.Pow(2, _consecutiveFailures - MaxConsecutiveFailures), 300);
                _logger.LogWarning(
                    "OrderExecutionWorker: circuit-breaker active — {Failures} consecutive failures, backing off {Seconds}s",
                    _consecutiveFailures, backoffSeconds);
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }

            await ProcessPendingOrdersAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Fetches up to 20 pending orders, rate-limits broker submissions, submits each order
    /// via <see cref="SubmitOrderCommand"/>, and for immediate fills, records execution quality
    /// metrics (slippage, latency) and publishes an <see cref="OrderFilledIntegrationEvent"/>.
    /// The batch size of 20 prevents any single cycle from monopolising the broker connection.
    /// </summary>
    private async Task ProcessPendingOrdersAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope      = _scopeFactory.CreateScope();
            var readContext      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var writeContext     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var mediator         = scope.ServiceProvider.GetRequiredService<IMediator>();
            var eventService     = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();

            // Read configurable rate-limit budget (falls back to 30/min if not set)
            int maxPerMin = DefaultMaxPerMin;
            var cfgEntry  = await readContext.GetDbContext()
                .Set<EngineConfig>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == "RateLimit:BrokerOrdersPerMinute", cancellationToken);
            if (cfgEntry?.Value is not null && int.TryParse(cfgEntry.Value, out var parsed))
                maxPerMin = parsed;

            var pendingIds = await readContext.GetDbContext()
                .Set<Order>()
                .Where(x => x.Status == OrderStatus.Pending && !x.IsDeleted)
                .Select(x => x.Id)
                .Take(20)
                .ToListAsync(cancellationToken);

            foreach (var id in pendingIds)
            {
                try
                {
                    // ── Rate-limit guard ────────────────────────────────────────
                    bool allowed = await _rateLimiter.TryAcquireAsync(
                        RateLimitKey, maxPerMin, TimeSpan.FromMinutes(1), cancellationToken);

                    if (!allowed)
                    {
                        _logger.LogWarning(
                            "OrderExecutionWorker: broker rate limit reached ({Max}/min) — " +
                            "deferring remaining {Count} order(s) to next cycle.",
                            maxPerMin, pendingIds.Count - pendingIds.IndexOf(id));
                        break;
                    }

                    var submitAt = DateTime.UtcNow;
                    _metrics.OrdersSubmitted.Add(1);
                    var response = await mediator.Send(new SubmitOrderCommand { Id = id }, cancellationToken);

                    if (response is { status: true, data: { Status: OrderStatus.Filled, FilledPrice: not null } result })
                    {
                        _metrics.OrdersFilled.Add(1);
                        var fillLatency = (DateTime.UtcNow - submitAt).TotalMilliseconds;
                        _metrics.OrderFillLatencyMs.Record(fillLatency);

                        await RecordExecutionQualityAsync(writeContext, result, submitAt, cancellationToken);
                        await PublishOrderFilledEventAsync(writeContext, eventService, result, submitAt);
                    }

                    _consecutiveFailures = 0; // Reset on success
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _metrics.OrdersFailed.Add(1);
                    _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", "OrderExecutionWorker"));
                    _logger.LogError(ex,
                        "Error submitting order {OrderId} (consecutive failures: {Failures}/{Max})",
                        id, _consecutiveFailures, MaxConsecutiveFailures);

                    if (_consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        _logger.LogError(
                            "OrderExecutionWorker: circuit-breaker tripped after {Failures} consecutive failures — " +
                            "remaining orders deferred to next cycle with backoff",
                            _consecutiveFailures);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OrderExecutionWorker.ProcessPendingOrdersAsync");
        }
    }

    /// <summary>
    /// Creates an <see cref="ExecutionQualityLog"/> record for the filled order, capturing
    /// slippage (in pips) and fill latency.
    /// </summary>
    private async Task RecordExecutionQualityAsync(
        IWriteApplicationDbContext writeContext,
        SubmitOrderResult result,
        DateTime submitAt,
        CancellationToken ct)
    {
        var fillMs     = (long)(DateTime.UtcNow - submitAt).TotalMilliseconds;
        var fillRate   = result.FilledQuantity.HasValue && result.Quantity > 0
            ? result.FilledQuantity.Value / result.Quantity
            : 1m;

        // Standard 5-decimal FX pip factor (10,000); direction sign: Buy = +1, Sell = −1
        const decimal pipFactor   = 10_000m;
        decimal directionSign     = result.OrderType == OrderType.Buy ? 1m : -1m;
        decimal slippagePips      = (result.FilledPrice!.Value - result.RequestedPrice) * pipFactor * directionSign;

        var qualityLog = new ExecutionQualityLog
        {
            OrderId        = result.OrderId,
            StrategyId     = result.StrategyId > 0 ? result.StrategyId : null,
            Symbol         = result.Symbol,
            Session        = result.Session,
            RequestedPrice = result.RequestedPrice,
            FilledPrice    = result.FilledPrice.Value,
            SlippagePips   = slippagePips,
            SubmitToFillMs = fillMs,
            WasPartialFill = fillRate < 1m,
            FillRate       = fillRate,
            RecordedAt     = DateTime.UtcNow
        };

        await writeContext.GetDbContext().Set<ExecutionQualityLog>().AddAsync(qualityLog, ct);
        await writeContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "OrderExecutionWorker: execution quality recorded for order {OrderId} — Slippage={Slippage:F1} pips, FillMs={FillMs}ms",
            result.OrderId, slippagePips, fillMs);
    }

    /// <summary>
    /// Publishes an <see cref="OrderFilledIntegrationEvent"/> via <see cref="IIntegrationEventService"/>
    /// so that downstream consumers (e.g., <see cref="OrderFilledEventHandler"/>) can react to
    /// fills without polling the database.
    /// </summary>
    private async Task PublishOrderFilledEventAsync(
        IWriteApplicationDbContext writeContext,
        IIntegrationEventService eventService,
        SubmitOrderResult result,
        DateTime submitAt)
    {
        var fillMs   = (long)(DateTime.UtcNow - submitAt).TotalMilliseconds;
        var fillRate = result.FilledQuantity.HasValue && result.Quantity > 0
            ? result.FilledQuantity.Value / result.Quantity
            : 1m;

        await eventService.SaveAndPublish(writeContext, new OrderFilledIntegrationEvent
        {
            OrderId        = result.OrderId,
            StrategyId     = result.StrategyId > 0 ? result.StrategyId : null,
            Symbol         = result.Symbol,
            Session        = result.Session,
            RequestedPrice = result.RequestedPrice,
            FilledPrice    = result.FilledPrice!.Value,
            SubmitToFillMs = fillMs,
            WasPartialFill = fillRate < 1m,
            FillRate       = fillRate,
            FilledAt       = result.FilledAt ?? DateTime.UtcNow
        });
    }
}
