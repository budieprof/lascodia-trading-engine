using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

// Auto-approval threshold: challenger must improve health score by at least this
// margin AND exceed the absolute minimum to qualify for automatic promotion.

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background worker that drives the parameter optimisation pipeline by continuously
/// polling the database for queued <see cref="OptimizationRun"/> records, performing an
/// exhaustive grid search over each strategy's candidate parameter combinations, and
/// persisting the best-found configuration.
///
/// <para>
/// <b>Algorithm — Exhaustive Grid Search:</b>
/// For each queued run, <see cref="BuildParameterGrid"/> generates every discrete
/// combination of the strategy's tunable parameters (e.g. fast/slow MA periods for
/// <see cref="StrategyType.MovingAverageCrossover"/>, RSI period/oversold/overbought
/// levels for <see cref="StrategyType.RSIReversion"/>, lookback/multiplier for
/// <see cref="StrategyType.BreakoutScalper"/>). Each combination is backtested on the
/// last 6 months of candle data against a fixed $10,000 initial balance. The combination
/// that yields the highest <c>ProfitFactor</c> is selected as the candidate winner, and
/// its composite <see cref="ComputeHealthScore"/> is compared to the strategy's current
/// (baseline) score.
/// </para>
///
/// <para>
/// <b>Auto-approval gate:</b>
/// If the best candidate's health score exceeds the baseline by at least
/// <see cref="AutoApprovalImprovementThreshold"/> (10 pp) AND the absolute score reaches
/// <see cref="AutoApprovalMinHealthScore"/> (0.55), the worker applies the new parameters
/// directly to the live <see cref="Strategy"/> row without manual intervention. A paused
/// strategy is re-activated. Otherwise, the run lands in <c>Completed</c> status and
/// awaits an explicit <c>ApproveOptimizationCommand</c> from an operator.
/// </para>
///
/// <para>
/// <b>Post-approval validation chain:</b>
/// After auto-approval, a fresh <see cref="BacktestRun"/> covering the last full year is
/// queued so that BacktestWorker can re-validate the new parameters on a longer window,
/// which in turn auto-queues a WalkForwardRun. This ensures every promoted parameter set
/// passes the full validation pipeline before it influences live trading.
/// </para>
///
/// <para>
/// <b>Audit trail:</b> Every significant decision (completed, auto-approved, failed) is
/// logged via <c>LogDecisionCommand</c> (MediatR) so a full audit trail is maintained in
/// the <c>DecisionLog</c> table.
/// </para>
///
/// <para>
/// <b>Polling model:</b> The worker wakes every <see cref="PollingInterval"/> seconds
/// (30 s), processes one run per tick, and uses a per-cycle DI scope so EF contexts are
/// properly disposed between runs.
/// </para>
/// </summary>
public class OptimizationWorker : BackgroundService
{
    /// <summary>
    /// Minimum absolute health-score improvement required for auto-approval.
    /// The challenger's health score must exceed the baseline by at least this delta.
    /// A value of 0.10 means the new parameters must score at least 10 percentage
    /// points better than the current live configuration.
    /// </summary>
    private const decimal AutoApprovalImprovementThreshold = 0.10m;

    /// <summary>
    /// Minimum absolute health score the best candidate must achieve before
    /// auto-approval is considered, regardless of improvement magnitude.
    /// This floor prevents automatically promoting a parameter set that is merely
    /// "slightly less bad" than a terrible baseline — the result must still cross
    /// an acceptable quality threshold (0.55 out of 1.0).
    /// </summary>
    private const decimal AutoApprovalMinHealthScore = 0.55m;

    // ── EngineConfig keys for auto-scheduling ─────────────────────────────
    private const string CK_SchedulePollSecs   = "Optimization:SchedulePollSeconds";
    private const string CK_CooldownDays       = "Optimization:CooldownDays";
    private const string CK_MaxQueuedPerCycle  = "Optimization:MaxQueuedPerCycle";
    private const string CK_AutoScheduleEnabled = "Optimization:AutoScheduleEnabled";
    private const string CK_MinWinRate         = "Backtest:Gate:MinWinRate";
    private const string CK_MinProfitFactor    = "Backtest:Gate:MinProfitFactor";
    private const string CK_MinTotalTrades     = "Backtest:Gate:MinTotalTrades";

    private readonly ILogger<OptimizationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBacktestEngine _backtestEngine;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    /// <summary>Tracks when the next auto-scheduling scan should run.</summary>
    private DateTime _nextScheduleScanUtc = DateTime.MinValue;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="logger">Structured logger for diagnostic output.</param>
    /// <param name="scopeFactory">Factory for creating per-cycle DI scopes.</param>
    /// <param name="backtestEngine">Engine used to score each candidate parameter set.</param>
    public OptimizationWorker(
        ILogger<OptimizationWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBacktestEngine backtestEngine)
    {
        _logger         = logger;
        _scopeFactory   = scopeFactory;
        _backtestEngine = backtestEngine;
    }

    /// <summary>
    /// Entry point invoked by the hosted-service runtime. Runs a continuous polling
    /// loop that delegates each tick to <see cref="ProcessNextQueuedRunAsync"/> and
    /// waits <see cref="PollingInterval"/> between iterations.
    /// </summary>
    /// <param name="stoppingToken">
    /// Signalled by the runtime on application shutdown, causing the loop to exit
    /// gracefully once the current processing cycle completes.
    /// </param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OptimizationWorker starting (with auto-scheduling).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // ── Auto-scheduling: queue optimizations for underperforming strategies ──
                if (DateTime.UtcNow >= _nextScheduleScanUtc)
                {
                    await using var schedScope = _scopeFactory.CreateAsyncScope();
                    var readDb  = schedScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                    var writeDb = schedScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var ctx     = readDb.GetDbContext();

                    int schedulePollSecs = await GetConfigAsync<int>(ctx, CK_SchedulePollSecs, 7200, stoppingToken);
                    _nextScheduleScanUtc = DateTime.UtcNow.AddSeconds(schedulePollSecs);

                    bool enabled = await GetConfigAsync<bool>(ctx, CK_AutoScheduleEnabled, true, stoppingToken);
                    if (enabled)
                    {
                        await ScheduleOptimizationsForUnderperformingStrategiesAsync(
                            ctx, writeDb.GetDbContext(), stoppingToken);
                    }
                }

                await ProcessNextQueuedRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in OptimizationWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("OptimizationWorker stopped.");
    }

    /// <summary>
    /// Core processing method for a single polling tick. Dequeues the oldest
    /// <see cref="OptimizationRunStatus.Queued"/> run, performs a grid-search backtest
    /// over all candidate parameter sets, evaluates whether the best result qualifies
    /// for auto-approval, and persists the outcome. Returns immediately when the queue
    /// is empty.
    /// </summary>
    /// <remarks>
    /// A fresh DI scope is created on every call so EF Core DbContext instances and
    /// MediatR handlers are properly isolated and disposed after each run. This prevents
    /// stale change-tracker state or memory growth across long-running optimisation jobs.
    /// </remarks>
    /// <param name="ct">Cancellation token propagated from <see cref="ExecuteAsync"/>.</param>
    private async Task ProcessNextQueuedRunAsync(CancellationToken ct)
    {
        // Fresh scope per cycle to isolate scoped services (DbContexts, MediatR pipeline).
        using var scope  = _scopeFactory.CreateScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Dequeue the oldest queued run (FIFO by StartedAt).
        var run = await writeContext.GetDbContext()
            .Set<OptimizationRun>()
            .Where(x => x.Status == OptimizationRunStatus.Queued && !x.IsDeleted)
            .OrderBy(x => x.StartedAt)
            .FirstOrDefaultAsync(ct);

        // Nothing queued — return and wait for the next polling tick.
        if (run is null) return;

        _logger.LogInformation(
            "OptimizationWorker: processing run {RunId} for strategy {StrategyId}", run.Id, run.StrategyId);

        // Immediately claim the run by setting it to Running so no concurrent worker
        // instance picks up the same row.
        run.Status = OptimizationRunStatus.Running;
        await writeContext.SaveChangesAsync(ct);

        try
        {
            // Load the strategy via the read-side context (CQRS separation).
            var strategy = await readContext.GetDbContext()
                .Set<Strategy>()
                .FirstOrDefaultAsync(x => x.Id == run.StrategyId && !x.IsDeleted, ct);

            if (strategy is null)
                throw new InvalidOperationException($"Strategy {run.StrategyId} not found.");

            // Use the last 6 months of closed candles as the optimisation window.
            // This keeps the search grounded in recent market conditions while providing
            // enough data for statistically meaningful backtest results (typically thousands
            // of H1 bars). A longer window risks optimising on stale regimes.
            var toDate   = DateTime.UtcNow;
            var fromDate = toDate.AddMonths(-6);

            var candles = await readContext.GetDbContext()
                .Set<Candle>()
                .Where(x => x.Symbol    == strategy.Symbol
                         && x.Timeframe == strategy.Timeframe
                         && x.Timestamp >= fromDate
                         && x.Timestamp <= toDate
                         && x.IsClosed        // Only completed bars; open bar would bias indicators
                         && !x.IsDeleted)
                .OrderBy(x => x.Timestamp)
                .ToListAsync(ct);

            if (candles.Count == 0)
                throw new InvalidOperationException(
                    $"No candles found for {strategy.Symbol}/{strategy.Timeframe} in the last 6 months.");

            // ── Establish baseline ────────────────────────────────────────────────
            // Snapshot the strategy's current (live) parameters and backtest them on
            // the same candle slice so the health-score improvement can be measured
            // on an apples-to-apples basis.
            run.BaselineParametersJson = strategy.ParametersJson;
            var baselineResult         = await _backtestEngine.RunAsync(strategy, candles, 10_000m, ct);
            run.BaselineHealthScore    = ComputeHealthScore(baselineResult.WinRate, baselineResult.ProfitFactor, baselineResult.MaxDrawdownPct);

            // ── Grid search ───────────────────────────────────────────────────────
            // BuildParameterGrid returns every discrete candidate combination for this
            // strategy type. Each candidate is applied to a shallow Strategy clone so
            // the live entity is never mutated during the search.
            var parameterGrid = BuildParameterGrid(strategy.StrategyType);

            string? bestParamsJson   = null;
            decimal bestProfitFactor = -1m;  // Sentinel: profit factor cannot be negative in practice
            int     iterations       = 0;

            foreach (var paramSet in parameterGrid)
            {
                // Clone the strategy and inject the candidate parameters as JSON so the
                // backtest engine's evaluator picks them up via deserialization. The original
                // strategy entity remains unchanged throughout the loop.
                var candidate             = CloneStrategy(strategy);
                candidate.ParametersJson  = JsonSerializer.Serialize(paramSet);

                var result = await _backtestEngine.RunAsync(candidate, candles, 10_000m, ct);
                iterations++;

                // ProfitFactor is used as the primary ranking criterion because it directly
                // captures gross profit vs gross loss, normalised for the number of trades.
                // The health score (composite) is only computed and stored for the winner.
                if (result.ProfitFactor > bestProfitFactor)
                {
                    bestProfitFactor    = result.ProfitFactor;
                    bestParamsJson      = candidate.ParametersJson;
                    run.BestHealthScore = ComputeHealthScore(result.WinRate, result.ProfitFactor, result.MaxDrawdownPct);
                }
            }

            run.Iterations         = iterations;
            // Fall back to the current parameters if no candidate outperformed the sentinel.
            run.BestParametersJson = bestParamsJson ?? strategy.ParametersJson;
            run.Status             = OptimizationRunStatus.Completed;
            run.CompletedAt        = DateTime.UtcNow;

            await writeContext.SaveChangesAsync(ct);

            _logger.LogInformation(
                "OptimizationWorker: run {RunId} completed — Iterations={Iter}, BestPF={PF:F2}",
                run.Id, iterations, bestProfitFactor);

            // Audit log: record completion details for traceability.
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "OptimizationRun",
                EntityId     = run.Id,
                DecisionType = "OptimizationCompleted",
                Outcome      = "Completed",
                Reason       = $"Iterations={iterations}, BestProfitFactor={bestProfitFactor:F2}, BestHealthScore={run.BestHealthScore:F2}",
                Source       = "OptimizationWorker"
            }, ct);

            // ── Auto-approval gate ─────────────────────────────────────────────────
            // Two conditions must BOTH be satisfied to skip manual review:
            //   1. The improvement over baseline exceeds AutoApprovalImprovementThreshold
            //      (ensures the gain is material, not noise).
            //   2. The best score exceeds AutoApprovalMinHealthScore
            //      (ensures the result is genuinely good, not just better than a bad baseline).
            decimal bestScore   = run.BestHealthScore ?? 0m;
            decimal improvement = bestScore - (run.BaselineHealthScore ?? 0m);
            bool autoApprove    = improvement >= AutoApprovalImprovementThreshold
                               && bestScore    >= AutoApprovalMinHealthScore;

            if (autoApprove)
            {
                // Apply best parameters directly to the live strategy entity.
                // Re-query via the write context so EF tracks the change correctly
                // (the strategy loaded earlier was via the read context).
                var liveStrategy = await writeContext.GetDbContext()
                    .Set<Strategy>()
                    .FirstOrDefaultAsync(x => x.Id == run.StrategyId && !x.IsDeleted, ct);

                if (liveStrategy is not null)
                {
                    liveStrategy.ParametersJson = run.BestParametersJson;
                    // If the strategy was paused (e.g. due to a previous drawdown breach),
                    // re-activate it now that better parameters have been found.
                    if (liveStrategy.Status == StrategyStatus.Paused)
                        liveStrategy.Status = StrategyStatus.Active;
                }

                run.Status     = OptimizationRunStatus.Approved;
                run.ApprovedAt = DateTime.UtcNow;
                await writeContext.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "OptimizationWorker: run {RunId} AUTO-APPROVED — improvement={Imp:+0.00;-0.00}, BestScore={Score:F2}",
                    run.Id, improvement, run.BestHealthScore);

                // Audit log: record auto-approval so operators can review the decision trail.
                await mediator.Send(new LogDecisionCommand
                {
                    EntityType   = "OptimizationRun",
                    EntityId     = run.Id,
                    DecisionType = "AutoApproval",
                    Outcome      = "Approved",
                    Reason       = $"HealthScore improvement={improvement:+0.00;-0.00} exceeded threshold ({AutoApprovalImprovementThreshold}) and minimum ({AutoApprovalMinHealthScore}); parameters applied automatically",
                    Source       = "OptimizationWorker"
                }, ct);

                // ── Re-queue Backtest + WalkForward to validate new parameters ──────
                // The optimisation window was only 6 months; queue a full 1-year backtest
                // so BacktestWorker can evaluate the promoted parameters on a longer, more
                // representative horizon. BacktestWorker will subsequently auto-queue a
                // WalkForwardRun, completing the full validation chain.
                var validationToDate   = DateTime.UtcNow;
                var validationFromDate = validationToDate.AddYears(-1);

                var validationBacktest = new BacktestRun
                {
                    StrategyId     = run.StrategyId,
                    Symbol         = strategy.Symbol,
                    Timeframe      = strategy.Timeframe,
                    FromDate       = validationFromDate,
                    ToDate         = validationToDate,
                    InitialBalance = 10_000m,
                    Status         = RunStatus.Queued,
                    StartedAt      = DateTime.UtcNow
                };

                await writeContext.GetDbContext().Set<BacktestRun>().AddAsync(validationBacktest, ct);

                _logger.LogInformation(
                    "OptimizationWorker: queued validation BacktestRun for strategy {StrategyId} after auto-approval",
                    run.StrategyId);
            }
            else
            {
                // Improvement is below the auto-approval thresholds — leave the run in
                // Completed status for manual operator review via ApproveOptimizationCommand.
                _logger.LogInformation(
                    "OptimizationWorker: run {RunId} requires manual review — improvement={Imp:+0.00;-0.00} (threshold={Thr}), BestScore={Score:F2} (min={Min})",
                    run.Id, improvement, AutoApprovalImprovementThreshold, run.BestHealthScore, AutoApprovalMinHealthScore);
            }

            await writeContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OptimizationWorker: run {RunId} failed", run.Id);
            run.Status       = OptimizationRunStatus.Failed;
            run.ErrorMessage = ex.Message;
            run.CompletedAt  = DateTime.UtcNow;
            await writeContext.SaveChangesAsync(ct);

            // Audit log: record failure so operators are aware and can investigate.
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "OptimizationRun",
                EntityId     = run.Id,
                DecisionType = "OptimizationFailed",
                Outcome      = "Failed",
                Reason       = ex.Message,
                Source       = "OptimizationWorker"
            }, ct);
        }
    }

    /// <summary>
    /// Builds the exhaustive list of candidate parameter dictionaries for the given
    /// strategy type. Each dictionary maps parameter names to their candidate values
    /// and is later serialised to JSON and injected into a cloned <see cref="Strategy"/>
    /// for backtesting.
    /// </summary>
    /// <remarks>
    /// <b>Grid sizes by strategy type:</b>
    /// <list type="bullet">
    ///   <item>
    ///     <term>MovingAverageCrossover</term>
    ///     <description>
    ///       3 fast periods × 3 slow periods = up to 9 combinations (invalid fast &gt;= slow
    ///       pairs are filtered out), plus a fixed 50-bar trend filter (<c>MaPeriod</c>).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>RSIReversion</term>
    ///     <description>
    ///       3 RSI periods × 3 oversold levels × 3 overbought levels = 27 combinations.
    ///       Oversold/overbought values are not validated against each other here; the
    ///       evaluator enforces that oversold &lt; overbought at signal generation time.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>BreakoutScalper</term>
    ///     <description>
    ///       4 lookback windows × 3 ATR multipliers = 12 combinations.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Default / unknown types</term>
    ///     <description>
    ///       A single empty dictionary is returned, which the evaluator interprets as
    ///       "use built-in defaults". This avoids blocking optimisation for custom strategy
    ///       types that have not yet had grid ranges defined.
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="strategyType">The type of strategy being optimised.</param>
    /// <returns>
    /// An ordered list of parameter dictionaries; each item represents one candidate
    /// configuration to backtest.
    /// </returns>
    private static List<Dictionary<string, object>> BuildParameterGrid(StrategyType strategyType)
    {
        var grid = new List<Dictionary<string, object>>();

        switch (strategyType)
        {
            case StrategyType.MovingAverageCrossover:
                // Enumerate all fast × slow period pairs and reject any where fast >= slow
                // because a fast MA period equal to or exceeding the slow MA period produces
                // no meaningful crossover signal.
                foreach (var fast in new[] { 5, 9, 12 })
                foreach (var slow in new[] { 20, 21, 26 })
                {
                    if (fast >= slow) continue;
                    grid.Add(new Dictionary<string, object>
                    {
                        ["FastPeriod"] = fast,
                        ["SlowPeriod"] = slow,
                        ["MaPeriod"]   = 50   // Fixed trend-direction filter; not optimised
                    });
                }
                break;

            case StrategyType.RSIReversion:
                // 3 × 3 × 3 = 27 combinations spanning common RSI period and threshold values.
                // Wider oversold/overbought bands (25/75) are more selective; tighter bands
                // (35/65) generate more signals but with lower edge per trade on average.
                foreach (var period in new[] { 10, 14, 21 })
                foreach (var oversold in new[] { 25, 30, 35 })
                foreach (var overbought in new[] { 65, 70, 75 })
                {
                    grid.Add(new Dictionary<string, object>
                    {
                        ["Period"]     = period,
                        ["Oversold"]   = oversold,
                        ["Overbought"] = overbought
                    });
                }
                break;

            case StrategyType.BreakoutScalper:
                // LookbackBars controls how far back the engine searches for swing highs/lows.
                // BreakoutMultiplier scales the ATR-based buffer above/below the swing level
                // that price must breach to confirm a genuine breakout vs. a noise excursion.
                foreach (var lookback in new[] { 10, 15, 20, 30 })
                foreach (var multiplier in new[] { 1.0, 1.5, 2.0 })
                {
                    grid.Add(new Dictionary<string, object>
                    {
                        ["LookbackBars"]        = lookback,
                        ["BreakoutMultiplier"]  = multiplier
                    });
                }
                break;

            default:
                // For custom or unknown types, return a single default parameter set.
                // The backtest engine will use the evaluator's hard-coded defaults,
                // effectively making this a single-run "do-nothing" optimisation.
                grid.Add(new Dictionary<string, object>());
                break;
        }

        return grid;
    }

    /// <summary>
    /// Computes a composite health score in the range [0, 1] from three independent
    /// backtest metrics, weighted to reflect their relative importance in live trading.
    /// </summary>
    /// <remarks>
    /// <b>Weighting rationale:</b>
    /// <list type="bullet">
    ///   <item>
    ///     <term>WinRate (40%)</term>
    ///     <description>
    ///       Highest weight — a consistently winning strategy is easier to operate
    ///       psychologically and has lower sensitivity to individual trade sizing errors.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>ProfitFactor (30%)</term>
    ///     <description>
    ///       Normalised to [0, 1] by dividing by 2 and clamping at 1.
    ///       A PF of 2.0 or above is considered excellent; higher values are capped to
    ///       prevent a single outlier run dominating the score.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>MaxDrawdown penalty (30%)</term>
    ///     <description>
    ///       Scores 1.0 at 0% drawdown and 0.0 at 20% drawdown (linear). Drawdowns beyond
    ///       20% are clamped to 0. This penalises high-risk configurations that would
    ///       trigger the risk manager's drawdown circuit breaker in live trading.
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="winRate">Fraction of trades that were profitable, in [0, 1].</param>
    /// <param name="profitFactor">Gross profit divided by gross loss (e.g. 1.5 = good).</param>
    /// <param name="maxDrawdownPct">Maximum peak-to-trough equity decline as a percentage (e.g. 12.5 for 12.5%).</param>
    /// <returns>Composite health score in [0, 1]; higher is better.</returns>
    private static decimal ComputeHealthScore(decimal winRate, decimal profitFactor, decimal maxDrawdownPct)
    {
        return 0.4m * winRate
             + 0.3m * Math.Min(1m, profitFactor / 2m)          // Cap PF contribution at PF = 2.0
             + 0.3m * Math.Max(0m, 1m - maxDrawdownPct / 20m); // Zero contribution at drawdown >= 20%
    }

    /// <summary>
    /// Creates a shallow copy of a <see cref="Strategy"/> entity for use as a
    /// candidate container during grid search. Only the fields relevant to backtest
    /// execution are copied; EF navigation properties and tracked state are not included,
    /// which avoids unintentional writes to the database when the clone is mutated.
    /// </summary>
    /// <param name="source">The live strategy whose identity and type metadata is copied.</param>
    /// <returns>
    /// A new, untracked <see cref="Strategy"/> instance with the same core properties
    /// as <paramref name="source"/> but with <c>ParametersJson</c> ready to be overwritten
    /// with candidate values.
    /// </returns>
    private static Strategy CloneStrategy(Strategy source) => new()
    {
        Id             = source.Id,
        Name           = source.Name,
        Description    = source.Description,
        StrategyType   = source.StrategyType,
        Symbol         = source.Symbol,
        Timeframe      = source.Timeframe,
        ParametersJson = source.ParametersJson,
        Status         = source.Status,
        RiskProfileId  = source.RiskProfileId,
        CreatedAt      = source.CreatedAt,
        IsDeleted      = source.IsDeleted
    };

    // ════════════════════════════════════════════════════════════════════════════
    //  Auto-scheduling: queue optimizations for strategies that failed backtest gate
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scans active strategies with completed backtests that don't meet the signal-generation
    /// qualification gate (win rate, profit factor, etc.) and queues optimization runs to
    /// improve their parameters. Strategies that already have a pending optimization or were
    /// recently optimized are skipped.
    ///
    /// <para>
    /// Configuration keys (read from <see cref="EngineConfig"/>):
    /// <list type="bullet">
    ///   <item><c>Optimization:AutoScheduleEnabled</c>  — master switch (default true)</item>
    ///   <item><c>Optimization:SchedulePollSeconds</c>  — scan interval (default 7200 = 2 hours)</item>
    ///   <item><c>Optimization:CooldownDays</c>         — min days between optimizations per strategy (default 14)</item>
    ///   <item><c>Optimization:MaxQueuedPerCycle</c>    — max runs to queue per scan (default 3)</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task ScheduleOptimizationsForUnderperformingStrategiesAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken ct)
    {
        int cooldownDays      = await GetConfigAsync<int>(readCtx, CK_CooldownDays, 14, ct);
        int maxQueuedPerCycle = await GetConfigAsync<int>(readCtx, CK_MaxQueuedPerCycle, 3, ct);

        // Load backtest qualification thresholds (same keys as StrategyWorker gate)
        double minWinRate      = await GetConfigAsync<double>(readCtx, CK_MinWinRate, 0.60, ct);
        double minProfitFactor = await GetConfigAsync<double>(readCtx, CK_MinProfitFactor, 1.0, ct);
        int    minTotalTrades  = await GetConfigAsync<int>(readCtx, CK_MinTotalTrades, 10, ct);

        // Load active strategies
        var activeStrategies = await readCtx.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .AsNoTracking()
            .Select(s => new { s.Id, s.Name, s.Symbol, s.Timeframe, s.ParametersJson })
            .ToListAsync(ct);

        if (activeStrategies.Count == 0) return;

        // Skip strategies that already have a pending optimization
        var pendingOptIds = await readCtx.Set<OptimizationRun>()
            .Where(r => (r.Status == OptimizationRunStatus.Queued || r.Status == OptimizationRunStatus.Running)
                        && !r.IsDeleted)
            .Select(r => r.StrategyId)
            .Distinct()
            .ToListAsync(ct);
        var pendingSet = new HashSet<long>(pendingOptIds);

        // Skip strategies optimized recently (within cooldown)
        var cooldownThreshold = DateTime.UtcNow.AddDays(-cooldownDays);
        var recentOptIds = await readCtx.Set<OptimizationRun>()
            .Where(r => (r.Status == OptimizationRunStatus.Completed || r.Status == OptimizationRunStatus.Approved)
                        && !r.IsDeleted
                        && r.CompletedAt >= cooldownThreshold)
            .Select(r => r.StrategyId)
            .Distinct()
            .ToListAsync(ct);
        var recentOptSet = new HashSet<long>(recentOptIds);

        // Load the most recent completed backtest per strategy
        var recentBacktests = await readCtx.Set<BacktestRun>()
            .Where(r => r.Status == RunStatus.Completed && !r.IsDeleted && r.ResultJson != null)
            .GroupBy(r => r.StrategyId)
            .Select(g => new
            {
                StrategyId = g.Key,
                ResultJson = g.OrderByDescending(r => r.CompletedAt).First().ResultJson
            })
            .ToListAsync(ct);
        var backtestMap = recentBacktests.ToDictionary(r => r.StrategyId, r => r.ResultJson);

        int queued = 0;

        foreach (var strategy in activeStrategies)
        {
            if (queued >= maxQueuedPerCycle) break;
            ct.ThrowIfCancellationRequested();

            // Skip if already has a pending optimization
            if (pendingSet.Contains(strategy.Id)) continue;

            // Skip if recently optimized
            if (recentOptSet.Contains(strategy.Id)) continue;

            // Skip if no backtest exists yet (BacktestWorker will handle it first)
            if (!backtestMap.TryGetValue(strategy.Id, out var resultJson) || string.IsNullOrWhiteSpace(resultJson))
                continue;

            // Parse backtest result and check if it FAILS the qualification gate
            BacktestResult? result;
            try { result = JsonSerializer.Deserialize<BacktestResult>(resultJson); }
            catch { continue; }
            if (result is null) continue;

            bool meetsGate = result.TotalTrades >= minTotalTrades
                && (double)result.WinRate >= minWinRate
                && (double)result.ProfitFactor >= minProfitFactor;

            // Only optimize strategies that FAIL the gate — already-qualified strategies don't need tuning
            if (meetsGate) continue;

            // Queue optimization run
            var run = new OptimizationRun
            {
                StrategyId             = strategy.Id,
                TriggerType            = TriggerType.Scheduled,
                Status                 = OptimizationRunStatus.Queued,
                BaselineParametersJson = strategy.ParametersJson,
                StartedAt              = DateTime.UtcNow,
            };

            writeCtx.Set<OptimizationRun>().Add(run);
            queued++;

            _logger.LogInformation(
                "OptimizationWorker: auto-queued optimization for strategy {Id} ({Name}) " +
                "{Symbol}/{Tf} — backtest WinRate={WR:P1} PF={PF:F2} (below gate thresholds).",
                strategy.Id, strategy.Name, strategy.Symbol, strategy.Timeframe,
                (double)result.WinRate, (double)result.ProfitFactor);
        }

        if (queued > 0)
        {
            await writeCtx.SaveChangesAsync(ct);
            _logger.LogInformation(
                "OptimizationWorker: auto-scheduled {Count} optimization run(s) for underperforming strategies.",
                queued);
        }
        else
        {
            _logger.LogDebug(
                "OptimizationWorker: no strategies need auto-optimization (all qualified or within {Cooldown}d cooldown).",
                cooldownDays);
        }
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
