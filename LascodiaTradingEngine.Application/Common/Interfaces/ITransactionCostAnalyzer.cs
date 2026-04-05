using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Decomposes execution cost using implementation-shortfall methodology after each fill.
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
}
