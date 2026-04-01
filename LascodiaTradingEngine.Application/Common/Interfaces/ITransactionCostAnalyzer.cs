using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Decomposes execution cost using implementation-shortfall methodology after each fill.
/// </summary>
public interface ITransactionCostAnalyzer
{
    Task<TransactionCostAnalysis> AnalyzeAsync(
        Order filledOrder,
        TradeSignal? originatingSignal,
        CancellationToken cancellationToken);
}
