using LascodiaTradingEngine.Application.Backtesting.Models;

namespace LascodiaTradingEngine.Application.Services;

public interface IPortfolioEquityCurveProvider
{
    Task<IReadOnlyList<(DateTime Date, decimal Equity)>> GetPortfolioEquityCurveAsync(
        int lookbackDays, CancellationToken ct);

    decimal ComputePortfolioSharpe(IReadOnlyList<(DateTime Date, decimal Equity)> curve);

    decimal ComputeMarginalSharpe(
        IReadOnlyList<(DateTime Date, decimal Equity)> portfolioCurve,
        IReadOnlyList<BacktestTrade> candidateTrades,
        decimal initialBalance,
        int activeStrategyCount = 10);
}
