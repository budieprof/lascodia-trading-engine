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
        CancellationToken ct,
        BacktestOptions? options = null);
}

/// <summary>
/// Optional parameters for backtest simulation realism.
/// </summary>
public class BacktestOptions
{
    /// <summary>Simulated slippage in price units applied to entries and exits. Defaults to 0.</summary>
    public decimal SlippagePriceUnits { get; set; }

    /// <summary>Commission per lot per round-trip (entry + exit) in account currency. Defaults to 0.</summary>
    public decimal CommissionPerLot { get; set; }

    /// <summary>Swap (rollover) cost per lot per day held in account currency. Defaults to 0.</summary>
    public decimal SwapPerLotPerDay { get; set; }

    /// <summary>Contract size for PnL calculation. Defaults to 100,000 (standard FX lot).</summary>
    public decimal ContractSize { get; set; } = 100_000m;

    /// <summary>
    /// Simulated bid-ask spread in price units. Applied to entry (buy at ask = mid + half spread)
    /// and exit prices. Defaults to 0.
    /// </summary>
    public decimal SpreadPriceUnits { get; set; }

    /// <summary>
    /// Percentage of the gap to apply as additional slippage when a bar opens beyond
    /// SL/TP levels (gap scenario). For example, 0.5 means 50% of the gap is applied.
    /// Defaults to 0 (SL/TP fill at exact level even through gaps).
    /// </summary>
    public decimal GapSlippagePct { get; set; }

    /// <summary>
    /// Fraction of the requested lot size that actually gets filled (0.0–1.0).
    /// Simulates partial fills where only a portion of the order executes.
    /// For example, 0.8 means 80% of each order is filled; the remaining 20% is cancelled.
    /// Defaults to 1.0 (full fill). Set to 0 to reject all orders (no fills).
    /// </summary>
    public decimal FillRatio { get; set; } = 1.0m;
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
        CancellationToken ct,
        BacktestOptions? options = null)
    {
        options ??= new BacktestOptions();

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

                        // Gap simulation: if bar opens beyond SL (gap down), fill at gap-adjusted price
                        if (options.GapSlippagePct > 0 && bar.Open < stopLoss.Value)
                        {
                            decimal gapSize = stopLoss.Value - bar.Open;
                            exitPrice = stopLoss.Value - gapSize * options.GapSlippagePct;
                        }
                    }
                    else if (takeProfit.HasValue && bar.High >= takeProfit.Value)
                    {
                        exitPrice  = takeProfit.Value;
                        exitReason = "TakeProfit";

                        // Gap simulation: if bar opens beyond TP (gap up), fill at gap-adjusted price (favorable)
                        if (options.GapSlippagePct > 0 && bar.Open > takeProfit.Value)
                        {
                            decimal gapSize = bar.Open - takeProfit.Value;
                            exitPrice = takeProfit.Value + gapSize * options.GapSlippagePct;
                        }
                    }
                }
                else // Sell
                {
                    if (stopLoss.HasValue && bar.High >= stopLoss.Value)
                    {
                        exitPrice  = stopLoss.Value;
                        exitReason = "StopLoss";

                        // Gap simulation: if bar opens beyond SL (gap up), fill at gap-adjusted price (unfavorable)
                        if (options.GapSlippagePct > 0 && bar.Open > stopLoss.Value)
                        {
                            decimal gapSize = bar.Open - stopLoss.Value;
                            exitPrice = stopLoss.Value + gapSize * options.GapSlippagePct;
                        }
                    }
                    else if (takeProfit.HasValue && bar.Low <= takeProfit.Value)
                    {
                        exitPrice  = takeProfit.Value;
                        exitReason = "TakeProfit";

                        // Gap simulation: if bar opens beyond TP (gap down), fill at gap-adjusted price (favorable)
                        if (options.GapSlippagePct > 0 && bar.Open < takeProfit.Value)
                        {
                            decimal gapSize = takeProfit.Value - bar.Open;
                            exitPrice = takeProfit.Value - gapSize * options.GapSlippagePct;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(exitReason))
                {
                    // Apply slippage against the trader on exit
                    decimal slippedExit = direction == TradeDirection.Buy
                        ? exitPrice - options.SlippagePriceUnits  // Long exit slips down
                        : exitPrice + options.SlippagePriceUnits; // Short exit slips up

                    decimal tradePnl = CalculatePnL(direction, entryPrice, slippedExit, lotSize, options.ContractSize);

                    // Apply commission (round-trip per lot)
                    decimal commission = lotSize * options.CommissionPerLot;

                    // Apply swap for days held (pro-rate for same-day trades)
                    decimal daysHeldExact = (decimal)(bar.Timestamp - entryTime).TotalDays;
                    decimal swap = lotSize * options.SwapPerLotPerDay * Math.Max(daysHeldExact, 0m);

                    decimal pnl = tradePnl - commission - swap;
                    balance += pnl;
                    equityCurve.Add(balance);

                    trades.Add(new BacktestTrade
                    {
                        Direction  = direction,
                        EntryPrice = entryPrice,
                        ExitPrice  = slippedExit,
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
                decimal halfSpread = options.SpreadPriceUnits / 2m;
                var currentPrice = (Bid: bar.Close - halfSpread, Ask: bar.Close + halfSpread);

                var signal = await evaluator.EvaluateAsync(strategy, window, currentPrice, ct);

                if (signal != null)
                {
                    // Execute on next bar's open price with slippage against the trader
                    var nextBar = candles[i + 1];
                    inTrade    = true;
                    direction  = signal.Direction;
                    decimal rawEntry = nextBar.Open;
                    entryPrice = direction == TradeDirection.Buy
                        ? rawEntry + options.SlippagePriceUnits  // Long entry slips up
                        : rawEntry - options.SlippagePriceUnits; // Short entry slips down
                    decimal requestedLots = signal.SuggestedLotSize > 0 ? signal.SuggestedLotSize : 0.01m;
                    lotSize = requestedLots * Math.Clamp(options.FillRatio, 0m, 1m);
                    if (lotSize <= 0) { inTrade = false; continue; } // Zero fill — order rejected
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
            decimal exitPrice = direction == TradeDirection.Buy
                ? lastBar.Close - options.SlippagePriceUnits
                : lastBar.Close + options.SlippagePriceUnits;
            decimal tradePnl = CalculatePnL(direction, entryPrice, exitPrice, lotSize, options.ContractSize);
            decimal commission = lotSize * options.CommissionPerLot;
            decimal daysHeldExact = (decimal)(lastBar.Timestamp - entryTime).TotalDays;
            decimal swap = lotSize * options.SwapPerLotPerDay * Math.Max(daysHeldExact, 0m);
            decimal pnl  = tradePnl - commission - swap;
            balance     += pnl;
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

    private static decimal CalculatePnL(TradeDirection direction, decimal entry, decimal exit, decimal lots, decimal contractSize = 100_000m)
    {
        decimal priceDiff = direction == TradeDirection.Buy ? exit - entry : entry - exit;
        return priceDiff * lots * contractSize;
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
