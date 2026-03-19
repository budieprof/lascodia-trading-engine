using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting.Models;

public record BacktestResult
{
    public decimal InitialBalance  { get; init; }
    public decimal FinalBalance    { get; init; }
    public decimal TotalReturn     { get; init; }    // %
    public int     TotalTrades     { get; init; }
    public int     WinningTrades   { get; init; }
    public int     LosingTrades    { get; init; }
    public decimal WinRate         { get; init; }
    public decimal ProfitFactor    { get; init; }
    public decimal MaxDrawdownPct  { get; init; }
    public decimal SharpeRatio     { get; init; }
    public List<BacktestTrade> Trades { get; init; } = new();
}

public record BacktestTrade
{
    public TradeDirection Direction    { get; init; }
    public decimal  EntryPrice   { get; init; }
    public decimal  ExitPrice    { get; init; }
    public decimal  LotSize      { get; init; }
    public decimal  PnL          { get; init; }
    public DateTime EntryTime    { get; init; }
    public DateTime ExitTime     { get; init; }
    public string   ExitReason   { get; init; } = string.Empty;  // "TakeProfit" | "StopLoss" | "EndOfData"
}
