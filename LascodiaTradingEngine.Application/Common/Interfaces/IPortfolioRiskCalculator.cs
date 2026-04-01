using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Computes portfolio-level statistical risk measures: VaR, CVaR, stressed VaR,
/// and marginal VaR for proposed new positions.
/// </summary>
public record PortfolioRiskMetrics(
    decimal VaR95,
    decimal VaR99,
    decimal CVaR95,
    decimal CVaR99,
    decimal StressedVaR,
    decimal CorrelationConcentration,
    decimal MonteCarloVaR95 = 0,
    decimal MonteCarloVaR99 = 0,
    decimal MonteCarloCVaR95 = 0)
{
    /// <summary>Empty metrics instance returned when computation is not possible.</summary>
    public static PortfolioRiskMetrics Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public record MarginalVaRResult(
    decimal MarginalVaR95,
    decimal PostTradeVaR95,
    bool WouldBreachLimit);

public interface IPortfolioRiskCalculator
{
    Task<PortfolioRiskMetrics> ComputeAsync(
        TradingAccount account,
        IReadOnlyList<Position> openPositions,
        CancellationToken cancellationToken);

    Task<MarginalVaRResult> ComputeMarginalAsync(
        TradeSignal proposedSignal,
        TradingAccount account,
        IReadOnlyList<Position> openPositions,
        CancellationToken cancellationToken);
}
