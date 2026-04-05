using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.MarketData.Queries.DTOs;

/// <summary>
/// Data transfer object for an OHLCV candle record.
/// </summary>
public class CandleDto : IMapFrom<Candle>
{
    /// <summary>Database ID of the candle record.</summary>
    public long    Id        { get; set; }

    /// <summary>Instrument symbol (e.g. "EURUSD").</summary>
    public string? Symbol    { get; set; }

    /// <summary>Bar timeframe (e.g. "M1", "H1", "D1").</summary>
    public string? Timeframe { get; set; }

    /// <summary>Opening price.</summary>
    public decimal Open      { get; set; }

    /// <summary>Highest price during the period.</summary>
    public decimal High      { get; set; }

    /// <summary>Lowest price during the period.</summary>
    public decimal Low       { get; set; }

    /// <summary>Closing (or current) price.</summary>
    public decimal Close     { get; set; }

    /// <summary>Tick volume.</summary>
    public decimal Volume    { get; set; }

    /// <summary>Candle open time.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Whether the candle is complete (true) or still forming (false).</summary>
    public bool    IsClosed  { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Candle, CandleDto>();
    }
}
