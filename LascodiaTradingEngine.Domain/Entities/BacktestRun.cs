using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records a single historical backtest simulation for a <see cref="Strategy"/>.
/// A backtest replays the strategy's signal-generation logic against a fixed window of
/// historical candle data to estimate how it would have performed without risking real capital.
/// </summary>
/// <remarks>
/// Backtest runs are queued via the API and processed asynchronously by the backtesting engine.
/// On completion, the aggregated result metrics (win rate, profit factor, max drawdown, etc.)
/// are serialised to <see cref="ResultJson"/> for display in the UI.
/// The <c>OptimizationWorker</c> internally creates ephemeral backtest runs for each
/// parameter combination in its grid search without persisting them as separate entities.
/// </remarks>
public class BacktestRun : Entity<long>
{
    /// <summary>Foreign key to the <see cref="Strategy"/> being backtested.</summary>
    public long     StrategyId     { get; set; }

    /// <summary>The currency pair used in this backtest (e.g. "EURUSD").</summary>
    public string   Symbol         { get; set; } = string.Empty;

    /// <summary>The chart timeframe on which candle data is replayed.</summary>
    public Timeframe   Timeframe      { get; set; } = Timeframe.H1;

    /// <summary>UTC start of the historical data window to replay.</summary>
    public DateTime FromDate       { get; set; }

    /// <summary>UTC end of the historical data window to replay.</summary>
    public DateTime ToDate         { get; set; }

    /// <summary>
    /// Starting account equity in account currency (e.g. 10 000 USD).
    /// Position sizing calculations during the replay use this as the initial balance.
    /// </summary>
    public decimal  InitialBalance { get; set; }

    /// <summary>Current processing state: Queued → Running → Completed / Failed.</summary>
    public RunStatus   Status         { get; set; } = RunStatus.Queued;

    /// <summary>
    /// Serialised backtest result metrics produced on completion (win rate, profit factor,
    /// max drawdown, total trades, net P&amp;L, Sharpe ratio, etc.).
    /// Null while the run has not yet completed.
    /// </summary>
    public string?  ResultJson     { get; set; }

    /// <summary>
    /// Error details if the run failed. Null on successful completion.
    /// </summary>
    public string?  ErrorMessage   { get; set; }

    /// <summary>UTC timestamp when this backtest was queued / the record was created.</summary>
    public DateTime StartedAt      { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when processing finished (succeeded or failed). Null while running.</summary>
    public DateTime? CompletedAt   { get; set; }

    /// <summary>
    /// Processing priority (higher = processed first). Auto-generated candidates set this
    /// based on screening quality (e.g. IS Sharpe rank) so the BacktestWorker processes
    /// the most promising candidates first. Default 0 for manually queued runs.
    /// </summary>
    public int      Priority       { get; set; }

    /// <summary>
    /// Optional foreign key to the optimisation run that queued this validation backtest.
    /// Enables idempotent follow-up scheduling after auto-approval.
    /// </summary>
    public long?    SourceOptimizationRunId { get; set; }

    /// <summary>
    /// Optional parameter snapshot pinned to this run at queue time.
    /// Optimization follow-up backtests use this instead of whatever parameters the
    /// strategy may hold later after rollouts, rollbacks, or manual edits.
    /// </summary>
    public string?  ParametersSnapshotJson { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool     IsDeleted      { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The strategy this backtest was run against.</summary>
    public virtual Strategy Strategy { get; set; } = null!;
}
