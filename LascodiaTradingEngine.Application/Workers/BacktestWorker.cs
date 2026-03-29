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
/// <b>Auto-scheduling:</b> In addition to processing manually queued runs, the worker
/// periodically scans all active strategies and automatically queues backtest runs for
/// any strategy that has not been backtested within the configured cooldown period
/// (<c>Backtest:CooldownDays</c>, default 7). This ensures every active strategy has
/// up-to-date backtest metrics without requiring manual intervention.
/// </para>
///
/// <para>
/// <b>Pipeline position:</b> BacktestWorker sits at the entry point of the
/// validation pipeline. Once a backtest completes successfully, it automatically
/// seeds the next stage by queuing a <see cref="WalkForwardRun"/> for the same
/// symbol/timeframe/date window. The WalkForwardWorker then picks that up and
/// evaluates out-of-sample generalisation. The full chain is:
/// <br/>
/// <c>BacktestRun (Queued) → BacktestWorker → WalkForwardRun (Queued) → WalkForwardWorker</c>
/// </para>
///
/// <para>
/// <b>Scheduling configuration (read from <see cref="EngineConfig"/>):</b>
/// <list type="bullet">
///   <item><c>Backtest:SchedulePollSeconds</c>  — how often to check for stale strategies (default 3600 = 1 hour)</item>
///   <item><c>Backtest:CooldownDays</c>         — min days between backtests per strategy (default 7)</item>
///   <item><c>Backtest:WindowDays</c>            — historical data window for each backtest (default 365)</item>
///   <item><c>Backtest:InitialBalance</c>        — starting equity for simulation (default 10000)</item>
///   <item><c>Backtest:MaxQueuedPerCycle</c>     — max runs to queue per scheduling cycle (default 5)</item>
///   <item><c>Backtest:MinCandlesRequired</c>    — skip strategy if fewer candles available (default 100)</item>
///   <item><c>Backtest:Enabled</c>               — master switch for auto-scheduling (default true)</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Error handling:</b> If the backtest itself throws (e.g. no candle data, missing
/// strategy), the run is marked <see cref="RunStatus.Failed"/> with the exception
/// message preserved in <c>ErrorMessage</c>. Unexpected polling-loop exceptions are
/// caught and logged without crashing the service.
/// </para>
///
/// <para>
/// <b>Event publication:</b> On success, a <see cref="BacktestCompletedIntegrationEvent"/>
/// is published to the event bus so downstream consumers can react without polling.
/// </para>
/// </summary>
public class BacktestWorker : BackgroundService
{
    private readonly ILogger<BacktestWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBacktestEngine _backtestEngine;

    // ── EngineConfig keys ───────────────────────────────────────────────────
    private const string CK_SchedulePollSecs   = "Backtest:SchedulePollSeconds";
    private const string CK_CooldownDays       = "Backtest:CooldownDays";
    private const string CK_WindowDays         = "Backtest:WindowDays";
    private const string CK_InitialBalance     = "Backtest:InitialBalance";
    private const string CK_MaxQueuedPerCycle  = "Backtest:MaxQueuedPerCycle";
    private const string CK_MinCandles         = "Backtest:MinCandlesRequired";
    private const string CK_Enabled            = "Backtest:Enabled";

    /// <summary>Fast polling interval for processing queued runs (10 seconds).</summary>
    private static readonly TimeSpan ProcessingPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>Tracks when the next auto-scheduling scan should run.</summary>
    private DateTime _nextScheduleScanUtc = DateTime.MinValue;

    public BacktestWorker(
        ILogger<BacktestWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBacktestEngine backtestEngine)
    {
        _logger         = logger;
        _scopeFactory   = scopeFactory;
        _backtestEngine = backtestEngine;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BacktestWorker starting (with auto-scheduling).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var ctx         = readContext.GetDbContext();

                // ── Auto-scheduling: periodically queue backtests for stale strategies ──
                if (DateTime.UtcNow >= _nextScheduleScanUtc)
                {
                    int schedulePollSecs = await GetConfigAsync<int>(ctx, CK_SchedulePollSecs, 3600, stoppingToken);
                    _nextScheduleScanUtc = DateTime.UtcNow.AddSeconds(schedulePollSecs);

                    bool enabled = await GetConfigAsync<bool>(ctx, CK_Enabled, true, stoppingToken);
                    if (enabled)
                    {
                        await ScheduleBacktestsForStaleStrategiesAsync(
                            ctx, writeContext.GetDbContext(), stoppingToken);
                    }
                }

                // ── Process next queued run ─────────────────────────────────────────
                await ProcessNextQueuedRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in BacktestWorker polling loop");
            }

            await Task.Delay(ProcessingPollInterval, stoppingToken);
        }

        _logger.LogInformation("BacktestWorker stopped.");
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Auto-scheduling: queue backtests for strategies that haven't been tested recently
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scans all active strategies and queues a <see cref="BacktestRun"/> for each one
    /// that has not been backtested within the configured cooldown period. Skips strategies
    /// that already have a queued or running backtest, or that lack sufficient candle data.
    /// </summary>
    private async Task ScheduleBacktestsForStaleStrategiesAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken ct)
    {
        int cooldownDays      = await GetConfigAsync<int>(readCtx, CK_CooldownDays, 7, ct);
        int windowDays        = await GetConfigAsync<int>(readCtx, CK_WindowDays, 365, ct);
        int maxQueuedPerCycle = await GetConfigAsync<int>(readCtx, CK_MaxQueuedPerCycle, 5, ct);
        int minCandles        = await GetConfigAsync<int>(readCtx, CK_MinCandles, 100, ct);
        decimal initialBalance = await GetConfigAsync<decimal>(readCtx, CK_InitialBalance, 10_000m, ct);

        // Load all active strategies
        var activeStrategies = await readCtx.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .AsNoTracking()
            .Select(s => new { s.Id, s.Symbol, s.Timeframe, s.Name })
            .ToListAsync(ct);

        if (activeStrategies.Count == 0) return;

        // Load strategy IDs that already have a queued or running backtest (skip these)
        var pendingStrategyIds = await readCtx.Set<BacktestRun>()
            .Where(r => (r.Status == RunStatus.Queued || r.Status == RunStatus.Running) && !r.IsDeleted)
            .Select(r => r.StrategyId)
            .Distinct()
            .ToListAsync(ct);
        var pendingSet = new HashSet<long>(pendingStrategyIds);

        // Load the most recent completed/failed backtest per strategy
        var recentBacktests = await readCtx.Set<BacktestRun>()
            .Where(r => (r.Status == RunStatus.Completed || r.Status == RunStatus.Failed) && !r.IsDeleted)
            .GroupBy(r => r.StrategyId)
            .Select(g => new { StrategyId = g.Key, LastCompletedAt = g.Max(r => r.CompletedAt) })
            .ToListAsync(ct);
        var lastBacktestMap = recentBacktests.ToDictionary(r => r.StrategyId, r => r.LastCompletedAt);

        var cooldownThreshold = DateTime.UtcNow.AddDays(-cooldownDays);
        int queued = 0;

        foreach (var strategy in activeStrategies)
        {
            if (queued >= maxQueuedPerCycle) break;
            ct.ThrowIfCancellationRequested();

            // Skip if already has a pending backtest
            if (pendingSet.Contains(strategy.Id))
                continue;

            // Skip if backtested recently (within cooldown)
            if (lastBacktestMap.TryGetValue(strategy.Id, out var lastCompleted)
                && lastCompleted.HasValue
                && lastCompleted.Value >= cooldownThreshold)
            {
                continue;
            }

            // Verify sufficient candle data exists for this symbol/timeframe
            var windowStart = DateTime.UtcNow.AddDays(-windowDays);
            int candleCount = await readCtx.Set<Candle>()
                .CountAsync(c =>
                    c.Symbol    == strategy.Symbol &&
                    c.Timeframe == strategy.Timeframe &&
                    c.Timestamp >= windowStart &&
                    c.IsClosed && !c.IsDeleted, ct);

            if (candleCount < minCandles)
            {
                _logger.LogDebug(
                    "BacktestWorker: skipping auto-backtest for strategy {Id} ({Name}) — " +
                    "only {Count} candles available (need {Min}).",
                    strategy.Id, strategy.Name, candleCount, minCandles);
                continue;
            }

            // Queue the backtest run
            var run = new BacktestRun
            {
                StrategyId     = strategy.Id,
                Symbol         = strategy.Symbol,
                Timeframe      = strategy.Timeframe,
                FromDate       = windowStart,
                ToDate         = DateTime.UtcNow,
                InitialBalance = initialBalance,
                Status         = RunStatus.Queued,
                StartedAt      = DateTime.UtcNow,
            };

            writeCtx.Set<BacktestRun>().Add(run);
            queued++;

            _logger.LogInformation(
                "BacktestWorker: auto-queued backtest for strategy {Id} ({Name}) " +
                "{Symbol}/{Tf} — window {From:yyyy-MM-dd} to {To:yyyy-MM-dd} ({Candles} candles).",
                strategy.Id, strategy.Name, strategy.Symbol, strategy.Timeframe,
                windowStart, DateTime.UtcNow, candleCount);
        }

        if (queued > 0)
        {
            await writeCtx.SaveChangesAsync(ct);
            _logger.LogInformation(
                "BacktestWorker: auto-scheduled {Count} backtest run(s) for stale strategies " +
                "(cooldown={Cooldown}d, window={Window}d).",
                queued, cooldownDays, windowDays);
        }
        else
        {
            _logger.LogDebug(
                "BacktestWorker: no strategies need auto-backtesting (all within {Cooldown}d cooldown).",
                cooldownDays);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Run processing (unchanged logic, restructured for async scope)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Core processing method for a single polling tick. Looks up the oldest
    /// <see cref="RunStatus.Queued"/> backtest run in FIFO order, executes it,
    /// persists the result, auto-queues a <see cref="WalkForwardRun"/>, and publishes
    /// the completion event. Returns immediately (no-op) when the queue is empty.
    /// </summary>
    private async Task ProcessNextQueuedRunAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var writeContext  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var eventService  = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();

        var db = writeContext.GetDbContext();

        var run = await db.Set<BacktestRun>()
            .Where(r => r.Status == RunStatus.Queued && !r.IsDeleted)
            .OrderBy(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (run == null) return;

        _logger.LogInformation(
            "BacktestWorker: processing run {RunId} for strategy {StrategyId}", run.Id, run.StrategyId);

        run.Status = RunStatus.Running;
        await writeContext.SaveChangesAsync(ct);

        try
        {
            var strategy = await readContext.GetDbContext()
                .Set<Strategy>()
                .FirstOrDefaultAsync(s => s.Id == run.StrategyId && !s.IsDeleted, ct);

            if (strategy == null)
                throw new InvalidOperationException($"Strategy {run.StrategyId} not found.");

            var candles = await readContext.GetDbContext()
                .Set<Candle>()
                .Where(c =>
                    c.Symbol    == run.Symbol    &&
                    c.Timeframe == run.Timeframe &&
                    c.Timestamp >= run.FromDate  &&
                    c.Timestamp <= run.ToDate    &&
                    c.IsClosed                   &&
                    !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            if (candles.Count == 0)
                throw new InvalidOperationException(
                    $"No closed candles found for {run.Symbol}/{run.Timeframe} between {run.FromDate:yyyy-MM-dd} and {run.ToDate:yyyy-MM-dd}.");

            var result = await _backtestEngine.RunAsync(strategy, candles, run.InitialBalance, ct);

            run.Status      = RunStatus.Completed;
            run.ResultJson  = JsonSerializer.Serialize(result);
            run.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "BacktestWorker: run {RunId} completed — TotalTrades={Trades}, WinRate={WinRate:P2}",
                run.Id, result.TotalTrades, (double)result.WinRate);

            // ── Auto-queue a WalkForwardRun using the same window ─────────────────
            var walkForwardRun = new WalkForwardRun
            {
                StrategyId        = run.StrategyId,
                Symbol            = run.Symbol,
                Timeframe         = run.Timeframe,
                FromDate          = run.FromDate,
                ToDate            = run.ToDate,
                InSampleDays      = (int)((run.ToDate - run.FromDate).TotalDays * 0.7),
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
            _logger.LogError(ex, "BacktestWorker: run {RunId} failed", run.Id);
            run.Status       = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            run.CompletedAt  = DateTime.UtcNow;
        }

        await writeContext.SaveChangesAsync(ct);

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

    // ── Config helper ─────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
