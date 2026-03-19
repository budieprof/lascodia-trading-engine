using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records a single parameter-optimisation job for a <see cref="Strategy"/>.
/// The <c>OptimizationWorker</c> performs a grid search over the strategy's parameter space,
/// backtests each candidate combination, and saves the best-performing parameter set.
/// </summary>
/// <remarks>
/// Optimisation runs are triggered either on a schedule (<see cref="TriggerType.Scheduled"/>)
/// or automatically by the <c>StrategyHealthWorker</c> when a strategy's health score falls
/// below the critical threshold. After a run completes, the best parameters are held in
/// <see cref="BestParametersJson"/> pending human review (<see cref="ApprovedAt"/>).
/// </remarks>
public class OptimizationRun : Entity<long>
{
    /// <summary>Foreign key to the <see cref="Strategy"/> being optimised.</summary>
    public long    StrategyId              { get; set; }

    /// <summary>
    /// Whether this run was started automatically (health degradation, schedule) or
    /// initiated manually via the API.
    /// </summary>
    public TriggerType  TriggerType            { get; set; } = TriggerType.Scheduled;

    /// <summary>Current processing state: Queued → Running → Completed / Failed.</summary>
    public OptimizationRunStatus  Status                 { get; set; } = OptimizationRunStatus.Queued;

    /// <summary>
    /// Total number of parameter combinations evaluated during the grid search.
    /// Populated after the run completes.
    /// </summary>
    public int     Iterations             { get; set; }

    /// <summary>
    /// JSON object containing the parameter values that produced the highest health score
    /// across all tested combinations (e.g. <c>{"FastPeriod":9,"SlowPeriod":21}</c>).
    /// Null until the run completes. Copied to <see cref="Strategy.ParametersJson"/> on approval.
    /// </summary>
    public string? BestParametersJson     { get; set; }

    /// <summary>
    /// Composite health score (0.0–1.0) achieved by the best parameter set.
    /// Combines win rate (40%), profit factor (30%), and inverse max-drawdown (30%).
    /// Null until the run completes.
    /// </summary>
    public decimal? BestHealthScore       { get; set; }

    /// <summary>
    /// Snapshot of the strategy's parameter JSON at the time the run was queued.
    /// Used to compare baseline performance against the optimised result.
    /// </summary>
    public string? BaselineParametersJson { get; set; }

    /// <summary>
    /// Health score of the strategy using its original (baseline) parameters.
    /// A higher <see cref="BestHealthScore"/> relative to this value confirms improvement.
    /// </summary>
    public decimal? BaselineHealthScore   { get; set; }

    /// <summary>
    /// Error details if the run failed. Null on successful completion.
    /// </summary>
    public string? ErrorMessage           { get; set; }

    /// <summary>UTC timestamp when this run was queued / the record was created.</summary>
    public DateTime StartedAt             { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when grid search finished (succeeded or failed). Null while running.</summary>
    public DateTime? CompletedAt          { get; set; }

    /// <summary>
    /// UTC timestamp when a human operator approved the optimised parameters and
    /// applied them to the strategy. Null until explicitly approved.
    /// </summary>
    public DateTime? ApprovedAt           { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted              { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The strategy this optimisation run was executed for.</summary>
    public virtual Strategy Strategy { get; set; } = null!;
}
