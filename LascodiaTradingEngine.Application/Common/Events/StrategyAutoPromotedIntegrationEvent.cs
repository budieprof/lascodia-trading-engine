using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published when a strategy candidate is auto-promoted for accelerated validation because it meets
/// elite screening criteria (2x thresholds, 3/3 walk-forward, p&lt;0.01, R²&gt;0.90).
/// </summary>
public record StrategyAutoPromotedIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>The fast-tracked strategy's database Id.</summary>
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

    /// <summary>The regime being observed when the strategy was generated.</summary>
    public MarketRegimeEnum ObservedRegime { get; init; }

    /// <summary>Whether the strategy came from primary or reserve generation.</summary>
    public string GenerationSource { get; init; } = string.Empty;

    /// <summary>Optional reserve target regime when the strategy came from reserve generation.</summary>
    public MarketRegimeEnum? ReserveTargetRegime { get; init; }

    /// <summary>In-sample Sharpe ratio at promotion time.</summary>
    public double IsSharpeRatio { get; init; }

    /// <summary>Out-of-sample Sharpe ratio at promotion time.</summary>
    public double OosSharpeRatio { get; init; }

    /// <summary>Equity curve R² (linearity measure).</summary>
    public double EquityCurveR2 { get; init; }

    /// <summary>Monte Carlo sign-flip p-value.</summary>
    public double MonteCarloPValue { get; init; }

    /// <summary>Monte Carlo shuffle p-value.</summary>
    public double ShufflePValue { get; init; }

    /// <summary>Number of walk-forward windows that passed.</summary>
    public int WalkForwardWindowsPassed { get; init; }

    /// <summary>Whether live-performance haircuts were applied.</summary>
    public bool LiveHaircutApplied { get; init; }

    /// <summary>Win-rate haircut multiplier used during promotion screening.</summary>
    public double WinRateHaircutApplied { get; init; }

    /// <summary>Profit-factor haircut multiplier used during promotion screening.</summary>
    public double ProfitFactorHaircutApplied { get; init; }

    /// <summary>Sharpe haircut multiplier used during promotion screening.</summary>
    public double SharpeHaircutApplied { get; init; }

    /// <summary>Drawdown inflation multiplier used during promotion screening.</summary>
    public double DrawdownInflationApplied { get; init; }

    /// <summary>UTC timestamp when the accelerated-validation decision was made.</summary>
    public DateTime PromotedAt { get; init; } = DateTime.UtcNow;
}
