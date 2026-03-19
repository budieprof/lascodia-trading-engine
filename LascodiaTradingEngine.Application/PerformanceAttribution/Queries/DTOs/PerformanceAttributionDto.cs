namespace LascodiaTradingEngine.Application.PerformanceAttribution.Queries.DTOs;

// No IMapFrom — manually constructed from StrategyPerformanceSnapshot
public class PerformanceAttributionDto
{
    public long    StrategyId          { get; set; }
    public string? StrategyName        { get; set; }
    public int     TotalTrades         { get; set; }
    public decimal WinRate             { get; set; }
    public decimal TotalPnL            { get; set; }
    public decimal AveragePnLPerTrade  { get; set; }
    public decimal SharpeRatio         { get; set; }
    public decimal MaxDrawdownPct      { get; set; }
}
