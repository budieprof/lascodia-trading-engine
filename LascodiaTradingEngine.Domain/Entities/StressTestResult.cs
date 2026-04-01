using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Result of applying a <see cref="StressTestScenario"/> to the current portfolio.
/// Computed by the StressTestWorker and stored for historical trend analysis and
/// regulatory reporting (Basel III / FRTB compliance).
/// </summary>
public class StressTestResult : Entity<long>
{
    /// <summary>FK to the scenario that was run.</summary>
    public long StressTestScenarioId { get; set; }

    /// <summary>FK to the trading account stress-tested.</summary>
    public long TradingAccountId { get; set; }

    /// <summary>Portfolio equity at the time of the test.</summary>
    public decimal PortfolioEquity { get; set; }

    /// <summary>Estimated portfolio P&amp;L under the stress scenario (negative = loss).</summary>
    public decimal StressedPnl { get; set; }

    /// <summary>Stressed P&amp;L as percentage of portfolio equity.</summary>
    public decimal StressedPnlPct { get; set; }

    /// <summary>Whether the stressed P&amp;L would trigger a margin call.</summary>
    public bool WouldTriggerMarginCall { get; set; }

    /// <summary>
    /// JSON array of per-position impacts: [{ "positionId": 1, "symbol": "EURUSD", "pnlImpact": -1234.56 }]
    /// </summary>
    public string PositionImpactsJson { get; set; } = "[]";

    /// <summary>
    /// For ReverseStress scenarios: the minimum shock percentage that causes the target loss.
    /// Null for Historical/Hypothetical scenarios.
    /// </summary>
    public decimal? MinimumShockPct { get; set; }

    /// <summary>VaR (95%) of the portfolio at test time for comparison.</summary>
    public decimal? PortfolioVaR95 { get; set; }

    /// <summary>CVaR (95%) — expected shortfall beyond VaR.</summary>
    public decimal? PortfolioCVaR95 { get; set; }

    /// <summary>When the stress test was executed.</summary>
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;

    public virtual StressTestScenario Scenario { get; set; } = null!;
    public virtual TradingAccount TradingAccount { get; set; } = null!;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
