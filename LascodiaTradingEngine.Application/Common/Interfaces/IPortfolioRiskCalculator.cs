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
    decimal MonteCarloCVaR95 = 0,
    decimal EvtVaR95 = 0,
    decimal EvtVaR99 = 0,
    decimal EvtCVaR99 = 0,
    decimal GpdShape = 0,
    decimal GpdScale = 0)
{
    /// <summary>Empty metrics instance returned when computation is not possible.</summary>
    public static PortfolioRiskMetrics Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Result of marginal VaR computation for a proposed new position.
/// </summary>
public record MarginalVaRResult(
    decimal MarginalVaR95,
    decimal PostTradeVaR95,
    bool WouldBreachLimit);

public interface IPortfolioRiskCalculator
{
    /// <summary>Computes VaR, CVaR, and Monte Carlo VaR for the account's current open positions.</summary>
    Task<PortfolioRiskMetrics> ComputeAsync(
        TradingAccount account,
        IReadOnlyList<Position> openPositions,
        CancellationToken cancellationToken);

    /// <summary>Computes the marginal VaR impact of adding the proposed signal to the existing portfolio.</summary>
    Task<MarginalVaRResult> ComputeMarginalAsync(
        TradeSignal proposedSignal,
        TradingAccount account,
        IReadOnlyList<Position> openPositions,
        CancellationToken cancellationToken);
}
