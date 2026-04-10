using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Backtesting;

internal readonly record struct BacktestRunMetrics(
    int TotalTrades,
    decimal WinRate,
    decimal ProfitFactor,
    decimal MaxDrawdownPct,
    decimal SharpeRatio,
    decimal? FinalBalance,
    decimal? TotalReturn);

internal static class BacktestRunMetricsReader
{
    internal static bool TryRead(BacktestRun run, out BacktestRunMetrics metrics)
        => TryRead(
            run.TotalTrades,
            run.WinRate,
            run.ProfitFactor,
            run.MaxDrawdownPct,
            run.SharpeRatio,
            run.FinalBalance,
            run.TotalReturn,
            out metrics);

    internal static bool TryRead(
        int? totalTrades,
        decimal? winRate,
        decimal? profitFactor,
        decimal? maxDrawdownPct,
        decimal? sharpeRatio,
        decimal? finalBalance,
        decimal? totalReturn,
        out BacktestRunMetrics metrics)
    {
        if (totalTrades.HasValue
            && winRate.HasValue
            && profitFactor.HasValue
            && maxDrawdownPct.HasValue
            && sharpeRatio.HasValue)
        {
            metrics = new BacktestRunMetrics(
                totalTrades.Value,
                winRate.Value,
                profitFactor.Value,
                maxDrawdownPct.Value,
                sharpeRatio.Value,
                finalBalance,
                totalReturn);
            return true;
        }

        metrics = default;
        return false;
    }
}
