using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores optimised parameter sets per market regime for a given <see cref="Strategy"/>.
/// When the <c>OptimizationWorker</c> approves a parameter set, it also records which regime
/// was active during optimisation. This allows the engine to maintain a library of
/// regime-specific parameters and swap them instantly on regime change instead of
/// re-running the full optimizer.
/// </summary>
public class StrategyRegimeParams : Entity<long>
{
    /// <summary>Foreign key to the <see cref="Strategy"/>.</summary>
    public long StrategyId { get; set; }

    /// <summary>The market regime these parameters are optimised for.</summary>
    public MarketRegime Regime { get; set; }

    /// <summary>JSON object with the optimised parameter values for this regime.</summary>
    public string ParametersJson { get; set; } = "{}";

    /// <summary>OOS health score achieved during the optimisation that produced these params.</summary>
    public decimal HealthScore { get; set; }

    /// <summary>
    /// Lower bound of the 95% bootstrap CI on the OOS health score.
    /// Provides a conservative estimate of the true performance.
    /// </summary>
    public decimal? HealthScoreCILower { get; set; }

    /// <summary>ID of the <see cref="OptimizationRun"/> that produced these parameters.</summary>
    public long? OptimizationRunId { get; set; }

    /// <summary>UTC timestamp when these parameters were last updated.</summary>
    public DateTime OptimizedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag.</summary>
    public bool IsDeleted { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    public virtual Strategy Strategy { get; set; } = null!;
    public virtual OptimizationRun? OptimizationRun { get; set; }
}
