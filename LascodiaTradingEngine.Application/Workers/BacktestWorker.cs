using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that picks up queued backtest runs, executes them via
/// BacktestEngine, and persists the result (or error) back to the database.
/// </summary>
public class BacktestWorker : BackgroundService
{
    private readonly ILogger<BacktestWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBacktestEngine _backtestEngine;
    private readonly IEventBus _eventBus;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(10);

    public BacktestWorker(
        ILogger<BacktestWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBacktestEngine backtestEngine,
        IEventBus eventBus)
    {
        _logger         = logger;
        _scopeFactory   = scopeFactory;
        _backtestEngine = backtestEngine;
        _eventBus       = eventBus;
    }

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
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in BacktestWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("BacktestWorker stopped");
    }

    private async Task ProcessNextQueuedRunAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var writeContext  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var db = writeContext.GetDbContext();

        // Pick the oldest queued run
        var run = await db.Set<BacktestRun>()
            .Where(r => r.Status == RunStatus.Queued && !r.IsDeleted)
            .OrderBy(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (run == null) return;

        _logger.LogInformation(
            "BacktestWorker: processing run {RunId} for strategy {StrategyId}", run.Id, run.StrategyId);

        // Mark as Running
        run.Status = RunStatus.Running;
        await writeContext.SaveChangesAsync(ct);

        try
        {
            // Load strategy from read-side
            var strategy = await readContext.GetDbContext()
                .Set<Strategy>()
                .FirstOrDefaultAsync(s => s.Id == run.StrategyId && !s.IsDeleted, ct);

            if (strategy == null)
                throw new InvalidOperationException($"Strategy {run.StrategyId} not found.");

            // Load candles within the date range
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

        // ── Publish BacktestCompletedIntegrationEvent ─────────────────────────
        if (run.Status == RunStatus.Completed)
        {
            _eventBus.Publish(new BacktestCompletedIntegrationEvent
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
