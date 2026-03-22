using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background worker that drives the backtesting pipeline by continuously polling
/// the database for queued <see cref="BacktestRun"/> records and executing each one
/// against historical candle data via the <see cref="IBacktestEngine"/>.
///
/// <para>
/// <b>Pipeline position:</b> BacktestWorker sits at the entry point of the
/// validation pipeline. Once a backtest completes successfully, it automatically
/// seeds the next stage by queuing a <see cref="WalkForwardRun"/> for the same
/// symbol/timeframe/date window. The WalkForwardWorker then picks that up and
/// evaluates out-of-sample generalisation. The full chain is:
/// <br/>
/// <c>BacktestRun (Queued) → BacktestWorker → WalkForwardRun (Queued) → WalkForwardWorker</c>
/// <br/>
/// Separately, an OptimizationRun that passes auto-approval also queues a fresh
/// BacktestRun to re-validate the newly promoted parameters.
/// </para>
///
/// <para>
/// <b>Polling model:</b> The worker wakes every <see cref="PollingInterval"/> seconds
/// (10 s), picks the single oldest queued run (FIFO by <c>StartedAt</c>), and processes
/// it to completion before sleeping again. One run per wake cycle keeps resource usage
/// bounded and avoids parallel write conflicts on shared strategy rows.
/// </para>
///
/// <para>
/// <b>Error handling:</b> If the backtest itself throws (e.g. no candle data, missing
/// strategy), the run is marked <see cref="RunStatus.Failed"/> with the exception
/// message preserved in <c>ErrorMessage</c>. Unexpected polling-loop exceptions are
/// caught and logged without crashing the service, so the worker keeps retrying on the
/// next tick.
/// </para>
///
/// <para>
/// <b>Event publication:</b> On success, a <see cref="BacktestCompletedIntegrationEvent"/>
/// is published to the event bus so downstream consumers (e.g. alert workers, dashboard
/// services) can react without polling the database themselves.
/// </para>
/// </summary>
public class BacktestWorker : BackgroundService
{
    private readonly ILogger<BacktestWorker> _logger;

    /// <summary>
    /// Used to create per-iteration DI scopes so that scoped services such as
    /// <see cref="IWriteApplicationDbContext"/> and <see cref="IReadApplicationDbContext"/>
    /// are properly disposed after each processing cycle.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Singleton backtest engine that simulates strategy execution over historical
    /// candle data, returning metrics such as win rate, profit factor, and Sharpe ratio.
    /// </summary>
    private readonly IBacktestEngine _backtestEngine;

    /// <summary>
    /// How long the worker sleeps between polling cycles. A 10-second interval keeps
    /// latency low for interactive backtest requests while avoiding tight-loop CPU burn.
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="logger">Structured logger for diagnostic output.</param>
    /// <param name="scopeFactory">Factory for creating per-cycle DI scopes.</param>
    /// <param name="backtestEngine">Engine that executes a strategy over a candle slice.</param>
    public BacktestWorker(
        ILogger<BacktestWorker> logger,
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
    /// Signalled by the runtime when the application is shutting down, causing the
    /// loop to exit gracefully after the current processing cycle completes.
    /// </param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BacktestWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextQueuedRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — exit the loop cleanly without logging as an error.
                break;
            }
            catch (Exception ex)
            {
                // Any other unexpected exception in the outer loop is logged and swallowed
                // so the worker stays alive and retries on the next tick.
                _logger.LogError(ex, "Unexpected error in BacktestWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("BacktestWorker stopped");
    }

    /// <summary>
    /// Core processing method for a single polling tick. Looks up the oldest
    /// <see cref="RunStatus.Queued"/> backtest run in FIFO order, executes it,
    /// persists the result, auto-queues a <see cref="WalkForwardRun"/>, and publishes
    /// the completion event. Returns immediately (no-op) when the queue is empty.
    /// </summary>
    /// <remarks>
    /// A fresh DI scope is created for each call so that EF Core DbContext instances
    /// (which are scoped) are isolated per run and disposed promptly after the
    /// <c>using</c> block exits. This avoids stale change-tracker state accumulating
    /// across successive runs.
    /// </remarks>
    /// <param name="ct">Cancellation token propagated from <see cref="ExecuteAsync"/>.</param>
    private async Task ProcessNextQueuedRunAsync(CancellationToken ct)
    {
        // Create a fresh DI scope so scoped DbContext instances are properly isolated
        // and disposed at the end of this processing cycle.
        using var scope   = _scopeFactory.CreateScope();
        var writeContext  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var eventService  = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();

        var db = writeContext.GetDbContext();

        // Pick the oldest queued run (FIFO by StartedAt) to ensure fair processing
        // order when multiple runs are queued simultaneously.
        var run = await db.Set<BacktestRun>()
            .Where(r => r.Status == RunStatus.Queued && !r.IsDeleted)
            .OrderBy(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);

        // Nothing in the queue — sleep until the next polling tick.
        if (run == null) return;

        _logger.LogInformation(
            "BacktestWorker: processing run {RunId} for strategy {StrategyId}", run.Id, run.StrategyId);

        // Immediately flip the status to Running and persist it so that no other worker
        // instance (or future polling tick) picks up the same run concurrently.
        run.Status = RunStatus.Running;
        await writeContext.SaveChangesAsync(ct);

        try
        {
            // Load strategy from the read-side context to keep CQRS separation intact.
            // The strategy's ParametersJson drives which evaluator logic is applied
            // inside the backtest engine.
            var strategy = await readContext.GetDbContext()
                .Set<Strategy>()
                .FirstOrDefaultAsync(s => s.Id == run.StrategyId && !s.IsDeleted, ct);

            if (strategy == null)
                throw new InvalidOperationException($"Strategy {run.StrategyId} not found.");

            // Fetch closed candles that fall within the run's date window, ordered
            // chronologically. Only closed (completed) candles are used — open candles
            // would include partial bars that distort indicator calculations.
            var candles = await readContext.GetDbContext()
                .Set<Candle>()
                .Where(c =>
                    c.Symbol    == run.Symbol    &&
                    c.Timeframe == run.Timeframe &&
                    c.Timestamp >= run.FromDate  &&
                    c.Timestamp <= run.ToDate    &&
                    c.IsClosed                   &&  // Exclude the current in-progress bar
                    !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            if (candles.Count == 0)
                throw new InvalidOperationException(
                    $"No closed candles found for {run.Symbol}/{run.Timeframe} between {run.FromDate:yyyy-MM-dd} and {run.ToDate:yyyy-MM-dd}.");

            // Execute the backtest: the engine replays the strategy's signal logic bar-by-bar
            // over the candle slice, simulating entries, exits, and position management,
            // then returns aggregate performance metrics (WinRate, ProfitFactor, SharpeRatio, etc.).
            var result = await _backtestEngine.RunAsync(strategy, candles, run.InitialBalance, ct);

            run.Status      = RunStatus.Completed;
            // Serialise the full result object so it can be surfaced via the API without
            // requiring a separate normalised result table.
            run.ResultJson  = JsonSerializer.Serialize(result);
            run.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "BacktestWorker: run {RunId} completed — TotalTrades={Trades}, WinRate={WinRate:P2}",
                run.Id, result.TotalTrades, (double)result.WinRate);

            // ── Auto-queue a WalkForwardRun using the same window ─────────────────
            // Walk-forward validation uses a 70/30 in-sample/out-of-sample split of the
            // same total date range. This mirrors common industry practice where ~70% of
            // data is used to "train" (fit indicators / confirm regime) and ~30% tests
            // whether the strategy generalises to truly unseen data. The split is
            // expressed in calendar days rather than bar count to remain timeframe-agnostic.
            var walkForwardRun = new WalkForwardRun
            {
                StrategyId        = run.StrategyId,
                Symbol            = run.Symbol,
                Timeframe         = run.Timeframe,
                FromDate          = run.FromDate,
                ToDate            = run.ToDate,
                // 70% of total days allocated to in-sample (strategy fitting / look-back)
                InSampleDays      = (int)((run.ToDate - run.FromDate).TotalDays * 0.7),
                // 30% of total days allocated to out-of-sample (blind forward evaluation)
                OutOfSampleDays   = (int)((run.ToDate - run.FromDate).TotalDays * 0.3),
                InitialBalance    = run.InitialBalance,
                Status            = RunStatus.Queued,
                StartedAt         = DateTime.UtcNow
            };

            await writeContext.GetDbContext().Set<WalkForwardRun>().AddAsync(walkForwardRun, ct);

            _logger.LogInformation(
                "BacktestWorker: auto-queued WalkForwardRun for strategy {StrategyId} following run {RunId}",
                run.StrategyId, run.Id);
        }
        catch (Exception ex)
        {
            // Mark the run as failed and capture the exception message for operator visibility.
            // The WalkForwardRun is NOT queued when the backtest itself fails, preventing
            // downstream OOS validation from running on a broken or data-deficient parameter set.
            _logger.LogError(ex, "BacktestWorker: run {RunId} failed", run.Id);
            run.Status       = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            run.CompletedAt  = DateTime.UtcNow;
        }

        // Persist the final status (Completed/Failed), result JSON, walk-forward row,
        // and CompletedAt timestamp in a single SaveChanges call.
        await writeContext.SaveChangesAsync(ct);

        // ── Publish BacktestCompletedIntegrationEvent ─────────────────────────
        // Only published on success. Downstream consumers (e.g. alert dispatchers,
        // reporting services) subscribe to this event via the event bus rather than
        // polling the database, keeping the system loosely coupled.
        if (run.Status == RunStatus.Completed)
        {
            await eventService.SaveAndPublish(writeContext, new BacktestCompletedIntegrationEvent
            {
                BacktestRunId  = run.Id,
                StrategyId     = run.StrategyId,
                Symbol         = run.Symbol,
                Timeframe      = run.Timeframe,
                FromDate       = run.FromDate,
                ToDate         = run.ToDate,
                InitialBalance = run.InitialBalance,
                CompletedAt    = run.CompletedAt ?? DateTime.UtcNow
            });
        }
    }
}
