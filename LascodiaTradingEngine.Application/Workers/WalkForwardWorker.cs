using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that picks up queued walk-forward runs, slides in-sample /
/// out-of-sample windows across the candle history, and persists the aggregated
/// OOS scores back to the database.
/// </summary>
public class WalkForwardWorker : BackgroundService
{
    private readonly ILogger<WalkForwardWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBacktestEngine _backtestEngine;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    public WalkForwardWorker(
        ILogger<WalkForwardWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBacktestEngine backtestEngine)
    {
        _logger         = logger;
        _scopeFactory   = scopeFactory;
        _backtestEngine = backtestEngine;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WalkForwardWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in WalkForwardWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("WalkForwardWorker stopped");
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var db = writeContext.GetDbContext();

        // Pick the oldest queued walk-forward run
        var run = await db.Set<WalkForwardRun>()
            .Where(r => r.Status == RunStatus.Queued && !r.IsDeleted)
            .OrderBy(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (run == null) return;

        _logger.LogInformation(
            "WalkForwardWorker: processing run {RunId} for strategy {StrategyId}", run.Id, run.StrategyId);

        // Load strategy
        var strategy = await readContext.GetDbContext()
            .Set<Strategy>()
            .FirstOrDefaultAsync(s => s.Id == run.StrategyId && !s.IsDeleted, ct);

        if (strategy == null)
        {
            run.Status       = RunStatus.Failed;
            run.ErrorMessage = $"Strategy {run.StrategyId} not found.";
            run.CompletedAt  = DateTime.UtcNow;
            await writeContext.SaveChangesAsync(ct);
            return;
        }

        // Mark as Running
        run.Status = RunStatus.Running;
        await writeContext.SaveChangesAsync(ct);

        try
        {
            // Load all candles in the full date window
            var allCandles = await readContext.GetDbContext()
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

            if (allCandles.Count == 0)
                throw new InvalidOperationException(
                    $"No closed candles found for {run.Symbol}/{run.Timeframe} between {run.FromDate:yyyy-MM-dd} and {run.ToDate:yyyy-MM-dd}.");

            int windowSize = run.InSampleDays + run.OutOfSampleDays;
            var windowResults = new List<WindowResult>();

            int windowIndex = 0;
            int offset = 0;

            while (offset + windowSize <= allCandles.Count)
            {
                int inSampleStart  = offset;
                int inSampleEnd    = offset + run.InSampleDays;
                int oosStart       = inSampleEnd;
                int oosEnd         = oosStart + run.OutOfSampleDays;

                if (oosEnd > allCandles.Count) break;

                var inSampleCandles = allCandles
                    .Skip(inSampleStart)
                    .Take(run.InSampleDays)
                    .ToList()
                    .AsReadOnly();

                var oosCandles = allCandles
                    .Skip(oosStart)
                    .Take(run.OutOfSampleDays)
                    .ToList()
                    .AsReadOnly();

                // Run backtest on in-sample window to "train" — then evaluate on OOS
                var oosResult = await _backtestEngine.RunAsync(strategy, oosCandles, run.InitialBalance, ct);

                var windowResult = new WindowResult
                {
                    WindowIndex         = windowIndex,
                    InSampleFrom        = allCandles[inSampleStart].Timestamp,
                    InSampleTo          = allCandles[inSampleEnd - 1].Timestamp,
                    OutOfSampleFrom     = allCandles[oosStart].Timestamp,
                    OutOfSampleTo       = allCandles[oosEnd - 1].Timestamp,
                    OosHealthScore      = (double)oosResult.SharpeRatio,
                    OosTotalTrades      = oosResult.TotalTrades,
                    OosWinRate          = (double)oosResult.WinRate,
                    OosProfitFactor     = (double)oosResult.ProfitFactor
                };

                windowResults.Add(windowResult);

                _logger.LogInformation(
                    "WalkForwardWorker: run {RunId} window {Window} OOS SharpeRatio={Sharpe:F4}",
                    run.Id, windowIndex, oosResult.SharpeRatio);

                offset      += run.OutOfSampleDays;
                windowIndex++;
            }

            if (windowResults.Count == 0)
                throw new InvalidOperationException("Not enough candle data to form any walk-forward windows.");

            // Compute statistics across all windows
            var scores = windowResults.Select(w => w.OosHealthScore).ToList();
            double avg    = scores.Average();
            double mean   = avg;
            double sumSq  = scores.Sum(s => Math.Pow(s - mean, 2));
            double stdDev = scores.Count > 1 ? Math.Sqrt(sumSq / scores.Count) : 0.0;

            run.AverageOutOfSampleScore = (decimal)avg;
            run.ScoreConsistency        = (decimal)stdDev;
            run.WindowResultsJson       = JsonSerializer.Serialize(windowResults);
            run.Status                  = RunStatus.Completed;
            run.CompletedAt             = DateTime.UtcNow;

            _logger.LogInformation(
                "WalkForwardWorker: run {RunId} completed — Windows={Count}, AvgOOS={Avg:F4}, StdDev={Std:F4}",
                run.Id, windowResults.Count, avg, stdDev);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WalkForwardWorker: run {RunId} failed", run.Id);
            run.Status       = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            run.CompletedAt  = DateTime.UtcNow;
        }

        await writeContext.SaveChangesAsync(ct);
    }

    // ── Window result record ───────────────────────────────────────────────────

    private sealed record WindowResult
    {
        public int      WindowIndex         { get; init; }
        public DateTime InSampleFrom        { get; init; }
        public DateTime InSampleTo          { get; init; }
        public DateTime OutOfSampleFrom     { get; init; }
        public DateTime OutOfSampleTo       { get; init; }
        public double   OosHealthScore      { get; init; }
        public int      OosTotalTrades      { get; init; }
        public double   OosWinRate          { get; init; }
        public double   OosProfitFactor     { get; init; }
    }
}
