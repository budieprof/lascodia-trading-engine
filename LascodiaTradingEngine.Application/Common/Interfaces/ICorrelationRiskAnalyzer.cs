using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Computes rolling Pearson correlation matrix from recent returns and evaluates
/// portfolio concentration risk using the Herfindahl index on correlated exposure.
/// </summary>
public record CorrelationRiskResult(
    decimal HerfindahlIndex,
    decimal MaxPairwiseCorrelation,
    string MostCorrelatedPair,
    bool ConcentrationBreached);

public interface ICorrelationRiskAnalyzer
{
    /// <summary>
    /// Evaluates correlation-based concentration risk of adding the proposed signal
    /// to the existing portfolio of open positions.
    /// </summary>
    Task<CorrelationRiskResult> EvaluateAsync(
        TradeSignal proposedSignal,
        IReadOnlyList<Position> openPositions,
        int correlationWindowDays,
        decimal maxConcentrationThreshold,
        CancellationToken cancellationToken);

    /// <summary>Returns the pairwise Pearson correlation matrix for the given symbols over the specified window.</summary>
    Task<IReadOnlyDictionary<string, decimal>> GetCorrelationMatrixAsync(
        IReadOnlyList<string> symbols,
        int windowDays,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes Marginal Contribution to Total Risk (MCTR) for each position.
    /// MCTR_i = beta_i * portfolio_sigma, where beta_i = Cov(position_i, portfolio) / Var(portfolio).
    /// </summary>
    /// <param name="positions">Open positions to analyse.</param>
    /// <param name="correlationMatrix">Pre-computed pairwise correlation matrix (from <see cref="GetCorrelationMatrixAsync"/>).</param>
    /// <param name="windowDays">Number of historical days used for return estimation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping positionId to its MCTR value.</returns>
    Task<Dictionary<long, decimal>> ComputeMCTRAsync(
        IReadOnlyList<Position> positions,
        IReadOnlyDictionary<string, decimal> correlationMatrix,
        int windowDays,
        CancellationToken cancellationToken);
}
