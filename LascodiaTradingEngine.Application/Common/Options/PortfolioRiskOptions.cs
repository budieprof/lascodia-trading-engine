using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the portfolio VaR/CVaR risk calculator and risk worker.</summary>
public class PortfolioRiskOptions : ConfigurationOption<PortfolioRiskOptions>
{
    /// <summary>Rolling window in days for historical returns used in VaR computation.</summary>
    public int ReturnWindowDays { get; set; } = 60;

    /// <summary>VaR confidence level (e.g. 0.95 for 95% VaR).</summary>
    public decimal VaRConfidence95 { get; set; } = 0.95m;

    /// <summary>VaR confidence level for the tighter limit.</summary>
    public decimal VaRConfidence99 { get; set; } = 0.99m;

    /// <summary>Maximum portfolio VaR (95%) as percentage of equity. New positions are gated when breached.</summary>
    public decimal MaxVaR95Pct { get; set; } = 5.0m;

    /// <summary>Polling interval in seconds for the PortfolioRiskWorker.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Number of Monte Carlo simulations for VaR computation (0 = use historical only).</summary>
    public int MonteCarloSimulations { get; set; } = 10_000;

    /// <summary>
    /// RNG seed for Monte Carlo simulations. Null uses a non-deterministic seed (production default).
    /// Set to a fixed value (e.g. 42) for reproducibility audits.
    /// </summary>
    public int? MonteCarloSeed { get; set; }
}
