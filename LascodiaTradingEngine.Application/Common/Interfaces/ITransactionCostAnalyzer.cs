using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Pre-trade cost estimation result containing estimated spread, slippage, market impact,
/// and commission costs for a proposed order.
/// </summary>
public record TransactionCostEstimate(
    string Symbol,
    decimal Quantity,
    decimal EstimatedSpreadCost,
    decimal EstimatedSlippagePips,
    decimal EstimatedMarketImpact,
    decimal EstimatedCommission,
    decimal TotalEstimatedCost,
    decimal TotalEstimatedBps,
    DateTime EstimatedAt);

/// <summary>
/// Decomposes execution cost using implementation-shortfall methodology after each fill.
/// Also provides pre-trade cost estimation for order sizing decisions.
/// </summary>
public interface ITransactionCostAnalyzer
{
    /// <summary>
    /// Decomposes the execution cost of a filled order into spread, slippage, market impact,
    /// and timing components using implementation-shortfall methodology.
    /// </summary>
    Task<TransactionCostAnalysis> AnalyzeAsync(
        Order filledOrder,
        TradeSignal? originatingSignal,
        CancellationToken cancellationToken);

    /// <summary>
    /// Estimates execution cost BEFORE placing the order, using historical spread,
    /// slippage, and estimated market impact based on order size.
    /// </summary>
    Task<TransactionCostEstimate> EstimatePreTradeAsync(
        string symbol,
        decimal quantity,
        decimal entryPrice,
        CancellationToken ct);
}
