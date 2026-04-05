using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published when a strategy candidate is auto-promoted to ShadowLive because it meets
/// elite screening criteria (2x thresholds, 3/3 walk-forward, p&lt;0.01, R²&gt;0.90).
/// </summary>
public record StrategyAutoPromotedIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>The promoted strategy's database Id.</summary>
    public long StrategyId { get; init; }

    /// <summary>Human-readable strategy name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Currency pair the strategy targets.</summary>
    public string Symbol { get; init; } = "";

    /// <summary>Timeframe the strategy operates on.</summary>
    public Timeframe Timeframe { get; init; }

    /// <summary>The strategy type.</summary>
    public StrategyType StrategyType { get; init; }

    /// <summary>The market regime the strategy was generated for.</summary>
    public MarketRegimeEnum Regime { get; init; }

    /// <summary>In-sample Sharpe ratio at promotion time.</summary>
    public double IsSharpeRatio { get; init; }

    /// <summary>Out-of-sample Sharpe ratio at promotion time.</summary>
    public double OosSharpeRatio { get; init; }

    /// <summary>Equity curve R² (linearity measure).</summary>
    public double EquityCurveR2 { get; init; }

    /// <summary>Monte Carlo sign-flip p-value.</summary>
    public double MonteCarloPValue { get; init; }

    /// <summary>Number of walk-forward windows that passed.</summary>
    public int WalkForwardWindowsPassed { get; init; }

    /// <summary>UTC timestamp when the strategy was auto-promoted.</summary>
    public DateTime PromotedAt { get; init; } = DateTime.UtcNow;
}
