using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting;

public interface IBacktestAutoScheduler
{
    Task<int> ScheduleAsync(
        DbContext writeDb,
        BacktestWorkerSettings settings,
        CancellationToken ct);
}

internal sealed class BacktestAutoScheduler : IBacktestAutoScheduler
{
    private readonly IValidationRunFactory _validationRunFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BacktestAutoScheduler> _logger;

    public BacktestAutoScheduler(
        IValidationRunFactory validationRunFactory,
        TimeProvider timeProvider,
        ILogger<BacktestAutoScheduler> logger)
    {
        _validationRunFactory = validationRunFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> ScheduleAsync(
        DbContext writeDb,
        BacktestWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var windowStart = nowUtc.AddDays(-settings.WindowDays);
        var cooldownThreshold = nowUtc.AddDays(-settings.CooldownDays);
        int candidateBatchSize = Math.Max(settings.MaxQueuedPerCycle, settings.MaxQueuedPerCycle * 8);

        var candidateStrategies = await writeDb.Set<Strategy>()
            .Where(strategy => strategy.Status == StrategyStatus.Active && !strategy.IsDeleted)
            .Where(strategy => !writeDb.Set<BacktestRun>().Any(run =>
                !run.IsDeleted &&
                run.StrategyId == strategy.Id &&
                (run.Status == RunStatus.Queued || run.Status == RunStatus.Running)))
            .Where(strategy => !writeDb.Set<BacktestRun>().Any(run =>
                !run.IsDeleted &&
                run.StrategyId == strategy.Id &&
                run.Status == RunStatus.Completed &&
                run.CompletedAt != null &&
                run.CompletedAt >= cooldownThreshold))
            .Select(strategy => new AutoRefreshCandidate(
                strategy.Id,
                strategy.Symbol,
                strategy.Timeframe,
                strategy.Name,
                strategy.ParametersJson,
                writeDb.Set<BacktestRun>()
                    .Where(run => !run.IsDeleted
                               && run.StrategyId == strategy.Id
                               && run.Status == RunStatus.Completed
                               && run.CompletedAt != null)
                    .Max(run => (DateTime?)run.CompletedAt)))
            .OrderBy(candidate => candidate.LastCompletedAt ?? DateTime.MinValue)
            .ThenBy(candidate => candidate.Id)
            .Take(candidateBatchSize)
            .ToListAsync(ct);

        if (candidateStrategies.Count == 0)
        {
            _logger.LogDebug(
                "BacktestAutoScheduler: no strategies need auto-backtesting (all within {Cooldown}d cooldown).",
                settings.CooldownDays);
            return 0;
        }

        var symbolTimeframeKeys = candidateStrategies
            .Select(candidate => (candidate.Symbol, candidate.Timeframe))
            .Distinct()
            .ToList();
        var symbols = symbolTimeframeKeys.Select(key => key.Symbol).Distinct().ToList();
        var timeframes = symbolTimeframeKeys.Select(key => key.Timeframe).Distinct().ToList();

        var candleCountMap = await writeDb.Set<Candle>()
            .Where(candle =>
                symbols.Contains(candle.Symbol) &&
                timeframes.Contains(candle.Timeframe) &&
                candle.Timestamp >= windowStart &&
                candle.IsClosed &&
                !candle.IsDeleted)
            .GroupBy(candle => new { candle.Symbol, candle.Timeframe })
            .Select(group => new { group.Key.Symbol, group.Key.Timeframe, Count = group.Count() })
            .ToDictionaryAsync(group => (group.Symbol, group.Timeframe), group => group.Count, ct);

        int queued = 0;
        foreach (var candidate in candidateStrategies)
        {
            if (queued >= settings.MaxQueuedPerCycle)
                break;

            ct.ThrowIfCancellationRequested();

            if (!candleCountMap.TryGetValue((candidate.Symbol, candidate.Timeframe), out int candleCount)
                || candleCount < settings.MinCandlesRequired)
            {
                _logger.LogDebug(
                    "BacktestAutoScheduler: skipping auto-backtest for strategy {Id} ({Name}) because only {Count} candles are available (need {Min}).",
                    candidate.Id,
                    candidate.Name,
                    candleCount,
                    settings.MinCandlesRequired);
                continue;
            }

            try
            {
                var run = await _validationRunFactory.BuildBacktestRunAsync(
                    writeDb,
                    new BacktestQueueRequest(
                        StrategyId: candidate.Id,
                        Symbol: candidate.Symbol,
                        Timeframe: candidate.Timeframe,
                        FromDate: windowStart,
                        ToDate: nowUtc,
                        InitialBalance: settings.InitialBalance,
                        QueueSource: ValidationRunQueueSources.AutoRefresh,
                        ParametersSnapshotJson: candidate.ParametersSnapshotJson,
                        ValidationQueueKey: $"backtest:auto-refresh:strategy:{candidate.Id}"),
                    ct);

                writeDb.Set<BacktestRun>().Add(run);
                await writeDb.SaveChangesAsync(ct);
                queued++;

                _logger.LogInformation(
                    "BacktestAutoScheduler: auto-queued backtest for strategy {Id} ({Name}) {Symbol}/{Tf} over {From:yyyy-MM-dd}..{To:yyyy-MM-dd} ({Candles} candles).",
                    candidate.Id,
                    candidate.Name,
                    candidate.Symbol,
                    candidate.Timeframe,
                    windowStart,
                    nowUtc,
                    candleCount);
            }
            catch (DbUpdateException ex) when (IsActiveValidationQueueViolation(ex, "IX_BacktestRun_ActiveValidationQueueKey"))
            {
                _logger.LogDebug(
                    ex,
                    "BacktestAutoScheduler: active auto-refresh backtest already exists for strategy {StrategyId}; skipping duplicate enqueue",
                    candidate.Id);
            }
        }

        if (queued > 0)
        {
            _logger.LogInformation(
                "BacktestAutoScheduler: auto-scheduled {Count} backtest run(s) for stale strategies (cooldown={Cooldown}d, window={Window}d, inspected={Inspected}).",
                queued,
                settings.CooldownDays,
                settings.WindowDays,
                candidateStrategies.Count);
        }
        else
        {
            _logger.LogDebug(
                "BacktestAutoScheduler: scanned {Count} stale strategy candidate(s); none met the scheduling requirements.",
                candidateStrategies.Count);
        }

        return queued;
    }

    private static bool IsActiveValidationQueueViolation(DbUpdateException ex, string indexName)
    {
        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains(indexName, StringComparison.OrdinalIgnoreCase)
               || (message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                   && message.Contains("ValidationQueueKey", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record AutoRefreshCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        string Name,
        string? ParametersSnapshotJson,
        DateTime? LastCompletedAt);
}
