using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
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
    /// Time-varying spread function. When provided, overrides <see cref="SpreadPriceUnits"/>
    /// on a per-bar basis. Receives the bar's timestamp and returns the spread in price units.
    /// When null (default), the fixed SpreadPriceUnits is used.
    /// </summary>
    public Func<DateTime, decimal>? SpreadFunction { get; set; }

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

    /// <summary>
    /// Optional position sizing function. Receives the current account balance and the
    /// trade signal, returns the lot size to use. When null (default), the signal's
    /// <see cref="TradeSignal.SuggestedLotSize"/> is used (falling back to 0.01 if zero).
    /// </summary>
    public Func<decimal, TradeSignal, decimal>? PositionSizer { get; set; }
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
        // ── Input validation ─────────────────────────────────────────────
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(candles);

        if (initialBalance <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialBalance), "Initial balance must be positive.");

        if (candles.Count == 0)
            return BuildEmptyResult(initialBalance);

        options ??= new BacktestOptions();

        var evaluator = _evaluators.FirstOrDefault(e => e.StrategyType == strategy.StrategyType)
            ?? throw new InvalidOperationException(
                $"No IStrategyEvaluator registered for StrategyType '{strategy.StrategyType}'.");

        int minCandles = evaluator.MinRequiredCandles(strategy);

        var trades          = new List<BacktestTrade>();
        var equityCurve     = new List<decimal> { initialBalance };
        decimal balance     = initialBalance;
        int barsInTrade     = 0;   // counts bars spent in a position (for exposure %)
        decimal totalCommission = 0m;
        decimal totalSwap       = 0m;
        decimal totalSlippage   = 0m;

        // State for the open position
        bool          inTrade    = false;
        TradeDirection direction = TradeDirection.Buy;
        decimal       entryPrice = 0m;
        decimal       lotSize    = 0.01m;
        decimal?      stopLoss   = null;
        decimal?      takeProfit = null;
        DateTime      entryTime  = DateTime.MinValue;
        decimal       entrySlippage = 0m;  // slippage incurred on entry

        bool useDynamicSpread = options.SpreadFunction != null;
        decimal fixedHalfSpread = options.SpreadPriceUnits / 2m;

        // Walk bar-by-bar
        for (int i = 0; i < candles.Count; i++)
        {
            var bar = candles[i];
            ct.ThrowIfCancellationRequested();

            decimal halfSpread = useDynamicSpread
                ? Math.Max(0m, options.SpreadFunction!(candles[i].Timestamp)) / 2m
                : fixedHalfSpread;

            // ── Check SL/TP on the current bar if in trade ───────────
            if (inTrade)
            {
                barsInTrade++;

                var exit = ResolveSLTP(bar, direction, stopLoss, takeProfit, options);

                if (exit.HasValue)
                {
                    var (exitPrice, exitReason) = exit.Value;

                    // Apply slippage + spread against the trader on exit
                    decimal exitSlip = options.SlippagePriceUnits + halfSpread;
                    decimal slippedExit = direction == TradeDirection.Buy
                        ? exitPrice - exitSlip   // Long exit: bid side, slip down
                        : exitPrice + exitSlip;  // Short exit: ask side, slip up

                    decimal grossPnl = CalculatePnL(direction, entryPrice, slippedExit, lotSize, options.ContractSize);
                    decimal commission = lotSize * options.CommissionPerLot;
                    decimal daysHeldExact = (decimal)(bar.Timestamp - entryTime).TotalDays;
                    decimal swap = lotSize * options.SwapPerLotPerDay * Math.Max(daysHeldExact, 0m);

                    decimal slippageCost = (entrySlippage + exitSlip) * lotSize * options.ContractSize;
                    decimal pnl = grossPnl - commission - swap;
                    balance += pnl;

                    totalCommission += commission;
                    totalSwap       += swap;
                    totalSlippage   += slippageCost;

                    trades.Add(new BacktestTrade
                    {
                        Direction  = direction,
                        EntryPrice = entryPrice,
                        ExitPrice  = slippedExit,
                        LotSize    = lotSize,
                        GrossPnL   = grossPnl,
                        PnL        = pnl,
                        Commission = commission,
                        Swap       = swap,
                        Slippage   = slippageCost,
                        EntryTime  = entryTime,
                        ExitTime   = bar.Timestamp,
                        ExitReason = exitReason
                    });

                    inTrade = false;
                }

                // Mark-to-market equity: use bar close to track unrealised PnL
                if (inTrade)
                {
                    decimal mtmExit = direction == TradeDirection.Buy
                        ? bar.Close - halfSpread
                        : bar.Close + halfSpread;
                    decimal unrealised = CalculatePnL(direction, entryPrice, mtmExit, lotSize, options.ContractSize);
                    equityCurve.Add(balance + unrealised);
                }
                else
                {
                    equityCurve.Add(balance);
                }
            }
            else
            {
                // Flat — equity equals cash balance
                equityCurve.Add(balance);
            }

            // ── Evaluate signal using bars up to (and including) current ──
            if (!inTrade && i < candles.Count - 1 && (i + 1) >= minCandles)
            {
                // Zero-copy slice: presents candles[0..i] without allocating a new list
                var window = new ReadOnlyListSlice<Candle>(candles, i + 1);
                var currentPrice = (Bid: bar.Close - halfSpread, Ask: bar.Close + halfSpread);

                var signal = await evaluator.EvaluateAsync(strategy, window, currentPrice, ct);

                if (signal != null)
                {
                    // Execute on next bar's open price with slippage + spread against the trader
                    var nextBar = candles[i + 1];
                    decimal rawEntry = nextBar.Open;
                    decimal slipAndSpread = options.SlippagePriceUnits + halfSpread;

                    // Must set direction before computing fill price
                    var signalDirection = signal.Direction;
                    decimal fillPrice = signalDirection == TradeDirection.Buy
                        ? rawEntry + slipAndSpread   // Long entry: ask side, slip up
                        : rawEntry - slipAndSpread;  // Short entry: bid side, slip down

                    // Position sizing: use custom sizer if provided, else signal's lot size
                    decimal requestedLots = options.PositionSizer != null
                        ? options.PositionSizer(balance, signal)
                        : signal.SuggestedLotSize > 0 ? signal.SuggestedLotSize : 0.01m;

                    decimal filledLots = requestedLots * Math.Clamp(options.FillRatio, 0m, 1m);
                    if (filledLots <= 0) continue; // Zero fill — order rejected

                    inTrade       = true;
                    direction     = signalDirection;
                    entryPrice    = fillPrice;
                    lotSize       = filledLots;
                    stopLoss      = signal.StopLoss;
                    takeProfit    = signal.TakeProfit;
                    entryTime     = nextBar.Timestamp;
                    entrySlippage = slipAndSpread;
                }
            }
        }

        // ── Close any open trade at end of data ──────────────────────────
        if (inTrade && candles.Count > 0)
        {
            var lastBar = candles[candles.Count - 1];
            decimal lastHalfSpread = useDynamicSpread
                ? Math.Max(0m, options.SpreadFunction!(lastBar.Timestamp)) / 2m
                : fixedHalfSpread;
            decimal exitSlip = options.SlippagePriceUnits + lastHalfSpread;
            decimal exitPrice = direction == TradeDirection.Buy
                ? lastBar.Close - exitSlip
                : lastBar.Close + exitSlip;

            decimal grossPnl = CalculatePnL(direction, entryPrice, exitPrice, lotSize, options.ContractSize);
            decimal commission = lotSize * options.CommissionPerLot;
            decimal daysHeldExact = (decimal)(lastBar.Timestamp - entryTime).TotalDays;
            decimal swap = lotSize * options.SwapPerLotPerDay * Math.Max(daysHeldExact, 0m);
            decimal slippageCost = (entrySlippage + exitSlip) * lotSize * options.ContractSize;
            decimal pnl = grossPnl - commission - swap;
            balance += pnl;

            totalCommission += commission;
            totalSwap       += swap;
            totalSlippage   += slippageCost;

            // Update final equity point (replace the last M2M point with realised)
            equityCurve[^1] = balance;

            trades.Add(new BacktestTrade
            {
                Direction  = direction,
                EntryPrice = entryPrice,
                ExitPrice  = exitPrice,
                LotSize    = lotSize,
                GrossPnL   = grossPnl,
                PnL        = pnl,
                Commission = commission,
                Swap       = swap,
                Slippage   = slippageCost,
                EntryTime  = entryTime,
                ExitTime   = lastBar.Timestamp,
                ExitReason = TradeExitReason.EndOfData
            });
        }

        // ── Compute statistics ───────────────────────────────────────────
        return ComputeResult(trades, equityCurve, initialBalance, balance, candles.Count,
                             barsInTrade, totalCommission, totalSwap, totalSlippage);
    }

    // ── SL/TP Resolution ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves which exit (SL or TP) fires on a given bar, handling:
    /// 1. Gap opens beyond SL/TP (filled at gap-adjusted price)
    /// 2. Both SL and TP hit intra-bar (resolved by proximity to bar open — closer level fires first;
    ///    if equidistant, pessimistic assumption: SL wins)
    /// </summary>
    private static (decimal Price, TradeExitReason Reason)? ResolveSLTP(
        Candle bar, TradeDirection direction, decimal? stopLoss, decimal? takeProfit, BacktestOptions options)
    {
        bool slHit, tpHit;
        decimal slPrice = 0m, tpPrice = 0m;

        if (direction == TradeDirection.Buy)
        {
            slHit = stopLoss.HasValue && bar.Low <= stopLoss.Value;
            tpHit = takeProfit.HasValue && bar.High >= takeProfit.Value;

            if (slHit) slPrice = GapAdjustedExit(stopLoss!.Value, bar.Open, direction, isStopLoss: true, options);
            if (tpHit) tpPrice = GapAdjustedExit(takeProfit!.Value, bar.Open, direction, isStopLoss: false, options);
        }
        else
        {
            slHit = stopLoss.HasValue && bar.High >= stopLoss.Value;
            tpHit = takeProfit.HasValue && bar.Low <= takeProfit.Value;

            if (slHit) slPrice = GapAdjustedExit(stopLoss!.Value, bar.Open, direction, isStopLoss: true, options);
            if (tpHit) tpPrice = GapAdjustedExit(takeProfit!.Value, bar.Open, direction, isStopLoss: false, options);
        }

        if (slHit && tpHit)
        {
            // Both levels hit on same bar — resolve by proximity to open
            decimal slDist = Math.Abs(bar.Open - slPrice);
            decimal tpDist = Math.Abs(bar.Open - tpPrice);
            // Pessimistic tie-break: if equidistant, SL fires first
            return slDist <= tpDist
                ? (slPrice, TradeExitReason.StopLoss)
                : (tpPrice, TradeExitReason.TakeProfit);
        }

        if (slHit) return (slPrice, TradeExitReason.StopLoss);
        if (tpHit) return (tpPrice, TradeExitReason.TakeProfit);
        return null;
    }

    private static decimal GapAdjustedExit(
        decimal level, decimal barOpen, TradeDirection direction, bool isStopLoss, BacktestOptions options)
    {
        if (options.GapSlippagePct <= 0)
            return level;

        if (direction == TradeDirection.Buy)
        {
            if (isStopLoss && barOpen < level)
            {
                // Gap down through SL — unfavourable
                decimal gap = level - barOpen;
                return level - gap * options.GapSlippagePct;
            }
            if (!isStopLoss && barOpen > level)
            {
                // Gap up through TP — favourable
                decimal gap = barOpen - level;
                return level + gap * options.GapSlippagePct;
            }
        }
        else
        {
            if (isStopLoss && barOpen > level)
            {
                // Gap up through SL — unfavourable
                decimal gap = barOpen - level;
                return level + gap * options.GapSlippagePct;
            }
            if (!isStopLoss && barOpen < level)
            {
                // Gap down through TP — favourable
                decimal gap = level - barOpen;
                return level - gap * options.GapSlippagePct;
            }
        }

        return level;
    }

    // ── Statistics ───────────────────────────────────────────────────────────

    private static BacktestResult ComputeResult(
        List<BacktestTrade> trades, List<decimal> equityCurve,
        decimal initialBalance, decimal finalBalance,
        int totalBars, int barsInTrade,
        decimal totalCommission, decimal totalSwap, decimal totalSlippage)
    {
        int totalTrades   = trades.Count;
        int winningTrades = trades.Count(t => t.PnL > 0);
        int losingTrades  = trades.Count(t => t.PnL <= 0);
        decimal winRate   = totalTrades > 0 ? (decimal)winningTrades / totalTrades : 0m;

        var wins   = trades.Where(t => t.PnL > 0).Select(t => t.PnL).ToList();
        var losses = trades.Where(t => t.PnL < 0).Select(t => t.PnL).ToList();

        decimal grossProfit = wins.Sum();
        decimal grossLoss   = Math.Abs(losses.Sum());
        decimal profitFactor = grossLoss > 0
            ? grossProfit / grossLoss
            : grossProfit > 0 ? decimal.MaxValue : 0m;

        decimal totalReturn = initialBalance > 0
            ? (finalBalance - initialBalance) / initialBalance * 100m
            : 0m;

        decimal avgWin  = wins.Count > 0 ? wins.Average() : 0m;
        decimal avgLoss = losses.Count > 0 ? Math.Abs(losses.Average()) : 0m;
        decimal largestWin  = wins.Count > 0 ? wins.Max() : 0m;
        decimal largestLoss = losses.Count > 0 ? Math.Abs(losses.Min()) : 0m;

        // Expectancy = (WinRate × AvgWin) - (LossRate × AvgLoss)
        decimal lossRate = totalTrades > 0 ? (decimal)losses.Count / totalTrades : 0m;
        decimal expectancy = (winRate * avgWin) - (lossRate * avgLoss);

        // Consecutive wins/losses
        var (maxConsWins, maxConsLosses) = CalculateStreaks(trades);

        // Exposure
        decimal exposurePct = totalBars > 0 ? (decimal)barsInTrade / totalBars * 100m : 0m;

        // Average trade duration
        double avgDurationHours = totalTrades > 0
            ? trades.Average(t => (t.ExitTime - t.EntryTime).TotalHours)
            : 0;

        // Drawdown (from mark-to-market equity curve)
        decimal maxDrawdownPct = CalculateMaxDrawdownPct(equityCurve);
        decimal maxDrawdownAbs = CalculateMaxDrawdownAbs(equityCurve);

        // Risk-adjusted returns — computed from equity curve bar returns (time-series)
        var barReturns = ComputeBarReturns(equityCurve);
        decimal sharpeRatio  = CalculateSharpeRatio(barReturns);
        decimal sortinoRatio = CalculateSortinoRatio(barReturns);
        decimal netProfit    = finalBalance - initialBalance;
        decimal calmarRatio  = maxDrawdownAbs > 0 ? netProfit / maxDrawdownAbs : (netProfit > 0 ? decimal.MaxValue : 0m);
        decimal recoveryFactor = maxDrawdownAbs > 0 ? netProfit / maxDrawdownAbs : 0m;

        return new BacktestResult
        {
            InitialBalance  = initialBalance,
            FinalBalance    = finalBalance,
            TotalReturn     = Math.Round(totalReturn, 4),
            TotalTrades     = totalTrades,
            WinningTrades   = winningTrades,
            LosingTrades    = losingTrades,
            WinRate         = Math.Round(winRate, 4),
            ProfitFactor    = Math.Round(profitFactor, 4),
            MaxDrawdownPct  = Math.Round(maxDrawdownPct, 4),
            SharpeRatio     = Math.Round(sharpeRatio, 4),
            SortinoRatio    = Math.Round(sortinoRatio, 4),
            CalmarRatio     = Math.Round(calmarRatio, 4),
            AverageWin      = Math.Round(avgWin, 2),
            AverageLoss     = Math.Round(avgLoss, 2),
            LargestWin      = Math.Round(largestWin, 2),
            LargestLoss     = Math.Round(largestLoss, 2),
            Expectancy      = Math.Round(expectancy, 2),
            MaxConsecutiveWins   = maxConsWins,
            MaxConsecutiveLosses = maxConsLosses,
            ExposurePct          = Math.Round(exposurePct, 2),
            AverageTradeDurationHours = Math.Round(avgDurationHours, 2),
            TotalCommission = Math.Round(totalCommission, 2),
            TotalSwap       = Math.Round(totalSwap, 2),
            TotalSlippage   = Math.Round(totalSlippage, 2),
            RecoveryFactor  = Math.Round(recoveryFactor, 4),
            Trades          = trades
        };
    }

    private static BacktestResult BuildEmptyResult(decimal initialBalance) => new()
    {
        InitialBalance = initialBalance,
        FinalBalance   = initialBalance,
        TotalReturn    = 0m,
        TotalTrades    = 0,
        WinningTrades  = 0,
        LosingTrades   = 0,
        WinRate        = 0m,
        ProfitFactor   = 0m,
        MaxDrawdownPct = 0m,
        SharpeRatio    = 0m,
        SortinoRatio   = 0m,
        CalmarRatio    = 0m,
        Trades         = []
    };

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static decimal CalculatePnL(
        TradeDirection direction, decimal entry, decimal exit, decimal lots, decimal contractSize = 100_000m)
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

    private static decimal CalculateMaxDrawdownAbs(List<decimal> equityCurve)
    {
        if (equityCurve.Count < 2) return 0m;

        decimal maxDrawdown = 0m;
        decimal peak        = equityCurve[0];

        foreach (var equity in equityCurve)
        {
            if (equity > peak) peak = equity;
            decimal drawdown = peak - equity;
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;
        }

        return maxDrawdown;
    }

    /// <summary>
    /// Computes per-bar percentage returns from the equity curve.
    /// This produces a time-series of returns at uniform intervals (one per bar),
    /// which is the correct input for Sharpe/Sortino annualisation regardless of
    /// how frequently trades occur.
    /// </summary>
    private static List<double> ComputeBarReturns(List<decimal> equityCurve)
    {
        var returns = new List<double>(equityCurve.Count - 1);
        for (int i = 1; i < equityCurve.Count; i++)
        {
            decimal prev = equityCurve[i - 1];
            if (prev != 0)
                returns.Add((double)(equityCurve[i] - prev) / (double)prev);
            else
                returns.Add(0);
        }
        return returns;
    }

    /// <summary>
    /// Sharpe ratio from time-series bar returns.
    /// Annualised assuming 252 trading bars per year (correct for daily bars;
    /// for intraday bars the caller should interpret accordingly).
    /// Uses sample standard deviation (Bessel's correction: N-1).
    /// </summary>
    private static decimal CalculateSharpeRatio(List<double> returns)
    {
        if (returns.Count < 2) return 0m;

        double mean  = returns.Average();
        double sumSq = returns.Sum(r => Math.Pow(r - mean, 2));
        double stdDev = Math.Sqrt(sumSq / (returns.Count - 1));

        if (stdDev < 1e-12) return 0m;

        // Annualise: Sharpe = (mean / stdDev) * sqrt(N)
        return (decimal)(mean / stdDev * Math.Sqrt(252));
    }

    /// <summary>
    /// Sortino ratio from time-series bar returns.
    /// Uses downside deviation (returns below 0) with Bessel's correction.
    /// </summary>
    private static decimal CalculateSortinoRatio(List<double> returns)
    {
        if (returns.Count < 2) return 0m;

        double mean = returns.Average();
        var downsideSquares = returns
            .Where(r => r < 0)
            .Select(r => r * r)
            .ToList();

        if (downsideSquares.Count == 0) return mean > 0 ? decimal.MaxValue : 0m;

        // Divide by total count (not downside count) — standard Sortino denominator
        double downsideDev = Math.Sqrt(downsideSquares.Sum() / (returns.Count - 1));
        if (downsideDev < 1e-12) return 0m;

        return (decimal)(mean / downsideDev * Math.Sqrt(252));
    }

    private static (int MaxConsecutiveWins, int MaxConsecutiveLosses) CalculateStreaks(List<BacktestTrade> trades)
    {
        int maxWins = 0, maxLosses = 0;
        int curWins = 0, curLosses = 0;

        foreach (var t in trades)
        {
            if (t.PnL > 0)
            {
                curWins++;
                curLosses = 0;
                if (curWins > maxWins) maxWins = curWins;
            }
            else
            {
                curLosses++;
                curWins = 0;
                if (curLosses > maxLosses) maxLosses = curLosses;
            }
        }

        return (maxWins, maxLosses);
    }
}
