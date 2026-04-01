using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Decomposes execution cost using implementation-shortfall methodology.
/// Breaks down total cost into delay, market impact, spread, and commission components.
/// </summary>
[RegisterService]
public class TransactionCostAnalyzer : ITransactionCostAnalyzer
{
    private readonly ILogger<TransactionCostAnalyzer> _logger;

    public TransactionCostAnalyzer(ILogger<TransactionCostAnalyzer> logger)
    {
        _logger = logger;
    }

    public Task<TransactionCostAnalysis> AnalyzeAsync(
        Order filledOrder,
        TradeSignal? originatingSignal,
        CancellationToken cancellationToken)
    {
        if (filledOrder.FilledPrice is null)
            throw new InvalidOperationException($"Order {filledOrder.Id} has no fill price");

        var fillPrice = filledOrder.FilledPrice.Value;

        // Arrival price = mid-price at signal creation (or order price as fallback)
        decimal arrivalPrice = originatingSignal?.EntryPrice ?? filledOrder.Price;

        // Submission price = order price (mid at time of submission to broker)
        decimal submissionPrice = filledOrder.Price;

        // Direction multiplier: Buy = +1 (higher fill = cost), Sell = -1 (lower fill = cost)
        decimal directionSign = filledOrder.OrderType == Domain.Enums.OrderType.Buy ? 1m : -1m;

        // Implementation shortfall: total execution cost vs decision price
        decimal implementationShortfall = directionSign * (fillPrice - arrivalPrice);

        // Delay cost: price drift from signal creation to order submission
        decimal delayCost = directionSign * (submissionPrice - arrivalPrice);

        // Market impact cost: price drift from submission to fill
        decimal marketImpactCost = directionSign * (fillPrice - submissionPrice);

        // Spread cost: half the spread at execution time (estimated from bid-ask if available)
        // Approximate as |fill - order price| when actual spread unavailable
        decimal spreadCost = Math.Abs(fillPrice - submissionPrice) / 2m;

        // Commission cost (from execution quality log if available)
        decimal commissionCost = 0m; // Populated by caller or from broker execution report

        // Total cost = delay + market impact + commission.
        // Spread is a component of market impact, not additive to implementation shortfall.
        decimal totalCost = delayCost + marketImpactCost + commissionCost;

        // Total cost in basis points
        decimal notional   = filledOrder.Quantity * fillPrice;
        decimal totalBps   = notional > 0 ? totalCost / notional * 10_000m : 0;

        // Timing metrics
        long signalToFillMs = 0;
        long submitToFillMs = 0;

        if (originatingSignal is not null && filledOrder.FilledAt.HasValue)
        {
            signalToFillMs = (long)(filledOrder.FilledAt.Value - originatingSignal.GeneratedAt).TotalMilliseconds;
        }

        if (filledOrder.FilledAt.HasValue)
        {
            submitToFillMs = (long)(filledOrder.FilledAt.Value - filledOrder.CreatedAt).TotalMilliseconds;
        }

        var tca = new TransactionCostAnalysis
        {
            OrderId                  = filledOrder.Id,
            TradeSignalId            = originatingSignal?.Id,
            Symbol                   = filledOrder.Symbol,
            ArrivalPrice             = arrivalPrice,
            FillPrice                = fillPrice,
            SubmissionPrice          = submissionPrice,
            ImplementationShortfall  = implementationShortfall,
            DelayCost                = delayCost,
            MarketImpactCost         = marketImpactCost,
            SpreadCost               = spreadCost,
            CommissionCost           = commissionCost,
            TotalCost                = totalCost,
            TotalCostBps             = totalBps,
            Quantity                 = filledOrder.Quantity,
            SignalToFillMs           = signalToFillMs,
            SubmissionToFillMs       = submitToFillMs,
            AnalyzedAt               = DateTime.UtcNow
        };

        _logger.LogDebug(
            "TCA: order {OrderId} {Symbol} — shortfall={IS:F5}, delay={Delay:F5}, impact={Impact:F5}, spread={Spread:F5}, total={Total:F2}bps",
            filledOrder.Id, filledOrder.Symbol,
            implementationShortfall, delayCost, marketImpactCost, spreadCost, totalBps);

        return Task.FromResult(tca);
    }
}
