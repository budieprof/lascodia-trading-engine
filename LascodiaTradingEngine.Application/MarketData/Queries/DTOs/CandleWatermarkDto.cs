namespace LascodiaTradingEngine.Application.MarketData.Queries.DTOs;

public class CandleWatermarkDto
{
    public string   Symbol          { get; set; } = string.Empty;
    public string   Timeframe       { get; set; } = string.Empty;
    public DateTime LatestTimestamp { get; set; }
}
