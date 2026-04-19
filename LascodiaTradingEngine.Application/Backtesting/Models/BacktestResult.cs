using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting.Models;

/// <summary>
/// Comprehensive result of a backtest simulation including equity curve, trade statistics,
/// risk-adjusted returns, streak analysis, cost breakdown, and individual trade records.
/// </summary>
public record BacktestResult
{
    // ── Core Equity ──────────────────────────────────────────────────────────
    public decimal InitialBalance  { get; init; }
    public decimal FinalBalance    { get; init; }
    public decimal TotalReturn     { get; init; }    // %

    // ── Trade Counts ─────────────────────────────────────────────────────────
    public int     TotalTrades     { get; init; }
    public int     WinningTrades   { get; init; }
    public int     LosingTrades    { get; init; }
    public decimal WinRate         { get; init; }

    // ── Risk-Adjusted Returns ────────────────────────────────────────────────
    public decimal ProfitFactor    { get; init; }
    public decimal MaxDrawdownPct  { get; init; }
    public decimal SharpeRatio     { get; init; }
    public decimal SortinoRatio    { get; init; }
    public decimal CalmarRatio     { get; init; }

    // ── Trade Statistics ─────────────────────────────────────────────────────
    public decimal AverageWin      { get; init; }
    public decimal AverageLoss     { get; init; }
    public decimal LargestWin      { get; init; }
    public decimal LargestLoss     { get; init; }
    public decimal Expectancy      { get; init; }    // (WinRate × AvgWin) - (LossRate × AvgLoss)

    // ── Streak Analysis ──────────────────────────────────────────────────────
    public int MaxConsecutiveWins   { get; init; }
    public int MaxConsecutiveLosses { get; init; }

    // ── Time / Exposure ──────────────────────────────────────────────────────
    public decimal ExposurePct         { get; init; }  // % of bars spent in a trade
    public double  AverageTradeDurationHours { get; init; }

    // ── Cost Breakdown ───────────────────────────────────────────────────────
    public decimal TotalCommission { get; init; }
    public decimal TotalSwap       { get; init; }
    public decimal TotalSlippage   { get; init; }

    /// <summary>
    /// Aggregate realised-cost deductions sourced from TCA (spread + commission + market
    /// impact measured on live fills). Zero when no TCA profile is supplied.
    /// </summary>
    public decimal TotalTcaCost    { get; init; }

    // ── Recovery ─────────────────────────────────────────────────────────────
    public decimal RecoveryFactor  { get; init; }  // Net profit / MaxDrawdown (absolute)

    // ── Trade Log ────────────────────────────────────────────────────────────
    public List<BacktestTrade> Trades { get; init; } = new();
}

/// <summary>
/// Individual trade record from a backtest simulation with entry/exit details, PnL, and costs.
/// </summary>
public record BacktestTrade
{
    public TradeDirection Direction    { get; init; }
    public decimal  EntryPrice   { get; init; }
    public decimal  ExitPrice    { get; init; }
    public decimal  LotSize      { get; init; }
    public decimal  PnL          { get; init; }
    public decimal  Commission   { get; init; }
    public decimal  Swap         { get; init; }
    public decimal  Slippage     { get; init; }
    /// <summary>Realised TCA cost deducted from this trade (0 when no TCA profile).</summary>
    public decimal  TcaCost      { get; init; }
    public decimal  GrossPnL     { get; init; }
    public DateTime EntryTime    { get; init; }
    public DateTime ExitTime     { get; init; }
    public TradeExitReason ExitReason { get; init; }
}
