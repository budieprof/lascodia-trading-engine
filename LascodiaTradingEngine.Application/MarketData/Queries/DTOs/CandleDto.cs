using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.MarketData.Queries.DTOs;

public class CandleDto : IMapFrom<Candle>
{
    public long    Id        { get; set; }
    public string? Symbol    { get; set; }
    public string? Timeframe { get; set; }
    public decimal Open      { get; set; }
    public decimal High      { get; set; }
    public decimal Low       { get; set; }
    public decimal Close     { get; set; }
    public decimal Volume    { get; set; }
    public DateTime Timestamp { get; set; }
    public bool    IsClosed  { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<Candle, CandleDto>();
    }
}
