using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

public partial class StrategyWorker
{
    // ════════════════════════════════════════════════════════════════════════════
    //  Backtest qualification gate
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the set of strategy IDs that pass the backtest qualification gate.
    /// Strategies without a recent passing backtest are excluded from signal generation
    /// to prevent untested or underperforming evaluators from producing live signals.
    /// </summary>
    private async Task<HashSet<long>> GetBacktestQualifiedStrategyIdsAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        List<long> strategyIds,
        CancellationToken ct)
    {
        // Check if the gate is enabled
        bool gateEnabled = await GetConfigAsync<bool>(ctx, "Backtest:Gate:Enabled", true, ct);
        if (!gateEnabled)
        {
            // Gate disabled — all strategies are qualified
            return [.. strategyIds];
        }

        // Load qualification thresholds from EngineConfig (hot-reloadable)
        // Only profitable, winning strategies should qualify.
        double minWinRate      = await GetConfigAsync<double>(ctx, "Backtest:Gate:MinWinRate",      0.60, ct);
        double minProfitFactor = await GetConfigAsync<double>(ctx, "Backtest:Gate:MinProfitFactor", 1.0,  ct);
        double maxDrawdownPct  = await GetConfigAsync<double>(ctx, "Backtest:Gate:MaxDrawdownPct",  0.25, ct);
        double minSharpe       = await GetConfigAsync<double>(ctx, "Backtest:Gate:MinSharpe",       0.0,  ct);

        // Max age (in days) of a completed backtest that still qualifies as
        // recent evidence for live trading. Beyond this window the strategy
        // hasn't been re-validated against current market conditions and we
        // treat it as unqualified until a fresh run completes. Defaults to 180.
        int maxAgeDays = await GetConfigAsync<int>(ctx, "Backtest:Gate:MaxAgeDays", 180, ct);

        // Timeframe-adaptive MinTotalTrades: higher timeframes produce fewer signals,
        // so they need a lower trade threshold to avoid permanently blocking profitable
        // H4/D1 strategies that only generate 5-8 trades in a 365-day backtest window.
        int minTradesDefault = await GetConfigAsync<int>(ctx, "Backtest:Gate:MinTotalTrades", 5, ct);
        int minTradesM5M15   = await GetConfigAsync<int>(ctx, "Backtest:Gate:MinTotalTrades:M5M15", 10, ct);
        int minTradesH1      = await GetConfigAsync<int>(ctx, "Backtest:Gate:MinTotalTrades:H1",    5,  ct);
        int minTradesH4      = await GetConfigAsync<int>(ctx, "Backtest:Gate:MinTotalTrades:H4",    5,  ct);
        int minTradesD1      = await GetConfigAsync<int>(ctx, "Backtest:Gate:MinTotalTrades:D1",    3,  ct);

        // Build a lookup of strategy timeframes for adaptive trade thresholds
        var strategyTimeframes = await ctx.Set<Domain.Entities.Strategy>()
            .Where(s => strategyIds.Contains(s.Id) && !s.IsDeleted)
            .Select(s => new { s.Id, s.Timeframe })
            .ToListAsync(ct);
        var timeframeMap = strategyTimeframes.ToDictionary(s => s.Id, s => s.Timeframe);

        // Load the most recent completed backtest per strategy (only for strategies
        // in scope). The age filter narrows the grouping set to recent runs only,
        // so on a 1,000-run history the DB no longer has to sort the whole table
        // just to pick the latest. Strategies with no run in the window appear as
        // "not qualified" — the same treatment as never-backtested strategies.
        DateTime minCompletedAt = DateTime.UtcNow.AddDays(-Math.Max(1, maxAgeDays));
        var recentBacktests = await ctx.Set<Domain.Entities.BacktestRun>()
            .Where(r => strategyIds.Contains(r.StrategyId)
                        && r.Status == RunStatus.Completed
                        && r.CompletedAt != null
                        && r.CompletedAt > minCompletedAt
                        && !r.IsDeleted)
            .GroupBy(r => r.StrategyId)
            .Select(g => g.OrderByDescending(r => r.CompletedAt)
                .Select(r => new
                {
                    r.StrategyId,
                    r.TotalTrades,
                    r.WinRate,
                    r.ProfitFactor,
                    r.MaxDrawdownPct,
                    r.SharpeRatio,
                    r.FinalBalance,
                    r.TotalReturn
                })
                .First())
            .ToListAsync(ct);

        var qualifiedIds = new HashSet<long>();

        foreach (var bt in recentBacktests)
        {
            if (!global::LascodiaTradingEngine.Application.Backtesting.BacktestRunMetricsReader.TryRead(
                    bt.TotalTrades,
                    bt.WinRate,
                    bt.ProfitFactor,
                    bt.MaxDrawdownPct,
                    bt.SharpeRatio,
                    bt.FinalBalance,
                    bt.TotalReturn,
                    out var result))
            {
                LoggerExtensions.LogWarning(
                    _logger,
                    "Strategy {Id}: backtest metrics were unavailable or malformed — treating as unqualified",
                    bt.StrategyId);
                continue;
            }

            Timeframe tf;
            if (timeframeMap.TryGetValue(bt.StrategyId, out var resolvedTf))
            {
                tf = resolvedTf;
            }
            else
            {
                tf = Timeframe.H1;
                _logger.LogWarning(
                    "Strategy {StrategyId}: timeframe not found in lookup — falling back to H1 for trade threshold calculation",
                    bt.StrategyId);
            }
            int minTotalTrades = tf switch
            {
                Timeframe.M1 or Timeframe.M5 or Timeframe.M15 => minTradesM5M15,
                Timeframe.H1 => minTradesH1,
                Timeframe.H4 => minTradesH4,
                Timeframe.D1 => minTradesD1,
                _ => minTradesDefault,
            };

            bool meetsMinTrades  = result.TotalTrades >= minTotalTrades;
            bool meetsWinRate    = (double)result.WinRate >= minWinRate;
            bool meetsPF         = (double)result.ProfitFactor >= minProfitFactor;
            bool meetsDrawdown   = (double)result.MaxDrawdownPct <= maxDrawdownPct;
            bool meetsSharpe     = (double)result.SharpeRatio >= minSharpe;

            if (meetsMinTrades && meetsWinRate && meetsPF && meetsDrawdown && meetsSharpe)
            {
                qualifiedIds.Add(bt.StrategyId);
            }
            else
            {
                LoggerExtensions.LogDebug(
                    _logger,
                    "Strategy {Id} ({Tf}): backtest did not meet qualification — " +
                    "trades={Trades}/{MinTrades} winRate={WR:P1}/{MinWR:P1} " +
                    "pf={PF:F2}/{MinPF:F2} dd={DD:P1}/{MaxDD:P1} sharpe={S:F2}/{MinS:F2}",
                    bt.StrategyId, tf,
                    result.TotalTrades, minTotalTrades,
                    (double)result.WinRate, minWinRate,
                    (double)result.ProfitFactor, minProfitFactor,
                    (double)result.MaxDrawdownPct, maxDrawdownPct,
                    (double)result.SharpeRatio, minSharpe);
            }
        }

        return qualifiedIds;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Configuration helper
    // ════════════════════════════════════════════════════════════════════════════

    // TTL for the EngineConfig raw-value cache consulted by the backtest gate.
    // 300 s is the safety-net window for multi-instance deployments; in single-
    // instance the UpsertEngineConfigCommandHandler invalidates the cache
    // directly on every write so readers see new values on the very next tick.
    private const int ConfigCacheTtlSeconds = 300;

    private async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var raw = await _engineConfigCache.GetRawAsync(ctx, key, ConfigCacheTtlSeconds, ct);
        if (raw is null) return defaultValue;

        try   { return (T)Convert.ChangeType(raw, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
        catch { return defaultValue; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Lifecycle cleanup
    // ════════════════════════════════════════════════════════════════════════════

    public override void Dispose()
    {
        _expirySweepTimer?.Dispose();
        _expirySweepLock.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
