using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting.Services;

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IBacktestEngine
{
    Task<BacktestResult> RunAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        decimal initialBalance,
        CancellationToken ct);
}

// ── Implementation ────────────────────────────────────────────────────────────

[RegisterService(ServiceLifetime.Singleton)]
public class BacktestEngine : IBacktestEngine
{
    private readonly IEnumerable<IStrategyEvaluator> _evaluators;

    public BacktestEngine(IEnumerable<IStrategyEvaluator> evaluators)
    {
        _evaluators = evaluators;
    }

    public async Task<BacktestResult> RunAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        decimal initialBalance,
        CancellationToken ct)
    {
        var evaluator = _evaluators.FirstOrDefault(e => e.StrategyType == strategy.StrategyType);

        if (evaluator == null)
            throw new InvalidOperationException(
                $"No IStrategyEvaluator registered for StrategyType '{strategy.StrategyType}'.");

        var trades     = new List<BacktestTrade>();
        var equityCurve = new List<decimal> { initialBalance };
        decimal balance = initialBalance;

        // State for the open position
        bool          inTrade    = false;
        TradeDirection direction  = TradeDirection.Buy;
        decimal       entryPrice = 0m;
        decimal lotSize    = 0.01m;
        decimal? stopLoss  = null;
        decimal? takeProfit = null;
        DateTime entryTime = DateTime.MinValue;

        // Walk bar-by-bar, starting from index 1 so we can "execute on next open"
        for (int i = 0; i < candles.Count; i++)
        {
            var bar = candles[i];
            ct.ThrowIfCancellationRequested();

            // ── Check SL/TP on the current bar if in trade ─────────────────
            if (inTrade)
            {
                string exitReason = string.Empty;
                decimal exitPrice = 0m;

                if (direction == TradeDirection.Buy)
                {
                    if (stopLoss.HasValue && bar.Low <= stopLoss.Value)
                    {
                        exitPrice  = stopLoss.Value;
                        exitReason = "StopLoss";
                    }
                    else if (takeProfit.HasValue && bar.High >= takeProfit.Value)
                    {
                        exitPrice  = takeProfit.Value;
                        exitReason = "TakeProfit";
                    }
                }
                else // Sell
                {
                    if (stopLoss.HasValue && bar.High >= stopLoss.Value)
                    {
                        exitPrice  = stopLoss.Value;
                        exitReason = "StopLoss";
                    }
                    else if (takeProfit.HasValue && bar.Low <= takeProfit.Value)
                    {
                        exitPrice  = takeProfit.Value;
                        exitReason = "TakeProfit";
                    }
                }

                if (!string.IsNullOrEmpty(exitReason))
                {
                    decimal pnl = CalculatePnL(direction, entryPrice, exitPrice, lotSize);
                    balance += pnl;
                    equityCurve.Add(balance);

                    trades.Add(new BacktestTrade
                    {
                        Direction  = direction,
                        EntryPrice = entryPrice,
                        ExitPrice  = exitPrice,
                        LotSize    = lotSize,
                        PnL        = pnl,
                        EntryTime  = entryTime,
                        ExitTime   = bar.Timestamp,
                        ExitReason = exitReason
                    });

                    inTrade = false;
                }
            }

            // ── Evaluate signal using bars up to (and including) current ───
            if (!inTrade && i < candles.Count - 1)
            {
                var window = candles.Take(i + 1).ToList().AsReadOnly();
                var currentPrice = (Bid: bar.Close, Ask: bar.Close);

                var signal = await evaluator.EvaluateAsync(strategy, window, currentPrice, ct);

                if (signal != null)
                {
                    // Execute on next bar's open price
                    var nextBar = candles[i + 1];
                    inTrade    = true;
                    direction  = signal.Direction;
                    entryPrice = nextBar.Open;
                    lotSize    = signal.SuggestedLotSize > 0 ? signal.SuggestedLotSize : 0.01m;
                    stopLoss   = signal.StopLoss;
                    takeProfit = signal.TakeProfit;
                    entryTime  = nextBar.Timestamp;
                }
            }
        }

        // ── Close any open trade at end of data ────────────────────────────
        if (inTrade && candles.Count > 0)
        {
            var lastBar   = candles[candles.Count - 1];
            decimal exitPrice = lastBar.Close;
            decimal pnl   = CalculatePnL(direction, entryPrice, exitPrice, lotSize);
            balance      += pnl;
            equityCurve.Add(balance);

            trades.Add(new BacktestTrade
            {
                Direction  = direction,
                EntryPrice = entryPrice,
                ExitPrice  = exitPrice,
                LotSize    = lotSize,
                PnL        = pnl,
                EntryTime  = entryTime,
                ExitTime   = lastBar.Timestamp,
                ExitReason = "EndOfData"
            });
        }

        // ── Compute statistics ─────────────────────────────────────────────
        int totalTrades    = trades.Count;
        int winningTrades  = trades.Count(t => t.PnL > 0);
        int losingTrades   = trades.Count(t => t.PnL <= 0);
        decimal winRate    = totalTrades > 0 ? (decimal)winningTrades / totalTrades : 0m;

        decimal grossProfit = trades.Where(t => t.PnL > 0).Sum(t => t.PnL);
        decimal grossLoss   = Math.Abs(trades.Where(t => t.PnL < 0).Sum(t => t.PnL));
        decimal profitFactor = grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? decimal.MaxValue : 0m;

        decimal totalReturn = initialBalance > 0
            ? (balance - initialBalance) / initialBalance * 100m
            : 0m;

        decimal maxDrawdownPct = CalculateMaxDrawdownPct(equityCurve);
        decimal sharpeRatio    = CalculateSharpeRatio(trades.Select(t => t.PnL).ToList());

        return new BacktestResult
        {
            InitialBalance = initialBalance,
            FinalBalance   = balance,
            TotalReturn    = Math.Round(totalReturn, 4),
            TotalTrades    = totalTrades,
            WinningTrades  = winningTrades,
            LosingTrades   = losingTrades,
            WinRate        = Math.Round(winRate, 4),
            ProfitFactor   = Math.Round(profitFactor, 4),
            MaxDrawdownPct = Math.Round(maxDrawdownPct, 4),
            SharpeRatio    = Math.Round(sharpeRatio, 4),
            Trades         = trades
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static decimal CalculatePnL(TradeDirection direction, decimal entry, decimal exit, decimal lots)
    {
        // Simplified PnL: pip-based approximation using price difference × lots × 100000
        decimal priceDiff = direction == TradeDirection.Buy ? exit - entry : entry - exit;
        return priceDiff * lots * 100_000m;
    }

    private static decimal CalculateMaxDrawdownPct(List<decimal> equityCurve)
    {
        if (equityCurve.Count < 2) return 0m;

        decimal maxDrawdown = 0m;
        decimal peak        = equityCurve[0];

        foreach (var equity in equityCurve)
        {
            if (equity > peak) peak = equity;
            if (peak > 0)
            {
                decimal drawdown = (peak - equity) / peak * 100m;
                if (drawdown > maxDrawdown) maxDrawdown = drawdown;
            }
        }

        return maxDrawdown;
    }

    private static decimal CalculateSharpeRatio(List<decimal> pnls)
    {
        if (pnls.Count < 2) return 0m;

        double mean   = (double)pnls.Average();
        double sumSq  = pnls.Sum(p => Math.Pow((double)p - mean, 2));
        double stdDev = Math.Sqrt(sumSq / pnls.Count);

        if (stdDev == 0) return 0m;

        // Annualise assuming each trade is ~1 day; risk-free rate = 0
        double annualisedReturn = mean * 252;
        double annualisedStdDev = stdDev * Math.Sqrt(252);

        return (decimal)(annualisedReturn / annualisedStdDev);
    }
}
