namespace LascodiaTradingEngine.Application.MarketData.Queries.DTOs;

public class LivePriceDto
{
    public string  Symbol    { get; set; } = string.Empty;
    public decimal Bid       { get; set; }
    public decimal Ask       { get; set; }
    public decimal Spread    { get; set; }
    public DateTime Timestamp { get; set; }
}
